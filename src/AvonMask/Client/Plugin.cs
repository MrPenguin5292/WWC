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

namespace AvonMask.Client;

/// <summary>
/// Avon gas mask (ported Arma model). Since 0.4.0 (Widowz 2026-11-16 "Remove the blower") the bundle carries no PAPR and the PAPR code
/// below stays dormant (its hooks are not even patched); it is kept so the PAPR can come back with a bundle that ships one.
/// The mask is a clone of the stock GP-7 gas mask, so the GP-7 prefab supplies
/// Dress, the head mount, LODs and the inspect pivot; this plugin swaps the meshes on its two LOD renderers, builds the materials from
/// the GP-7's own (same shaders, Avon textures) and fits the loot colliders. The mask has one slot, mod_avon_papr, for the PAPR (a no-filter
/// mask variant is used only when the bundle ships one; the blower-only PAPR of 0.3.0 does not). The PAPR is a clone of the Ops-Core single
/// clamp; loose or on a mask that nobody wears it is drawn as one rigid piece, on a worn mask it becomes a skinned mesh on the wearer, the
/// blower on an anchor under the pelvis that sits out past whatever rig or armor is worn on the left hip.
/// Everything is put back when the object returns to the pool; on any failure the donor look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Avon Gas Mask", "0.4.1")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Avon Mask");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("AvonMask");
#endif
    public const string Id = "local.avonmask.visuals";
    internal static Plugin? instance;
    private AssetBundle bundle = null!;
    private readonly Dictionary<string, ItemBinding> items = new();
    private readonly Dictionary<string, Mesh> meshes = new();
    private readonly Dictionary<(Material, string), Material> materials = new();
    private readonly HashSet<string> errors = new();
    private Dictionary<string, MaterialBinding> materialRows = new();
    internal PaprBinding? papr;
    internal Mesh paprStatic = null!, paprSkinned = null!;
    internal string maskId = "";
    private Harmony? harmony;

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            var path = Path.Combine(Path.GetDirectoryName(Info.Location)!, "avon_mask.bundle");
            bundle = AssetBundle.LoadFromFile(path) ?? throw new FileNotFoundException("Avon mask assets are missing.", path);
            var bindings = JsonConvert.DeserializeObject<Bindings>(Load<TextAsset>("AvonBindings").text)
                ?? throw new InvalidDataException("Bindings could not be read.");
            materialRows = bindings.materials;
            foreach (var pair in bindings.items)
            {
                var row = pair.Value;
                row.key = pair.Key;
                foreach (var name in row.lods.Select(l => l.mesh).Concat(row.noFilterLods))
                {
                    if (meshes.ContainsKey(name)) continue;
                    var mesh = Load<Mesh>(name);
                    if (mesh.subMeshCount != row.materials.Length) throw new InvalidDataException("Submesh count differs: " + name);
                    meshes[name] = mesh;
                }
                foreach (var key in row.materials)
                    if (!materialRows.ContainsKey(key)) throw new InvalidDataException("No material row: " + key);
                items[row.templateId] = row;
                if (pair.Key == "mask") maskId = row.templateId;
            }
            if (items.Count == 0 || maskId.Length == 0) throw new InvalidDataException("No mask binding.");
            if (bindings.papr != null)
            {
                papr = bindings.papr;
                paprStatic = Load<Mesh>(papr.staticMesh);
                paprSkinned = Load<Mesh>(papr.skinnedMesh);
                if (paprSkinned.bindposes.Length != papr.bones.Length) throw new InvalidDataException("PAPR bindposes do not match its bones.");
            }
            harmony = new Harmony(Id);
            var create = AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Single(m => m.Name == nameof(ObjectsFactory.CreateItemAsync)
                && m.ReturnType == typeof(Task<GameObject>) && m.GetParameters()[0].ParameterType == typeof(Item));
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)));
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterReturnToPool)));
            // PAPR-only hooks (mask socket, slot label): not patched at all when the bundle carries no PAPR (0.4.0 on, audit 2026-11-17)
            if (papr != null)
            {
                harmony.Patch(AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.AttachMods)),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeAttachMods)));
                harmony.Patch(AccessTools.Method(typeof(Slot), nameof(Slot.LocalizedName)),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLocalizedSlotName)));
            }
            instance = this;
            // The slot view loads "Slots/<slot name>" for its icon; mod_avon_papr has none, so it gets the equipment one. Put straight into the
            // cache (never patch the generic ResourcesCache.Pop: that broke Pop<Image> and the inspect window for the USMC carrier).
            try
            {
                if (papr != null)
                {
                    var storage = AccessTools.Field(typeof(EFT.Utilities.ResourcesCache), "_storage").GetValue(null) as Dictionary<string, object>;
                    var icon = EFT.Utilities.ResourcesCache.Pop<Sprite>("Slots/mod_equipment");
                    if (storage != null && icon != null) storage["Slots/" + papr.socket] = icon;
                }
            }
            catch (Exception error) { Logger.LogWarning("PAPR slot icon not set (the slot shows without an icon): " + error.Message); }
            Logger.LogInfo($"Avon gas mask ready: {items.Count} item(s), {meshes.Count} meshes, PAPR {(papr != null ? "on" : "off")}.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Logger.LogError("Avon mask visuals unavailable; the GP-7 appearance remains. " + error);
        }
    }

    private T Load<T>(string name) where T : UnityEngine.Object =>
        bundle.LoadAsset<T>(name) ?? throw new InvalidDataException("Missing Avon asset: " + name);

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        if (instance is null) return;
        var id = __0.StringTemplateId;
        if (instance.items.ContainsKey(id) || (instance.papr != null && id == instance.papr.templateId)) __result = instance.ApplyAsync(__result, __0);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item)
    {
        var model = await task;
        if (model == null) return null;
        var state = model.GetComponent<AvonVisualState>() ?? model.AddComponent<AvonVisualState>();
        state.Restore();
        try
        {
            if (items.TryGetValue(item.StringTemplateId, out var binding)) ApplyMask(model, binding, state, HasPapr(item));
            else ApplyPapr(model, state);
        }
        catch (Exception error)
        {
            state.Restore();
            if (errors.Add(item.StringTemplateId)) Logger.LogError($"Avon model failed for {item.StringTemplateId}: {error}");
        }
        return model;
    }

    private bool HasPapr(Item mask) =>
        papr != null && mask is CompoundItem c && c.Containers.OfType<Slot>().Any(s => s.ID == papr.socket && s.ContainedItem != null);

    internal void ApplyMask(GameObject model, ItemBinding binding, AvonVisualState state, bool noFilter)
    {
        // the blower-only PAPR (Widowz 2026-11-16 "Scrap hose") ships no no-filter meshes: the mask keeps its cheek filter
        noFilter = noFilter && binding.noFilterLods.Length > 0;
        var all = model.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < binding.lods.Length; i++)
        {
            var lod = binding.lods[i];
            var target = all.FirstOrDefault(t => t.name == lod.renderer) ?? throw new InvalidDataException("Renderer not found: " + lod.renderer);
            var filter = target.GetComponent<MeshFilter>() ?? throw new InvalidDataException("No MeshFilter on " + lod.renderer);
            var renderer = target.GetComponent<MeshRenderer>() ?? throw new InvalidDataException("No MeshRenderer on " + lod.renderer);
            var donor = state.DonorMaterials(renderer);
            if (donor.Length < binding.materials.Length) throw new InvalidDataException("Donor has fewer materials than the Avon mesh on " + lod.renderer);
            var mesh = meshes[noFilter && binding.noFilterLods.Length > i ? binding.noFilterLods[i] : lod.mesh];
            state.Remember(renderer, filter);
            filter.sharedMesh = mesh;
            renderer.sharedMaterials = binding.materials.Select((key, k) => MakeMaterial(donor[k], key)).ToArray();
            // The GP-7's loot colliders are boxes around its own shape; fit them to the Avon (harness pad reaches further back).
            foreach (var box in target.GetComponents<BoxCollider>())
            {
                state.Remember(box);
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;
            }
        }
        // The mask's LOD group is left exactly as the GP-7 prefab has it. Audit 2026-11-17: up to 0.4.0 it was recalculated to the Avon's
        // bounds (size ~0.33), but BSG gave the GP-7 a size of 2.0, so a dropped or bot-worn Avon dropped out at ~14 m where a stock gas
        // mask stays to ~85 m; the recalculation was also never undone on pool return (fixture runs 2026-11-17: GP-7 back to 2.0 once
        // restored). The Avon is about the GP-7's size, so the donor's values fit it.
        state.Key = binding.key;
        state.Binding = binding;
        state.NoFilter = noFilter;
    }

    /// <summary>The PAPR's own model (single-clamp donor): the clamp renderer shows the PAPR as one piece; a presenter decides each
    /// frame whether it is loose (centred for icons), on a mask (socket layout) or on a worn mask (skinned on the wearer).</summary>
    private void ApplyPapr(GameObject model, AvonVisualState state)
    {
        if (papr == null) return;
        var all = model.GetComponentsInChildren<Transform>(true);
        var target = all.FirstOrDefault(t => t.name == papr.staticRenderer) ?? throw new InvalidDataException("Renderer not found: " + papr.staticRenderer);
        var filter = target.GetComponent<MeshFilter>() ?? throw new InvalidDataException("No MeshFilter on " + papr.staticRenderer);
        var renderer = target.GetComponent<MeshRenderer>() ?? throw new InvalidDataException("No MeshRenderer on " + papr.staticRenderer);
        state.Remember(renderer, filter);
        state.RememberTransform(target);
        // The clamp's LOD group is sized for a 5 cm clamp, so a 0.8 m PAPR set on it would drop out a metre or two away. The presenter
        // holds it at the set's size (KeepLod); the originals are recorded here and put back exactly when the clamp goes back to the pool.
        // Size 1.5 with the clamp's 6.7 % cut-off drops the set at about the distance the mask's own LOD group (0.34 at 1.5 %) drops the mask.
        var lodGroups = model.GetComponentsInChildren<LODGroup>(true);
        foreach (var g in lodGroups)
        {
            var group = g; var size = g.size; var reference = g.localReferencePoint;
            state.OnRestore(() => { if (group != null) { group.size = size; group.localReferencePoint = reference; } });
        }
        filter.sharedMesh = paprStatic;
        renderer.sharedMaterials = PaprMaterials(renderer.sharedMaterial);
        state.Key = "papr";
        var presenter = model.GetComponent<AvonPaprPresenter>() ?? model.AddComponent<AvonPaprPresenter>();
        presenter.Setup(target, renderer, lodGroups);
        state.OnRestore(() => { if (presenter != null) presenter.Clear(); });
    }

    /// <summary>PAPR materials: built from the GP-7 body material when one is loaded (same shader as the mask), else from the clamp's.</summary>
    internal Material[] PaprMaterials(Material fallback)
    {
        var donor = gp7Body != null ? gp7Body : fallback;
        return papr!.materials.Select(key => MakeMaterial(donor, key)).ToArray();
    }

    internal static Material? gp7Body;

    internal Material MakeMaterial(Material donor, string key)
    {
        if (materials.TryGetValue((donor, key), out var found)) return found;
        var row = materialRows[key];
        // Same shader and numbers as the GP-7 material it replaces; textures were calibrated on the GP-7's (tools/make_tex.py).
        var material = new Material(donor) { name = "Avon_" + key };
        foreach (var pair in row.textures)
        {
            if (!material.HasProperty(pair.Key)) continue;
            material.SetTexture(pair.Key, string.IsNullOrEmpty(pair.Value) ? null : Load<Texture2D>(pair.Value!));
            material.SetTextureScale(pair.Key, Vector2.one);
            material.SetTextureOffset(pair.Key, Vector2.zero);
        }
        materials.Add((donor, key), material);
        if (key == "avon_mask" && gp7Body == null && donor.shader != null && donor.shader.name.StartsWith("p0/Reflective")) gp7Body = donor;
        return material;
    }

    // The mask's PAPR socket is made on the assembled mask before the game looks for it by name, and the mask follows the slot
    // (no-filter variant while a PAPR is fitted, also when it is fitted or removed live).
    private static void BeforeAttachMods(ContainerCollection containerCollection, ContainerCollectionView collectionView)
    {
        try
        {
            if (instance?.papr is not { } p || containerCollection.StringTemplateId != instance.maskId) return;
            var root = collectionView.GameObject;
            var state = root.GetComponent<AvonVisualState>() ?? root.AddComponent<AvonVisualState>();
            // identity turn (the mask's own axes); the presenter pins the one-piece set to this socket, whatever turn the game gives the PAPR's root
            state.EnsureSocket(p.socket, new Vector3(p.socketPos[0], p.socketPos[1], p.socketPos[2]), Quaternion.identity);
            var slot = containerCollection.Containers.OfType<Slot>().FirstOrDefault(s => s.ID == p.socket);
            if (slot == null || state.FollowingSlot) return;
            Action<Item> changed = _ =>
            {
                try
                {
                    var want = slot.ContainedItem != null && state?.Binding != null && state.Binding.noFilterLods.Length > 0;
                    if (state != null && state.Binding != null && state.NoFilter != want)
                    {
                        state.Restore();
                        instance.ApplyMask(root, state.Binding, state, want);
                    }
                }
                catch (Exception error) { Debug.LogError("[Avon] filter swap failed: " + error); }
            };
            slot.OnAddOrRemoveItem += changed;
            state.Unsubscribe(() => slot.OnAddOrRemoveItem -= changed);
            state.FollowingSlot = true;
        }
        catch (Exception error) { Debug.LogError("[Avon] socket setup failed: " + error); }
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<AvonVisualState>()?.Restore();
    }

    private static void AfterReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<AvonVisualState>()?.ClearSockets();
    }

    private static void AfterLocalizedSlotName(Slot __instance, ref string __result)
    {
        if (instance?.papr != null && __instance.ID == instance.papr.socket) __result = "PAPR";
    }

    public sealed class Bindings
    {
        public Dictionary<string, ItemBinding> items = new();
        public Dictionary<string, MaterialBinding> materials = new();
        public PaprBinding? papr;
    }
    public sealed class LodBinding { public string renderer = "", mesh = ""; }
    public sealed class ItemBinding
    {
        public string key = "", templateId = "";
        public LodBinding[] lods = Array.Empty<LodBinding>();
        public string[] noFilterLods = Array.Empty<string>();
        public string[] materials = Array.Empty<string>();
    }
    public sealed class MaterialBinding { public string key = ""; public Dictionary<string, string?> textures = new(); }
    public sealed class PaprBinding
    {
        public string templateId = "", staticMesh = "", skinnedMesh = "", staticRenderer = "", socket = "", anchorParent = "";
        public string[] materials = Array.Empty<string>(), bones = Array.Empty<string>();
        public float[] socketPos = new float[3], anchorBackLocal = new float[3];
        public float defaultDepth = 0.04f, maxDepth = 0.12f;
        public Dictionary<string, float> depth = new();
    }
}

/// <summary>
/// Shows the PAPR the right way for where its model is: loose (stash icon, inspect, on the ground) = the one-piece mesh centred on the
/// clamp's own pivot; on a mask's socket with nobody wearing it = the one-piece mesh in its socket layout; on a mask worn on a head =
/// a skinned mesh on the wearer, the blower on a pack anchor under the pelvis (static piece hidden). The anchor sits out past the worn
/// armor/rig by the left-hip depth measured for it. Never throws into the game loop: a failure falls back to the static piece.
/// </summary>
public sealed class AvonPaprPresenter : MonoBehaviour
{
    private Transform target = null!;
    private MeshRenderer staticRenderer = null!;
    private Vector3 baseLocalPos;
    private Quaternion baseLocalRot = Quaternion.identity;
    private GameObject? skinnedObject, anchor;
    private Transform? wornHead;
    private object? body;
    private float nextDepthCheck;
    private bool failed;
    private string lastVests = "";

    // The one-piece mesh is laid out relative to the mask's socket in the clamp renderer's own Rx(-90) frame (mesh = (X, -Z, Y)).
    private static readonly Quaternion RendererTurn = Quaternion.Euler(-90f, 0f, 0f);

    public void Setup(Transform rendererTransform, MeshRenderer renderer, LODGroup[] lods)
    {
        target = rendererTransform; staticRenderer = renderer; failed = false; lodGroups = lods;
        baseLocalPos = rendererTransform.localPosition; baseLocalRot = rendererTransform.localRotation;
        enabled = true;
        Place();
    }

    public void Clear()
    {
        DestroySkinned();
        if (target != null) { target.localPosition = baseLocalPos; target.localRotation = baseLocalRot; }
        if (staticRenderer != null) staticRenderer.enabled = true;
        enabled = false;
    }

    private static Transform? SocketAbove(Transform? t)
    {
        var name = Plugin.instance?.papr?.socket;
        for (; t != null; t = t.parent)
            if (t.name == name) return t;
        return null;
    }

    private static bool UnderSocket(Transform? t) => SocketAbove(t) != null;
    internal static bool IsUnderSocket(Transform t) => UnderSocket(t);

    // LOD groups on the PAPR's own model, held at the PAPR set's size (the originals are put back by the visual state on pool return)
    private LODGroup[] lodGroups = Array.Empty<LODGroup>();
    private const float SetLodSize = 1.5f;

    private void KeepLod()
    {
        foreach (var g in lodGroups)
        {
            if (g == null || Mathf.Approximately(g.size, SetLodSize)) continue;
            g.localReferencePoint = g.transform.InverseTransformPoint(target.TransformPoint(Plugin.instance!.paprStatic.bounds.center));
            g.size = SetLodSize;
        }
    }

    /// <summary>On a mask: the set in its worn layout, pinned to the socket. Run 6 (2026-11-16): the game hangs the PAPR's model root
    /// under the socket turned Rx(+90) (root local (90,0,0)), which cancelled the clamp renderer's Rx(-90) and laid the set on its back
    /// (hose and blower out behind the mask at mask height). Pinning the renderer's world pose works whatever the root's turn is.</summary>
    private void PinToSocket(Transform socket)
    {
        target.SetPositionAndRotation(socket.position, socket.rotation * RendererTurn);
    }

    private static Transform? HeadAbove(Transform t)
    {
        for (int i = 0; i < 6 && t != null; i++, t = t.parent!)
            if (t.name == "Base HumanHead") return t;
        return null;
    }

    private void OnTransformParentChanged() => Place();

    private void Place()
    {
        if (target == null) return;
        var socket = SocketAbove(transform.parent);
        if (socket == null)
        {
            // loose: centre the whole set on the clamp's pivot so stash icons and the inspect view frame it (icons draw before LateUpdate)
            target.localRotation = baseLocalRot;
            var c = Plugin.instance!.paprStatic.bounds.center;
            target.localPosition = baseLocalPos - target.localRotation * c;
            DestroySkinned();
            staticRenderer.enabled = true;
        }
        else PinToSocket(socket);
        KeepLod();
    }

    private void LateUpdate()
    {
        try
        {
            if (failed || Plugin.instance?.papr is not { } p) return;
            var socket = SocketAbove(transform.parent);
            var head = socket != null ? HeadAbove(transform) : null;
            if (head == null)
            {
                if (skinnedObject != null) { DestroySkinned(); staticRenderer.enabled = true; }
                if (socket != null) PinToSocket(socket);   // a mask nobody wears (ground, inspect, displays): keep the set hanging right
                KeepLod();
                return;
            }
            if (skinnedObject == null || wornHead != head) Build(head, p);
            if (Time.time >= nextDepthCheck) { nextDepthCheck = Time.time + 0.5f; UpdateDepth(p); }
        }
        catch (Exception error)
        {
            failed = true; DestroySkinned(); if (staticRenderer != null) staticRenderer.enabled = true;
            Debug.LogError("[Avon] PAPR on the wearer failed; showing the one-piece model. " + error);
        }
    }

    private void Build(Transform head, Plugin.PaprBinding p)
    {
        DestroySkinned();
        // the skeleton above the head, by name (Root_Joint/Base HumanPelvis/Base HumanSpine1/2/3/Base HumanNeck/Base HumanHead)
        var chain = new Dictionary<string, Transform>();
        for (var t = head; t != null; t = t.parent) { if (!chain.ContainsKey(t.name)) chain[t.name] = t; if (t.name == "Root_Joint") break; }
        var bones = new Transform[p.bones.Length];
        for (int i = 0; i < p.bones.Length - 1; i++)
            bones[i] = chain.TryGetValue(p.bones[i], out var b) ? b : throw new InvalidOperationException("bone not above the head: " + p.bones[i]);
        if (!chain.TryGetValue(p.anchorParent, out var anchorParent)) throw new InvalidOperationException("bone not above the head: " + p.anchorParent);
        anchor = new GameObject("AvonPackAnchor");
        anchor.transform.SetParent(anchorParent, false);
        bones[bones.Length - 1] = anchor.transform;
        // Run 4 (2026-11-16): a Unity SkinnedMeshRenderer with the game's p0/Reflective mask material skinned correctly (bounds right) but
        // never drew. The hose is now skinned on the CPU into a plain MeshRenderer, the kind the mask itself uses, right before it renders.
        skinnedObject = new GameObject("AvonPAPR_Worn");
        skinnedObject.transform.SetParent(transform, false);
        var maskRenderer = transform.parent != null && transform.parent.parent != null
            ? transform.parent.parent.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name.EndsWith("_LOD0_base", StringComparison.Ordinal))
            : null;
        skinnedObject.layer = maskRenderer != null ? maskRenderer.gameObject.layer : staticRenderer.gameObject.layer;
        var skin = skinnedObject.AddComponent<AvonPaprSkin>();
        skin.Setup(bones, Plugin.instance!.paprSkinned, staticRenderer.sharedMaterials, maskRenderer);
        staticRenderer.enabled = false;
        wornHead = head;
        body = FindBody(head);
        lastVests = "";
        UpdateDepth(p);
    }

    private static object? FindBody(Transform from)
    {
        for (var t = from; t != null; t = t.parent)
        {
            var b = t.GetComponent<PlayerBody>();
            if (b != null) return b;
        }
        return null;
    }

    /// <summary>Pack anchor out behind the worn armor/rig: the larger of the two measured depths (unknown vests use defaultDepth).</summary>
    private void UpdateDepth(Plugin.PaprBinding p)
    {
        if (anchor == null) return;
        float depth = 0f; var seen = new List<string>();
        try
        {
            if (body is PlayerBody pb && pb.Equipment != null)
                foreach (var slot in new[] { EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest })
                {
                    var it = pb.Equipment.GetSlot(slot).ContainedItem;
                    if (it == null) continue;
                    seen.Add(it.StringTemplateId);
                    depth = Math.Max(depth, p.depth.TryGetValue(it.StringTemplateId, out var d) ? d : p.defaultDepth);
                }
        }
        catch { /* equipment not readable here (menu preview): bare-back placement */ }
        depth = Mathf.Clamp(depth, 0f, p.maxDepth);
        var key = string.Join(",", seen);
        anchor.transform.localPosition = new Vector3(p.anchorBackLocal[0], p.anchorBackLocal[1], p.anchorBackLocal[2]) * depth;
        anchor.transform.localRotation = Quaternion.identity;
        if (key != lastVests) { lastVests = key; Debug.Log($"[Avon] PAPR pack {depth * 100f:F1} cm out behind [{key}]"); }
    }

    private void DestroySkinned()
    {
        if (skinnedObject != null) Destroy(skinnedObject);
        if (anchor != null) Destroy(anchor);
        skinnedObject = null; anchor = null; wornHead = null; body = null;
    }

    private void OnDestroy() => DestroySkinned();
}

/// <summary>
/// Linear-blend skinning on the CPU into a dynamic mesh drawn by a MeshRenderer. Vertices of the source mesh are in the skeleton's rest
/// frame; each bone's matrix is (this object's world-to-local) * bone local-to-world * bindpose, so the result lands in this object's
/// space whatever it is parented to. Skinned once per frame from OnWillRenderObject (after animation, IK and procedural layers ran);
/// while the game shows it as shadow only (first person) OnWillRenderObject never fires, so LateUpdate skins it every other frame.
/// Visibility and shadow mode follow the mask's own LOD0 renderer, so the PAPR hides whenever the game hides the mask (first person).
/// </summary>
public sealed class AvonPaprSkin : MonoBehaviour
{
    private Transform[] bones = Array.Empty<Transform>();
    private Matrix4x4[] bind = Array.Empty<Matrix4x4>(), mats = Array.Empty<Matrix4x4>();
    private Vector3[] baseV = Array.Empty<Vector3>(), baseN = Array.Empty<Vector3>(), outV = Array.Empty<Vector3>(), outN = Array.Empty<Vector3>();
    private Vector4[] baseT = Array.Empty<Vector4>(), outT = Array.Empty<Vector4>();
    private BoneWeight[] weights = Array.Empty<BoneWeight>();
    private Mesh mesh = null!;
    private MeshRenderer meshRenderer = null!;
    private Renderer? follow;
    private int lastFrame = -1;
    public int SkinnedFrames { get; private set; }

    public void Setup(Transform[] boneList, Mesh source, Material[] materials, Renderer? followRenderer)
    {
        bones = boneList; follow = followRenderer;
        bind = source.bindposes; weights = source.boneWeights;
        baseV = source.vertices; baseN = source.normals;
        mesh = new Mesh { name = "AvonPAPR_dynamic", indexFormat = source.indexFormat };
        mesh.MarkDynamic();
        mesh.SetVertices(baseV); mesh.SetNormals(baseN); mesh.SetUVs(0, new List<Vector2>(source.uv));
        mesh.subMeshCount = source.subMeshCount;
        for (int i = 0; i < source.subMeshCount; i++) mesh.SetTriangles(source.GetTriangles(i), i, false);
        mesh.RecalculateTangents();
        baseT = mesh.tangents;
        outV = new Vector3[baseV.Length]; outN = new Vector3[baseV.Length]; outT = new Vector4[baseV.Length];
        mats = new Matrix4x4[bones.Length];
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterials = materials;
        Skin();
        Follow();
    }

    private void LateUpdate()
    {
        Follow();
        // First person: the game draws the wearer's head gear as shadow only, and Unity never calls OnWillRenderObject for a
        // shadows-only renderer (run 5: one skin in 8 s), so the hose's shadow froze in the pose it was built in. Skin it here instead,
        // every other frame (a soft shadow does not need more).
        if (meshRenderer != null && meshRenderer.enabled && meshRenderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
            && Time.frameCount - lastFrame >= 2)
        {
            try { Skin(); } catch (Exception error) { enabled = false; Debug.LogError("[Avon] PAPR skinning stopped: " + error.Message); }
        }
    }

    private void Follow()
    {
        if (meshRenderer == null) return;
        if (follow != null)
        {
            bool shown = follow.enabled && follow.gameObject.activeInHierarchy && !follow.forceRenderingOff;
            if (meshRenderer.enabled != shown) meshRenderer.enabled = shown;
            if (meshRenderer.shadowCastingMode != follow.shadowCastingMode) meshRenderer.shadowCastingMode = follow.shadowCastingMode;
        }
    }

    private void OnWillRenderObject()
    {
        if (lastFrame == Time.frameCount) return;
        try { Skin(); } catch (Exception error) { enabled = false; Debug.LogError("[Avon] PAPR skinning stopped: " + error.Message); }
    }

    private void Skin()
    {
        lastFrame = Time.frameCount;
        var toLocal = transform.worldToLocalMatrix;
        for (int b = 0; b < bones.Length; b++)
            mats[b] = bones[b] != null ? toLocal * bones[b].localToWorldMatrix * bind[b] : Matrix4x4.zero;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        for (int i = 0; i < baseV.Length; i++)
        {
            var w = weights[i];
            Vector3 v = Vector3.zero, n = Vector3.zero, t = Vector3.zero;
            Add(ref v, ref n, ref t, w.boneIndex0, w.weight0, i);
            if (w.weight1 > 0f) Add(ref v, ref n, ref t, w.boneIndex1, w.weight1, i);
            if (w.weight2 > 0f) Add(ref v, ref n, ref t, w.boneIndex2, w.weight2, i);
            if (w.weight3 > 0f) Add(ref v, ref n, ref t, w.boneIndex3, w.weight3, i);
            outV[i] = v; outN[i] = n.normalized; outT[i] = new Vector4(t.x, t.y, t.z, baseT.Length > i ? baseT[i].w : 1f);
            min = Vector3.Min(min, v); max = Vector3.Max(max, v);
        }
        mesh.SetVertices(outV); mesh.SetNormals(outN); mesh.SetTangents(outT);
        var bounds = new Bounds((min + max) * 0.5f, max - min);
        bounds.Expand(0.4f);                 // room for the next frame's movement before this frame's skin is replaced
        mesh.bounds = bounds;
        SkinnedFrames++;
    }

    private void Add(ref Vector3 v, ref Vector3 n, ref Vector3 t, int bone, float weight, int i)
    {
        var m = mats[bone];
        v += m.MultiplyPoint3x4(baseV[i]) * weight;
        n += m.MultiplyVector(baseN[i]) * weight;
        if (baseT.Length > i) t += m.MultiplyVector(baseT[i]) * weight;
    }

    private void OnDestroy() { if (mesh != null) Destroy(mesh); }
}

/// <summary>Records every change made to a pooled object so it can be put back exactly.</summary>
public sealed class AvonVisualState : MonoBehaviour
{
    private readonly List<Action> restore = new();
    private readonly List<Action> unsubscribe = new();
    private readonly List<GameObject> socketObjects = new();
    private readonly Dictionary<MeshRenderer, Material[]> donorMaterials = new();
    public string Key { get; internal set; } = "";
    internal Plugin.ItemBinding? Binding { get; set; }
    internal bool NoFilter { get; set; }
    internal bool FollowingSlot { get; set; }

    /// <summary>The renderer's own materials before any Avon swap (a live re-apply must build from these, not from Avon copies).</summary>
    public Material[] DonorMaterials(MeshRenderer r)
    {
        if (!donorMaterials.TryGetValue(r, out var m)) donorMaterials[r] = m = r.sharedMaterials;
        return m;
    }

    public void Remember(MeshRenderer renderer, MeshFilter filter)
    {
        var mesh = filter.sharedMesh;
        var mats = renderer.sharedMaterials;
        var enabledBefore = renderer.enabled;
        restore.Add(() =>
        {
            if (renderer != null) { renderer.sharedMaterials = mats; renderer.enabled = enabledBefore; }
            if (filter != null) filter.sharedMesh = mesh;
        });
    }

    public void Remember(BoxCollider box)
    {
        var center = box.center; var size = box.size;
        restore.Add(() => { if (box != null) { box.center = center; box.size = size; } });
    }

    public void RememberTransform(Transform t)
    {
        var p = t.localPosition; var r = t.localRotation;
        restore.Add(() => { if (t != null) { t.localPosition = p; t.localRotation = r; } });
    }

    public void OnRestore(Action action) => restore.Add(action);
    public void Unsubscribe(Action action) => unsubscribe.Add(action);

    public void EnsureSocket(string name, Vector3 pos, Quaternion rot)
    {
        if (transform.Find(name) != null) return;
        var socket = new GameObject(name);
        socket.transform.SetParent(transform, false);
        socket.transform.localPosition = pos;
        socket.transform.localRotation = rot;
        socketObjects.Add(socket);
    }

    public void Restore()
    {
        for (int i = restore.Count - 1; i >= 0; i--) { try { restore[i](); } catch { } }
        restore.Clear();
        Key = "";
    }

    public void ClearSockets()
    {
        foreach (var action in unsubscribe) { try { action(); } catch { } }
        unsubscribe.Clear();
        FollowingSlot = false;
        Binding = null;
        donorMaterials.Clear();
        foreach (var socket in socketObjects) if (socket != null) DestroyImmediate(socket);
        socketObjects.Clear();
    }

    private void OnDestroy()
    {
        foreach (var action in unsubscribe) { try { action(); } catch { } }
        unsubscribe.Clear();
        Restore();
    }
}
