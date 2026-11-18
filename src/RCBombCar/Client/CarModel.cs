using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>
/// The car's look, built in code (no asset files): a short-course RC truck about 45 cm long, knobby tyres, two olive bricks of plastic
/// explosive taped to the bed with grey tape, a black detonator box with a red LED, wires and a whip antenna. Metres, +Z forward, +Y up,
/// origin on the ground under the middle of the car. Materials use the first shader the game has loaded from a short list.
/// </summary>
internal static class CarModel
{
    public static string ShaderUsed = "?";
    static readonly Dictionary<string, Material> materials = new();
    static Texture2D? flatNormal;

    sealed class Builder
    {
        readonly List<Vector3> v = new(); readonly List<Vector3> n = new(); readonly List<Vector2> uv = new();
        readonly Dictionary<string, List<int>> tris = new();

        List<int> T(string mat) { if (!tris.TryGetValue(mat, out var t)) tris[mat] = t = new List<int>(); return t; }

        void Quad(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            int i = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            for (int k = 0; k < 4; k++) n.Add(normal);
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
            // Unity draws clockwise (as seen from the front) triangles
            var t = T(mat); t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        /// <summary>A box; rot turns it about its own centre.</summary>
        public void Box(string mat, Vector3 c, Vector3 size, Quaternion? rot = null)
        {
            var r = rot ?? Quaternion.identity; var h = size * 0.5f;
            Vector3 P(float x, float y, float z) => c + r * new Vector3(x * h.x, y * h.y, z * h.z);
            Vector3 N(float x, float y, float z) => r * new Vector3(x, y, z);
            Quad(mat, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), N(0, 0, 1));      // front
            Quad(mat, P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), N(0, 0, -1));  // back
            Quad(mat, P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), N(1, 0, 0));       // right
            Quad(mat, P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), N(-1, 0, 0));  // left
            Quad(mat, P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), N(0, 1, 0));       // top
            Quad(mat, P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), N(0, -1, 0));  // bottom
        }

        /// <summary>A cylinder lying along X (wheels) or standing along Y (antenna).</summary>
        public void Cylinder(string mat, Vector3 c, float radius, float length, int seg, bool alongX)
        {
            Vector3 Axis(float a, float rr, float along) => alongX ? c + new Vector3(along, Mathf.Sin(a) * rr, Mathf.Cos(a) * rr) : c + new Vector3(Mathf.Cos(a) * rr, along, Mathf.Sin(a) * rr);
            Vector3 Out(float a) => alongX ? new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) : new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var axis = alongX ? Vector3.right : Vector3.up;
            float h = length * 0.5f;
            var t = T(mat);
            for (int s = 0; s < seg; s++)
            {
                float a0 = s * Mathf.PI * 2f / seg, a1 = (s + 1) * Mathf.PI * 2f / seg;
                int i = v.Count;
                v.Add(Axis(a0, radius, -h)); v.Add(Axis(a1, radius, -h)); v.Add(Axis(a1, radius, h)); v.Add(Axis(a0, radius, h));
                n.Add(Out(a0)); n.Add(Out(a1)); n.Add(Out(a1)); n.Add(Out(a0));
                for (int k = 0; k < 4; k++) uv.Add(Vector2.zero);
                // clockwise seen from outside, for both orientations
                t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i); t.Add(i + 3); t.Add(i + 2);
                // caps
                foreach (var side in new[] { -1f, 1f })
                {
                    int j = v.Count;
                    v.Add(c + axis * h * side); v.Add(Axis(a0, radius, h * side)); v.Add(Axis(a1, radius, h * side));
                    for (int k = 0; k < 3; k++) { n.Add(axis * side); uv.Add(Vector2.zero); }
                    if (side > 0) { t.Add(j); t.Add(j + 2); t.Add(j + 1); } else { t.Add(j); t.Add(j + 1); t.Add(j + 2); }
                }
            }
        }

        public GameObject Make(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            // double-sided (house rule): every triangle again, reversed, on its own vertices with the normal turned around
            int count = v.Count;
            for (int i = 0; i < count; i++) { v.Add(v[i]); n.Add(-n[i]); uv.Add(uv[i]); }
            foreach (var list in tris.Values)
            {
                int tc = list.Count;
                for (int i = 0; i < tc; i += 3) { list.Add(list[i] + count); list.Add(list[i + 2] + count); list.Add(list[i + 1] + count); }
            }
            var mesh = new Mesh { name = "rc_" + name };
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv);
            var keys = new List<string>(tris.Keys);
            mesh.subMeshCount = keys.Count;
            for (int i = 0; i < keys.Count; i++) mesh.SetTriangles(tris[keys[i]], i);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mats = new Material[keys.Count];
            for (int i = 0; i < keys.Count; i++) mats[i] = Mat(keys[i]);
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            return go;
        }
    }

    /// <summary>The Hilux from rc_hilux.mesh next to the DLL (tools/make_hilux.py); the simple built-in truck if that file is missing or bad.</summary>
    /// <summary>The folder with the car's model, textures and sounds.</summary>
    public static string AssetDir()
    {
#if WWC_FORGE   // built into WWC: its files are in BepInEx\plugins\WWC\RCBombCar
        return Path.GetDirectoryName(WWC.Client.ForgePlugin.PartAt("RCBombCar").Location)!;
#else
        return Path.GetDirectoryName(typeof(CarModel).Assembly.Location)!;
#endif
    }

    public static GameObject Build(Transform root, Transform[] wheels)
    {
        try
        {
            var dir = AssetDir();
            var parts = MeshFile.Load(Path.Combine(dir, "rc_hilux.mesh"));
            if (parts != null && parts.ContainsKey("body") && parts.ContainsKey("wheel"))
            {
                TextureDir = dir;
                var model = new GameObject("rc_model");
                model.transform.SetParent(root, false);
                foreach (var name in new[] { "body", "charge" })
                {
                    if (!parts.TryGetValue(name, out var part)) continue;
                    var go = new GameObject(name); go.transform.SetParent(model.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials = Array.ConvertAll(part.Materials, Mat);
                }
                // wheels: a pivot each (steer + spin), the left ones turned round so the rim faces out
                var mounts = new[] { new Vector3(-0.112f, 0.058f, 0.135f), new Vector3(0.112f, 0.058f, 0.135f), new Vector3(-0.112f, 0.058f, -0.140f), new Vector3(0.112f, 0.058f, -0.140f) };
                var wheel = parts["wheel"];
                var wheelMats = Array.ConvertAll(wheel.Materials, Mat);
                for (int i = 0; i < 4; i++)
                {
                    var pivot = new GameObject("wheel" + i); pivot.transform.SetParent(model.transform, false); pivot.transform.localPosition = mounts[i];
                    var mesh = new GameObject("tyre"); mesh.transform.SetParent(pivot.transform, false);
                    if (mounts[i].x < 0) mesh.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                    mesh.AddComponent<MeshFilter>().sharedMesh = wheel.Mesh;
                    mesh.AddComponent<MeshRenderer>().sharedMaterials = wheelMats;
                    wheels[i] = pivot.transform;
                }
                Hilux = true;
                return model;
            }
        }
        catch (Exception e) { Plugin.ErrorOnce("car model file", e); }
        return BuildSimple(root, wheels);
    }

    public static bool Hilux;
    static string TextureDir = "";

    static GameObject BuildSimple(Transform root, Transform[] wheels)
    {
        Hilux = false;
        var model = new GameObject("rc_model");
        model.transform.SetParent(root, false);
        var b = new Builder();
        // chassis and bumpers
        b.Box("chassis", new Vector3(0, 0.065f, 0), new Vector3(0.19f, 0.025f, 0.40f));
        b.Box("black", new Vector3(0, 0.075f, 0.215f), new Vector3(0.20f, 0.04f, 0.035f));
        b.Box("black", new Vector3(0, 0.075f, -0.215f), new Vector3(0.20f, 0.04f, 0.035f));
        // body: wide lower shell over the wheels, cab, hood slope, bed rails
        b.Box("paint", new Vector3(0, 0.118f, 0.0f), new Vector3(0.255f, 0.05f, 0.39f));
        b.Box("paint", new Vector3(0, 0.16f, 0.13f), new Vector3(0.22f, 0.035f, 0.13f), Quaternion.Euler(-8f, 0, 0));    // hood
        b.Box("paint", new Vector3(0, 0.18f, 0.0f), new Vector3(0.19f, 0.07f, 0.13f));                                    // cab
        b.Box("paint", new Vector3(0, 0.218f, -0.005f), new Vector3(0.175f, 0.012f, 0.11f));                              // roof
        b.Box("glass", new Vector3(0, 0.188f, 0.069f), new Vector3(0.17f, 0.05f, 0.012f), Quaternion.Euler(-28f, 0, 0)); // windscreen
        b.Box("glass", new Vector3(0.0955f, 0.19f, 0.0f), new Vector3(0.004f, 0.04f, 0.10f));
        b.Box("glass", new Vector3(-0.0955f, 0.19f, 0.0f), new Vector3(0.004f, 0.04f, 0.10f));
        b.Box("glass", new Vector3(0, 0.19f, -0.066f), new Vector3(0.15f, 0.035f, 0.004f));
        b.Box("paint", new Vector3(0.112f, 0.158f, -0.14f), new Vector3(0.018f, 0.035f, 0.14f));                           // bed rails
        b.Box("paint", new Vector3(-0.112f, 0.158f, -0.14f), new Vector3(0.018f, 0.035f, 0.14f));
        b.Box("paint", new Vector3(0, 0.158f, -0.205f), new Vector3(0.24f, 0.035f, 0.018f));
        b.Box("decal", new Vector3(0.1285f, 0.12f, 0.02f), new Vector3(0.003f, 0.022f, 0.16f));                            // side stripes
        b.Box("decal", new Vector3(-0.1285f, 0.12f, 0.02f), new Vector3(0.003f, 0.022f, 0.16f));
        b.Box("lamp", new Vector3(0.07f, 0.125f, 0.1955f), new Vector3(0.04f, 0.018f, 0.004f));
        b.Box("lamp", new Vector3(-0.07f, 0.125f, 0.1955f), new Vector3(0.04f, 0.018f, 0.004f));
        b.Box("black", new Vector3(0, 0.122f, 0.197f), new Vector3(0.07f, 0.03f, 0.004f));                                  // grille
        // the charge: two bricks in the bed, two bands of tape, detonator, wires
        b.Box("c4", new Vector3(0.031f, 0.162f, -0.14f), new Vector3(0.056f, 0.036f, 0.115f));
        b.Box("c4", new Vector3(-0.031f, 0.162f, -0.14f), new Vector3(0.056f, 0.036f, 0.115f));
        b.Box("label", new Vector3(0.031f, 0.1805f, -0.14f), new Vector3(0.04f, 0.002f, 0.06f));
        b.Box("label", new Vector3(-0.031f, 0.1805f, -0.14f), new Vector3(0.04f, 0.002f, 0.06f));
        b.Box("tape", new Vector3(0, 0.162f, -0.105f), new Vector3(0.126f, 0.041f, 0.022f));
        b.Box("tape", new Vector3(0, 0.162f, -0.175f), new Vector3(0.126f, 0.041f, 0.022f));
        b.Box("black", new Vector3(0, 0.196f, -0.138f), new Vector3(0.045f, 0.028f, 0.038f));                               // detonator
        b.Box("led", new Vector3(0.012f, 0.2115f, -0.128f), new Vector3(0.007f, 0.004f, 0.007f));
        b.Box("wire_red", new Vector3(0.008f, 0.2f, -0.095f), new Vector3(0.004f, 0.004f, 0.06f), Quaternion.Euler(12f, 0, 0));
        b.Box("black", new Vector3(-0.008f, 0.2f, -0.095f), new Vector3(0.004f, 0.004f, 0.06f), Quaternion.Euler(12f, 0, 0));
        b.Box("wire_red", new Vector3(0.024f, 0.183f, -0.12f), new Vector3(0.003f, 0.025f, 0.003f));                        // det cord into a brick
        // antenna
        b.Cylinder("black", new Vector3(-0.09f, 0.29f, -0.19f), 0.0025f, 0.28f, 6, alongX: false);
        b.Box("black", new Vector3(-0.09f, 0.432f, -0.19f), new Vector3(0.008f, 0.008f, 0.008f));
        b.Make("body", model.transform);

        // wheels: 0 FL, 1 FR, 2 RL, 3 RR
        var mounts = new[] { new Vector3(-0.118f, 0.055f, 0.135f), new Vector3(0.118f, 0.055f, 0.135f), new Vector3(-0.118f, 0.055f, -0.135f), new Vector3(0.118f, 0.055f, -0.135f) };
        for (int i = 0; i < 4; i++)
        {
            var w = new Builder();
            w.Cylinder("tyre", Vector3.zero, 0.05f, 0.048f, 18, alongX: true);
            for (int k = 0; k < 14; k++)
            {
                float a = k * Mathf.PI * 2f / 14f;
                var at = new Vector3(0, Mathf.Sin(a) * 0.051f, Mathf.Cos(a) * 0.051f);
                // y = along the tread, z = how far the knob stands out; the turn points local +Z outward
                w.Box("tyre", at, new Vector3(0.046f, 0.014f, 0.008f), Quaternion.Euler(-a * Mathf.Rad2Deg, 0, 0));
            }
            float outward = mounts[i].x > 0 ? 1f : -1f;
            w.Cylinder("rim", new Vector3(outward * 0.002f, 0, 0), 0.031f, 0.05f, 12, alongX: true);
            w.Box("black", new Vector3(outward * 0.026f, 0, 0), new Vector3(0.004f, 0.012f, 0.012f));
            var wheel = w.Make("wheel" + i, model.transform);
            wheel.transform.localPosition = mounts[i];
            wheels[i] = wheel.transform;
        }
        return model;
    }

    // ------------------------------------------------------------ materials

    static readonly Dictionary<string, (Color color, float spec, float gloss)> Look = new()
    {
        // the Hilux (rc_hilux.mesh); "paint" also colours the simple fallback truck
        ["paint"] = (Color.white, 0.45f, 0.62f),
        ["trim"] = (new Color(0.05f, 0.05f, 0.055f), 0.15f, 0.35f),
        ["chrome"] = (new Color(0.78f, 0.79f, 0.80f), 0.8f, 0.85f),
        ["glass"] = (new Color(0.04f, 0.05f, 0.06f), 0.7f, 0.88f),
        ["grille"] = (new Color(0.06f, 0.06f, 0.065f), 0.2f, 0.4f),
        ["headlight"] = (Color.white, 0.7f, 0.9f),
        ["taillight"] = (Color.white, 0.6f, 0.8f),
        ["tailgate"] = (Color.white, 0.4f, 0.6f),
        ["chassis"] = (new Color(0.12f, 0.12f, 0.13f), 0.2f, 0.4f),
        ["tyre"] = (new Color(0.045f, 0.045f, 0.045f), 0.04f, 0.15f),
        ["rim"] = (new Color(0.11f, 0.11f, 0.12f), 0.45f, 0.55f),
        ["bolt"] = (new Color(0.72f, 0.72f, 0.74f), 0.8f, 0.8f),
        ["reflector"] = (new Color(0.85f, 0.86f, 0.88f), 0.9f, 0.92f),
        ["amber"] = (new Color(0.95f, 0.48f, 0.06f), 0.6f, 0.8f),
        ["reverse"] = (new Color(0.88f, 0.88f, 0.86f), 0.6f, 0.8f),
        ["metal"] = (new Color(0.46f, 0.47f, 0.49f), 0.7f, 0.6f),
        ["spring"] = (new Color(0.72f, 0.12f, 0.08f), 0.5f, 0.6f),
        ["motor"] = (new Color(0.62f, 0.63f, 0.66f), 0.8f, 0.7f),
        ["plate"] = (Color.white, 0.3f, 0.5f),
        ["grille_tex"] = (Color.white, 0.35f, 0.55f),
        ["hook"] = (new Color(0.75f, 0.06f, 0.04f), 0.5f, 0.6f),
        ["rope"] = (new Color(0.74f, 0.73f, 0.69f), 0.1f, 0.3f),
        ["c4"] = (new Color(0.31f, 0.33f, 0.21f), 0.12f, 0.3f),
        ["label"] = (Color.white, 0.1f, 0.3f),
        ["tape"] = (new Color(0.42f, 0.43f, 0.44f), 0.35f, 0.55f),
        ["cap"] = (new Color(0.72f, 0.72f, 0.68f), 0.7f, 0.7f),
        ["wire_red"] = (new Color(0.7f, 0.05f, 0.04f), 0.3f, 0.5f),
        ["wire_black"] = (new Color(0.035f, 0.035f, 0.04f), 0.3f, 0.5f),
        ["receiver"] = (new Color(0.05f, 0.05f, 0.055f), 0.2f, 0.4f),
        ["led"] = (new Color(1f, 0.1f, 0.06f), 0.5f, 0.6f),
        // the simple truck only
        ["decal"] = (new Color(0.85f, 0.82f, 0.74f), 0.3f, 0.5f),
        ["black"] = (new Color(0.035f, 0.035f, 0.04f), 0.15f, 0.35f),
        ["lamp"] = (new Color(0.9f, 0.88f, 0.78f), 0.6f, 0.8f),
    };
    static readonly Dictionary<string, string> TextureOf = new()
    {
        ["tailgate"] = "tailgate.png", ["label"] = "c4label.png", ["paint"] = "paint.png", ["plate"] = "plate.png",
        ["grille_tex"] = "grille.png", ["headlight"] = "headlight.png", ["taillight"] = "taillight.png",
    };

    static Texture2D? LoadTexture(string key)
    {
        if (!TextureOf.TryGetValue(key, out var file) || TextureDir.Length == 0) return null;
        var path = Path.Combine(TextureDir, file);
        if (!File.Exists(path)) { Plugin.Warn(file + " not found; plain colour instead"); return null; }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "rc_" + key, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
        return ImageConversion.LoadImage(tex, File.ReadAllBytes(path)) ? tex : null;
    }
    static Shader? PickShader()
    {
        foreach (var name in new[] { "Standard", "p0/Reflective/Bumped Specular", "p0/Reflective/Bumped Specular SMap", "Legacy Shaders/Diffuse", "Diffuse", "Unlit/Color" })
        {
            var s = Shader.Find(name);
            if (s != null && s.isSupported) { ShaderUsed = name; return s; }
        }
        ShaderUsed = "none";
        return null;
    }

    static Material Mat(string key)
    {
        if (materials.TryGetValue(key, out var m) && m != null) return m;
        var (color, spec, gloss) = Look.TryGetValue(key, out var l) ? l : (Color.magenta, 0.2f, 0.4f);
        var shader = PickShader();
        m = new Material(shader ?? Shader.Find("Hidden/InternalErrorShader")) { name = "rc_" + key };
        if (ShaderUsed == "Standard")
        {
            m.SetColor("_Color", color);
            m.SetFloat("_Glossiness", gloss);
            m.SetFloat("_Metallic", key == "chrome" || key == "cap" || key == "bolt" || key == "reflector" || key == "metal" || key == "motor" ? 0.8f : 0f);
            var tex = LoadTexture(key);
            if (tex != null) m.SetTexture("_MainTex", tex);
            if (key == "led" || key == "lamp" || key == "headlight" || key == "taillight" || key == "amber")
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", color * (key == "led" ? 2f : key == "taillight" ? 0.55f : key == "amber" ? 0.25f : 0.45f));
                if (tex != null) m.SetTexture("_EmissionMap", tex);   // the lens lights up in its own colours
            }
        }
        else
        {
            // the game's own shaders: albedo alpha = specular strength, _SpecMap = gloss, a flat normal map, no reflection cube
            var albedo = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "rc_" + key + "_d" };
            var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = new Color(1f, 1f, 1f, spec); albedo.SetPixels(px); albedo.Apply(false, true);
            var glossTex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "rc_" + key + "_g" };
            for (int i = 0; i < 16; i++) px[i] = new Color(gloss, gloss, gloss, 1f); glossTex.SetPixels(px); glossTex.Apply(false, true);
            if (flatNormal == null)
            {
                flatNormal = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = "rc_flat_n" };
                for (int i = 0; i < 16; i++) px[i] = new Color(0.5f, 0.5f, 1f, 0.5f); flatNormal.SetPixels(px); flatNormal.Apply(false, true);
            }
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", albedo);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_SpecMap")) m.SetTexture("_SpecMap", glossTex);
            if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", flatNormal);
            if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", Color.black);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(spec, spec, spec, 1f));
            if (m.HasProperty("_Glossness")) m.SetFloat("_Glossness", gloss);
            if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", spec);
        }
        materials[key] = m;
        return m;
    }
}
