// MeshletLit の色付け。ハードの描画(ForwardLit)とソフトウェアラスタの色付け(SwResolve)で共通。
// 先に Lighting.hlsl と VsmSample.hlsl を include しておくこと。
#ifndef VRL_MESHLET_SHADE
#define VRL_MESHLET_SHADE

// 環境光。全画面で描くソフトウェアラスタには物ごとの環境光(unity_SH*)が来ないので、シーンの環境光を C# から渡す
float4 _VrlSH[7];

// ids = (塊, 三角形, 段, 物)  kind: 0=ハード 1 回目 1=Hi-Z の 2 回目 2=ソフトウェアラスタ
float3 ShadeMeshlet(float3 posWS, float3 nrmWS, uint4 ids, int kind)
{
    float3 n = normalize(nrmWS);
    Light L = GetMainLight(TransformWorldToShadowCoord(posWS));
    float ndl = saturate(dot(n, L.direction));
    float3 ambient = max(0, SampleSH9(_VrlSH, n));
    float shadow = L.shadowAttenuation;
    float3 vsm = float3(1, -1, 0);
    if (_VsmEnabled > 0.5) { vsm = SampleVsm(posWS, n, ndl); shadow = vsm.x; }

    float3 albedo = _BaseColor.rgb * lerp(0.85, 1.1, HashColor(ids.w).r);
    if (_DebugMode == 1) albedo = HashColor(ids.x);
    else if (_DebugMode == 2) albedo = HashColor(ids.x * 131u + ids.y);
    else if (_DebugMode == 3) albedo = LodColor(ids.z);
    else if (_DebugMode == 4) albedo = HashColor(ids.w + 7u);
    else if (_DebugMode == 5) albedo = kind == 1 ? float3(1, 0.2, 0.15) : float3(0.55, 0.57, 0.6);
    else if (_DebugMode == 6) albedo = kind == 2 ? float3(0.25, 0.95, 0.35) : float3(0.55, 0.57, 0.6);

    float3 col = albedo * (L.color * ndl * shadow + ambient);
    if (_DebugMode != 0) col = albedo * (0.35 + 0.65 * ndl * shadow);
    int vdbg = (int)_VsmPhys.w;
    if (_VsmEnabled > 0.5 && vdbg != 0 && _DebugMode == 0) col = VsmDebugTint(vsm, vdbg) * (0.25 + 0.75 * ndl * shadow);
    return col;
}
#endif
