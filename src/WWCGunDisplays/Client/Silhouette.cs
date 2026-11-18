using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WWCGunDisplays.Client;

/// <summary>
/// Measures a hanging gun. Gun meshes can't be read on the CPU, so the gun is drawn alone (layer 30, moved 3 km away for the moment)
/// by small orthographic cameras: once from the room side for its outline, and once per pad or standoff through a thin slice from the side
/// to find exactly where its back is.
/// </summary>
internal static class Silhouette
{
    private const int Layer = 30;
    private const float Pixel = 0.0006f;      // metres per pixel of the outline, so thin parts (sling loops, gas blocks) still show

    private static bool Additive(Renderer r) => r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0);

    private static Renderer[] Drawn(GameObject model) =>
        model.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && r.gameObject.activeInHierarchy && (r is MeshRenderer || r is SkinnedMeshRenderer)).ToArray();

    public static GunShape Measure(Transform holder, GameObject model)
    {
        var renderers = Drawn(model);
        // Additive parts (lens glints, glows) are left out of both the box and the outline.
        var min = Vector3.positiveInfinity; var max = Vector3.negativeInfinity;
        foreach (var r in renderers.Where(r => !Additive(r)))
        {
            var lb = r.localBounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = holder.InverseTransformPoint(r.transform.TransformPoint(corner));
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
        }
        if (float.IsInfinity(min.x)) { min = -Vector3.one * 0.1f; max = Vector3.one * 0.1f; }
        var shape = new GunShape { Box = new Bounds((min + max) * 0.5f, max - min) };
        var fireport = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "fireport");
        if (fireport != null) shape.MuzzleTowardMinusX = holder.InverseTransformPoint(fireport.position).x < shape.Box.center.x;

        // The outline, drawn flat from the room side.
        const float margin = 0.01f;
        float viewW = shape.Box.size.x + 2 * margin, viewH = shape.Box.size.y + 2 * margin;
        int width = Mathf.Clamp(Mathf.CeilToInt(viewW / Pixel), 256, 2400);
        int height = Mathf.Clamp(Mathf.CeilToInt(width * viewH / Mathf.Max(viewW, 0.01f)), 16, 1200);
        var centre = new Vector3(shape.Box.center.x, shape.Box.center.y, shape.Box.max.z + 2f);
        var px = Render(holder, model, renderers, centre, Quaternion.LookRotation(-Vector3.forward, Vector3.up), viewW, viewH, width, height, 0.01f, 6f);
        // Image column 0 is on the viewer's left, which is +x; row 0 is the bottom.
        float x0 = shape.Box.center.x + viewW * 0.5f, y0 = shape.Box.center.y - viewH * 0.5f, step = viewW / width, rowH = viewH / height;
        var bottom = new float[width]; var top = new float[width];
        double sx = 0, sy = 0; long count = 0;
        int minCol = width, maxCol = -1, minRow = height, maxRow = -1;
        for (int c = 0; c < width; c++)
        {
            bottom[c] = float.NaN; top[c] = float.NaN;
            if (px == null) continue;
            for (int r = 0; r < height; r++)
            {
                if (!Solid(px[r * width + c])) continue;
                float y = y0 + (r + 0.5f) * rowH;
                if (float.IsNaN(bottom[c])) bottom[c] = y;
                top[c] = y;
                sx += x0 - (c + 0.5f) * step; sy += y; count++;
                minRow = Math.Min(minRow, r); maxRow = Math.Max(maxRow, r);
            }
            if (!float.IsNaN(bottom[c])) { minCol = Math.Min(minCol, c); maxCol = Math.Max(maxCol, c); }
        }
        shape.ColumnX0 = x0; shape.Step = step; shape.Bottom = bottom; shape.Top = top;
        if (count > 20)
        {
            shape.Centroid = new Vector2((float)(sx / count), (float)(sy / count));
            // The outline is the truer measure of length and height; the renderer boxes can include empty space.
            float bx1 = x0 - minCol * step, bx0 = x0 - (maxCol + 1) * step, by0 = y0 + minRow * rowH, by1 = y0 + (maxRow + 1) * rowH;
            var b = shape.Box;
            shape.Box = new Bounds(new Vector3((bx0 + bx1) * 0.5f, (by0 + by1) * 0.5f, b.center.z), new Vector3(bx1 - bx0, by1 - by0, b.size.z));
        }
        else shape.Centroid = new Vector2(shape.Box.center.x, shape.Box.center.y);
        var box = shape.Box;
        shape.BackAt = (at, halfX, halfY) => BackAt(holder, model, box, at, halfX, halfY);
        return shape;
    }

    /// <summary>
    /// The gun's back (smallest z) over a patch of its side: a camera on the viewer's left looks across the gun, its near and far planes cut a slice
    /// as wide as the patch, and the first solid column from the wall side is the back. Null when nothing of the gun is in the patch.
    /// </summary>
    public static float? BackAt(Transform holder, GameObject model, Bounds box, Vector2 at, float halfX, float halfY)
    {
        float half = Mathf.Max(box.size.z * 0.5f + 0.01f, halfY + 0.01f);
        int size = Mathf.Clamp(Mathf.CeilToInt(2 * half / 0.0003f), 128, 1024);
        var renderers = Drawn(model);
        const float stand = 0.5f;
        var camAt = new Vector3(at.x + halfX + stand, at.y, box.center.z);
        // Looking along -x with y up, the camera's right is +z: column 0 is the wall side.
        var px = Render(holder, model, renderers, camAt, Quaternion.LookRotation(Vector3.left, Vector3.up), 2 * half, 2 * half, size, size, stand, stand + 2 * halfX);
        if (px == null) return null;
        int r0 = Mathf.Clamp(Mathf.FloorToInt((half - halfY) / (2 * half) * size), 0, size - 1), r1 = Mathf.Clamp(Mathf.CeilToInt((half + halfY) / (2 * half) * size), 0, size - 1);
        for (int c = 0; c < size; c++)
            for (int r = r0; r <= r1; r++)
                if (Solid(px[r * size + c])) return box.center.z - half + c * (2 * half / size);
        return null;
    }

    private static bool Solid(Color32 p) => !(p.r > 235 && p.g < 20 && p.b > 235);

    /// <summary>Draws the gun alone with an orthographic camera placed in the holder's axes (position and rotation local to the holder). Everything it changes is put back.</summary>
    private static Color32[]? Render(Transform holder, GameObject model, Renderer[] renderers, Vector3 localPos, Quaternion localRot, float viewW, float viewH, int w, int h, float near, float far)
    {
        float scale = holder.lossyScale.y;
        var oldLayers = model.GetComponentsInChildren<Transform>(true).Select(t => (t, t.gameObject.layer)).ToList();
        var hidden = renderers.Where(Additive).Where(r => r.enabled).ToList();
        var oldPos = holder.position;
        var go = new GameObject("WWCGun_measure");
        var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
        Texture2D? img = null;
        try
        {
            foreach (var (t, _) in oldLayers) t.gameObject.layer = Layer;
            foreach (var r in hidden) r.enabled = false;
            holder.position = new Vector3(0f, -3000f, 0f);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(1f, 0f, 1f, 0f); cam.cullingMask = 1 << Layer;
            cam.allowHDR = false; cam.allowMSAA = false; cam.nearClipPlane = near * scale; cam.farClipPlane = far * scale;
            cam.orthographicSize = viewH * 0.5f * scale; cam.aspect = viewW / viewH;
            go.transform.SetPositionAndRotation(holder.TransformPoint(localPos), holder.rotation * localRot);
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            img = new Texture2D(w, h, TextureFormat.RGBA32, false);
            img.ReadPixels(new Rect(0, 0, w, h), 0, 0); img.Apply(false);
            RenderTexture.active = prev;
            return img.GetPixels32();
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("measuring a gun", error); return null; }
        finally
        {
            holder.position = oldPos;
            foreach (var r in hidden) if (r != null) r.enabled = true;
            foreach (var (t, l) in oldLayers) if (t != null) t.gameObject.layer = l;
            if (img != null) UnityEngine.Object.Destroy(img);
            UnityEngine.Object.Destroy(go);
            RenderTexture.ReleaseTemporary(rt);
        }
    }
}
