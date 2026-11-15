using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VRL.VSM
{
    // URP の描画の途中(深度を先に描いた直後)で VsmManager.Execute を呼ぶ。シーンに VsmManager が無ければ何もしない。メインカメラだけ。
    public class VsmFeature : ScriptableRendererFeature
    {
        VsmPass pass;

        public override void Create()
        {
            // Meshlet の 2 回目(Hi-Z で隠れていた塊を深度へ描き足す)の後に回す
            pass = new VsmPass { renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 1 };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var m = VsmManager.Active;
            if (m == null || !m.isActiveAndEnabled || !m.vsmEnabled) return;
            // ページの持ち越しはカメラ 1 台ぶん。メインカメラだけで回す(シーンビューはその結果を読むだけ)
            if (renderingData.cameraData.camera != Camera.main) return;
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(pass);
        }

        class VsmPass : ScriptableRenderPass
        {
            class PassData
            {
                public VsmManager mgr;
                public TextureHandle depth;
                public Camera cam;
                public int width, height;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                var camData = frameData.Get<UniversalCameraData>();
                if (!res.cameraDepthTexture.IsValid()) return;

                using var builder = renderGraph.AddUnsafePass<PassData>("VRL VSM", out var data);
                data.mgr = VsmManager.Active;
                data.depth = res.cameraDepthTexture;
                data.cam = camData.camera;
                data.width = camData.cameraTargetDescriptor.width;
                data.height = camData.cameraTargetDescriptor.height;
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData d, UnsafeGraphContext ctx) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    RTHandle depth = d.depth;
                    d.mgr.Execute(cmd, d.cam, depth, d.width, d.height);
                });
            }
        }
    }
}
