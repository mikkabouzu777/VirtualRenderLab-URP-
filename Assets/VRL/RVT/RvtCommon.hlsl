// RVT(Runtime Virtual Texture)の共通。地形の表面の色を世界座標 xz から作る重い関数 TerrainAlbedo と、仮想テクスチャの決まり。
// 焼く時(RvtBake)も、比べ用に毎画素そのまま計算する時(RvtTerrain の直描きモード)も、同じ関数を通す。
#ifndef VRL_RVT_COMMON
#define VRL_RVT_COMMON

#define RVT_PAGES 256       // mip0 の 1 辺のページ数
#define RVT_MIPS 9          // 256 → 1
#define RVT_TILE 128        // 1 ページの中身の画素
#define RVT_BORDER 4        // 双一次で隣と混ざらないための縁
#define RVT_TILE_FULL 136   // 縁込み

float4 _TerrainRect;        // xy=地形の xz の最小 z=1 辺(m) w=未使用
Texture2D<float> _HeightTex; SamplerState sampler_HeightTex;
StructuredBuffer<float4> _Segments;  // 道の線分 (ax, az, bx, bz)
StructuredBuffer<float4> _Decals;    // 貼り付け (x, z, 半径, 種類)
int _SegmentCount;
int _DecalCount;
float _RoadHalfWidth;

float RvtHash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float RvtValueNoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3 - 2 * f);
    float a = RvtHash21(i), b = RvtHash21(i + float2(1, 0)), c = RvtHash21(i + float2(0, 1)), d = RvtHash21(i + 1);
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// 1 画素(texel m)より細かい波は消していく fbm = 焼いた時にちらつかない(前もってぼかしておく)
float RvtFbm(float2 p, float freq, int octaves, float texel)
{
    float sum = 0, amp = 0.5, norm = 0;
    for (int i = 0; i < octaves; i++)
    {
        float wave = 1.0 / freq;
        float fade = saturate(wave / (texel * 4.0) - 0.5);
        sum += amp * fade * (RvtValueNoise(p * freq + i * 17.17) * 2 - 1);
        norm += amp;
        freq *= 2.07; amp *= 0.5;
    }
    return sum / norm;
}

float TerrainHeight(float2 xz)
{
    float2 uv = (xz - _TerrainRect.xy) / _TerrainRect.z;
    return _HeightTex.SampleLevel(sampler_HeightTex, uv, 0);
}

float3 TerrainNormal(float2 xz)
{
    float e = 1.0;
    float hx = TerrainHeight(xz + float2(e, 0)) - TerrainHeight(xz - float2(e, 0));
    float hz = TerrainHeight(xz + float2(0, e)) - TerrainHeight(xz - float2(0, e));
    return normalize(float3(-hx, 2 * e, -hz));
}

float SegDist(float2 p, float4 s)
{
    float2 a = s.xy, b = s.zw;
    float2 ab = b - a;
    float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-6));
    return length(p - (a + ab * t));
}

// 地形の色。texel = 1 画素の大きさ(m)
float3 TerrainAlbedo(float2 xz, float texel)
{
    float h = TerrainHeight(xz);
    float3 n = TerrainNormal(xz);
    float slope = 1 - n.y;

    float big = RvtFbm(xz, 0.012, 5, texel);
    float mid = RvtFbm(xz, 0.35, 8, texel);
    float fine = RvtFbm(xz, 6.0, 6, texel);
    float micro = RvtFbm(xz, 40.0, 4, texel);

    float3 grass = lerp(float3(0.16, 0.27, 0.07), float3(0.33, 0.37, 0.12), saturate(big * 1.6 + 0.5));
    grass *= 0.82 + 0.3 * mid + 0.18 * fine + 0.12 * micro;
    float3 dirt = float3(0.34, 0.26, 0.17) * (0.8 + 0.3 * mid + 0.25 * fine);
    float strata = RvtValueNoise(float2(xz.x * 0.02, h * 1.5 + mid * 2)) ;
    float3 rock = lerp(float3(0.36, 0.35, 0.33), float3(0.55, 0.52, 0.47), strata) * (0.75 + 0.25 * fine + 0.2 * micro);
    float3 snow = float3(0.9, 0.92, 0.96) * (0.95 + 0.05 * fine);
    float3 sand = float3(0.72, 0.64, 0.47) * (0.9 + 0.1 * fine + 0.08 * micro);

    float3 col = grass;
    col = lerp(col, dirt, saturate(smoothstep(0.08, 0.2, slope) + smoothstep(0.35, 0.65, mid * 0.5 + 0.5) * 0.35));
    col = lerp(col, sand, smoothstep(6, 2, h + mid * 2));
    col = lerp(col, rock, smoothstep(0.22, 0.38, slope + fine * 0.05));
    col = lerp(col, snow, smoothstep(58, 66, h + big * 10) * smoothstep(0.45, 0.25, slope));

    // 道(線分を全部なめる = 重い)
    float d = 1e9;
    for (int i = 0; i < _SegmentCount; i++) d = min(d, SegDist(xz, _Segments[i]));
    float edge = _RoadHalfWidth + mid * 0.4;
    float roadW = 1 - smoothstep(edge - 0.5, edge + 0.3, d);
    float gravel = 0.85 + 0.15 * fine + 0.2 * micro;
    float3 road = float3(0.42, 0.36, 0.28) * gravel;
    float rut = exp(-pow((d - 0.85) / 0.18, 2)) * 0.35;
    road *= 1 - rut;
    road = lerp(road, float3(0.3, 0.33, 0.12) * gravel, smoothstep(0.25, 0.0, d) * 0.6); // 真ん中の草
    col = lerp(col, road, roadW);

    // 貼り付け(花畑・石畳・ぬかるみ)
    for (int k = 0; k < _DecalCount; k++)
    {
        float4 dc = _Decals[k];
        float dd = length(xz - dc.xy) / dc.z;
        if (dd >= 1) continue;
        float w = smoothstep(1, 0.6 + mid * 0.2, dd);
        if (dc.w < 0.5)
        {
            float2 cell = floor(xz * 3);
            float r = RvtHash21(cell + dc.xy);
            float2 cp = frac(xz * 3) - 0.5;
            float dot_ = smoothstep(0.22, 0.12, length(cp + (RvtHash21(cell * 1.7) - 0.5) * 0.4));
            float3 petal = r < 0.33 ? float3(0.95, 0.85, 0.2) : r < 0.66 ? float3(0.95, 0.95, 0.95) : float3(0.6, 0.35, 0.85);
            col = lerp(col, petal, dot_ * w * step(0.35, r + mid * 0.3) * saturate(texel < 0.1 ? 1 : 0.4));
        }
        else if (dc.w < 1.5)
        {
            float2 g = xz * 1.6;
            float2 f = abs(frac(g + float2(floor(g.y) * 0.5, 0)) - 0.5);
            float joint = smoothstep(0.42, 0.48, max(f.x, f.y));
            float3 stone = float3(0.5, 0.48, 0.45) * (0.8 + 0.4 * RvtHash21(floor(g + float2(floor(g.y) * 0.5, 0))));
            col = lerp(col, lerp(stone, float3(0.2, 0.18, 0.15), joint), w);
        }
        else
        {
            col = lerp(col, float3(0.17, 0.13, 0.09) * (0.8 + 0.3 * fine), w * 0.85);
        }
    }
    return col;
}

float3 RvtMipColor(int m)
{
    static const float3 c[9] = { float3(1,0.2,0.2), float3(1,0.55,0.1), float3(1,0.95,0.2), float3(0.4,1,0.3),
                                 float3(0.2,0.9,0.9), float3(0.25,0.5,1), float3(0.6,0.35,1), float3(1,0.4,0.85), float3(0.6,0.6,0.6) };
    return c[clamp(m, 0, 8)];
}
#endif
