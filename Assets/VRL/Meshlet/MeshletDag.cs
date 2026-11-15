using System.Collections.Generic;
using UnityEngine;

namespace VRL.Meshlet
{
    // 継ぎ目のない塊単位の LOD(Nanite の DAG と同じ考え方。公開されている説明をもとに自作、UE のコードは見ていない)。
    // 1. 元の形を塊に分ける(段 0)
    // 2. 隣り合う塊を 4 つずつグループにまとめ、グループの外周の頂点は動かさずに中だけ三角形を半分に減らし、また塊に分ける(段 1)
    //    減らし方は QEM(辺をつぶす。つぶした時に元の面からどれだけ離れるかを二次式で見て、小さい物から)
    // 3. これを塊が 1 つになるまで繰り返す
    // グループの外周を動かさないので、隣のグループが細かいままでも境目に隙間ができない。
    // 次の段ではグループの組み方が変わるので、前の段で止めていた外周も減らされる。
    // long の既定のハッシュは「上 32 ビット xor 下 32 ビット」なので、(a<<32)|b の形の鍵は a と b が近いとほぼ全部ぶつかる。混ぜてから使う
    public sealed class MixedLongComparer : IEqualityComparer<long>
    {
        public static readonly MixedLongComparer Instance = new MixedLongComparer();
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long k)
        {
            ulong x = (ulong)k;
            x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
            return (int)x;
        }
    }

    public static class MeshletDag
    {
        public const int GroupSize = 4;

        public struct LevelInfo
        {
            public uint start, count;
            public float minSelfErr, maxParentErr;
        }

        public class Result
        {
            public List<MeshletVertex> verts = new List<MeshletVertex>();
            public List<uint> indices = new List<uint>();
            public List<MeshletGpu> meshlets = new List<MeshletGpu>();
            public List<LevelInfo> levels = new List<LevelInfo>();
            public List<int> levelTris = new List<int>();
            public int originalVertexCount;
        }

        public static Result Build(RockMeshGen.Geo g0)
        {
            var res = new Result { originalVertexCount = g0.pos.Length };
            var pos = new List<Vector3>(g0.pos);
            var nrm = new List<Vector3>(g0.nrm);

            MeshletBuilder.Build(g0.pos, g0.nrm, g0.idx, 0, 0, res.meshlets, res.indices);
            int levelStart = 0, levelCount = res.meshlets.Count;
            res.levelTris.Add(g0.TriCount);

            var groupTris = new List<int>(GroupSize * MeshletBuilder.MaxTris * 3);
            for (int level = 0; level < 24 && levelCount > 1; level++)
            {
                var groups = MakeGroups(res, levelStart, levelCount);

                // どのグループの三角形が使っているか。2 つ以上のグループで使う頂点 = グループの外周 = 動かさない
                var vGroup = new int[pos.Count];
                for (int i = 0; i < vGroup.Length; i++) vGroup[i] = -1;
                for (int gi = 0; gi < groups.Count; gi++)
                    foreach (int ci in groups[gi])
                    {
                        var m = res.meshlets[ci];
                        for (uint k = m.triOffset * 3; k < (m.triOffset + m.triCount) * 3; k++)
                        {
                            int v = (int)res.indices[(int)k];
                            if (vGroup[v] == -1) vGroup[v] = gi;
                            else if (vGroup[v] != gi) vGroup[v] = -2;
                        }
                    }

                int nextStart = res.meshlets.Count;
                int nextTris = 0;
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi];
                    groupTris.Clear();
                    float childErr = 0;
                    var spheres = new List<Vector4>(g.Count);
                    foreach (int ci in g)
                    {
                        var m = res.meshlets[ci];
                        for (uint k = m.triOffset * 3; k < (m.triOffset + m.triCount) * 3; k++) groupTris.Add((int)res.indices[(int)k]);
                        childErr = Mathf.Max(childErr, m.selfErr);
                        spheres.Add(m.selfLod);
                    }

                    var simp = SimplifyQem(groupTris, pos, nrm, vGroup, out float simpErr);
                    if (simp.Count < 3) continue; // 減らせない(全部消えた)時は子が一番上のまま

                    // グループの球は子の球をすべて含む・誤差は子以上 → 遠くへ行くほど必ず粗い方へ切り替わる
                    var gs = EnclosingSphere(spheres);
                    float gerr = Mathf.Max(childErr, simpErr);
                    foreach (int ci in g)
                    {
                        var m = res.meshlets[ci];
                        m.parentLod = gs; m.parentErr = gerr; m.group = (uint)gi;
                        res.meshlets[ci] = m;
                    }

                    // グループ内の頂点を 0 から詰めて塊に分け、番号は全体の頂点へ戻す
                    var local = new Dictionary<int, int>();
                    var globalIds = new List<int>();
                    var lidx = new int[simp.Count];
                    for (int i = 0; i < simp.Count; i++)
                    {
                        int v = simp[i];
                        if (!local.TryGetValue(v, out int l)) { l = globalIds.Count; local[v] = l; globalIds.Add(v); }
                        lidx[i] = l;
                    }
                    var lp = new Vector3[globalIds.Count];
                    var ln = new Vector3[globalIds.Count];
                    for (int i = 0; i < lp.Length; i++) { lp[i] = pos[globalIds[i]]; ln[i] = nrm[globalIds[i]]; }
                    int before = res.meshlets.Count;
                    MeshletBuilder.Build(lp, ln, lidx, 0, globalIds.ToArray(), (uint)(level + 1), res.meshlets, res.indices);
                    for (int i = before; i < res.meshlets.Count; i++)
                    {
                        var m = res.meshlets[i];
                        m.selfLod = gs; m.selfErr = gerr; m.parentErr = 1e30f;
                        res.meshlets[i] = m;
                    }
                    nextTris += simp.Count / 3;
                }

                int newCount = res.meshlets.Count - nextStart;
                AddLevel(res, levelStart, levelCount);
                if (newCount == 0) { levelCount = 0; break; }
                res.levelTris.Add(nextTris);
                bool stalled = newCount > levelCount * 0.92f; // ほとんど減らなかったらここを一番上にする
                levelStart = nextStart; levelCount = newCount;
                if (stalled) break;
            }
            if (levelCount > 0) AddLevel(res, levelStart, levelCount);

            res.verts.Capacity = pos.Count;
            for (int i = 0; i < pos.Count; i++) res.verts.Add(new MeshletVertex { pos = pos[i], nrm = nrm[i] });
            return res;
        }

        static void AddLevel(Result res, int start, int count)
        {
            var li = new LevelInfo { start = (uint)start, count = (uint)count, minSelfErr = float.MaxValue, maxParentErr = 0 };
            for (int i = start; i < start + count; i++)
            {
                li.minSelfErr = Mathf.Min(li.minSelfErr, res.meshlets[i].selfErr);
                li.maxParentErr = Mathf.Max(li.maxParentErr, res.meshlets[i].parentErr);
            }
            res.levels.Add(li);
        }

        // ---- 隣り合う塊を 4 つずつまとめる ----
        static List<List<int>> MakeGroups(Result res, int start, int count)
        {
            // 塊どうしが共有する辺の数 = つながりの強さ
            var edgeOwner = new Dictionary<long, int>(count * 200, MixedLongComparer.Instance);
            var adj = new Dictionary<int, int>[count];
            for (int i = 0; i < count; i++) adj[i] = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                var m = res.meshlets[start + i];
                for (uint t = m.triOffset; t < m.triOffset + m.triCount; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        long a = res.indices[(int)(t * 3 + k)], b = res.indices[(int)(t * 3 + (k + 1) % 3)];
                        long key = a < b ? (a << 32) | b : (b << 32) | a;
                        if (edgeOwner.TryGetValue(key, out int o))
                        {
                            if (o != i)
                            {
                                adj[i].TryGetValue(o, out int w1); adj[i][o] = w1 + 1;
                                adj[o].TryGetValue(i, out int w2); adj[o][i] = w2 + 1;
                            }
                        }
                        else edgeOwner[key] = i;
                    }
            }

            // 場所の近い順(Morton 順)に種を選び、つながりの強い隣を足していく
            Vector3 mn = Vector3.one * float.MaxValue, mx = -mn;
            for (int i = 0; i < count; i++) { Vector3 c = res.meshlets[start + i].sphere; mn = Vector3.Min(mn, c); mx = Vector3.Max(mx, c); }
            var ext = Vector3.Max(mx - mn, Vector3.one * 1e-5f);
            var order = new int[count];
            var keys = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
                Vector3 c = res.meshlets[start + i].sphere;
                keys[i] = Morton((c.x - mn.x) / ext.x, (c.y - mn.y) / ext.y, (c.z - mn.z) / ext.z);
            }
            System.Array.Sort(keys, order);

            var groupOf = new int[count];
            for (int i = 0; i < count; i++) groupOf[i] = -1;
            var groups = new List<List<int>>();
            foreach (int seed in order)
            {
                if (groupOf[seed] >= 0) continue;
                var g = new List<int> { seed };
                groupOf[seed] = groups.Count;
                while (g.Count < GroupSize)
                {
                    int best = -1, bestW = 0;
                    foreach (int gm in g)
                        foreach (var kv in adj[gm])
                        {
                            if (groupOf[kv.Key] >= 0) continue;
                            int w = 0;
                            foreach (int gm2 in g) if (adj[gm2].TryGetValue(kv.Key, out int ww)) w += ww;
                            if (w > bestW) { bestW = w; best = kv.Key; }
                        }
                    if (best < 0) break;
                    groupOf[best] = groups.Count;
                    g.Add(best);
                }
                // 1 つだけ余った塊は、隣のグループ(大きすぎない物)へ入れる
                if (g.Count == 1)
                {
                    int bestG = -1, bestW = 0;
                    foreach (var kv in adj[seed])
                    {
                        int og = groupOf[kv.Key];
                        if (og < 0 || og == groups.Count || groups[og].Count >= GroupSize * 2) continue;
                        if (kv.Value > bestW) { bestW = kv.Value; bestG = og; }
                    }
                    if (bestG >= 0) { groups[bestG].Add(seed); groupOf[seed] = bestG; continue; }
                }
                groups.Add(g);
            }
            for (int gi = 0; gi < groups.Count; gi++)
                for (int i = 0; i < groups[gi].Count; i++) groups[gi][i] += start;
            return groups;
        }

        static ulong Morton(float x, float y, float z)
        {
            ulong Spread(float f)
            {
                ulong v = (ulong)Mathf.Clamp(f * 1023f, 0, 1023);
                v = (v | (v << 16)) & 0x030000FF;
                v = (v | (v << 8)) & 0x0300F00F;
                v = (v | (v << 4)) & 0x030C30C3;
                v = (v | (v << 2)) & 0x09249249;
                return v;
            }
            return Spread(x) | (Spread(y) << 1) | (Spread(z) << 2);
        }

        // ---- グループの中だけ減らす: 外周の頂点は動かさず、中の頂点を格子のマスごとに 1 つへまとめる ----
        // 三角形がおおよそ半分になるまでマスを大きくしていく。誤差 = 頂点が動いた距離の最大
        static List<int> SimplifyLocked(List<int> tris, List<Vector3> pos, List<Vector3> nrm, int[] vGroup, out float error)
        {
            int triCount = tris.Count / 3;
            int target = triCount / 2;
            double edgeSum = 0;
            for (int i = 0; i < tris.Count; i += 3) edgeSum += (pos[tris[i]] - pos[tris[i + 1]]).magnitude;
            float cell = (float)(edgeSum / triCount) * 1.2f;

            var uniq = new List<int>();
            var seen = new HashSet<int>();
            foreach (int v in tris) if (seen.Add(v)) uniq.Add(v);

            List<int> best = null;
            Dictionary<int, int> bestRemap = null;
            List<(Vector3 p, Vector3 n, int cnt, int first)> bestCells = null;
            error = 0;
            for (int iter = 0; iter < 14; iter++)
            {
                var cellOf = new Dictionary<long, int>(MixedLongComparer.Instance);
                var cells = new List<(Vector3 p, Vector3 n, int cnt, int first)>();
                var remap = new Dictionary<int, int>(uniq.Count); // 頂点 → マス番号(負なら -1-元の頂点 = 動かさない)
                float inv = 1f / cell;
                foreach (int v in uniq)
                {
                    if (vGroup[v] == -2) { remap[v] = -1 - v; continue; }
                    var p = pos[v];
                    long kx = Mathf.FloorToInt(p.x * inv) + 1048576, ky = Mathf.FloorToInt(p.y * inv) + 1048576, kz = Mathf.FloorToInt(p.z * inv) + 1048576;
                    long key = (kx << 42) | (ky << 21) | kz;
                    if (!cellOf.TryGetValue(key, out int c)) { c = cells.Count; cellOf[key] = c; cells.Add((Vector3.zero, Vector3.zero, 0, v)); }
                    var e = cells[c];
                    cells[c] = (e.p + p, e.n + nrm[v], e.cnt + 1, e.first);
                    remap[v] = c;
                }
                var outTris = new List<int>(tris.Count / 2);
                var dup = new HashSet<(int, int, int)>();
                for (int i = 0; i < tris.Count; i += 3)
                {
                    int a = remap[tris[i]], b = remap[tris[i + 1]], c = remap[tris[i + 2]];
                    if (a == b || b == c || c == a) continue;
                    var k = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
                    if (!dup.Add(k)) continue;
                    outTris.Add(a); outTris.Add(b); outTris.Add(c);
                }
                best = outTris; bestRemap = remap; bestCells = cells;
                if (outTris.Count / 3 <= target * 1.1f) break;
                cell *= 1.3f;
            }

            // マスの代表点を作る(頂点 1 つだけのマスは元の頂点をそのまま使う)
            var cellVert = new int[bestCells.Count];
            for (int c = 0; c < bestCells.Count; c++)
            {
                var e = bestCells[c];
                if (e.cnt == 1) { cellVert[c] = e.first; continue; }
                cellVert[c] = pos.Count;
                pos.Add(e.p / e.cnt);
                nrm.Add(e.n.sqrMagnitude > 0 ? e.n.normalized : Vector3.up);
            }
            foreach (var kv in bestRemap)
            {
                if (kv.Value < 0) continue;
                error = Mathf.Max(error, (pos[kv.Key] - pos[cellVert[kv.Value]]).magnitude);
            }
            for (int i = 0; i < best.Count; i++) best[i] = best[i] < 0 ? -1 - best[i] : cellVert[best[i]];
            return best;
        }

        // ---- QEM: グループの中だけ辺をつぶして三角形を半分へ。外周の頂点(vGroup == -2)は動かさない ----
        // 1 回の見回しで、費用の小さい辺から「まだ触っていない頂点どうし」の物をつぶす(つぶした所の周りは次の見回しで)。
        // 誤差 = つぶした時の二次式の値(元の面までの距離の 2 乗の和)の最大の平方根
        static List<int> SimplifyQem(List<int> tris, List<Vector3> pos, List<Vector3> nrm, int[] vGroup, out float error)
        {
            error = 0;
            var g2l = new Dictionary<int, int>();
            var l2g = new List<int>();
            int nt = tris.Count / 3;
            var tv = new int[tris.Count];
            for (int i = 0; i < tris.Count; i++)
            {
                int g = tris[i];
                if (!g2l.TryGetValue(g, out int l)) { l = l2g.Count; g2l[g] = l; l2g.Add(g); }
                tv[i] = l;
            }
            int nv = l2g.Count;
            var P = new Vector3[nv];
            var N = new Vector3[nv];
            var locked = new bool[nv];
            var moved = new bool[nv];
            var parent = new int[nv];
            var Q = new double[nv * 10];
            var vt = new List<int>[nv];
            for (int v = 0; v < nv; v++) { P[v] = pos[l2g[v]]; N[v] = nrm[l2g[v]]; locked[v] = vGroup[l2g[v]] == -2; parent[v] = v; vt[v] = new List<int>(8); }
            var alive = new bool[nt];
            for (int t = 0; t < nt; t++)
            {
                alive[t] = true;
                int a = tv[t * 3], b = tv[t * 3 + 1], c = tv[t * 3 + 2];
                vt[a].Add(t); vt[b].Add(t); vt[c].Add(t);
                var n = Vector3.Cross(P[b] - P[a], P[c] - P[a]);
                double len = n.magnitude;
                if (len < 1e-12) continue;
                double nx = n.x / len, ny = n.y / len, nz = n.z / len;
                double d = -(nx * P[a].x + ny * P[a].y + nz * P[a].z);
                foreach (int v in new[] { a, b, c }) AddPlane(Q, v, nx, ny, nz, d);
            }

            int Find(int v) { while (parent[v] != v) { parent[v] = parent[parent[v]]; v = parent[v]; } return v; }

            int aliveCount = nt, target = nt / 2;
            double maxCost = 0;
            var edgeSet = new HashSet<long>();
            var edges = new List<(double cost, int keep, int rem, Vector3 p)>();
            var touched = new bool[nv];
            var q = new double[10];
            for (int pass = 0; pass < 24 && aliveCount > target; pass++)
            {
                edgeSet.Clear(); edges.Clear();
                for (int t = 0; t < nt; t++)
                {
                    if (!alive[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int u = Find(tv[t * 3 + k]), v = Find(tv[t * 3 + (k + 1) % 3]);
                        if (u == v || (locked[u] && locked[v])) continue;
                        long key = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
                        if (!edgeSet.Add(key)) continue;
                        for (int j = 0; j < 10; j++) q[j] = Q[u * 10 + j] + Q[v * 10 + j];
                        // 外周の頂点は動かさないので、そちらへ寄せる。両方動かせるなら両端と真ん中で一番安い所
                        if (locked[u]) edges.Add((Eval(q, P[u]), u, v, P[u]));
                        else if (locked[v]) edges.Add((Eval(q, P[v]), v, u, P[v]));
                        else
                        {
                            var mid = (P[u] + P[v]) * 0.5f;
                            double cu = Eval(q, P[u]), cv = Eval(q, P[v]), cm = Eval(q, mid);
                            if (cm < cu && cm < cv) edges.Add((cm, u, v, mid));
                            else if (cu <= cv) edges.Add((cu, u, v, P[u]));
                            else edges.Add((cv, v, u, P[v]));
                        }
                    }
                }
                if (edges.Count == 0) break;
                edges.Sort((x, y) => x.cost.CompareTo(y.cost));
                System.Array.Clear(touched, 0, nv);
                int collapsed = 0;
                foreach (var e in edges)
                {
                    if (aliveCount <= target) break;
                    int keep = e.keep, rem = e.rem;
                    if (touched[keep] || touched[rem] || Find(keep) != keep || Find(rem) != rem) continue;
                    if (Flips(keep, rem, e.p) || Flips(rem, keep, e.p)) continue;
                    // つぶす: rem → keep
                    parent[rem] = keep;
                    if ((e.p - P[keep]).sqrMagnitude > 0) moved[keep] = true;
                    P[keep] = e.p;
                    N[keep] += N[rem];
                    for (int j = 0; j < 10; j++) Q[keep * 10 + j] += Q[rem * 10 + j];
                    foreach (int t in vt[rem])
                    {
                        if (!alive[t]) continue;
                        int a = Find(tv[t * 3]), b = Find(tv[t * 3 + 1]), c = Find(tv[t * 3 + 2]);
                        if (a == b || b == c || c == a) { alive[t] = false; aliveCount--; }
                        else vt[keep].Add(t);
                    }
                    touched[keep] = touched[rem] = true;
                    // 周りの頂点も今回は触らない(費用が古くなるため)
                    foreach (int t in vt[keep])
                        if (alive[t]) for (int k = 0; k < 3; k++) touched[Find(tv[t * 3 + k])] = true;
                    maxCost = System.Math.Max(maxCost, e.cost);
                    collapsed++;
                }
                if (collapsed == 0) break;
            }

            // 頂点 r を p へ動かした時に、r を含み k を含まない三角形が裏返らないか
            bool Flips(int r, int k, Vector3 p)
            {
                foreach (int t in vt[r])
                {
                    if (!alive[t]) continue;
                    int a = Find(tv[t * 3]), b = Find(tv[t * 3 + 1]), c = Find(tv[t * 3 + 2]);
                    if (a == k || b == k || c == k) continue;
                    if (a != r && b != r && c != r) continue;
                    Vector3 pa = P[a], pb = P[b], pc = P[c];
                    var before = Vector3.Cross(pb - pa, pc - pa);
                    if (a == r) pa = p; else if (b == r) pb = p; else pc = p;
                    var after = Vector3.Cross(pb - pa, pc - pa);
                    if (after.sqrMagnitude < 1e-20f || Vector3.Dot(before, after) < 0.2f * before.magnitude * after.magnitude) return true;
                }
                return false;
            }

            // 出力: 動いた頂点は新しい頂点にする(元の頂点は前の段がまだ使う)
            var newId = new int[nv];
            for (int v = 0; v < nv; v++) newId[v] = -1;
            var outTris = new List<int>(aliveCount * 3);
            var dup = new HashSet<(int, int, int)>();
            for (int t = 0; t < nt; t++)
            {
                if (!alive[t]) continue;
                int a = Find(tv[t * 3]), b = Find(tv[t * 3 + 1]), c = Find(tv[t * 3 + 2]);
                if (a == b || b == c || c == a) continue;
                var key = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
                if (!dup.Add(key)) continue;
                foreach (int v in new[] { a, b, c })
                {
                    if (newId[v] >= 0) continue;
                    if (!moved[v]) newId[v] = l2g[v];
                    else { newId[v] = pos.Count; pos.Add(P[v]); nrm.Add(N[v].sqrMagnitude > 0 ? N[v].normalized : Vector3.up); }
                }
                outTris.Add(newId[a]); outTris.Add(newId[b]); outTris.Add(newId[c]);
            }
            error = (float)System.Math.Sqrt(System.Math.Max(0, maxCost));
            return outTris;
        }

        static void AddPlane(double[] Q, int v, double a, double b, double c, double d)
        {
            int o = v * 10;
            Q[o] += a * a; Q[o + 1] += a * b; Q[o + 2] += a * c; Q[o + 3] += a * d;
            Q[o + 4] += b * b; Q[o + 5] += b * c; Q[o + 6] += b * d;
            Q[o + 7] += c * c; Q[o + 8] += c * d; Q[o + 9] += d * d;
        }

        // 二次式の値 = 点 p から(集めた)面までの距離の 2 乗の和
        static double Eval(double[] q, Vector3 p)
        {
            double x = p.x, y = p.y, z = p.z;
            double v = q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x
                     + q[4] * y * y + 2 * q[5] * y * z + 2 * q[6] * y
                     + q[7] * z * z + 2 * q[8] * z + q[9];
            return v < 0 ? 0 : v;
        }

        static Vector4 EnclosingSphere(List<Vector4> s)
        {
            Vector3 mn = Vector3.one * float.MaxValue, mx = -mn;
            foreach (var q in s)
            {
                Vector3 c = q; var r = Vector3.one * q.w;
                mn = Vector3.Min(mn, c - r); mx = Vector3.Max(mx, c + r);
            }
            var cc = (mn + mx) * 0.5f;
            float rr = 0;
            foreach (var q in s) rr = Mathf.Max(rr, ((Vector3)q - cc).magnitude + q.w);
            return new Vector4(cc.x, cc.y, cc.z, rr);
        }
    }
}
