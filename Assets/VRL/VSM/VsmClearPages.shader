// このコマ描き直す物理のマスだけを「一番奥」で塗りつぶす(アトラス全体は消さない=持ち越すマスを残す)。
Shader "Hidden/VRL/VsmClearPages"
{
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite On Cull Off ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "VsmCommon.hlsl"
            StructuredBuffer<uint> _VsmRenderList;

            float4 vert(uint vid : SV_VertexID, uint iid : SV_InstanceID) : SV_POSITION
            {
                static const float2 q[6] = { float2(0,0), float2(1,0), float2(0,1), float2(1,0), float2(1,1), float2(0,1) };
                return VsmAtlasClip(_VsmRenderList[iid], q[vid], 1);
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
