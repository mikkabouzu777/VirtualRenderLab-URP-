using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VRL.Meshlet
{
    // 組んだ DAG を persistentDataPath/VRLCache に保存して、次からは読むだけにする(組むのは 1〜2 秒、読むのは数十 ms)。
    // 組み方を変えたら Version を上げる(古いファイルは読まない)。
    public static class MeshletDagCache
    {
        const int Version = 2; // 2 = QEM で減らす

        static string PathFor(int subdiv, int seed) =>
            Path.Combine(Application.persistentDataPath, "VRLCache", $"rock_s{subdiv}_seed{seed}_dag_v{Version}.bin");

        public static void Save(int subdiv, int seed, MeshletDag.Result r)
        {
            try
            {
                var path = PathFor(subdiv, seed);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var w = new BinaryWriter(File.Create(path));
                w.Write(Version);
                w.Write(r.originalVertexCount);
                WriteArray(w, r.verts.ToArray());
                WriteArray(w, r.indices.ToArray());
                WriteArray(w, r.meshlets.ToArray());
                WriteArray(w, r.levels.ToArray());
                WriteArray(w, r.levelTris.ToArray());
            }
            catch (Exception e) { Debug.LogWarning("[VRL Meshlet] DAG を保存できなかった: " + e.Message); }
        }

        public static MeshletDag.Result Load(int subdiv, int seed)
        {
            var path = PathFor(subdiv, seed);
            if (!File.Exists(path)) return null;
            try
            {
                using var rd = new BinaryReader(File.OpenRead(path));
                if (rd.ReadInt32() != Version) return null;
                var r = new MeshletDag.Result { originalVertexCount = rd.ReadInt32() };
                r.verts.AddRange(ReadArray<MeshletVertex>(rd));
                r.indices.AddRange(ReadArray<uint>(rd));
                r.meshlets.AddRange(ReadArray<MeshletGpu>(rd));
                r.levels.AddRange(ReadArray<MeshletDag.LevelInfo>(rd));
                r.levelTris.AddRange(ReadArray<int>(rd));
                return r.levels.Count > 0 ? r : null;
            }
            catch (Exception e) { Debug.LogWarning("[VRL Meshlet] DAG のファイルが読めないので組み直す: " + e.Message); return null; }
        }

        static void WriteArray<T>(BinaryWriter w, T[] a) where T : struct
        {
            w.Write(a.Length);
            w.Write(MemoryMarshal.AsBytes(a.AsSpan()));
        }

        static T[] ReadArray<T>(BinaryReader rd) where T : struct
        {
            int n = rd.ReadInt32();
            var a = new T[n];
            var bytes = MemoryMarshal.AsBytes(a.AsSpan());
            int got = 0;
            while (got < bytes.Length)
            {
                int k = rd.Read(bytes.Slice(got));
                if (k <= 0) throw new EndOfStreamException();
                got += k;
            }
            return a;
        }
    }
}
