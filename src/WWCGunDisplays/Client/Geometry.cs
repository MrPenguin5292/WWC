using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WWCGunDisplays.Client;

/// <summary>
/// Builds display meshes in code, so every board, hook and shadow box is made to the size of the guns on it.
/// Axes are the hideout poster spot's: +x is the viewer's left seen from the room, +y up, +z out of the wall; the wall is z = 0.
/// Parts: 0 wood, 1 felt, 2 steel, 3 rubber, 4 brass, 5 glass, 6 light glow, 7 the face of an engraved name plate. Every part but glass and glow is written double-sided
/// (each triangle again with reversed winding and flipped normals); those two use shaders that already draw both sides.
/// </summary>
internal sealed class MeshBuilder
{
    public const int Wood = 0, Felt = 1, Steel = 2, Rubber = 3, Brass = 4, Glass = 5, Glow = 6, Plate = 7, PartCount = 8;
    public const float Tile = 0.5f;          // metres per repeat of a tiling texture
    private static readonly float Smooth = Mathf.Cos(38f * Mathf.Deg2Rad);
    private readonly List<Vector3> v = new();
    private readonly List<Vector3> n = new();
    private readonly List<Vector2> uv = new();
    private readonly List<int>[] tris = Enumerable.Range(0, PartCount).Select(_ => new List<int>()).ToArray();

    /// <summary>A flat polygon (3 or 4 corners) wound so it faces along <paramref name="facing"/>. Unity draws a triangle from the side its edge cross product points to.</summary>
    public void Face(int part, Vector3[] p, Vector3[] normals, Vector2[] uvs, Vector3 facing)
    {
        int b = v.Count;
        for (int i = 0; i < p.Length; i++) { v.Add(p[i]); n.Add(normals[i].normalized); uv.Add(uvs[i]); }
        void Tri(int a, int c, int d)
        {
            var cross = Vector3.Cross(p[c] - p[a], p[d] - p[a]);
            if (Vector3.Dot(cross, facing) >= 0f) { tris[part].Add(b + a); tris[part].Add(b + c); tris[part].Add(b + d); }
            else { tris[part].Add(b + a); tris[part].Add(b + d); tris[part].Add(b + c); }
        }
        Tri(0, 1, 2);
        if (p.Length == 4) Tri(0, 2, 3);
    }

    /// <summary>Texture coordinates for a face in metres, with the grain (texture V) running along <paramref name="grain"/>.</summary>
    public static Vector2 Planar(Vector3 p, Vector3 normal, Vector3 grain)
    {
        var across = Vector3.Cross(normal, grain);
        if (across.sqrMagnitude < 1e-6f) { grain = Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right; across = Vector3.Cross(normal, grain); }
        across.Normalize();
        var along = Vector3.Cross(across, normal).normalized;
        return new Vector2(Vector3.Dot(p, across) / Tile + 0.5f, Vector3.Dot(p, along) / Tile + 0.5f);
    }

    public void Quad(int part, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector3 grain)
    {
        var pts = new[] { a, b, c, d };
        Face(part, pts, new[] { normal, normal, normal, normal }, pts.Select(p => Planar(p, normal, grain)).ToArray(), normal);
    }

    public void Box(int part, Vector3 lo, Vector3 hi, Vector3 grain)
    {
        float x0 = lo.x, y0 = lo.y, z0 = lo.z, x1 = hi.x, y1 = hi.y, z1 = hi.z;
        Quad(part, new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), Vector3.forward, grain);
        Quad(part, new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0), Vector3.back, grain);
        Quad(part, new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), Vector3.up, grain);
        Quad(part, new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), Vector3.down, grain);
        Quad(part, new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1), Vector3.right, grain);
        Quad(part, new(x0, y0, z0), new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), Vector3.left, grain);
    }

    /// <summary>
    /// A moulding round a rectangle (centre cx, cy; size w x h) standing on z = z0: profile points are (inset, height), inset growing into the material.
    /// Corners come out mitred and each side's grain runs along it. capTop fills the last ring; capBack closes the outline at z0 against the wall.
    /// </summary>
    public void Loft(int part, float cx, float cy, float w, float h, IList<Vector2> profile, float z0, bool capTop, bool capBack)
    {
        Vector2[] Ring(float inset) { float a = w / 2 - inset, b = h / 2 - inset; return new[] { new Vector2(cx - a, cy - b), new Vector2(cx + a, cy - b), new Vector2(cx + a, cy + b), new Vector2(cx - a, cy + b) }; }
        var outward = new[] { new Vector3(0, -1, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(-1, 0, 0) };
        var grains = new[] { Vector3.right, Vector3.up, Vector3.right, Vector3.up };
        int count = profile.Count;
        var segNormal = new Vector2[count - 1];
        for (int k = 0; k < count - 1; k++)
        {
            var d = profile[k + 1] - profile[k];
            segNormal[k] = d.sqrMagnitude < 1e-12f ? Vector2.zero : new Vector2(-d.y, d.x).normalized;
        }
        Vector2 PointNormal(int k, bool atStart)
        {
            var own = segNormal[k];
            int other = atStart ? k - 1 : k + 1;
            if (other < 0 || other >= segNormal.Length || segNormal[other] == Vector2.zero) return own;
            return Vector2.Dot(own, segNormal[other]) > Smooth ? (own + segNormal[other]).normalized : own;
        }
        for (int k = 0; k < count - 1; k++)
        {
            if (segNormal[k] == Vector2.zero) continue;
            var r0 = Ring(profile[k].x); var r1 = Ring(profile[k + 1].x);
            float h0 = z0 + profile[k].y, h1 = z0 + profile[k + 1].y;
            var na = PointNormal(k, true); var nb = PointNormal(k, false);
            for (int j = 0; j < 4; j++)
            {
                int jj = (j + 1) % 4;
                var o = outward[j];
                Vector3 N(Vector2 l) => (-l.x * o + l.y * Vector3.forward).normalized;
                var face = N(segNormal[k]);
                var pts = new[] { new Vector3(r0[j].x, r0[j].y, h0), new Vector3(r0[jj].x, r0[jj].y, h0), new Vector3(r1[jj].x, r1[jj].y, h1), new Vector3(r1[j].x, r1[j].y, h1) };
                Face(part, pts, new[] { N(na), N(na), N(nb), N(nb) }, pts.Select(p => Planar(p, face, grains[j])).ToArray(), face);
            }
        }
        if (capTop)
        {
            var r = Ring(profile[count - 1].x); float z = z0 + profile[count - 1].y;
            Quad(part, new(r[0].x, r[0].y, z), new(r[1].x, r[1].y, z), new(r[2].x, r[2].y, z), new(r[3].x, r[3].y, z), Vector3.forward, w >= h ? Vector3.right : Vector3.up);
        }
        if (capBack)
        {
            var r = Ring(profile[0].x);
            Quad(part, new(r[0].x, r[0].y, z0), new(r[1].x, r[1].y, z0), new(r[2].x, r[2].y, z0), new(r[3].x, r[3].y, z0), Vector3.back, w >= h ? Vector3.right : Vector3.up);
        }
    }

    /// <summary>A round bar swept along a path (bends included), with flat or no end caps. Its grain runs along the bar.</summary>
    public void Tube(int part, IList<Vector3> path, float radius, int sides = 14, bool capStart = true, bool capEnd = true)
    {
        int m = path.Count;
        if (m < 2) return;
        var tangents = new Vector3[m];
        for (int i = 0; i < m; i++)
        {
            var a = path[Mathf.Max(0, i - 1)]; var b = path[Mathf.Min(m - 1, i + 1)];
            tangents[i] = (b - a).normalized;
        }
        // One reference direction carried along the path keeps the rings from twisting round the bends.
        var refDir = Mathf.Abs(Vector3.Dot(tangents[0], Vector3.up)) < 0.9f ? Vector3.up : Vector3.right;
        var rings = new Vector3[m][];
        var ringNormals = new Vector3[m][];
        var side = Vector3.Cross(tangents[0], refDir).normalized;
        float length = 0f; var dist = new float[m];
        for (int i = 0; i < m; i++)
        {
            if (i > 0) { length += Vector3.Distance(path[i - 1], path[i]); side = (side - Vector3.Dot(side, tangents[i]) * tangents[i]).normalized; }
            dist[i] = length;
            var up = Vector3.Cross(side, tangents[i]).normalized;
            rings[i] = new Vector3[sides]; ringNormals[i] = new Vector3[sides];
            for (int s = 0; s < sides; s++)
            {
                float ang = 2 * Mathf.PI * s / sides;
                var dir = side * Mathf.Cos(ang) + up * Mathf.Sin(ang);
                ringNormals[i][s] = dir; rings[i][s] = path[i] + dir * radius;
            }
        }
        float around = 2 * Mathf.PI * radius;
        for (int i = 0; i < m - 1; i++)
            for (int s = 0; s < sides; s++)
            {
                int ss = (s + 1) % sides;
                var pts = new[] { rings[i][s], rings[i][ss], rings[i + 1][ss], rings[i + 1][s] };
                var nrm = new[] { ringNormals[i][s], ringNormals[i][ss], ringNormals[i + 1][ss], ringNormals[i + 1][s] };
                float u0 = around * s / sides / Tile, u1 = around * (s + 1) / sides / Tile;
                var uvs = new[] { new Vector2(u0, dist[i] / Tile), new Vector2(u1, dist[i] / Tile), new Vector2(u1, dist[i + 1] / Tile), new Vector2(u0, dist[i + 1] / Tile) };
                Face(part, pts, nrm, uvs, (nrm[0] + nrm[1] + nrm[2] + nrm[3]).normalized);
            }
        void Cap(int i, Vector3 facing)
        {
            for (int s = 0; s < sides; s++)
            {
                int ss = (s + 1) % sides;
                var pts = new[] { path[i], rings[i][s], rings[i][ss] };
                Face(part, pts, new[] { facing, facing, facing }, pts.Select(p => Planar(p, facing, Vector3.up)).ToArray(), facing);
            }
        }
        if (capStart) Cap(0, -tangents[0]);
        if (capEnd) Cap(m - 1, tangents[m - 1]);
    }

    public void Sphere(int part, Vector3 centre, float radius, int rings = 6, int sides = 12)
    {
        for (int r = 0; r < rings; r++)
        {
            float a0 = Mathf.PI * r / rings - Mathf.PI / 2, a1 = Mathf.PI * (r + 1) / rings - Mathf.PI / 2;
            for (int s = 0; s < sides; s++)
            {
                float b0 = 2 * Mathf.PI * s / sides, b1 = 2 * Mathf.PI * (s + 1) / sides;
                Vector3 D(float a, float b) => new(Mathf.Cos(a) * Mathf.Cos(b), Mathf.Sin(a), Mathf.Cos(a) * Mathf.Sin(b));
                var d = new[] { D(a0, b0), D(a0, b1), D(a1, b1), D(a1, b0) };
                var pts = d.Select(x => centre + x * radius).ToArray();
                var uvs = d.Select(x => new Vector2(x.x * 0.5f + 0.5f, x.y * 0.5f + 0.5f)).ToArray();
                if (r == 0) Face(part, new[] { pts[0], pts[2], pts[3] }, new[] { d[0], d[2], d[3] }, new[] { uvs[0], uvs[2], uvs[3] }, (d[2] + d[3]).normalized);
                else if (r == rings - 1) Face(part, new[] { pts[0], pts[1], pts[2] }, new[] { d[0], d[1], d[2] }, new[] { uvs[0], uvs[1], uvs[2] }, (d[0] + d[1]).normalized);
                else Face(part, pts, d, uvs, (d[0] + d[1] + d[2] + d[3]).normalized);
            }
        }
    }

    /// <summary>
    /// A turned part: a (radius, height) profile spun round an axis through <paramref name="centre"/>. Used for rosettes, screw heads, collars and finials.
    /// The profile runs from the axis outward and back (or along); neighbouring segments within 38 degrees are shaded smooth.
    /// </summary>
    public void Lathe(int part, Vector3 centre, Vector3 axis, IList<Vector2> profile, int segments = 20)
    {
        axis.Normalize();
        var u = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized;
        var w = Vector3.Cross(axis, u).normalized;
        int count = profile.Count;
        var seg = new Vector2[count - 1];
        for (int k = 0; k < count - 1; k++)
        {
            var d = profile[k + 1] - profile[k];
            // In (radius, height) space the outside of the part is to the right of the direction of travel.
            seg[k] = d.sqrMagnitude < 1e-12f ? Vector2.zero : new Vector2(d.y, -d.x).normalized;
        }
        Vector2 At(int k, bool start)
        {
            var own = seg[k]; int o = start ? k - 1 : k + 1;
            if (o < 0 || o >= seg.Length || seg[o] == Vector2.zero) return own;
            return Vector2.Dot(own, seg[o]) > Smooth ? (own + seg[o]).normalized : own;
        }
        for (int k = 0; k < count - 1; k++)
        {
            if (seg[k] == Vector2.zero) continue;
            var na = At(k, true); var nb = At(k, false);
            for (int s = 0; s < segments; s++)
            {
                float a0 = 2 * Mathf.PI * s / segments, a1 = 2 * Mathf.PI * (s + 1) / segments;
                Vector3 R(float a) => u * Mathf.Cos(a) + w * Mathf.Sin(a);
                Vector3 P(Vector2 q, float a) => centre + R(a) * q.x + axis * q.y;
                Vector3 N(Vector2 q, float a) => (R(a) * q.x + axis * q.y).normalized;
                var pts = new[] { P(profile[k], a0), P(profile[k], a1), P(profile[k + 1], a1), P(profile[k + 1], a0) };
                var nrm = new[] { N(na, a0), N(na, a1), N(nb, a1), N(nb, a0) };
                var uvs = new[] { new Vector2(a0 / (2 * Mathf.PI), profile[k].y / Tile), new Vector2(a1 / (2 * Mathf.PI), profile[k].y / Tile), new Vector2(a1 / (2 * Mathf.PI), profile[k + 1].y / Tile), new Vector2(a0 / (2 * Mathf.PI), profile[k + 1].y / Tile) };
                var facing = N(seg[k], (a0 + a1) / 2);
                if (profile[k].x < 1e-6f) Face(part, new[] { pts[0], pts[2], pts[3] }, new[] { nrm[0], nrm[2], nrm[3] }, new[] { uvs[0], uvs[2], uvs[3] }, facing);
                else if (profile[k + 1].x < 1e-6f) Face(part, new[] { pts[0], pts[1], pts[2] }, new[] { nrm[0], nrm[1], nrm[2] }, new[] { uvs[0], uvs[1], uvs[2] }, facing);
                else Face(part, pts, nrm, uvs, facing);
            }
        }
    }

    /// <summary>A flat slab on z = z0 with its front edges rounded over by <paramref name="round"/>: back plates, magnet pads, name plates.</summary>
    public void Slab(int part, float cx, float cy, float w, float h, float z0, float thickness, float round, bool capBack = true)
    {
        round = Mathf.Min(round, thickness * 0.95f, Mathf.Min(w, h) * 0.45f);
        var profile = new List<Vector2> { new(0f, 0f), new(0f, thickness - round) };
        for (int a = 1; a <= 4; a++) { float t = a / 4f * Mathf.PI / 2; profile.Add(new Vector2(round * (1 - Mathf.Cos(t)), thickness - round + round * Mathf.Sin(t))); }
        Loft(part, cx, cy, w, h, profile, z0, true, capBack);
    }

    /// <summary>
    /// A moulding running along x: a closed (z, y) profile swept from x0 to x1 with flat ends, its grain along x.
    /// The ends are fanned from <paramref name="hub"/>, so the profile must be star-shaped round that point (true for cornices and aprons built here).
    /// </summary>
    public void ExtrudeX(int part, IList<Vector2> profileZY, float x0, float x1, Vector2 hub)
    {
        int m = profileZY.Count;
        // Wind the profile so its outside is to the right of travel, the same rule as the loft.
        float area = 0f;
        for (int i = 0; i < m; i++) { var a = profileZY[i]; var b = profileZY[(i + 1) % m]; area += a.x * b.y - b.x * a.y; }
        var p = area > 0 ? profileZY.ToList() : profileZY.Reverse().ToList();     // anticlockwise in (z, y): the outside is on the right
        var segN = new Vector2[m];
        for (int i = 0; i < m; i++) { var d = p[(i + 1) % m] - p[i]; segN[i] = d.sqrMagnitude < 1e-12f ? Vector2.zero : new Vector2(d.y, -d.x).normalized; }
        Vector2 At(int i, bool start)
        {
            var own = segN[i]; var o = segN[start ? (i - 1 + m) % m : (i + 1) % m];
            return o != Vector2.zero && Vector2.Dot(own, o) > Smooth ? (own + o).normalized : own;
        }
        for (int i = 0; i < m; i++)
        {
            if (segN[i] == Vector2.zero) continue;
            var a = p[i]; var b = p[(i + 1) % m];
            Vector3 N(Vector2 q) => new Vector3(0f, q.y, q.x).normalized;
            var face = N(segN[i]);
            var pts = new[] { new Vector3(x0, a.y, a.x), new Vector3(x1, a.y, a.x), new Vector3(x1, b.y, b.x), new Vector3(x0, b.y, b.x) };
            var na = N(At(i, true)); var nb = N(At(i, false));
            Face(part, pts, new[] { na, na, nb, nb }, pts.Select(q => Planar(q, face, Vector3.right)).ToArray(), face);
        }
        foreach (var (x, f) in new[] { (x0, Vector3.left), (x1, Vector3.right) })
            for (int i = 0; i < m; i++)
            {
                var a = p[i]; var b = p[(i + 1) % m];
                var pts = new[] { new Vector3(x, hub.y, hub.x), new Vector3(x, a.y, a.x), new Vector3(x, b.y, b.x) };
                Face(part, pts, new[] { f, f, f }, pts.Select(q => Planar(q, f, Vector3.up)).ToArray(), f);
            }
    }

    /// <summary>The finished mesh. Its bounds are balanced round the origin, which keeps the origin on the poster spot when the hideout centres the model.</summary>
    public Mesh Build(string name)
    {
        var verts = new List<Vector3>(v); var normals = new List<Vector3>(n); var uvs = new List<Vector2>(uv);
        var parts = new List<int>[PartCount];
        for (int p = 0; p < PartCount; p++)
        {
            parts[p] = new List<int>(tris[p]);
            if (p == Glass || p == Glow) continue;
            var t = tris[p];
            for (int i = 0; i < t.Count; i += 3)
            {
                int b = verts.Count;
                for (int k = 0; k < 3; k++) { verts.Add(v[t[i + k]]); normals.Add(-n[t[i + k]]); uvs.Add(uv[t[i + k]]); }
                parts[p].Add(b); parts[p].Add(b + 2); parts[p].Add(b + 1);
            }
        }
        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.DontUnloadUnusedAsset };
        mesh.SetVertices(verts); mesh.SetNormals(normals); mesh.SetUVs(0, uvs);
        mesh.subMeshCount = PartCount;
        for (int p = 0; p < PartCount; p++) mesh.SetTriangles(parts[p], p, false);
        mesh.RecalculateTangents();
        float ex = 0.01f, ey = 0.01f, ez = 0.01f;
        foreach (var q in verts) { ex = Mathf.Max(ex, Mathf.Abs(q.x)); ey = Mathf.Max(ey, Mathf.Abs(q.y)); ez = Mathf.Max(ez, Mathf.Abs(q.z)); }
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(ex * 2f + 0.01f, ey * 2f + 0.01f, ez * 2f + 0.01f));
        mesh.UploadMeshData(false);
        return mesh;
    }
}

/// <summary>What a gun looks like from the room, measured once it is turned the way it hangs: its box, and its outline column by column.</summary>
internal sealed class GunShape
{
    public Bounds Box;                        // in the display's axes, with the gun's own origin at the holder's origin
    public float[] Bottom = Array.Empty<float>(), Top = Array.Empty<float>();
    public float ColumnX0, Step;              // column i is centred at x = ColumnX0 - (i + 0.5) * Step (column 0 on the viewer's left, +x)
    public bool MuzzleTowardMinusX = true;
    public Vector2 Centroid;                  // middle of the solid outline, in x and y
    /// <summary>The gun's back (z) over a patch of its side centred at a point (x, y), given the patch's half width and half height; null if nothing is there.</summary>
    public Func<Vector2, float, float, float?>? BackAt;
    public int Columns => Bottom.Length;
    public float X(int i) => ColumnX0 - (i + 0.5f) * Step;
    /// <summary>0 at the butt, 1 at the muzzle.</summary>
    public float T(int i) { float f = (i + 0.5f) / Mathf.Max(1, Columns); return MuzzleTowardMinusX ? f : 1f - f; }
    public float Length => Box.size.x;
    public float Height => Box.size.y;
    public float Depth => Box.size.z;
    public bool Short => Box.size.x < Layouts.ShortGun;

    public int ColumnAt(float x) => Mathf.Clamp(Mathf.FloorToInt((ColumnX0 - x) / Mathf.Max(Step, 1e-5f)), 0, Mathf.Max(0, Columns - 1));
}

/// <summary>A light a display carries: a spot (with a direction) or a point (direction zero), in the display's axes.</summary>
internal sealed class LightSpec
{
    public Vector3 Pos, Dir;
    public float Angle, Range, Intensity;
}

/// <summary>Where each gun, hook, pad, plate and light goes on a display. Built from the guns' shapes; also drawn into the mesh.</summary>
internal sealed class Layout
{
    public sealed class Seat
    {
        public Vector3 GunOffset;             // added to the gun holder's position, so the gun's box lands where it should
        public readonly List<Vector3> Contacts = new();   // where a hook's sleeve top or a pad face meets the gun
        public bool OnPad;
    }

    public readonly List<Seat> Seats = new();
    public readonly List<LightSpec> Lights = new();
    public Vector3 Size;                      // width, height, depth of the board or box
    public Bounds Extent;                     // everything, guns and light included: what the look ray can hit
    public Vector2 PlateSize;                 // the engraved face of the shadow box's name plate, zero when there is none
    public Mesh Mesh = null!;
}

internal static class Layouts
{
    public const float ShortGun = 0.42f;      // guns shorter than this sit on a magnet pad instead of two hooks
    private const float BoardT = 0.022f, GunGap = 0.014f, Sleeve = 0.0072f, HookBend = 0.011f, TipRise = 0.009f;

    // Board edge, from the wall up the side and over the top: a square step, an ogee (cove into a round-over), a fillet, then the face.
    // A routed bead runs in from the edge, and a line of brass stringing is set into the face just inside it.
    private static readonly Vector2[] BoardEdge =
    {
        new(0f, 0f), new(0f, 0.0085f), new(0.0016f, 0.0085f), new(0.0016f, 0.0098f),
        new(0.0026f, 0.0118f), new(0.0042f, 0.0131f), new(0.0062f, 0.0139f), new(0.0080f, 0.0151f), new(0.0094f, 0.0172f), new(0.0104f, 0.0196f), new(0.0112f, 0.0208f),
        new(0.0128f, 0.0208f), new(0.0128f, BoardT),
        new(0.024f, BoardT), new(0.0252f, BoardT - 0.0016f), new(0.0264f, BoardT - 0.0022f), new(0.0276f, BoardT - 0.0016f), new(0.0288f, BoardT),
    };

    private static void Board(MeshBuilder mb, float w, float h, bool triple)
    {
        mb.Loft(MeshBuilder.Wood, 0f, 0f, w, h, BoardEdge, 0f, true, true);
        // Brass stringing: a 2.2 mm line laid a hair proud of the face, round the panel.
        mb.Loft(MeshBuilder.Brass, 0f, 0f, w, h, new[] { new Vector2(0.0362f, BoardT + 0.00015f), new Vector2(0.0384f, BoardT + 0.00015f) }, 0f, false, false);
        // Wood plugs over the mounting screws.
        var plugs = triple
            ? new[] { new Vector2(w / 2 - 0.05f, h / 2 - 0.06f), new Vector2(-w / 2 + 0.05f, h / 2 - 0.06f), new Vector2(w / 2 - 0.05f, -h / 2 + 0.05f), new Vector2(-w / 2 + 0.05f, -h / 2 + 0.05f) }
            : new[] { new Vector2(w / 2 - 0.052f, 0f), new Vector2(-w / 2 + 0.052f, 0f) };
        var plug = new[] { new Vector2(0.0062f, 0f), new Vector2(0.0062f, 0.0004f), new Vector2(0.0052f, 0.0013f), new Vector2(0.0034f, 0.0019f), new Vector2(0f, 0.0021f) };
        foreach (var p in plugs) mb.Lathe(MeshBuilder.Wood, new Vector3(p.x, p.y, BoardT), Vector3.forward, plug, 18);
        if (!triple) return;
        // A cornice along the top and an apron under the bottom, returned square at their ends.
        float yb = h / 2 - 0.006f;
        var cornice = new[]
        {
            new Vector2(0f, yb), new Vector2(0.0236f, yb), new Vector2(0.0262f, yb + 0.0026f), new Vector2(0.0262f, yb + 0.0052f), new Vector2(0.0248f, yb + 0.0068f),
            new Vector2(0.0262f, yb + 0.0108f), new Vector2(0.0296f, yb + 0.0168f), new Vector2(0.0344f, yb + 0.0222f), new Vector2(0.0408f, yb + 0.0262f), new Vector2(0.0462f, yb + 0.0278f),
            new Vector2(0.0478f, yb + 0.0292f), new Vector2(0.0478f, yb + 0.0362f), new Vector2(0.0462f, yb + 0.0378f), new Vector2(0f, yb + 0.0378f),
        };
        mb.ExtrudeX(MeshBuilder.Wood, cornice, -w / 2 - 0.016f, w / 2 + 0.016f, new Vector2(0.012f, yb + 0.019f));
        float yt = -h / 2 + 0.006f;
        var apron = new[]
        {
            new Vector2(0f, yt), new Vector2(0.0262f, yt), new Vector2(0.0296f, yt - 0.0034f), new Vector2(0.0296f, yt - 0.0078f), new Vector2(0.0262f, yt - 0.0112f),
            new Vector2(0.0214f, yt - 0.0136f), new Vector2(0.0150f, yt - 0.0186f), new Vector2(0.0104f, yt - 0.0246f), new Vector2(0f, yt - 0.0266f),
        };
        mb.ExtrudeX(MeshBuilder.Wood, apron, -w / 2 + 0.03f, w / 2 - 0.03f, new Vector2(0.008f, yt - 0.009f));
    }

    public static Layout Hanger(IList<GunShape?> guns, bool triple, string name)
    {
        var mb = new MeshBuilder();
        var layout = new Layout();
        var shapes = guns.ToList();
        float maxLen = shapes.Where(g => g != null).Select(g => g!.Length).DefaultIfEmpty(0f).Max();
        float boardW = Mathf.Max(triple ? 0.62f : 0.46f, maxLen + 0.14f);
        if (shapes.All(g => g == null)) boardW = triple ? 0.86f : 0.9f;
        // Heights: one band per gun, sized to the gun, top to bottom.
        var bands = shapes.Select(g => g == null ? 0.2f : Mathf.Max(triple ? 0.19f : 0.15f, g.Height + (triple ? 0.07f : 0.02f))).ToList();
        float boardH = triple ? Mathf.Max(0.64f, bands.Sum() + 0.1f) : 0.15f;
        float gunBack = BoardT + GunGap;
        Board(mb, boardW, boardH, triple);
        float top = triple ? boardH / 2 - 0.05f - (boardH - 0.1f - bands.Sum()) / 2 : 0f;
        float highest = -1f, lowest = -boardH / 2 - (triple ? 0.03f : 0f), frontMost = gunBack + 0.06f;
        float gunTop = float.NegativeInfinity, gunBottom = float.PositiveInfinity;
        var placed = new List<(GunShape g, Vector3 offset)>();
        for (int i = 0; i < shapes.Count; i++)
        {
            var g = shapes[i];
            float bandMid = triple ? top - bands.Take(i).Sum() - bands[i] / 2 : 0f;
            var seat = new Layout.Seat();
            if (g == null)
            {
                // An empty spot shows its two hooks, so you can see where a gun goes.
                float y = bandMid - 0.015f;
                foreach (var x in new[] { boardW * 0.27f, -boardW * 0.27f }) Hook(mb, x, y, gunBack, gunBack + 0.06f);
                layout.Seats.Add(seat);
                highest = Mathf.Max(highest, bandMid + 0.04f);
                continue;
            }
            float cx = -(g.Box.center.x);
            float gx(int col) => g.X(col) + cx;
            float z = gunBack - g.Box.min.z;
            float front = gunBack + g.Depth;
            frontMost = Mathf.Max(frontMost, front);
            if (g.Short)
            {
                // A handgun sits flat on a rubber-coated magnet pad behind its middle.
                float y = bandMid - g.Centroid.y;
                seat.GunOffset = new Vector3(cx, y, z);
                seat.OnPad = true;
                // The pad's face goes to the gun's real back there, which can sit a little forward of the gun's overall back (a slide stop, a sight).
                float back = g.BackAt?.Invoke(g.Centroid, 0.034f, 0.017f) ?? g.Box.min.z;
                var pad = new Vector3(g.Centroid.x + cx, bandMid, back + z);
                Pad(mb, pad);
                seat.Contacts.Add(pad);
            }
            else
            {
                var (rear, fore) = HookColumns(g);
                float contactRear = Contact(g, rear), contactFore = Contact(g, fore);
                // On the single board the hook contacts sit a little under its middle line, so the gun's weight looks carried by the board;
                // on the triple each gun is centred in its own band so neighbours never overlap, and the hooks reach up to it.
                float y = triple ? bandMid - g.Box.center.y : bandMid - 0.012f - (contactRear + contactFore) / 2f;
                if (!triple) y = Mathf.Max(y, -boardH / 2 + 0.045f - Mathf.Min(contactRear, contactFore));
                seat.GunOffset = new Vector3(cx, y, z);
                foreach (var (col, c) in new[] { (rear, contactRear), (fore, contactFore) })
                {
                    var p = new Vector3(gx(col), c + y, gunBack);
                    Hook(mb, p.x, p.y, gunBack, front);
                    seat.Contacts.Add(p);
                }
            }
            placed.Add((g, seat.GunOffset));
            highest = Mathf.Max(highest, g.Box.max.y + seat.GunOffset.y);
            lowest = Mathf.Min(lowest, g.Box.min.y + seat.GunOffset.y);
            gunTop = Mathf.Max(gunTop, g.Box.max.y + seat.GunOffset.y);
            gunBottom = Mathf.Min(gunBottom, g.Box.min.y + seat.GunOffset.y);
            layout.Seats.Add(seat);
        }
        // A brass picture light over the board, its arm rising behind the guns and reaching out over them.
        float lightY = Mathf.Max(highest, boardH / 2 + (triple ? 0.032f : 0f)) + 0.08f;
        float shadeZ = frontMost + 0.035f;
        float shadeLen = Mathf.Clamp(boardW * 0.46f, 0.24f, 0.62f);
        var (armX, plateY, plateOnWall) = LightArm(placed, shadeLen, boardH, triple);
        PictureLight(mb, lightY, shadeZ, shadeLen, plateY, triple ? 0.06f : BoardT + 0.0092f, armX, plateOnWall);
        // Its lamp throws a wide spot down onto the guns, with a little spill lighting the board round them.
        if (float.IsInfinity(gunTop)) { gunTop = boardH / 2; gunBottom = -boardH / 2; }
        var lamp = new Vector3(0f, lightY - 0.012f, shadeZ - 0.004f);
        var target = new Vector3(0f, (gunTop + gunBottom) * 0.5f, gunBack + 0.03f);
        float reach = Vector3.Distance(lamp, target);
        float half = Mathf.Max(boardW, gunTop - gunBottom) * 0.55f;
        layout.Lights.Add(new LightSpec { Pos = lamp, Dir = (target - lamp).normalized, Angle = Mathf.Clamp(2f * Mathf.Atan2(half, reach) * Mathf.Rad2Deg, 70f, 150f), Range = reach * 2.4f + 0.3f, Intensity = triple ? 2.2f : 1.8f });
        layout.Lights.Add(new LightSpec { Pos = lamp + new Vector3(0f, -0.03f, 0.12f), Range = Mathf.Max(0.8f, boardW * 0.9f), Intensity = 0.35f });
        // The triple is tall: a soft fill in front of its lower half keeps the bottom guns from sinking into the dark under the lamp's pool.
        if (triple) layout.Lights.Add(new LightSpec { Pos = new Vector3(0f, (gunBottom + (gunTop + gunBottom) * 0.5f) * 0.5f, frontMost + 0.45f), Range = Mathf.Max(1.2f, boardH * 1.6f), Intensity = 0.5f });
        layout.Size = new Vector3(boardW, boardH, frontMost + 0.03f);
        var lo = new Vector3(-boardW / 2, lowest - 0.01f, 0f); var hi = new Vector3(boardW / 2, lightY + 0.035f, frontMost + 0.05f);
        layout.Extent = new Bounds((lo + hi) * 0.5f, hi - lo);
        layout.Mesh = mb.Build(name);
        return layout;
    }

    /// <summary>The gun shadow box, or with <paramref name="pistol"/> the pistol shadow box: a smaller, deeper case with a wider frame and more space round a handgun.</summary>
    public static Layout ShadowBox(GunShape? g, string name, bool withGlass = true, bool withPlate = true, bool pistol = false)
    {
        var mb = new MeshBuilder();
        var layout = new Layout();
        float lip = pistol ? 0.062f : 0.054f;
        const float backT = 0.008f, feltZ = 0.010f, standoff = 0.026f;
        float innerW = g == null ? (pistol ? 0.34f : 0.78f) : Mathf.Max(pistol ? 0.30f : 0.32f, g.Length + (pistol ? 0.2f : 0.16f));
        float innerH = g == null ? (pistol ? 0.26f : 0.32f) : Mathf.Max(pistol ? 0.24f : 0.24f, g.Height + (withPlate ? 0.17f : 0.13f) + (pistol ? 0.03f : 0f));
        float gunBack = feltZ + standoff;
        float gunDepth = g == null ? 0.06f : g.Depth;
        float glassZ = Mathf.Max(pistol ? 0.088f : 0.075f, gunBack + gunDepth + 0.014f);
        float depth = glassZ + 0.011f;
        float w = innerW + 2 * lip, h = innerH + 2 * lip;
        mb.Box(MeshBuilder.Wood, new Vector3(-w / 2 + 0.004f, -h / 2 + 0.004f, 0f), new Vector3(w / 2 - 0.004f, h / 2 - 0.004f, backT), Vector3.right);
        // Frame: a stepped and rounded moulding with a raised bead and a cove into the sight edge, its inside wall running back to the felt.
        var frame = new List<Vector2>
        {
            new(0f, 0f), new(0f, depth - 0.011f), new(0.0016f, depth - 0.0062f), new(0.0048f, depth - 0.0024f), new(0.0092f, depth - 0.0004f), new(0.0118f, depth),
            new(0.0212f, depth), new(0.0238f, depth - 0.0012f), new(0.0262f, depth - 0.0038f), new(0.0278f, depth - 0.0040f),
            new(0.0296f, depth - 0.0020f), new(0.0316f, depth - 0.0012f), new(0.0336f, depth - 0.0020f), new(0.0350f, depth - 0.0040f),
            new(0.0386f, depth - 0.0050f), new(0.0428f, depth - 0.0074f), new(0.0466f, depth - 0.0088f), new(lip - 0.003f, depth - 0.0092f), new(lip, depth - 0.0118f), new(lip, backT),
        };
        mb.Loft(MeshBuilder.Wood, 0f, 0f, w, h, frame, 0f, false, true);
        // A gilt slip round the sight edge, sloping down to the glass.
        mb.Loft(MeshBuilder.Brass, 0f, 0f, w, h, new[] { new Vector2(lip - 0.0006f, depth - 0.0118f), new Vector2(lip + 0.0042f, glassZ + 0.0012f), new Vector2(lip + 0.0042f, glassZ - 0.002f) }, 0f, false, false);
        // Felt on the back and round the inside walls.
        float iw = innerW / 2 - 0.0008f, ih = innerH / 2 - 0.0008f;
        mb.Quad(MeshBuilder.Felt, new(-iw, -ih, feltZ), new(iw, -ih, feltZ), new(iw, ih, feltZ), new(-iw, ih, feltZ), Vector3.forward, Vector3.right);
        float wallFront = glassZ - 0.003f;
        mb.Quad(MeshBuilder.Felt, new(-iw, -ih, feltZ), new(iw, -ih, feltZ), new(iw, -ih, wallFront), new(-iw, -ih, wallFront), Vector3.up, Vector3.right);
        mb.Quad(MeshBuilder.Felt, new(-iw, ih, feltZ), new(iw, ih, feltZ), new(iw, ih, wallFront), new(-iw, ih, wallFront), Vector3.down, Vector3.right);
        mb.Quad(MeshBuilder.Felt, new(-iw, -ih, feltZ), new(-iw, ih, feltZ), new(-iw, ih, wallFront), new(-iw, -ih, wallFront), Vector3.right, Vector3.up);
        mb.Quad(MeshBuilder.Felt, new(iw, -ih, feltZ), new(iw, ih, feltZ), new(iw, ih, wallFront), new(iw, -ih, wallFront), Vector3.left, Vector3.up);
        // The light: a slim black channel along the top, just behind the glass, its diffuser facing down with a lit edge to the front.
        float chW = innerW / 2 - 0.02f, chY0 = innerH / 2 - 0.011f, chZ0 = glassZ - 0.024f, chZ1 = glassZ - 0.004f;
        mb.Box(MeshBuilder.Steel, new Vector3(-chW, chY0, chZ0), new Vector3(chW, innerH / 2, chZ1), Vector3.right);
        mb.Quad(MeshBuilder.Glow, new(-chW + 0.004f, chY0 - 0.0018f, chZ0 + 0.003f), new(chW - 0.004f, chY0 - 0.0018f, chZ0 + 0.003f), new(chW - 0.004f, chY0 - 0.0018f, chZ1 - 0.003f), new(-chW + 0.004f, chY0 - 0.0018f, chZ1 - 0.003f), Vector3.down, Vector3.right);
        mb.Quad(MeshBuilder.Glow, new(-chW + 0.004f, chY0 + 0.0005f, chZ1 + 0.0015f), new(chW - 0.004f, chY0 + 0.0005f, chZ1 + 0.0015f), new(chW - 0.004f, chY0 + 0.0045f, chZ1 + 0.0015f), new(-chW + 0.004f, chY0 + 0.0045f, chZ1 + 0.0015f), Vector3.forward, Vector3.right);
        // Glass over the whole opening, its glare picture spanning it once. Only once hung: the stash icon renderer turns see-through shaders opaque, which would draw it as a white panel.
        float gw = innerW / 2, gh = innerH / 2;
        if (withGlass) mb.Face(MeshBuilder.Glass, new[] { new Vector3(-gw, -gh, glassZ), new Vector3(gw, -gh, glassZ), new Vector3(gw, gh, glassZ), new Vector3(-gw, gh, glassZ) }, new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward }, new[] { new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1) }, Vector3.forward);
        // An engraved brass plate on the felt under the gun, on two small pins.
        float plateCentreY = -innerH / 2 + 0.036f;
        if (withPlate)
        {
            float pw = pistol ? Mathf.Clamp(innerW * 0.6f, 0.13f, 0.2f) : Mathf.Clamp(innerW * 0.34f, 0.15f, 0.27f), ph = pistol ? 0.034f : 0.038f, pz = feltZ + 0.003f, pt = 0.0022f;
            mb.Slab(MeshBuilder.Brass, 0f, plateCentreY, pw, ph, pz, pt, 0.0012f);
            float fw = pw / 2 - 0.0016f, fh = ph / 2 - 0.0016f, fz = pz + pt + 0.00012f;
            mb.Face(MeshBuilder.Plate, new[] { new Vector3(fw, plateCentreY - fh, fz), new Vector3(-fw, plateCentreY - fh, fz), new Vector3(-fw, plateCentreY + fh, fz), new Vector3(fw, plateCentreY + fh, fz) },
                new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward }, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) }, Vector3.forward);
            foreach (var px in new[] { pw * 0.3f, -pw * 0.3f })
                mb.Tube(MeshBuilder.Brass, new[] { new Vector3(px, plateCentreY, feltZ), new Vector3(px, plateCentreY, pz) }, 0.0022f, 10, false, false);
            layout.PlateSize = new Vector2(fw * 2, fh * 2);
        }
        var seat = new Layout.Seat();
        if (g != null)
        {
            float raise = withPlate ? 0.018f : 0f;
            float cx = -g.Box.center.x, cy = -g.Box.center.y + raise;
            seat.GunOffset = new Vector3(cx, cy, gunBack - g.Box.min.z);
            // Two black standoffs from the felt to the back of the gun, behind its thickest parts so they stay hidden.
            foreach (var t in g.Short ? new[] { 0.4f, 0.72f } : new[] { 0.25f, 0.68f })
            {
                int col = Thickest(g, t, g.Short ? 0.12f : 0.1f);
                if (col < 0) continue;
                // Each standoff reaches the gun's real back at its spot, measured through a thin slice of the gun.
                float ly = (g.Bottom[col] + g.Top[col]) / 2;
                float back = g.BackAt?.Invoke(new Vector2(g.X(col), ly), 0.0065f, 0.0065f) ?? g.Box.min.z;
                float face = back + seat.GunOffset.z;
                var p = new Vector3(g.X(col) + cx, ly + cy, face);
                mb.Lathe(MeshBuilder.Steel, new Vector3(p.x, p.y, feltZ), Vector3.forward, new[] { new Vector2(0.0085f, 0f), new Vector2(0.0085f, 0.0012f), new Vector2(0.0072f, 0.0022f), new Vector2(0.0046f, 0.0024f), new Vector2(0f, 0.0024f) }, 16);
                mb.Tube(MeshBuilder.Steel, new[] { new Vector3(p.x, p.y, feltZ + 0.002f), new Vector3(p.x, p.y, face - 0.003f) }, 0.0042f, 12);
                mb.Tube(MeshBuilder.Rubber, new[] { new Vector3(p.x, p.y, face - 0.0035f), new Vector3(p.x, p.y, face) }, 0.0064f, 14);
                seat.Contacts.Add(p);
            }
        }
        layout.Seats.Add(seat);
        // Spots from the channel down onto the gun and the plate, and a soft point light to fill the box.
        int spots = innerW > 0.6f ? 2 : 1;
        for (int s = 0; s < spots; s++)
        {
            float x = spots == 1 ? 0f : (s == 0 ? innerW * 0.25f : -innerW * 0.25f);
            var at = new Vector3(x, chY0 - 0.004f, chZ0 + 0.008f);
            var to = new Vector3(x * 0.6f, -innerH * 0.18f, gunBack + gunDepth * 0.5f);
            layout.Lights.Add(new LightSpec { Pos = at, Dir = (to - at).normalized, Angle = 125f, Range = innerH * 2.6f, Intensity = 1.25f });
        }
        layout.Lights.Add(new LightSpec { Pos = new Vector3(0f, 0f, glassZ + 0.08f), Range = Mathf.Max(0.5f, innerW * 0.7f), Intensity = 0.3f });
        layout.Size = new Vector3(w, h, depth);
        layout.Extent = new Bounds(new Vector3(0f, 0f, depth * 0.5f), new Vector3(w, h, depth));
        layout.Mesh = mb.Build(name);
        return layout;
    }

    /// <summary>A rubber-dipped J hook: a turned steel rosette screwed to the board, the arm straight out under the gun, and a tip turned up in front of it.</summary>
    private static void Hook(MeshBuilder mb, float x, float contactY, float gunBack, float gunFront)
    {
        float armY = contactY - Sleeve;
        var root = new Vector3(x, armY, BoardT);
        mb.Lathe(MeshBuilder.Steel, root, Vector3.forward, new[] { new Vector2(0.0158f, 0f), new Vector2(0.0158f, 0.0014f), new Vector2(0.0146f, 0.0032f), new Vector2(0.0118f, 0.0044f), new Vector2(0.0082f, 0.0050f), new Vector2(0.0072f, 0.0064f), new Vector2(0.0066f, 0.0090f), new Vector2(0f, 0.0090f) }, 24);
        // Two screw heads on the rosette, one above and one below the arm.
        foreach (var dy in new[] { 0.0112f, -0.0112f })
            mb.Lathe(MeshBuilder.Steel, new Vector3(x, armY + dy, BoardT + 0.0042f), Vector3.forward, new[] { new Vector2(0.0024f, 0f), new Vector2(0.0022f, 0.0008f), new Vector2(0.0014f, 0.0013f), new Vector2(0f, 0.0015f) }, 10);
        // The arm and its upturned tip, all dipped in rubber; it swells a little where it leaves the rosette.
        float end = gunFront + 0.009f;
        var path = new List<Vector3> { new(x, armY, BoardT + 0.008f), new(x, armY, end) };
        for (int a = 1; a <= 7; a++)
        {
            float t = a / 7f * Mathf.PI / 2;
            path.Add(new Vector3(x, armY + HookBend * (1 - Mathf.Cos(t)), end + HookBend * Mathf.Sin(t)));
        }
        var tip = new Vector3(x, armY + HookBend + TipRise, end + HookBend);
        path.Add(tip);
        mb.Tube(MeshBuilder.Rubber, path, Sleeve, 16, false, false);
        mb.Sphere(MeshBuilder.Rubber, tip, Sleeve, 6, 16);
        mb.Lathe(MeshBuilder.Rubber, root + new Vector3(0f, 0f, 0.0088f), Vector3.forward, new[] { new Vector2(0.0098f, -0.0002f), new Vector2(0.0094f, 0.0016f), new Vector2(0.0082f, 0.0034f), new Vector2(Sleeve, 0.0056f), new Vector2(Sleeve * 0.6f, 0.0062f) }, 16);
    }

    /// <summary>A magnet pad: a steel base on the board and a rubber-coated block with rounded edges, its face where the gun's side rests.</summary>
    private static void Pad(MeshBuilder mb, Vector3 face)
    {
        mb.Slab(MeshBuilder.Steel, face.x, face.y, 0.082f, 0.042f, BoardT, 0.003f, 0.0014f);
        float t = Mathf.Max(0.004f, face.z - BoardT - 0.003f);
        mb.Slab(MeshBuilder.Rubber, face.x, face.y, 0.074f, 0.036f, BoardT + 0.003f, t, 0.004f, false);
    }

    /// <summary>
    /// A brass picture light: a turned rosette on the board, an arm rising behind the guns with a knuckle where it bends out over them,
    /// and a half-round shade with rolled lips, end caps and finials, tipped so its opening looks down and back onto the guns.
    /// </summary>
    /// <summary>
    /// Where the light's arm rises. Centred, its rosette sat on the board behind a rifle's carry handle or scope, and the arm looked as if it
    /// ran down into the gun (WWC trailer, 2026-11-17). So the arm goes where the guns under the shade stand lowest, nearest the middle, with
    /// its rosette above them there; if a gun stands higher than the board, the rosette goes on the wall above the board.
    /// </summary>
    private static (float x, float plateY, bool onWall) LightArm(List<(GunShape g, Vector3 offset)> placed, float shadeLen, float boardH, bool triple)
    {
        const float rosette = 0.025f, clearance = 0.012f, reach = rosette + 0.002f;
        float plateY = triple ? boardH / 2 - 0.035f : boardH / 2 - 0.025f;
        float TopAt(float x)
        {
            float t = float.NegativeInfinity;
            foreach (var (g, off) in placed)
                for (int c = 0; c < g.Columns; c++)
                    if (!float.IsNaN(g.Top[c]) && Mathf.Abs(g.X(c) + off.x - x) <= reach) t = Mathf.Max(t, g.Top[c] + off.y);
            return t;
        }
        float span = Mathf.Max(0f, shadeLen / 2 - 0.04f);
        var xs = new List<float>();
        for (float x = 0f; x <= span + 1e-4f; x += 0.005f) { xs.Add(x); if (x > 0f) xs.Add(-x); }
        // Nearest the middle where the rosette fits on the board above every gun.
        foreach (var x in xs) if (TopAt(x) + clearance <= plateY - rosette) return (x, plateY, false);
        // Otherwise where the guns stand lowest (a little preference for the middle), the rosette just above them.
        float best = 0f, bestCost = float.PositiveInfinity;
        foreach (var x in xs) { float cost = TopAt(x) + 0.05f * Mathf.Abs(x); if (cost < bestCost) { bestCost = cost; best = x; } }
        float y = TopAt(best) + clearance + rosette;
        return (best, y, y + rosette > boardH / 2 - 0.004f);
    }

    private static void PictureLight(MeshBuilder mb, float y, float z, float length, float plateY, float armZ, float armX = 0f, bool plateOnWall = false)
    {
        float baseZ = plateOnWall ? 0f : BoardT;
        mb.Lathe(MeshBuilder.Brass, new Vector3(armX, plateY, baseZ), Vector3.forward, new[] { new Vector2(0.025f, 0f), new Vector2(0.025f, 0.0016f), new Vector2(0.0232f, 0.0036f), new Vector2(0.019f, 0.0052f), new Vector2(0.0122f, 0.0062f), new Vector2(0.0094f, 0.0078f), new Vector2(0.0072f, 0.0112f), new Vector2(0f, 0.0112f) }, 28);
        const float r = 0.028f, r2 = 0.012f;
        var arm = new List<Vector3>();
        if (armZ - r2 > baseZ + 0.01f)
        {
            // Out from the rosette first (clear of the cornice on the triple, or of the board when it sits on the wall), then a bend up.
            arm.Add(new Vector3(armX, plateY, baseZ + 0.008f));
            arm.Add(new Vector3(armX, plateY, armZ - r2));
            for (int a = 1; a <= 6; a++) { float t = a / 6f * Mathf.PI / 2; arm.Add(new Vector3(armX, plateY + r2 - r2 * Mathf.Cos(t), armZ - r2 + r2 * Mathf.Sin(t))); }
        }
        else arm.Add(new Vector3(armX, plateY, armZ));
        arm.Add(new Vector3(armX, y - r, armZ));
        for (int a = 1; a <= 8; a++) { float t = a / 8f * Mathf.PI / 2; arm.Add(new Vector3(armX, y - r + r * Mathf.Sin(t), armZ + r - r * Mathf.Cos(t))); }
        arm.Add(new Vector3(armX, y, z - 0.03f));
        mb.Tube(MeshBuilder.Brass, arm, 0.0043f, 14);
        mb.Lathe(MeshBuilder.Brass, new Vector3(armX, y, z - 0.034f), Vector3.forward, new[] { new Vector2(0f, -0.004f), new Vector2(0.0062f, -0.0036f), new Vector2(0.0068f, 0f), new Vector2(0.0062f, 0.0036f), new Vector2(0f, 0.0042f) }, 16);
        // The shade: a half tube along x, tipped 22 degrees so its open side faces down and toward the wall.
        const float rad = 0.027f, tilt = 22f * Mathf.Deg2Rad; const int steps = 14;
        float x0 = -length / 2, x1 = length / 2;
        var c = new Vector3(0f, y, z);
        Vector3 D(float a) => new(0f, Mathf.Sin(a), -Mathf.Cos(a));
        for (int s = 0; s < steps; s++)
        {
            float a0 = tilt + Mathf.PI * s / steps, a1 = tilt + Mathf.PI * (s + 1) / steps;
            var d0 = D(a0); var d1 = D(a1);
            var outer = new[] { c + new Vector3(x0, 0, 0) + d0 * rad, c + new Vector3(x1, 0, 0) + d0 * rad, c + new Vector3(x1, 0, 0) + d1 * rad, c + new Vector3(x0, 0, 0) + d1 * rad };
            mb.Face(MeshBuilder.Brass, outer, new[] { d0, d0, d1, d1 }, outer.Select(p => new Vector2(p.x / MeshBuilder.Tile, (a0 + a1) * 0.5f * rad / MeshBuilder.Tile)).ToArray(), (d0 + d1).normalized);
            // Its inside is a bright reflector, lit when the lamp is on.
            var inner = new[] { c + new Vector3(x0 + 0.004f, 0, 0) + d0 * (rad - 0.0012f), c + new Vector3(x1 - 0.004f, 0, 0) + d0 * (rad - 0.0012f), c + new Vector3(x1 - 0.004f, 0, 0) + d1 * (rad - 0.0012f), c + new Vector3(x0 + 0.004f, 0, 0) + d1 * (rad - 0.0012f) };
            mb.Face(MeshBuilder.Glow, inner, new[] { -d0, -d0, -d1, -d1 }, new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, -(d0 + d1).normalized);
        }
        // Rolled lips along both open edges.
        foreach (var a in new[] { tilt, tilt + Mathf.PI })
            mb.Tube(MeshBuilder.Brass, new[] { c + new Vector3(x0, 0, 0) + D(a) * rad, c + new Vector3(x1, 0, 0) + D(a) * rad }, 0.0021f, 10);
        // End caps (half discs) and a turned finial on each.
        foreach (var (x, f) in new[] { (x0, Vector3.left), (x1, Vector3.right) })
        {
            for (int s = 0; s < steps; s++)
            {
                float a0 = tilt + Mathf.PI * s / steps, a1 = tilt + Mathf.PI * (s + 1) / steps;
                var pc = new Vector3(x, y, z);
                var pts = new[] { pc, pc + D(a0) * rad, pc + D(a1) * rad };
                mb.Face(MeshBuilder.Brass, pts, new[] { f, f, f }, pts.Select(p => MeshBuilder.Planar(p, f, Vector3.up)).ToArray(), f);
            }
            mb.Lathe(MeshBuilder.Brass, new Vector3(x, y, z) + D(tilt + Mathf.PI / 2) * (rad * 0.35f), f, new[] { new Vector2(0.0058f, 0f), new Vector2(0.0058f, 0.0016f), new Vector2(0.0034f, 0.0034f), new Vector2(0.0046f, 0.0062f), new Vector2(0.0030f, 0.0090f), new Vector2(0f, 0.0098f) }, 14);
        }
    }

    /// <summary>The hook spots: under the stock or receiver behind the grip, and under the handguard or barrel, each where the underside sits highest (so not under a magazine, grip or foregrip).</summary>
    public static (int rear, int fore) HookColumns(GunShape g)
    {
        int rear = Best(g, 0.08f, 0.34f, 0.2f), fore = Best(g, 0.55f, 0.88f, 0.7f);
        if (rear < 0) rear = Nearest(g, 0.2f);
        if (fore < 0) fore = Nearest(g, 0.72f);
        return (rear, fore);
    }

    private static int PadHalf(GunShape g) => Mathf.Max(1, Mathf.RoundToInt(0.012f / Mathf.Max(g.Step, 1e-5f)));

    /// <summary>Height of a hook's sleeve top at a column: the lowest point of the gun over the sleeve's width.</summary>
    public static float Contact(GunShape g, int col)
    {
        int k = PadHalf(g); float c = float.PositiveInfinity;
        for (int j = col - k; j <= col + k; j++) if (j >= 0 && j < g.Columns && !float.IsNaN(g.Bottom[j])) c = Mathf.Min(c, g.Bottom[j]);
        return float.IsInfinity(c) ? g.Box.min.y : c;
    }

    private static int Best(GunShape g, float from, float to, float pref)
    {
        int best = -1; float bestScore = float.NegativeInfinity;
        for (int i = 0; i < g.Columns; i++)
        {
            float t = g.T(i);
            if (t < from || t > to || float.IsNaN(g.Bottom[i])) continue;
            float score = Contact(g, i) - 0.05f * Mathf.Abs(t - pref);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    private static int Nearest(GunShape g, float t)
    {
        int best = -1; float d = float.MaxValue;
        for (int i = 0; i < g.Columns; i++) if (!float.IsNaN(g.Bottom[i]) && Mathf.Abs(g.T(i) - t) < d) { d = Mathf.Abs(g.T(i) - t); best = i; }
        return best;
    }

    private static int Thickest(GunShape g, float t, float span)
    {
        int best = -1; float most = 0.012f;
        for (int i = 0; i < g.Columns; i++)
        {
            if (Mathf.Abs(g.T(i) - t) > span || float.IsNaN(g.Bottom[i])) continue;
            float thick = g.Top[i] - g.Bottom[i];
            if (thick > most) { most = thick; best = i; }
        }
        return best;
    }
}
