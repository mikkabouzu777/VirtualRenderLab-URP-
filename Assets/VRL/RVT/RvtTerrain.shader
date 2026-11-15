// 地形: 間接テーブル(ページ → 物理のマス)を引いて、焼いた色を読む。_RvtDirect=1 なら毎画素 TerrainAlbedo を計算する(比べ用)。
Shader "VRL/RvtTerrain"
{
    Properties { }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "RvtCommon.hlsl"
            #include "Assets/VRL/VSM/VsmSample.hlsl"

            Texture2D _RvtAtlas;
            Texture2D<float4> _RvtIndirection;
            SamplerState sampler_linear_clamp;
            float4 _RvtAtlasInfo;   // x=アトラスの画素数(1 辺) y=mip のずらし z=直描き w=デバッグ表示

            struct A { float4 pos : POSITION; float3 nrm : NORMAL; };
            struct V { float4 cs : SV_POSITION; float3 ws : TEXCOORD0; float3 n : TEXCOORD1; float fog : TEXCOORD2; };

            V vert(A i)
            {
                V o;
                o.ws = TransformObjectToWorld(i.pos.xyz);
                o.n = TransformObjectToWorldNormal(i.nrm);
                o.cs = TransformWorldToHClip(o.ws);
                o.fog = ComputeFogFactor(o.cs.z);
                return o;
            }

            // 戻り値 rgb=色、a=実際に使った mip
            float4 SampleRvt(float2 uv, out int wantMip, out float2 tileOut)
            {
                float2 vt = uv * (RVT_PAGES * RVT_TILE);
                float2 dx = ddx(vt), dy = ddy(vt);
                float mip = 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), 1e-8)) + _RvtAtlasInfo.y;
                wantMip = clamp((int)floor(mip), 0, RVT_MIPS - 1);
                uint pages = RVT_PAGES >> wantMip;
                int2 p = (int2)clamp(uv * pages, 0, pages - 1);
                float4 e = _RvtIndirection.Load(int3(p, wantMip)) * 255.0 + 0.5;
                int resMip = (int)e.b;
                float2 tile = floor(e.rg);
                tileOut = tile;
                float2 local = frac(uv * (RVT_PAGES >> resMip));
                float2 phys = (tile * RVT_TILE_FULL + RVT_BORDER + local * RVT_TILE) / _RvtAtlasInfo.x;
                return float4(_RvtAtlas.SampleLevel(sampler_linear_clamp, phys, 0).rgb, resMip);
            }

            half4 frag(V i) : SV_Target
            {
                float2 uv = (i.ws.xz - _TerrainRect.xy) / _TerrainRect.z;
                float3 albedo;
                int wantMip = 0; float resMip = 0; float2 tile = 0;
                if (_RvtAtlasInfo.z > 0.5)
                {
                    float texel = max(length(ddx(i.ws.xz)), length(ddy(i.ws.xz)));
                    albedo = TerrainAlbedo(i.ws.xz, texel);
                }
                else
                {
                    float4 s = SampleRvt(uv, wantMip, tile);
                    albedo = s.rgb; resMip = s.a;
                }

                float3 n = normalize(i.n);
                Light L = GetMainLight(TransformWorldToShadowCoord(i.ws));
                float ndl = saturate(dot(n, L.direction));
                float shadow = L.shadowAttenuation;
                float3 vsm = float3(1, -1, 0);
                if (_VsmEnabled > 0.5) { vsm = SampleVsm(i.ws, n, ndl); shadow = vsm.x; }
                float3 col = albedo * (L.color * ndl * shadow + SampleSH(n));
                int vdbg = (int)_VsmPhys.w;
                if (_VsmEnabled > 0.5 && vdbg != 0) col = VsmDebugTint(vsm, vdbg) * (0.25 + 0.75 * ndl * shadow);

                int dbg = (int)_RvtAtlasInfo.w;
                if (_RvtAtlasInfo.z < 0.5 && dbg == 1) col = RvtMipColor((int)resMip) * (0.35 + 0.65 * ndl) * lerp(0.6, 1.0, albedo.g * 2);
                if (_RvtAtlasInfo.z < 0.5 && dbg == 2)
                {
                    float h = RvtHash21(tile * 0.37 + 0.11);
                    col = lerp(float3(h, frac(h * 7.1), frac(h * 13.3)), albedo, 0.35) * (0.4 + 0.6 * ndl);
                }
                if (_RvtAtlasInfo.z < 0.5 && dbg == 3) col = (wantMip < (int)resMip ? float3(1, 0.3, 0.2) : float3(0.3, 0.9, 0.4)) * (0.4 + 0.6 * ndl);
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "VsmCaster"
            Tags { "LightMode" = "VsmCaster" }
            ZWrite On ZTest LEqual Cull Off ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/VRL/VSM/VsmSample.hlsl"
            float4x4 _VsmObjectToWorld;
            VsmCasterOut vert(float4 pos : POSITION, uint iid : SV_InstanceID)
            {
                return VsmCasterVertex(mul(_VsmObjectToWorld, float4(pos.xyz, 1)).xyz, iid);
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 vert(float4 pos : POSITION) : SV_POSITION { return TransformObjectToHClip(pos.xyz); }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float4 vert(float4 pos : POSITION, float3 nrm : NORMAL) : SV_POSITION
            {
                float3 ws = TransformObjectToWorld(pos.xyz);
                float4 cs = TransformWorldToHClip(ApplyShadowBias(ws, TransformObjectToWorldNormal(nrm), _LightDirection));
                #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return cs;
            }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
