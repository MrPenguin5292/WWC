using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>
/// Reads rc_hilux.mesh (written by tools/make_hilux.py in Blender): "RCM1", part count; per part a name, vertices (position, normal, uv,
/// already in Unity axes) and submeshes (material name + triangle indices). Every triangle is added again reversed on its own vertices
/// with the normal turned around, so the meshes are double-sided (house rule). Parsed once per game session.
/// </summary>
internal static class MeshFile
{
    internal sealed class Part
    {
        public Mesh Mesh = null!;
        public string[] Materials = Array.Empty<string>();
    }

    static Dictionary<string, Part>? parts;
    static bool tried;

    public static Dictionary<string, Part>? Load(string path)
    {
        if (tried) return parts;
        tried = true;
        if (!File.Exists(path)) { Plugin.Warn($"{Path.GetFileName(path)} not found, using the simple built-in car"); return null; }
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RCM1") throw new InvalidDataException("not an RC mesh file");
        int count = r.ReadInt32();
        var result = new Dictionary<string, Part>();
        for (int p = 0; p < count; p++)
        {
            string name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
            int n = r.ReadInt32();
            var pos = new Vector3[n]; var nrm = new Vector3[n]; var uv = new Vector2[n];
            for (int i = 0; i < n; i++) pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            for (int i = 0; i < n; i++) nrm[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            for (int i = 0; i < n; i++) uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
            int subs = r.ReadInt32();
            var mats = new string[subs]; var tris = new int[subs][];
            for (int s = 0; s < subs; s++)
            {
                mats[s] = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
                int k = r.ReadInt32(); tris[s] = new int[k];
                for (int i = 0; i < k; i++) tris[s][i] = r.ReadInt32();
            }
            // double-sided copy
            var allPos = new Vector3[n * 2]; var allNrm = new Vector3[n * 2]; var allUv = new Vector2[n * 2];
            Array.Copy(pos, allPos, n); Array.Copy(pos, 0, allPos, n, n);
            Array.Copy(uv, allUv, n); Array.Copy(uv, 0, allUv, n, n);
            for (int i = 0; i < n; i++) { allNrm[i] = nrm[i]; allNrm[n + i] = -nrm[i]; }
            var mesh = new Mesh { name = "rc_" + name, indexFormat = n * 2 > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.vertices = allPos; mesh.normals = allNrm; mesh.uv = allUv;
            mesh.subMeshCount = subs;
            for (int s = 0; s < subs; s++)
            {
                var t = tris[s]; var both = new int[t.Length * 2];
                Array.Copy(t, both, t.Length);
                for (int i = 0; i < t.Length; i += 3) { both[t.Length + i] = t[i] + n; both[t.Length + i + 1] = t[i + 2] + n; both[t.Length + i + 2] = t[i + 1] + n; }
                mesh.SetTriangles(both, s);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            result[name] = new Part { Mesh = mesh, Materials = mats };
        }
        parts = result;
        Plugin.Note($"car model loaded: {string.Join(", ", result.Keys)}");
        return parts;
    }
}
