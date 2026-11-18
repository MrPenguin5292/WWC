using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WWCHelmetDisplays.Client;

/// <summary>
/// Dresses the WWC helmet displays (a documents case wearing a poster's model until now) with their own hardware, and shows the real gear in their grids:
/// helmets on padded crowns cut from the game's mannequin head, and on the busts a live copy of the hideout mannequin that the game itself dresses.
/// Every change is made on objects the game already spawns and is undone before they go back to the pool. On any failure the poster look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "WWC Helmet Displays", "0.1.4")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Helmet Displays");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("HelmetDisplays");
#endif
    public const string Id = "local.wwchelmetdisplays.client";
    internal static Plugin? Instance;
    internal readonly Dictionary<string, string> KeyByTpl = new();
    internal readonly Dictionary<string, Layout> Layouts = new();
    internal Mesh EmptyMesh = null!;
    internal DisplayStates States = null!;
    private readonly Dictionary<string, Mesh> meshes = new();
    private readonly Dictionary<string, Texture2D> textures = new();
    private readonly Dictionary<string, Material> materials = new();
    private readonly HashSet<string> errors = new();
    private string root = "";
    private Harmony? harmony;

    public static readonly string[] Woods = { "walnut", "oak", "cherry", "ebony" };
    public static readonly string[] Finishes = { "mannequin", "black", "bronze", "marble" };

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            root = Path.GetDirectoryName(Info.Location)!;
#if WWC
            var gameRoot = BepInEx.Paths.GameRootPath;
#else
            var gameRoot = Path.GetFullPath(Path.Combine(root, "..", "..", ".."));
#endif
            Settings.Load(gameRoot, Logger);
            EmptyMesh = new Mesh { name = "WWCHelmet_empty", hideFlags = HideFlags.HideAndDontSave };
            var layoutPath = Path.Combine(root, "shared", "displays.json");
            foreach (var kv in JsonConvert.DeserializeObject<Dictionary<string, Layout>>(File.ReadAllText(layoutPath)) ?? new Dictionary<string, Layout>())
            {
                Layouts[kv.Key] = kv.Value;
                KeyByTpl[Hash("wwchelmet|" + kv.Key)] = kv.Key;
            }
            States = new DisplayStates(Path.Combine(root, "display_state.json"), Logger);
            Instance = this;
            harmony = new Harmony(Id);
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)) { priority = Priority.Last });
            }
            harmony.Patch(AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.Update)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterAppUpdate)));
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)));
            harmony.Patch(AccessTools.Method(typeof(HideoutAreaStashController), nameof(HideoutAreaStashController.PositionGameObject)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterPosition)));
            harmony.Patch(AccessTools.Method(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableHideoutActions)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeHideoutActions)));
            // One piece per place, the same rule as the gun displays: the game reads a grid's maxCount from the template but never hands it to the grid,
            // so a display's grid refuses an item while it holds a different one (the piece already there can still be moved about).
            // Plain reflection, not AccessTools.DeclaredMethod: most grid types don't declare these methods, and HarmonyX
            // logs a "Could not find method" warning for every miss.
            const System.Reflection.BindingFlags declared = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var type in typeof(Grid).Assembly.GetTypes().Where(t => typeof(Grid).IsAssignableFrom(t)))
            {
                var check = type.GetMethod(nameof(Grid.CheckCompatibility), declared, null, new[] { typeof(Item) }, null);
                if (check != null && !check.IsAbstract) harmony.Patch(check, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeGridCheck)));
                var add = type.GetMethod(nameof(Grid.AddInternal), declared);
                if (add != null && !add.IsAbstract) harmony.Patch(add, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeGridAdd)));
            }
            Logger.LogInfo($"WWC Helmet Displays ready: {Layouts.Count} displays.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Instance = null;
            Logger.LogError("WWC Helmet Displays unavailable; the displays keep the poster look. " + error);
        }
    }

    internal static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2"))).Substring(0, 24);
    }

    internal void Log(string message) => Logger.LogInfo("WWC Helmet Displays: " + message);

    // The mannequin parts are made once, as soon as the game has logged in, so stash icons and the hideout have them from the start.
    private static bool kitStarted;
    private static void AfterAppUpdate()
    {
        if (kitStarted || Instance is null) return;
        try
        {
            if (!Singleton<CustomizationSolver>.Instantiated || !Singleton<Diz.Resources.IEasyAssets>.Instantiated || !Singleton<ItemFactory>.Instantiated) return;
            StartKit();
        }
        catch (Exception error) { kitStarted = true; Instance?.ReportOnce("starting the mannequin kit", error); }
    }

    internal static void StartKit()
    {
        if (kitStarted) return;
        kitStarted = true;
        Kit.Build();
    }

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"WWC Helmet Displays: {key}: {error}");
    }

    // ---------- one piece per place ----------

    private static bool HoldsAnother(Grid grid, Item item)
    {
        var parent = grid.ParentItem;
        if (Instance is null || parent == null || !Instance.KeyByTpl.TryGetValue(parent.StringTemplateId, out var key)) return false;
        foreach (var held in grid.Items) if (held != null && held != item) return true;
        return (key == "bust" || key == "trophy") && ClashOnBust(parent as CompoundItem, grid, item) != null;
    }

    // ---------- a bust wears only what a player could ----------
    // A bust keeps its six pieces in six grids, so the game's own equipment rules never meet them: it took an Avon mask (which blocks
    // headwear) under a helmet, a pair no player can wear, and the two ran into each other (WWC trailer, 2026-11-17). A bust grid now
    // refuses a piece the player's slots would refuse next to what the bust already wears: either side's conflicting items (any part on
    // it, a helmet's headset or visor too), or a piece that blocks the other one's slot.
    private static readonly Dictionary<string, string> BlocksGrid = new()
    {
        ["helmet"] = "BlocksHeadwear", ["mask"] = "BlocksFaceCover", ["glasses"] = "BlocksEyewear", ["headset"] = "BlocksEarpiece", ["armor"] = "BlocksArmorVest",
    };

    internal static Item? ClashOnBust(CompoundItem? bust, Grid grid, Item item)
    {
        if (bust?.Grids == null) return null;
        var mine = item.GetAllItems().ToList();
        foreach (var other in bust.Grids)
        {
            if (other == null || other == grid) continue;
            foreach (var held in other.Items)
            {
                if (held == null || held == item) continue;
                var theirs = held.GetAllItems().ToList();
                if (Conflicts(mine, theirs) || Conflicts(theirs, mine)) return held;
                if (BlocksGrid.TryGetValue(other.ID, out var flag) && mine.Any(i => Flag(i, flag))) return held;
                if (BlocksGrid.TryGetValue(grid.ID, out flag) && theirs.Any(i => Flag(i, flag))) return held;
            }
        }
        return null;
    }

    private static bool Conflicts(List<Item> a, List<Item> b)
    {
        var ids = new HashSet<string>(b.Select(i => i.StringTemplateId));
        foreach (var i in a)
        {
            var list = i.ConflictingItems;
            if (list == null) continue;
            foreach (var s in list) if (s != null && ids.Contains(s.ToString())) return true;
        }
        return false;
    }

    private static bool Flag(Item item, string name)
    {
        try { return Traverse.Create(item.Template).Field(name).GetValue() is bool b && b; }
        catch { return false; }
    }

    private static bool BeforeGridCheck(Grid __instance, Item item, ref bool __result)
    {
        try { if (item != null && HoldsAnother(__instance, item)) { __result = false; return false; } }
        catch (Exception error) { Instance?.ReportOnce("one piece per place (check)", error); }
        return true;
    }

    private static bool BeforeGridAdd(Grid __instance, Item item, bool ignoreRestrictions, ref Diz.LanguageExtensions.OperationResult<GridAddResult> __result)
    {
        try
        {
            if (ignoreRestrictions || item == null || !HoldsAnother(__instance, item)) return true;
            __result = new Grid.MaxItemsCountReachedError(1);
            return false;
        }
        catch (Exception error) { Instance?.ReportOnce("one piece per place (add)", error); return true; }
    }

    // ---------- dressing a display when the game makes its model ----------

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result, System.Reflection.MethodBase __originalMethod)
    {
        var p = Instance;
        if (p is null || __0 is not CompoundItem compound || !p.KeyByTpl.TryGetValue(__0.StringTemplateId, out var key)) return;
        // The stash icon is drawn from a clean loot model; there the cut mannequin gets a look without reflections, which the icon studio turns blue.
        __result = p.ApplyAsync(__result, compound, key, __originalMethod?.Name == "CreateCleanLootPrefabAsync");
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, CompoundItem item, string key, bool iconLook)
    {
        var model = await task;
        if (model == null) return null;
        // Never waits for the mannequin here: some callers block on this task. The display adds its cut parts as soon as the kit is ready.
        if (!Kit.Ready) StartKit();
        Dress(model, item, key, iconLook);
        return model;
    }

    private void Dress(GameObject model, CompoundItem item, string key, bool iconLook)
    {
        var state = model.GetComponent<VisualState>() ?? model.AddComponent<VisualState>();
        state.Restore();
        try
        {
            var layout = Layouts[key];
            var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
            if (filters.Length == 0) throw new InvalidDataException("display donor has no rigid renderer");
            var primary = filters[0];
            var donor = primary.GetComponent<MeshRenderer>().sharedMaterials.LastOrDefault(m => m != null) ?? throw new InvalidDataException("display donor has no material");
            var mesh = HardwareMesh(layout.mesh);
            foreach (var filter in filters)
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                state.Remember(renderer, filter);
                filter.sharedMesh = filter == primary ? mesh : EmptyMesh;
                renderer.sharedMaterials = filter == primary ? HardwareMaterials(donor, States.Wood(item.Id)) : new Material[0];
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) { state.RememberEnabled(c); c.enabled = false; }
            foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
            var pivot = model.GetComponent<PreviewPivot>();
            if (pivot != null) { state.RememberPivot(pivot); pivot.pivotPosition = model.transform.InverseTransformPoint(primary.transform.TransformPoint(mesh.bounds.center)); }
            var display = model.GetComponent<DisplayRoot>() ?? model.AddComponent<DisplayRoot>();
            display.Bind(primary.transform, item, key, layout, donor, iconLook);
        }
        catch (Exception error) { state.Restore(); ReportOnce("dress " + key, error); }
    }

    internal Mesh HardwareMesh(string file)
    {
        if (meshes.TryGetValue(file, out var cached) && cached != null) return cached;
        var mesh = ReadMesh(Path.Combine(root, "shared", file), "WWCHelmet_" + Path.GetFileNameWithoutExtension(file));
        meshes[file] = mesh;
        return mesh;
    }

    // ---------- materials ----------

    internal Material[] HardwareMaterials(Material donor, int wood) => new[] { Wood(donor, wood), Steel(donor), Brass(donor), Felt(donor) };

    internal Material Wood(Material donor, int index)
    {
        index = ((index % Woods.Length) + Woods.Length) % Woods.Length;
        var key = "wood_" + Woods[index];
        if (materials.TryGetValue(key, out var cached) && cached != null) return cached;
        var shared = Path.Combine(root, "shared");
        var m = Fresh(donor, key);
        Set(m, "_MainTex", Load(Path.Combine(shared, "wood_a.png"), false, false));
        Set(m, "_BumpMap", Load(Path.Combine(shared, "wood_n.png"), true, true));
        Set(m, "_SpecMap", Load(Path.Combine(shared, "wood_s.png"), true, false));
        var tint = index switch { 1 => new Color(0.66f, 0.5f, 0.32f), 2 => new Color(0.46f, 0.21f, 0.13f), 3 => new Color(0.11f, 0.09f, 0.08f), _ => new Color(0.30f, 0.19f, 0.12f) };
        if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
        if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", index == 3 ? 0.9f : 0.45f);
        if (m.HasProperty("_Glossness")) m.SetFloat("_Glossness", 1f);
        if (m.HasProperty("_NormalIntensity")) m.SetFloat("_NormalIntensity", 1f);
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(0.08f, 0.08f, 0.08f, 0.5f));
        materials[key] = m;
        return m;
    }

    internal Material Steel(Material donor) => Plain(donor, "steel", new Color(0.13f, 0.13f, 0.14f), 2.0f, 0.7f, 0.3f);
    internal Material Brass(Material donor) => Plain(donor, "brass", new Color(0.72f, 0.54f, 0.26f), 2.2f, 0.8f, 0.4f);
    internal Material Felt(Material donor) => Plain(donor, "felt", new Color(0.07f, 0.07f, 0.075f), 0.2f, 0.1f, 0f);

    internal Material Plain(Material donor, string key, Color colour, float specularness, float glossness, float reflect)
    {
        if (materials.TryGetValue(key, out var cached) && cached != null) return cached;
        var m = Fresh(donor, key);
        Set(m, "_MainTex", Texture2D.whiteTexture);
        Set(m, "_SpecMap", Texture2D.whiteTexture);
        if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
        if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", specularness);
        if (m.HasProperty("_Glossness")) m.SetFloat("_Glossness", glossness);
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(reflect, reflect, reflect, 0.5f));
        if (m.HasProperty("_Cube") && reflect > 0f) m.SetTexture("_Cube", ReflectionCube());
        materials[key] = m;
        return m;
    }

    /// <summary>The bust in its finish: the mannequin's own material (its texture and normals), tinted.</summary>
    internal Material? BustMaterial(int index, bool icon = false, bool head = false)
    {
        var src = head ? Kit.HeadMaterial : Kit.MannequinMaterial;
        if (src == null) return null;
        index = ((index % Finishes.Length) + Finishes.Length) % Finishes.Length;
        var key = "bust_" + Finishes[index] + (head ? "_head" : "") + (icon ? "_icon" : "");
        if (materials.TryGetValue(key, out var cached) && cached != null) return cached;
        if (icon)
        {
            var plainSurface = IconMaterial(key, BustMaterial(index, false, head)!, true);
            materials[key] = plainSurface;
            return plainSurface;
        }
        var m = new Material(src) { name = "WWCHelmet_" + key, hideFlags = HideFlags.DontUnloadUnusedAsset };
        switch (Finishes[index])
        {
            case "black":
                if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.12f, 0.12f, 0.13f));
                if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(0.12f, 0.12f, 0.12f, 0.5f));
                break;
            case "bronze":
                if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.55f, 0.36f, 0.18f));
                if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", 2.4f);
                if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(0.55f, 0.4f, 0.22f, 0.5f));
                if (m.HasProperty("_Cube")) m.SetTexture("_Cube", ReflectionCube());
                break;
            case "marble":
                if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(1.35f, 1.33f, 1.3f));
                if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(0.18f, 0.18f, 0.18f, 0.5f));
                break;
        }
        materials[key] = m;
        return m;
    }

    /// <summary>The flat faces left by the cut: the bust's colour without the mannequin's texture, which would smear across them.</summary>
    internal Material? CapMaterial(int index, bool icon = false)
    {
        var src = Kit.MannequinMaterial;
        if (src == null) return null;
        index = ((index % Finishes.Length) + Finishes.Length) % Finishes.Length;
        var key = "cap_" + Finishes[index] + (icon ? "_icon" : "");
        if (materials.TryGetValue(key, out var cached) && cached != null) return cached;
        var tint = Finishes[index] switch { "black" => new Color(0.1f, 0.1f, 0.11f), "bronze" => new Color(0.42f, 0.28f, 0.14f), "marble" => new Color(0.86f, 0.85f, 0.83f), _ => new Color(0.58f, 0.58f, 0.6f) };
        if (icon)
        {
            var plainCap = IconMaterial(key, BustMaterial(index)!, false);
            if (plainCap.HasProperty("_Color")) plainCap.SetColor("_Color", tint);
            materials[key] = plainCap;
            return plainCap;
        }
        var m = new Material(BustMaterial(index) ?? src) { name = "WWCHelmet_" + key, hideFlags = HideFlags.DontUnloadUnusedAsset };
        foreach (var name in new[] { "_MainTex", "_BumpMap", "_SpecMap", "_DetailAlbedoMap", "_DetailNormalMap" }) if (m.HasProperty(name)) m.SetTexture(name, name == "_BumpMap" || name == "_DetailNormalMap" ? null : Texture2D.whiteTexture);
        if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
        materials[key] = m;
        return m;
    }

    internal Material? CrownMaterial(bool icon = false)
    {
        var src = Kit.HeadMaterial ?? Kit.MannequinMaterial;
        if (src == null) return null;
        var key = icon ? "crown_icon" : "crown";
        if (materials.TryGetValue(key, out var cached) && cached != null) return cached;
        var m = new Material(src) { name = "WWCHelmet_" + key, hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.09f, 0.085f, 0.08f));
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(0.03f, 0.03f, 0.03f, 0.5f));
        if (icon) NoReflection(m);
        materials[key] = m;
        return m;
    }

    /// <summary>
    /// The icon studio swaps every shader for its "_Icon" twin except "Standard" and "Diffuse", and its twin of the mannequin's shader draws the bust blue-white.
    /// The icon look uses the Standard shader with the same colour and texture when the game has it, and the reflection-free game shader when it doesn't.
    /// </summary>
    private static Material IconMaterial(string key, Material from, bool keepTexture)
    {
        var standard = Shader.Find("Standard");
        if (standard != null)
        {
            var s = new Material(standard) { name = "WWCHelmet_" + key, hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (s.HasProperty("_Color") && from.HasProperty("_Color")) s.SetColor("_Color", from.GetColor("_Color"));
            if (keepTexture && s.HasProperty("_MainTex") && from.HasProperty("_MainTex")) s.SetTexture("_MainTex", from.GetTexture("_MainTex"));
            if (s.HasProperty("_Glossiness")) s.SetFloat("_Glossiness", 0.25f);
            if (s.HasProperty("_Metallic")) s.SetFloat("_Metallic", 0f);
            return s;
        }
        var m = new Material(from) { name = "WWCHelmet_" + key, hideFlags = HideFlags.DontUnloadUnusedAsset };
        NoReflection(m);
        if (!keepTexture) foreach (var name in new[] { "_MainTex", "_SpecMap" }) if (m.HasProperty(name)) m.SetTexture(name, Texture2D.whiteTexture);
        return m;
    }

    private static void NoReflection(Material m)
    {
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(0f, 0f, 0f, 0f));
        if (m.HasProperty("_Cube")) m.SetTexture("_Cube", null);
        if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", Mathf.Min(m.GetFloat("_Specularness"), 0.5f));
    }

    // A copy of the poster's material with every texture it had cleared, so nothing in it points into a bundle the game may unload.
    private static Material Fresh(Material donor, string key)
    {
        var m = new Material(donor) { name = "WWCHelmet_" + key, hideFlags = HideFlags.DontUnloadUnusedAsset };
        foreach (var name in m.GetTexturePropertyNames()) m.SetTexture(name, null);
        if (m.HasProperty("_TRIPLANAR")) m.SetFloat("_TRIPLANAR", 0f);
        return m;
    }

    private static void Set(Material m, string prop, Texture tex)
    {
        if (!m.HasProperty(prop)) return;
        m.SetTexture(prop, tex);
        m.SetTextureScale(prop, Vector2.one);
        m.SetTextureOffset(prop, Vector2.zero);
    }

    private Cubemap? cube;
    private Cubemap? ReflectionCube()
    {
        if (cube != null) return cube;
        cube = Resources.FindObjectsOfTypeAll<Cubemap>().FirstOrDefault(c => c.name == "patron_cubemap_metall") ?? Resources.FindObjectsOfTypeAll<Cubemap>().FirstOrDefault();
        return cube;
    }

    private Texture2D Load(string path, bool linear, bool isNormal)
    {
        if (textures.TryGetValue(path, out var cached) && cached != null) return cached;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "WWCHelmet_" + Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (!tex.LoadImage(File.ReadAllBytes(path), false)) throw new InvalidDataException("Could not read " + path);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 8;
        if (isNormal)
        {
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) { var c = px[i]; px[i] = new Color32(255, c.g, 255, c.r); }
            tex.SetPixels32(px);
        }
        tex.Apply(true, false);
        tex.Compress(false);
        tex.Apply(false, true);
        textures[path] = tex;
        return tex;
    }

    internal static Mesh ReadMesh(string path, string name)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 12 || data[0] != 'J' || data[1] != 'P' || data[2] != 'M' || data[3] != '1') throw new InvalidDataException("Not a display mesh: " + path);
        int vertexCount = BitConverter.ToInt32(data, 4), subCount = BitConverter.ToInt32(data, 8), off = 12;
        var verts = new Vector3[vertexCount]; var normals = new Vector3[vertexCount]; var uv = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++, off += 12) verts[i] = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
        for (int i = 0; i < vertexCount; i++, off += 12) normals[i] = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
        for (int i = 0; i < vertexCount; i++, off += 8) uv[i] = new Vector2(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4));
        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.DontUnloadUnusedAsset };
        mesh.vertices = verts; mesh.normals = normals; mesh.uv = uv;
        mesh.subMeshCount = subCount;
        for (int s = 0; s < subCount; s++)
        {
            int count = BitConverter.ToInt32(data, off); off += 4;
            var idx = new int[count];
            for (int t = 0; t < count; t++, off += 4) idx[t] = BitConverter.ToInt32(data, off);
            mesh.SetTriangles(idx, s, false);
        }
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(false);
        return mesh;
    }

    // ---------- the hideout ----------

    // Just hung by the game. A container uses the generic stash model, which lifts it by half its height; a poster does not.
    private static void AfterPosition(StashItemModel model)
    {
        try
        {
            if (Instance is null || model == null) return;
            var display = model.GetComponentInChildren<DisplayRoot>(true);
            if (display == null) return;
            var pivot = Traverse.Create(model).Field("_rotationPivot").GetValue<Transform>();
            if (pivot != null) pivot.localPosition = Vector3.zero;
            display.OnPlaced();
        }
        catch (Exception error) { Instance?.ReportOnce("hanging a display", error); }
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance == null) return;
        __instance.GetComponent<GearCut>()?.Restore();
        __instance.GetComponent<LightsOff>()?.Restore();
        __instance.GetComponent<VisualState>()?.Restore();
    }

    // Must be a prefix: the original throws "No interactions defined" for any object type it does not know.
    private static bool BeforeHideoutActions(HideoutPlayerOwner owner, IInteractive interactive, ref AvailableInteractionState? __result)
    {
        if (interactive is not DisplayInteractive hit) return true;
        __result = null;
        var display = hit.Display;
        if (Instance is null || !owner.AvailableForInteractions || display == null) return false;
        void Refresh() { try { owner.Player.UpdateInteractionCast(); } catch { } }
        var state = new AvailableInteractionState();
        var open = display.Key switch { "mount" => "Hang a helmet", "rack" => "Hang helmets", _ => "Dress the bust" };
        state.Actions.Add(new InteractionAction { Name = open, Action = () => Instance.OpenDisplay(owner, display) });
        state.Actions.Add(new InteractionAction { Name = $"Change the wood ({Title(Woods[display.Wood])})", Action = () => { display.NextWood(); Refresh(); } });
        if (display.IsBust) state.Actions.Add(new InteractionAction { Name = $"Change the finish ({Title(Finishes[display.Finish])})", Action = () => { display.NextFinish(); Refresh(); } });
        state.Actions.Add(new InteractionAction { Name = display.LightOn ? "Switch the light off" : "Switch the light on", Action = () => { display.ToggleLight(); Refresh(); } });
        state.InitSelected();
        __result = state;
        return false;
    }

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    internal void OpenDisplay(HideoutPlayerOwner owner, DisplayRoot display)
    {
        try
        {
            var item = display.Item;
            owner.HideoutPlayer.SetInventoryOpened(true);
            Singleton<HideoutRepresentation>.Instance.ShowInventoryScreen(() =>
            {
                owner.HideoutPlayer.SetInventoryOpened(false);
                owner.Player.UpdateInteractionCast();
            });
            // The display runs the wait: BepInEx's own manager object may be gone by now, and a plugin can't start coroutines without it.
            display.StartCoroutine(OpenWindowSoon(item));
        }
        catch (Exception error) { ReportOnce("open display", error); }
    }

    private System.Collections.IEnumerator OpenWindowSoon(CompoundItem item)
    {
        yield return new WaitForSeconds(0.5f);
        try { ItemUiContext.Instance.OpenItem(item, new DefaultItemContext(item, EItemViewType.Inventory)); }
        catch (Exception error) { ReportOnce("open display window", error); }
    }
}

// ---------- data ----------

public sealed class Layout
{
    public string mesh = "";
    public bool wall;
    public List<GearSpot> gear = new();
    public BustSpot? bust;
    public BoxSpot box = new();
    public LightSpot light = new();
    public float[] hardwareMin = new float[3], hardwareMax = new float[3];
}
public sealed class GearSpot { public float x, y, z; public bool crown; }
public sealed class BustSpot { public string kind = "bust"; public float x, y, z; }
public sealed class BoxSpot { public float cx, cy, cz, w, h, d; }
public sealed class LightSpot { public float x, y, z, range = 1.2f, intensity = 1f; }

/// <summary>Settings the client reads from the mod's one config.json, in the server mod's folder. Any problem leaves the normal values in place.</summary>
internal sealed class Settings
{
    public int Wood;
    public int Finish;
    public bool LightOnByDefault = true;
    public float LightBrightness = 1f;
    public static Settings Current = new();

    public static void Load(string gameRoot, BepInEx.Logging.ManualLogSource log)
    {
        try
        {
#if WWC
            var path = Path.Combine(gameRoot, "SPT_Runtime", "user", "mods", "Widowz-WWC.Server", "HelmetDisplays", "config.json");
#else
            var path = Path.Combine(gameRoot, "SPT_Runtime", "user", "mods", "Widowz-WWCHelmetDisplays.Server", "config.json");
#endif
            if (!File.Exists(path)) return;
            var json = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            if (json["look"] is not JObject look) return;
            var next = new Settings();
            if (look["wood"] is JValue w) next.Wood = Math.Max(0, Array.IndexOf(Plugin.Woods, (w.ToString() ?? "").Trim().ToLowerInvariant()));
            if (look["bustFinish"] is JValue f) next.Finish = Math.Max(0, Array.IndexOf(Plugin.Finishes, (f.ToString() ?? "").Trim().ToLowerInvariant()));
            if (look["lightOnByDefault"] is JValue l) next.LightOnByDefault = l.ToObject<bool>();
            if (look["lightBrightness"] is JValue b) next.LightBrightness = Mathf.Clamp(b.ToObject<float>(), 0.2f, 3f);
            Current = next;
        }
        catch (Exception ex) { log.LogWarning("WWC Helmet Displays: config.json could not be read (" + ex.Message + "); normal display settings are in use"); }
    }
}

/// <summary>Wood, finish and light chosen on each display, saved by item id so they survive restarts and updates.</summary>
internal sealed class DisplayStates
{
    public sealed class Entry { public int? wood; public int? finish; public bool? light; }
    private readonly string path;
    private readonly BepInEx.Logging.ManualLogSource log;
    private readonly Dictionary<string, Entry> entries = new();

    public DisplayStates(string path, BepInEx.Logging.ManualLogSource log)
    {
        this.path = path; this.log = log;
        if (!File.Exists(path)) return;
        try { entries = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new Dictionary<string, Entry>(); }
        catch (Exception ex) { log.LogWarning("WWC Helmet Displays: display_state.json could not be read (" + ex.Message + "); displays start with the normal look"); }
    }

    public int Wood(string id) => entries.TryGetValue(id, out var e) && e.wood.HasValue ? e.wood.Value : Settings.Current.Wood;
    public int Finish(string id) => entries.TryGetValue(id, out var e) && e.finish.HasValue ? e.finish.Value : Settings.Current.Finish;
    public bool Light(string id) => entries.TryGetValue(id, out var e) && e.light.HasValue ? e.light.Value : Settings.Current.LightOnByDefault;

    public void Set(string id, int? wood = null, int? finish = null, bool? light = null)
    {
        if (!entries.TryGetValue(id, out var e)) entries[id] = e = new Entry();
        if (wood.HasValue) e.wood = wood;
        if (finish.HasValue) e.finish = finish;
        if (light.HasValue) e.light = light;
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(entries, Formatting.Indented));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        catch (Exception ex) { log.LogWarning("WWC Helmet Displays: could not save display_state.json: " + ex.Message); }
    }
}
