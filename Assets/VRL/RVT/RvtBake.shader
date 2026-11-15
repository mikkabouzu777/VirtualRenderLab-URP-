// 1 ページを物理アトラスのマスへ焼く。頂点 6 つの四角をマスの位置に直接置く(ビューポートは使わない)。
Shader "Hidden/VRL/RvtBake"
{
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "RvtCommon.hlsl"

            float4 _TileRect;   // アトラスの uv: xy=左下 zw=大きさ
            float4 _WorldRect;  // xz の最小(縁込み) z=大きさ(縁込み) w=1 画素(m)

            struct V { float4 cs : SV_POSITION; float2 uv : TEXCOORD0; };

            V vert(uint vid : SV_VertexID)
            {
                static const float2 q[6] = { float2(0,0), float2(1,0), float2(0,1), float2(1,0), float2(1,1), float2(0,1) };
                float2 c = q[vid];
                float2 a = _TileRect.xy + c * _TileRect.zw;
                V o;
            #if UNITY_UV_STARTS_AT_TOP
                o.cs = float4(a.x * 2 - 1, 1 - a.y * 2, 0.5, 1);
            #else
                o.cs = float4(a.x * 2 - 1, a.y * 2 - 1, 0.5, 1);
            #endif
                o.uv = c;
                return o;
            }

            half4 frag(V i) : SV_Target
            {
                float2 xz = _WorldRect.xy + i.uv * _WorldRect.z;
                return half4(TerrainAlbedo(xz, _WorldRect.w), 1);
            }
            ENDHLSL
        }
    }
}
