using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace WWCMags.Client;

/// <summary>
/// Gives WWC mags their look. Each one is a clone of a stock STANAG or PMAG; when the game builds any model of it (in a weapon, in hands,
/// on the ground, in the stash or the inspect window) the near model gets this mod's copy of the stock mesh, whose mirrored lower side
/// is split off so each side can carry its own design, and two material copies wearing the mag's own textures. The far model and the
/// window glass get copies too. Everything is put back before the model goes back to the pool, because the pool also serves the stock
/// mags. On any failure the stock look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "WWC Mags", "0.4.0")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Mags");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("Mags");
    private BepInEx.Configuration.ConfigFile Config => WWC.Client.ForgePlugin.Cfg;
#endif
#if WWC_FORGE
    private const string Sec = "Mags - ";   // WWC's F12 entry groups each part's settings under its name
#else
    private const string Sec = "";
#endif
    public const string Id = "local.wwcmags.client";
    internal static Plugin? Instance;
    private readonly Dictionary<string, MagEntry> mags = new();
    private readonly Dictionary<string, BodyInfo> bodies = new();
    private readonly Dictionary<string, Mesh> meshes = new();
    private readonly Dictionary<string, Mesh> splitMeshes = new();
    private readonly Dictionary<string, Texture2D> textures = new();
    private readonly Dictionary<string, MagMaterial> looks = new();
    private readonly Dictionary<(int Material, string Tpl, string Part), Material> copies = new();
    private readonly HashSet<string> errors = new();
    private string root = "";
    private Harmony? harmony;
    public int Applied { get; private set; }
    /// <summary>The GOON Raid Tape rolls of WWC Wraps (ids from the same fixed text WWC Wraps uses).</summary>
    private static readonly HashSet<string> TapeRolls = new() { WrapsTape("raid_black"), WrapsTape("raid_odg") };
    private BepInEx.Configuration.ConfigEntry<float>? tapeLoadBonus;

    private static string WrapsTape(string key)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("wwcwraps|tape|" + key)).Select(b => b.ToString("x2"))).Substring(0, 24);
    }

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            root = Path.GetDirectoryName(Info.Location)!;
            foreach (var key in new[] { "stanag", "pmag", "pmagw" })
            {
                var info = Path.Combine(root, "bodies", key, "body.json");
                if (File.Exists(info)) bodies[key] = JsonConvert.DeserializeObject<BodyInfo>(File.ReadAllText(info))!;
            }
            var indexPath = Path.Combine(root, "mags", "index.json");
            if (File.Exists(indexPath))
                foreach (var e in JsonConvert.DeserializeObject<List<MagEntry>>(File.ReadAllText(indexPath)) ?? new List<MagEntry>())
                    if (bodies.ContainsKey(e.body)) mags[e.tpl] = e;
            Instance = this;
            harmony = new Harmony(Id);
            // Every way the game makes a model of an item, including a mag inside a weapon (AttachMods builds each part with CreateItemAsync).
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)));
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(AssetPoolObject)).Where(m => m.Name == "ReturnToPool"))
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)));
            // A WWC mag with a GOON Raid Tape roll on it loads and unloads rounds faster (Widowz, 2026-11-17: "+1 to your reload speed").
            // The game reads Magazine.LoadUnloadModifier when it times each round and for the mag's stats, where minus is faster.
            tapeLoadBonus = Config.Bind(Sec + "Raid Tape", "Loading speed bonus (%)", 1f,
                "How much faster rounds go in and out of a WWC mag that has a GOON Raid Tape roll on it. 0 turns it off.");
            harmony.Patch(AccessTools.PropertyGetter(typeof(Magazine), nameof(Magazine.LoadUnloadModifier)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLoadUnloadModifier)));
            harmony.Patch(AccessTools.Method(typeof(Magazine), "SetAttributes"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterSetAttributes)));
            Logger.LogInfo($"WWC Mags ready: {mags.Count} mags on {bodies.Count} bodies.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Logger.LogError("WWC Mags unavailable; the mags keep the stock look. " + error);
        }
    }

    public bool Knows(string tpl) => mags.ContainsKey(tpl);

    /// <summary>True when this is a WWC mag with a Raid Tape roll in its tape slot.</summary>
    public static bool Taped(Magazine mag)
    {
        var p = Instance;
        if (p is null || mag is null || !p.mags.ContainsKey(mag.StringTemplateId)) return false;
        foreach (var item in mag.GetAllItems()) if (!ReferenceEquals(item, mag) && TapeRolls.Contains(item.StringTemplateId)) return true;
        return false;
    }

    private static void AfterLoadUnloadModifier(Magazine __instance, ref float __result)
    {
        try
        {
            var bonus = Instance?.tapeLoadBonus?.Value ?? 0f;
            if (bonus != 0f && Taped(__instance)) __result -= bonus;
        }
        catch { }   // never let a stat lookup throw into loading
    }

    /// <summary>The game lists a mag's loading speed only when its template has one, which the stock STANAG and PMAG don't; a WWC mag
    /// gets the line too, read live, so the tape's bonus shows in the inspect window when a roll is on.</summary>
    private static void AfterSetAttributes(Magazine __instance)
    {
        try
        {
            var p = Instance;
            if (p is null || !p.mags.ContainsKey(__instance.StringTemplateId) || __instance.Attributes.Any(a => Equals(a.Id, EItemAttributeId.LoadUnloadSpeed))) return;
            var line = new RangedItemAttribute(EItemAttributeId.LoadUnloadSpeed)
            {
                Name = ItemAttributeNames.GetName(EItemAttributeId.LoadUnloadSpeed),
                LessIsGood = true,
                Base = () => __instance.LoadUnloadModifier,
                StringValue = () => ModifierText(__instance.LoadUnloadModifier),
                DisplayType = () => EItemAttributeDisplayType.Compact,
            };
            __instance.Attributes.Add(line);
        }
        catch (Exception error) { Instance?.ReportOnce("attributes", error); }
    }

    private static string ModifierText(float value) => value > 0 ? $"<color=#870000>+{value}%</color>" : $"<color=#54c1ff>{value}%</color>";

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"WWC Mags: {key}: {error}");
    }

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        var p = Instance;
        if (p is null || __0 is null || !p.mags.ContainsKey(__0.StringTemplateId)) return;
        var tpl = __0.StringTemplateId;
        // A finished task is dressed now, so the synchronous callers (the inspect window) see the mag's own look straight away.
        if (__result.IsCompleted)
        {
            if (__result.Status == TaskStatus.RanToCompletion && __result.Result != null) p.Apply(__result.Result, tpl);
            return;
        }
        __result = p.ApplyAsync(__result, tpl);
    }

    private static void AfterCreateNow(Item __0, GameObject? __result)
    {
        var p = Instance;
        if (p is null || __0 is null || __result == null || !p.mags.ContainsKey(__0.StringTemplateId)) return;
        p.Apply(__result, __0.StringTemplateId);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, string tpl)
    {
        var model = await task;
        if (model != null) Apply(model, tpl);
        return model;
    }

    /// <summary>Puts the mag's look on the model, leaving the stock look if anything goes wrong. Also used by the QA probe.</summary>
    public void Apply(GameObject model, string tpl)
    {
        var state = model.GetComponent<MagVisualState>() ?? model.AddComponent<MagVisualState>();
        state.Restore();
        try
        {
            var entry = mags[tpl];
            var body = bodies[entry.body];
            var look = LookFor(entry);
            var done = 0;
            // A translucent mag is drawn with the window's own see-through glass material, so the rounds inside show.
            Material? glass = null;
            if (look.translucent != null)
                glass = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.sharedMesh.name.EndsWith("_glass", StringComparison.Ordinal))
                    .Select(f => f.GetComponent<MeshRenderer>()?.sharedMaterial).FirstOrDefault(m => m != null);
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (mesh == null || renderer == null) continue;
                var name = mesh.name;
                if (name == body.lod0)
                {
                    var donor = renderer.sharedMaterials.FirstOrDefault(m => m != null) ?? throw new InvalidDataException("stock mag has no material");
                    state.Remember(filter, renderer);
                    if (glass != null)
                    {   // see-through body, but the follower and the other inside parts stay solid and stock (their own submeshes, the opaque material)
                        filter.sharedMesh = SplitMeshFor(entry.body);
                        renderer.sharedMaterials = new[] { CopyFor(glass, entry, look, "ta"), CopyFor(glass, entry, look, "tb"), CopyFor(donor, entry, look, "a"), CopyFor(donor, entry, look, "b") };
                    }
                    else
                    {
                        filter.sharedMesh = MeshFor(entry.body);
                        renderer.sharedMaterials = new[] { CopyFor(donor, entry, look, "a"), CopyFor(donor, entry, look, "b") };
                    }
                    done++;
                }
                else if (name == body.lod1)
                {
                    var donor = renderer.sharedMaterials.FirstOrDefault(m => m != null);
                    if (donor == null) continue;
                    state.Remember(filter, renderer);
                    renderer.sharedMaterials = new[] { CopyFor(donor, entry, look, "lod1") };
                }
                else if (name.EndsWith("_glass", StringComparison.Ordinal) && look.window != null && !IsPlain(look.window.tint))
                {
                    var donor = renderer.sharedMaterials.FirstOrDefault(m => m != null);
                    if (donor == null) continue;
                    state.Remember(filter, renderer);
                    renderer.sharedMaterials = new[] { CopyFor(donor, entry, look, "glass") };
                }
            }
            if (done == 0) throw new InvalidDataException($"no {body.lod0} model found");
            Applied++;
        }
        catch (Exception error)
        {
            state.Restore();
            ReportOnce(tpl, error);
        }
    }

    private static bool IsPlain(float[]? tint) => tint == null || tint.Length < 3 || (Mathf.Approximately(tint[0], 1) && Mathf.Approximately(tint[1], 1) && Mathf.Approximately(tint[2], 1));

    private MagMaterial LookFor(MagEntry entry)
    {
        if (looks.TryGetValue(entry.tpl, out var look)) return look;
        var path = Path.Combine(root, "mags", entry.n.ToString(), "material.json");
        look = JsonConvert.DeserializeObject<MagMaterial>(File.ReadAllText(path)) ?? throw new InvalidDataException("empty " + path);
        return looks[entry.tpl] = look;
    }

    /// <summary>One copy of a stock material per mag and part, shared by every model of that mag.</summary>
    private Material CopyFor(Material donor, MagEntry entry, MagMaterial look, string part)
    {
        var key = (donor.GetInstanceID(), entry.tpl, part);
        if (copies.TryGetValue(key, out var copy) && copy != null) return copy;
        copy = new Material(donor) { name = $"{donor.name}_wwc{entry.n}_{part}", hideFlags = HideFlags.DontUnloadUnusedAsset };
        var dir = Path.Combine(root, "mags", entry.n.ToString());
        if (part == "glass")
        {
            var c = copy.HasProperty("_Color") ? copy.GetColor("_Color") : Color.white;
            var t = look.window!.tint!;
            copy.SetColor("_Color", new Color(Mathf.Clamp01(c.r * t[0]), Mathf.Clamp01(c.g * t[1]), Mathf.Clamp01(c.b * t[2]), c.a));
            if (copy.HasProperty("_Tinted")) copy.SetFloat("_Tinted", 1f);          // the glass shader only takes _Color's tint with this on
            return copies[key] = copy;
        }
        if (part == "ta" || part == "tb")
        {
            var set = part.Substring(1);
            copy.SetTexture("_MainTex", TextureFor(Path.Combine(dir, $"glass_{set}.png"), TextureKind.Colour, donor.mainTexture));
            if (copy.HasProperty("_SpecTex")) copy.SetTexture("_SpecTex", TextureFor(Path.Combine(dir, $"gloss_{set}.png"), TextureKind.Gloss, donor.mainTexture));
            // The plastic's tint goes through _Color with _Tinted on; the window glass reflects at full strength, which reads milky over a whole body.
            var tint = look.translucent?.tint;
            if (copy.HasProperty("_Color")) copy.SetColor("_Color", tint != null && tint.Length >= 3 ? new Color(Mathf.Clamp01(tint[0] * 1.6f + 0.08f), Mathf.Clamp01(tint[1] * 1.6f + 0.08f), Mathf.Clamp01(tint[2] * 1.6f + 0.08f), 1f) : Color.white);
            if (copy.HasProperty("_Tinted")) copy.SetFloat("_Tinted", 1f);
            if (copy.HasProperty("_GlobalReflectionStrength")) copy.SetFloat("_GlobalReflectionStrength", 0.45f);
            SetColour(copy, "_ReflectColor", look.reflectColor);
            return copies[key] = copy;
        }
        var colourFile = part == "lod1" ? "lod1.png" : $"colour_{part}.png";
        copy.SetTexture("_MainTex", TextureFor(Path.Combine(dir, colourFile), TextureKind.Colour, donor.mainTexture));
        if (part != "lod1")
        {
            if (copy.HasProperty("_SpecMap")) copy.SetTexture("_SpecMap", TextureFor(Path.Combine(dir, $"gloss_{part}.png"), TextureKind.Gloss, donor.mainTexture));
            var normal = Path.Combine(dir, $"normal_{part}.png");
            if (look.hasNormal && copy.HasProperty("_BumpMap") && File.Exists(normal)) copy.SetTexture("_BumpMap", TextureFor(normal, TextureKind.Normal, donor.mainTexture));
            if (copy.HasProperty("_Glossness")) copy.SetFloat("_Glossness", look.glossness);
            if (copy.HasProperty("_Specularness")) copy.SetFloat("_Specularness", look.specularness);
        }
        SetColour(copy, "_SpecColor", look.specColor);
        SetColour(copy, "_ReflectColor", look.reflectColor);
        SetColour(copy, "_Color", look.color);
        return copies[key] = copy;
    }

    private static void SetColour(Material m, string name, float[]? c)
    {
        if (c == null || c.Length < 3 || !m.HasProperty(name)) return;
        m.SetColor(name, new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f));
    }

    private enum TextureKind { Colour, Gloss, Normal }

    private Texture2D TextureFor(string path, TextureKind kind, Texture? donor)
    {
        if (textures.TryGetValue(path, out var loaded) && loaded != null) return loaded;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, kind == TextureKind.Normal) { name = "wwcmags_" + Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (!tex.LoadImage(File.ReadAllBytes(path), false)) throw new InvalidDataException("Could not read " + path);
        if (kind == TextureKind.Normal)
        {
            // The game's own normal maps keep X in alpha and Y in green.
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) { var c = px[i]; px[i] = new Color32(255, c.g, c.g, c.r); }
            tex.SetPixels32(px);
        }
        tex.wrapMode = donor != null ? donor.wrapMode : TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 5;
        tex.Apply(true, false);
        tex.Compress(true);
        tex.Apply(false, true);
        return textures[path] = tex;
    }

    /// <summary>The body's near model: the stock mesh with its mirrored lower left side split into a second submesh, every face doubled.</summary>
    private Mesh MeshFor(string body)
    {
        if (meshes.TryGetValue(body, out var mesh) && mesh != null) return mesh;
        var data = File.ReadAllBytes(Path.Combine(root, "bodies", body, "mesh.bin"));
        if (data.Length < 12 || data[0] != 'W' || data[1] != 'M' || data[2] != 'G' || data[3] != '1') throw new InvalidDataException("Not a WWC mag mesh: " + body);
        int count = BitConverter.ToInt32(data, 4), subCount = BitConverter.ToInt32(data, 8), off = 12;
        var verts = new Vector3[count]; var normals = new Vector3[count]; var tangents = new Vector4[count]; var uv = new Vector2[count];
        for (int i = 0; i < count; i++, off += 12) verts[i] = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
        for (int i = 0; i < count; i++, off += 12) normals[i] = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
        for (int i = 0; i < count; i++, off += 16) tangents[i] = new Vector4(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8), BitConverter.ToSingle(data, off + 12));
        for (int i = 0; i < count; i++, off += 8) uv[i] = new Vector2(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4));
        mesh = new Mesh { name = bodies[body].lod0, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.DontUnloadUnusedAsset };
        mesh.vertices = verts; mesh.normals = normals; mesh.tangents = tangents; mesh.uv = uv;
        mesh.subMeshCount = subCount;
        for (int s = 0; s < subCount; s++)
        {
            int n = BitConverter.ToInt32(data, off); off += 4;
            var idx = new int[n];
            for (int t = 0; t < n; t++, off += 4) idx[t] = BitConverter.ToInt32(data, off);
            mesh.SetTriangles(idx, s, false);
        }
        mesh.RecalculateBounds();
        mesh.UploadMeshData(false);
        return meshes[body] = mesh;
    }

    /// <summary>For a see-through mag: the body's mesh with the follower and the other loose parts inside it moved to submeshes 2 and 3
    /// (from pages a and b), so they can keep the stock look on an opaque material while the shell is drawn with the window's glass.
    /// The loose parts are every connected piece of the mesh but the largest (vertices welded by position), as the kit finds them.</summary>
    private Mesh SplitMeshFor(string body)
    {
        if (splitMeshes.TryGetValue(body, out var cached) && cached != null) return cached;
        var src = MeshFor(body);
        var verts = src.vertices;
        var ids = new int[verts.Length];
        var welded = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < verts.Length; i++)
        {
            var v = verts[i];
            var key = (Mathf.RoundToInt(v.x * 1e5f), Mathf.RoundToInt(v.y * 1e5f), Mathf.RoundToInt(v.z * 1e5f));
            if (!welded.TryGetValue(key, out var id)) welded[key] = id = welded.Count;
            ids[i] = id;
        }
        var parent = new int[welded.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var subs = new int[src.subMeshCount][];
        for (int s = 0; s < subs.Length; s++)
        {
            subs[s] = src.GetTriangles(s);
            for (int t = 0; t < subs[s].Length; t += 3)
            {
                int a = Find(ids[subs[s][t]]);
                parent[Find(ids[subs[s][t + 1]])] = a;
                parent[Find(ids[subs[s][t + 2]])] = a;
            }
        }
        var size = new Dictionary<int, int>();
        foreach (var tris in subs)
            for (int t = 0; t < tris.Length; t += 3) { int r = Find(ids[tris[t]]); size[r] = size.TryGetValue(r, out var c) ? c + 1 : 1; }
        int shell = size.OrderByDescending(p => p.Value).First().Key;
        var mesh = Instantiate(src);
        mesh.name = src.name; mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        mesh.subMeshCount = subs.Length * 2;
        for (int s = 0; s < subs.Length; s++)
        {
            var keep = new List<int>(); var stock = new List<int>();
            for (int t = 0; t < subs[s].Length; t += 3)
            {
                var to = Find(ids[subs[s][t]]) == shell ? keep : stock;
                to.Add(subs[s][t]); to.Add(subs[s][t + 1]); to.Add(subs[s][t + 2]);
            }
            mesh.SetTriangles(keep, s, false);
            mesh.SetTriangles(stock, subs.Length + s, false);
        }
        mesh.RecalculateBounds();
        mesh.UploadMeshData(false);
        Logger.LogInfo($"WWC Mags: {body} see-through mesh keeps {size.Count - 1} inside part(s) solid");
        return splitMeshes[body] = mesh;
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<MagVisualState>()?.Restore();
    }

    public sealed class MagEntry { public int n; public string name = ""; public string tpl = ""; public string body = ""; }
    public sealed class BodyInfo { public string body = ""; public string lod0 = ""; public string lod1 = ""; }
    public sealed class WindowLook { public string key = ""; public float[]? tint; }
    public sealed class TranslucentLook { public float[]? tint; public float opacity; }
    public sealed class MagMaterial
    {
        public float glossness = 1f, specularness = 1f;
        public float[]? specColor, reflectColor, color;
        public bool hasNormal;
        public WindowLook? window;
        public TranslucentLook? translucent;
    }
}

/// <summary>Holds what a pooled model looked like before the swap, so it can go back unchanged.</summary>
public sealed class MagVisualState : MonoBehaviour
{
    private readonly List<(MeshFilter Filter, Mesh Mesh, MeshRenderer Renderer, Material[] Materials)> saved = new();

    public void Remember(MeshFilter filter, MeshRenderer renderer) => saved.Add((filter, filter.sharedMesh, renderer, renderer.sharedMaterials));

    public bool Dressed => saved.Count > 0;

    public void Restore()
    {
        foreach (var (filter, mesh, renderer, materials) in saved)
        {
            if (filter != null) filter.sharedMesh = mesh;
            if (renderer != null) renderer.sharedMaterials = materials;
        }
        saved.Clear();
    }
}
