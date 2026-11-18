using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WWCGunDisplays.Client;

/// <summary>
/// Engraves the shadow box's brass name plate: the gun's short name large and its full name small, cut dark into brushed brass inside a fine border.
/// Glyphs come from font_atlas.png/json made by make_displays.py; the plate is drawn on the CPU into a 1024 x 256 texture.
/// </summary>
internal static class PlateMaker
{
    private const int W = 1024, H = 256;
    private sealed class Glyph { public int X, Y, Width, Advance; }
    private static Color32[]? atlas, brass;
    private static int atlasW, atlasH, ascent, descent, pad, cellH, brassW, brassH;
    private static readonly Dictionary<char, Glyph> glyphs = new();

    private static void Load(string shared)
    {
        if (atlas != null) return;
        var meta = JObject.Parse(File.ReadAllText(Path.Combine(shared, "font_atlas.json")));
        ascent = (int)meta["ascent"]!; descent = (int)meta["descent"]!; pad = (int)meta["pad"]!; cellH = (int)meta["cellHeight"]!;
        foreach (var p in (JObject)meta["glyphs"]!)
            glyphs[p.Key[0]] = new Glyph { X = (int)p.Value!["x"]!, Y = (int)p.Value!["y"]!, Width = (int)p.Value!["w"]!, Advance = (int)p.Value!["advance"]! };
        var a = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        a.LoadImage(File.ReadAllBytes(Path.Combine(shared, "font_atlas.png")), false);
        atlasW = a.width; atlasH = a.height; atlas = a.GetPixels32();
        UnityEngine.Object.Destroy(a);
        var b = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        b.LoadImage(File.ReadAllBytes(Path.Combine(shared, "brass_a.png")), false);
        brassW = b.width; brassH = b.height; brass = b.GetPixels32();
        UnityEngine.Object.Destroy(b);
    }

    private static float Ink(float x, float y)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        float S(int xx, int yy) => xx < 0 || yy < 0 || xx >= atlasW || yy >= atlasH ? 0f : atlas![yy * atlasW + xx].r / 255f;
        return Mathf.Lerp(Mathf.Lerp(S(x0, y0), S(x0 + 1, y0), fx), Mathf.Lerp(S(x0, y0 + 1), S(x0 + 1, y0 + 1), fx), fy);
    }

    private static string Clean(string text)
    {
        var chars = text.Select(c => c == '×' ? 'x' : c).Where(c => glyphs.ContainsKey(c)).ToArray();
        return new string(chars).Trim();
    }

    private static float Width(string text) => text.Sum(c => (float)glyphs[c].Advance) + Math.Max(0, text.Length - 1) * 4f;

    /// <summary>Draws one line centred at <paramref name="baseline"/> (rows from the bottom), at most <paramref name="maxWidth"/> wide on the plate as seen.</summary>
    private static void Line(float[] ink, string text, float baseline, float capPx, float maxWidth, float squeeze)
    {
        if (text.Length == 0) return;
        float s = capPx / ascent;
        float sx = s * squeeze;
        float natural = Width(text) * sx;
        if (natural > maxWidth) { float k = maxWidth / natural; s *= k; sx *= k; natural = maxWidth; }
        float pen = (W - natural) / 2f;
        foreach (var c in text)
        {
            var g = glyphs[c];
            int rb = atlasH - g.Y - pad - ascent;           // the glyph's baseline in the atlas, rows from the bottom
            int r0 = atlasH - g.Y - cellH;
            int xFrom = Mathf.FloorToInt(pen - pad * sx), xTo = Mathf.CeilToInt(pen + (g.Width - pad) * sx);
            int yFrom = Mathf.FloorToInt(baseline - (rb - r0) * s), yTo = Mathf.CeilToInt(baseline + (r0 + cellH - rb) * s);
            for (int y = Math.Max(0, yFrom); y < Math.Min(H, yTo); y++)
                for (int x = Math.Max(0, xFrom); x < Math.Min(W, xTo); x++)
                {
                    float ax = g.X + pad + (x + 0.5f - pen) / sx, ay = rb + (y + 0.5f - baseline) / s;
                    if (ax < g.X || ax >= g.X + g.Width) continue;
                    float v = Ink(ax - 0.5f, ay - 0.5f);
                    if (v > ink[y * W + x]) ink[y * W + x] = v;
                }
            pen += (g.Advance + 4f) * sx;
        }
    }

    /// <summary>A plate texture for a face <paramref name="faceAspect"/> times as wide as it is tall. The caller owns (and destroys) the texture.</summary>
    public static Texture2D Make(string shared, string big, string small, float faceAspect)
    {
        Load(shared);
        // The texture is 4:1; the plate's face is wider, so glyphs are drawn narrower here and come out true on the plate.
        float squeeze = (W / (float)H) / Mathf.Max(faceAspect, 1f);
        var ink = new float[W * H];
        var line1 = Clean(big).ToUpperInvariant();
        var line2 = Clean(small);
        if (line1.Length > 0 && line2.Length > 0 && !string.Equals(line1, line2, StringComparison.OrdinalIgnoreCase))
        {
            Line(ink, line1, H * 0.47f, H * 0.30f, W * 0.86f, squeeze);
            Line(ink, line2, H * 0.17f, H * 0.12f, W * 0.86f, squeeze);
        }
        else Line(ink, line1.Length > 0 ? line1 : line2, H * 0.34f, H * 0.32f, W * 0.86f, squeeze);
        // A fine engraved border, the same width all round on the plate as seen.
        int bx = Mathf.RoundToInt(16 * squeeze), by = 16, lx = Math.Max(1, Mathf.RoundToInt(3 * squeeze)), ly = 3;
        for (int y = by; y < H - by; y++)
            for (int x = bx; x < W - bx; x++)
                if (x < bx + lx || x >= W - bx - lx || y < by + ly || y >= H - by - ly) ink[y * W + x] = Mathf.Max(ink[y * W + x], 0.9f);
        var px = new Color32[W * H];
        var engraved = new Color(0.085f, 0.062f, 0.04f);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var bc = brass![(y % brassH) * brassW + (x % brassW)];
                var b = new Color(bc.r / 255f, bc.g / 255f, bc.b / 255f);
                // A soft sheen across the plate and slightly darker bevelled edges.
                float edge = Mathf.Min(Mathf.Min(x, W - 1 - x) / (8f * squeeze), Mathf.Min(y, H - 1 - y) / 8f);
                b *= 0.92f + 0.12f * Mathf.Sin((x / (float)W + y / (float)H * 0.3f) * Mathf.PI) - (edge < 1f ? 0.12f * (1f - edge) : 0f);
                // A hairline of light along the lower lip of each cut, the way engraving catches light from above.
                float a = ink[y * W + x];
                float lip = y > 0 ? Mathf.Clamp01(ink[(y - 1) * W + x] - a) : 0f;
                var c = Color.Lerp(b, engraved, a) + new Color(0.25f, 0.2f, 0.12f) * lip * 0.6f;
                px[y * W + x] = new Color32((byte)(Mathf.Clamp01(c.r) * 255), (byte)(Mathf.Clamp01(c.g) * 255), (byte)(Mathf.Clamp01(c.b) * 255), 255);
            }
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, true, false) { name = "WWCGun_plate", hideFlags = HideFlags.DontUnloadUnusedAsset, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
        tex.SetPixels32(px);
        tex.Apply(true, false);
        return tex;
    }
}
