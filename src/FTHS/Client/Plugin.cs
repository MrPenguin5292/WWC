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

namespace FTHS.Client;

/// <summary>
/// USMC FTHS helmets (ballistic + bump) and their parts. Every item is a clone of a native template, so the donor prefab
/// supplies Dress, LODs, sockets and behaviour. This plugin swaps meshes and materials on the donor's renderers, adds the
/// sockets the helmets' new slots need, and moves the strobe and battery sockets onto whichever cover is fitted.
/// All of it is undone when the object goes back to the pool; on any failure the donor look stays.
/// Same route as the Mu-13 helmet (mods/MTFHelmet).
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "FTHS Helmets", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC FTHS");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("FTHS");
#endif
    public const string Id = "local.fths.visuals";
    private const string CoverSlot = "mod_equipment_005";
    private static Plugin instance = null!;
    private AssetBundle bundle = null!;
    private readonly Dictionary<string, ItemBinding> items = new();
    private readonly Dictionary<string, Mesh> meshes = new();
    private readonly Dictionary<(Material, string), Material> materials = new();
    private readonly HashSet<string> errors = new();
    private Dictionary<string, MaterialBinding> materialRows = new();
    private Dictionary<string, float[]> sockets = new();
    private Dictionary<string, SocketMove> socketMoves = new();
    private Dictionary<string, Dictionary<string, Pose>> coverMoves = new();
    private readonly HashSet<string> helmetIds = new();
    private string ballisticId = "";
    private readonly Dictionary<string, string> coverKeys = new();   // cover template id -> cover key
    private Mesh emptyMesh = null!;
    private Harmony? harmony;

    private static readonly Dictionary<string, string> SlotNames = new()
    {
        ["mod_equipment_003"] = "STROBE", ["mod_equipment_004"] = "BATTERY PACK", ["mod_equipment_005"] = "COVER",
        ["mod_equipment_006"] = "HELMET LIGHT", ["mod_equipment_007"] = "CHINSTRAP",
    };

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            var path = Path.Combine(Path.GetDirectoryName(Info.Location)!, "fths_helmets.bundle");
            bundle = AssetBundle.LoadFromFile(path) ?? throw new FileNotFoundException("FTHS helmet assets are missing.", path);
            var bindings = JsonConvert.DeserializeObject<Bindings>(Load<TextAsset>("FTHSBindings").text)
                ?? throw new InvalidDataException("Bindings could not be read.");
            materialRows = bindings.materials;
            sockets = bindings.sockets;
            socketMoves = bindings.socketMoves;
            coverMoves = bindings.coverMoves;
            foreach (var pair in bindings.items)
            {
                var row = pair.Value;
                row.key = pair.Key;
                if (!meshes.ContainsKey(row.mesh))
                {
                    var mesh = Load<Mesh>(row.mesh);
                    if (mesh.subMeshCount != row.materials.Length) throw new InvalidDataException("Submesh count differs: " + row.mesh);
                    meshes[row.mesh] = mesh;
                }
                items[row.templateId] = row;
                if (pair.Key.StartsWith("helmet_")) helmetIds.Add(row.templateId);
                if (pair.Key == "helmet_bal") ballisticId = row.templateId;
                if (pair.Key.StartsWith("cover_")) coverKeys[row.templateId] = pair.Key;
            }
            if (helmetIds.Count != 2 || ballisticId.Length == 0) throw new InvalidDataException("Helmet bindings are missing.");
            emptyMesh = new Mesh { name = "FTHS_empty", hideFlags = HideFlags.HideAndDontSave };
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
            Logger.LogInfo($"FTHS helmets ready: {items.Count} items, {sockets.Count} sockets, {coverKeys.Count} covers.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Logger.LogError("FTHS helmet visuals unavailable; the native donor appearance remains. " + error);
        }
    }

    private T Load<T>(string name) where T : UnityEngine.Object =>
        bundle.LoadAsset<T>(name) ?? throw new InvalidDataException("Missing FTHS asset: " + name);

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        if (instance is not null && instance.items.ContainsKey(__0.StringTemplateId)) __result = instance.ApplyAsync(__result, __0);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item)
    {
        var model = await task;
        if (model == null) return null;
        var state = model.GetComponent<FTHSVisualState>() ?? model.AddComponent<FTHSVisualState>();
        state.Restore();
        try { Apply(model, items[item.StringTemplateId], state); }
        catch (Exception error)
        {
            state.Restore();
            if (errors.Add(item.StringTemplateId)) Logger.LogError($"FTHS model failed for {item.StringTemplateId}: {error}");
        }
        return model;
    }

    private void Apply(GameObject model, ItemBinding binding, FTHSVisualState state)
    {
        var all = model.GetComponentsInChildren<Transform>(true);
        var target = all.FirstOrDefault(t => t.name == binding.renderer) ?? throw new InvalidDataException("Renderer not found: " + binding.renderer);
        var filter = target.GetComponent<MeshFilter>() ?? throw new InvalidDataException("No MeshFilter on " + binding.renderer);
        var renderer = target.GetComponent<MeshRenderer>() ?? throw new InvalidDataException("No MeshRenderer on " + binding.renderer);
        var donor = renderer.sharedMaterial ?? throw new InvalidDataException("No donor material on " + binding.renderer);
        var mesh = meshes[binding.mesh];
        var mats = binding.materials.Select(key => MakeMaterial(donor, key)).ToArray();
        void Swap(Transform t)
        {
            var f = t.GetComponent<MeshFilter>();
            var r = t.GetComponent<MeshRenderer>();
            if (f == null || r == null) return;
            state.Remember(r, f);
            f.sharedMesh = mesh;
            r.sharedMaterials = mats;
            // FAST-family donors carry a CustomItem that writes its own base/custom mesh back when the wearer's face gear changes.
            foreach (var custom in t.GetComponents<CustomItem>())
            {
                state.Remember(custom);
                custom._data = new CustomItemData { Base = mesh, Custom = mesh };
            }
        }
        Swap(target);
        foreach (var extraName in binding.extra)
            foreach (var t in all.Where(t => t.name == extraName)) Swap(t);
        foreach (var hideName in binding.hide)
            foreach (var t in all.Where(t => t.name == hideName))
            {
                var f = t.GetComponent<MeshFilter>();
                var r = t.GetComponent<MeshRenderer>();
                if (f == null || r == null) continue;
                state.Remember(r, f);
                f.sharedMesh = emptyMesh;
            }
        // A loose battery shows its helmet-side (black) face to the icon and preview cameras; turn the loose model around and put it
        // back when it is attached under its socket. Icons draw before any LateUpdate, so the loose turn is set here at creation.
        if (binding.looseYaw != 0f)
        {
            var turn = model.GetComponent<FTHSLooseTurn>() ?? model.AddComponent<FTHSLooseTurn>();
            var targets = new[] { target }.Concat(binding.extra.SelectMany(n => all.Where(t => t.name == n))).Distinct().ToArray();
            state.RememberRotation(targets);
            turn.Setup(targets, binding.looseYaw);
            state.OnRestore(() => { if (turn != null) turn.Clear(); });
        }
        foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
        state.Key = binding.key;
    }

    private Material MakeMaterial(Material donor, string key)
    {
        if (materials.TryGetValue((donor, key), out var found)) return found;
        var row = materialRows[key];
        var material = new Material(donor) { name = "FTHS_" + key };
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
        // Same numbers on every part, whichever donor it came from (Mu-13 port, taken from Tarkov's own high-cut helmets).
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        if (material.HasProperty("_Specularness")) material.SetFloat("_Specularness", 1f);
        if (material.HasProperty("_Glossness")) material.SetFloat("_Glossness", 1f);
        if (material.HasProperty("_SpecColor")) material.SetColor("_SpecColor", new Color(.9f, .9f, .88f, 1f));
        if (material.HasProperty("_ReflectColor")) material.SetColor("_ReflectColor", new Color(.2f, .2f, .2f, .5f));
        if (material.HasProperty("_SpecVals")) material.SetVector("_SpecVals", new Vector4(1.1f, 2f, 0f, 0f));
        if (material.HasProperty("_DefVals")) material.SetVector("_DefVals", new Vector4(.8f, .7f, 0f, 0f));
        if (material.HasProperty("_HasTint")) material.SetFloat("_HasTint", 0f);
        materials.Add((donor, key), material);
        return material;
    }

    // Slots are created on the assembled helmet before the game looks for them by name.
    private static void BeforeAttachMods(ContainerCollection containerCollection, ContainerCollectionView collectionView)
    {
        try
        {
            if (instance is null || !instance.helmetIds.Contains(containerCollection.StringTemplateId)) return;
            var root = collectionView.GameObject;
            var state = root.GetComponent<FTHSVisualState>() ?? root.AddComponent<FTHSVisualState>();
            state.Owner = containerCollection;
            state.EnsureSockets(instance.sockets);
            state.MoveSockets(instance.socketMoves);
            if (containerCollection.StringTemplateId == instance.ballisticId) instance.FollowCover(containerCollection, state);
        }
        catch (Exception error) { Debug.LogError("[FTHS] socket setup failed: " + error); }
    }

    /// <summary>The strobe and battery sit on the cover's surface, so their sockets follow whichever cover is fitted (also live,
    /// when a cover is added or removed on a built helmet).</summary>
    private void FollowCover(ContainerCollection helmet, FTHSVisualState state)
    {
        var slot = helmet.Containers.OfType<Slot>().FirstOrDefault(s => s.ID == CoverSlot);
        if (slot == null) return;
        void Place()
        {
            try
            {
                Dictionary<string, Pose>? moves = null;
                var fitted = slot.ContainedItem;
                if (fitted != null && coverKeys.TryGetValue(fitted.StringTemplateId, out var key)) coverMoves.TryGetValue(key, out moves);
                state.PlaceCoverSockets(sockets, moves);
            }
            catch (Exception error) { Debug.LogError("[FTHS] cover placement failed: " + error); }
        }
        Place();
        if (state.FollowingCover) return;
        Action<Item> changed = _ => Place();
        slot.OnAddOrRemoveItem += changed;
        state.Unsubscribe(() => slot.OnAddOrRemoveItem -= changed);
        state.FollowingCover = true;
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<FTHSVisualState>()?.Restore();
    }

    private static void AfterReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<FTHSVisualState>()?.ClearSockets();
    }

    private static string? SlotLabel(Slot slot) =>
        instance is not null && slot.ParentItem != null && instance.helmetIds.Contains(slot.ParentItem.StringTemplateId) && SlotNames.TryGetValue(slot.ID, out var name) ? name : null;

    private static void AfterLocalizedSlotName(Slot __instance, ref string __result)
    {
        var label = SlotLabel(__instance);
        if (label != null) __result = label;
    }

    private static void AfterShowSlot(object __instance, Slot __0)
    {
        var label = SlotLabel(__0);
        if (label == null) return;
        var field = AccessTools.Field(__instance.GetType(), "_slotName");
        var text = field?.GetValue(__instance);
        if (text != null) AccessTools.Property(text.GetType(), "text").SetValue(text, label, null);
    }

    public sealed class Bindings
    {
        public Dictionary<string, ItemBinding> items = new();
        public Dictionary<string, float[]> sockets = new();
        public Dictionary<string, MaterialBinding> materials = new();
        public Dictionary<string, SocketMove> socketMoves = new();
        public Dictionary<string, Dictionary<string, Pose>> coverMoves = new();
    }
    public sealed class SocketMove { public float[] offset = new float[3]; public float tiltDeg; }
    public sealed class Pose { public float[] pos = new float[3]; public float[] rot = { 0f, 0f, 0f, 1f }; }
    public sealed class ItemBinding
    {
        public string key = "", templateId = "", mesh = "", renderer = "";
        public string? socket;
        public float looseYaw;
        public string[] materials = Array.Empty<string>(), hide = Array.Empty<string>(), extra = Array.Empty<string>();
    }
    public sealed class MaterialBinding { public string key = ""; public string? albedo, normal, gloss; }
}

/// <summary>Turns a part's renderers about the item's up axis while the part is loose (stash icon, preview, ground) and back
/// when it sits under a helmet socket (any ancestor named mod_*).</summary>
public sealed class FTHSLooseTurn : MonoBehaviour
{
    private Transform[] targets = Array.Empty<Transform>();
    private Quaternion[] attached = Array.Empty<Quaternion>();
    private float yaw;

    public void Setup(Transform[] renderers, float yawDeg)
    {
        targets = renderers; yaw = yawDeg;
        attached = renderers.Select(t => t.localRotation).ToArray();
        enabled = true;
        Apply();
    }

    public void Clear() { targets = Array.Empty<Transform>(); attached = Array.Empty<Quaternion>(); enabled = false; }

    private static bool UnderSocket(Transform? t)
    {
        for (; t != null; t = t.parent)
            if (t.name.StartsWith("mod_", StringComparison.Ordinal)) return true;
        return false;
    }

    private void Apply()
    {
        bool onHelmet = UnderSocket(transform.parent);
        var turn = Quaternion.AngleAxis(yaw, Vector3.up);
        for (int i = 0; i < targets.Length; i++)
            if (targets[i] != null) targets[i].localRotation = onHelmet ? attached[i] : turn * attached[i];
    }

    private void OnTransformParentChanged() { if (targets.Length > 0) Apply(); }
}

/// <summary>Records every change made to a pooled object so it can be put back exactly.</summary>
public sealed class FTHSVisualState : MonoBehaviour
{
    private static readonly Quaternion SocketBase = Quaternion.Euler(-90f, 0f, 0f);
    private readonly List<Action> restore = new();
    private readonly List<GameObject> socketObjects = new();
    private readonly List<Action> movedSockets = new();
    private readonly List<Action> unsubscribe = new();
    public string Key { get; internal set; } = "";
    public bool FollowingCover { get; internal set; }
    /// <summary>The helmet item this assembled model shows (diagnostics: lets a QA probe change exactly that item).</summary>
    public ContainerCollection? Owner { get; internal set; }

    public void Remember(MeshRenderer renderer, MeshFilter filter)
    {
        var mesh = filter.sharedMesh;
        var mats = renderer.sharedMaterials;
        restore.Add(() =>
        {
            if (renderer != null) renderer.sharedMaterials = mats;
            if (filter != null) filter.sharedMesh = mesh;
        });
    }

    public void Remember(CustomItem custom)
    {
        var data = custom._data;
        restore.Add(() => { if (custom != null) custom._data = data; });
    }

    public void Unsubscribe(Action action) => unsubscribe.Add(action);

    public void OnRestore(Action action) => restore.Add(action);

    public void RememberRotation(IEnumerable<Transform> targets)
    {
        foreach (var t in targets)
        {
            var r = t.localRotation;
            restore.Add(() => { if (t != null) t.localRotation = r; });
        }
    }

    public void EnsureSockets(Dictionary<string, float[]> sockets)
    {
        if (socketObjects.Count > 0) return;
        foreach (var pair in sockets)
        {
            if (transform.Find(pair.Key) != null) continue;
            var socket = new GameObject(pair.Key);
            socket.transform.SetParent(transform, false);
            socket.transform.localPosition = new Vector3(pair.Value[0], pair.Value[1], pair.Value[2]);
            socket.transform.localRotation = SocketBase;
            socketObjects.Add(socket);
        }
    }

    /// <summary>Shifts the donor's own sockets to fit this shell. Their original pose is restored when the object goes back to the pool.</summary>
    public void MoveSockets(Dictionary<string, Plugin.SocketMove> moves)
    {
        if (movedSockets.Count > 0) return;
        foreach (var pair in moves)
        {
            var socket = transform.Find(pair.Key);
            if (socket == null) continue;
            var position = socket.localPosition; var rotation = socket.localRotation;
            movedSockets.Add(() => { if (socket != null) { socket.localPosition = position; socket.localRotation = rotation; } });
            socket.localPosition = position + new Vector3(pair.Value.offset[0], pair.Value.offset[1], pair.Value.offset[2]);
            socket.localRotation = Quaternion.AngleAxis(pair.Value.tiltDeg, Vector3.right) * rotation;
        }
    }

    /// <summary>Puts the strobe and battery sockets at the bare-shell spot, or at the fitted cover's spot (move = rigid
    /// transform of the bare-shell placement, so rotation is R * base and position is given directly).</summary>
    public void PlaceCoverSockets(Dictionary<string, float[]> sockets, Dictionary<string, Plugin.Pose>? moves)
    {
        foreach (var name in new[] { "mod_equipment_003", "mod_equipment_004" })
        {
            var socket = transform.Find(name);
            if (socket == null || !sockets.TryGetValue(name, out var basePos)) continue;
            if (moves != null && moves.TryGetValue(name, out var pose))
            {
                socket.localPosition = new Vector3(pose.pos[0], pose.pos[1], pose.pos[2]);
                socket.localRotation = new Quaternion(pose.rot[0], pose.rot[1], pose.rot[2], pose.rot[3]) * SocketBase;
            }
            else
            {
                socket.localPosition = new Vector3(basePos[0], basePos[1], basePos[2]);
                socket.localRotation = SocketBase;
            }
        }
    }

    public void Restore()
    {
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
        Key = "";
    }

    public void ClearSockets()
    {
        foreach (var action in unsubscribe) { try { action(); } catch { } }
        unsubscribe.Clear();
        FollowingCover = false;
        Owner = null;
        foreach (var socket in socketObjects) if (socket != null) DestroyImmediate(socket);
        socketObjects.Clear();
        for (int i = movedSockets.Count - 1; i >= 0; i--) movedSockets[i]();
        movedSockets.Clear();
    }

    private void OnDestroy()
    {
        foreach (var action in unsubscribe) { try { action(); } catch { } }
        unsubscribe.Clear();
        Restore();
    }
}
