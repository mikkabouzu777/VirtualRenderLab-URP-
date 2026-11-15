using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace VRL.Meshlet
{
    // Meshlet 描画の試作。重い岩を作って DAG(継ぎ目のない塊単位の LOD)に組み、GPU で塊単位に外してから間接描画する。
    // できること:
    //  - DAG の切り口: 塊ごとに「画面での誤差が errorPx 以下になる一番粗い段」を選ぶ(1 個の岩の中で近い所は細かく・遠い所は粗く)
    //  - 視錐台・裏向き・Hi-Z(前のコマの深度ピラミッドで隠れた塊)で外す。Hi-Z で外した塊は、このコマの深度でもう一度調べて見えたら描く(2 回目・MeshletFeature)
    //  - VSM へは、描き直すページごとに、ページの 1 画素に合う段の塊でページに掛かる物だけを描く
    //  - 物ごとに、距離の幅から切り口に入りうる DAG の段だけを先に選ぶ(全部の塊 x 物を調べない)
    //  - 画面で小さい塊はソフトウェアラスタ(compute で三角形を塗る)。ハードの描画と深度でまざる
    //  - DAG は組んだら persistentDataPath/VRLCache へ保存し、次からは読むだけ
    public class MeshletField : MonoBehaviour, VSM.IVsmClusterCaster
    {
        public static MeshletField Active { get; private set; }

        [Header("岩")]
        [Tooltip("正二十面体の細分化の回数。7 で 1 個 327,680 三角形")]
        [Range(4, 8)] public int rockSubdiv = 7;
        [Tooltip("岩の数")] public int instanceCount = 160;
        [Tooltip("並べる半径(m)")] public float fieldRadius = 110f;
        public int seed = 3;

        [Header("描画")]
        public ComputeShader cullShader;
        public Material meshletMaterial;
        [Tooltip("普通の描画で比べる時の材質(URP Lit・GPU Instancing 有効)")]
        public Material compareMaterial;
        [Tooltip("画面での形の誤差がこのピクセル数以下の、一番粗い塊を選ぶ")]
        public float errorPx = 1f;
        public bool coneCull = true;
        [Tooltip("前のコマの深度で隠れた塊を外す(Hi-Z)。Renderer に MeshletFeature が要る")]
        public bool occlusionCull = true;
        public bool castShadows = true;
        [Tooltip("検証用: 1 回目で全部を「隠れた」扱いにし、Hi-Z の 2 回目だけで描く(2 回目の流れが正しければ見た目は変わらない)")]
        public bool debugAllToPost;
        [Tooltip("画面で小さい塊を compute で塗る(ソフトウェアラスタ)。Renderer に MeshletFeature が要る")]
        public bool softwareRaster = true;
        [Tooltip("塊の球の半径がこのピクセル数より小さければソフトウェアラスタ")]
        public float swMaxPx = 12f;
        [Tooltip("DAG をファイルに保存して次から読む")]
        public bool useDagCache = true;

        [Header("合体")]
        [Tooltip("入れると岩を RVT の地形の上に置く")]
        public RVT.RvtManager terrain;
        [Tooltip("シーンに VsmManager があれば、岩の影を塊単位で VSM へ描く")]
        public bool castVsmShadows = true;
        [Tooltip("VSM の影: ページの 1 画素の何倍までの形の誤差を許すか")]
        public float shadowErrorTexels = 1f;
        [Tooltip("切ると自分ではキーを読まない(合体シーンでまとめて切り替える時)")]
        public bool handleKeys = true;
        public int DebugMode { get => debugMode; set => debugMode = value; }
        public bool Frozen { get => frozen; set => frozen = value; }
        /// <summary>MeshletFeature を回すか(Hi-Z の 2 回目かソフトウェアラスタ)。</summary>
        public bool WantsPost => ready && !compareMode && (occlusionCull || softwareRaster);
        public bool SoftwareRaster { get => softwareRaster; set => softwareRaster = value; }

        // 実行時の状態
        GraphicsBuffer vtxBuf, idxBuf, meshletBuf, instBuf, levelBuf, visBuf, vis2Buf, occBuf, argsBuf, args2Buf, dispatchBuf, statsBuf;
        GraphicsBuffer shadowWorkBuf, shadowDrawBuf, shadowArgsBuf;
        GraphicsBuffer workBuf, swListBuf, swDepthBuf, swPayloadBuf, swDrawArgsBuf;
        int swW, swH;
        bool usedSwThisFrame;
        int postFrame = -10;
        uint statWork, statTested, statSw, statSwTris;
        int kSwDrawArgs;
        MaterialPropertyBlock mpbSwShadow;
        Material swShadowMat; // ShadowCaster だけ通す写し(ShadowsOnly でも深度を先に描くパスに入ってしまうため)
        int kInst, kMainArgs, kSwArgs, kSwClear, kSwDepth, kSwPayload, passSwDepth, passSwResolve;
        MaterialPropertyBlock mpbSw;
        Matrix4x4[] instMatrices;
        Mesh compareMesh;
        int totalClusters, lod0Clusters, levelCount, maxVisible;
        int kMain, kPost, kPostArgs, kHzb0, kHzbReduce, kShPages, kShArgs, kShClusters, kShFinish;
        int passForward, passDepth, passVsm;
        Vector4 meshSphere;
        readonly List<int> levelTris = new List<int>();
        MaterialPropertyBlock mpb, mpb2, mpbShadow;
        readonly Plane[] planes = new Plane[6];
        readonly Vector4[] planeVec = new Vector4[6];
        Vector3 cullCamPos;
        bool frozen, compareMode, ready;
        int debugMode, forceLevel = -1;
        string buildLog = "";
        uint statVisible, statVisible2, statTris, statTris2, statFrustum, statCone, statOccluded;
        uint statShadowWork, statShadowClusters, statShadowFailed;
        long statShadowTris;
        bool readbackPending;

        // Hi-Z(段 0..n を 1 枚に並べる: 段 0 が左、段 1 以降は右の列に上から)
        RenderTexture hzb;
        int hzbW0, hzbH0, hzbMips;
        readonly Vector4[] hzbRects = new Vector4[16];
        Matrix4x4 hzbVP;
        Vector4 hzbCamPos, hzbCamFwd;
        bool hzbValid, usedHzbThisFrame;
        int hzbFrame = -10;

        const int MaxShadowWork = 65535, MaxShadowDraw = 1 << 20;
        static readonly uint[] ArgsInit = { MeshletBuilder.MaxTris * 3, 0, 0, 0 };
        static readonly uint[] StatsZero = new uint[16];
        static readonly uint[] ShadowStatsZero = { 0, 0, 0 };

        [StructLayout(LayoutKind.Sequential)]
        struct InstanceGpu { public Matrix4x4 m; public Vector4 extra; }

        void OnEnable() { Active = this; VrlHud.Lines += Hud; }
        void OnDisable() { if (Active == this) Active = null; VrlHud.Lines -= Hud; }

        void Start()
        {
            var sw = Stopwatch.StartNew();
            MeshletDag.Result dag = useDagCache ? MeshletDagCache.Load(rockSubdiv, seed) : null;
            string how;
            if (dag != null) how = $"DAG をファイルから読んだ {sw.ElapsedMilliseconds} ms";
            else
            {
                var g0 = RockMeshGen.Rock(rockSubdiv, seed);
                long tGen = sw.ElapsedMilliseconds;
                dag = MeshletDag.Build(g0);
                long tDag = sw.ElapsedMilliseconds - tGen;
                how = $"作成 {tGen} ms + DAG 組み {tDag} ms";
                if (useDagCache) { MeshletDagCache.Save(rockSubdiv, seed, dag); how += "(保存した)"; }
            }

            totalClusters = dag.meshlets.Count;
            lod0Clusters = (int)dag.levels[0].count;
            levelCount = dag.levels.Count;
            levelTris.AddRange(dag.levelTris);

            // 元の形 = 段 0 の頂点(先頭 originalVertexCount 個)と、段 0 の塊の三角形(先頭)
            int n0 = dag.originalVertexCount;
            var pos0 = new Vector3[n0];
            var nrm0 = new Vector3[n0];
            for (int i = 0; i < n0; i++) { pos0[i] = dag.verts[i].pos; nrm0[i] = dag.verts[i].nrm; }
            var idx0 = new int[levelTris[0] * 3];
            for (int i = 0; i < idx0.Length; i++) idx0[i] = (int)dag.indices[i];

            // 全体の球(物体座標)
            Vector3 mn = pos0[0], mx = mn;
            foreach (var p in pos0) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            var c = (mn + mx) * 0.5f; float r = 0;
            foreach (var p in pos0) r = Mathf.Max(r, (p - c).magnitude);
            meshSphere = new Vector4(c.x, c.y, c.z, r);

            // 比べる用の普通のメッシュ(元の形)
            compareMesh = new Mesh { indexFormat = IndexFormat.UInt32, name = "Rock_LOD0" };
            compareMesh.SetVertices(pos0); compareMesh.SetNormals(nrm0); compareMesh.SetTriangles(idx0, 0);

            // 物を並べる
            if (terrain != null) terrain.EnsureTerrain();
            var rnd = new System.Random(seed * 7919);
            var inst = new InstanceGpu[instanceCount];
            instMatrices = new Matrix4x4[instanceCount];
            for (int i = 0; i < instanceCount; i++)
            {
                float ang = (float)rnd.NextDouble() * Mathf.PI * 2f;
                float rad = Mathf.Sqrt((float)rnd.NextDouble()) * fieldRadius;
                float s = Mathf.Lerp(0.8f, 4.5f, Mathf.Pow((float)rnd.NextDouble(), 2f));
                if (i % 23 == 0) s *= 3.5f; // ときどき大岩
                var pos = new Vector3(Mathf.Cos(ang) * rad, 0.2f * s, Mathf.Sin(ang) * rad);
                if (terrain != null) pos.y = terrain.SampleHeight(pos.x, pos.z) + 0.05f * s; // 少し埋める
                var rot = Quaternion.Euler((float)rnd.NextDouble() * 10f - 5f, (float)rnd.NextDouble() * 360f, (float)rnd.NextDouble() * 10f - 5f);
                var m = Matrix4x4.TRS(pos, rot, Vector3.one * s);
                inst[i] = new InstanceGpu { m = m, extra = new Vector4(s, 0, 0, 0) };
                instMatrices[i] = m;
            }

            vtxBuf = MakeBuffer(dag.verts.ToArray());
            idxBuf = MakeBuffer(dag.indices.ToArray());
            meshletBuf = MakeBuffer(dag.meshlets.ToArray());
            instBuf = MakeBuffer(inst);
            levelBuf = MakeBuffer(dag.levels.ToArray());
            maxVisible = instanceCount * lod0Clusters;
            visBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxVisible, 8);
            vis2Buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxVisible, 8);
            occBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxVisible, 8);
            argsBuf = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 4, 4);
            args2Buf = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 4, 4);
            dispatchBuf = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 12, 4);
            workBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCount * levelCount, 8);
            swListBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxVisible, 8);
            swDrawArgsBuf = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 4, 4);
            statsBuf = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 16, 4);
            shadowWorkBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxShadowWork, 8);
            shadowDrawBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxShadowDraw, 16);
            shadowArgsBuf = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 4, 4);

            var cs = cullShader;
            kInst = cs.FindKernel("CullInstances");
            kMainArgs = cs.FindKernel("WriteMainArgs");
            kSwArgs = cs.FindKernel("WriteSwArgs");
            kSwDrawArgs = cs.FindKernel("WriteSwDrawArgs");
            kSwClear = cs.FindKernel("SwClear");
            kSwDepth = cs.FindKernel("SwRasterDepth");
            kSwPayload = cs.FindKernel("SwRasterPayload");
            kMain = cs.FindKernel("CullMain");
            kPost = cs.FindKernel("CullPost");
            kPostArgs = cs.FindKernel("WritePostArgs");
            kHzb0 = cs.FindKernel("HzbFromDepth");
            kHzbReduce = cs.FindKernel("HzbReduce");
            kShPages = cs.FindKernel("ShadowPages");
            kShArgs = cs.FindKernel("ShadowArgs");
            kShClusters = cs.FindKernel("ShadowClusters");
            kShFinish = cs.FindKernel("ShadowFinish");
            cs.SetBuffer(kSwDrawArgs, "_SwDrawArgs", swDrawArgsBuf);
            foreach (int k in new[] { kSwDrawArgs, kInst, kMainArgs, kMain, kPost, kPostArgs, kShPages, kShArgs, kShClusters, kShFinish, kSwArgs, kSwClear, kSwDepth, kSwPayload })
            {
                cs.SetBuffer(k, "_Work", workBuf);
                cs.SetBuffer(k, "_SwList", swListBuf);
                cs.SetBuffer(k, "_Vertices", vtxBuf);
                cs.SetBuffer(k, "_Indices", idxBuf);
                cs.SetBuffer(k, "_Meshlets", meshletBuf);
                cs.SetBuffer(k, "_Instances", instBuf);
                cs.SetBuffer(k, "_Levels", levelBuf);
                cs.SetBuffer(k, "_Visible", visBuf);
                cs.SetBuffer(k, "_Visible2", vis2Buf);
                cs.SetBuffer(k, "_Occluded", occBuf);
                cs.SetBuffer(k, "_Args", argsBuf);
                cs.SetBuffer(k, "_Args2", args2Buf);
                cs.SetBuffer(k, "_Dispatch", dispatchBuf);
                cs.SetBuffer(k, "_Stats", statsBuf);
                cs.SetBuffer(k, "_ShadowWork", shadowWorkBuf);
                cs.SetBuffer(k, "_ShadowDraw", shadowDrawBuf);
                cs.SetBuffer(k, "_ShadowArgs", shadowArgsBuf);
            }
            // Hi-Z がまだ無い時も、縛っていないと「設定されていない」で止まるので仮の物を渡す
            cs.SetTexture(kMain, "_Hzb", Texture2D.whiteTexture);
            cs.SetTexture(kPost, "_Hzb", Texture2D.whiteTexture);
            cs.SetInt("_InstanceCount", instanceCount);
            cs.SetInt("_TotalClusters", totalClusters);
            cs.SetInt("_MaxVisible", maxVisible);
            cs.SetInt("_MaxShadowWork", MaxShadowWork);
            cs.SetInt("_MaxShadowDraw", MaxShadowDraw);
            cs.SetInt("_LevelCount", levelCount);
            cs.SetVector("_MeshSphere", meshSphere);

            mpb = MakeMpb(visBuf, 0);
            mpb2 = MakeMpb(vis2Buf, 1);
            mpbShadow = MakeMpb(visBuf, 0);
            mpbShadow.SetBuffer("_ShadowDraw", shadowDrawBuf);
            passForward = meshletMaterial.FindPass("ForwardLit");
            passDepth = meshletMaterial.FindPass("DepthOnly");
            passVsm = meshletMaterial.FindPass("VsmCaster");
            passSwDepth = meshletMaterial.FindPass("SwDepth");
            passSwResolve = meshletMaterial.FindPass("SwResolve");
            mpbSw = MakeMpb(visBuf, 0);
            mpbSw.SetBuffer("_SwList", swListBuf);
            mpbSwShadow = MakeMpb(swListBuf, 0);
            swShadowMat = new Material(meshletMaterial) { name = "Meshlet_SwShadowOnly" };
            foreach (var tag in new[] { "DepthOnly", "DepthNormals", "UniversalForward", "VsmCaster", "SwDepth", "SwResolve" })
                swShadowMat.SetShaderPassEnabled(tag, false);

            var vsm = VSM.VsmManager.Active;
            if (castVsmShadows && vsm != null) vsm.RegisterClusterCaster(this);

            var sbLv = new StringBuilder();
            for (int i = 0; i < levelTris.Count; i++) sbLv.Append(i == 0 ? "" : " → ").Append(levelTris[i] >= 10000 ? $"{levelTris[i] / 1000}k" : levelTris[i].ToString());
            buildLog = $"岩 1 個: {levelTris[0]:N0} 三角形 → DAG {levelCount} 段・塊 計 {totalClusters:N0}(段 0 は {lod0Clusters:N0})\n" +
                       $"各段の三角形 {sbLv}\n" +
                       $"{how}  GPU {((long)dag.verts.Count * 24 + dag.indices.Count * 4 + totalClusters * 96) / (1024 * 1024)} MB";
            Debug.Log("[VRL Meshlet] " + buildLog.Replace('\n', ' '));
            ready = true;
        }

        MaterialPropertyBlock MakeMpb(GraphicsBuffer visible, float postPass)
        {
            var p = new MaterialPropertyBlock();
            p.SetBuffer("_Vertices", vtxBuf);
            p.SetBuffer("_Indices", idxBuf);
            p.SetBuffer("_Meshlets", meshletBuf);
            p.SetBuffer("_Instances", instBuf);
            p.SetBuffer("_Visible", visible);
            p.SetFloat("_PostPass", postPass);
            return p;
        }

        static GraphicsBuffer MakeBuffer<T>(T[] data) where T : struct
        {
            var b = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.Length, Marshal.SizeOf<T>());
            b.SetData(data);
            return b;
        }

        void Update()
        {
            if (!handleKeys) return;
            for (int i = 0; i < ModeNames.Length; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) debugMode = i;
            if (Input.GetKeyDown(KeyCode.F)) frozen = !frozen;
            if (Input.GetKeyDown(KeyCode.C)) coneCull = !coneCull;
            if (Input.GetKeyDown(KeyCode.O)) occlusionCull = !occlusionCull;
            if (Input.GetKeyDown(KeyCode.R)) softwareRaster = !softwareRaster;
            if (Input.GetKeyDown(KeyCode.M)) compareMode = !compareMode;
            if (Input.GetKeyDown(KeyCode.X)) castShadows = !castShadows;
            if (Input.GetKeyDown(KeyCode.K)) forceLevel = forceLevel + 1 >= levelCount ? -1 : forceLevel + 1;
            if (Input.GetKeyDown(KeyCode.LeftBracket)) errorPx = Mathf.Max(0.25f, errorPx * 0.5f);
            if (Input.GetKeyDown(KeyCode.RightBracket)) errorPx = Mathf.Min(64f, errorPx * 2f);
        }

        void LateUpdate()
        {
            if (!ready) return;
            var cam = Camera.main;
            if (cam == null) return;

            if (compareMode)
            {
                var rpc = new RenderParams(compareMaterial)
                {
                    shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = true,
                };
                for (int i = 0; i < instMatrices.Length; i += 1023)
                    Graphics.RenderMeshInstanced(rpc, compareMesh, 0, instMatrices, Mathf.Min(1023, instMatrices.Length - i), i);
                return;
            }

            // F で止めている間は、止めた時のカメラで外し続ける(外れ方を横から見られる)
            if (!frozen)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, planes);
                for (int i = 0; i < 6; i++) planeVec[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
                cullCamPos = cam.transform.position;
            }

            // Hi-Z は前のコマに 2 回目が回っていた時だけ使う(回らないと、隠れていた塊が描かれないまま消える)
            usedHzbThisFrame = occlusionCull && hzbValid && !frozen && hzbFrame >= Time.frameCount - 1 && hzb != null;
            // ソフトウェアラスタの塊は MeshletFeature が塗る。前のコマに回っていなければ使わない(塗られずに消える)
            usedSwThisFrame = softwareRaster && postFrame >= Time.frameCount - 1;

            argsBuf.SetData(ArgsInit);
            args2Buf.SetData(ArgsInit);
            swDrawArgsBuf.SetData(ArgsInit);
            statsBuf.SetData(StatsZero);
            var cs = cullShader;
            cs.SetVectorArray("_Planes", planeVec);
            cs.SetVector("_CamPos", cullCamPos);
            cs.SetFloat("_PxScale", 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / cam.pixelHeight);
            cs.SetFloat("_ErrorPx", errorPx);
            cs.SetInt("_ForceLevel", forceLevel);
            cs.SetInt("_ConeCull", coneCull ? 1 : 0);
            cs.SetInt("_UseHzb", usedHzbThisFrame ? 1 : 0);
            cs.SetInt("_DebugAllToPost", debugAllToPost ? 1 : 0);
            cs.SetInt("_SwRaster", usedSwThisFrame ? 1 : 0);
            cs.SetFloat("_SwMaxPx", swMaxPx);
            cs.SetFloat("_NearClip", cam.nearClipPlane);
            if (usedHzbThisFrame) SetHzbParams(null, hzbVP, hzbCamPos, hzbCamFwd, kMain);
            cs.Dispatch(kInst, (instanceCount + 63) / 64, 1, 1);
            cs.Dispatch(kMainArgs, 1, 1, 1);
            cs.DispatchIndirect(kMain, dispatchBuf, 24);
            cs.Dispatch(kSwDrawArgs, 1, 1, 1);

            meshletMaterial.SetInt("_DebugMode", debugMode);
            SetAmbient();
            bool vsmOn = VSM.VsmManager.Active != null && VSM.VsmManager.Active.vsmEnabled;
            var rp = new RenderParams(meshletMaterial)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f),
                matProps = mpb,
                shadowCastingMode = castShadows && !vsmOn ? ShadowCastingMode.On : ShadowCastingMode.Off,
                receiveShadows = true,
            };
            Graphics.RenderPrimitivesIndirect(rp, MeshTopology.Triangles, argsBuf, 1);
            // ソフトウェアラスタの塊は色を compute で塗るが、URP の普通の影には入らないので、影だけハードで描く
            if (usedSwThisFrame && castShadows && !vsmOn)
            {
                var rps = rp;
                rps.material = swShadowMat;
                rps.matProps = mpbSwShadow;
                rps.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                Graphics.RenderPrimitivesIndirect(rps, MeshTopology.Triangles, swDrawArgsBuf, 1);
            }

            if (!readbackPending)
            {
                readbackPending = true;
                AsyncGPUReadback.Request(argsBuf, r =>
                {
                    if (!r.hasError) statVisible = r.GetData<uint>()[1];
                    AsyncGPUReadback.Request(args2Buf, r1 =>
                    {
                        if (!r1.hasError) statVisible2 = r1.GetData<uint>()[1];
                        AsyncGPUReadback.Request(statsBuf, r2 =>
                        {
                            readbackPending = false;
                            if (r2.hasError) return;
                            var d = r2.GetData<uint>();
                            statTris = d[0]; statFrustum = d[1]; statCone = d[2]; statOccluded = d[3]; statTris2 = d[4];
                            statShadowWork = d[6]; statShadowTris = d[7]; statShadowFailed = d[8];
                            statWork = d[9]; statTested = d[10]; statSw = d[11]; statSwTris = d[12];
                        });
                    });
                });
                AsyncGPUReadback.Request(shadowArgsBuf, r => { if (!r.hasError) statShadowClusters = r.GetData<uint>()[1]; });
            }
        }

        readonly Vector4[] shCoef = new Vector4[7];

        // シーンの環境光(SH)を、Unity の unity_SHAr..SHC と同じ並びにして渡す
        void SetAmbient()
        {
            var sh = RenderSettings.ambientProbe;
            for (int i = 0; i < 3; i++)
            {
                shCoef[i] = new Vector4(sh[i, 3], sh[i, 1], sh[i, 2], sh[i, 0] - sh[i, 6]);
                shCoef[i + 3] = new Vector4(sh[i, 4], sh[i, 5], sh[i, 6] * 3f, sh[i, 7]);
            }
            shCoef[6] = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1);
            Shader.SetGlobalVectorArray("_VrlSH", shCoef);
        }

        // ---------------- Hi-Z(MeshletFeature から、メインカメラについて) ----------------

        void EnsureHzb(int w, int h)
        {
            int w0 = Mathf.Max(8, Mathf.NextPowerOfTwo(w) / 2), h0 = Mathf.Max(8, Mathf.NextPowerOfTwo(h) / 2);
            if (hzb != null && w0 == hzbW0 && h0 == hzbH0) return;
            if (hzb != null) hzb.Release();
            hzbW0 = w0; hzbH0 = h0;
            hzb = new RenderTexture(w0 + w0 / 2, h0, 0, RenderTextureFormat.RFloat)
            { enableRandomWrite = true, filterMode = FilterMode.Point, name = "Meshlet_HZB" };
            hzb.Create();
            hzbMips = Mathf.Min(16, Mathf.Min(Mathf.RoundToInt(Mathf.Log(w0, 2)), Mathf.RoundToInt(Mathf.Log(h0, 2))) + 1);
            hzbRects[0] = new Vector4(0, 0, w0, h0);
            for (int k = 1; k < hzbMips; k++)
                hzbRects[k] = new Vector4(w0, h0 - (h0 >> (k - 1)), Mathf.Max(1, w0 >> k), Mathf.Max(1, h0 >> k));
            hzbValid = false;
        }

        void SetHzbParams(CommandBuffer cmd, Matrix4x4 vp, Vector4 camPos, Vector4 camFwd, int kernel)
        {
            var cs = cullShader;
            var info = new Vector4(hzbW0, hzbH0, hzbMips, 0);
            if (cmd == null)
            {
                cs.SetTexture(kernel, "_Hzb", hzb);
                cs.SetVector("_HzbInfo", info);
                cs.SetVectorArray("_HzbMips", hzbRects);
                cs.SetMatrix("_HzbVP", vp);
                cs.SetVector("_HzbCamPos", camPos);
                cs.SetVector("_HzbCamFwd", camFwd);
                return;
            }
            cmd.SetComputeTextureParam(cs, kernel, "_Hzb", hzb);
            cmd.SetComputeVectorParam(cs, "_HzbInfo", info);
            cmd.SetComputeVectorArrayParam(cs, "_HzbMips", hzbRects);
            cmd.SetComputeMatrixParam(cs, "_HzbVP", vp);
            cmd.SetComputeVectorParam(cs, "_HzbCamPos", camPos);
            cmd.SetComputeVectorParam(cs, "_HzbCamFwd", camFwd);
        }

        void BuildHzb(CommandBuffer cmd, RenderTargetIdentifier depth, int w, int h, Vector4 zParams)
        {
            var cs = cullShader;
            cmd.SetComputeTextureParam(cs, kHzb0, "_DepthTex", depth);
            cmd.SetComputeTextureParam(cs, kHzb0, "_HzbRW", hzb);
            cmd.SetComputeTextureParam(cs, kHzbReduce, "_HzbRW", hzb);
            cmd.SetComputeVectorParam(cs, "_DepthInfo", new Vector4(w, h, 0, 0));
            cmd.SetComputeVectorParam(cs, "_ZParams", zParams);
            cmd.SetComputeVectorParam(cs, "_HzbDst", hzbRects[0]);
            cmd.DispatchCompute(cs, kHzb0, (hzbW0 + 7) / 8, (hzbH0 + 7) / 8, 1);
            for (int k = 1; k < hzbMips; k++)
            {
                cmd.SetComputeVectorParam(cs, "_HzbSrc", hzbRects[k - 1]);
                cmd.SetComputeVectorParam(cs, "_HzbDst", hzbRects[k]);
                cmd.DispatchCompute(cs, kHzbReduce, ((int)hzbRects[k].z + 7) / 8, ((int)hzbRects[k].w + 7) / 8, 1);
            }
        }

        /// <summary>深度を先に描いた直後。Hi-Z を作り、1 回目で隠れていた塊を調べ直して深度へ描き足し、次のコマ用に Hi-Z を作り直す。</summary>
        public void PostPass(CommandBuffer cmd, RenderTargetIdentifier depth, int w, int h, Camera cam)
        {
            if (!WantsPost) { hzbValid = false; return; }
            postFrame = Time.frameCount;
            if (usedSwThisFrame) SoftwareRasterize(cmd, depth, w, h, cam);
            if (!occlusionCull) { hzbValid = false; return; }
            EnsureHzb(w, h);
            float n = cam.nearClipPlane, f = cam.farClipPlane;
            bool rev = SystemInfo.usesReversedZBuffer;
            float zx = rev ? -1f + f / n : 1f - f / n, zy = rev ? 1f : f / n;
            var zParams = new Vector4(zx, zy, zx / f, zy / f);
            var vp = cam.projectionMatrix * cam.worldToCameraMatrix; // GL の向き(深度の行 0 = 画面の下と同じ)
            var camPos = (Vector4)cam.transform.position; camPos.w = n;
            var camFwd = (Vector4)cam.transform.forward;

            cmd.BeginSample("VRL.Meshlet.HiZ");
            BuildHzb(cmd, depth, w, h, zParams);
            if (usedHzbThisFrame)
            {
                SetHzbParams(cmd, vp, camPos, camFwd, kPost);
                cmd.DispatchCompute(cullShader, kPostArgs, 1, 1, 1);
                cmd.DispatchCompute(cullShader, kPost, dispatchBuf, 0);
                cmd.SetRenderTarget(depth);
                cmd.DrawProceduralIndirect(Matrix4x4.identity, meshletMaterial, passDepth, MeshTopology.Triangles, args2Buf, 0, mpb2);
                BuildHzb(cmd, depth, w, h, zParams); // 2 回目の塊も入れて、次のコマの 1 回目に使う
            }
            cmd.EndSample("VRL.Meshlet.HiZ");
            hzbVP = vp; hzbCamPos = camPos; hzbCamFwd = camFwd;
            hzbValid = true;
            hzbFrame = Time.frameCount;
        }

        /// <summary>不透明の後。2 回目で見えた塊と、ソフトウェアラスタで塗った画素を色付きで描く。</summary>
        public void DrawPostColor(RasterCommandBuffer cmd)
        {
            if (!ready || compareMode) return;
            if (usedHzbThisFrame) cmd.DrawProceduralIndirect(Matrix4x4.identity, meshletMaterial, passForward, MeshTopology.Triangles, args2Buf, 0, mpb2);
            if (usedSwThisFrame) cmd.DrawProcedural(Matrix4x4.identity, meshletMaterial, passSwResolve, MeshTopology.Triangles, 3, 1, mpbSw);
        }

        // ---------------- ソフトウェアラスタ ----------------

        void EnsureSwBuffers(int w, int h)
        {
            if (swDepthBuf != null && w == swW && h == swH) return;
            swDepthBuf?.Release(); swPayloadBuf?.Release();
            swW = w; swH = h;
            swDepthBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, w * h, 4);
            swPayloadBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, w * h, 4);
            foreach (int k in new[] { kSwClear, kSwDepth, kSwPayload })
            {
                cullShader.SetBuffer(k, "_SwDepth", swDepthBuf);
                cullShader.SetBuffer(k, "_SwPayload", swPayloadBuf);
            }
            mpbSw.SetBuffer("_SwDepth", swDepthBuf);
            mpbSw.SetBuffer("_SwPayload", swPayloadBuf);
        }

        /// <summary>小さい塊の三角形を compute で塗り(深度 → 一致した画素に番号)、深度テクスチャへ書き足す。色は DrawPostColor。</summary>
        void SoftwareRasterize(CommandBuffer cmd, RenderTargetIdentifier depth, int w, int h, Camera cam)
        {
            EnsureSwBuffers(w, h);
            var cs = cullShader;
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            var screen = new Vector4(w, h, SystemInfo.graphicsUVStartsAtTop ? 1 : 0, SystemInfo.usesReversedZBuffer ? 1 : 0);
            cmd.SetComputeMatrixParam(cs, "_SwVP", vp);
            cmd.SetComputeVectorParam(cs, "_SwScreen", screen);
            cmd.SetComputeFloatParam(cs, "_SwCullSign", swCullSign);
            cmd.SetGlobalMatrix("_SwVP", vp);
            cmd.SetGlobalVector("_SwScreen", screen);
            cmd.BeginSample("VRL.Meshlet.SwRaster");
            cmd.DispatchCompute(cs, kSwClear, (w * h + 63) / 64, 1, 1);
            cmd.DispatchCompute(cs, kSwArgs, 1, 1, 1);
            cmd.DispatchCompute(cs, kSwDepth, dispatchBuf, 36);
            cmd.DispatchCompute(cs, kSwPayload, dispatchBuf, 36);
            cmd.SetRenderTarget(depth);
            cmd.DrawProcedural(Matrix4x4.identity, meshletMaterial, passSwDepth, MeshTopology.Triangles, 3, 1, mpbSw);
            cmd.EndSample("VRL.Meshlet.SwRaster");
        }

        [Tooltip("ソフトウェアラスタの表裏の向き(画面の上下反転で変わる)。裏が見えたら -1 に")]
        public float swCullSign = -1f;

        // ---------------- VSM(VsmManager から) ----------------

        public long VsmShadowTris => castVsmShadows ? statShadowTris : 0;

        public void VsmCull(CommandBuffer cmd, VSM.VsmManager vsm)
        {
            if (!ready || !castVsmShadows) return;
            var cs = cullShader;
            cmd.SetBufferData(shadowArgsBuf, ArgsInit);
            cmd.SetBufferData(statsBuf, ShadowStatsZero, 0, 6, 3);
            vsm.BindPages(cmd, cs, kShPages);
            vsm.BindPages(cmd, cs, kShClusters);
            cmd.SetComputeFloatParam(cs, "_ShadowErrTexels", shadowErrorTexels);
            int maxPhys = vsm.physPagesPerAxis * vsm.physPagesPerAxis;
            cmd.DispatchCompute(cs, kShPages, (maxPhys + 63) / 64, instanceCount, 1);
            cmd.DispatchCompute(cs, kShArgs, 1, 1, 1);
            cmd.DispatchCompute(cs, kShClusters, dispatchBuf, 12);
            cmd.DispatchCompute(cs, kShFinish, 1, 1, 1);
        }

        public void VsmDraw(CommandBuffer cmd, VSM.VsmManager vsm)
        {
            if (!ready || !castVsmShadows) return;
            cmd.DrawProceduralIndirect(Matrix4x4.identity, meshletMaterial, passVsm, MeshTopology.Triangles, shadowArgsBuf, 0, mpbShadow);
        }

        static readonly string[] ModeNames = { "仕上がり", "塊(Cluster)", "三角形", "DAG の段", "物", "Hi-Z の 2 回目で描いた塊", "ソフトウェアラスタで塗った塊" };

        void Hud(StringBuilder sb)
        {
            sb.Append("<b>Meshlet</b>  ");
            sb.Append(ready ? buildLog : "作成中…").Append('\n');
            if (!ready) return;
            if (compareMode)
            {
                sb.AppendFormat("<color=#ff8>比べ: 普通の描画(元の形を {0} 個そのまま = {1:N0} 三角形)</color>\n", instanceCount, (long)levelTris[0] * instanceCount);
            }
            else
            {
                sb.AppendFormat("描いた塊 {0:N0}(うち Hi-Z の 2 回目 {1:N0}・ソフトウェアラスタ {2:N0}) / 三角形 {3:N0}(うちソフト {4:N0})  (全部元の形なら {5:N0})\n",
                    statVisible + statVisible2 + statSw, statVisible2, statSw, statTris + statTris2 + statSwTris, statSwTris, (long)levelTris[0] * instanceCount);
                sb.AppendFormat("調べた塊 {0:N0}((物, 段) {1:N0} 組。全部調べると {2:N0})\n", statTested, statWork, (long)totalClusters * instanceCount);
                sb.AppendFormat("外した塊: 視錐台 {0:N0} / 裏向き {1:N0} / <b>Hi-Z で隠れていた {2:N0}</b>{3}\n", statFrustum, statCone,
                    statOccluded >= statVisible2 ? statOccluded - statVisible2 : 0, occlusionCull ? "" : " <color=#f88>(Hi-Z 切)</color>");
                if (castVsmShadows && VSM.VsmManager.Active != null && VSM.VsmManager.Active.vsmEnabled)
                    sb.AppendFormat("VSM の影: (岩, ページ) {0:N0} 組 → 塊 {1:N0} / 三角形 {2:N0}{3}\n", statShadowWork, statShadowClusters, statShadowTris,
                        statShadowFailed > 0 ? $" <color=#f88>入りきらない {statShadowFailed}</color>" : "");
            }
            if (!handleKeys) return;
            sb.AppendFormat("[1-7] 表示: {0}   [K] 段: {1}   [ ] 誤差 {2} px   [O] Hi-Z: {3}   [R] ソフトウェアラスタ: {4}\n", ModeNames[debugMode],
                forceLevel < 0 ? "自動(DAG)" : forceLevel + " に固定", errorPx, occlusionCull ? "入" : "切", softwareRaster ? "入" : "切");
            sb.AppendFormat("[C] 裏向きで外す: {0}  [F] 外す判定を止める: {1}  [X] 影: {2}  [M] 普通の描画と比べる: {3}",
                coneCull ? "入" : "切", frozen ? "<color=#f88>止め中</color>" : "切", castShadows ? "入" : "切", compareMode ? "入" : "切");
        }

        void OnDestroy()
        {
            var vsm = VSM.VsmManager.Active;
            if (vsm != null) vsm.UnregisterClusterCaster(this);
            foreach (var b in new[] { vtxBuf, idxBuf, meshletBuf, instBuf, levelBuf, visBuf, vis2Buf, occBuf, argsBuf, args2Buf, dispatchBuf, statsBuf, shadowWorkBuf, shadowDrawBuf, shadowArgsBuf, workBuf, swListBuf, swDepthBuf, swPayloadBuf, swDrawArgsBuf })
                b?.Release();
            if (hzb != null) hzb.Release();
            if (compareMesh != null) Destroy(compareMesh);
            if (swShadowMat != null) Destroy(swShadowMat);
        }
    }
}
