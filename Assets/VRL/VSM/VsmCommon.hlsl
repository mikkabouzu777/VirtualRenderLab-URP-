// VSM(Virtual Shadow Maps)の共通の決まり。C# の VsmManager と値の意味を合わせる。
// 仮想の影 = レベル(クリップマップ)ごとに 64x64 ページ、1 ページ 128px。レベル l は幅 W0*2^l の正方形でカメラを追う。
// ページは「世界に固定した番号」(光の空間の xy をページ幅で割った整数 = abs)で持ち、表の場所は abs を 64 で折り返した所。
// カメラが動いても同じ abs のページは同じ中身なので、前のコマの影をそのまま使える(持ち越し)。
#ifndef VRL_VSM_COMMON
#define VRL_VSM_COMMON

#define VSM_PAGES 64
#define VSM_PAGE_PX 128
#define VSM_PAGES_PER_LEVEL (VSM_PAGES * VSM_PAGES)
#define VSM_NONE 0xffffffffu

float4x4 _VsmLightView;     // ワールド → 光の空間(z は光の向き)
float4 _VsmOrigins[16];     // xy=窓の中心のページ番号(abs) z=レベルの幅(m) w=1 ページの幅(m)
float4 _VsmZ;               // x=近い側の z(切りのいい所にそろえる) y=1/奥行きの幅 z=テクセル 0 の大きさ(m) w=レベル数
float4 _VsmCam;             // xyz=カメラ位置 w=距離 1m での 1 画素の大きさ
float4 _VsmPhys;            // x=物理のマス数(1 辺) y=アトラスの画素数(1 辺) z=LOD のずらし w=デバッグ表示
uint _VsmFrame;
StructuredBuffer<uint2> _VsmPageTable;  // 表の場所 → (物理のマス番号+1(0 = 置いていない), そこに入っているページの abs(詰めた値))
StructuredBuffer<uint4> _VsmSlotInfo;   // 物理のマス → (表の場所, abs, 最後に使ったコマ, 最後に描き直したコマ)

int VsmLevelCount() { return (int)_VsmZ.w; }

// その点の 1 画素の大きさに合う一番細かいレベル
int VsmLevelFor(float3 posWS)
{
    float footprint = distance(posWS, _VsmCam.xyz) * _VsmCam.w;
    float lv = log2(max(footprint, 1e-6) / _VsmZ.z) + _VsmPhys.z;
    return clamp((int)ceil(lv), 0, VsmLevelCount() - 1);
}

uint VsmPackAbs(int2 a) { return ((uint)a.x & 0xffffu) | (((uint)a.y & 0xffffu) << 16); }
int2 VsmUnpackAbs(uint p)
{
    int x = (int)(p & 0xffffu); if (x >= 32768) x -= 65536;
    int y = (int)(p >> 16);     if (y >= 32768) y -= 65536;
    return int2(x, y);
}

// 光の空間の xy → そのレベルでのページ座標(小数。整数部が abs)
float2 VsmPageCoord(float3 ls, int level) { return ls.xy / _VsmOrigins[level].w; }

bool VsmInWindow(int2 absP, int level)
{
    int2 d = absP - (int2)_VsmOrigins[level].xy;
    return all(d >= -VSM_PAGES / 2) && all(d < VSM_PAGES / 2);
}

uint VsmEntry(int level, int2 absP)
{
    uint2 w = (uint2)(absP & (VSM_PAGES - 1));
    return (uint)level * VSM_PAGES_PER_LEVEL + w.y * VSM_PAGES + w.x;
}

// 表の場所 e が今の窓で受け持つ abs
int2 VsmAbsOfEntry(uint e, out int level)
{
    level = (int)(e / VSM_PAGES_PER_LEVEL);
    int2 w = int2(e % VSM_PAGES, (e / VSM_PAGES) % VSM_PAGES);
    int2 base = (int2)_VsmOrigins[level].xy - VSM_PAGES / 2;
    return base + ((w - base) & (VSM_PAGES - 1));
}

float VsmDepth01(float3 ls) { return (ls.z - _VsmZ.x) * _VsmZ.y; }

uint2 VsmPhysXY(uint slot)
{
    uint n = (uint)_VsmPhys.x;
    return uint2(slot % n, slot / n);
}

float3 VsmHash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
    return float3(x & 255u, (x >> 8) & 255u, (x >> 16) & 255u) / 255.0;
}

float3 VsmLevelColor(uint l)
{
    static const float3 c[8] = { float3(1,0.25,0.25), float3(1,0.6,0.15), float3(1,1,0.25), float3(0.35,1,0.35),
                                 float3(0.25,0.85,1), float3(0.35,0.45,1), float3(0.75,0.4,1), float3(1,0.45,0.8) };
    return c[l % 8u];
}

// 物理アトラスのマスへ描く時のクリップ座標。inPage は 0〜1(マスの中)
float4 VsmAtlasClip(uint slot, float2 inPage, float z01)
{
    float2 atlas = (VsmPhysXY(slot) + inPage) / _VsmPhys.x;
#if UNITY_REVERSED_Z
    float z = 1 - z01;
#else
    float z = z01;
#endif
#if UNITY_UV_STARTS_AT_TOP
    return float4(atlas.x * 2 - 1, 1 - atlas.y * 2, z, 1);
#else
    return float4(atlas.x * 2 - 1, atlas.y * 2 - 1, z, 1);
#endif
}
#endif
