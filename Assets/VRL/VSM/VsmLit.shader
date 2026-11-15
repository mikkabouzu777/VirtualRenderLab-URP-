// VSM を受ける・落とす材質。V キーで URP の普通の影(カスケード)と切り替えて比べられる。
// パス: ForwardLit(受ける) / DepthOnly(ページの印付けに必須) / ShadowCaster(URP の影用) / VsmCaster(物理アトラスへ描く)
Shader "VRL/VsmLit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.7, 0.7, 0.7, 1)
        _Pattern ("Pattern (0=無地 1=市松 2=ノイズ)", Float) = 0
        _PatternScale ("Pattern Scale", Float) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float _Pattern;
            float _PatternScale;
        CBUFFER_END
        ENDHLSL

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
            #include "VsmSample.hlsl"

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

            float Pattern(float3 ws)
            {
                float3 p = ws * _PatternScale;
                if (_Pattern > 1.5)
                {
                    float n = frac(sin(dot(floor(p * 4), float3(12.9898, 78.233, 37.719))) * 43758.5453);
                    return lerp(0.85, 1.05, n);
                }
                if (_Pattern > 0.5)
                {
                    float c = fmod(abs(floor(p.x) + floor(p.z)), 2.0);
                    return lerp(0.82, 1.0, c);
                }
                return 1;
            }

            half4 frag(V i) : SV_Target
            {
                float3 n = normalize(i.n);
                float4 shadowCoord = TransformWorldToShadowCoord(i.ws);
                Light L = GetMainLight(shadowCoord);
                float ndl = saturate(dot(n, L.direction));
                float shadow = L.shadowAttenuation;
                float3 vsm = float3(1, -1, 0);
                if (_VsmEnabled > 0.5)
                {
                    vsm = SampleVsm(i.ws, n, ndl);
                    shadow = vsm.x;
                }
                float3 albedo = _BaseColor.rgb * Pattern(i.ws);
                float3 col = albedo * (L.color * ndl * shadow + SampleSH(n));

                int dbg = (int)_VsmPhys.w;
                if (_VsmEnabled > 0.5 && dbg != 0)
                {
                    col = VsmDebugTint(vsm, dbg) * (0.25 + 0.75 * ndl * shadow);
                }
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
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

        // 物理アトラスへ描く。1 インスタンス = この物が掛かる物理のマス 1 つ(VsmPages.compute の CullCasterPages が並べる)。マスの外はクリップ距離で切る。
        Pass
        {
            Name "VsmCaster"
            Tags { "LightMode" = "VsmCaster" }
            ZWrite On ZTest LEqual Cull Off ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "VsmSample.hlsl"
            float4x4 _VsmObjectToWorld;

            VsmCasterOut vert(float4 pos : POSITION, uint iid : SV_InstanceID)
            {
                return VsmCasterVertex(mul(_VsmObjectToWorld, float4(pos.xyz, 1)).xyz, iid);
            }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
