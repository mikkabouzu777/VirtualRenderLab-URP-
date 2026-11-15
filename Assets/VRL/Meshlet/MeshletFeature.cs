using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VRL.Meshlet
{
    // Hi-Z の 2 回目。メインカメラだけ。
    //  深度を先に描いた直後: このコマの深度から Hi-Z を作る → 1 回目で隠れていた塊をもう一度調べる → 見えた物を深度へ描き足す → 次のコマ用に Hi-Z を作り直す
    //  不透明の後: 2 回目で見えた塊を色付きで描く
    // VSM(AfterRenderingPrePasses + 1)より先に回るので、2 回目の塊にも影のページが付く。
    public class MeshletFeature : ScriptableRendererFeature
    {
        PostPass post;
        ColorPass color;

        public override void Create()
        {
            post = new PostPass { renderPassEvent = RenderPassEvent.AfterRenderingPrePasses };
            color = new ColorPass { renderPassEvent = RenderPassEvent.AfterRenderingOpaques };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var f = MeshletField.Active;
            if (f == null || !f.isActiveAndEnabled || !f.WantsPost) return;
            if (renderingData.cameraData.camera != Camera.main) return;
            post.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(post);
            renderer.EnqueuePass(color);
        }

        class PostPass : ScriptableRenderPass
        {
            class PassData
            {
                public MeshletField f;
                public TextureHandle depth;
                public Camera cam;
                public int width, height;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                var camData = frameData.Get<UniversalCameraData>();
                if (!res.cameraDepthTexture.IsValid()) return;

                using var builder = renderGraph.AddUnsafePass<PassData>("VRL Meshlet Hi-Z", out var data);
                data.f = MeshletField.Active;
                data.depth = res.cameraDepthTexture;
                data.cam = camData.camera;
                data.width = camData.cameraTargetDescriptor.width;
                data.height = camData.cameraTargetDescriptor.height;
                builder.UseTexture(data.depth, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData d, UnsafeGraphContext ctx) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    RTHandle depth = d.depth;
                    d.f.PostPass(cmd, depth, d.width, d.height, d.cam);
                });
            }
        }

        class ColorPass : ScriptableRenderPass
        {
            class PassData { public MeshletField f; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                using var builder = renderGraph.AddRasterRenderPass<PassData>("VRL Meshlet Hi-Z 2nd Color", out var data);
                data.f = MeshletField.Active;
                builder.SetRenderAttachment(res.activeColorTexture, 0);
                builder.SetRenderAttachmentDepth(res.activeDepthTexture, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) => d.f.DrawPostColor(ctx.cmd));
            }
        }
    }
}
