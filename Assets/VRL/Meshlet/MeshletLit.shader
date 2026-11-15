// Meshlet を RenderPrimitivesIndirect で描く。1 インスタンス = 見えた塊 1 つ、頂点 = 124 三角形 x 3。
// 塊の三角形数より後ろの頂点は NaN にして捨てる。
Shader "VRL/MeshletLit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.52, 0.48, 0.43, 1)
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Vtx { float3 pos; float3 nrm; };
        struct MeshletGpu
        {
            float4 sphere; float4 cone; float4 selfLod; float4 parentLod;
            uint triOffset; uint triCount; uint lod; uint group;
            float selfErr; float parentErr; float pad0; float pad1;
        };
        struct InstanceGpu { float4x4 m; float4 extra; };

        StructuredBuffer<Vtx> _Vertices;
        StructuredBuffer<uint> _Indices;
        StructuredBuffer<MeshletGpu> _Meshlets;
        StructuredBuffer<InstanceGpu> _Instances;
        StructuredBuffer<uint2> _Visible;

        float4 _BaseColor;
        int _DebugMode;     // 0=仕上がり 1=塊 2=三角形 3=DAG の段 4=物 5=Hi-Z の 2 回目で描いた塊 6=ソフトウェアラスタで塗った塊
        float _PostPass;    // 1 = Hi-Z の 2 回目の描画

        struct Fetched
        {
            float3 posWS;
            float3 nrmWS;
            uint cluster, tri, lod, inst;
            bool valid;
        };

        Fetched FetchVertex(uint vid, uint iid)
        {
            Fetched f = (Fetched)0;
            uint2 v = _Visible[iid];
            MeshletGpu m = _Meshlets[v.y];
            uint tri = vid / 3;
            f.valid = tri < m.triCount;
            tri = min(tri, m.triCount - 1);
            uint vi = _Indices[(m.triOffset + tri) * 3 + vid % 3];
            Vtx vx = _Vertices[vi];
            InstanceGpu I = _Instances[v.x];
            f.posWS = mul(I.m, float4(vx.pos, 1)).xyz;
            f.nrmWS = normalize(mul((float3x3)I.m, vx.nrm));
            f.cluster = v.y; f.tri = tri; f.lod = m.lod; f.inst = v.x;
            return f;
        }

        float3 HashColor(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
            return float3(x & 255u, (x >> 8) & 255u, (x >> 16) & 255u) / 255.0;
        }

        float3 LodColor(uint l)
        {
            static const float3 c[10] = { float3(1,0.2,0.2), float3(1,0.6,0.1), float3(1,1,0.2), float3(0.3,1,0.3), float3(0.2,0.9,0.8),
                                          float3(0.2,0.6,1), float3(0.5,0.35,1), float3(0.9,0.35,1), float3(1,0.5,0.7), float3(0.9,0.9,0.9) };
            return c[min(l, 9u)];
        }

        float4 NanPos() { return asfloat(0x7fc00000u).xxxx; }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back ZWrite On ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/VRL/VSM/VsmSample.hlsl"
            #include "MeshletShade.hlsl"

            struct V2F
            {
                float4 posCS : SV_POSITION;
                float3 posWS : TEXCOORD0;
                float3 nrmWS : TEXCOORD1;
                nointerpolation uint4 ids : TEXCOORD2; // 塊, 三角形, LOD, 物
            };

            V2F vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                V2F o;
                Fetched f = FetchVertex(vid, iid);
                o.posWS = f.posWS;
                o.nrmWS = f.nrmWS;
                o.posCS = f.valid ? TransformWorldToHClip(f.posWS) : NanPos();
                o.ids = uint4(f.cluster, f.tri, f.lod, f.inst);
                return o;
            }

            half4 frag(V2F i) : SV_Target
            {
                return half4(ShadeMeshlet(i.posWS, i.nrmWS, i.ids, _PostPass > 0.5 ? 1 : 0), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull Back ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;

            float4 vert(uint vid : SV_VertexID, uint iid : SV_InstanceID) : SV_POSITION
            {
                Fetched f = FetchVertex(vid, iid);
                if (!f.valid) return NanPos();
                float4 cs = TransformWorldToHClip(ApplyShadowBias(f.posWS, f.nrmWS, _LightDirection));
                #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return cs;
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        // VSM の物理アトラスへ描く。1 インスタンス = (岩, 塊, ページ) 1 つ。塊はページの 1 画素に合う DAG の段から、ページに掛かる物だけ(MeshletCull.compute の ShadowClusters)
        Pass
        {
            Name "VsmCaster"
            Tags { "LightMode" = "VsmCaster" }
            ZWrite On ZTest LEqual Cull Off ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/VRL/VSM/VsmSample.hlsl"
            StructuredBuffer<uint4> _ShadowDraw;

            VsmCasterOut vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                uint4 e = _ShadowDraw[iid];
                MeshletGpu m = _Meshlets[e.y];
                uint tri = vid / 3;
                if (tri >= m.triCount) { VsmCasterOut o; o.cs = NanPos(); o.clip = 0; return o; }
                uint vi = _Indices[(m.triOffset + tri) * 3 + vid % 3];
                float3 ws = mul(_Instances[e.x].m, float4(_Vertices[vi].pos, 1)).xyz;
                return VsmCasterVertexSlot(ws, e.z);
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Back ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(uint vid : SV_VertexID, uint iid : SV_InstanceID) : SV_POSITION
            {
                Fetched f = FetchVertex(vid, iid);
                return f.valid ? TransformWorldToHClip(f.posWS) : NanPos();
            }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            Cull Back ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            struct V2F { float4 posCS : SV_POSITION; float3 nrmWS : TEXCOORD0; };
            V2F vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                V2F o;
                Fetched f = FetchVertex(vid, iid);
                o.posCS = f.valid ? TransformWorldToHClip(f.posWS) : NanPos();
                o.nrmWS = f.nrmWS;
                return o;
            }
            half4 frag(V2F i) : SV_Target { return half4(normalize(i.nrmWS), 0); }
            ENDHLSL
        }
            // ---- ソフトウェアラスタ(MeshletCull.compute の SwRaster*)で塗った画素を、全画面の三角形 1 枚で深度・色にする ----
        HLSLINCLUDE
        StructuredBuffer<uint> _SwDepth;
        StructuredBuffer<uint> _SwPayload;
        StructuredBuffer<uint2> _SwList;
        float4x4 _SwVP;
        float4 _SwScreen;   // xy=画素数 z=上下反転 w=reversed-Z

        float4 FullscreenVert(uint vid : SV_VertexID) : SV_POSITION
        {
            float2 uv = float2((vid << 1) & 2, vid & 2);
            return float4(uv * 2 - 1, UNITY_NEAR_CLIP_VALUE, 1);
        }

        bool SwFetch(float4 pos, out uint payload, out float depth)
        {
            uint2 px = (uint2)pos.xy;
            uint idx = px.y * (uint)_SwScreen.x + px.x;
            uint d = _SwDepth[idx];
            payload = _SwPayload[idx];
            float z = asfloat(d);
            depth = _SwScreen.w > 0.5 ? z : 1 - z;
            return d != 0 && payload != 0;
        }
        ENDHLSL

        Pass
        {
            Name "SwDepth"
            Tags { "LightMode" = "SwDepth" }
            Cull Off ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex FullscreenVert
            #pragma fragment frag
            float frag(float4 pos : SV_POSITION) : SV_Depth
            {
                uint p; float d;
                if (!SwFetch(pos, p, d)) discard;
                return d;
            }
            ENDHLSL
        }

        Pass
        {
            Name "SwResolve"
            Tags { "LightMode" = "SwResolve" }
            Cull Off ZWrite On ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex FullscreenVert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/VRL/VSM/VsmSample.hlsl"
            #include "MeshletShade.hlsl"

            float4 SwClip(float3 ws) { return mul(_SwVP, float4(ws, 1)); }
            float2 SwPx(float4 cp)
            {
                float2 ndc = cp.xy / cp.w;
                return float2((ndc.x * 0.5 + 0.5) * _SwScreen.x, (_SwScreen.z > 0.5 ? (1 - ndc.y) * 0.5 : ndc.y * 0.5 + 0.5) * _SwScreen.y);
            }

            half4 frag(float4 pos : SV_POSITION, out float depthOut : SV_Depth) : SV_Target
            {
                uint p; float d;
                if (!SwFetch(pos, p, d)) discard;
                depthOut = d;
                uint swi = (p - 1) / 128, tri = (p - 1) % 128;
                uint2 e = _SwList[swi];
                MeshletGpu m = _Meshlets[e.y];
                InstanceGpu I = _Instances[e.x];
                float3 ws[3], nw[3]; float4 cp[3]; float2 sp[3];
                [unroll] for (int k = 0; k < 3; k++)
                {
                    Vtx v = _Vertices[_Indices[(m.triOffset + tri) * 3 + k]];
                    ws[k] = mul(I.m, float4(v.pos, 1)).xyz;
                    nw[k] = mul((float3x3)I.m, v.nrm);
                    cp[k] = SwClip(ws[k]);
                    sp[k] = SwPx(cp[k]);
                }
                // 画素の中心の重心座標(画面で)→ 1/w で割って遠近を正す
                float2 c = pos.xy;
                float area = (sp[1].x - sp[0].x) * (sp[2].y - sp[0].y) - (sp[1].y - sp[0].y) * (sp[2].x - sp[0].x);
                float b0 = ((sp[2].x - sp[1].x) * (c.y - sp[1].y) - (sp[2].y - sp[1].y) * (c.x - sp[1].x)) / area;
                float b1 = ((sp[0].x - sp[2].x) * (c.y - sp[2].y) - (sp[0].y - sp[2].y) * (c.x - sp[2].x)) / area;
                float3 b = saturate(float3(b0, b1, 1 - b0 - b1)) / float3(cp[0].w, cp[1].w, cp[2].w);
                b /= (b.x + b.y + b.z);
                float3 posWS = ws[0] * b.x + ws[1] * b.y + ws[2] * b.z;
                float3 nrmWS = nw[0] * b.x + nw[1] * b.y + nw[2] * b.z;
                return half4(ShadeMeshlet(posWS, nrmWS, uint4(e.y, tri, m.lod, e.x), 2), 1);
            }
            ENDHLSL
        }
    }
}
