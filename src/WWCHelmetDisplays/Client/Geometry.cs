using System;
using System.Collections.Generic;

namespace WWCHelmetDisplays.Client.Geometry;

// Plain mesh maths with no Unity types, so the same code runs in the game and in the offline shape tester (tools/cliptest).
// Axes follow Unity: x right, y up, z forward; a triangle faces the side its edge cross product (b - a) x (c - a) points to.

public readonly struct V3
{
    public readonly float X, Y, Z;
    public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 operator *(V3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
    public V3 Normalized { get { var l = Length; return l > 1e-12f ? this * (1f / l) : this; } }
    public static V3 Lerp(V3 a, V3 b, float t) => a + (b - a) * t;
}

public readonly struct V2
{
    public readonly float X, Y;
    public V2(float x, float y) { X = x; Y = y; }
    public static V2 Lerp(V2 a, V2 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}

/// <summary>A cutting plane. Points with <see cref="Dist"/> of zero or more are kept.</summary>
public readonly struct Cut
{
    public readonly V3 N;
    public readonly float D;
    public Cut(V3 normal, float d) { var l = normal.Length; N = normal * (1f / l); D = d / l; }
    /// <summary>The plane through <paramref name="point"/> that keeps the side <paramref name="keepToward"/> points to.</summary>
    public static Cut Through(V3 point, V3 keepToward) { var n = keepToward.Normalized; return new Cut(n, -V3.Dot(n, point)); }
    public float Dist(V3 p) => V3.Dot(N, p) + D;
}

/// <summary>A mesh as lists: positions, normals and UVs share an index; each sub-list holds one material's triangles.</summary>
public sealed class Shape
{
    public readonly List<V3> P = new();
    public readonly List<V3> N = new();
    public readonly List<V2> T = new();
    public readonly List<List<int>> Sub = new();

    public int Add(V3 p, V3 n, V2 t) { P.Add(p); N.Add(n); T.Add(t); return P.Count - 1; }

    public void Bounds(out V3 min, out V3 max)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        foreach (var p in P) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z); }
        min = new V3(x0, y0, z0); max = new V3(x1, y1, z1);
    }

    public int TriangleCount { get { int n = 0; foreach (var s in Sub) n += s.Count / 3; return n; } }
}

public static class MeshClip
{
    private struct Vtx
    {
        public V3 P, N; public V2 T;
        public Vtx(V3 p, V3 n, V2 t) { P = p; N = n; T = t; }
    }

    /// <summary>
    /// Keeps the part of <paramref name="src"/> on the kept side of every cut. With <paramref name="capSub"/> of 0 or more, every opening a cut leaves in a closed
    /// surface is filled with a flat face (put in that sub-mesh, mapped at <paramref name="capTile"/> metres per texture repeat), so the result is a solid again.
    /// Double-sided adds every triangle a second time facing the other way, so an open shell shows from both sides.
    /// </summary>
    public static Shape Clip(Shape src, IReadOnlyList<Cut> cuts, int capSub = -1, float capTile = 0.25f, bool doubleSided = true)
    {
        var dst = new Shape();
        for (int s = 0; s < src.Sub.Count; s++) dst.Sub.Add(new List<int>());
        var poly = new List<Vtx>(12);
        var next = new List<Vtx>(12);
        for (int s = 0; s < src.Sub.Count; s++)
        {
            var tris = src.Sub[s];
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                poly.Clear();
                for (int k = 0; k < 3; k++) { int v = tris[i + k]; poly.Add(new Vtx(src.P[v], src.N[v], src.T[v])); }
                bool inside = true;
                foreach (var cut in cuts)
                {
                    ClipPolygon(poly, cut, next);
                    (poly, next) = (next, poly);
                    if (poly.Count < 3) { inside = false; break; }
                }
                if (!inside) continue;
                int first = dst.Add(poly[0].P, poly[0].N.Normalized, poly[0].T);
                int prev = dst.Add(poly[1].P, poly[1].N.Normalized, poly[1].T);
                for (int k = 2; k < poly.Count; k++)
                {
                    int cur = dst.Add(poly[k].P, poly[k].N.Normalized, poly[k].T);
                    dst.Sub[s].Add(first); dst.Sub[s].Add(prev); dst.Sub[s].Add(cur);
                    prev = cur;
                }
            }
        }
        if (capSub >= 0)
        {
            while (dst.Sub.Count <= capSub) dst.Sub.Add(new List<int>());
            for (int c = 0; c < cuts.Count; c++) Cap(src, cuts, c, dst, capSub, capTile);
        }
        if (doubleSided) MakeDoubleSided(dst);
        return dst;
    }

    private static void ClipPolygon(List<Vtx> poly, Cut cut, List<Vtx> output)
    {
        output.Clear();
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var cur = poly[i]; var prev = poly[(i + n - 1) % n];
            float dc = cut.Dist(cur.P), dp = cut.Dist(prev.P);
            if (dc >= 0f)
            {
                if (dp < 0f) output.Add(Between(prev, cur, dp / (dp - dc)));
                output.Add(cur);
            }
            else if (dp >= 0f) output.Add(Between(prev, cur, dp / (dp - dc)));
        }
    }

    private static Vtx Between(Vtx a, Vtx b, float t) => new(V3.Lerp(a.P, b.P, t), V3.Lerp(a.N, b.N, t), V2.Lerp(a.T, b.T, t));

    public static void MakeDoubleSided(Shape m)
    {
        int count = m.P.Count;
        for (int v = 0; v < count; v++) m.Add(m.P[v], -m.N[v], m.T[v]);
        foreach (var sub in m.Sub)
        {
            int tris = sub.Count;
            for (int i = 0; i + 2 < tris; i += 3) { sub.Add(sub[i] + count); sub.Add(sub[i + 2] + count); sub.Add(sub[i + 1] + count); }
        }
    }

    // ---------- filling the openings ----------

    private static long Key(V3 p) => ((long)Math.Round(p.X * 20000f) & 0x1FFFFF) | (((long)Math.Round(p.Y * 20000f) & 0x1FFFFF) << 21) | (((long)Math.Round(p.Z * 20000f) & 0x1FFFFF) << 42);

    private static void Cap(Shape src, IReadOnlyList<Cut> cuts, int c, Shape dst, int capSub, float tile)
    {
        var cut = cuts[c];
        // Where the untrimmed surface crosses this plane, as segments joined into closed outlines.
        var links = new Dictionary<long, List<long>>();
        var points = new Dictionary<long, V3>();
        void Link(V3 a, V3 b)
        {
            long ka = Key(a), kb = Key(b);
            if (ka == kb) return;
            points[ka] = a; points[kb] = b;
            if (!links.TryGetValue(ka, out var la)) links[ka] = la = new List<long>();
            if (!links.TryGetValue(kb, out var lb)) links[kb] = lb = new List<long>();
            if (!la.Contains(kb)) la.Add(kb);
            if (!lb.Contains(ka)) lb.Add(ka);
        }
        var crossing = new List<V3>(3);
        foreach (var tris in src.Sub)
        {
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                crossing.Clear();
                for (int e = 0; e < 3; e++)
                {
                    var a = src.P[tris[i + e]]; var b = src.P[tris[i + (e + 1) % 3]];
                    // Always interpolate from the same end, so two triangles sharing an edge find the very same point.
                    if (Key(a) > Key(b)) (a, b) = (b, a);
                    float da = cut.Dist(a), db = cut.Dist(b);
                    if ((da < 0f) != (db < 0f)) crossing.Add(V3.Lerp(a, b, da / (da - db)));
                }
                if (crossing.Count == 2) Link(crossing[0], crossing[1]);
            }
        }
        if (links.Count < 3) return;

        // A flat 2D frame on the plane: X = O + u U + v V.
        var n = cut.N;
        var helper = Math.Abs(n.Y) < 0.9f ? new V3(0, 1, 0) : new V3(1, 0, 0);
        var U = V3.Cross(helper, n).Normalized;
        var V = V3.Cross(n, U).Normalized;
        var O = n * (-cut.D);
        // The outlines are walked as the faces of a flat map: at every corner the walk takes the next link clockwise from the one it came in by,
        // so each face comes out as one closed outline, counter-clockwise, and the endless face around them all clockwise (left out). Where the
        // mannequin's hanging arm presses into its side, two outlines meet at one corner (three or four links there); a walk that took the first
        // free link got lost there, the opening was never filled, and the arm's cut showed as a hollow shell (WWC trailer, 2026-11-18).
        var flat = new Dictionary<long, V2>();
        foreach (var kv in points) flat[kv.Key] = Flat(kv.Value, O, U, V);
        var around = new Dictionary<long, List<long>>();
        foreach (var kv in links)
        {
            var at = flat[kv.Key];
            var list = new List<long>(kv.Value);
            list.Sort((x, y) => Math.Atan2(flat[x].Y - at.Y, flat[x].X - at.X).CompareTo(Math.Atan2(flat[y].Y - at.Y, flat[y].X - at.X)));
            around[kv.Key] = list;
        }
        var used = new HashSet<(long, long)>();
        foreach (var start in links.Keys)
        {
            foreach (var firstNext in links[start])
            {
                if (used.Contains((start, firstNext))) continue;
                var loop = new List<V2>();
                long prev = start, cur = firstNext;
                used.Add((start, firstNext));
                loop.Add(flat[start]);
                int guard = 0;
                bool closed = false;
                while (guard++ < 100000)
                {
                    var ring = around[cur];
                    int back = ring.IndexOf(prev);
                    long pick = ring[(back - 1 + ring.Count) % ring.Count];
                    if (cur == start && pick == firstNext) { closed = true; break; }
                    if (!used.Add((cur, pick))) break;
                    loop.Add(flat[cur]);
                    prev = cur; cur = pick;
                }
                if (!closed || loop.Count < 3 || Area(loop) <= 0f) continue;
                // Trim the outline by the other cuts, so the face stops where they stop.
                for (int j = 0; j < cuts.Count && loop.Count >= 3; j++)
                {
                    if (j == c) continue;
                    var o = cuts[j];
                    float a = V3.Dot(o.N, U), b = V3.Dot(o.N, V), k0 = V3.Dot(o.N, O) + o.D;
                    loop = Clip2D(loop, a, b, k0);
                }
                if (loop.Count < 3) continue;
                if (Area(loop) < 0) loop.Reverse();
                var faceNormal = -n;
                var idx = new int[loop.Count];
                for (int q = 0; q < loop.Count; q++)
                {
                    var p3 = O + U * loop[q].X + V * loop[q].Y;
                    idx[q] = dst.Add(p3, faceNormal, new V2(loop[q].X / tile, loop[q].Y / tile));
                }
                foreach (var (x, y, z) in EarClip(loop))
                {
                    int ia = idx[x], ib = idx[y], ic = idx[z];
                    var cr = V3.Cross(dst.P[ib] - dst.P[ia], dst.P[ic] - dst.P[ia]);
                    if (V3.Dot(cr, faceNormal) < 0) (ib, ic) = (ic, ib);
                    dst.Sub[capSub].Add(ia); dst.Sub[capSub].Add(ib); dst.Sub[capSub].Add(ic);
                }
            }
        }
    }

    private static V2 Flat(V3 p, V3 o, V3 u, V3 v) { var d = p - o; return new V2(V3.Dot(d, u), V3.Dot(d, v)); }

    private static List<V2> Clip2D(List<V2> poly, float a, float b, float c)
    {
        var o = new List<V2>(poly.Count + 4);
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var cur = poly[i]; var prev = poly[(i + n - 1) % n];
            float dc = a * cur.X + b * cur.Y + c, dp = a * prev.X + b * prev.Y + c;
            if (dc >= 0f)
            {
                if (dp < 0f) o.Add(V2.Lerp(prev, cur, dp / (dp - dc)));
                o.Add(cur);
            }
            else if (dp >= 0f) o.Add(V2.Lerp(prev, cur, dp / (dp - dc)));
        }
        return o;
    }

    public static float Area(List<V2> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; s += (double)a.X * b.Y - (double)b.X * a.Y; }
        return (float)(s * 0.5);
    }

    /// <summary>Fills a counter-clockwise outline with triangles (index triples into it).</summary>
    public static List<(int, int, int)> EarClip(List<V2> pts)
    {
        var tris = new List<(int, int, int)>();
        var idx = new List<int>();
        for (int i = 0; i < pts.Count; i++) idx.Add(i);
        float Cross2(int a, int b, int c) => (pts[b].X - pts[a].X) * (pts[c].Y - pts[a].Y) - (pts[c].X - pts[a].X) * (pts[b].Y - pts[a].Y);
        bool Inside(int p, int a, int b, int c)
        {
            float d1 = Cross2(a, b, p), d2 = Cross2(b, c, p), d3 = Cross2(c, a, p);
            return d1 >= -1e-12f && d2 >= -1e-12f && d3 >= -1e-12f;
        }
        int guard = 0;
        while (idx.Count > 3 && guard++ < 100000)
        {
            bool found = false;
            for (int k = 0; k < idx.Count; k++)
            {
                int a = idx[(k + idx.Count - 1) % idx.Count], b = idx[k], c = idx[(k + 1) % idx.Count];
                if (Cross2(a, b, c) <= 1e-12f) continue;
                bool ear = true;
                foreach (var v in idx)
                {
                    if (v == a || v == b || v == c) continue;
                    if (Inside(v, a, b, c)) { ear = false; break; }
                }
                if (!ear) continue;
                tris.Add((a, b, c));
                idx.RemoveAt(k);
                found = true;
                break;
            }
            if (!found)
            {
                // Leftovers that are flat or tangled: drop the flattest corner and go on, which never loops forever.
                int worst = 0; float best = float.MaxValue;
                for (int k = 0; k < idx.Count; k++)
                {
                    float c2 = Math.Abs(Cross2(idx[(k + idx.Count - 1) % idx.Count], idx[k], idx[(k + 1) % idx.Count]));
                    if (c2 < best) { best = c2; worst = k; }
                }
                idx.RemoveAt(worst);
            }
        }
        if (idx.Count == 3 && Cross2(idx[0], idx[1], idx[2]) > 0) tris.Add((idx[0], idx[1], idx[2]));
        return tris;
    }

    /// <summary>Joins several shapes into one, sub-mesh by sub-mesh.</summary>
    public static Shape Merge(params Shape[] shapes)
    {
        var dst = new Shape();
        foreach (var s in shapes)
        {
            int baseIndex = dst.P.Count;
            for (int i = 0; i < s.P.Count; i++) dst.Add(s.P[i], s.N[i], s.T[i]);
            for (int k = 0; k < s.Sub.Count; k++)
            {
                while (dst.Sub.Count <= k) dst.Sub.Add(new List<int>());
                foreach (var i in s.Sub[k]) dst.Sub[k].Add(i + baseIndex);
            }
        }
        return dst;
    }
}

/// <summary>Pieces of gear frozen on a bust: which ones to leave off whole.</summary>
public static class GearPieces
{
    /// <summary>
    /// Pieces of body gear that would hang in the air on a bust are left off whole. A piece is an island: triangles joined by shared corners, or
    /// by corners at the same spot. Two kinds go (WWC trailer v3.2, 2026-11-18: "Improve the boss busts so nothing is floating"):
    /// a long, narrow piece reaching past the chin: a radio with its antenna, which on a player hangs at the hip and leans back with the arm, but in
    /// the bust's arms-down pose stood straight up past the face, and trimmed at the shoulders still stood beside the chest on its own (the Knight's
    /// CPC Goons Edition, 8 x 8 x 42 cm; the AirLite's radio);
    /// and a small piece the bust's cuts would take most of: a pouch, buckle or strap end hanging below the chest, of which only a sliver was left
    /// in the air beside the stand (Killa's Triton, Tagilla's AVS, the Knight's CPC). Large pieces (a vest's body) are always cut, never left off.
    /// <paramref name="note"/> says what was left off, for the log.
    /// </summary>
    public static void LeaveOffLoose(Shape s, List<Cut> cuts, out string note)
    {
        var off = FindLoose(s, cuts, out note);
        if (off.Count == 0) return;
        for (int k = 0; k < s.Sub.Count; k++)
        {
            var src = s.Sub[k]; var keep = new List<int>(src.Count);
            for (int t = 0; t + 2 < src.Count; t += 3)
            {
                if (off.Contains(src[t])) continue;
                keep.Add(src[t]); keep.Add(src[t + 1]); keep.Add(src[t + 2]);
            }
            s.Sub[k] = keep;
        }
    }

    /// <summary>Under this length (its longest side) a piece may be left off; over it, it is part of the vest itself and is always cut.</summary>
    private const float LooseSize = 0.30f;
    /// <summary>A small piece is left off when less than this share of its surface is on the bust's side of the cuts.</summary>
    private const float LooseShare = 0.40f;

    /// <summary>The corners of every piece to leave off (the head bone is at 0, the shoulders' tops near -0.12).</summary>
    private static HashSet<int> FindLoose(Shape s, List<Cut> cuts, out string note)
    {
        int n = s.P.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Join(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        foreach (var sub in s.Sub) for (int t = 0; t + 2 < sub.Count; t += 3) { Join(sub[t], sub[t + 1]); Join(sub[t], sub[t + 2]); }
        // corners split at UV seams share a position: join them too (0.1 mm grid)
        var at = new Dictionary<(long, long, long), int>();
        for (int i = 0; i < n; i++)
        {
            var key = ((long)Math.Round(s.P[i].X * 1e4), (long)Math.Round(s.P[i].Y * 1e4), (long)Math.Round(s.P[i].Z * 1e4));
            if (at.TryGetValue(key, out var j)) Join(i, j); else at[key] = i;
        }
        var lo = new Dictionary<int, V3>(); var hi = new Dictionary<int, V3>();
        var lowest = new Dictionary<int, V3>(); var highest = new Dictionary<int, V3>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i); var p = s.P[i];
            if (!lowest.TryGetValue(r, out var l0) || p.Y < l0.Y) lowest[r] = p;
            if (!highest.TryGetValue(r, out var h0) || p.Y > h0.Y) highest[r] = p;
            if (!lo.TryGetValue(r, out var a)) { lo[r] = p; hi[r] = p; continue; }
            var b = hi[r];
            lo[r] = new V3(Math.Min(a.X, p.X), Math.Min(a.Y, p.Y), Math.Min(a.Z, p.Z));
            hi[r] = new V3(Math.Max(b.X, p.X), Math.Max(b.Y, p.Y), Math.Max(b.Z, p.Z));
        }
        // How far each long piece reaching past the chin strays from the line between its lowest and highest corner: a radio's antenna leaning
        // with the shoulder is no longer narrow in the frame's box (the AirLite's, once the bust's arms were swung out), but stays near that line.
        var stray = new Dictionary<int, float>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            var foot = lowest[r]; var axis = highest[r] - foot; float length = axis.Length;
            if (length < 0.25f || highest[r].Y <= -0.06f) continue;
            var u = axis * (1f / length); var d = s.P[i] - foot;
            float off = (d - u * V3.Dot(d, u)).Length;
            if (!stray.TryGetValue(r, out var most) || off > most) stray[r] = off;
        }
        // How much of each piece's surface is on the bust's side of every cut (by triangle, at its middle).
        var area = new Dictionary<int, float>(); var kept = new Dictionary<int, float>();
        foreach (var sub in s.Sub)
            for (int t = 0; t + 2 < sub.Count; t += 3)
            {
                var p0 = s.P[sub[t]]; var p1 = s.P[sub[t + 1]]; var p2 = s.P[sub[t + 2]];
                float size = V3.Cross(p1 - p0, p2 - p0).Length * 0.5f;
                int r = Find(sub[t]);
                area[r] = (area.TryGetValue(r, out var was) ? was : 0f) + size;
                var mid = (p0 + p1 + p2) * (1f / 3f);
                bool on = true;
                foreach (var c in cuts) if (c.Dist(mid) < 0f) { on = false; break; }
                if (on) kept[r] = (kept.TryGetValue(r, out var k) ? k : 0f) + size;
            }
        var offRoots = new HashSet<int>(); var notes = new List<string>();
        int small = 0; string largest = ""; float largestSize = 0f;
        foreach (var r in lo.Keys)
        {
            var a = lo[r]; var b = hi[r];
            float[] e = { b.X - a.X, b.Y - a.Y, b.Z - a.Z };
            Array.Sort(e);
            float total = area.TryGetValue(r, out var t0) ? t0 : 0f;
            float share = total > 0f ? (kept.TryGetValue(r, out var k0) ? k0 : 0f) / total : 0f;
            // long (over 25 cm) and narrow (under 10 cm both other ways, or within 7 cm of its own long line at any lean) and reaching past the
            // chin: a radio and its antenna, not a collar or strap
            bool rod = stray.TryGetValue(r, out var st) && st < 0.07f;
            if (b.Y > -0.06f && ((e[2] > 0.25f && e[1] < 0.10f) || rod))
            {
                offRoots.Add(r);
                notes.Add($"left off a long narrow piece {e[0] * 100:0}x{e[1] * 100:0}x{e[2] * 100:0} cm, top {b.Y:0.00}" + (rod ? $", {st * 100:0} cm off its line" : ""));
            }
            // small, and mostly cut away (a piece wholly cut away needs nothing)
            else if (e[2] < LooseSize && share > 0f && share < LooseShare)
            {
                offRoots.Add(r); small++;
                if (e[2] > largestSize) { largestSize = e[2]; largest = $"{e[0] * 100:0}x{e[1] * 100:0}x{e[2] * 100:0} cm, {share * 100:0} % on the bust"; }
            }
        }
        if (small > 0) notes.Add($"left off {small} small piece(s) the cut would mostly take (largest {largest})");
        note = string.Join("; ", notes);
        var corners = new HashSet<int>();
        if (offRoots.Count > 0) for (int i = 0; i < n; i++) if (offRoots.Contains(Find(i))) corners.Add(i);
        return corners;
    }
}
