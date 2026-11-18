using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Grunts.Client;

/// <summary>
/// The pack's Grunt models on Grunt bots. Each look (a-d) is one skinned mesh already bound to Tarkov's body skeleton (bone paths under
/// Root_Joint, bind poses in Root_Joint space; tools/bind_grunt.py), so the zombie animator, hit boxes, ragdoll and corpse all keep
/// working: the mesh rides the bot's own bones. The stock zombie body/head/legs renderers are switched off while ours is on; everything is
/// put back when the bot's object goes back to the pool. Materials are copies of the zombie body's own material with the pack's textures.
/// Fail soft: any problem leaves the stock zombie look.
/// </summary>
internal static class GruntModel
{
    private sealed class Look { public string Key = ""; public Mesh Mesh = null!; public string[] Bones = Array.Empty<string>(); public JArray Materials = new(); }
    private static AssetBundle? bundle;
    private static readonly Dictionary<string, Look> looks = new();
    private static readonly Dictionary<string, Texture2D?> textures = new();
    private static readonly Dictionary<(Material, string), Material> materials = new();
    private static Shader? cutout;
    private static bool shaderSearched, loggedCutout;
    public static int Dressed { get; private set; }
    public static bool Ready => looks.Count > 0;
    private static Look? Machete;
    /// <summary>The Grunt machete's grip point and blade direction in the right palm's frame (for lining up the game's own weapon).</summary>
    public static Vector3 Grip, Blade = Vector3.down;
    public static bool HasMachete => Machete != null;
    private static Vector3 V3(JToken? t) => t is JArray a && a.Count == 3 ? new Vector3((float)a[0], (float)a[1], (float)a[2]) : Vector3.zero;

    public static void Load(string path, ManualLogSource log)
    {
        if (!File.Exists(path)) { log.LogWarning("Grunts: grunts_model.bundle not found; Grunts keep the zombie look"); return; }
        bundle = AssetBundle.LoadFromFile(path);
        if (bundle == null) { log.LogWarning("Grunts: grunts_model.bundle could not be opened"); return; }
        var index = JObject.Parse(bundle.LoadAsset<TextAsset>("assets/generated/grunts_model_index.txt").text);
        if (index["machete"] is JObject mj)
        {
            var mm = bundle.LoadAsset<Mesh>("assets/generated/model/" + (string)mj["mesh"] + ".asset");
            if (mm != null)
            {
                Machete = new Look { Key = "machete", Mesh = mm, Materials = (JArray)mj["materials"]! };
                Grip = V3(mj["grip"]); Blade = V3(mj["blade"]).normalized;
            }
        }
        foreach (var p in index.Properties())
        {
            if (p.Name == "machete") continue;
            var mesh = bundle.LoadAsset<Mesh>("assets/generated/model/" + (string)p.Value["mesh"] + ".asset");
            if (mesh == null) { log.LogWarning("Grunts: mesh missing for look " + p.Name); continue; }
            looks[p.Name] = new Look { Key = p.Name, Mesh = mesh, Bones = p.Value["bones"]!.Select(b => (string)b!).ToArray(), Materials = (JArray)p.Value["materials"]! };
        }
        log.LogInfo($"Grunts: {looks.Count} Grunt looks loaded");
    }

    private static Texture2D? Tex(string? name)
    {
        if (string.IsNullOrEmpty(name) || bundle == null) return null;
        if (textures.TryGetValue(name!, out var t)) return t;
        t = bundle.LoadAsset<Texture2D>("assets/generated/tex/" + name + ".png");
        textures[name!] = t;
        return t;
    }

    private static Shader? Cutout()
    {
        if (shaderSearched) return cutout;
        shaderSearched = true;
        foreach (var n in new[] { "p0/Reflective/Bumped Specular SMap Transparent Cutoff", "p0/Cutout/Bumped Specular SMap", "p0/Reflective/Bumped Specular SMap_Cutout", "Legacy Shaders/Transparent/Cutout/Bumped Specular" })
        {
            var s = Shader.Find(n);
            if (s != null) { cutout = s; Plugin.Instance?.Say("cutout shader: " + n); break; }
        }
        if (cutout == null) Plugin.Instance?.Note("no cutout shader found; torn cloth edges draw solid");
        return cutout;
    }

    /// <summary>Material for one submesh: a copy of the stock body material (its shader and settings) with the pack's textures.</summary>
    private static Material Mat(Material donor, Look look, int i)
    {
        var key = (donor, look.Key + "/" + i);
        if (materials.TryGetValue(key, out var m)) return m;
        var info = i < look.Materials.Count ? look.Materials[i] : null;
        m = new Material(donor) { name = "Grunt_" + (string?)info?["name"] };
        var a = Tex((string?)info?["albedo"]); var n = Tex((string?)info?["normal"]); var s = Tex((string?)info?["spec"]);
        if (a != null) m.SetTexture("_MainTex", a);
        if (n != null && m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", n);
        if (m.HasProperty("_SpecMap")) m.SetTexture("_SpecMap", s != null ? s : Texture2D.blackTexture);
        if (m.HasProperty("_DecalsTex")) m.SetTexture("_DecalsTex", null);
        // The body shader takes specular strength from the colour map's alpha: opaque parts carry the pack's phong mask there (built in),
        // cut-out cloth needs its alpha for the holes, so it gets a low specular instead (its alpha is ~1 everywhere it is drawn).
        bool cut = (bool?)info?["alphatest"] == true;
        if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", donor.GetFloat("_Specularness") * Plugin.Shine.Value * (cut ? 0.12f : 1f));
        if (cut)
        {
            // the cut-out shader also mirrors the environment cubemap by colour alpha: wet-looking white streaks on the cloth (run 5 stills)
            if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", Color.black);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(0.15f, 0.15f, 0.15f, 1f));
            if (!loggedCutout)
            {
                loggedCutout = true;
                var sh = m.shader; var names = new List<string>();
                for (int k = 0; k < sh.GetPropertyCount(); k++) names.Add(sh.GetPropertyName(k) + ":" + sh.GetPropertyType(k));
                Plugin.Instance?.Say("cutout shader properties: " + string.Join(", ", names));
            }
        }
        if ((bool?)info?["alphatest"] == true)
        {
            var c = Cutout();
            if (c != null) m.shader = c;
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", (float?)info?["cutoff"] ?? 0.5f);
            m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = 2450;
        }
        materials[key] = m;
        return m;
    }

    /// <summary>The body's bones by name, first found, leaving out everything inside a weapon. The machete the Grunt holds (under the right
    /// palm's weapon_Hand_root) carries its own "Base HumanRDigit41/42/43" chain, which a plain search found before the hand's real ring
    /// finger; that copy sits wherever the hidden weapon is (once 10 km under the map) and drew a streak from the hand to the floor.</summary>
    public static Dictionary<string, Transform> BodyBones(Transform root)
    {
        var byName = new Dictionary<string, Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (!byName.ContainsKey(t.name) && !InWeapon(t, root)) byName[t.name] = t;
        return byName;
    }

    private static bool InWeapon(Transform t, Transform root)
    {
        for (var a = t; a != null && a != root; a = a.parent)
            if (a.name.StartsWith("weapon", StringComparison.OrdinalIgnoreCase) || a.name.StartsWith("Weapon_root", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Dress one Grunt (idempotent). Returns false when it could not (the stock look stays).</summary>
    public static bool Dress(Player p)
    {
        if (!Ready || p == null || p.PlayerBones == null) return false;
        var body = p.PlayerBody;
        if (body == null) return false;
        var existing = body.GetComponentInChildren<GruntSkin>(true);
        if (existing != null) { existing.Enforce(); return true; }
        var root = p.PlayerBones.RootJoint;
        if (root == null) return false;
        var look = looks.TryGetValue(((char)('a' + Grunt.Look(p))).ToString(), out var l) ? l : looks.Values.First();
        // by name: the zombie body uses the game's simplified skeleton (no Spine1/Thigh2 in between), like the stock Skin component does
        var byName = BodyBones(root);
        var bones = new Transform[look.Bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            var name = look.Bones[i].Substring(look.Bones[i].LastIndexOf('/') + 1);
            if (!byName.TryGetValue(name, out bones[i])) { Plugin.Instance?.Note("bone not found: " + name); return false; }
        }
        var stock = body.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && r.gameObject.GetComponent<GruntSkin>() == null).ToList();
        var donor = stock.Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.HasProperty("_MainTex"));
        if (donor == null) { Plugin.Instance?.Note("no stock body material to copy"); return false; }
        var go = new GameObject("GruntSkin_" + look.Key);
        go.transform.SetParent(root.parent, false);
        go.transform.localPosition = root.localPosition; go.transform.localRotation = root.localRotation; go.transform.localScale = Vector3.one;
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = look.Mesh;
        smr.bones = bones;
        smr.rootBone = bones[0];
        smr.sharedMaterials = Enumerable.Range(0, look.Mesh.subMeshCount).Select(i => Mat(donor, look, i)).ToArray();
        smr.updateWhenOffscreen = false;
        smr.localBounds = new Bounds(Vector3.zero, new Vector3(2.5f, 2.5f, 2.5f));
        smr.shadowCastingMode = ShadowCastingMode.On;
        smr.quality = SkinQuality.Bone4;
        var skin = go.AddComponent<GruntSkin>();
        skin.Init(smr, stock);
        // his own machete, rigid on the right palm (the game's melee weapon is hidden and lined up with it by GruntMotion)
        if (Machete != null && byName.TryGetValue("Base HumanRPalm", out var palm))
        {
            var mgo = new GameObject("GruntMachete");
            mgo.transform.SetParent(palm, false);
            mgo.AddComponent<MeshFilter>().sharedMesh = Machete.Mesh;
            var mr = mgo.AddComponent<MeshRenderer>();
            mr.sharedMaterials = Enumerable.Range(0, Machete.Mesh.subMeshCount).Select(i => Mat(donor, Machete, i)).ToArray();
            mr.shadowCastingMode = ShadowCastingMode.On;
            skin.Machete = mgo;
        }
        Dressed++;
        return true;
    }

    /// <summary>Before a player object goes back to the pool: our skin off, the stock renderers back on.</summary>
    public static void Undress(GameObject pooled)
    {
        foreach (var s in pooled.GetComponentsInChildren<GruntSkin>(true)) s.Restore();
    }
}

/// <summary>Our renderer on a Grunt, and the stock renderers it switched off (to give back).</summary>
internal sealed class GruntSkin : MonoBehaviour
{
    private SkinnedMeshRenderer? mine;
    public GameObject? Machete;
    private readonly List<(SkinnedMeshRenderer R, bool Off)> stock = new();

    public void Init(SkinnedMeshRenderer smr, List<SkinnedMeshRenderer> renderers)
    {
        mine = smr;
        foreach (var r in renderers) stock.Add((r, r.forceRenderingOff));
        Enforce();
    }

    /// <summary>Keep the stock zombie body hidden, including renderers the game adds after we dressed him (LOD switches, late parts).</summary>
    public void Enforce()
    {
        foreach (var (r, _) in stock) if (r != null && !r.forceRenderingOff) r.forceRenderingOff = true;
        var body = transform.parent != null ? transform.parent.GetComponentInParent<PlayerBody>() : null;
        if (body == null) return;
        foreach (var r in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r == mine || r.sharedMesh == null || stock.Exists(s => s.R == r)) continue;
            stock.Add((r, r.forceRenderingOff));
            r.forceRenderingOff = true;
        }
    }

    public void Restore()
    {
        foreach (var (r, off) in stock) if (r != null) r.forceRenderingOff = off;
        stock.Clear();
        if (Machete != null) Destroy(Machete);
        if (mine != null) Destroy(mine.gameObject);
    }
}
