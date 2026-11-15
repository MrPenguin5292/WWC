using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using Diz.Skinning;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace Slickster.Client;

/// <summary>
/// Slickster plate carrier visuals. Both carriers are clones of the Tasmanian Tiger SK, so the donor prefab supplies its dress,
/// loot model, plate slots and behaviour. Each rig mesh holds all four patches as submeshes; a patch shows only while its item sits in
/// the matching slot, so patches can be removed and moved between the rigs. Every change is undone when the object goes back to the pool;
/// on any failure the donor look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Slickster Plate Carriers", "0.2.0")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Slickster");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.Part;
#endif
    public const string Id = "local.slickster.visuals";
    public const string ChestSlot = "mod_equipment_000", DockSlot = "mod_equipment_001";
    private static Plugin instance = null!;
    private AssetBundle bundle = null!;
    private readonly Dictionary<string, CarrierBinding> carriers = new();
    private readonly Dictionary<string, PatchBinding> patches = new();
    private readonly Dictionary<string, Mesh> meshes = new();
    private readonly Dictionary<(string, int), Mesh> variants = new();
    private readonly Dictionary<(Material, string), Material> materials = new();
    private readonly HashSet<string> errors = new();
    private Dictionary<string, MaterialBinding> materialRows = new();
    private Harmony? harmony;
    private Mesh emptyMesh = null!;

    private void Awake()
    {
        try
        {
            emptyMesh = new Mesh { name = "Slickster_empty", hideFlags = HideFlags.HideAndDontSave };
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            var path = Path.Combine(Path.GetDirectoryName(Info.Location)!, "slickster.bundle");
            bundle = AssetBundle.LoadFromFile(path) ?? throw new FileNotFoundException("Slickster assets are missing.", path);
            var bindings = JsonConvert.DeserializeObject<Bindings>(Load<TextAsset>("SlicksterBindings").text)
                ?? throw new InvalidDataException("Bindings could not be read.");
            materialRows = bindings.materials;
            foreach (var pair in bindings.items)
            {
                var row = pair.Value;
                row.key = pair.Key;
                foreach (var lod in row.lods.Values)
                {
                    var mesh = Load<Mesh>(lod.mesh);
                    if (mesh.subMeshCount != row.materials.Length) throw new InvalidDataException("Submesh count differs: " + lod.mesh);
                    if (!mesh.isReadable) throw new InvalidDataException("Rig mesh is not readable: " + lod.mesh);
                    meshes[lod.mesh] = mesh;
                }
                carriers[row.templateId] = row;
            }
            foreach (var row in bindings.patches.Values)
            {
                meshes[row.mesh] = Load<Mesh>(row.mesh);
                patches[row.templateId] = row;
            }
            instance = this;
            harmony = new Harmony(Id);
            var create = AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Single(m => m.Name == nameof(ObjectsFactory.CreateItemAsync)
                && m.ReturnType == typeof(Task<GameObject>) && m.GetParameters()[0].ParameterType == typeof(Item));
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)));
            harmony.Patch(AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.AttachMods)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeAttachMods)));
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterReturnToPool)));
            harmony.Patch(AccessTools.Method(typeof(Slot), nameof(Slot.LocalizedName)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLocalizedSlotName)));
            var modSlotType = AccessTools.TypeByName("EFT.UI.DragAndDrop.ModSlotView");
            var showSlot = AccessTools.GetDeclaredMethods(modSlotType).Single(m => m.Name == "Show" &&
                m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(Slot));
            harmony.Patch(showSlot, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterShowSlot)));
            Logger.LogInfo($"Slickster ready: {carriers.Count} carriers, {patches.Count} patches, {meshes.Count} meshes.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Logger.LogError("Slickster visuals unavailable; the native donor appearance remains. " + error);
        }
    }

    private T Load<T>(string name) where T : UnityEngine.Object =>
        bundle.LoadAsset<T>(name) ?? throw new InvalidDataException("Missing Slickster asset: " + name);

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        if (instance is null) return;
        if (instance.carriers.ContainsKey(__0.StringTemplateId) || instance.patches.ContainsKey(__0.StringTemplateId)) __result = instance.ApplyAsync(__result, __0);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item)
    {
        var model = await task;
        if (model == null) return null;
        var state = model.GetComponent<SlicksterVisualState>() ?? model.AddComponent<SlicksterVisualState>();
        state.Restore();
        try
        {
            if (carriers.TryGetValue(item.StringTemplateId, out var carrier)) ApplyCarrier(model, item, carrier, state);
            else ApplyPatch(model, patches[item.StringTemplateId], state);
        }
        catch (Exception error)
        {
            state.Restore();
            if (errors.Add(item.StringTemplateId)) Logger.LogError($"Slickster model failed for {item.StringTemplateId}: {error}");
        }
        return model;
    }

    private void ApplyCarrier(GameObject model, Item item, CarrierBinding binding, SlicksterVisualState state)
    {
        var dress = model.GetComponent<DressItem>() ?? throw new InvalidDataException("Donor DressItem is missing.");
        var skins = dress.DressPrefab.GetComponentsInChildren<Skin>(true);
        var filters = dress.LootPrefab.GetComponentsInChildren<MeshFilter>(true)
            .Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
        if (skins.Length == 0 || filters.Length == 0) throw new InvalidDataException("Donor dress/loot meshes are missing.");
        var donor = skins[0].SkinnedMeshRenderer.sharedMaterial ?? throw new InvalidDataException("Donor material is missing.");
        var mats = binding.materials.Select(key => MakeMaterial(donor, key)).ToArray();
        var renderers = skins.Select(s => s.SkinnedMeshRenderer).ToArray();
        foreach (var renderer in renderers) { state.Remember(renderer, null); renderer.sharedMaterials = mats; renderer.updateWhenOffscreen = true; }
        foreach (var filter in filters) { var r = filter.GetComponent<MeshRenderer>(); state.Remember(r, filter); r.sharedMaterials = mats; }
        var collection = item as ContainerCollection ?? throw new InvalidDataException("Slickster has no slots.");
        var slots = collection.Containers.OfType<Slot>().Where(s => s.ID == ChestSlot || s.ID == DockSlot).ToArray();
        void Refresh()
        {
            if (model == null || dress == null || dress.LootPrefab == null) return;
            int mask = 0;
            foreach (var slot in slots)
                if (slot.ContainedItem != null && patches.TryGetValue(slot.ContainedItem.StringTemplateId, out var fitted)) mask |= 1 << fitted.submeshes[slot.ID];
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var mesh = Variant(binding, renderer.name, mask);
                renderer.sharedMesh = mesh;
                renderer.localBounds = mesh.bounds;
            }
            foreach (var filter in filters) if (filter != null) filter.sharedMesh = Variant(binding, filter.name, mask);
            foreach (var lod in dress.LootPrefab.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
            state.CurrentMask = mask;
        }
        state.RefreshVisuals = Refresh;
        Refresh();
        foreach (var slot in slots)
        {
            Action<Item> changed = _ => state.RefreshVisuals?.Invoke();
            slot.OnAddOrRemoveItem += changed;
            state.Unsubscribe(() => slot.OnAddOrRemoveItem -= changed);
        }
        state.Key = binding.key;
    }

    /// <summary>Clones a rig mesh with the patches not in the mask stripped out. Clones are cached and shared.</summary>
    private Mesh Variant(CarrierBinding binding, string rendererName, int mask)
    {
        string lod = rendererName.EndsWith("lod1", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
        string name = binding.lods[lod].mesh;
        if (variants.TryGetValue((name, mask), out var cached)) return cached;
        var source = meshes[name];
        var mesh = Instantiate(source);
        mesh.name = $"{name}_m{mask}";
        foreach (var patch in patches.Values)
            foreach (int submesh in patch.submeshes.Values)
                if ((mask & (1 << submesh)) == 0) mesh.SetTriangles(Array.Empty<int>(), submesh, false);
        mesh.bounds = source.bounds;
        variants[(name, mask)] = mesh;
        return mesh;
    }

    private void ApplyPatch(GameObject model, PatchBinding binding, SlicksterVisualState state)
    {
        // The mount donor supplies a rigid loot and inventory model. A fitted patch shows on the carrier mesh instead (its own model hangs on a hidden mount).
        var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
        if (filters.Length == 0) throw new InvalidDataException("Patch donor has no rigid renderer.");
        var mats = new[] { MakeMaterial(filters[0].GetComponent<MeshRenderer>().sharedMaterial, binding.key) };
        var mesh = meshes[binding.mesh];
        // The TATM donor has two renderers (base and arm) at different poses: draw the patch once, on the base, and blank the rest.
        var primary = filters.FirstOrDefault(f => f.name.IndexOf("_base", StringComparison.OrdinalIgnoreCase) >= 0) ?? filters[0];
        foreach (var filter in filters)
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            state.Remember(renderer, filter);
            filter.sharedMesh = filter == primary ? mesh : emptyMesh;
            renderer.sharedMaterials = mats;
        }
        foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
        state.Key = binding.key;
    }

    private Material MakeMaterial(Material donor, string key)
    {
        if (materials.TryGetValue((donor, key), out var found)) return found;
        var row = materialRows[key];
        var material = new Material(donor) { name = "Slickster_" + key };
        void Set(string property, string? asset)
        {
            if (!material.HasProperty(property)) return;
            material.SetTexture(property, string.IsNullOrEmpty(asset) ? null : Load<Texture2D>(asset!));
            material.SetTextureScale(property, Vector2.one);
            material.SetTextureOffset(property, Vector2.zero);
        }
        Set("_MainTex", row.albedo);
        Set("_BumpMap", row.normal);
        Set("_SpecMap", row.gloss);
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        // Numbers from Tarkov's own armored rigs (rig_material_survey): fabric keeps the donor's 1.95, hard parts are nearer 1.
        float specularness = key switch { "gslickster" => 2.0f, "gmkv" => 2.0f, "kagwerksdocklite_co" => 1.1f, "pmag30" => 1.0f, _ => 1.2f };
        if (material.HasProperty("_Specularness")) material.SetFloat("_Specularness", specularness);
        if (material.HasProperty("_Glossness")) material.SetFloat("_Glossness", 1f);
        materials.Add((donor, key), material);
        return material;
    }

    // Each patch slot needs a socket on the assembled rig. The sockets sit on an inactive object so a fitted patch's own loose model stays hidden.
    private static void BeforeAttachMods(ContainerCollection containerCollection, ContainerCollectionView collectionView)
    {
        try
        {
            if (instance is null || !instance.carriers.ContainsKey(containerCollection.StringTemplateId)) return;
            var root = collectionView.GameObject;
            (root.GetComponent<SlicksterVisualState>() ?? root.AddComponent<SlicksterVisualState>()).EnsureHiddenMounts();
        }
        catch (Exception error) { Debug.LogError("[Slickster] socket setup failed: " + error); }
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<SlicksterVisualState>()?.Restore();
    }

    private static void AfterReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<SlicksterVisualState>()?.ClearMounts();
    }

    private static string? SlotLabel(Slot slot)
    {
        if (instance is null || slot.ParentItem == null || !instance.carriers.ContainsKey(slot.ParentItem.StringTemplateId)) return null;
        return slot.ID == ChestSlot ? "CHEST PATCH" : slot.ID == DockSlot ? "DOCK PATCH" : null;
    }

    private static void AfterLocalizedSlotName(Slot __instance, ref string __result)
    {
        var label = SlotLabel(__instance);
        if (label != null) __result = label;
    }

    private static void AfterShowSlot(object __instance, Slot __0)
    {
        var label = SlotLabel(__0);
        if (label == null) return;
        var text = AccessTools.Field(__instance.GetType(), "_slotName")?.GetValue(__instance);
        if (text != null) AccessTools.Property(text.GetType(), "text").SetValue(text, label, null);
    }

    public sealed class Bindings
    {
        public Dictionary<string, CarrierBinding> items = new();
        public Dictionary<string, PatchBinding> patches = new();
        public Dictionary<string, MaterialBinding> materials = new();
    }
    public sealed class CarrierBinding
    {
        public string key = "", templateId = "";
        public Dictionary<string, LodBinding> lods = new();
        public string[] materials = Array.Empty<string>();
    }
    public sealed class LodBinding { public string mesh = ""; }
    public sealed class PatchBinding { public string key = "", templateId = "", mesh = ""; public Dictionary<string, int> submeshes = new(); }
    public sealed class MaterialBinding { public string? albedo, normal, gloss; }
}

/// <summary>Records every change made to a pooled object so it can be put back exactly.</summary>
public sealed class SlicksterVisualState : MonoBehaviour
{
    private readonly List<Action> restore = new();
    private GameObject? mounts;
    public string Key { get; internal set; } = "";
    public int CurrentMask { get; internal set; }
    public Action? RefreshVisuals { get; internal set; }

    public void EnsureHiddenMounts()
    {
        if (mounts != null) return;
        mounts = new GameObject("SlicksterHiddenPatchMounts");
        mounts.transform.SetParent(transform, false);
        mounts.SetActive(false);
        foreach (string slot in new[] { Plugin.ChestSlot, Plugin.DockSlot })
            new GameObject(slot).transform.SetParent(mounts.transform, false);
    }

    public void Remember(Renderer renderer, MeshFilter? filter)
    {
        var skinned = renderer as SkinnedMeshRenderer;
        var mesh = skinned != null ? skinned.sharedMesh : filter!.sharedMesh;
        var materials = renderer.sharedMaterials;
        var bounds = skinned != null ? skinned.localBounds : default;
        bool offscreen = skinned != null && skinned.updateWhenOffscreen;
        restore.Add(() =>
        {
            if (renderer == null) return;
            renderer.sharedMaterials = materials;
            if (skinned != null) { skinned.sharedMesh = mesh; skinned.localBounds = bounds; skinned.updateWhenOffscreen = offscreen; }
            else if (filter != null) filter.sharedMesh = mesh;
        });
    }

    public void Unsubscribe(Action action) => restore.Add(action);

    public void Restore()
    {
        RefreshVisuals = null;
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
        Key = "";
        CurrentMask = 0;
    }

    public void ClearMounts()
    {
        if (mounts == null) return;
        // AssetPoolObject has returned its attached children before this postfix.
        DestroyImmediate(mounts);
        mounts = null;
    }

    private void OnDestroy() => Restore();
}
