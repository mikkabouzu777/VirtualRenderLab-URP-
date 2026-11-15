using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VRL.Meshlet
{
    // GPU へ送る 1 塊ぶん。シェーダー側の MeshletGpu と並びを合わせる(96 バイト)。
    // selfLod/selfErr = この塊を作った単純化の球と誤差、parentLod/parentErr = この塊を含むグループをさらに粗くした時の球と誤差。
    // 「自分の誤差は目標以下、親の誤差は目標より大きい」塊だけ描くと、グループの境目がそろったまま場所ごとに細かさが変わる(Nanite の DAG)。
    [StructLayout(LayoutKind.Sequential)]
    public struct MeshletGpu
    {
        public Vector4 sphere;   // xyz=中心(物体座標) w=半径
        public Vector4 cone;     // xyz=法線の平均 w=カットオフ(1 なら裏向き判定をしない)
        public Vector4 selfLod;  // xyz=中心 w=半径
        public Vector4 parentLod;
        public uint triOffset;   // 全体の三角形リストでの先頭
        public uint triCount;
        public uint lod;         // DAG の段(0 が元の形)
        public uint group;
        public float selfErr;
        public float parentErr;  // 一番上の塊は 1e30
        public float pad0, pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MeshletVertex
    {
        public Vector3 pos;
        public Vector3 nrm;
    }

    // 三角形を、頂点 64・三角形 124 までの小さな塊(Meshlet)に分ける。
    // 隣り合う三角形から「新しく増える頂点が少ないもの」を順に足していく貪欲法(meshoptimizer の考え方をなぞった自作。コードは写していない)。
    public static class MeshletBuilder
    {
        public const int MaxVerts = 64;
        public const int MaxTris = 124;

        public static void Build(Vector3[] pos, Vector3[] nrm, int[] idx, uint baseVertex, uint lod,
            List<MeshletGpu> outMeshlets, List<uint> outIndices) => Build(pos, nrm, idx, baseVertex, null, lod, outMeshlets, outIndices);

        /// <summary>globalIds を渡すと、頂点番号 i を globalIds[i] として書く(グループごとに小さく詰めた頂点を、全体の頂点の番号へ戻す)。</summary>
        public static void Build(Vector3[] pos, Vector3[] nrm, int[] idx, uint baseVertex, int[] globalIds, uint lod,
            List<MeshletGpu> outMeshlets, List<uint> outIndices)
        {
            int triCount = idx.Length / 3;
            int vCount = pos.Length;

            // 頂点 → それを使う三角形(CSR 形式)
            var vtCount = new int[vCount + 1];
            for (int i = 0; i < idx.Length; i++) vtCount[idx[i] + 1]++;
            for (int i = 0; i < vCount; i++) vtCount[i + 1] += vtCount[i];
            var vtList = new int[idx.Length];
            var fill = new int[vCount];
            for (int t = 0; t < triCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int v = idx[t * 3 + k];
                    vtList[vtCount[v] + fill[v]++] = t;
                }

            var faceN = new Vector3[triCount];
            var faceC = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
            {
                Vector3 a = pos[idx[t * 3]], b = pos[idx[t * 3 + 1]], c = pos[idx[t * 3 + 2]];
                var n = Vector3.Cross(b - a, c - a);
                faceN[t] = n.sqrMagnitude > 0 ? n.normalized : Vector3.up;
                faceC[t] = (a + b + c) / 3f;
            }

            var used = new bool[triCount];
            var vertStamp = new int[vCount];   // この塊に入っている頂点なら塊番号+1
            var candStamp = new int[triCount];
            var cands = new List<int>(256);
            var mVerts = new List<int>(MaxVerts);
            var mTris = new List<int>(MaxTris);
            int scan = 0, stamp = 0;

            while (true)
            {
                while (scan < triCount && used[scan]) scan++;
                if (scan >= triCount) break;
                stamp++;
                mVerts.Clear(); mTris.Clear(); cands.Clear();
                Vector3 centroidSum = Vector3.zero;

                int next = scan;
                while (next >= 0)
                {
                    // 三角形 next を塊に入れる
                    used[next] = true;
                    mTris.Add(next);
                    centroidSum += faceC[next];
                    for (int k = 0; k < 3; k++)
                    {
                        int v = idx[next * 3 + k];
                        if (vertStamp[v] != stamp) { vertStamp[v] = stamp; mVerts.Add(v); }
                        for (int j = vtCount[v]; j < vtCount[v + 1]; j++)
                        {
                            int nt = vtList[j];
                            if (!used[nt] && candStamp[nt] != stamp) { candStamp[nt] = stamp; cands.Add(nt); }
                        }
                    }
                    if (mTris.Count >= MaxTris) break;

                    // 次の三角形: 増える頂点が少ない → 塊の中心に近い の順
                    Vector3 centroid = centroidSum / mTris.Count;
                    int best = -1, bestNew = 4; float bestD = float.MaxValue;
                    for (int ci = cands.Count - 1; ci >= 0; ci--)
                    {
                        int t = cands[ci];
                        if (used[t]) { cands.RemoveAt(ci); continue; }
                        int nNew = 0;
                        for (int k = 0; k < 3; k++) if (vertStamp[idx[t * 3 + k]] != stamp) nNew++;
                        if (mVerts.Count + nNew > MaxVerts) continue;
                        float d = (faceC[t] - centroid).sqrMagnitude;
                        if (nNew < bestNew || (nNew == bestNew && d < bestD)) { best = t; bestNew = nNew; bestD = d; }
                    }
                    next = best;
                }

                outMeshlets.Add(MakeMeshlet(pos, idx, faceN, mTris, mVerts, (uint)outIndices.Count / 3, lod));
                foreach (int t in mTris)
                    for (int k = 0; k < 3; k++) outIndices.Add(globalIds != null ? (uint)globalIds[idx[t * 3 + k]] : baseVertex + (uint)idx[t * 3 + k]);
            }
        }

        static MeshletGpu MakeMeshlet(Vector3[] pos, int[] idx, Vector3[] faceN, List<int> tris, List<int> verts, uint triOffset, uint lod)
        {
            Vector3 min = pos[verts[0]], max = min;
            foreach (int v in verts) { min = Vector3.Min(min, pos[v]); max = Vector3.Max(max, pos[v]); }
            Vector3 c = (min + max) * 0.5f;
            float r = 0;
            foreach (int v in verts) r = Mathf.Max(r, (pos[v] - c).magnitude);

            Vector3 axis = Vector3.zero;
            foreach (int t in tris) axis += faceN[t];
            float cutoff = 1f;
            if (axis.sqrMagnitude > 1e-8f)
            {
                axis.Normalize();
                float minDp = 1f;
                foreach (int t in tris) minDp = Mathf.Min(minDp, Vector3.Dot(axis, faceN[t]));
                // 法線が 84° 以上ばらつく塊は裏向き判定に使わない
                if (minDp > 0.1f) cutoff = Mathf.Sqrt(1f - minDp * minDp);
            }
            else axis = Vector3.up;

            return new MeshletGpu
            {
                sphere = new Vector4(c.x, c.y, c.z, r),
                cone = new Vector4(axis.x, axis.y, axis.z, cutoff),
                selfLod = new Vector4(c.x, c.y, c.z, r),
                parentLod = new Vector4(c.x, c.y, c.z, r),
                parentErr = 1e30f,
                triOffset = triOffset,
                triCount = (uint)tris.Count,
                lod = lod,
            };
        }
    }
}
