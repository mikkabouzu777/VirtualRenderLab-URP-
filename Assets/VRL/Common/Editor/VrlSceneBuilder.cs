using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace VRL.EditorTools
{
    // 3 つのパーツの確認シーンを作り直す。シーンの中身は全部ここで決まる(手で直したら作り直しで消える)。
    public static class VrlSceneBuilder
    {
        const string SceneDir = "Assets/VRL/Scenes";
        const string MatDir = "Assets/VRL/Settings/Materials";

        // ---------- 共通 ----------

        public static Scene NewScene()
        {
            var s = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.42f, 0.5f, 0.62f);
            RenderSettings.ambientEquatorColor = new Color(0.32f, 0.33f, 0.32f);
            RenderSettings.ambientGroundColor = new Color(0.16f, 0.14f, 0.12f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.0025f;
            RenderSettings.fogColor = new Color(0.62f, 0.7f, 0.78f);
            return s;
        }

        public static Camera MakeCamera(Vector3 pos, Vector3 lookAt, float far = 2000f)
        {
            var go = new GameObject("Spectator Camera");
            go.tag = "MainCamera";
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(lookAt - pos);
            var cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = far;
            cam.fieldOfView = 55f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.7f, 0.78f);
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderShadows = true;
            go.AddComponent<VrlFlyCam>();
            go.AddComponent<VrlHud>();
            return cam;
        }

        public static Light MakeSun(Vector3 euler, LightShadows shadows = LightShadows.Soft)
        {
            var go = new GameObject("Sun");
            go.transform.rotation = Quaternion.Euler(euler);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.color = new Color(1f, 0.95f, 0.86f);
            l.intensity = 1.6f;
            l.shadows = shadows;
            return l;
        }

        public static Material LoadOrCreateMat(string name, string shaderName)
        {
            Directory.CreateDirectory(MatDir);
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var sh = Shader.Find(shaderName);
            if (sh == null) { Debug.LogError("[VRL] シェーダーが無い: " + shaderName); return null; }
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            else m.shader = sh;
            return m;
        }

        public static void Save(Scene s, string name)
        {
            Directory.CreateDirectory(SceneDir);
            string path = $"{SceneDir}/{name}.unity";
            EditorSceneManager.SaveScene(s, path);
            AssetDatabase.SaveAssets();
            Debug.Log("[VRL] シーンを作った: " + path);
        }

        // ---------- Meshlet ----------

        [MenuItem("Mikkabouzu/VirtualRenderLab/シーンを作る/1 Meshlet")]
        public static void BuildMeshlet()
        {
            var s = NewScene();
            var cam = MakeCamera(new Vector3(0, 8, -60), new Vector3(0, 2, 0));
            var fly = cam.GetComponent<VrlFlyCam>();
            fly.orbitRadius = 70f; fly.orbitHeight = 10f;
            MakeSun(new Vector3(42, -35, 0));

            var groundMat = LoadOrCreateMat("Ground_Lit", "Universal Render Pipeline/Lit");
            groundMat.SetColor("_BaseColor", new Color(0.36f, 0.34f, 0.3f));
            groundMat.SetFloat("_Smoothness", 0.1f);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = Vector3.one * 60f;
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundMat;
            Object.DestroyImmediate(ground.GetComponent<Collider>());

            var meshletMat = LoadOrCreateMat("Meshlet", "VRL/MeshletLit");
            var compareMat = LoadOrCreateMat("Meshlet_Compare_Lit", "Universal Render Pipeline/Lit");
            compareMat.SetColor("_BaseColor", new Color(0.52f, 0.48f, 0.43f));
            compareMat.SetFloat("_Smoothness", 0.15f);
            compareMat.enableInstancing = true;

            var go = new GameObject("MeshletField");
            var f = go.AddComponent<VRL.Meshlet.MeshletField>();
            f.cullShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/VRL/Meshlet/MeshletCull.compute");
            f.meshletMaterial = meshletMat;
            f.compareMaterial = compareMat;
            Save(s, "1_Meshlet");
        }

        // ---------- VSM ----------

        static Material VsmMat(string name, Color c, float pattern = 0, float scale = 1)
        {
            var m = LoadOrCreateMat(name, "VRL/VsmLit");
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Pattern", pattern);
            m.SetFloat("_PatternScale", scale);
            return m;
        }

        static GameObject Prim(PrimitiveType t, string name, Transform parent, Vector3 pos, Vector3 scale, Material m, Quaternion? rot = null)
        {
            var go = GameObject.CreatePrimitive(t);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            return go;
        }

        [MenuItem("Mikkabouzu/VirtualRenderLab/シーンを作る/2 VSM")]
        public static void BuildVsm()
        {
            var s = NewScene();
            var cam = MakeCamera(new Vector3(-3, 2.2f, -13), new Vector3(1, 1.2f, 2));
            var fly = cam.GetComponent<VrlFlyCam>();
            fly.orbitRadius = 30f; fly.orbitHeight = 5f;
            var sun = MakeSun(new Vector3(32, -55, 0), LightShadows.None);

            var ground = VsmMat("Vsm_Ground", new Color(0.42f, 0.45f, 0.36f), 1, 0.25f);
            var trunk = VsmMat("Vsm_Trunk", new Color(0.33f, 0.24f, 0.17f));
            var leaves = VsmMat("Vsm_Leaves", new Color(0.24f, 0.42f, 0.18f), 2, 1f);
            var wood = VsmMat("Vsm_Wood", new Color(0.62f, 0.5f, 0.36f));
            var stone = VsmMat("Vsm_Stone", new Color(0.62f, 0.6f, 0.57f), 2, 0.5f);
            var white = VsmMat("Vsm_White", new Color(0.85f, 0.85f, 0.83f));

            var root = new GameObject("VSM_World").transform;
            Prim(PrimitiveType.Plane, "Ground", root, Vector3.zero, Vector3.one * 100f, ground);

            // あずまや: 細い板の影で細かさを見る
            var perg = new GameObject("Pergola").transform; perg.SetParent(root, false);
            for (int i = 0; i < 4; i++)
                Prim(PrimitiveType.Cube, "Post", perg, new Vector3((i % 2) * 8 - 4, 1.5f, (i / 2) * 5 - 2.5f), new Vector3(0.18f, 3f, 0.18f), wood);
            for (int i = 0; i < 2; i++)
                Prim(PrimitiveType.Cube, "Beam", perg, new Vector3(0, 3.05f, i * 5 - 2.5f), new Vector3(8.4f, 0.2f, 0.18f), wood);
            for (int i = 0; i < 34; i++)
                Prim(PrimitiveType.Cube, "Slat", perg, new Vector3(-4.1f + i * 0.25f, 3.2f, 0), new Vector3(0.06f, 0.1f, 5.6f), wood);
            // ベンチと柵(とても細い棒)
            Prim(PrimitiveType.Cube, "Bench", perg, new Vector3(0, 0.45f, 0), new Vector3(2.4f, 0.08f, 0.5f), wood);
            for (int i = 0; i < 70; i++)
                Prim(PrimitiveType.Cube, "Picket", root, new Vector3(-12f + i * 0.35f, 0.6f, 6f), new Vector3(0.04f, 1.2f, 0.04f), white);
            Prim(PrimitiveType.Cube, "Rail", root, new Vector3(0.1f, 1.05f, 6f), new Vector3(24.5f, 0.05f, 0.05f), white);

            // 風車(動く影)
            var mill = new GameObject("Windmill").transform; mill.SetParent(root, false);
            mill.localPosition = new Vector3(9, 0, 3);
            Prim(PrimitiveType.Cylinder, "Tower", mill, new Vector3(0, 4, 0), new Vector3(0.6f, 4, 0.6f), stone);
            var hub = new GameObject("Hub").transform; hub.SetParent(mill, false);
            hub.localPosition = new Vector3(0, 8, -0.5f);
            hub.gameObject.AddComponent<VrlSpin>().axis = Vector3.forward;
            for (int i = 0; i < 4; i++)
            {
                var arm = new GameObject("Arm").transform; arm.SetParent(hub, false);
                arm.localRotation = Quaternion.Euler(0, 0, i * 90);
                Prim(PrimitiveType.Cube, "Blade", arm, new Vector3(0, 2.6f, 0), new Vector3(0.7f, 5f, 0.05f), white);
            }

            // 森: 近くから 300m 先まで
            var rnd = new System.Random(11);
            var forest = new GameObject("Forest").transform; forest.SetParent(root, false);
            for (int i = 0; i < 420; i++)
            {
                float a = (float)rnd.NextDouble() * Mathf.PI * 2f;
                float d = Mathf.Lerp(16f, 300f, Mathf.Pow((float)rnd.NextDouble(), 1.6f));
                var p = new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
                float h = Mathf.Lerp(4f, 11f, (float)rnd.NextDouble());
                var t = new GameObject("Tree").transform; t.SetParent(forest, false); t.localPosition = p;
                Prim(PrimitiveType.Cylinder, "Trunk", t, new Vector3(0, h * 0.5f, 0), new Vector3(0.35f, h * 0.5f, 0.35f), trunk);
                float cr = Mathf.Lerp(2f, 3.6f, (float)rnd.NextDouble());
                Prim(PrimitiveType.Sphere, "Canopy", t, new Vector3(0, h + cr * 0.3f, 0), new Vector3(cr * 2f, cr * 1.6f, cr * 2f), leaves);
                Prim(PrimitiveType.Sphere, "Canopy2", t, new Vector3(cr * 0.5f, h - cr * 0.2f, 0.3f), Vector3.one * cr * 1.3f, leaves);
            }

            // 遠くの塔(CSM の届かない所の影)
            for (int i = 0; i < 10; i++)
            {
                float a = i / 10f * Mathf.PI * 2f + 0.3f;
                float d = 320f + (i % 3) * 60f;
                float h = 40f + (i % 4) * 15f;
                Prim(PrimitiveType.Cube, "FarTower", root, new Vector3(Mathf.Cos(a) * d, h * 0.5f, Mathf.Sin(a) * d), new Vector3(8, h, 8), stone);
            }

            var go = new GameObject("VsmManager");
            var mgr = go.AddComponent<VRL.VSM.VsmManager>();
            mgr.sun = sun;
            mgr.pagesShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/VRL/VSM/VsmPages.compute");
            mgr.clearShader = Shader.Find("Hidden/VRL/VsmClearPages");
            Save(s, "2_VSM");
        }

        // ---------- RVT ----------

        [MenuItem("Mikkabouzu/VirtualRenderLab/シーンを作る/3 RVT")]
        public static void BuildRvt()
        {
            var s = NewScene();
            RenderSettings.fogDensity = 0.0018f;
            var cam = MakeCamera(new Vector3(-20, 22, -175), new Vector3(10, 10, -100), 3000f);
            var fly = cam.GetComponent<VrlFlyCam>();
            fly.orbitCenter = new Vector3(0, 0, -40); fly.orbitRadius = 140f; fly.orbitHeight = 45f; fly.orbitPeriod = 90f;
            MakeSun(new Vector3(38, -40, 0), LightShadows.Soft);

            var trunk = LoadOrCreateMat("Rvt_TreeTrunk", "Universal Render Pipeline/Lit");
            trunk.SetColor("_BaseColor", new Color(0.3f, 0.22f, 0.15f));
            trunk.enableInstancing = true;
            var leaf = LoadOrCreateMat("Rvt_TreeLeaf", "Universal Render Pipeline/Lit");
            leaf.SetColor("_BaseColor", new Color(0.18f, 0.33f, 0.12f));
            leaf.SetFloat("_Smoothness", 0.05f);
            leaf.enableInstancing = true;

            var go = new GameObject("RvtManager");
            var m = go.AddComponent<VRL.RVT.RvtManager>();
            m.terrainMaterial = LoadOrCreateMat("Rvt_Terrain", "VRL/RvtTerrain");
            m.bakeShader = Shader.Find("Hidden/VRL/RvtBake");
            m.feedbackShader = Shader.Find("Hidden/VRL/RvtFeedback");
            m.treeTrunkMaterial = trunk;
            m.treeLeafMaterial = leaf;
            Save(s, "3_RVT");
        }

        // ---------- 合体 ----------

        [MenuItem("Mikkabouzu/VirtualRenderLab/シーンを作る/4 合体(Meshlet+VSM+RVT)")]
        public static void BuildCombined()
        {
            var s = NewScene();
            RenderSettings.fogDensity = 0.0018f;
            var cam = MakeCamera(new Vector3(-30, 24, -150), new Vector3(0, 12, -60), 3000f);
            var fly = cam.GetComponent<VrlFlyCam>();
            fly.orbitCenter = new Vector3(0, 0, -30); fly.orbitRadius = 110f; fly.orbitHeight = 40f; fly.orbitPeriod = 90f;
            var sun = MakeSun(new Vector3(30, -40, 0), LightShadows.None);

            var vsmGo = new GameObject("VsmManager");
            var vsm = vsmGo.AddComponent<VRL.VSM.VsmManager>();
            vsm.sun = sun;
            vsm.pagesShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/VRL/VSM/VsmPages.compute");
            vsm.clearShader = Shader.Find("Hidden/VRL/VsmClearPages");
            vsm.depthRange = 800f;

            var rvtGo = new GameObject("RvtManager");
            var rvt = rvtGo.AddComponent<VRL.RVT.RvtManager>();
            rvt.terrainMaterial = LoadOrCreateMat("Rvt_Terrain", "VRL/RvtTerrain");
            rvt.bakeShader = Shader.Find("Hidden/VRL/RvtBake");
            rvt.feedbackShader = Shader.Find("Hidden/VRL/RvtFeedback");
            rvt.treeTrunkMaterial = LoadOrCreateMat("Rvt_TreeTrunk", "Universal Render Pipeline/Lit");
            rvt.treeLeafMaterial = LoadOrCreateMat("Rvt_TreeLeaf", "Universal Render Pipeline/Lit");
            rvt.treeObjectTrunk = VsmMat("Vsm_Trunk", new Color(0.3f, 0.22f, 0.15f));
            rvt.treeObjectLeaf = VsmMat("Vsm_Leaves", new Color(0.2f, 0.36f, 0.13f), 2, 1f);

            var mlGo = new GameObject("MeshletField");
            var ml = mlGo.AddComponent<VRL.Meshlet.MeshletField>();
            ml.cullShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/VRL/Meshlet/MeshletCull.compute");
            ml.meshletMaterial = LoadOrCreateMat("Meshlet", "VRL/MeshletLit");
            ml.compareMaterial = LoadOrCreateMat("Meshlet_Compare_Lit", "Universal Render Pipeline/Lit");
            ml.terrain = rvt;
            ml.instanceCount = 140;
            ml.fieldRadius = 150f;

            var keys = new GameObject("CombinedKeys").AddComponent<VrlCombinedKeys>();
            keys.meshlet = ml; keys.vsm = vsm; keys.rvt = rvt;
            Save(s, "4_Combined");
        }
    }
}
