using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using Diz.Skinning;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace USMCPC.Client;

/// <summary>
/// Shaw Concepts ARC V2 visuals (called "USMC Plate Carrier Gen III" until 2026-11-18). The carrier clones the Eagle MMAC's stats but uses the Tasmanian Tiger SK prefab (the Slickster
/// pipeline's donor), so the donor supplies dress, loot model and behaviour. Every part (fronts, side pouches, phone mount, radio, patch)
/// is a readable skinned mesh in the donor's bind space; the client copies the parts fitted in the carrier's slots into one mesh per
/// carrier and LOD (cached by combination, so bots with the same kit share it). A patch (kit-made or one of the Slickster's own four) in
/// the chest or back patch slot is drawn as an extra skinned renderer laid on its spot (PatchDraw.cs, patch_spots.json). Every change is undone when the object goes back to the pool; on any failure the
/// donor look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Shaw Concepts ARC V2", "0.3.2")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC USMC PC");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("USMCPC");
#endif
    public const string Id = "local.usmcpc.visuals";
    internal static Plugin? instance;
    private AssetBundle bundle = null!;
    internal Bindings binding = null!;
    private readonly Dictionary<string, PartData[]> parts = new();
    private readonly Dictionary<(Material, string), Material> materials = new();
    private readonly Dictionary<string, Mesh> displayMeshes = new();
    private readonly HashSet<string> errors = new();
    private readonly HashSet<string> ourSlots = new();
    internal readonly MeshCache cache = new();
    private readonly Dictionary<(string, string), Mesh> kitMeshes = new();
    private string[] materialOrder = Array.Empty<string>();
    private Harmony? harmony;
    internal Mesh emptyMesh = null!;

    private void Awake()
    {
        try
        {
            emptyMesh = new Mesh { name = "USMCPC_empty", hideFlags = HideFlags.HideAndDontSave };
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            var path = Path.Combine(Path.GetDirectoryName(Info.Location)!, "usmcpc.bundle");
            bundle = AssetBundle.LoadFromFile(path) ?? throw new FileNotFoundException("USMCPC assets are missing.", path);
            binding = JsonConvert.DeserializeObject<Bindings>(Load<TextAsset>("USMCPCBindings").text) ?? throw new InvalidDataException("Bindings could not be read.");
            materialOrder = binding.materials.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            foreach (var pair in binding.parts)
            {
                var lods = new PartData[2];
                for (int lod = 0; lod < 2; lod++)
                {
                    var mesh = Load<Mesh>(pair.Value.lods[lod.ToString()].mesh);
                    if (!mesh.isReadable) throw new InvalidDataException("Part mesh is not readable: " + mesh.name);
                    if (mesh.subMeshCount != pair.Value.materials.Length) throw new InvalidDataException("Submesh count differs: " + mesh.name);
                    lods[lod] = new PartData(mesh, pair.Value.materials);
                }
                parts[pair.Key] = lods;
            }
            foreach (var row in binding.items.Values) foreach (var p in row.parts) if (!parts.ContainsKey(p)) throw new InvalidDataException("Item part missing: " + p);
            LoadSpots(Path.GetDirectoryName(Info.Location)!);
            foreach (var s in binding.slots.Values) ourSlots.Add(s);
            instance = this;
            harmony = new Harmony(Id);
            var create = AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Single(m => m.Name == nameof(ObjectsFactory.CreateItemAsync)
                && m.ReturnType == typeof(Task<GameObject>) && m.GetParameters()[0].ParameterType == typeof(Item));
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)));
            harmony.Patch(AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.AttachMods)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeAttachMods)));
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterReturnToPool)));
            harmony.Patch(AccessTools.Method(typeof(Slot), nameof(Slot.LocalizedName)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLocalizedSlotName)));
            var modSlotType = AccessTools.TypeByName("EFT.UI.DragAndDrop.ModSlotView");
            var showSlot = AccessTools.GetDeclaredMethods(modSlotType).Single(m => m.Name == "Show" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(Slot));
            harmony.Patch(showSlot, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeShowSlot)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterShowSlot)));
            SeedSlotIcons();
            harmony.Patch(AccessTools.Method(typeof(IconsHash), nameof(IconsHash.GetItemHash)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterIconHash)));
            harmony.Patch(AccessTools.Method(typeof(ItemIconCreator), "RenderModel"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRenderIcon)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRenderIcon)));
            Logger.LogInfo($"Shaw Concepts ARC V2 ready: {parts.Count} parts, {binding.items.Count} part items, {binding.materials.Count} materials.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            instance = null;
            Logger.LogError("Shaw Concepts ARC V2 visuals unavailable; the native donor appearance remains. " + error);
        }
    }

    // Own rig layout (RigLayout.cs): applied once the item templates are loaded, again whenever the item factory is replaced.
    private void Update()
    {
        if (instance != null) RigLayout.Tick(Logger);
    }

    /// <summary>
    /// The slot view asks ResourcesCache for "Slots/&lt;slot name&gt;"; our slot names have no icon of their own, so the cache gets the
    /// equipment icon under our names (ResourcesCache._storage, the dictionary Pop reads first), seeded at start and again before one of
    /// our slots is drawn if the cache lost it. 0.2.0 patched the generic ResourcesCache.Pop&lt;Sprite&gt; instead: generic code over reference
    /// types is shared, so Pop&lt;Image&gt;("Prefabs/UGUI/filter_icon") went through the Sprite-typed patch and came back null, and every inspect
    /// window with mod slots threw in ModSlotView.SetFilters (found by the Airlite thread, 2026-11-16). Nothing here patches Pop.
    /// </summary>
    private static System.Collections.IDictionary? iconStorage;
    private static MethodInfo? popSprite;

    private void SeedSlotIcons()
    {
        try
        {
            var cache = AccessTools.TypeByName("EFT.Utilities.ResourcesCache") ?? AccessTools.TypeByName("ResourcesCache");
            iconStorage = cache == null ? null : AccessTools.Field(cache, "_storage")?.GetValue(null) as System.Collections.IDictionary;
            var pop = cache == null ? null : AccessTools.GetDeclaredMethods(cache).FirstOrDefault(m => m.Name == "Pop" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(string));
            popSprite = pop?.MakeGenericMethod(typeof(Sprite));
            if (iconStorage == null || popSprite == null) { Logger.LogWarning("Slot icons: ResourcesCache not found; our slots show without an icon."); return; }
            Logger.LogInfo($"Slot icons: {SeedIcons()} slot names use the equipment slot icon.");
        }
        catch (Exception error) { Logger.LogWarning("Slot icons not seeded; our slots show without an icon. " + error.Message); }
    }

    private static int SeedIcons()
    {
        if (iconStorage == null || popSprite == null || instance == null) return 0;
        int seeded = 0;
        Sprite? icon = null;
        foreach (var slot in instance.ourSlots)
        {
            var key = "Slots/" + slot;
            if (iconStorage.Contains(key) && iconStorage[key] != null) continue;
            icon ??= popSprite.Invoke(null, new object[] { "Slots/mod_equipment" }) as Sprite;
            if (icon == null) return seeded;
            iconStorage[key] = icon; seeded++;
        }
        return seeded;
    }

    private static void BeforeShowSlot(Slot __0)
    {
        try { if (__0 != null && instance != null && instance.ourSlots.Contains(__0.ID)) SeedIcons(); } catch { }
    }

    // ---------------------------------------------------------------------------------------------------------------- stash icons
    // The stash icon is cached by an item hash that does not see what sits in our part slots, so every carrier got the first icon drawn
    // (fixture run USMCPC_20261116_031410: ten loadouts, one identical bare icon). The parts now count in the hash.
    private static void AfterIconHash(Item item, ref int __result)
    {
        var p = instance;
        if (p is null || item is not CompoundItem compound || item.StringTemplateId != p.binding.carrier) return;
        int i = 1;
        foreach (var slot in compound.Containers.OfType<Slot>().Where(s => p.ourSlots.Contains(s.ID)).OrderBy(s => s.ID, StringComparer.Ordinal))
        {
            if (slot.ContainedItem != null) __result ^= (slot.ContainedItem.TemplateId.GetHashCode() * 397) ^ (i * 7919);
            i++;
        }
    }

    // The icon is drawn from a clone that drops the contents of anything with hidden contents, and a rig is one: the clone kept no parts.
    // For a carrier with parts, the clone is made from a copy holding only the parts, with the template letting them through for that one
    // synchronous step (same route as the WWC ammo cans' stickers).
    private static void BeforeRenderIcon(ref Item item, out (ItemTemplate template, bool hide)? __state)
    {
        __state = null;
        var p = instance;
        if (p is null || item is not CompoundItem compound || item.StringTemplateId != p.binding.carrier) return;
        try
        {
            var filled = compound.Containers.OfType<Slot>().Where(s => p.ourSlots.Contains(s.ID) && s.ContainedItem != null).ToList();
            if (filled.Count == 0) return;
            var trimmed = (CompoundItem)item.CloneVisibleItem();
            var target = trimmed.Containers.OfType<Slot>().ToDictionary(s => s.ID);
            foreach (var slot in filled)
                if (target.TryGetValue(slot.ID, out var t) && t.ContainedItem == null) t.AddWithoutRestrictions(slot.ContainedItem.CloneVisibleItem());
            __state = (trimmed.Template, trimmed.Template.HideEntrails);
            trimmed.Template.HideEntrails = false;
            item = trimmed;
        }
        catch (Exception error) { p.ReportOnce("icon parts", error); }
    }

    // The clone is made before RenderModel's first await, so this runs with it already done.
    private static void AfterRenderIcon((ItemTemplate template, bool hide)? __state)
    {
        if (__state != null) __state.Value.template.HideEntrails = __state.Value.hide;
    }

    internal T Load<T>(string name) where T : UnityEngine.Object => bundle.LoadAsset<T>(name) ?? throw new InvalidDataException("Missing USMCPC asset: " + name);

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"Shaw Concepts ARC V2: {key}: {error}");
    }

    internal void LogInfo(string text) => Logger.LogInfo(text);

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        if (instance is null) return;
        var tpl = __0.StringTemplateId;
        if (tpl == instance.binding.carrier || instance.binding.items.ContainsKey(tpl)) __result = instance.ApplyAsync(__result, __0);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item)
    {
        var model = await task;
        if (model == null) return null;
        var state = model.GetComponent<CarrierVisualState>() ?? model.AddComponent<CarrierVisualState>();
        state.Restore();
        try
        {
            if (item.StringTemplateId == binding.carrier) ApplyCarrier(model, item, state);
            else ApplyPartItem(model, binding.items[item.StringTemplateId], state);
        }
        catch (Exception error)
        {
            state.Restore();
            ReportOnce("model " + item.StringTemplateId, error);
        }
        return model;
    }

    // ---------------------------------------------------------------------------------------------------------------- carrier
    private void ApplyCarrier(GameObject model, Item item, CarrierVisualState state)
    {
        var dress = model.GetComponent<DressItem>() ?? throw new InvalidDataException("Donor DressItem is missing.");
        var skins = dress.DressPrefab.GetComponentsInChildren<Skin>(true).Select(s => s.SkinnedMeshRenderer).Where(r => r != null).ToArray();
        var filters = dress.LootPrefab.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
        if (skins.Length == 0 || filters.Length == 0) throw new InvalidDataException("Donor dress/loot meshes are missing.");
        var donor = skins[0].sharedMaterial ?? throw new InvalidDataException("Donor material is missing.");
        foreach (var r in skins) { state.Remember(r, null); r.updateWhenOffscreen = true; }
        foreach (var f in filters) state.Remember(f.GetComponent<MeshRenderer>(), f);
        var collection = item as CompoundItem ?? throw new InvalidDataException("Carrier has no slots.");
        var slots = collection.Containers.OfType<Slot>().Where(s => ourSlots.Contains(s.ID)).ToDictionary(s => s.ID);
        var mainSkin = skins.FirstOrDefault(r => !IsLod1(r.name)) ?? skins[0];
        var mainLoot = filters.FirstOrDefault(f => !IsLod1(f.name)) ?? filters[0];
        void Refresh()
        {
            if (model == null || dress == null) return;
            var fitted = FittedParts(slots, out var kitPatch, out bool phone);
            foreach (var r in skins)
            {
                if (r == null) continue;
                var mesh = state.Use(r, fitted, IsLod1(r.name) ? 1 : 0);
                r.sharedMesh = mesh;
                r.sharedMaterials = MaterialsFor(donor, mesh);
                r.localBounds = mesh.bounds;
            }
            foreach (var f in filters)
            {
                if (f == null) continue;
                var mesh = state.Use(f, fitted, IsLod1(f.name) ? 1 : 0);
                f.sharedMesh = mesh;
                f.GetComponent<MeshRenderer>().sharedMaterials = MaterialsFor(donor, mesh);
            }
            foreach (var lod in dress.LootPrefab.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
            state.ShowKitPatch("front", kitPatch, phone ? "patch_mount" : "patch_chest", mainSkin, mainLoot, donor);
            state.ShowKitPatch("back", BackPatch(slots), backSpot, mainSkin, mainLoot, donor);
            state.Fitted = string.Join(",", fitted);
        }
        state.RefreshVisuals = Refresh;
        Refresh();
        foreach (var slot in slots.Values)
        {
            Action<Item> changed = _ => { try { state.RefreshVisuals?.Invoke(); } catch (Exception error) { ReportOnce("refresh", error); } };
            slot.OnAddOrRemoveItem += changed;
            state.OnRestore(() => slot.OnAddOrRemoveItem -= changed);
        }
        state.Key = "carrier";
    }

    private static bool IsLod1(string name) => name.EndsWith("lod1", StringComparison.OrdinalIgnoreCase);

    /// <summary>The parts to draw for what is in the slots, sorted (the cache key). A kit patch in the patch slot comes back separately.</summary>
    internal List<string> FittedParts(Dictionary<string, Slot> slots, out Item? kitPatch, out bool phone)
    {
        kitPatch = null;
        var list = new List<string> { "carrier" };
        Item? In(string kind) => slots.TryGetValue(binding.slots[kind], out var s) ? s.ContainedItem : null;
        ItemRow? Row(Item? i) => i != null && binding.items.TryGetValue(i.StringTemplateId, out var r) ? r : null;
        var front = In("front");
        phone = Row(In("phone"))?.key == "phone_mount";
        if (front == null || !binding.chestBucklesHiddenBy.Contains(front.StringTemplateId)) list.Add("chest_buckles");
        if (Row(front) is { } fr) list.Add(fr.parts[0]);
        if (Row(In("left")) is { } l) list.Add(l.parts[0]);
        if (Row(In("right")) is { } r) list.Add(r.parts[r.parts.Length > 1 ? 1 : 0]);
        if (phone) list.Add("phone_mount");
        if (Row(In("radio"))?.key == "radio") list.Add("radio");
        var patch = In("patch");
        if (patch != null)
        {
            if (Row(patch)?.key == "patch_usmc") list.Add(phone ? "patch_mount" : "patch_chest");
            else kitPatch = patch;
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>The patch in the back slot (0.3.0, from patch_spots.json); null without that file or with the slot empty.</summary>
    internal Item? BackPatch(Dictionary<string, Slot> slots) =>
        backSpot.Length > 0 && binding.slots.TryGetValue("patch_back", out var name) && slots.TryGetValue(name, out var s) ? s.ContainedItem : null;

    /// <summary>One mesh for a set of parts: vertices copied part after part, triangles grouped by material (one submesh per material).</summary>
    internal Mesh Build(List<string> keys, int lod)
    {
        var datas = keys.Select(k => parts[k][lod]).ToArray();
        int total = datas.Sum(d => d.Vertices.Length);
        var v = new Vector3[total]; var n = new Vector3[total]; var t = new Vector4[total]; var uv = new Vector2[total]; var bw = new BoneWeight[total];
        var tris = new Dictionary<string, List<int>>();
        int offset = 0;
        foreach (var d in datas)
        {
            Array.Copy(d.Vertices, 0, v, offset, d.Vertices.Length);
            Array.Copy(d.Normals, 0, n, offset, d.Normals.Length);
            Array.Copy(d.Tangents, 0, t, offset, d.Tangents.Length);
            Array.Copy(d.Uv, 0, uv, offset, d.Uv.Length);
            Array.Copy(d.Weights, 0, bw, offset, d.Weights.Length);
            for (int s = 0; s < d.Materials.Length; s++)
            {
                if (!tris.TryGetValue(d.Materials[s], out var list)) tris[d.Materials[s]] = list = new List<int>();
                foreach (int i in d.Triangles[s]) list.Add(i + offset);
            }
            offset += d.Vertices.Length;
        }
        var order = materialOrder.Where(tris.ContainsKey).ToArray();
        var mesh = new Mesh { name = $"USMCPC_{lod}_{string.Join("+", keys)}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = v; mesh.normals = n; mesh.tangents = t; mesh.uv = uv; mesh.boneWeights = bw; mesh.bindposes = datas[0].Bindposes;
        mesh.subMeshCount = order.Length;
        for (int s = 0; s < order.Length; s++) mesh.SetTriangles(tris[order[s]], s, false);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);
        meshMaterials[mesh] = order;
        return mesh;
    }

    private readonly Dictionary<Mesh, string[]> meshMaterials = new();

    internal void Forget(Mesh mesh) => meshMaterials.Remove(mesh);

    private Material[] MaterialsFor(Material donor, Mesh mesh) =>
        meshMaterials.TryGetValue(mesh, out var keys) ? keys.Select(k => MakeMaterial(donor, k)).ToArray() : Array.Empty<Material>();

    internal Material MakeMaterial(Material donor, string key)
    {
        if (materials.TryGetValue((donor, key), out var found)) return found;
        var row = binding.materials[key];
        var material = new Material(donor) { name = "USMCPC_" + key };
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
        // Numbers from Tarkov's own coyote rigs (tools/rig_material_survey.py): fabric 1.1-1.2, hard parts about 1.
        if (material.HasProperty("_Specularness")) material.SetFloat("_Specularness", row.specularness);
        if (material.HasProperty("_Glossness")) material.SetFloat("_Glossness", 1f);
        materials.Add((donor, key), material);
        return material;
    }

    // ---------------------------------------------------------------------------------------------------------------- part items
    private void ApplyPartItem(GameObject model, ItemRow row, CarrierVisualState state)
    {
        var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
        if (filters.Length == 0) throw new InvalidDataException("Part donor has no rigid renderer.");
        var donor = filters[0].GetComponent<MeshRenderer>().sharedMaterial;
        var displayPart = row.key == "patch_usmc" ? "patch_chest" : row.parts[row.parts.Length > 1 ? 1 : 0];
        var mesh = DisplayMesh(displayPart);
        var mats = parts[displayPart][0].Materials.Select(k => MakeMaterial(donor, k)).ToArray();
        // The TATM donor has a base and an arm at different poses: the part goes on the base, at the model's origin, the rest is blanked.
        var primary = filters.FirstOrDefault(f => f.name.IndexOf("_base", StringComparison.OrdinalIgnoreCase) >= 0) ?? filters[0];
        foreach (var filter in filters)
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            state.Remember(renderer, filter);
            filter.sharedMesh = filter == primary ? mesh : emptyMesh;
            renderer.sharedMaterials = mats;
        }
        state.RememberTransform(primary.transform);
        primary.transform.localPosition = Vector3.zero;
        primary.transform.localRotation = Quaternion.identity;
        foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
        var pivot = model.GetComponent<PreviewPivot>();
        if (pivot != null)
        {
            state.RememberPivot(pivot);
            pivot.pivotPosition = model.transform.InverseTransformPoint(primary.transform.TransformPoint(mesh.bounds.center));
        }
        state.Key = row.key;
    }

    /// <summary>A rigid copy of a part's LOD0 with its centre at the origin, for the loose item, its icon and the inspect window.</summary>
    private Mesh DisplayMesh(string part)
    {
        if (displayMeshes.TryGetValue(part, out var found)) return found;
        var d = parts[part][0];
        // Part meshes are in the donor's bind space, which is turned against the body (run USMCPC_20261116_031410: every part icon showed
        // the part edge-on). Turn them into body axes (x right, y up, z out of the chest) so the front faces the icon and inspect cameras.
        var b = binding.bindBase;
        var rot = new Matrix4x4();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) rot[r, c] = b[r][c];
        rot[3, 3] = 1f;
        var turned = d.Vertices.Select(p => rot.MultiplyVector(p)).ToArray();
        var lo = turned.Aggregate(Vector3.Min); var hi = turned.Aggregate(Vector3.Max);
        var centre = (lo + hi) * 0.5f;
        var mesh = new Mesh { name = "USMCPC_display_" + part, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = turned.Select(p => p - centre).ToArray();
        mesh.normals = d.Normals.Select(n => rot.MultiplyVector(n)).ToArray();
        mesh.tangents = d.Tangents.Select(t => { var v = rot.MultiplyVector(new Vector3(t.x, t.y, t.z)); return new Vector4(v.x, v.y, v.z, t.w); }).ToArray();
        mesh.uv = d.Uv;
        mesh.subMeshCount = d.Materials.Length;
        for (int s = 0; s < d.Materials.Length; s++) mesh.SetTriangles(d.Triangles[s], s, false);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);
        displayMeshes[part] = mesh;
        return mesh;
    }

    // ---------------------------------------------------------------------------------------------------------------- kit patches
    private object? jackalPatches;
    private bool jackalTried;
    /// <summary>Every patch spot, by name: the bundle's own (patch_chest, patch_mount; 0.2.x placement) with patch_spots.json laid over them
    /// (0.3.0: the same spots draped, rooms on the velcro, and the new patch_back).</summary>
    internal readonly Dictionary<string, DrawSpot> spots = new();
    internal string backSpot = "";

    /// <summary>The bundle's spots as 0.2.x placed patches (room round the USMC patch, measured from the fitted meshes: the phone case face
    /// ends 35 mm above it, the chest velcro's laser-cut top edge too), then patch_spots.json over them, then the back slot it names.</summary>
    private void LoadSpots(string folder)
    {
        foreach (var pair in binding.patchPoints)
        {
            var p = pair.Value;
            bool mount = pair.Key == "patch_mount";
            binding.patchFields.TryGetValue(pair.Key, out var f);
            spots[pair.Key] = new DrawSpot
            {
                Centre = p.V(p.centre), Normal = p.V(p.normal).normalized, Up = p.V(p.up).normalized, Right = p.V(p.right).normalized,
                Back = p.back, HalfW = mount ? 0.045f : 0.060f, Top = 0.033f, Bottom = mount ? 0.040f : 0.050f, Drape = false,
                Field = f == null ? null : new SpotField { cell = f.cell, halfW = f.halfW, halfH = f.halfH, nx = f.nx, ny = f.ny, heights = f.heights, boneIndices = f.boneIndices, boneWeights = f.boneWeights },
                Weight = new BoneWeight
                {
                    boneIndex0 = p.boneIndices[0], boneIndex1 = p.boneIndices[1], boneIndex2 = p.boneIndices[2], boneIndex3 = p.boneIndices[3],
                    weight0 = p.boneWeights[0], weight1 = p.boneWeights[1], weight2 = p.boneWeights[2], weight3 = p.boneWeights[3],
                },
            };
        }
        var file = PatchDraw.LoadSpots(folder, spots, text => Logger.LogInfo(text));
        if (file == null) return;
        // 0.3.1: the chest phone mounts (and the patches drawn on them) move as one solid piece; see SpotFile.rigid
        PatchDraw.ApplyRigid(file, (p, lod) => parts.TryGetValue(p, out var d) && lod < d.Length ? d[lod].Weights : null, spots, text => Logger.LogInfo(text));
        if (file.slots.TryGetValue("patch_back", out var backSlot) && spots.ContainsKey(file.backSpot))
        {
            binding.slots["patch_back"] = backSlot;
            if (file.slotLabels.TryGetValue(backSlot, out var label)) binding.slotLabels[backSlot] = label;
            backSpot = file.backSpot;
        }
    }

    /// <summary>A patch fitted onto one of our spots as a skinned mesh in the carrier's bind space. Null when its mesh cannot be found.</summary>
    internal (Mesh mesh, Material[] mats)? KitPatch(Item patch, string point, Material donor)
    {
        if (!spots.TryGetValue(point, out var spot)) return null;
        var src = KitSource(patch, donor);
        if (src == null) return null;
        var (patchMesh, mats) = src.Value;
        var key = (patch.StringTemplateId + "|" + patchMesh.GetInstanceID(), point);
        if (kitMeshes.TryGetValue(key, out var made)) return (made, mats);
        // Each vertex bends with the surface under it (run USMCPC_20261116_032517: with one weight for the whole patch, the phone case's top
        // edge came forward over the patch's top in the range pose, though the two sit 0.6 mm apart at rest).
        var mesh = PatchDraw.Fit(patchMesh, spot, parts["carrier"][0].Bindposes, $"USMCPC_kitpatch_{patch.StringTemplateId}_{point}", out var report);
        kitMeshes[key] = mesh;
        LogInfo($"Patch {patch.StringTemplateId} on {point}: {report}");
        return (mesh, mats);
    }

    /// <summary>Where a patch's mesh comes from: Jackal Patches' own rig asset by template id, else the Slickster's own patches (their loose
    /// mesh, levelled; PatchDraw.SlicksterPatch).</summary>
    private (Mesh, Material[])? KitSource(Item patch, Material donor)
    {
        return JackalSource(patch, donor) ?? PatchDraw.SlicksterPatch(patch.StringTemplateId, donor, binding.bindBase, ReportOnce);
    }

    private (Mesh, Material[])? JackalSource(Item patch, Material donor)
    {
        try
        {
            if (!jackalTried)
            {
                jackalTried = true;
                var type = AccessTools.TypeByName("JackalPatches.Client.Plugin");
                jackalPatches = type == null ? null : AccessTools.Field(type, "Instance")?.GetValue(null);
            }
            if (jackalPatches != null)
            {
                var dict = AccessTools.Field(jackalPatches.GetType(), "Patches")?.GetValue(jackalPatches) as System.Collections.IDictionary;
                var entry = dict?[patch.StringTemplateId];
                if (entry != null)
                {
                    var asset = AccessTools.Method(jackalPatches.GetType(), "RigAsset")?.Invoke(jackalPatches, new[] { entry, donor });
                    if (asset != null)
                    {
                        var mesh = Traverse.Create(asset).Field("Mesh").GetValue<Mesh>();
                        var mats = Traverse.Create(asset).Field("Materials").GetValue<Material[]>();
                        if (mesh != null && mesh.isReadable && mats != null) return (mesh, mats);
                    }
                }
            }
        }
        catch (Exception error) { ReportOnce("reading Jackal Patches", error); }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------------------- sockets, pool, labels
    // Each part slot needs a socket on the assembled carrier. They sit on an inactive object so a fitted part's own loose model stays hidden.
    private static void BeforeAttachMods(ContainerCollection containerCollection, ContainerCollectionView collectionView)
    {
        try
        {
            if (instance is null || containerCollection.StringTemplateId != instance.binding.carrier) return;
            var root = collectionView.GameObject;
            (root.GetComponent<CarrierVisualState>() ?? root.AddComponent<CarrierVisualState>()).EnsureHiddenMounts(instance.ourSlots);
        }
        catch (Exception error) { Debug.LogError("[USMCPC] socket setup failed: " + error); }
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<CarrierVisualState>()?.Restore();
    }

    private static void AfterReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<CarrierVisualState>()?.ClearMounts();
    }

    private static string? SlotLabel(Slot slot)
    {
        if (instance is null || slot.ParentItem == null || slot.ParentItem.StringTemplateId != instance.binding.carrier) return null;
        return instance.binding.slotLabels.TryGetValue(slot.ID, out var label) ? label : null;
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

    // ---------------------------------------------------------------------------------------------------------------- data
    public sealed class Bindings
    {
        public string carrier = "", donor = "";
        public Dictionary<string, string> slots = new();
        public Dictionary<string, string> slotLabels = new();
        public Dictionary<string, ItemRow> items = new();
        public Dictionary<string, PartRow> parts = new();
        public Dictionary<string, PatchPoint> patchPoints = new();
        public Dictionary<string, MaterialRow> materials = new();
        public HashSet<string> chestBucklesHiddenBy = new();
        public float[][] bindBase = Array.Empty<float[]>();
        public Dictionary<string, PatchField> patchFields = new();
    }
    /// <summary>Surface height along a patch point's normal (metres from its centre) on a grid in its (wearer's right, up) plane.</summary>
    public sealed class PatchField
    {
        public float cell, halfW, halfH;
        public int nx, ny;
        public float[] heights = Array.Empty<float>();
        public int[] boneIndices = Array.Empty<int>();
        public float[] boneWeights = Array.Empty<float>();
        private int Cell(float a, float b)
        {
            int i = Mathf.FloorToInt((a + halfW) / cell), j = Mathf.FloorToInt((b + halfH) / cell);
            return i < 0 || j < 0 || i >= nx || j >= ny ? -1 : j * nx + i;
        }
        public float At(float a, float b)
        {
            int c = Cell(a, b);
            return c < 0 || heights.Length != nx * ny ? float.MinValue : heights[c];
        }
        public BoneWeight? Weight(float a, float b)
        {
            int c = Cell(Mathf.Clamp(a, -halfW, halfW - cell * 0.5f), Mathf.Clamp(b, -halfH, halfH - cell * 0.5f));
            if (c < 0 || boneIndices.Length != nx * ny * 4 || boneWeights.Length != nx * ny * 4) return null;
            int k = c * 4;
            return new BoneWeight
            {
                boneIndex0 = boneIndices[k], boneIndex1 = boneIndices[k + 1], boneIndex2 = boneIndices[k + 2], boneIndex3 = boneIndices[k + 3],
                weight0 = boneWeights[k], weight1 = boneWeights[k + 1], weight2 = boneWeights[k + 2], weight3 = boneWeights[k + 3],
            };
        }
    }
    public sealed class ItemRow { public string key = "", slot = ""; public string[] parts = Array.Empty<string>(); }
    public sealed class PartRow { public string[] materials = Array.Empty<string>(); public Dictionary<string, LodRow> lods = new(); }
    public sealed class LodRow { public string mesh = ""; }
    public sealed class MaterialRow { public string? albedo, normal, gloss; public float specularness = 1f; }
    public sealed class PatchPoint
    {
        public float[] centre = new float[3], normal = new float[3], up = new float[3], right = new float[3];
        public float back, width, height;
        public int[] boneIndices = new int[4];
        public float[] boneWeights = new float[4];
        public Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    }

    internal sealed class PartData
    {
        public readonly Vector3[] Vertices, Normals;
        public readonly Vector4[] Tangents;
        public readonly Vector2[] Uv;
        public readonly BoneWeight[] Weights;
        public readonly Matrix4x4[] Bindposes;
        public readonly int[][] Triangles;
        public readonly string[] Materials;
        public readonly Bounds Bounds;

        public PartData(Mesh mesh, string[] materials)
        {
            Vertices = mesh.vertices; Normals = mesh.normals; Tangents = mesh.tangents; Uv = mesh.uv; Weights = mesh.boneWeights; Bindposes = mesh.bindposes;
            Triangles = Enumerable.Range(0, mesh.subMeshCount).Select(s => mesh.GetTriangles(s)).ToArray();
            Materials = materials;
            Bounds = mesh.bounds;
            if (Tangents.Length != Vertices.Length) Tangents = new Vector4[Vertices.Length];
        }
    }
}

/// <summary>Combined carrier meshes, shared by every carrier with the same parts and LOD. Unused ones are kept a little while (the inspect
/// window rebuilds on every change), then destroyed.</summary>
internal sealed class MeshCache
{
    private sealed class Entry { public Mesh Mesh = null!; public int Users; public float Idle; }
    private readonly Dictionary<string, Entry> entries = new();
    private const int KeepIdle = 12;

    public Mesh Acquire(List<string> parts, int lod)
    {
        var key = lod + "|" + string.Join(",", parts);
        if (!entries.TryGetValue(key, out var e))
        {
            e = new Entry { Mesh = Plugin.instance!.Build(parts, lod) };
            entries[key] = e;
        }
        e.Users++;
        return e.Mesh;
    }

    public void Release(Mesh mesh)
    {
        var e = entries.Values.FirstOrDefault(x => x.Mesh == mesh);
        if (e == null) return;
        e.Users = Math.Max(0, e.Users - 1);
        if (e.Users == 0) e.Idle = Time.unscaledTime;
        var idle = entries.Where(p => p.Value.Users == 0).OrderBy(p => p.Value.Idle).ToList();
        for (int i = 0; i < idle.Count - KeepIdle; i++)
        {
            Plugin.instance?.Forget(idle[i].Value.Mesh);
            UnityEngine.Object.Destroy(idle[i].Value.Mesh);
            entries.Remove(idle[i].Key);
        }
    }

    public int Count => entries.Count;
}

/// <summary>Records every change made to a pooled object so it can be put back exactly; holds the carrier's mesh uses and its kit patch.</summary>
public sealed class CarrierVisualState : MonoBehaviour
{
    private readonly List<Action> restore = new();
    private readonly Dictionary<UnityEngine.Object, Mesh> used = new();
    private GameObject? mounts;
    private sealed class Shown { public readonly List<GameObject> Objects = new(); public SkinnedMeshRenderer? Skin; }
    private readonly Dictionary<string, Shown> kits = new();
    private SkinnedMeshRenderer? kitSource;
    private float nextBones;
    public string Key { get; internal set; } = "";
    public string Fitted { get; internal set; } = "";
    /// <summary>The chest patch: "tpl@spot", "missing:tpl" or "" (read by the QA probes).</summary>
    public string KitPatch { get; private set; } = "";
    /// <summary>The back patch, same form (0.3.0).</summary>
    public string BackPatch { get; private set; } = "";
    public Action? RefreshVisuals { get; internal set; }

    internal Mesh Use(UnityEngine.Object owner, List<string> parts, int lod)
    {
        var mesh = Plugin.instance!.cache.Acquire(parts, lod);
        if (used.TryGetValue(owner, out var old)) Plugin.instance.cache.Release(old);
        used[owner] = mesh;
        return mesh;
    }

    public void EnsureHiddenMounts(IEnumerable<string> slots)
    {
        if (mounts != null) return;
        mounts = new GameObject("USMCPCHiddenMounts");
        mounts.transform.SetParent(transform, false);
        mounts.SetActive(false);
        foreach (string slot in slots) new GameObject(slot).transform.SetParent(mounts.transform, false);
    }

    /// <summary>Shows the patch for one place ("front" = the chest or phone mount spot, "back"), replacing what was there.</summary>
    internal void ShowKitPatch(string place, Item? patch, string point, SkinnedMeshRenderer skin, MeshFilter loot, Material donor)
    {
        ClearKit(place);
        if (patch == null || Plugin.instance is null || point.Length == 0) return;
        string text;
        var made = Plugin.instance.KitPatch(patch, point, donor);
        if (made == null) text = "missing:" + patch.StringTemplateId;
        else
        {
            var (mesh, mats) = made.Value;
            var shown = new Shown();
            var worn = new GameObject("USMCPC_kitpatch_" + place);
            worn.layer = skin.gameObject.layer;
            worn.transform.SetParent(skin.transform.parent, false);
            var ours = worn.AddComponent<SkinnedMeshRenderer>();
            ours.sharedMesh = mesh; ours.sharedMaterials = mats; ours.localBounds = mesh.bounds; ours.updateWhenOffscreen = true;
            ours.bones = skin.bones; ours.rootBone = skin.rootBone;
            shown.Skin = ours; kitSource = skin;
            shown.Objects.Add(worn);
            var ground = new GameObject("USMCPC_kitpatch_loot_" + place);
            ground.layer = loot.gameObject.layer;
            ground.transform.SetParent(loot.transform, false);
            ground.AddComponent<MeshFilter>().sharedMesh = mesh;
            ground.AddComponent<MeshRenderer>().sharedMaterials = mats;
            shown.Objects.Add(ground);
            kits[place] = shown;
            text = patch.StringTemplateId + "@" + point;
        }
        if (place == "back") BackPatch = text; else KitPatch = text;
    }

    // Unity only really destroys at the end of the frame and the inspect window can take this carrier back from the pool before then, so the
    // old patch is taken out of the carrier straight away.
    private void ClearKit(string place)
    {
        if (kits.TryGetValue(place, out var shown))
        {
            foreach (var go in shown.Objects)
            {
                if (go == null) continue;
                go.SetActive(false);
                go.transform.SetParent(null, false);
                Destroy(go);
            }
            kits.Remove(place);
        }
        if (place == "back") BackPatch = ""; else KitPatch = "";
        if (kits.Count == 0) kitSource = null;
    }

    private void ClearKit()
    {
        foreach (var place in kits.Keys.ToList()) ClearKit(place);
        KitPatch = BackPatch = "";
        kitSource = null;
    }

    // The game turns the rig's renderer off for first person and changes its bones when it is put on; the patches copy it.
    private void LateUpdate()
    {
        if (kits.Count == 0) return;
        bool bones = Time.unscaledTime >= nextBones;
        if (bones) nextBones = Time.unscaledTime + 0.5f;
        foreach (var shown in kits.Values)
        {
            var kitSkin = shown.Skin;
            if (kitSkin == null) continue;
            if (kitSource == null) { kitSkin.enabled = false; continue; }
            kitSkin.enabled = kitSource.enabled;
            kitSkin.forceRenderingOff = kitSource.forceRenderingOff;
            kitSkin.shadowCastingMode = kitSource.shadowCastingMode;
            if (kitSkin.gameObject.layer != kitSource.gameObject.layer) kitSkin.gameObject.layer = kitSource.gameObject.layer;
            if (kitSkin.rootBone != kitSource.rootBone || bones)
            {
                var a = kitSkin.bones; var b = kitSource.bones;
                if (kitSkin.rootBone != kitSource.rootBone || a.Length != b.Length || (a.Length > 0 && a[0] != b[0])) { kitSkin.bones = b; kitSkin.rootBone = kitSource.rootBone; }
            }
        }
    }

    public void Remember(Renderer renderer, MeshFilter? filter)
    {
        var skinned = renderer as SkinnedMeshRenderer;
        var mesh = skinned != null ? skinned.sharedMesh : filter!.sharedMesh;
        var mats = renderer.sharedMaterials;
        var bounds = skinned != null ? skinned.localBounds : default;
        bool offscreen = skinned != null && skinned.updateWhenOffscreen;
        restore.Add(() =>
        {
            if (renderer == null) return;
            renderer.sharedMaterials = mats;
            if (skinned != null) { skinned.sharedMesh = mesh; skinned.localBounds = bounds; skinned.updateWhenOffscreen = offscreen; }
            else if (filter != null) filter.sharedMesh = mesh;
        });
    }

    public void RememberTransform(Transform t)
    {
        var p = t.localPosition; var r = t.localRotation;
        restore.Add(() => { if (t != null) { t.localPosition = p; t.localRotation = r; } });
    }

    public void RememberPivot(PreviewPivot pivot)
    {
        var position = pivot.pivotPosition;
        restore.Add(() => { if (pivot != null) pivot.pivotPosition = position; });
    }

    public void OnRestore(Action action) => restore.Add(action);

    public void Restore()
    {
        RefreshVisuals = null;
        ClearKit();
        for (int i = restore.Count - 1; i >= 0; i--)
        {
            try { restore[i](); } catch (Exception error) { Debug.LogError("[USMCPC] restore step failed: " + error); }
        }
        restore.Clear();
        if (Plugin.instance != null) foreach (var mesh in used.Values) Plugin.instance.cache.Release(mesh);
        used.Clear();
        Key = "";
        Fitted = "";
    }

    public void ClearMounts()
    {
        if (mounts == null) return;
        DestroyImmediate(mounts);
        mounts = null;
    }

    private void OnDestroy() => Restore();
}
