using System.Collections.Generic;
using UnityEngine;

namespace VRL.Meshlet
{
    // 試験用の重い岩(細分化した正二十面体をノイズで凸凹に)と、LOD 用の単純化(格子で頂点をまとめる)。
    public static class RockMeshGen
    {
        public struct Geo
        {
            public Vector3[] pos;
            public Vector3[] nrm;
            public int[] idx;
            public int TriCount => idx.Length / 3;
        }

        /// <summary>subdiv=7 で 327,680 三角形。</summary>
        public static Geo Rock(int subdiv, int seed)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            Icosphere(subdiv, verts, tris);

            var off = new Vector3(seed * 13.17f, seed * 7.31f, seed * 3.77f);
            var pos = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++)
            {
                var d = verts[i];
                float big = VrlNoise.Fbm(d * 1.1f + off, 3) * 0.35f;
                float ridge = VrlNoise.Ridged(d * 2.6f + off * 1.7f, 5) * 0.22f;
                float fine = VrlNoise.Fbm(d * 9f + off, 4) * 0.035f;
                float r = 1f + big + ridge + fine;
                var p = d * r;
                // 下を平らにして地面に座らせる
                if (p.y < -0.35f) p.y = -0.35f + (p.y + 0.35f) * 0.25f;
                p.x *= 1.25f; p.z *= 0.9f; p.y *= 0.8f;
                pos[i] = p;
            }
            var g = new Geo { pos = pos, idx = tris.ToArray() };
            g.nrm = ComputeNormals(g.pos, g.idx);
            return g;
        }

        static void Icosphere(int subdiv, List<Vector3> v, List<int> t)
        {
            float a = (1f + Mathf.Sqrt(5f)) / 2f;
            Vector3[] b =
            {
                new(-1, a, 0), new(1, a, 0), new(-1, -a, 0), new(1, -a, 0),
                new(0, -1, a), new(0, 1, a), new(0, -1, -a), new(0, 1, -a),
                new(a, 0, -1), new(a, 0, 1), new(-a, 0, -1), new(-a, 0, 1),
            };
            foreach (var p in b) v.Add(p.normalized);
            // Unity は左手系なので外向きに見える順(時計回り)に並べる
            int[] f =
            {
                0, 5, 11, 0, 1, 5, 0, 7, 1, 0, 10, 7, 0, 11, 10,
                1, 9, 5, 5, 4, 11, 11, 2, 10, 10, 6, 7, 7, 8, 1,
                3, 4, 9, 3, 2, 4, 3, 6, 2, 3, 8, 6, 3, 9, 8,
                4, 5, 9, 2, 11, 4, 6, 10, 2, 8, 7, 6, 9, 1, 8,
            };
            t.AddRange(f);
            for (int s = 0; s < subdiv; s++)
            {
                var cache = new Dictionary<long, int>(t.Count, MixedLongComparer.Instance);
                var nt = new List<int>(t.Count * 4);
                for (int i = 0; i < t.Count; i += 3)
                {
                    int i0 = t[i], i1 = t[i + 1], i2 = t[i + 2];
                    int m01 = Mid(i0, i1, v, cache), m12 = Mid(i1, i2, v, cache), m20 = Mid(i2, i0, v, cache);
                    nt.Add(i0); nt.Add(m01); nt.Add(m20);
                    nt.Add(i1); nt.Add(m12); nt.Add(m01);
                    nt.Add(i2); nt.Add(m20); nt.Add(m12);
                    nt.Add(m01); nt.Add(m12); nt.Add(m20);
                }
                t.Clear(); t.AddRange(nt);
            }
            // 向きの確かめ: Unity の表は Cross(b-a,c-a) が外向き。逆なら全部裏返す。
            var fn = Vector3.Cross(v[t[1]] - v[t[0]], v[t[2]] - v[t[0]]);
            if (Vector3.Dot(fn, v[t[0]] + v[t[1]] + v[t[2]]) < 0)
                for (int i = 0; i < t.Count; i += 3) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
        }

        static int Mid(int a, int b, List<Vector3> v, Dictionary<long, int> cache)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (cache.TryGetValue(key, out int m)) return m;
            m = v.Count;
            v.Add(((v[a] + v[b]) * 0.5f).normalized);
            cache[key] = m;
            return m;
        }

        public static Vector3[] ComputeNormals(Vector3[] pos, int[] idx)
        {
            var n = new Vector3[pos.Length];
            for (int i = 0; i < idx.Length; i += 3)
            {
                int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]); // 面積で重み付け
                n[a] += fn; n[b] += fn; n[c] += fn;
            }
            for (int i = 0; i < n.Length; i++) n[i] = n[i].sqrMagnitude > 0 ? n[i].normalized : Vector3.up;
            return n;
        }

        /// <summary>格子のマスごとに頂点を 1 つへまとめる単純化(vertex clustering)。継ぎ目のない Nanite 方式ではなく、物ごとに LOD を切り替える試作用。</summary>
        public static Geo Simplify(Geo src, float cell)
        {
            var map = new Dictionary<long, int>(MixedLongComparer.Instance);
            var remap = new int[src.pos.Length];
            var sumP = new List<Vector3>();
            var sumN = new List<Vector3>();
            var cnt = new List<int>();
            float inv = 1f / cell;
            for (int i = 0; i < src.pos.Length; i++)
            {
                var p = src.pos[i];
                long kx = Mathf.FloorToInt(p.x * inv) + 1048576, ky = Mathf.FloorToInt(p.y * inv) + 1048576, kz = Mathf.FloorToInt(p.z * inv) + 1048576;
                long key = (kx << 42) | (ky << 21) | kz;
                if (!map.TryGetValue(key, out int id))
                {
                    id = sumP.Count; map[key] = id;
                    sumP.Add(Vector3.zero); sumN.Add(Vector3.zero); cnt.Add(0);
                }
                remap[i] = id;
                sumP[id] += p; sumN[id] += src.nrm[i]; cnt[id]++;
            }
            var pos = new Vector3[sumP.Count];
            var nrm = new Vector3[sumP.Count];
            for (int i = 0; i < pos.Length; i++) { pos[i] = sumP[i] / cnt[i]; nrm[i] = sumN[i].normalized; }

            var idx = new List<int>(src.idx.Length / 2);
            var seen = new HashSet<(int, int, int)>();
            for (int i = 0; i < src.idx.Length; i += 3)
            {
                int a = remap[src.idx[i]], b = remap[src.idx[i + 1]], c = remap[src.idx[i + 2]];
                if (a == b || b == c || c == a) continue;
                // 同じ三角形の重複を除く(向きは保つので回転だけそろえる)
                var k = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
                if (!seen.Add(k)) continue;
                idx.Add(a); idx.Add(b); idx.Add(c);
            }
            return new Geo { pos = pos, nrm = nrm, idx = idx.ToArray() };
        }

        public static float AverageEdge(Geo g)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i < g.idx.Length; i += 3)
            {
                sum += (g.pos[g.idx[i]] - g.pos[g.idx[i + 1]]).magnitude;
                n++;
            }
            return n > 0 ? (float)(sum / n) : 0f;
        }
    }
}
