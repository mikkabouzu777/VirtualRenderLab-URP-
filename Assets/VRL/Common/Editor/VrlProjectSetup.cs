using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRL.EditorTools
{
    // 新規プロジェクトに URP を一度だけ入れる。VSM のページ印付けにカメラの深度が要るので、深度は「先に描く(ForcePrepass)」にする。
    [InitializeOnLoad]
    public static class VrlProjectSetup
    {
        const string SettingsDir = "Assets/VRL/Settings";
        const string AssetPath = SettingsDir + "/VRL_URP.asset";
        const string RendererPath = SettingsDir + "/VRL_URP_Renderer.asset";

        static VrlProjectSetup()
        {
            // delayCall はエディタが裏にいると走らないので update で 1 回だけ。
            EditorApplication.update += Once;
            VRL.VSM.VsmManager.BindDummies();
        }

        static void Once()
        {
            EditorApplication.update -= Once;
            if (GraphicsSettings.defaultRenderPipeline == null) Setup();
        }

        // レンダラーに機能(Renderer Feature)を足す。m_RendererFeatureMap も合わせないとインスペクタで壊れて見える。
        static void EnsureFeature<T>(ScriptableRendererData data, string name) where T : ScriptableRendererFeature
        {
            foreach (var f in data.rendererFeatures) if (f is T) return;
            var feature = ScriptableObject.CreateInstance<T>();
            feature.name = name;
            AssetDatabase.AddObjectToAsset(feature, data);
            data.rendererFeatures.Add(feature);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            var so = new SerializedObject(data);
            var map = so.FindProperty("m_RendererFeatureMap");
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
            data.SetDirty();
            Debug.Log("[VRL] レンダラーに機能を足した: " + name);
        }

        [MenuItem("Mikkabouzu/VirtualRenderLab/URP を設定し直す")]
        public static void Setup()
        {
            Directory.CreateDirectory(SettingsDir);
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetPath);
            if (asset == null)
            {
                // CreateRendererAsset は internal。メニューの「URP Asset (with Universal Renderer)」と同じ作り方を reflection で呼ぶ。
                var t = typeof(UniversalRenderPipelineAsset);
                var create = t.GetMethod("CreateRendererAsset", BindingFlags.Static | BindingFlags.NonPublic);
                var rendererTypeEnum = t.Assembly.GetType("UnityEngine.Rendering.Universal.RendererType");
                var rd = (ScriptableRendererData)create.Invoke(null, new object[]
                {
                    RendererPath, Enum.Parse(rendererTypeEnum, "UniversalRenderer"), false, "Renderer"
                });
                asset = UniversalRenderPipelineAsset.Create(rd);
                AssetDatabase.CreateAsset(asset, AssetPath);
            }

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer != null)
            {
                renderer.copyDepthMode = CopyDepthMode.ForcePrepass;
                EnsureFeature<VRL.VSM.VsmFeature>(renderer, "VSM");
                EnsureFeature<VRL.Meshlet.MeshletFeature>(renderer, "Meshlet Hi-Z");
                EditorUtility.SetDirty(renderer);
            }

            asset.supportsCameraDepthTexture = true;
            asset.supportsHDR = true;
            asset.msaaSampleCount = 1;
            asset.shadowDistance = 200f;
            asset.shadowCascadeCount = 4;
            EditorUtility.SetDirty(asset);

            GraphicsSettings.defaultRenderPipeline = asset;
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = null;
                QualitySettings.vSyncCount = 0;
            }
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.enableFrameTimingStats = true; // HUD の GPU 時間に使う
            AssetDatabase.SaveAssets();
            Debug.Log("[VRL] URP を設定した: " + AssetPath);
        }
    }
}
