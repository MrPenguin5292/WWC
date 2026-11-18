using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using Comfort.Common;
using Diz.Jobs;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.CameraControl;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.WeaponModding;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WWCGunDisplays.Client;

/// <summary>
/// WWC gun displays: a gun hanger, a triple gun hanger and a gun shadow box that hang on hideout poster spots and show the real guns in their grids.
/// Each display is built in code to fit the guns on it. Every change is made on objects the game already spawns and is undone before they go back to the pool;
/// on any failure the donor poster look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "WWC Gun Displays", "0.1.2")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Gun Displays");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("GunDisplays");
#endif
    public const string Id = "local.wwcgundisplays.client";
    internal static Plugin? Instance;
    internal readonly Dictionary<string, string> Kinds = new();   // template id -> hanger1 | hanger3 | shadowbox
    internal Mesh EmptyMesh = null!;
    internal SavedLooks Looks = null!;
    private readonly Dictionary<string, Texture2D> textures = new();
    private readonly Dictionary<string, Material> materials = new();
    private readonly HashSet<string> errors = new();
    private string root = "";
    private Harmony? harmony;

    internal static readonly string[] Woods = { "walnut", "oak", "cherry", "ebony" };
    internal static readonly string[] WoodLabels = { "Walnut", "Oak", "Cherry", "Ebony" };
    internal static readonly (string key, string label, Color colour)[] Felts =
        { ("black", "black felt", new Color(0.06f, 0.06f, 0.065f)), ("green", "green felt", new Color(0.15f, 0.33f, 0.22f)), ("burgundy", "burgundy felt", new Color(0.42f, 0.08f, 0.1f)), ("navy", "navy felt", new Color(0.1f, 0.15f, 0.34f)) };

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
            ClientSettings.Load(gameRoot, Logger);
            EmptyMesh = new Mesh { name = "WWCGun_empty", hideFlags = HideFlags.HideAndDontSave };
            foreach (var key in new[] { "hanger1", "hanger3", "shadowbox", "pistolbox" }) Kinds[Hash("wwcgun|" + key)] = key;
            Looks = new SavedLooks(Path.Combine(root, "displays.json"), Logger);
            Instance = this;
            harmony = new Harmony(Id);
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)) { priority = Priority.Last });
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)));
            harmony.Patch(AccessTools.Method(typeof(HideoutAreaStashController), nameof(HideoutAreaStashController.PositionGameObject)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterPosition)));
            harmony.Patch(AccessTools.Method(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableHideoutActions)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeHideoutActions)));
            // One gun per spot. The game reads a grid's maxCount from the template but never hands it to the grid, so the rule is kept here:
            // a display's grid refuses any item while it holds a different one (the gun already there can still be moved about).
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
            Logger.LogInfo("WWC Gun Displays ready.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Instance = null;
            Logger.LogError("WWC Gun Displays unavailable; displays look like posters. " + error);
        }
    }

    internal static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2"))).Substring(0, 24);
    }

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"WWC Gun Displays: {key}: {error}");
    }

    internal void Note(string message) => Logger.LogInfo("WWC Gun Displays: " + message);

    // ---------- one gun per spot ----------

    private static bool HoldsAnother(Grid grid, Item item)
    {
        var parent = grid.ParentItem;
        if (Instance is null || parent == null || !Instance.Kinds.ContainsKey(parent.StringTemplateId)) return false;
        foreach (var held in grid.Items) if (held != null && held != item) return true;
        return false;
    }

    private static bool BeforeGridCheck(Grid __instance, Item item, ref bool __result)
    {
        try { if (item != null && HoldsAnother(__instance, item)) { __result = false; return false; } }
        catch (Exception error) { Instance?.ReportOnce("one gun per spot (check)", error); }
        return true;
    }

    private static bool BeforeGridAdd(Grid __instance, Item item, bool ignoreRestrictions, ref OperationResult<GridAddResult> __result)
    {
        try
        {
            if (ignoreRestrictions || item == null || !HoldsAnother(__instance, item)) return true;
            __result = new Grid.MaxItemsCountReachedError(1);
            return false;
        }
        catch (Exception error) { Instance?.ReportOnce("one gun per spot (add)", error); return true; }
    }

    // ---------- dressing the poster donor ----------

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        var p = Instance;
        if (p is null || __0 is null || !p.Kinds.ContainsKey(__0.StringTemplateId)) return;
        __result = p.ApplyAsync(__result, __0);
    }

    private static void AfterCreateNow(Item __0, GameObject? __result)
    {
        var p = Instance;
        if (p is null || __0 is null || __result == null || !p.Kinds.TryGetValue(__0.StringTemplateId, out var kind) || __0 is not CompoundItem compound) return;
        p.Dress(__result, compound, kind);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item)
    {
        var model = await task;
        if (model == null) return null;
        if (item is CompoundItem compound && Kinds.TryGetValue(item.StringTemplateId, out var kind)) Dress(model, compound, kind);
        return model;
    }

    /// <summary>Swaps the poster's model for an empty display of the right kind. Guns are added only once it is hung (<see cref="GunDisplay.OnPlaced"/>).</summary>
    private void Dress(GameObject model, CompoundItem item, string kind)
    {
        var state = model.GetComponent<DisplayVisualState>() ?? model.AddComponent<DisplayVisualState>();
        state.Restore();
        try
        {
            var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
            if (filters.Length == 0) throw new InvalidDataException("Display donor has no rigid renderer.");
            var primary = filters[0];
            var donor = primary.GetComponent<MeshRenderer>().sharedMaterials.LastOrDefault(m => m != null) ?? throw new InvalidDataException("Display donor has no material.");
            foreach (var filter in filters)
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                state.Remember(renderer, filter);
                if (filter != primary) { filter.sharedMesh = EmptyMesh; renderer.sharedMaterials = new Material[0]; }
            }
            foreach (var box in primary.GetComponents<BoxCollider>()) { state.RememberEnabled(box); box.enabled = false; }
            foreach (var mesh in primary.GetComponents<MeshCollider>()) { state.RememberEnabled(mesh); mesh.enabled = false; }
            var display = model.GetComponent<GunDisplay>() ?? model.AddComponent<GunDisplay>();
            display.Bind(primary, item, kind, donor);
            display.ShowEmpty();
            foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
            var pivot = model.GetComponent<PreviewPivot>();
            if (pivot != null)
            {
                state.RememberPivot(pivot);
                pivot.pivotPosition = model.transform.InverseTransformPoint(primary.transform.TransformPoint(new Vector3(0f, 0f, display.Size.z * 0.5f)));
            }
        }
        catch (Exception error) { state.Restore(); ReportOnce("dressing " + kind, error); }
    }

    // ---------- materials ----------

    /// <summary>The eight materials a display draws with, in the wood and felt chosen for it. The glow and the engraved plate are the display's own.</summary>
    internal Material[] MaterialsFor(Material donor, int wood, int felt, Material glow, Material? plate)
    {
        var shared = Path.Combine(root, "shared");
        var woodKey = Woods[Mathf.Clamp(wood, 0, Woods.Length - 1)];
        bool ebony = woodKey == "ebony";
        var woodMat = Lit(donor, "wood_" + woodKey, Path.Combine(shared, $"wood_{woodKey}_a.png"), Path.Combine(shared, "wood_n.png"), Path.Combine(shared, "wood_s.png"), ebony ? 1.5f : 1.0f, ebony ? 0.1f : 0.06f, false);
        var feltBase = Lit(donor, "felt", Path.Combine(shared, "felt_a.png"), Path.Combine(shared, "felt_n.png"), Path.Combine(shared, "felt_s.png"), 0.2f, 0.01f, false);
        var feltColour = Felts[Mathf.Clamp(felt, 0, Felts.Length - 1)];
        var feltMat = Tinted(feltBase, "felt_" + feltColour.key, feltColour.colour);
        var steel = Lit(donor, "steel", Path.Combine(shared, "steel_a.png"), Path.Combine(shared, "steel_n.png"), Path.Combine(shared, "steel_s.png"), 1.3f, 0.1f, true);
        var rubber = Lit(donor, "rubber", Path.Combine(shared, "rubber_a.png"), Path.Combine(shared, "flat_n.png"), Path.Combine(shared, "rubber_s.png"), 0.08f, 0f, false);
        var brass = Lit(donor, "brass", Path.Combine(shared, "brass_a.png"), Path.Combine(shared, "flat_n.png"), Path.Combine(shared, "brass_s.png"), 2.4f, 0.32f, true);
        var glass = GlassMaterial() ?? feltMat;
        return new[] { woodMat, feltMat, steel, rubber, brass, glass, glow, plate ?? brass };
    }

    private Material Lit(Material donor, string key, string albedo, string normal, string gloss, float spec, float reflect, bool metal)
    {
        if (materials.TryGetValue(key, out var cached) && cached != null) return cached;
        var m = new Material(donor) { name = "WWCGun_" + key, hideFlags = HideFlags.DontUnloadUnusedAsset };
        void Tex(string prop, Texture tex) { if (m.HasProperty(prop)) { m.SetTexture(prop, tex); m.SetTextureScale(prop, Vector2.one); m.SetTextureOffset(prop, Vector2.zero); } }
        Tex("_MainTex", Load(albedo, false, false));
        Tex("_BumpMap", Load(normal, true, true));
        Tex("_SpecMap", Load(gloss, true, false));
        if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", spec);
        if (m.HasProperty("_Glossness")) m.SetFloat("_Glossness", 1f);
        if (m.HasProperty("_NormalIntensity")) m.SetFloat("_NormalIntensity", 1f);
        if (m.HasProperty("_TRIPLANAR")) m.SetFloat("_TRIPLANAR", 0f);
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(reflect, reflect, reflect, 0.5f));
        // The donor's near-white specular colour makes rubber and cloth read pale grey; dull surfaces get a dark one.
        if (m.HasProperty("_SpecColor") && spec < 0.5f) m.SetColor("_SpecColor", new Color(0.12f, 0.12f, 0.12f, 1f));
        // Metal has little colour of its own: without something to reflect it reads black wherever a light doesn't reach.
        if (m.HasProperty("_Cube")) m.SetTexture("_Cube", metal ? ReflectionCube() : null);
        materials[key] = m;
        return m;
    }

    private Material Tinted(Material source, string key, Color colour)
    {
        if (materials.TryGetValue(key, out var cached) && cached != null) return cached;
        var m = new Material(source) { name = "WWCGun_" + key, hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
        materials[key] = m;
        return m;
    }

    private Shader? additive;

    private Material? GlassMaterial()
    {
        if (materials.TryGetValue("glass", out var cached) && cached != null) return cached;
        if (additive == null) additive = Shader.Find("Legacy Shaders/Particles/Additive") ?? Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(s => s != null && s.name == "Legacy Shaders/Particles/Additive");
        if (additive == null) return null;
        var m = new Material(additive) { name = "WWCGun_glass", hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", Load(Path.Combine(root, "shared", "glare_a.png"), false, false));
        if (m.HasProperty("_InvFade")) m.SetFloat("_InvFade", 100000f);
        // A faint streak; anything stronger reads as a white beam.
        if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.085f, 0.085f, 0.095f, 0.5f));
        materials["glass"] = m;
        return m;
    }

    internal Material NewGlow()
    {
        var shader = Shader.Find("Sprites/Default") ?? Resources.FindObjectsOfTypeAll<Shader>().First(s => s != null && s.name == "Sprites/Default");
        var m = new Material(shader) { name = "WWCGun_glow", hideFlags = HideFlags.DontUnloadUnusedAsset };
        // Sprites/Default reads its texture per renderer; without one set it samples whatever is bound and the strip comes out dashed.
        m.mainTexture = Texture2D.whiteTexture;
        return m;
    }

    /// <summary>A brass material wearing an engraved plate texture; the display that asked owns both.</summary>
    internal Material PlateMaterial(Material donor, Texture2D engraved)
    {
        var shared = Path.Combine(root, "shared");
        var brass = Lit(donor, "brass", Path.Combine(shared, "brass_a.png"), Path.Combine(shared, "flat_n.png"), Path.Combine(shared, "brass_s.png"), 2.4f, 0.32f, true);
        var m = new Material(brass) { name = "WWCGun_plate", hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", engraved);
        if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", 1.8f);
        return m;
    }

    internal string SharedFolder => Path.Combine(root, "shared");

    private Cubemap? cubeCache;

    private Cubemap? ReflectionCube()
    {
        if (cubeCache != null) return cubeCache;
        cubeCache = Resources.FindObjectsOfTypeAll<Cubemap>().FirstOrDefault(c => c.name == "patron_cubemap_metall") ?? Resources.FindObjectsOfTypeAll<Cubemap>().FirstOrDefault();
        return cubeCache;
    }

    private Texture2D Load(string path, bool linear, bool isNormal)
    {
        if (textures.TryGetValue(path, out var cached) && cached != null) return cached;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "WWCGun_" + Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (!tex.LoadImage(File.ReadAllBytes(path), false)) throw new InvalidDataException("Could not read " + path);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 8;
        if (isNormal)
        {
            // The game's shaders read normals packed the way Unity's DXT5nm does: x in alpha, y in green.
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

    // ---------- hanging and interaction ----------

    private static void AfterPosition(StashItemModel model)
    {
        try
        {
            if (Instance is null || model == null) return;
            var display = model.GetComponentInChildren<GunDisplay>(true);
            if (display == null) return;
            // A container uses the generic stash model, which lifts it by half its height; a poster does not.
            var pivot = Traverse.Create(model).Field("_rotationPivot").GetValue<Transform>();
            if (pivot != null) pivot.localPosition = Vector3.zero;
            display.OnPlaced();
        }
        catch (Exception error) { Instance?.ReportOnce("hanging a display", error); }
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<DisplayVisualState>()?.Restore();
    }

    // Must be a prefix: the original throws "No interactions defined" for any object type it does not know.
    private static bool BeforeHideoutActions(HideoutPlayerOwner owner, IInteractive interactive, ref AvailableInteractionState? __result)
    {
        if (interactive is not GunInteractive hit) return true;
        __result = null;
        if (Instance is null || !owner.AvailableForInteractions || hit.Display == null) return false;
        var d = hit.Display;
        void Refresh() { try { owner.Player.UpdateInteractionCast(); } catch { } }
        var state = new AvailableInteractionState();
        state.Actions.Add(new InteractionAction { Name = d.IsBox ? "Open the shadow box" : "Hang guns", Action = () => Instance.OpenDisplay(owner, d) });
        state.Actions.Add(new InteractionAction { Name = d.Kind == "hanger3" ? "Turn the guns round" : "Turn the gun round", Action = () => { d.TurnRound(); Refresh(); } });
        state.Actions.Add(new InteractionAction { Name = $"Change the wood ({d.WoodLabel})", Action = () => { d.NextWood(); Refresh(); } });
        if (d.IsBox) state.Actions.Add(new InteractionAction { Name = $"Change the felt ({d.FeltLabel})", Action = () => { d.NextFelt(); Refresh(); } });
        if (d.IsBox) state.Actions.Add(new InteractionAction { Name = d.PlateOn ? "Take the name plate off" : "Put the name plate on", Action = () => { d.TogglePlate(); Refresh(); } });
        state.Actions.Add(new InteractionAction { Name = d.LightOn ? "Switch the light off" : "Switch the light on", Action = () => { d.ToggleLight(); Refresh(); } });
        state.InitSelected();
        __result = state;
        return false;
    }

    internal void OpenDisplay(HideoutPlayerOwner owner, GunDisplay display)
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

/// <summary>Settings the client reads from the mod's one config.json, in the server mod's folder. Any problem leaves the normal values in place.</summary>
internal sealed class ClientSettings
{
    public bool MuzzleLeft;
    public int Wood;
    public int Felt;
    public bool LightOnByDefault = true;
    public float Brightness = 1f;
    public bool NamePlate = true;
    public static ClientSettings Current = new();

    public static void Load(string gameRoot, BepInEx.Logging.ManualLogSource log)
    {
        try
        {
#if WWC
            var path = Path.Combine(gameRoot, "SPT_Runtime", "user", "mods", "Widowz-WWC.Server", "GunDisplays", "config.json");
#else
            var path = Path.Combine(gameRoot, "SPT_Runtime", "user", "mods", "Widowz-WWCGunDisplays.Server", "config.json");
#endif
            if (!File.Exists(path)) return;
            var json = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            if (json["look"] is not JObject look) return;
            var next = new ClientSettings();
            if (look["muzzle"] is JValue m) next.MuzzleLeft = string.Equals(((string?)m ?? "").Trim(), "left", StringComparison.OrdinalIgnoreCase);
            if (look["wood"] is JValue w) next.Wood = Math.Max(0, Array.IndexOf(Plugin.Woods, ((string?)w ?? "walnut").Trim().ToLowerInvariant()));
            if (look["lining"] is JValue f) next.Felt = Math.Max(0, Array.FindIndex(Plugin.Felts, x => x.key == ((string?)f ?? "black").Trim().ToLowerInvariant()));
            if (look["lightOnByDefault"] is JValue l) next.LightOnByDefault = l.ToObject<bool>();
            if (look["lightBrightness"] is JValue b) next.Brightness = Mathf.Clamp(b.ToObject<float>(), 0.2f, 3f);
            if (look["namePlate"] is JValue p) next.NamePlate = p.ToObject<bool>();
            Current = next;
        }
        catch (Exception ex) { log.LogWarning("WWC Gun Displays: config.json could not be read (" + ex.Message + "); normal display settings are in use"); }
    }
}

/// <summary>What each display was switched to in game, by item id, kept in displays.json beside the plugin.</summary>
internal sealed class SavedLooks
{
    public sealed class Look { public bool? muzzleLeft; public int? wood; public int? felt; public bool? lightOn; public bool? plate; }
    private readonly string path;
    private readonly BepInEx.Logging.ManualLogSource log;
    private Dictionary<string, Look> data = new();

    public SavedLooks(string path, BepInEx.Logging.ManualLogSource log)
    {
        this.path = path; this.log = log;
        if (!File.Exists(path)) return;
        try { data = JsonConvert.DeserializeObject<Dictionary<string, Look>>(File.ReadAllText(path)) ?? new Dictionary<string, Look>(); }
        catch (Exception ex) { log.LogWarning("WWC Gun Displays: displays.json could not be read (" + ex.Message + "); displays start with their normal look"); }
    }

    public Look Get(string itemId) => data.TryGetValue(itemId, out var l) ? l : new Look();

    public void Set(string itemId, Action<Look> change)
    {
        var l = Get(itemId);
        change(l);
        data[itemId] = l;
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        catch (Exception ex) { log.LogWarning("WWC Gun Displays: saving displays.json failed: " + ex.Message); }
    }
}

/// <summary>What the player's look ray hits on a hung display; the actions themselves come from <see cref="Plugin"/>.</summary>
public sealed class GunInteractive : InteractableObject
{
    public GunDisplay Display = null!;
}

/// <summary>Records every change made to a pooled object so it can be put back exactly.</summary>
public sealed class DisplayVisualState : MonoBehaviour
{
    private readonly List<Action> restore = new();

    public void Remember(Renderer renderer, MeshFilter filter)
    {
        var mesh = filter.sharedMesh; var mats = renderer.sharedMaterials; var shadow = renderer.shadowCastingMode;
        restore.Add(() => { if (renderer != null) { renderer.sharedMaterials = mats; renderer.shadowCastingMode = shadow; } if (filter != null) filter.sharedMesh = mesh; });
    }

    public void RememberPivot(PreviewPivot pivot)
    {
        var position = pivot.pivotPosition; var rotation = pivot.pivotRotation;
        restore.Add(() => { if (pivot != null) { pivot.pivotPosition = position; pivot.pivotRotation = rotation; } });
    }

    public void RememberEnabled(Collider collider)
    {
        var on = collider.enabled;
        restore.Add(() => { if (collider != null) collider.enabled = on; });
    }

    public void Restore()
    {
        var display = GetComponent<GunDisplay>();
        if (display != null) display.Clear();
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
    }

    private void OnDestroy() => Restore();
}

/// <summary>Everything changed on a pooled gun model while it hangs, put back before it returns to the pool.</summary>
internal sealed class GunModelState
{
    private readonly List<Action> restore = new();

    public static GunModelState Prepare(GameObject model)
    {
        var s = new GunModelState();
        foreach (var c in model.GetComponentsInChildren<Collider>(true)) { var on = c.enabled; s.restore.Add(() => { if (c != null) c.enabled = on; }); c.enabled = false; }
        foreach (var a in model.GetComponentsInChildren<Animator>(true)) { var on = a.enabled; s.restore.Add(() => { if (a != null) a.enabled = on; }); a.enabled = false; }
        var placer = model.GetComponent<ModPlacer>();
        if (placer != null) { var on = placer.enabled; s.restore.Add(() => { if (placer != null) placer.enabled = on; }); placer.enabled = false; }
        foreach (var t in model.GetComponentsInChildren<Transform>(true)) { var l = t.gameObject.layer; s.restore.Add(() => { if (t != null) t.gameObject.layer = l; }); t.gameObject.layer = 0; }
        var tr = model.transform; var scale = tr.localScale;
        s.restore.Add(() => { if (tr != null) tr.localScale = scale; });
        return s;
    }

    public void Restore() { for (int i = restore.Count - 1; i >= 0; i--) restore[i](); restore.Clear(); }
}

/// <summary>
/// A hung display: reads the item's grids (gun_1, gun_2, ...) and hangs the guns in them, then builds the board, hooks, pads or shadow box to fit.
/// Reads only; guns go on and come off through the game's normal item window.
/// </summary>
public sealed class GunDisplay : MonoBehaviour
{
    private sealed class OnShow
    {
        public Item Item = null!;
        public GameObject Holder = null!;
        public GameObject? Model;
        public GunModelState? State;
        public GunShape? Shape;
    }

    private MeshFilter filter = null!;
    private MeshRenderer renderer = null!;
    private Transform anchor = null!;
    private CompoundItem item = null!;
    private Material donor = null!;
    private Material? glow;
    private Mesh? built;
    private Layout? layout;
    private readonly List<OnShow> shown = new();
    private readonly List<Light> lights = new();
    private GameObject? body;
    private Coroutine? watching;
    private string current = "\u0001";
    private int generation;
    private bool placed, muzzleLeft, lightOn = true, plateOn = true;
    private Texture2D? plateTex;
    private Material? plateMat;
    private int wood, felt;

    public string Kind { get; private set; } = "hanger1";
    /// <summary>The gun shadow box and the pistol shadow box: glass, felt, a name plate, standoffs instead of hooks.</summary>
    public bool IsBox => Kind == "shadowbox" || Kind == "pistolbox";
    public CompoundItem Item => item;
    public Vector3 Size => layout?.Size ?? Vector3.one * 0.1f;
    public bool LightOn => lightOn;
    public bool MuzzleLeft => muzzleLeft;
    public bool PlateOn => plateOn;
    public bool HasPlate => plateMat != null;
    public int GunCount => shown.Count(s => s.Model != null);
    public string WoodLabel => Plugin.WoodLabels[Mathf.Clamp(wood, 0, Plugin.Woods.Length - 1)];
    public string FeltLabel => Plugin.Felts[Mathf.Clamp(felt, 0, Plugin.Felts.Length - 1)].label;
    /// <summary>For QA: every hook, pad or standoff contact, in the display's own axes, with its kind and the gun it carries.</summary>
    public List<string> Report { get; } = new();
    public List<(Vector3 point, string kind, GameObject gun)> Contacts { get; } = new();
    public Transform Anchor => anchor;
    public Bounds Extent => layout?.Extent ?? new Bounds(Vector3.zero, Vector3.one * 0.2f);

    internal void Bind(MeshFilter primary, CompoundItem compound, string kind, Material donorMaterial)
    {
        filter = primary; renderer = primary.GetComponent<MeshRenderer>(); anchor = primary.transform; item = compound; Kind = kind; donor = donorMaterial;
        var look = Plugin.Instance?.Looks.Get(compound.Id) ?? new SavedLooks.Look();
        var settings = ClientSettings.Current;
        muzzleLeft = look.muzzleLeft ?? settings.MuzzleLeft;
        wood = look.wood ?? settings.Wood;
        felt = look.felt ?? settings.Felt;
        lightOn = look.lightOn ?? settings.LightOnByDefault;
        plateOn = look.plate ?? settings.NamePlate;
    }

    /// <summary>The display with no guns: what the stash icon, the inspect window and a dropped display show.</summary>
    public void ShowEmpty()
    {
        var guns = new GunShape?[Kind == "hanger3" ? 3 : 1];
        ClearPlate();
        Apply(IsBox ? Layouts.ShadowBox(null, "WWCGun_" + Kind, placed, false, Kind == "pistolbox") : Layouts.Hanger(guns, Kind == "hanger3", "WWCGun_" + Kind));
    }

    public void OnPlaced()
    {
        if (Plugin.Instance is null || anchor == null || item == null) return;
        placed = true;
        EnsureBody();
        Refresh();
        if (watching == null && isActiveAndEnabled) watching = StartCoroutine(Watch());
    }

    private void OnEnable() { if (placed && watching == null && isActiveAndEnabled) watching = StartCoroutine(Watch()); }
    private void OnDisable() { watching = null; }

    private System.Collections.IEnumerator Watch()
    {
        var wait = new WaitForSeconds(1.5f);
        while (true) { yield return wait; Refresh(); }
    }

    /// <summary>The gun in each spot, in spot order (null for an empty spot). A gun with parts added or taken off counts as changed.</summary>
    private List<Item?> Guns(out string key)
    {
        int count = Kind == "hanger3" ? 3 : 1;
        var grids = item.Grids;
        var result = new List<Item?>();
        var sb = new StringBuilder();
        for (int n = 1; n <= count; n++)
        {
            var grid = grids.FirstOrDefault(g => g.ID == "gun_" + n) ?? (grids.Length >= n ? grids[n - 1] : null);
            var gun = grid?.Items.FirstOrDefault(i => i is Weapon);
            result.Add(gun);
            sb.Append(n).Append(':');
            if (gun != null) foreach (var part in gun.GetAllItems()) sb.Append(part.Id).Append(',');
            sb.Append('|');
        }
        sb.Append(muzzleLeft ? "L" : "R").Append(plateOn ? "P" : "-");
        key = sb.ToString();
        return result;
    }

    private void Refresh()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || item == null || !placed) return;
        try
        {
            var guns = Guns(out var key);
            if (key == current) return;
            current = key;
            Rebuild(plugin, guns);
        }
        catch (Exception error) { plugin.ReportOnce("display contents", error); }
    }

    private void Rebuild(Plugin plugin, List<Item?> guns)
    {
        ClearGuns();
        var gen = ++generation;
        var entries = new List<OnShow?>();
        foreach (var gun in guns)
        {
            if (gun == null) { entries.Add(null); continue; }
            var holder = new GameObject("WWCGun_gun") { layer = 0 };
            holder.transform.SetParent(anchor, false);
            var e = new OnShow { Item = gun, Holder = holder };
            shown.Add(e);
            entries.Add(e);
        }
        if (entries.All(e => e == null)) { ShowEmpty(); ApplyLook(); return; }
        SpawnAll(plugin, entries, gen);
    }

    private async void SpawnAll(Plugin plugin, List<OnShow?> entries, int gen)
    {
        try
        {
            foreach (var e in entries)
            {
                if (e == null) continue;
                try
                {
                    await EasyAssetsExtensions.WaitForAllBundlesJob(e.Item.GetAllBundleTokens(), () => { });
                    var model = await Singleton<ObjectsFactory>.Instance.CreateItemAsync(e.Item, ECameraType.Default, null, false, JobYieldPriority.General);
                    if (model == null) continue;
                    if (gen != generation || e.Holder == null) { AssetPoolObject.ReturnToPool(model); return; }
                    e.State = GunModelState.Prepare(model);
                    e.Model = model;
                    model.transform.SetParent(e.Holder.transform, false);
                    model.transform.localPosition = Vector3.zero;
                    model.transform.localScale = Vector3.one;
                    model.SetActive(true);
                    var pivot = model.GetComponent<PreviewPivot>();
                    // The game's own weapon stand turns guns this way: upright, muzzle to the viewer's right, right side out.
                    var standard = pivot != null ? Quaternion.Inverse(pivot.pivotRotation) : Quaternion.identity;
                    model.transform.localRotation = muzzleLeft ? Quaternion.Euler(0f, 180f, 0f) * standard : standard;
                    e.Shape = Silhouette.Measure(e.Holder.transform, model);
                }
                catch (Exception error) { plugin.ReportOnce("hanging gun " + e.Item.TemplateId, error); }
            }
            if (gen != generation) return;
            var shapes = entries.Select(e => e?.Shape).ToList();
            var built = IsBox ? Layouts.ShadowBox(shapes[0], "WWCGun_" + Kind, true, plateOn, Kind == "pistolbox") : Layouts.Hanger(shapes, Kind == "hanger3", "WWCGun_" + Kind);
            ClearPlate();
            var named = entries.FirstOrDefault(x => x?.Shape != null);
            if (built.PlateSize.x > 0f && named != null)
            {
                try
                {
                    plateTex = PlateMaker.Make(plugin.SharedFolder, named.Item.ShortName.Localized(), named.Item.Name.Localized(), built.PlateSize.x / built.PlateSize.y);
                    plateMat = plugin.PlateMaterial(donor, plateTex);
                }
                catch (Exception error) { plugin.ReportOnce("engraving the name plate", error); }
            }
            Apply(built);
            Report.Clear();
            Contacts.Clear();
            for (int i = 0; i < entries.Count && i < built.Seats.Count; i++)
            {
                var e = entries[i];
                if (e?.Shape == null || e.Holder == null) continue;
                var seat = built.Seats[i];
                e.Holder.transform.localPosition = seat.GunOffset;
                e.Holder.transform.localRotation = Quaternion.identity;
                foreach (var c in seat.Contacts)
                {
                    var kind = seat.OnPad ? "pad" : IsBox ? "standoff" : "hook";
                    Report.Add($"{Kind} spot {i + 1} {kind} at ({c.x:F3}, {c.y:F3}, {c.z:F3}); gun {e.Shape.Length:F2} x {e.Shape.Height:F2} x {e.Shape.Depth:F2} m");
                    if (e.Model != null) Contacts.Add((c, kind, e.Model));
                }
            }
            ApplyLook();
            plugin.Note($"{Kind} {item.Id} shows {GunCount} gun(s), {Size.x:F2} x {Size.y:F2} m");
        }
        catch (Exception error) { plugin.ReportOnce("building display", error); }
    }

    private void Apply(Layout next)
    {
        if (built != null && built != next.Mesh) Destroy(built);
        layout = next;
        built = next.Mesh;
        filter.sharedMesh = built;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        FitBody();
        ApplyLook();
    }

    /// <summary>The look-ray box covers the whole display, guns and light included, since the guns' own colliders are off while they hang.</summary>
    private void FitBody()
    {
        if (body == null || layout == null) return;
        var box = body.GetComponent<BoxCollider>();
        box.center = layout.Extent.center;
        box.size = layout.Extent.size + new Vector3(0f, 0f, 0.02f);
    }

    private void ApplyLook()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || layout == null) return;
        if (glow == null) glow = plugin.NewGlow();
        float b = ClientSettings.Current.Brightness;
        glow.color = lightOn ? new Color(1f, 0.86f, 0.62f) : new Color(0.1f, 0.09f, 0.08f);
        try { renderer.sharedMaterials = plugin.MaterialsFor(donor, wood, felt, glow, plateMat); }
        catch (Exception error) { plugin.ReportOnce("display materials", error); }
        if (!placed) return;
        // Lights only when hung: the lamp in the shade or channel, and a soft fill in front for the bigger displays.
        foreach (var l in lights) if (l != null) Destroy(l.gameObject);
        lights.Clear();
        foreach (var spec in layout.Lights) AddLight(spec, b);
        foreach (var l in lights) l.enabled = lightOn;
    }

    private void AddLight(LightSpec spec, float brightness)
    {
        var go = new GameObject("WWCGun_light") { layer = 0 };
        go.transform.SetParent(anchor, false);
        go.transform.localPosition = spec.Pos;
        var l = go.AddComponent<Light>();
        if (spec.Dir.sqrMagnitude > 0.5f)
        {
            go.transform.localRotation = Quaternion.LookRotation(spec.Dir, Vector3.up);
            l.type = LightType.Spot; l.spotAngle = spec.Angle; l.innerSpotAngle = spec.Angle * 0.55f;
        }
        else l.type = LightType.Point;
        l.range = spec.Range; l.intensity = spec.Intensity * brightness;
        l.color = new Color(1f, 0.84f, 0.64f);
        l.shadows = LightShadows.None; l.renderMode = LightRenderMode.ForcePixel;
        lights.Add(l);
    }

    private void EnsureBody()
    {
        if (body != null || anchor == null) return;
        body = new GameObject("WWCGun_interact");
        int layer = LayerMask.NameToLayer("Interactive");
        body.layer = layer >= 0 ? layer : anchor.gameObject.layer;
        body.transform.SetParent(anchor, false);
        body.AddComponent<BoxCollider>();
        body.AddComponent<GunInteractive>().Display = this;
        FitBody();
    }

    public void TurnRound()
    {
        muzzleLeft = !muzzleLeft;
        Plugin.Instance?.Looks.Set(item.Id, l => l.muzzleLeft = muzzleLeft);
        Refresh();
    }

    public void NextWood()
    {
        wood = (wood + 1) % Plugin.Woods.Length;
        Plugin.Instance?.Looks.Set(item.Id, l => l.wood = wood);
        ApplyLook();
    }

    public void TogglePlate()
    {
        plateOn = !plateOn;
        Plugin.Instance?.Looks.Set(item.Id, l => l.plate = plateOn);
        Refresh();
    }

    private void ClearPlate()
    {
        if (plateMat != null) Destroy(plateMat);
        if (plateTex != null) Destroy(plateTex);
        plateMat = null; plateTex = null;
    }

    public void NextFelt()
    {
        felt = (felt + 1) % Plugin.Felts.Length;
        Plugin.Instance?.Looks.Set(item.Id, l => l.felt = felt);
        ApplyLook();
    }

    public void ToggleLight()
    {
        lightOn = !lightOn;
        // "is null", not "== null": the plugin object may be gone (a destroyed Unity object compares equal to null) while its data is still fine to use.
        if (Plugin.Instance is not null) Plugin.Instance.Looks.Set(item.Id, l => l.lightOn = lightOn);
        ApplyLook();
    }

    private void ClearGuns()
    {
        generation++;
        foreach (var s in shown)
        {
            if (s.Model != null) { s.State?.Restore(); AssetPoolObject.ReturnToPool(s.Model); }
            if (s.Holder != null) Destroy(s.Holder);
        }
        shown.Clear();
        Report.Clear();
        Contacts.Clear();
    }

    /// <summary>Removes everything this component added, before the donor object goes back to the pool.</summary>
    public void Clear()
    {
        ClearGuns();
        foreach (var l in lights) if (l != null) Destroy(l.gameObject);
        lights.Clear();
        if (body != null) Destroy(body);
        body = null;
        if (built != null) Destroy(built);
        built = null; layout = null;
        if (glow != null) Destroy(glow);
        glow = null;
        ClearPlate();
        current = "\u0001";
        placed = false;
        if (watching != null) { StopCoroutine(watching); watching = null; }
    }

    private void OnDestroy() => Clear();
}

