// フィードバック: 地形を小さな画面に描き、各画素が「どのページを、どの細かさで」欲しいかを書く。CPU が読み戻してページを焼く。
// R=ページ x G=ページ y B=mip A=1(0 は空)
Shader "Hidden/VRL/RvtFeedback"
{
    SubShader
    {
        Pass
        {
            ZTest LEqual ZWrite On Cull Back
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "RvtCommon.hlsl"

            float4x4 _FbViewProj;
            float4x4 _FbObjectToWorld;
            float _FbMipOffset;     // フィードバックの画面が小さい分(log2 の縮小率)と、ずらし

            struct V { float4 cs : SV_POSITION; float3 ws : TEXCOORD0; };

            V vert(float4 pos : POSITION)
            {
                V o;
                o.ws = mul(_FbObjectToWorld, float4(pos.xyz, 1)).xyz;
                o.cs = mul(_FbViewProj, float4(o.ws, 1));
                return o;
            }

            float4 frag(V i) : SV_Target
            {
                float2 uv = (i.ws.xz - _TerrainRect.xy) / _TerrainRect.z;
                float2 vt = uv * (RVT_PAGES * RVT_TILE);
                float2 dx = ddx(vt), dy = ddy(vt);
                float mip = 0.5 * log2(max(dot(dx, dx), dot(dy, dy))) - _FbMipOffset;
                int m = clamp((int)floor(mip), 0, RVT_MIPS - 1);
                uint pages = RVT_PAGES >> m;
                uint2 p = (uint2)clamp(uv * pages, 0, pages - 1);
                return float4(p.x / 255.0, p.y / 255.0, m / 255.0, 1);
            }
            ENDHLSL
        }
    }
}
