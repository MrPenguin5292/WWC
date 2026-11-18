using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using Comfort.Common;
using Diz.Resources;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace JackalArmbands.Client;

/// <summary>
/// Gives kit-made armbands their texture: each one is a clone of the white armband, and when the game builds any model of it (on a character, on the ground,
/// in the stash or the inspect window) its materials are swapped for copies wearing the player's texture. Everything is undone before the model goes back to
/// the pool, because the same pool also serves the plain white armband. On any failure the white donor look stays.
/// It also fits every armband (the game's own too) to the top being worn: the game keeps one armband shape per top and gives a top it has no
/// shape for (the WWC tees, other mods' tops, some stock tops) a shape made for another sleeve, which the cloth pokes through. See FitTable.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Jackal Armbands", "0.1.3")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Armbands");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("Armbands");
#endif
    public const string Id = "local.jackalarmbands.client";
    internal static Plugin? Instance;
    private readonly Dictionary<string, ArmbandEntry> armbands = new();
    private readonly Dictionary<string, Texture2D> textures = new();
    private readonly Dictionary<(int Material, string Tpl), Material> copies = new();
    private readonly HashSet<string> errors = new();
    private string root = "";
    private Harmony? harmony;

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            root = Path.GetDirectoryName(Info.Location)!;
            var indexPath = Path.Combine(root, "armbands", "index.json");
            if (File.Exists(indexPath))
                foreach (var e in JsonConvert.DeserializeObject<List<ArmbandEntry>>(File.ReadAllText(indexPath)) ?? new List<ArmbandEntry>()) armbands[e.tpl] = e;
            Instance = this;
            harmony = new Harmony(Id);
            // Every way the game makes a model of an item: worn and carried, loose on a raid floor, and in the stash and inspect previews.
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)));
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)));
            harmony.Patch(AccessTools.Method(typeof(ArmBandView), nameof(ArmBandView.SetBodyHandler)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSetBody)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterSetBody)));
            Logger.LogInfo($"Jackal Armbands ready: {armbands.Count} armbands; fit table for {FitTable.Bodies.Count} tops, {FitTable.Meshes.Count} top meshes, {FitTable.OffsetsCm.Count} bulky tops.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Logger.LogError("Jackal Armbands unavailable; armbands stay plain white. " + error);
        }
    }

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"Jackal Armbands: {key}: {error}");
    }

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        var p = Instance;
        if (p is null) return;
        if (p.armbands.ContainsKey(__0.StringTemplateId)) __result = p.ApplyAsync(__result, __0.StringTemplateId);
    }

    // The same swap for the calls that return straight away, which is how loose loot on a raid floor is made.
    private static void AfterCreateNow(Item __0, GameObject? __result)
    {
        var p = Instance;
        if (p is null || __result == null) return;
        var tpl = __0.StringTemplateId;
        if (!p.armbands.ContainsKey(tpl)) return;
        p.Apply(__result, tpl);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, string tpl)
    {
        var model = await task;
        if (model != null) Apply(model, tpl);
        return model;
    }

    /// <summary>Puts the armband's texture on every renderer of the model, leaving the donor look if anything goes wrong. Also used by the QA probe.</summary>
    public void Apply(GameObject model, string tpl)
    {
        var state = model.GetComponent<ArmbandVisualState>() ?? model.AddComponent<ArmbandVisualState>();
        state.Restore();
        try
        {
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidDataException("Armband donor has no renderer.");
            foreach (var renderer in renderers)
            {
                var original = renderer.sharedMaterials;
                state.Remember(renderer, original);
                renderer.sharedMaterials = original.Select(m => m == null ? null! : CopyFor(m, tpl)).ToArray();
            }
        }
        catch (Exception error)
        {
            state.Restore();
            ReportOnce(tpl, error);
        }
    }

    /// <summary>One copy of a donor material per armband, so a character and the stash share the same object.</summary>
    private Material CopyFor(Material donor, string tpl)
    {
        var key = (donor.GetInstanceID(), tpl);
        if (copies.TryGetValue(key, out var copy) && copy != null) return copy;
        var tex = TextureFor(armbands[tpl], donor.mainTexture);
        copy = new Material(donor) { name = donor.name + "_jackal", mainTexture = tex, hideFlags = HideFlags.DontUnloadUnusedAsset };
        // The kit prints designs on cloth with the stitched middle seam taken out; take the seam's crease out of the bump and gloss maps too.
        foreach (var prop in new[] { "_BumpMap", "_SpecMap" })
            if (copy.HasProperty(prop)) copy.SetTexture(prop, SmoothedMiddle(copy.GetTexture(prop)));
        return copies[key] = copy;
    }

    private readonly Dictionary<int, Texture> smoothed = new();

    /// <summary>
    /// A copy of one of the band's own textures (bump or gloss) with the middle seam of both halves replaced, the same way the kit does it for the
    /// cloth (tools/make_armband.py smooth_middle, without the brightness matching): read back from the GPU as stored, changed, kept uncompressed.
    /// The game's own texture is untouched; any failure keeps it.
    /// </summary>
    private Texture? SmoothedMiddle(Texture? source)
    {
        if (source == null) return source;
        var id = source.GetInstanceID();
        if (smoothed.TryGetValue(id, out var done) && done != null) return done;
        try
        {
            int w = source.width, h = source.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var old = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, true) { name = source.name + "_jackal_flat_middle", hideFlags = HideFlags.DontUnloadUnusedAsset };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            RenderTexture.active = old;
            RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels32();
            SmoothMiddle(px, w, h);
            tex.SetPixels32(px);
            tex.wrapMode = source.wrapMode; tex.filterMode = source.filterMode; tex.anisoLevel = source.anisoLevel;
            tex.Apply(true, true);
            return smoothed[id] = tex;
        }
        catch (Exception error)
        {
            ReportOnce("armband middle seam " + source.name, error);
            return smoothed[id] = source;
        }
    }

    /// <summary>Seam rows in the kit's 1024 space (top of the image is row 0), as in tools/make_armband.py MIDDLE_SEAMS.</summary>
    private static readonly (int First, int End)[] MiddleSeams = { (286, 356), (744, 820) };

    /// <summary>
    /// Fills each middle seam with the texture on either side of it mirrored into the gap (so each side meets its own edge without a join),
    /// crossfaded in the middle, then softens the gap's two edges. Pixels are bottom row first, as Unity stores them.
    /// </summary>
    internal static void SmoothMiddle(Color32[] px, int w, int h)
    {
        var k = h / 1024f;
        int Row(int imageRow) => (h - 1 - Mathf.Clamp(imageRow, 0, h - 1)) * w;
        foreach (var (first, end) in MiddleSeams)
        {
            int a = Mathf.RoundToInt(first * k), b = Mathf.RoundToInt(end * k), n = b - a;
            if (n <= 0 || a < 1 || b >= h) continue;
            var fade = Mathf.Max(1, Mathf.RoundToInt(14 * k));
            var gap = new Color32[n * w];
            for (var i = 0; i < n; i++)
            {
                int up = Row(a - 1 - i), down = Row(b + (n - 1 - i));
                var mix = Mathf.Clamp01((i - (n / 2f - fade)) / (2f * fade));
                for (var x = 0; x < w; x++) gap[i * w + x] = Color32.Lerp(px[up + x], px[down + x], mix);
            }
            for (var i = 0; i < n; i++) Array.Copy(gap, i * w, px, Row(a + i), w);
            var r = Mathf.Max(1, Mathf.RoundToInt(3 * k));
            foreach (var edge in new[] { a, b })
            {
                int lo = Mathf.Max(edge - r, 1), hi = Mathf.Min(edge + r, h - 1);
                var copy = new Color32[(hi - lo + 2) * w];
                for (var y = lo - 1; y <= hi; y++) Array.Copy(px, Row(y), copy, (y - lo + 1) * w, w);
                for (var y = lo; y < hi; y++)
                    for (var x = 0; x < w; x++)
                    {
                        Color32 p0 = copy[(y - lo) * w + x], p1 = copy[(y - lo + 1) * w + x], p2 = copy[(y - lo + 2) * w + x];
                        px[Row(y) + x] = new Color32((byte)((p0.r + 2 * p1.r + p2.r) / 4), (byte)((p0.g + 2 * p1.g + p2.g) / 4), (byte)((p0.b + 2 * p1.b + p2.b) / 4), (byte)((p0.a + 2 * p1.a + p2.a) / 4));
                    }
            }
        }
    }

    private Texture2D TextureFor(ArmbandEntry entry, Texture? donor)
    {
        if (textures.TryGetValue(entry.tpl, out var loaded) && loaded != null) return loaded;
        var path = Path.Combine(root, "armbands", entry.n.ToString(), "colour.png");
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = "jackal_armband_" + entry.n, hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (!tex.LoadImage(File.ReadAllBytes(path), false)) throw new InvalidDataException("Could not read " + path);
        // Keep the donor's sampling: the band's seam relies on the same wrap as the game's own texture.
        if (donor != null) { tex.wrapMode = donor.wrapMode; tex.filterMode = donor.filterMode; tex.anisoLevel = donor.anisoLevel; }
        tex.Apply(true, false);        // mips from the full-size image, while it can still make them
        tex.Compress(true);
        tex.Apply(false, true);
        return textures[entry.tpl] = tex;
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance == null) return;
        __instance.GetComponent<ArmbandVisualState>()?.Restore();
        foreach (var s in __instance.GetComponentsInChildren<ArmbandFitState>(true)) s.Restore();
    }

    // ---- Fit to the top: which armband shape a top wears, and how far out it sits ----

    private static readonly AccessTools.FieldRef<ArmBandView, Dictionary<string, Mesh[]>> shapesOf = AccessTools.FieldRefAccess<ArmBandView, Dictionary<string, Mesh[]>>("_dict");
    private static readonly AccessTools.FieldRef<ArmBandView, Diz.Skinning.Skin[]> skinsOf = AccessTools.FieldRefAccess<ArmBandView, Diz.Skinning.Skin[]>("_main");
    private static readonly AccessTools.FieldRef<Dress, PlayerBody> wearerOf = AccessTools.FieldRefAccess<Dress, PlayerBody>("PlayerBody");
    private readonly Dictionary<string, (string Target, float OffsetCm)> fitted = new();
    private readonly Dictionary<(int Mesh, int Mm), Mesh> eased = new();

    /// <summary>
    /// Runs before the game picks the armband's shape for a top. Swaps the top's id for the one whose shape fits its sleeve best; the game's
    /// own lookup then does the rest. Hands the postfix how far out the band should sit on this top (0 for almost every top).
    /// Any failure leaves the game's own choice.
    /// </summary>
    private static void BeforeSetBody(ArmBandView __instance, ref string bodyCustomizationId, out float __state)
    {
        __state = 0f;
        var p = Instance;
        if (p is null || __instance == null || string.IsNullOrEmpty(bodyCustomizationId)) return;
        try
        {
            var shapes = shapesOf(__instance);
            if (shapes is null || shapes.Count == 0) return;
            var (target, offset) = p.FitFor(bodyCustomizationId, __instance, shapes);
            if (target != bodyCustomizationId && shapes.ContainsKey(target)) bodyCustomizationId = target;
            __state = offset;
        }
        catch (Exception error)
        {
            p.ReportOnce("armband fit", error);
        }
    }

    /// <summary>
    /// Runs after the game has put the chosen shape on the band. On a bulky top whose sleeve still folds through the band, swaps in a copy of
    /// that shape moved out from the arm by the same distance all the way round: the band keeps its height, thickness and print, it just sits
    /// further out. The next shape change, or the model going back to the pool, puts the game's own mesh back.
    /// </summary>
    private static void AfterSetBody(ArmBandView __instance, float __state)
    {
        var p = Instance;
        if (p is null || __instance == null) return;
        var state = __instance.GetComponent<ArmbandFitState>();
        state?.Forget();   // the game just put its own meshes back
        if (__state <= 0f) return;
        try
        {
            var skins = skinsOf(__instance);
            if (skins is null) return;
            foreach (var skin in skins)
            {
                var smr = skin != null ? skin.SkinnedMeshRenderer : null;
                if (smr == null || smr.sharedMesh == null) continue;
                var copy = p.Eased(smr, __state);
                if (copy == smr.sharedMesh) continue;
                (state ??= __instance.gameObject.AddComponent<ArmbandFitState>()).Remember(smr, smr.sharedMesh);
                smr.sharedMesh = copy;
            }
        }
        catch (Exception error)
        {
            state?.Restore();
            p.ReportOnce("armband offset", error);
        }
    }

    /// <summary>The top whose armband shape this top should wear (itself when the game's own shape already fits), and how far out it sits.</summary>
    private (string Target, float OffsetCm) FitFor(string body, ArmBandView view, Dictionary<string, Mesh[]> shapes)
    {
        if (fitted.TryGetValue(body, out var known)) return known;
        string target, how, measured = body;
        var final = true;
        if (FitTable.Bodies.TryGetValue(body, out var pick)) { target = pick; how = "measured"; }
        else if (shapes.ContainsKey(body)) { target = body; how = "the game's own shape"; }
        else
        {
            // A top the game has no shape for: a cloned top keeps its donor's mesh, so its mesh names say which measured top it is.
            target = FitTable.Fallback; how = "no measured mesh, the least-clipping shape"; measured = "";
            var (names, sure) = MeshNamesOf(body, view);
            foreach (var name in names)
            {
                if (name is null || !FitTable.Meshes.TryGetValue(name, out var stock)) continue;
                target = FitTable.Bodies.TryGetValue(stock, out var s) ? s : stock;
                measured = stock;
                how = $"mesh {name}, same as {stock}";
                break;
            }
            final = sure;   // a guess from the wearer's current skin may be the old top's; try again next time
        }
        if (!shapes.ContainsKey(target)) { target = body; how += ", not in this armband's list"; }
        var offset = measured.Length > 0 && FitTable.OffsetsCm.TryGetValue(measured, out var cm) ? cm : 0f;
        if (final) fitted[body] = (target, offset);
        Logger.LogInfo($"Armband fit: top {body} wears the shape of {target} ({how}){(offset > 0 ? $", {offset:0.0} cm further out" : "")}.");
        return (target, offset);
    }

    /// <summary>
    /// A copy of the band shape on this renderer with every point moved the same distance straight out from the upper arm: along the arm
    /// nothing moves, so the band keeps its height, thickness and place on the arm. The arm's line comes from the shape's own bind pose (upper arm
    /// bone to forearm bone), the centre from the band's best-fitting circle around that line. Returns the shape unchanged if it can't be read.
    /// </summary>
    private Mesh Eased(SkinnedMeshRenderer smr, float cm)
    {
        var shape = smr.sharedMesh;
        var mm = Mathf.RoundToInt(cm * 10f);
        var key = (shape.GetInstanceID(), mm);
        if (eased.TryGetValue(key, out var done) && done != null) return done;
        if (!shape.isReadable) { ReportOnce("armband shape not readable: " + shape.name, new InvalidOperationException("left as the game has it")); return eased[key] = shape; }
        var v = shape.vertices;
        if (v.Length < 8) return eased[key] = shape;
        var (centre, axis) = ArmFrame(smr, shape, v) ?? RingFrame(v);
        var d = mm / 1000f;
        for (var i = 0; i < v.Length; i++)
        {
            var rel = v[i] - centre;
            var radial = rel - axis * Vector3.Dot(rel, axis);
            var len = radial.magnitude;
            if (len > 1e-5f) v[i] += radial * (d / len);
        }
        var copy = Instantiate(shape);
        copy.name = shape.name + "_out" + mm + "mm";
        copy.hideFlags = HideFlags.DontUnloadUnusedAsset;
        copy.vertices = v;
        copy.RecalculateBounds();
        return eased[key] = copy;
    }

    /// <summary>
    /// The upper arm's line in the shape's own (bind) space: from the upper arm bone to the forearm bone, read from the shape's bind poses, and
    /// the centre of the band's best-fitting circle around it. Null when the shape isn't bound to both bones.
    /// </summary>
    internal static (Vector3 Centre, Vector3 Axis)? ArmFrame(SkinnedMeshRenderer smr, Mesh shape, Vector3[] v)
    {
        var bones = smr.bones;
        var binds = shape.bindposes;
        int up = -1, fore = -1;
        for (var i = 0; i < bones.Length && i < binds.Length; i++)
        {
            if (bones[i] == null) continue;
            if (bones[i].name == "Base HumanLUpperarm") up = i;
            else if (bones[i].name == "Base HumanLForearm1") fore = i;
        }
        if (up < 0 || fore < 0) return null;
        Vector3 at(int i) => binds[i].inverse.MultiplyPoint3x4(Vector3.zero);
        var axis = at(fore) - at(up);
        if (axis.sqrMagnitude < 1e-8f) return null;
        axis.Normalize();
        return (CircleCentre(v, axis), axis);
    }

    /// <summary>Centre of the best-fitting circle through points seen down an axis (least squares), placed at the points' mean height.</summary>
    internal static Vector3 CircleCentre(Vector3[] v, Vector3 axis)
    {
        var mean = Vector3.zero;
        foreach (var p in v) mean += p;
        mean /= v.Length;
        var e1 = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        var e2 = Vector3.Cross(axis, e1);
        double s11 = 0, s12 = 0, s13 = 0, s22 = 0, s23 = 0, s33 = v.Length, b1 = 0, b2 = 0, b3 = 0;
        foreach (var p in v)
        {
            var q = p - mean;
            double x = Vector3.Dot(q, e1), y = Vector3.Dot(q, e2), r = x * x + y * y;
            s11 += 4 * x * x; s12 += 4 * x * y; s13 += 2 * x; s22 += 4 * y * y; s23 += 2 * y; b1 += 2 * x * r; b2 += 2 * y * r; b3 += r;
        }
        double det = s11 * (s22 * s33 - s23 * s23) - s12 * (s12 * s33 - s23 * s13) + s13 * (s12 * s23 - s22 * s13);
        if (Math.Abs(det) < 1e-12) return mean;
        double cx = (b1 * (s22 * s33 - s23 * s23) - s12 * (b2 * s33 - s23 * b3) + s13 * (b2 * s23 - s22 * b3)) / det;
        double cy = (s11 * (b2 * s33 - s23 * b3) - b1 * (s12 * s33 - s23 * s13) + s13 * (s12 * b3 - b2 * s13)) / det;
        return mean + e1 * (float)cx + e2 * (float)cy;
    }

    /// <summary>Centre and axis of a ring of points when the bones can't say: the axis is the direction the points spread least along.</summary>
    internal static (Vector3 Centre, Vector3 Axis) RingFrame(Vector3[] v)
    {
        var mean = Vector3.zero;
        foreach (var p in v) mean += p;
        mean /= v.Length;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in v)
        {
            var q = p - mean;
            xx += q.x * q.x; xy += q.x * q.y; xz += q.x * q.z; yy += q.y * q.y; yz += q.y * q.z; zz += q.z * q.z;
        }
        Vector3 Mul(Vector3 a) => new(xx * a.x + xy * a.y + xz * a.z, xy * a.x + yy * a.y + yz * a.z, xz * a.x + yz * a.y + zz * a.z);
        Vector3 Power(Vector3 a, Vector3? orth)
        {
            for (var i = 0; i < 64; i++)
            {
                a = Mul(a);
                if (orth is Vector3 o) a -= o * Vector3.Dot(a, o);
                if (a.sqrMagnitude < 1e-20f) break;
                a.Normalize();
            }
            return a;
        }
        var e1 = Power(new Vector3(0.577f, 0.577f, 0.577f), null);
        var seed = Mathf.Abs(e1.x) < 0.9f ? Vector3.right : Vector3.up;
        var e2 = Power((seed - e1 * Vector3.Dot(seed, e1)).normalized, e1);
        var axis = Vector3.Cross(e1, e2).normalized;
        return (CircleCentre(v, axis), axis);
    }

    /// <summary>Mesh names of a top: from its loaded prefab (sure), or else from what the wearer has on right now (may lag behind a change).</summary>
    private static (List<string?> Names, bool Sure) MeshNamesOf(string body, ArmBandView view)
    {
        var names = new List<string?>();
        try
        {
            var key = Singleton<CustomizationSolver>.Instance?.GetBundle(body);
            var prefab = key is null ? null : Singleton<IEasyAssets>.Instance?.GetAsset<LoddedSkin>(key);
            if (prefab != null)
            {
                foreach (var r in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (r.sharedMesh != null) names.Add(r.sharedMesh.name);
                if (names.Count > 0) return (names, true);
            }
        }
        catch (Exception)
        {
            // the top's bundle is not loaded (GetAsset throws then); fall through to the wearer
        }
        var wearer = wearerOf(view);
        if (wearer != null && wearer.BodySkins.TryGetValue(EBodyModelPart.Body, out var skin) && skin != null)
            foreach (var r in skin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.sharedMesh != null) names.Add(r.sharedMesh.name);
        return (names, false);
    }

    public sealed class ArmbandEntry { public int n; public string name = ""; public string tpl = ""; }
}

/// <summary>The game's own band meshes under a moved-out copy, so a pooled model goes back unchanged.</summary>
public sealed class ArmbandFitState : MonoBehaviour
{
    private readonly List<(SkinnedMeshRenderer Renderer, Mesh Mesh)> saved = new();

    public void Remember(SkinnedMeshRenderer renderer, Mesh mesh) => saved.Add((renderer, mesh));

    /// <summary>The game has already put its own meshes back (it does on every shape change): just drop the record.</summary>
    public void Forget() => saved.Clear();

    public void Restore()
    {
        foreach (var (renderer, mesh) in saved)
            if (renderer != null && mesh != null) renderer.sharedMesh = mesh;
        saved.Clear();
    }
}

/// <summary>Holds what a pooled model looked like before the swap, so it can go back unchanged.</summary>
public sealed class ArmbandVisualState : MonoBehaviour
{
    private readonly List<(Renderer Renderer, Material[] Materials)> saved = new();

    public void Remember(Renderer renderer, Material[] materials) => saved.Add((renderer, materials));

    public void Restore()
    {
        foreach (var (renderer, materials) in saved)
            if (renderer != null) renderer.sharedMaterials = materials;
        saved.Clear();
    }
}
