using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace VRL.RVT
{
    // RVT(Runtime Virtual Texture)の試作。512m の地形 1 枚の色を、仮想 32768px 四方(1 画素 1.6cm)のテクスチャとして持つ。
    // 毎コマ: 地形を小さく描いて「欲しいページ」を読み戻す → 足りないページを物理アトラスへ焼く(古いものから追い出す)→ 間接テーブルを更新。
    // 地形のシェーダーは間接テーブルを引いて焼いた色を読むだけ。B キーで、毎画素その場で計算する描き方と比べられる。
    // まだ無いもの(次の段): 法線・粗さの層、mip 間の補間(トライリニア)、ページの焼きを GPU 側だけで回す、物の貼り付け(道や岩を描き込む仕組み)。
    public class RvtManager : MonoBehaviour
    {
        [Header("地形")]
        [Tooltip("1 辺(m)")] public float terrainSize = 512f;
        [Tooltip("メッシュのマス数(1 辺)")] public int meshCells = 512;
        public int seed = 5;
        public Material terrainMaterial;
        public Shader bakeShader;
        public Shader feedbackShader;

        [Header("木(影と見た目の目安)")]
        public Material treeTrunkMaterial;
        public Material treeLeafMaterial;
        public int treeCount = 350;
        [Tooltip("入れると木を GameObject で置く(VSM の影を落とす・受けるため。材質は VsmLit)。空なら上の材質でまとめて描く")]
        public Material treeObjectTrunk;
        public Material treeObjectLeaf;

        [Header("VSM との合体")]
        [Tooltip("シーンに VsmManager があれば、地形を 8x8 の塊に切って影を落とす物として登録する")]
        public bool castVsmShadows = true;

        [Header("物理アトラス")]
        [Tooltip("マス数(1 辺)。20 なら 2720px 四方 = 400 ページ")]
        public int tilesPerAxis = 20;
        [Tooltip("1 コマに焼くページの上限")]
        public int bakeBudget = 24;
        [Tooltip("フィードバックの縮小率(1/n)")]
        public int feedbackDiv = 4;
        [Tooltip("mip のずらし。+1 で 1 段粗く")]
        public float mipBias = 0f;

        [Header("切り替え")]
        public bool direct;
        [Tooltip("0=仕上がり 1=mip 2=マス 3=欲しい mip に届いているか")] public int debugMode;
        [Tooltip("切ると自分ではキーを読まない(合体シーンでまとめて切り替える時)")]
        public bool handleKeys = true;

        const int Pages = 256, Mips = 9, Tile = 128, Border = 4, TileFull = 136;

        // 地形
        Mesh terrainMesh;
        Texture2D heightTex;
        float[] heights; int heightRes;
        GraphicsBuffer segBuf, decalBuf;
        Mesh trunkMesh, leafMesh;
        Matrix4x4[] trunkM, leafM;

        // 仮想テクスチャ
        RenderTexture atlas, feedbackRT;
        Texture2D indirection;
        Color32[][] indData;
        Material bakeMat, feedbackMat;
        MaterialPropertyBlock bakeMpb;
        CommandBuffer cmd;
        readonly int[] mipOffset = new int[Mips + 1];
        int[] pageToTile, pageStamp;
        int[] tileToPage, tileLastUsed;
        readonly Stack<int> freeTiles = new Stack<int>();
        readonly List<int> requested = new List<int>(4096);
        readonly List<int> missing = new List<int>(1024);
        int serial = 1, tileCount;
        bool readbackPending, indirectionDirty;
        string buildLog = "";
        int statRequested, statMissing, statBaked, statResident, statBakedTotal;

        static readonly int IdTerrainRect = Shader.PropertyToID("_TerrainRect");
        static readonly int IdAtlasInfo = Shader.PropertyToID("_RvtAtlasInfo");

        void OnEnable() { VrlHud.Lines += Hud; VrlHud.Draw += HudDraw; }
        void OnDisable() { VrlHud.Lines -= Hud; VrlHud.Draw -= HudDraw; }

        // ---------- 地形づくり ----------

        float HeightFn(float x, float z)
        {
            var off = new Vector3(seed * 31.7f, 0, seed * 17.3f);
            float baseH = VrlNoise.Fbm(new Vector3(x / 170f, 0.5f, z / 170f) + off, 5) * 26f;
            float ridge = VrlNoise.Ridged(new Vector3(x / 240f, 3.3f, z / 240f) + off, 5) * 85f;
            float mask = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-40f, 200f, z));
            float h = 14f + baseH + ridge * mask - 10f * mask * 0.5f;
            float r2 = (x * x + (z + 60) * (z + 60)) / (130f * 130f);
            h = Mathf.Lerp(h, 9f + baseH * 0.25f, Mathf.Exp(-r2));
            return h;
        }

        /// <summary>地形の高さ(m)。EnsureTerrain の後に使う。</summary>
        public float SampleHeight(float x, float z)
        {
            float u = Mathf.Clamp01((x + terrainSize * 0.5f) / terrainSize) * (heightRes - 1);
            float v = Mathf.Clamp01((z + terrainSize * 0.5f) / terrainSize) * (heightRes - 1);
            int x0 = Mathf.Min((int)u, heightRes - 2), y0 = Mathf.Min((int)v, heightRes - 2);
            float fx = u - x0, fy = v - y0;
            float a = heights[y0 * heightRes + x0], b = heights[y0 * heightRes + x0 + 1];
            float c = heights[(y0 + 1) * heightRes + x0], d = heights[(y0 + 1) * heightRes + x0 + 1];
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        /// <summary>地形を作る(何度呼んでも 1 回だけ)。ほかの部品が Start で高さを使う時に先に呼ぶ。</summary>
        public void EnsureTerrain()
        {
            if (terrainMesh != null) return;
            var sw = Stopwatch.StartNew();
            BuildTerrain();
            terrainBuildMs = sw.ElapsedMilliseconds;
        }
        long terrainBuildMs;

        void BuildTerrain()
        {
            heightRes = 1025;
            heights = new float[heightRes * heightRes];
            float half = terrainSize * 0.5f;
            for (int y = 0; y < heightRes; y++)
                for (int x = 0; x < heightRes; x++)
                    heights[y * heightRes + x] = HeightFn(x / (float)(heightRes - 1) * terrainSize - half, y / (float)(heightRes - 1) * terrainSize - half);
            heightTex = new Texture2D(heightRes, heightRes, TextureFormat.RFloat, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "RVT_Height" };
            heightTex.SetPixelData(heights, 0);
            heightTex.Apply(false, true);

            int n = meshCells + 1;
            var v = new Vector3[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float wx = x / (float)meshCells * terrainSize - half, wz = y / (float)meshCells * terrainSize - half;
                    v[y * n + x] = new Vector3(wx, SampleHeight(wx, wz), wz);
                }
            var idx = new int[meshCells * meshCells * 6];
            int k = 0;
            for (int y = 0; y < meshCells; y++)
                for (int x = 0; x < meshCells; x++)
                {
                    int i0 = y * n + x;
                    idx[k++] = i0; idx[k++] = i0 + n; idx[k++] = i0 + 1;
                    idx[k++] = i0 + 1; idx[k++] = i0 + n; idx[k++] = i0 + n + 1;
                }
            terrainMesh = new Mesh { indexFormat = IndexFormat.UInt32, name = "RVT_Terrain" };
            terrainMesh.SetVertices(v);
            terrainMesh.SetTriangles(idx, 0);
            terrainMesh.RecalculateNormals();
            terrainMesh.RecalculateBounds();

            var go = new GameObject("Terrain");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = terrainMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = terrainMaterial;
            mr.shadowCastingMode = ShadowCastingMode.On;
            go.AddComponent<VSM.VsmExclude>(); // VSM へは下の塊で登録する(1 枚のままだと全ページに 52 万三角形を描く)

            var vsm = VSM.VsmManager.Active;
            if (castVsmShadows && vsm != null)
            {
                const int chunks = 8;
                int cells = meshCells / chunks;
                for (int cz = 0; cz < chunks; cz++)
                    for (int cx = 0; cx < chunks; cx++)
                    {
                        var cv = new Vector3[(cells + 1) * (cells + 1)];
                        for (int y = 0; y <= cells; y++)
                            for (int x = 0; x <= cells; x++)
                                cv[y * (cells + 1) + x] = v[(cz * cells + y) * n + cx * cells + x];
                        var ci = new int[cells * cells * 6];
                        int q = 0;
                        for (int y = 0; y < cells; y++)
                            for (int x = 0; x < cells; x++)
                            {
                                int i0 = y * (cells + 1) + x;
                                ci[q++] = i0; ci[q++] = i0 + cells + 1; ci[q++] = i0 + 1;
                                ci[q++] = i0 + 1; ci[q++] = i0 + cells + 1; ci[q++] = i0 + cells + 2;
                            }
                        var cm = new Mesh { name = $"RVT_TerrainChunk_{cx}_{cz}" };
                        cm.SetVertices(cv); cm.SetTriangles(ci, 0); cm.RecalculateBounds();
                        vsm.RegisterMesh(cm, transform.localToWorldMatrix, terrainMaterial);
                    }
            }

            // 道: 端から真ん中の方へ曲がりながら進む
            var rnd = new System.Random(seed * 101);
            var segs = new List<Vector4>();
            for (int r = 0; r < 4; r++)
            {
                float a = r * Mathf.PI * 0.5f + 0.4f;
                var p = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (half - 10f);
                float heading = a + Mathf.PI + ((float)rnd.NextDouble() - 0.5f) * 0.6f;
                for (int s = 0; s < 60; s++)
                {
                    heading += ((float)rnd.NextDouble() - 0.5f) * 0.5f;
                    var toCenter = -p.normalized;
                    var dir = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
                    dir = Vector2.Lerp(dir, toCenter, 0.08f).normalized;
                    heading = Mathf.Atan2(dir.y, dir.x);
                    var q = p + dir * 7f;
                    segs.Add(new Vector4(p.x, p.y, q.x, q.y));
                    p = q;
                }
            }
            segBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, segs.Count, 16);
            segBuf.SetData(segs);

            var decals = new List<Vector4>();
            for (int i = 0; i < 400; i++)
            {
                float a = (float)rnd.NextDouble() * Mathf.PI * 2f, d = Mathf.Sqrt((float)rnd.NextDouble()) * 230f;
                int type = rnd.Next(3);
                float rad = type == 0 ? Mathf.Lerp(3f, 11f, (float)rnd.NextDouble()) : type == 1 ? Mathf.Lerp(2f, 6f, (float)rnd.NextDouble()) : Mathf.Lerp(1.5f, 5f, (float)rnd.NextDouble());
                decals.Add(new Vector4(Mathf.Cos(a) * d, Mathf.Sin(a) * d - 40f, rad, type));
            }
            decalBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, decals.Count, 16);
            decalBuf.SetData(decals);

            Shader.SetGlobalVector(IdTerrainRect, new Vector4(-half, -half, terrainSize, 0));
            Shader.SetGlobalTexture("_HeightTex", heightTex);
            Shader.SetGlobalBuffer("_Segments", segBuf);
            Shader.SetGlobalBuffer("_Decals", decalBuf);
            Shader.SetGlobalInt("_SegmentCount", segs.Count);
            Shader.SetGlobalInt("_DecalCount", decals.Count);
            Shader.SetGlobalFloat("_RoadHalfWidth", 2.2f);

            // 木
            trunkMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            leafMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            var tl = new List<Matrix4x4>(); var ll = new List<Matrix4x4>();
            for (int i = 0; i < treeCount * 4 && tl.Count < treeCount; i++)
            {
                float x = ((float)rnd.NextDouble() - 0.5f) * terrainSize * 0.9f, z = ((float)rnd.NextDouble() - 0.5f) * terrainSize * 0.9f;
                float h0 = SampleHeight(x, z);
                float slope = Mathf.Abs(SampleHeight(x + 2, z) - SampleHeight(x - 2, z)) + Mathf.Abs(SampleHeight(x, z + 2) - SampleHeight(x, z - 2));
                if (h0 > 50f || h0 < 7f || slope > 2.2f) continue;
                float th = Mathf.Lerp(4f, 9f, (float)rnd.NextDouble()), cr = Mathf.Lerp(1.8f, 3.2f, (float)rnd.NextDouble());
                tl.Add(Matrix4x4.TRS(new Vector3(x, h0 + th * 0.5f, z), Quaternion.identity, new Vector3(0.35f, th * 0.5f, 0.35f)));
                ll.Add(Matrix4x4.TRS(new Vector3(x, h0 + th + cr * 0.3f, z), Quaternion.identity, new Vector3(cr * 2f, cr * 1.7f, cr * 2f)));
            }
            trunkM = tl.ToArray(); leafM = ll.ToArray();

            if (treeObjectTrunk != null && treeObjectLeaf != null)
            {
                var root = new GameObject("Trees").transform;
                root.SetParent(transform, false);
                for (int i = 0; i < trunkM.Length; i++)
                {
                    MakeTreePart(root, trunkMesh, treeObjectTrunk, trunkM[i]);
                    MakeTreePart(root, leafMesh, treeObjectLeaf, leafM[i]);
                }
                trunkM = new Matrix4x4[0]; leafM = new Matrix4x4[0];
            }
        }

        static void MakeTreePart(Transform parent, Mesh mesh, Material mat, Matrix4x4 m)
        {
            var go = new GameObject("Tree");
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(m.GetPosition(), m.rotation);
            go.transform.localScale = m.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // ---------- 仮想テクスチャの準備 ----------

        void Start()
        {
            EnsureTerrain();
            long tTerrain = terrainBuildMs;

            for (int m = 0, off = 0; m <= Mips; m++) { mipOffset[m] = off; if (m < Mips) off += (Pages >> m) * (Pages >> m); }
            int totalPages = mipOffset[Mips];
            pageToTile = new int[totalPages];
            pageStamp = new int[totalPages];
            for (int i = 0; i < totalPages; i++) pageToTile[i] = -1;

            tileCount = tilesPerAxis * tilesPerAxis;
            tileToPage = new int[tileCount];
            tileLastUsed = new int[tileCount];
            for (int t = tileCount - 1; t >= 0; t--) { tileToPage[t] = -1; freeTiles.Push(t); }

            int px = tilesPerAxis * TileFull;
            atlas = new RenderTexture(px, px, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "RVT_Atlas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            atlas.Create();
            indirection = new Texture2D(Pages, Pages, TextureFormat.RGBA32, Mips, true) { name = "RVT_Indirection", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            indData = new Color32[Mips][];
            for (int m = 0; m < Mips; m++) indData[m] = new Color32[(Pages >> m) * (Pages >> m)];

            bakeMat = new Material(bakeShader);
            feedbackMat = new Material(feedbackShader);
            bakeMpb = new MaterialPropertyBlock();
            cmd = new CommandBuffer { name = "RVT" };

            Shader.SetGlobalTexture("_RvtAtlas", atlas);
            Shader.SetGlobalTexture("_RvtIndirection", indirection);

            // 一番粗いページ(地形全体 1 枚)はずっと置いておく
            Bake(new List<int> { mipOffset[Mips - 1] }, pin: true);
            RebuildIndirection();
            buildLog = $"地形 {terrainSize}m / 仮想 {Pages * Tile:N0}px 四方(1 画素 {terrainSize / (Pages * Tile) * 100f:0.0} cm)/ 物理 {px}px = {tileCount} ページ  準備 {tTerrain} ms";
            Debug.Log("[VRL RVT] " + buildLog);
        }

        // ---------- 毎コマ ----------

        void Update()
        {
            if (handleKeys) Keys();
            ProcessRequests();
            if (indirectionDirty) RebuildIndirection();
        }

        void Keys()
        {
            if (Input.GetKeyDown(KeyCode.B)) direct = !direct;
            if (Input.GetKeyDown(KeyCode.Alpha1)) debugMode = 0;
            if (Input.GetKeyDown(KeyCode.Alpha2)) debugMode = 1;
            if (Input.GetKeyDown(KeyCode.Alpha3)) debugMode = 2;
            if (Input.GetKeyDown(KeyCode.Alpha4)) debugMode = 3;
            if (Input.GetKeyDown(KeyCode.LeftBracket)) mipBias -= 0.5f;
            if (Input.GetKeyDown(KeyCode.RightBracket)) mipBias += 0.5f;
            if (Input.GetKeyDown(KeyCode.R)) FlushCache();
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null || atlas == null) return;
            Shader.SetGlobalVector(IdAtlasInfo, new Vector4(atlas.width, mipBias, direct ? 1 : 0, debugMode));

            var rp = new RenderParams(treeTrunkMaterial) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            if (trunkM.Length > 0) Graphics.RenderMeshInstanced(rp, trunkMesh, 0, trunkM);
            rp.material = treeLeafMaterial;
            if (leafM.Length > 0) Graphics.RenderMeshInstanced(rp, leafMesh, 0, leafM);

            if (direct) return;
            RenderFeedback(cam);
        }

        void RenderFeedback(Camera cam)
        {
            int w = Mathf.Max(16, cam.pixelWidth / feedbackDiv), h = Mathf.Max(16, cam.pixelHeight / feedbackDiv);
            if (feedbackRT == null || feedbackRT.width != w || feedbackRT.height != h)
            {
                if (feedbackRT != null) feedbackRT.Release();
                feedbackRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "RVT_Feedback", filterMode = FilterMode.Point };
                feedbackRT.Create();
            }
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            feedbackMat.SetMatrix("_FbViewProj", vp);
            feedbackMat.SetMatrix("_FbObjectToWorld", transform.localToWorldMatrix);
            feedbackMat.SetFloat("_FbMipOffset", Mathf.Log(feedbackDiv, 2f) - mipBias);

            cmd.Clear();
            cmd.SetRenderTarget(feedbackRT);
            cmd.ClearRenderTarget(true, true, Color.clear, 1f);
            cmd.DrawMesh(terrainMesh, transform.localToWorldMatrix, feedbackMat, 0, 0);
            Graphics.ExecuteCommandBuffer(cmd);

            if (readbackPending) return;
            readbackPending = true;
            AsyncGPUReadback.Request(feedbackRT, 0, TextureFormat.RGBA32, OnFeedback);
        }

        void OnFeedback(AsyncGPUReadbackRequest r)
        {
            readbackPending = false;
            if (r.hasError || pageToTile == null) return;
            var data = r.GetData<Color32>();
            serial++;
            requested.Clear();
            for (int i = 0; i < data.Length; i++)
            {
                var c = data[i];
                if (c.a == 0) continue;
                int m = c.b, x = c.r, y = c.g;
                if (m >= Mips) continue;
                // そのページと、代わりに使う親たちも「要る」にする
                for (; m < Mips; m++, x >>= 1, y >>= 1)
                {
                    int flat = mipOffset[m] + y * (Pages >> m) + x;
                    if (pageStamp[flat] == serial) break;
                    pageStamp[flat] = serial;
                    requested.Add(flat);
                }
            }
            statRequested = requested.Count;
        }

        void ProcessRequests()
        {
            if (requested.Count == 0) return;
            missing.Clear();
            foreach (int p in requested)
            {
                int t = pageToTile[p];
                if (t >= 0) tileLastUsed[t] = serial;
                else missing.Add(p);
            }
            statMissing = missing.Count;
            requested.Clear();
            if (missing.Count == 0) { statBaked = 0; return; }

            // 粗いページから(すぐに代わりの絵が出る)
            missing.Sort((a, b) => MipOf(b).CompareTo(MipOf(a)));
            var toBake = new List<int>(bakeBudget);
            foreach (int p in missing)
            {
                if (toBake.Count >= bakeBudget) break;
                toBake.Add(p);
            }
            statBaked = Bake(toBake, pin: false);
            statBakedTotal += statBaked;
        }

        int MipOf(int flat)
        {
            int m = 0;
            while (m < Mips - 1 && flat >= mipOffset[m + 1]) m++;
            return m;
        }

        int AllocTile()
        {
            if (freeTiles.Count > 0) return freeTiles.Pop();
            // 今回要らなかった中で、一番長く使っていないマスを追い出す
            int best = -1, bestUsed = int.MaxValue;
            for (int t = 0; t < tileCount; t++)
            {
                if (tileLastUsed[t] == int.MaxValue || tileLastUsed[t] >= serial) continue;
                if (tileLastUsed[t] < bestUsed) { bestUsed = tileLastUsed[t]; best = t; }
            }
            if (best < 0) return -1;
            pageToTile[tileToPage[best]] = -1;
            tileToPage[best] = -1;
            return best;
        }

        int Bake(List<int> pagesToBake, bool pin)
        {
            cmd.Clear();
            cmd.SetRenderTarget(atlas);
            int baked = 0;
            float atlasPx = atlas.width;
            foreach (int flat in pagesToBake)
            {
                int t = AllocTile();
                if (t < 0) break;
                int m = MipOf(flat);
                int local = flat - mipOffset[m];
                int pagesM = Pages >> m;
                int x = local % pagesM, y = local / pagesM;
                float pageWorld = terrainSize / pagesM;
                float texel = pageWorld / Tile;
                float half = terrainSize * 0.5f;
                int tx = t % tilesPerAxis, ty = t / tilesPerAxis;

                bakeMpb.SetVector("_TileRect", new Vector4(tx * TileFull / atlasPx, ty * TileFull / atlasPx, TileFull / atlasPx, TileFull / atlasPx));
                bakeMpb.SetVector("_WorldRect", new Vector4(-half + x * pageWorld - Border * texel, -half + y * pageWorld - Border * texel, TileFull * texel, texel));
                cmd.DrawProcedural(Matrix4x4.identity, bakeMat, 0, MeshTopology.Triangles, 6, 1, bakeMpb);

                pageToTile[flat] = t;
                tileToPage[t] = flat;
                tileLastUsed[t] = pin ? int.MaxValue : serial;
                baked++;
            }
            if (baked > 0)
            {
                Graphics.ExecuteCommandBuffer(cmd);
                indirectionDirty = true;
            }
            return baked;
        }

        // 間接テーブル: 各ページ → 置いてあればそのマス、無ければ一番近い親のマス
        void RebuildIndirection()
        {
            indirectionDirty = false;
            int resident = 0;
            for (int m = Mips - 1; m >= 0; m--)
            {
                int n = Pages >> m;
                var arr = indData[m];
                var parent = m < Mips - 1 ? indData[m + 1] : null;
                int pn = n >> 1;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int t = pageToTile[mipOffset[m] + y * n + x];
                        if (t >= 0)
                        {
                            arr[y * n + x] = new Color32((byte)(t % tilesPerAxis), (byte)(t / tilesPerAxis), (byte)m, 255);
                            resident++;
                        }
                        else arr[y * n + x] = parent != null ? parent[(y >> 1) * pn + (x >> 1)] : new Color32(0, 0, (byte)m, 0);
                    }
                indirection.SetPixels32(arr, m);
            }
            indirection.Apply(false, false);
            statResident = resident;
        }

        void FlushCache()
        {
            for (int t = 0; t < tileCount; t++)
            {
                if (tileLastUsed[t] == int.MaxValue) continue;
                if (tileToPage[t] >= 0) pageToTile[tileToPage[t]] = -1;
                tileToPage[t] = -1;
                tileLastUsed[t] = 0;
                freeTiles.Push(t);
            }
            indirectionDirty = true;
        }

        static readonly string[] ModeNames = { "仕上がり", "mip(RVT Levels)", "物理のマス", "欲しい細かさに届いたか" };

        void Hud(StringBuilder sb)
        {
            sb.Append("<b>RVT</b>  ").Append(buildLog).Append('\n');
            if (direct)
                sb.Append("<color=#ff8>比べ: 毎画素その場で計算(道 240 本・貼り付け 400 個を全画素でなめる)</color>\n");
            else
                sb.AppendFormat("置いたページ {0} / {1}   要る {2}  足りない {3}  このコマ焼いた {4}(計 {5:N0})\n",
                    statResident, tileCount, statRequested, statMissing, statBaked, statBakedTotal);
            if (handleKeys) sb.AppendFormat("[B] 描き方: {0}  [1-4] 表示: {1}  [ ] mip のずらし {2:+0.0;-0.0;0}  [R] 焼き直し",
                direct ? "毎画素計算" : "RVT", ModeNames[Mathf.Clamp(debugMode, 0, 3)], mipBias);
        }

        void HudDraw(Rect area)
        {
            if (atlas == null || direct) return;
            // VSM の絵があればその右に並べる
            float x = area.x + (VSM.VsmManager.Active != null && VSM.VsmManager.Active.vsmEnabled ? 740 : 0);
            var r = new Rect(x, area.y, 205, 205);
            GUI.DrawTexture(r, atlas, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(r.x + 4, r.yMax + 2, 300, 20), "RVT 物理アトラス(焼いたページ)");
        }

        void OnDestroy()
        {
            segBuf?.Release(); decalBuf?.Release();
            if (atlas != null) atlas.Release();
            if (feedbackRT != null) feedbackRT.Release();
            cmd?.Release();
        }
    }
}
