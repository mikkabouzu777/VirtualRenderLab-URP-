// 受ける側: VSM の影を引く。VsmLit / MeshletLit / RvtTerrain が共通で使う。
#ifndef VRL_VSM_SAMPLE
#define VRL_VSM_SAMPLE
#include "VsmCommon.hlsl"

Texture2D<float> _VsmAtlas;
float _VsmEnabled;

float VsmTexel(uint2 t)
{
    float d = _VsmAtlas.Load(int3(t, 0));
#if UNITY_REVERSED_Z
    d = 1 - d;
#endif
    return d;
}

// 戻り値: x=影(1=日なた) y=使ったレベル(-1=無し) z=物理のマス+1(0=無し)
float3 SampleVsm(float3 posWS, float3 nrmWS, float ndl)
{
    int level = VsmLevelFor(posWS);
    int levels = VsmLevelCount();
    for (int l = level; l < levels; l++)
    {
        // 法線方向へ少しずらしてから引く(影のにきび対策)。ずらす量はそのレベルの 1 画素ぶん
        float texelWS = _VsmOrigins[l].w / VSM_PAGE_PX;
        float3 p = posWS + nrmWS * texelWS * (1.0 + 1.5 * (1 - ndl));
        float3 ls = mul(_VsmLightView, float4(p, 1)).xyz;
        float2 pc = VsmPageCoord(ls, l);
        int2 absP = (int2)floor(pc);
        if (!VsmInWindow(absP, l)) continue;
        uint e = VsmEntry(l, absP);
        uint2 pt = _VsmPageTable[e];
        uint slot1 = pt.x;
        if (slot1 == 0 || pt.y != VsmPackAbs(absP)) continue;

        float z = VsmDepth01(ls);
        if (z > 1) return float3(1, l, slot1);
        float2 inPage = pc - absP;
        float2 base = VsmPhysXY(slot1 - 1) * VSM_PAGE_PX;
        float2 t = base + inPage * VSM_PAGE_PX - 0.5;
        float2 f = frac(t);
        float2 lo = base, hi = base + VSM_PAGE_PX - 1;
        // 3x3 の格子 x 双一次 = 4x4 画素の PCF。ページの外へははみ出さない
        float sum = 0;
        float bias = 0.00002;
        [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 q = floor(t) + float2(x, y);
            uint2 q00 = (uint2)clamp(q, lo, hi);
            uint2 q11 = (uint2)clamp(q + 1, lo, hi);
            float s00 = z <= VsmTexel(q00) + bias;
            float s10 = z <= VsmTexel(uint2(q11.x, q00.y)) + bias;
            float s01 = z <= VsmTexel(uint2(q00.x, q11.y)) + bias;
            float s11 = z <= VsmTexel(q11) + bias;
            sum += lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y);
        }
        return float3(sum / 9.0, l, slot1);
    }
    return float3(1, -1, 0);
}

// デバッグ表示の色。mode 1=ページ 2=レベル 3=このコマ描き直したページ
float3 VsmDebugTint(float3 vsm, int mode)
{
    if (vsm.z == 0) return float3(1, 0, 1);
    if (mode == 1) return lerp(VsmHash((uint)vsm.z), VsmLevelColor((uint)vsm.y), 0.3);
    if (mode == 2) return VsmLevelColor((uint)vsm.y);
    if (mode == 3) return _VsmSlotInfo[(uint)vsm.z - 1].w == _VsmFrame ? float3(1, 0.25, 0.15) : float3(0.55, 0.6, 0.65);
    return 1;
}

// 影を落とす側(VsmCaster パス)の共通: そのマスに掛かる物の頂点をアトラスのマスへ置く
struct VsmCasterOut { float4 cs : SV_POSITION; float4 clip : SV_ClipDistance0; };
float _VsmCasterIndex;
StructuredBuffer<uint> _VsmCasterPages;

// 物理のマス slot を直接指定する版(塊ごとにマスを選んで並べる時)
VsmCasterOut VsmCasterVertexSlot(float3 posWS, uint slot)
{
    VsmCasterOut o;
    uint4 si = _VsmSlotInfo[slot];
    uint e = si.x;
    int level = (int)(e / VSM_PAGES_PER_LEVEL);
    float2 absP = (float2)VsmUnpackAbs(si.y);
    float3 ls = mul(_VsmLightView, float4(posWS, 1)).xyz;
    float2 inPage = VsmPageCoord(ls, level) - absP;
    o.cs = VsmAtlasClip(slot, inPage, max(VsmDepth01(ls), 0));   // 光より手前の物はつぶして残す(pancaking)
    o.clip = float4(inPage.x, 1 - inPage.x, inPage.y, 1 - inPage.y);
    return o;
}

VsmCasterOut VsmCasterVertex(float3 posWS, uint iid)
{
    uint maxPhys = (uint)(_VsmPhys.x * _VsmPhys.x);
    return VsmCasterVertexSlot(posWS, _VsmCasterPages[(uint)_VsmCasterIndex * maxPhys + iid]);
}
#endif
