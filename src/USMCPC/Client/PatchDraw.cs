using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace USMCPC.Client;

// Shared patch drawing for the USMC PC Gen III, JPC and AirLite clients: the three files are the same apart from the namespace line
// (tools/patchfit/sync_patchdraw.ps1 copies this one into the other two). 2026-11-17 "Patches on more gear".

/// <summary>One place a patch can go on a carrier: its frame in the donor's bind space, its room, and what is under it.</summary>
internal sealed class DrawSpot
{
    public Vector3 Centre, Normal, Up, Right;
    public float Back, HalfW, Top, Bottom;
    /// <summary>True: every patch vertex is laid on the field's sheet (patch_spots.json). False: the whole flat patch is lifted onto the
    /// highest surface under it (the bundle's own fields, the 0.2.x way).</summary>
    public bool Drape;
    public SpotField? Field;
    public BoneWeight Weight;
    /// <summary>Set when the spot sits on a part that moves as one solid piece (a phone mount): every patch vertex then takes this weight,
    /// so the patch stays glued to the piece instead of following the surface's own varying weights.</summary>
    public BoneWeight? Rigid;
}

/// <summary>A grid in a spot's (right, up) plane: surface (or drape sheet) height along the normal, and the skin weights under each cell.</summary>
internal sealed class SpotField
{
    public float cell, halfW, halfH;
    public int nx, ny;
    public float[] heights = Array.Empty<float>();
    public int[] boneIndices = Array.Empty<int>();
    public float[] boneWeights = Array.Empty<float>();

    public bool Valid => nx > 1 && ny > 1 && heights.Length == nx * ny;

    private int Cell(float a, float b)
    {
        int i = Mathf.FloorToInt((a + halfW) / cell), j = Mathf.FloorToInt((b + halfH) / cell);
        return i < 0 || j < 0 || i >= nx || j >= ny ? -1 : j * nx + i;
    }

    /// <summary>The cell's own value (the 0.2.x lookup), MinValue outside.</summary>
    public float At(float a, float b)
    {
        int c = Cell(a, b);
        return c < 0 || !Valid ? float.MinValue : heights[c];
    }

    /// <summary>Bilinear between cell centres, clamped at the edges (tools/patchfit/patch_fit_audit.py Field.sheet does the same).</summary>
    public float Sheet(float a, float b)
    {
        float x = Mathf.Clamp((a + halfW) / cell - 0.5f, 0f, nx - 1.0001f), y = Mathf.Clamp((b + halfH) / cell - 0.5f, 0f, ny - 1.0001f);
        int i = (int)x, j = (int)y;
        float fx = x - i, fy = y - j;
        float h00 = heights[j * nx + i], h10 = heights[j * nx + i + 1], h01 = heights[(j + 1) * nx + i], h11 = heights[(j + 1) * nx + i + 1];
        return h00 * (1 - fx) * (1 - fy) + h10 * fx * (1 - fy) + h01 * (1 - fx) * fy + h11 * fx * fy;
    }

    public BoneWeight? Weight(float a, float b)
    {
        int c = Cell(Mathf.Clamp(a, -halfW, halfW - cell * 0.5f), Mathf.Clamp(b, -halfH, halfH - cell * 0.5f));
        if (c < 0 || boneIndices.Length != nx * ny * 4 || boneWeights.Length != nx * ny * 4) return null;
        int k = c * 4;
        return new BoneWeight
        {
            boneIndex0 = boneIndices[k], boneIndex1 = boneIndices[k + 1], boneIndex2 = boneIndices[k + 2], boneIndex3 = boneIndices[k + 3],
            weight0 = boneWeights[k], weight1 = boneWeights[k + 1], weight2 = boneWeights[k + 2], weight3 = boneWeights[k + 3],
        };
    }
}

#pragma warning disable CS0649 // filled by Json.NET
/// <summary>patch_spots.json beside the carrier's bundle (tools/patchfit/build_patch_spots.py).</summary>
internal sealed class SpotFile
{
    public int version;
    public float standoff = 0.0006f;
    public Dictionary<string, SpotRow> spots = new();
    public Dictionary<string, string> slots = new();
    public Dictionary<string, string> slotLabels = new();
    public string backSpot = "";
    /// <summary>Parts that must move as one solid piece (0.3.1, 2026-11-18 "Fix on the vests the phone being stretched"): the chest phone
    /// mounts were skinned with weights that vary from their top to their bottom (spine 3 from 0.42 to 0.83), so in the idle pose the
    /// mount, phone and patch on it were squashed and looked stretched wide. Each group gets one weight, the average of its main part.</summary>
    public List<RigidGroup> rigid = new();

    internal sealed class RigidGroup
    {
        public string main = "";
        public List<string> parts = new();
        public List<string> spots = new();
    }

    internal sealed class SpotRow
    {
        public float[] centre = new float[3], normal = new float[3], up = new float[3], right = new float[3];
        public float back, halfW, top, bottom;
        public bool drape;
        public SpotField field = new();
    }
}
#pragma warning restore CS0649

internal static class PatchDraw
{
    /// <summary>Draped patches: no edge longer than this once placed, so a straight edge never cuts under a curved sheet.</summary>
    public const float MaxEdge = 0.008f;
    public static float Standoff = 0.0006f;

    private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);

    /// <summary>Reads patch_spots.json and lays its spots over the bundle's (same names replace, new names add). Null when the file is
    /// missing or unreadable; the bundle's spots then stay as they are.</summary>
    public static SpotFile? LoadSpots(string folder, Dictionary<string, DrawSpot> spots, Action<string> log)
    {
        var path = Path.Combine(folder, "patch_spots.json");
        if (!File.Exists(path)) { log("patch_spots.json not found: patches use the spots in the bundle (no back patch)"); return null; }
        try
        {
            var file = JsonConvert.DeserializeObject<SpotFile>(File.ReadAllText(path));
            if (file == null || file.version != 1) { log("patch_spots.json has an unknown version: ignored"); return null; }
            foreach (var pair in file.spots)
            {
                var r = pair.Value;
                if (!r.field.Valid) { log($"patch_spots.json: spot {pair.Key} has no usable field: ignored"); continue; }
                spots[pair.Key] = new DrawSpot
                {
                    Centre = V(r.centre), Normal = V(r.normal).normalized, Up = V(r.up).normalized, Right = V(r.right).normalized,
                    Back = r.back, HalfW = r.halfW, Top = r.top, Bottom = r.bottom, Drape = r.drape, Field = r.field,
                    Weight = r.field.Weight(0f, 0f) ?? default,
                };
            }
            if (file.standoff > 0f && file.standoff < 0.005f) Standoff = file.standoff;
            log($"patch_spots.json: {file.spots.Count} spots ({string.Join(", ", file.spots.Keys)}), back spot '{file.backSpot}'");
            return file;
        }
        catch (Exception error) { log("patch_spots.json unreadable, the bundle's spots stay: " + error.Message); return null; }
    }

    /// <summary>
    /// A patch mesh (kit convention: art right -x, up +y, front +z, back at its lowest z) fitted onto a spot as a skinned mesh in the
    /// donor's bind space. The patch shrinks to the room (never grows) and slides down when taller than the room above the centre.
    /// Draped spots split long edges, then lay every vertex on the sheet plus the standoff and turn its normal with the sheet's slope;
    /// skin weights come from the surface under each vertex.
    /// </summary>
    public static Mesh Fit(Mesh source, DrawSpot s, Matrix4x4[] bindposes, string name, out string report)
    {
        var b = source.bounds;
        float scale = Mathf.Min(1f, Mathf.Min(2f * s.HalfW / Mathf.Max(b.size.x, 1e-4f), (s.Top + s.Bottom) / Mathf.Max(b.size.y, 1e-4f)));
        float half = b.size.y * scale * 0.5f;
        float offset = Mathf.Clamp(0f, -s.Bottom + half, s.Top - half);
        var verts = new List<Vector3>(source.vertices);
        var normals = new List<Vector3>(source.normals);
        var uv = new List<Vector2>(source.uv);
        while (normals.Count < verts.Count) normals.Add(Vector3.forward);
        while (uv.Count < verts.Count) uv.Add(Vector2.zero);
        var subs = Enumerable.Range(0, source.subMeshCount).Select(i => new List<int>(source.GetIndices(i))).ToList();
        bool drape = s.Drape && s.Field != null && s.Field.Valid;
        int before = verts.Count;
        if (drape) Subdivide(verts, normals, uv, subs, MaxEdge / Mathf.Max(scale, 1e-4f));
        float lift = 0f, under = float.MinValue;
        if (!drape)
        {
            // 0.2.x: the highest surface under the patch's footprint, plus 0.6 mm
            if (s.Field != null)
                for (int i = 0; i < verts.Count; i += Mathf.Max(1, verts.Count / 600))
                    under = Mathf.Max(under, s.Field.At((verts[i].x - b.center.x) * scale, (verts[i].y - b.center.y) * scale + offset));
            lift = (under > float.MinValue ? Mathf.Max(under, s.Back) : s.Back) + 0.0006f;
        }
        int n = verts.Count;
        var outV = new Vector3[n]; var outN = new Vector3[n]; var weights = new BoneWeight[n];
        float lowest = float.MaxValue, highest = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var v = verts[i];
            float a = (v.x - b.center.x) * scale, c = (v.y - b.center.y) * scale + offset, depth = (v.z - b.min.z) * scale;
            var nn = normals[i];
            float z;
            if (drape)
            {
                float h = s.Field!.Sheet(a, c);
                z = h + Standoff + depth;
                // the sheet's slope turns the normal (finite difference over 1 mm)
                float dx = (s.Field.Sheet(a + 0.001f, c) - s.Field.Sheet(a - 0.001f, c)) / 0.002f;
                float dy = (s.Field.Sheet(a, c + 0.001f) - s.Field.Sheet(a, c - 0.001f)) / 0.002f;
                nn = Quaternion.FromToRotation(Vector3.forward, new Vector3(-dx, -dy, 1f).normalized) * nn;
                lowest = Mathf.Min(lowest, h); highest = Mathf.Max(highest, h);
            }
            else z = lift + depth;
            // a = along Right (art right is -x on the patch, Right is the art's left as the viewer sees it), c = along Up, z = along Normal
            outV[i] = s.Centre + s.Right * a + s.Up * c + s.Normal * z;
            outN[i] = (s.Right * nn.x + s.Up * nn.y + s.Normal * nn.z).normalized;
            weights[i] = s.Rigid ?? s.Field?.Weight(a, c) ?? s.Weight;
        }
        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = outV; mesh.normals = outN; mesh.uv = uv.ToArray();
        mesh.subMeshCount = subs.Count;
        for (int i = 0; i < subs.Count; i++) mesh.SetTriangles(subs[i], i, false);
        mesh.boneWeights = weights;
        mesh.bindposes = bindposes;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(true);
        report = drape
            ? $"scale {scale * 100f:0}%, {b.size.x * scale * 1000f:0} x {b.size.y * scale * 1000f:0} mm, {offset * 1000f:0.0} mm up, laid on the surface ({(highest - lowest) * 1000f:0.0} mm of curve), {before} -> {n} vertices"
            : $"scale {scale * 100f:0}%, {b.size.x * scale * 1000f:0} x {b.size.y * scale * 1000f:0} mm, {offset * 1000f:0.0} mm up, back {lift * 1000f:0.0} mm from the spot (highest surface under it {(under > float.MinValue ? under * 1000f : float.NaN):0.0} mm)";
        return mesh;
    }

    /// <summary>One skin weight for a whole piece: the mean of every vertex's weights per bone, the four strongest kept, normalised.</summary>
    public static BoneWeight Average(BoneWeight[] weights)
    {
        var sum = new Dictionary<int, double>();
        void Add(int bone, float w) { if (w <= 0f) return; sum.TryGetValue(bone, out var s); sum[bone] = s + w; }
        foreach (var w in weights) { Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3); }
        var top = sum.OrderByDescending(p => p.Value).Take(4).ToList();
        double total = top.Sum(p => p.Value);
        if (total <= 0) return weights.Length > 0 ? weights[0] : default;
        float W(int k) => k < top.Count ? (float)(top[k].Value / total) : 0f;
        int B(int k) => k < top.Count ? top[k].Key : 0;
        return new BoneWeight { boneIndex0 = B(0), boneIndex1 = B(1), boneIndex2 = B(2), boneIndex3 = B(3), weight0 = W(0), weight1 = W(1), weight2 = W(2), weight3 = W(3) };
    }

    /// <summary>
    /// Applies the spot file's rigid groups: each group's parts (both LODs) and the patches drawn on its spots take one weight, the average of
    /// the group's main part at LOD0. `weightsOf(part, lod)` hands back that part's weight array (changed in place, before any carrier mesh
    /// is built from it). Logs what it did; a group naming a part the bundle does not have is skipped.
    /// </summary>
    public static void ApplyRigid(SpotFile file, Func<string, int, BoneWeight[]?> weightsOf, Dictionary<string, DrawSpot> spots, Action<string> log)
    {
        foreach (var g in file.rigid)
        {
            var main = weightsOf(g.main, 0);
            if (main == null || main.Length == 0) { log($"rigid group {g.main}: part not found, left as it is"); continue; }
            var one = Average(main);
            int parts = 0;
            foreach (var p in g.parts.Append(g.main).Distinct())
                for (int lod = 0; lod < 2; lod++)
                {
                    var w = weightsOf(p, lod);
                    if (w == null) continue;
                    for (int i = 0; i < w.Length; i++) w[i] = one;
                    if (lod == 0) parts++;
                }
            foreach (var s in g.spots) if (spots.TryGetValue(s, out var spot)) spot.Rigid = one;
            log($"rigid group {g.main}: {parts} part(s) and spot(s) {string.Join(", ", g.spots)} move as one piece (bones {one.boneIndex0}:{one.weight0:0.00} {one.boneIndex1}:{one.weight1:0.00} {one.boneIndex2}:{one.weight2:0.00} {one.boneIndex3}:{one.weight3:0.00})");
        }
    }

    /// <summary>Splits every edge longer than maxEdge (model units) at its middle; a triangle with 1, 2 or 3 split edges becomes 2, 3 or 4.
    /// Edges are keyed by their vertex pair, so both triangles of an edge agree (no cracks). Repeats until nothing is too long.</summary>
    public static void Subdivide(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<List<int>> subs, float maxEdge)
    {
        float lim = maxEdge * maxEdge;
        for (int pass = 0; pass < 8; pass++)
        {
            var mids = new Dictionary<long, int>();
            bool Long(int i, int j) => (v[i] - v[j]).sqrMagnitude > lim;
            long Key(int i, int j) => i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
            var marked = new HashSet<long>();
            foreach (var t in subs)
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    int a = t[k], b = t[k + 1], c = t[k + 2];
                    if (Long(a, b)) marked.Add(Key(a, b));
                    if (Long(b, c)) marked.Add(Key(b, c));
                    if (Long(c, a)) marked.Add(Key(c, a));
                }
            if (marked.Count == 0) return;
            int Mid(int i, int j)
            {
                long key = Key(i, j);
                if (mids.TryGetValue(key, out int m)) return m;
                v.Add((v[i] + v[j]) * 0.5f);
                n.Add((n[i] + n[j]).normalized);
                uv.Add((uv[i] + uv[j]) * 0.5f);
                mids[key] = v.Count - 1;
                return v.Count - 1;
            }
            for (int s = 0; s < subs.Count; s++)
            {
                var t = subs[s];
                var o = new List<int>(t.Count * 2);
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    int p = t[k], q = t[k + 1], r = t[k + 2];
                    bool e0 = marked.Contains(Key(p, q)), e1 = marked.Contains(Key(q, r)), e2 = marked.Contains(Key(r, p));
                    int count = (e0 ? 1 : 0) + (e1 ? 1 : 0) + (e2 ? 1 : 0);
                    if (count == 0) { o.Add(p); o.Add(q); o.Add(r); continue; }
                    // turn the triangle so its first split edge is (p, q); winding stays the same
                    while (!e0) { (p, q, r) = (q, r, p); (e0, e1, e2) = (e1, e2, e0); }
                    if (count == 1) { int m = Mid(p, q); o.AddRange(new[] { p, m, r, m, q, r }); }
                    else if (count == 3)
                    {
                        int m0 = Mid(p, q), m1 = Mid(q, r), m2 = Mid(r, p);
                        o.AddRange(new[] { p, m0, m2, m0, q, m1, m2, m1, r, m0, m1, m2 });
                    }
                    else if (e1) { int m0 = Mid(p, q), m1 = Mid(q, r); o.AddRange(new[] { p, m0, r, m0, q, m1, m0, m1, r }); }
                    else { int m0 = Mid(p, q), m2 = Mid(r, p); o.AddRange(new[] { p, m0, m2, m0, q, r, m0, r, m2 }); }
                }
                subs[s] = o;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------ the Slickster's patches
    private static object? slickster;
    private static bool slicksterTried;
    private static readonly Dictionary<string, Mesh?> slicksterMeshes = new();

    /// <summary>
    /// The Slickster's own four patches (Aggressive, Boar head, Hooter Bros, Tread On These Nuts) are drawn by that mod's rig mesh, so
    /// there is no kit patch file for them. Their loose meshes (readable, in the same Tasmanian Tiger SK bind space as our parts) are turned
    /// into body axes with the carrier's bindBase, turned to face +z (faces within 45 degrees of it give the facing), and their gentle bend
    /// (a quadratic through every vertex) is taken out so they lie flat like a kit patch. The material is the Slickster's own, made from
    /// our donor material. Null when the Slickster part is missing or changed; the patch then simply does not show.
    /// </summary>
    public static (Mesh mesh, Material[] mats)? SlicksterPatch(string tpl, Material donor, float[][] bindBase, Action<string, Exception> report)
    {
        try
        {
            if (!slicksterTried)
            {
                slicksterTried = true;
                var type = AccessTools.TypeByName("Slickster.Client.Plugin");
                slickster = type == null ? null : Traverse.Create(type).Field("instance").GetValue();
            }
            if (slickster == null) return null;
            var plugin = Traverse.Create(slickster);
            var row = (plugin.Field("patches").GetValue() as System.Collections.IDictionary)?[tpl];
            if (row == null) return null;
            string key = Traverse.Create(row).Field("key").GetValue<string>(), meshName = Traverse.Create(row).Field("mesh").GetValue<string>();
            if (!slicksterMeshes.TryGetValue(tpl, out var mesh))
            {
                var loose = (plugin.Field("meshes").GetValue() as System.Collections.IDictionary)?[meshName] as Mesh;
                mesh = loose != null && loose.isReadable && bindBase.Length >= 3 ? Level(loose, bindBase, "Slickster_level_" + key) : null;
                slicksterMeshes[tpl] = mesh;
            }
            if (mesh == null) return null;
            var material = AccessTools.Method(slickster.GetType(), "MakeMaterial")?.Invoke(slickster, new object[] { donor, key }) as Material;
            return material == null ? null : (mesh, new[] { material });
        }
        catch (Exception error) { report("reading the Slickster's patches", error); return null; }
    }

    private static Mesh Level(Mesh loose, float[][] bindBase, string name)
    {
        var rot = new Matrix4x4();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) rot[r, c] = bindBase[r][c];
        rot[3, 3] = 1f;
        var v = loose.vertices.Select(p => rot.MultiplyVector(p)).ToArray();
        var nrm = loose.normals.Select(p => rot.MultiplyVector(p)).ToArray();
        var tris = Enumerable.Range(0, loose.subMeshCount).SelectMany(s => loose.GetIndices(s)).ToArray();
        var sum = Vector3.zero;
        for (int k = 0; k + 2 < tris.Length; k += 3)
        {
            var cr = Vector3.Cross(v[tris[k + 1]] - v[tris[k]], v[tris[k + 2]] - v[tris[k]]);
            if (cr.z > 0.7f * cr.magnitude) sum += cr;
        }
        var turn = Quaternion.FromToRotation(sum.sqrMagnitude > 0f ? sum.normalized : Vector3.forward, Vector3.forward);
        for (int i = 0; i < v.Length; i++) { v[i] = turn * v[i]; if (i < nrm.Length) nrm[i] = turn * nrm[i]; }
        // least-squares quadratic z = c0 + c1 x + c2 y + c3 x^2 + c4 xy + c5 y^2 over every vertex, then take the bend out
        double mx = v.Average(p => p.x), my = v.Average(p => p.y);
        var ata = new double[6, 6]; var atz = new double[6];
        foreach (var p in v)
        {
            double x = p.x - mx, y = p.y - my;
            var row = new[] { 1, x, y, x * x, x * y, y * y };
            for (int r = 0; r < 6; r++) { atz[r] += row[r] * p.z; for (int c = 0; c < 6; c++) ata[r, c] += row[r] * row[c]; }
        }
        var coef = Solve(ata, atz);
        if (coef != null)
            for (int i = 0; i < v.Length; i++)
            {
                double x = v[i].x - mx, y = v[i].y - my;
                double bend = coef[1] * x + coef[2] * y + coef[3] * x * x + coef[4] * x * y + coef[5] * y * y;
                v[i].z -= (float)bend;
            }
        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = v; mesh.normals = nrm; mesh.uv = loose.uv;
        mesh.subMeshCount = loose.subMeshCount;
        for (int s = 0; s < loose.subMeshCount; s++) mesh.SetTriangles(loose.GetIndices(s), s, false);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(false);   // stays readable: Fit() reads it
        return mesh;
    }

    private static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone(); var x = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-18) return null;
            if (piv != col) { for (int c = 0; c < n; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]); (x[col], x[piv]) = (x[piv], x[col]); }
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (int c = col; c < n; c++) m[r, c] -= f * m[col, c];
                x[r] -= f * x[col];
            }
        }
        for (int r = 0; r < n; r++) x[r] /= m[r, r];
        return x;
    }
}
