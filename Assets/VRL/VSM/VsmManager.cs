using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRL.VSM
{
    /// <summary>
    /// 塊単位で影を描く物(Meshlet の岩など)。VsmManager が、描き直すページが決まった後に VsmCull(compute で並べる)、
    /// アトラスへ描く所で VsmDraw を呼ぶ。描き直すマスの一覧は BindPages で compute に渡せる。
    /// </summary>
    public interface IVsmClusterCaster
    {
        void VsmCull(CommandBuffer cmd, VsmManager vsm);
        void VsmDraw(CommandBuffer cmd, VsmManager vsm);
        long VsmShadowTris { get; }
    }

    // VSM(Virtual Shadow Maps)の試作。太陽(平行光)だけ。
    // ページは世界に固定した番号で持ち、表は 64 で折り返して使う(クリップマップ)。前のコマのページは中身ごと持ち越し、
    // 描き直すのは「新しく要るページ」と「動いた物に掛かるページ」だけ。光の向きや奥行きの基準が変わった時だけ全部捨てる。
    // VsmFeature(URP の描画の途中、深度を描いた直後)から、メインカメラについて Execute が呼ばれる。
    public class VsmManager : MonoBehaviour
    {
        public static VsmManager Active { get; private set; }

        [Tooltip("影を落とす太陽")] public Light sun;
        public ComputeShader pagesShader;
        public Shader clearShader;

        [Header("仮想の影")]
        [Tooltip("レベル(クリップマップ)の数。レベル l の幅は W0 x 2^l")]
        [Range(4, 16)] public int levels = 10;
        [Tooltip("一番細かいレベルの幅(m)。64 ページ x 128px = 8192px なので 8m なら 1 画素 約 1mm")]
        public float level0Width = 8f;
        [Tooltip("光の向きの奥行き(m、カメラの前後にこの半分ずつ)")]
        public float depthRange = 600f;
        [Tooltip("奥行きの基準をそろえる刻み(m)。これをまたぐと全部描き直し")]
        public float depthSnap = 50f;
        [Tooltip("レベルの選び方のずらし。+1 で 1 段粗く(軽く・ぼける)")]
        public float lodBias = 0f;
        [Tooltip("要らなくなったページを何コマ残すか(戻った時に描き直さずに済む)")]
        public int keepFrames = 120;

        [Header("物理の置き場")]
        [Tooltip("物理アトラスのマス数(1 辺)。32 なら 4096px 四方 = 1024 ページ")]
        public int physPagesPerAxis = 32;

        [Header("切り替え")]
        public bool vsmEnabled = true;
        [Tooltip("持ち越しを切ると毎コマ全部描き直す(比べ用)")]
        public bool caching = true;
        [Tooltip("0=仕上がり 1=ページ 2=レベル 3=このコマ描き直したページ")] public int debugMode;
        [Tooltip("切ると自分ではキーを読まない(合体シーンでまとめて切り替える時)")]
        public bool handleKeys = true;

        const int Pages = 64, PagePx = 128, MaxDirty = 256;

        class Caster
        {
            public Renderer renderer;      // 普通のメッシュ
            public Mesh mesh;
            public Material mat;
            public int pass;
            public MaterialPropertyBlock mpb;
            public bool procedural;        // 頂点を自分で作る物(Meshlet の岩など)
            public uint vertexCount;
            public Vector4 sphere;
            public Matrix4x4 lastMatrix;   // renderer が無い物は、これがそのまま位置(動かない)
        }

        readonly List<Caster> casters = new List<Caster>();
        readonly List<IVsmClusterCaster> clusterCasters = new List<IVsmClusterCaster>();
        Matrix4x4 curLightView;
        Vector4 curZ, curPhys;
        bool collected;
        int builtCasterCount = -1;
        GraphicsBuffer requests, pageTable, slotInfo, freeStack, allocList, renderList;
        GraphicsBuffer counter, clearArgs, casterArgs, casterPages, casterSpheres, dirtySpheres;
        Vector4[] sphereData;
        readonly List<Vector4> dirty = new List<Vector4>();
        RenderTexture atlas, viz;
        Material clearMat;
        int kInit, kClear, kMark, kUpdate, kAlloc, kFinish, kInvalidate, kCull, kClearArgs, kViz;
        int totalEntries, maxPhys;
        readonly Vector4[] origins = new Vector4[16];
        uint frame;
        bool needInit = true;
        Quaternion lastSunRot;
        float lastZNear = float.NaN;
        uint statUsed, statRequested, statRender, statKept, statFailed;
        bool readbackPending;
        string lastInitReason = "";
        // 1 コマで影に描いた三角形の数(GPU の仕事量の目安)。エディタでは処理ごとの GPU 時間が取れない(Recorder/ProfilerRecorder とも 0)ので数で見る
        long statShadowTris;
        bool argsReadbackPending;

        static readonly int IdLightView = Shader.PropertyToID("_VsmLightView");
        static readonly int IdOrigins = Shader.PropertyToID("_VsmOrigins");
        static readonly int IdZ = Shader.PropertyToID("_VsmZ");
        static readonly int IdCam = Shader.PropertyToID("_VsmCam");
        static readonly int IdPhys = Shader.PropertyToID("_VsmPhys");
        static readonly int IdFrame = Shader.PropertyToID("_VsmFrame");
        static readonly int IdPageTable = Shader.PropertyToID("_VsmPageTable");
        static readonly int IdSlotInfo = Shader.PropertyToID("_VsmSlotInfo");
        static readonly int IdRenderList = Shader.PropertyToID("_VsmRenderList");
        static readonly int IdCasterPages = Shader.PropertyToID("_VsmCasterPages");
        static readonly int IdAtlas = Shader.PropertyToID("_VsmAtlas");
        static readonly int IdEnabled = Shader.PropertyToID("_VsmEnabled");
        static readonly int IdObjToWorld = Shader.PropertyToID("_VsmObjectToWorld");
        static readonly int IdCasterIndex = Shader.PropertyToID("_VsmCasterIndex");
        static GraphicsBuffer dummy, dummy8, dummy16;

        /// <summary>VsmManager が無い時(編集中・1 コマ目)でも、VSM を読むシェーダーが「バッファが無い」で描画を飛ばされないよう、空の物を渡す。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void BindDummies()
        {
            if (dummy == null || !dummy.IsValid()) dummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
            if (dummy8 == null || !dummy8.IsValid()) dummy8 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 8);
            if (dummy16 == null || !dummy16.IsValid()) dummy16 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            if (Active != null) return;
            Shader.SetGlobalBuffer(IdPageTable, dummy8);
            Shader.SetGlobalBuffer(IdSlotInfo, dummy16);
            Shader.SetGlobalBuffer(IdRenderList, dummy);
            Shader.SetGlobalBuffer(IdCasterPages, dummy);
            Shader.SetGlobalFloat(IdEnabled, 0);
        }

        void OnEnable()
        {
            Active = this;
            VrlHud.Lines += Hud;
            VrlHud.Draw += HudDraw;
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            VrlHud.Lines -= Hud;
            VrlHud.Draw -= HudDraw;
            BindDummies();
        }

        /// <summary>
        /// 頂点を自分で作る影の落とし手を足す(動かない物)。描く時は DrawProceduralIndirect(頂点 vertexCount、インスタンス = 掛かるページ)。
        /// シェーダーの VsmCaster パスは VsmSample.hlsl の VsmCasterVertex を使うこと。
        /// </summary>
        public void RegisterProcedural(Material mat, MaterialPropertyBlock mpb, uint vertexCount, Vector4 worldSphere)
        {
            int pass = mat.FindPass("VsmCaster");
            if (pass < 0) { Debug.LogError("[VRL VSM] VsmCaster パスが無い: " + mat.shader.name); return; }
            casters.Add(new Caster { procedural = true, mat = mat, pass = pass, mpb = mpb, vertexCount = vertexCount, sphere = worldSphere });
        }

        public void RegisterClusterCaster(IVsmClusterCaster c) { if (!clusterCasters.Contains(c)) { clusterCasters.Add(c); RequestInit("影を落とす物が変わった"); } }
        public void UnregisterClusterCaster(IVsmClusterCaster c) { clusterCasters.Remove(c); }

        /// <summary>
        /// compute に、このコマのページの決まり(_VsmLightView/_VsmOrigins/_VsmZ/_VsmPhys)と、
        /// 描き直すマスの一覧 _VsmRenderList・数(_VsmCounter の +12)・マスの情報 _VsmSlotInfo を渡す。
        /// </summary>
        public void BindPages(CommandBuffer cmd, ComputeShader cs, int kernel)
        {
            cmd.SetComputeMatrixParam(cs, IdLightView, curLightView);
            cmd.SetComputeVectorArrayParam(cs, IdOrigins, origins);
            cmd.SetComputeVectorParam(cs, IdZ, curZ);
            cmd.SetComputeVectorParam(cs, IdPhys, curPhys);
            cmd.SetComputeBufferParam(cs, kernel, IdSlotInfo, slotInfo);
            cmd.SetComputeBufferParam(cs, kernel, IdRenderList, renderList);
            cmd.SetComputeBufferParam(cs, kernel, "_VsmCounter", counter);
        }

        /// <summary>シーンの物ではないメッシュを、動かない影の落とし手として足す(地形を切り分けた塊など)。材質は VsmCaster パスを持つこと。</summary>
        public void RegisterMesh(Mesh mesh, Matrix4x4 localToWorld, Material mat)
        {
            int pass = mat.FindPass("VsmCaster");
            if (pass < 0) { Debug.LogError("[VRL VSM] VsmCaster パスが無い: " + mat.shader.name); return; }
            var b = mesh.bounds;
            var c = localToWorld.MultiplyPoint3x4(b.center);
            float r = localToWorld.MultiplyVector(b.extents).magnitude;
            casters.Add(new Caster { mesh = mesh, mat = mat, pass = pass, mpb = new MaterialPropertyBlock(), lastMatrix = localToWorld, sphere = new Vector4(c.x, c.y, c.z, r) });
        }

        void Start()
        {
            totalEntries = levels * Pages * Pages;
            maxPhys = physPagesPerAxis * physPagesPerAxis;

            requests = Buf(totalEntries);
            pageTable = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalEntries, 8);
            slotInfo = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxPhys, 16);
            freeStack = Buf(maxPhys);
            allocList = Buf(totalEntries);
            renderList = Buf(maxPhys);
            counter = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 8, 4);
            clearArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 4, 4);
            dirtySpheres = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxDirty, 16);

            int px = physPagesPerAxis * PagePx;
            atlas = new RenderTexture(px, px, 32, RenderTextureFormat.Depth) { name = "VSM_PhysicalAtlas", filterMode = FilterMode.Point };
            atlas.Create();
            viz = new RenderTexture(Pages * 5, Pages * 2, 0, RenderTextureFormat.ARGB32) { enableRandomWrite = true, filterMode = FilterMode.Point, name = "VSM_PageTableViz" };
            viz.Create();
            clearMat = new Material(clearShader);

            var cs = pagesShader;
            kInit = cs.FindKernel("InitAll");
            kClear = cs.FindKernel("ClearRequests");
            kMark = cs.FindKernel("MarkFromDepth");
            kUpdate = cs.FindKernel("UpdatePages");
            kAlloc = cs.FindKernel("Allocate");
            kFinish = cs.FindKernel("FinishAlloc");
            kInvalidate = cs.FindKernel("InvalidateDynamic");
            kCull = cs.FindKernel("CullCasterPages");
            kClearArgs = cs.FindKernel("WriteClearArgs");
            kViz = cs.FindKernel("DrawViz");

            // 全部のカーネルに同じ名前で縛る(使わない物は無視される)
            foreach (int k in new[] { kInit, kClear, kMark, kUpdate, kAlloc, kFinish, kInvalidate, kCull, kClearArgs, kViz })
            {
                cs.SetBuffer(k, "_Requests", requests);
                cs.SetBuffer(k, "_PageTableRW", pageTable);
                cs.SetBuffer(k, "_SlotInfoRW", slotInfo);
                cs.SetBuffer(k, "_FreeStack", freeStack);
                cs.SetBuffer(k, "_AllocList", allocList);
                cs.SetBuffer(k, "_RenderList", renderList);
                cs.SetBuffer(k, "_Counter", counter);
                cs.SetBuffer(k, "_ClearArgs", clearArgs);
                cs.SetBuffer(k, "_DirtySpheres", dirtySpheres);
            }
            cs.SetTexture(kViz, "_Viz", viz);

            Shader.SetGlobalBuffer(IdPageTable, pageTable);
            Shader.SetGlobalBuffer(IdSlotInfo, slotInfo);
            Shader.SetGlobalBuffer(IdRenderList, renderList);
            Shader.SetGlobalTexture(IdAtlas, atlas);
            ApplyMode();
        }

        static GraphicsBuffer Buf(int n) => new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, n), 4);

        // 影を落とす物を集める(最初の描画の時=全部の Start の後)。普通のメッシュは VsmCaster パスを持つ材質の物すべて。
        void CollectMeshCasters()
        {
            collected = true;
            foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || r.sharedMaterial == null) continue;
                if (r.GetComponent<VsmExclude>() != null) continue;
                int pass = r.sharedMaterial.FindPass("VsmCaster");
                if (pass < 0) continue;
                casters.Add(new Caster { renderer = r, mesh = mf.sharedMesh, mat = r.sharedMaterial, pass = pass, mpb = new MaterialPropertyBlock(), lastMatrix = r.localToWorldMatrix });
            }
        }

        void BuildCasterBuffers()
        {
            builtCasterCount = casters.Count;
            casterArgs?.Release(); casterPages?.Release(); casterSpheres?.Release();
            int cc = Mathf.Max(1, casters.Count);
            casterArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, cc * 5, 4);
            casterPages = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cc * maxPhys, 4);
            casterSpheres = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cc, 16);
            sphereData = new Vector4[cc];
            var args = new uint[cc * 5];
            for (int i = 0; i < casters.Count; i++)
            {
                var c = casters[i];
                if (c.procedural) { args[i * 5] = c.vertexCount; continue; }
                args[i * 5 + 0] = c.mesh.GetIndexCount(0);
                args[i * 5 + 2] = c.mesh.GetIndexStart(0);
                args[i * 5 + 3] = c.mesh.GetBaseVertex(0);
            }
            casterArgs.SetData(args);
            foreach (int k in new[] { kClear, kCull })
            {
                pagesShader.SetBuffer(k, "_CasterArgs", casterArgs);
                pagesShader.SetBuffer(k, "_CasterPagesRW", casterPages);
                pagesShader.SetBuffer(k, "_CasterSpheres", casterSpheres);
            }
            Shader.SetGlobalBuffer(IdCasterPages, casterPages);
            RequestInit("影を落とす物が変わった");
        }

        void RequestInit(string why) { needInit = true; lastInitReason = why; }

        void ApplyMode()
        {
            // VSM の時は URP の影を切る(影の描き直しを二重にしない)
            if (sun != null) sun.shadows = vsmEnabled ? LightShadows.None : LightShadows.Soft;
            Shader.SetGlobalFloat(IdEnabled, vsmEnabled ? 1 : 0);
        }

        public void SetEnabled(bool on) { vsmEnabled = on; ApplyMode(); }

        void Update()
        {
            if (!handleKeys) return;
            if (Input.GetKeyDown(KeyCode.V)) SetEnabled(!vsmEnabled);
            if (Input.GetKeyDown(KeyCode.Alpha1)) debugMode = 0;
            if (Input.GetKeyDown(KeyCode.Alpha2)) debugMode = 1;
            if (Input.GetKeyDown(KeyCode.Alpha3)) debugMode = 2;
            if (Input.GetKeyDown(KeyCode.Alpha4)) debugMode = 3;
            if (Input.GetKeyDown(KeyCode.N)) caching = !caching;
            if (Input.GetKeyDown(KeyCode.LeftBracket)) { lodBias -= 0.5f; RequestInit("レベルのずらし"); }
            if (Input.GetKeyDown(KeyCode.RightBracket)) { lodBias += 0.5f; RequestInit("レベルのずらし"); }
        }

        /// <summary>VsmFeature から、メインカメラについて深度を描いた直後に呼ばれる。</summary>
        public void Execute(CommandBuffer cmd, Camera cam, RenderTargetIdentifier depth, int width, int height)
        {
            if (atlas == null || sun == null) return;
            if (!collected) CollectMeshCasters();
            if (builtCasterCount != casters.Count) BuildCasterBuffers();
            var cs = pagesShader;
            frame++;

            // ---- 決まりの値 ----
            var sunRot = sun.transform.rotation;
            if (sunRot != lastSunRot) { lastSunRot = sunRot; RequestInit("光の向き"); }
            var lightView = Matrix4x4.Rotate(sunRot).inverse;
            Vector3 camPos = cam.transform.position;
            Vector3 camLs = lightView.MultiplyPoint3x4(camPos);
            for (int l = 0; l < levels; l++)
            {
                float w = level0Width * Mathf.Pow(2f, l);
                float pageW = w / Pages;
                origins[l] = new Vector4(Mathf.Round(camLs.x / pageW), Mathf.Round(camLs.y / pageW), w, pageW);
            }
            float zNear = Mathf.Floor((camLs.z - depthRange * 0.5f) / depthSnap) * depthSnap;
            if (zNear != lastZNear) { lastZNear = zNear; RequestInit("奥行きの基準"); }
            var z = new Vector4(zNear, 1f / (depthRange + depthSnap), level0Width / (Pages * PagePx), levels);
            var camV = new Vector4(camPos.x, camPos.y, camPos.z, 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / height);
            var phys = new Vector4(physPagesPerAxis, physPagesPerAxis * PagePx, lodBias, debugMode);
            curLightView = lightView; curZ = z; curPhys = phys;

            cmd.SetGlobalMatrix(IdLightView, lightView);
            cmd.SetGlobalVectorArray(IdOrigins, origins);
            cmd.SetGlobalVector(IdZ, z);
            cmd.SetGlobalVector(IdCam, camV);
            cmd.SetGlobalVector(IdPhys, phys);
            cmd.SetGlobalInt(IdFrame, (int)frame);
            cmd.SetComputeMatrixParam(cs, IdLightView, lightView);
            cmd.SetComputeVectorArrayParam(cs, IdOrigins, origins);
            cmd.SetComputeVectorParam(cs, IdZ, z);
            cmd.SetComputeVectorParam(cs, IdCam, camV);
            cmd.SetComputeVectorParam(cs, IdPhys, phys);

            var gpuVP = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            cmd.SetComputeMatrixParam(cs, "_InvViewProj", gpuVP.inverse);
            cmd.SetComputeVectorParam(cs, "_DepthSize", new Vector4(width, height, SystemInfo.graphicsUVStartsAtTop ? 1 : 0, 0));
            cmd.SetComputeIntParam(cs, "_TotalEntries", totalEntries);
            cmd.SetComputeIntParam(cs, "_MaxPhys", maxPhys);
            cmd.SetComputeIntParam(cs, "_CasterCount", casters.Count);
            cmd.SetComputeIntParam(cs, "_KeepFrames", keepFrames);
            cmd.SetComputeIntParam(cs, "_Frame", (int)frame);

            // 物の外接球と、動いた物(前と今の球を「描き直し」に)
            dirty.Clear();
            for (int i = 0; i < casters.Count; i++)
            {
                var c = casters[i];
                if (c.procedural || c.renderer == null) { sphereData[i] = c.sphere; continue; }
                var b = c.renderer.bounds;
                var sph = new Vector4(b.center.x, b.center.y, b.center.z, b.extents.magnitude);
                var m = c.renderer.localToWorldMatrix;
                if (m != c.lastMatrix && dirty.Count < MaxDirty - 1)
                {
                    dirty.Add(sphereData[i]);
                    dirty.Add(sph);
                    c.lastMatrix = m;
                }
                sphereData[i] = sph;
            }
            cmd.SetBufferData(casterSpheres, sphereData);
            if (dirty.Count > 0) cmd.SetBufferData(dirtySpheres, dirty);
            cmd.SetComputeIntParam(cs, "_DirtyCount", dirty.Count);
            if (!caching) RequestInit("持ち越し切り");

            // ---- 0 全部捨てる(最初・光の向き・奥行きの基準が変わった時) ----
            if (needInit)
            {
                needInit = false;
                cmd.DispatchCompute(cs, kInit, (Mathf.Max(totalEntries, maxPhys) + 63) / 64, 1, 1);
            }

            int groupsE = (totalEntries + 63) / 64;
            cmd.BeginSample("VRL.VSM.Pages");
            cmd.DispatchCompute(cs, kClear, (Mathf.Max(totalEntries, casters.Count) + 63) / 64, 1, 1);
            cmd.SetComputeTextureParam(cs, kMark, "_CameraDepth", depth);
            cmd.DispatchCompute(cs, kMark, (width + 7) / 8, (height + 7) / 8, 1);
            cmd.DispatchCompute(cs, kUpdate, groupsE, 1, 1);
            cmd.DispatchCompute(cs, kAlloc, groupsE, 1, 1);
            cmd.DispatchCompute(cs, kFinish, 1, 1, 1);
            cmd.DispatchCompute(cs, kInvalidate, (maxPhys + 63) / 64, 1, 1);
            if (casters.Count > 0) cmd.DispatchCompute(cs, kCull, (maxPhys + 63) / 64, casters.Count, 1);
            foreach (var cc in clusterCasters) cc.VsmCull(cmd, this);
            cmd.DispatchCompute(cs, kClearArgs, 1, 1, 1);
            cmd.DispatchCompute(cs, kViz, Pages * 5 / 8, Pages * 2 / 8, 1);
            cmd.EndSample("VRL.VSM.Pages");

            // ---- 描き直すマスだけ消して、掛かる物を描く ----
            cmd.BeginSample("VRL.VSM.Draw");
            cmd.SetRenderTarget(atlas);
            cmd.DrawProceduralIndirect(Matrix4x4.identity, clearMat, 0, MeshTopology.Triangles, clearArgs, 0);
            for (int i = 0; i < casters.Count; i++)
            {
                var c = casters[i];
                if (c.procedural)
                {
                    c.mpb.SetFloat(IdCasterIndex, i);
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, c.mat, c.pass, MeshTopology.Triangles, casterArgs, i * 20, c.mpb);
                    continue;
                }
                if (c.renderer != null && (!c.renderer.enabled || !c.renderer.gameObject.activeInHierarchy)) continue;
                c.mpb.SetMatrix(IdObjToWorld, c.renderer != null ? c.renderer.localToWorldMatrix : c.lastMatrix);
                c.mpb.SetFloat(IdCasterIndex, i);
                cmd.DrawMeshInstancedIndirect(c.mesh, 0, c.mat, c.pass, casterArgs, i * 20, c.mpb);
            }
            foreach (var cc in clusterCasters) cc.VsmDraw(cmd, this);
            cmd.EndSample("VRL.VSM.Draw");
            cmd.SetGlobalTexture(IdAtlas, atlas);

            if (!argsReadbackPending && casters.Count > 0)
            {
                argsReadbackPending = true;
                AsyncGPUReadback.Request(casterArgs, r =>
                {
                    argsReadbackPending = false;
                    if (r.hasError) return;
                    var d = r.GetData<uint>();
                    long tris = 0;
                    for (int i = 0; i < casters.Count && i * 5 + 1 < d.Length; i++) tris += (long)d[i * 5] / 3 * d[i * 5 + 1];
                    statShadowTris = tris;
                });
            }

            if (!readbackPending)
            {
                readbackPending = true;
                AsyncGPUReadback.Request(counter, r =>
                {
                    readbackPending = false;
                    if (r.hasError) return;
                    var d = r.GetData<uint>();
                    statRequested = d[1]; statUsed = (uint)maxPhys - d[2]; statRender = d[3]; statFailed = d[4]; statKept = d[5];
                });
            }
        }

        static readonly string[] ModeNames = { "仕上がり", "ページ(物理のマス)", "レベル", "このコマ描き直したページ" };

        void Hud(StringBuilder sb)
        {
            sb.Append("<b>VSM</b>  ");
            if (!vsmEnabled)
            {
                sb.Append("<color=#ff8>比べ: URP の普通の影(4 カスケード・200m まで)</color>\n");
            }
            else
            {
                sb.AppendFormat("ページ 使用 {0:N0} / {1:N0}  要る {2:N0}  <b>このコマ描き直し {3:N0}</b>  持ち越し {4:N0}{5}\n",
                    statUsed, maxPhys, statRequested, statRender, statKept,
                    statFailed > 0 ? $" <color=#f88>置けない {statFailed}</color>" : "");
                long clusterTris = 0;
                foreach (var cc in clusterCasters) clusterTris += cc.VsmShadowTris;
                sb.AppendFormat("このコマ影に描いた三角形 <b>{0:N0}</b>{1}\n", statShadowTris + clusterTris,
                    clusterCasters.Count > 0 ? $"(うち塊単位の岩 {clusterTris:N0})" : "");
                sb.AppendFormat("影を落とす物 {0}  仮想 {1} レベル x {2}px  一番細かい 1 画素 = {3:0.0} mm  全部描き直した理由: {4}\n",
                    casters.Count + clusterCasters.Count, levels, Pages * PagePx, level0Width / (Pages * PagePx) * 1000f, lastInitReason);
            }
            if (handleKeys)
                sb.AppendFormat("[V] 影: {0}  [N] 持ち越し: {1}  [1-4] 表示: {2}  [ ] レベルのずらし {3:+0.0;-0.0;0}",
                    vsmEnabled ? "VSM" : "URP", caching ? "入" : "<color=#f88>切(毎コマ全部)</color>", ModeNames[Mathf.Clamp(debugMode, 0, 3)], lodBias);
        }

        void HudDraw(Rect area)
        {
            if (!vsmEnabled || viz == null) return;
            var r = new Rect(area.x, area.y, Pages * 5 * 1.6f, Pages * 2 * 1.6f);
            GUI.DrawTexture(r, viz, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(r.x + 4, r.yMax + 2, 500, 20), "VSM ページ表(レベル 0〜9・白=このコマ描き直し)");
            var r2 = new Rect(r.xMax + 12, area.y, 205, 205);
            GUI.DrawTexture(r2, atlas, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(r2.x + 4, r2.yMax + 2, 300, 20), "VSM 物理アトラス(深度)");
        }

        void OnDestroy()
        {
            foreach (var b in new[] { requests, pageTable, slotInfo, freeStack, allocList, renderList, counter, clearArgs, casterArgs, casterPages, casterSpheres, dirtySpheres })
                b?.Release();
            if (atlas != null) atlas.Release();
            if (viz != null) viz.Release();
        }
    }
}
