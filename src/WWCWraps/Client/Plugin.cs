using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace WWCWraps.Client;

/// <summary>
/// Gives WWC wraps and Raid Tape their look. Three kinds of item are dressed when the game builds a model of them (in a weapon, in hands, on
/// the ground, in the stash or the inspect window):
///  - wrapped handguards and grips, clones of stock parts: the near and far models get this mod's copy of the stock mesh with the camo
///    tape added as one more submesh, and one more material;
///  - Raid Tape rolls: the game's tape roll model repainted in the tape's colour or print;
///  - WWC mags (any magazine with this mod's tape slot): while a roll of Raid Tape sits in the slot, the mag gets the wound tape as a small
///    renderer of its own over whatever WWC Mags painted on it. The mag's own mesh and materials are never touched.
/// Everything is put back before a model goes back to the pool, because the pool also serves the stock items. On any failure the stock look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "WWC Wraps", "0.4.0")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Wraps");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("Wraps");
#endif
    public const string Id = "local.wwcwraps.client";
    internal const string TapeObject = "WWCWraps_tape_LOD";
    internal static Plugin? Instance;
    private readonly Dictionary<string, WrapEntry> wraps = new();
    private readonly Dictionary<string, string> tapes = new();                 // tape roll template -> tape key
    private readonly Dictionary<string, BaseInfo> bases = new();
    private readonly Dictionary<string, BaseInfo> magSlots = new();            // tape slot name -> mag body
    private readonly Dictionary<string, BaseInfo?> magOf = new();              // template -> mag body, or null when it takes no tape
    private readonly Dictionary<string, BaseInfo> wrappables = new();          // 0.4.0 Wrappable part template -> its part
    private readonly Dictionary<string, BaseInfo> wrapSlots = new();           // wrap slot name -> its part
    private readonly Dictionary<string, Mesh> meshes = new();
    private readonly Dictionary<string, Texture2D> textures = new();
    private readonly Dictionary<(int Material, string Key, int Lod), Material> copies = new();
    private readonly HashSet<string> errors = new();
    internal readonly HashSet<WrapVisualState> watched = new();
    private readonly List<WrapVisualState> gone = new();
    private float nextLook;
    private bool updating;
    private int notes;
    private string root = "";
    private Harmony? harmony;
    public int Watching => watched.Count;
    public int Applied { get; private set; }

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            root = Path.GetDirectoryName(Info.Location)!;
            var basesDir = Path.Combine(root, "bases");
            if (Directory.Exists(basesDir))
                foreach (var dir in Directory.GetDirectories(basesDir))
                {
                    var info = Path.Combine(dir, "base.json");
                    if (!File.Exists(info)) continue;
                    var b = JsonConvert.DeserializeObject<BaseInfo>(File.ReadAllText(info));
                    if (b == null || string.IsNullOrEmpty(b.@base)) continue;
                    bases[b.@base] = b;
                    if (b.tapeOnly && !string.IsNullOrEmpty(b.slot)) magSlots[b.slot!] = b;
                }
            var indexPath = Path.Combine(root, "index.json");
            if (File.Exists(indexPath))
                foreach (var e in JsonConvert.DeserializeObject<List<WrapEntry>>(File.ReadAllText(indexPath)) ?? new List<WrapEntry>())
                    if (bases.TryGetValue(e.@base, out var b) && !b.tapeOnly) wraps[e.tpl] = e;
            var tapesPath = Path.Combine(root, "tapes.json");
            if (File.Exists(tapesPath))
                foreach (var e in JsonConvert.DeserializeObject<List<WrapEntry>>(File.ReadAllText(tapesPath)) ?? new List<WrapEntry>())
                    tapes[e.tpl] = e.camo;
            // 0.4.0: the Wrappable parts, one per wrapped part. Their ids come from the same fixed text the server uses, so no list is needed;
            // the wrap slot is named after the part's near-model node and takes a Raid Tape roll (Widowz 2026-11-18: "Raid tape").
            foreach (var b in bases.Values.Where(b => !b.tapeOnly))
            {
                wrappables[IdOf("wrappable|" + b.@base)] = b;
                wrapSlots[b.lod0] = b;
            }
            Instance = this;
            harmony = new Harmony(Id);
            // Every way the game makes a model of an item, including a part inside a weapon (AttachMods builds each part with CreateItemAsync).
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)));
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(AssetPoolObject)).Where(m => m.Name == "ReturnToPool"))
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)));
            if (magSlots.Count > 0) PatchMagazineSlots();
            SeedSlotIcon();
            // The watcher runs from a hidden object of its own, not the BepInEx manager: the fixture's manager object, which is not hidden
            // there, is destroyed once the game is up (run Wraps_20261110_092914), and the live install hides it.
            var runner = new GameObject("WWCWrapsWatcher") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(runner);
            runner.AddComponent<WrapWatcher>();
            Logger.LogInfo($"WWC Wraps ready: {wraps.Count} wrapped items on {bases.Values.Count(b => !b.tapeOnly)} parts, {tapes.Count} Raid Tape rolls for {magSlots.Count} mag bodies, "
                + $"{wrappables.Count} Wrappable parts (wrapped with Raid Tape).");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Logger.LogError("WWC Wraps unavailable; wrapped items keep the stock look. " + error);
        }
    }

    public bool Knows(string tpl) => wraps.ContainsKey(tpl) || tapes.ContainsKey(tpl) || wrappables.ContainsKey(tpl);

    /// <summary>The server's ids: sha256("wwcwraps|" + key), first 24 hex digits.</summary>
    internal static string IdOf(string key)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("wwcwraps|" + key));
        var sb = new System.Text.StringBuilder(24);
        for (int i = 0; i < 12; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- a slot on a magazine

    /// <summary>The game's Magazine class answers for its cartridge stack only: Containers, GetContainer and Apply are overridden and leave
    /// out the slots every other item with slots has (only its revolver-cylinder subclass adds its own chambers). So the tape slot the
    /// server puts on a WWC mag does not exist for the game: the profile reader reports "No slot with id ..." and drops the roll (fixture
    /// run Wraps_20261110_163707). These three small patches let a magazine answer for this mod's tape slot as well, and for nothing else:
    /// stock mags and other mods' mags are untouched, and each patch leaves the game's own answer alone if anything goes wrong.</summary>
    private void PatchMagazineSlots()
    {
        try
        {
            harmony!.Patch(AccessTools.PropertyGetter(typeof(Magazine), nameof(Magazine.Containers)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterMagContainers)));
            harmony.Patch(AccessTools.Method(typeof(Magazine), nameof(Magazine.GetContainer), new[] { typeof(string) }), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterMagGetContainer)));
            magazineSlots = true;
        }
        catch (Exception error) { Logger.LogError("WWC Wraps: the tape slot on WWC mags is unavailable (Raid Tape can't be fitted). " + error); return; }
        try
        {
            // dropping a roll onto a mag: the game's Magazine.Apply only takes ammo, so a roll goes to the stock "fit it in a free slot" code
            var args = new[] { typeof(ItemController), typeof(Item), typeof(int), typeof(bool) };
            harmony.CreateReversePatcher(AccessTools.Method(typeof(CompoundItem), nameof(CompoundItem.Apply), args), new HarmonyMethod(typeof(Plugin), nameof(FitInSlot))).Patch();
            harmony.Patch(AccessTools.Method(typeof(Magazine), nameof(Magazine.Apply), args), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeMagApply)));
        }
        catch (Exception error) { Logger.LogWarning("WWC Wraps: a roll can't be dropped onto a mag; fit it in the mag's Tape slot from the inspect window instead. " + error.Message); }
        try
        {
            // the inspect window lists a magazine's rounds where other items list their slots, so the tape slot is added to that list
            harmony.Patch(AccessTools.Method(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.CreateModSlots)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateModSlots)));
        }
        catch (Exception error) { Logger.LogWarning("WWC Wraps: the Tape slot is not shown in a mag's inspect window (drop a roll on the mag; take it off in the gun's parts list). " + error.Message); }
    }

    private static readonly System.Reflection.FieldInfo? SlotViewPrefab = AccessTools.Field(typeof(ItemSpecificationPanel), "_modSlotViewPrefab");
    private static readonly System.Reflection.FieldInfo? SlotViewHolder = AccessTools.Field(typeof(ItemSpecificationPanel), "_modsContainer");
    private static readonly System.Reflection.FieldInfo? SlotViewPanel = AccessTools.Field(typeof(ItemSpecificationPanel), "_modsPanel");
    private static readonly System.Reflection.FieldInfo? PanelContext = AccessTools.Field(typeof(ItemSpecificationPanel), "_itemContext");
    private static readonly System.Reflection.FieldInfo? PanelController = AccessTools.Field(typeof(ItemSpecificationPanel), "_itemController");
    private static readonly System.Reflection.FieldInfo? PanelUi = AccessTools.Field(typeof(ItemSpecificationPanel), "_itemUiContext");

    /// <summary>After the inspect window has listed a WWC mag's rounds: one more slot view, made the way the window makes its own, for the
    /// tape slot, so a roll can be put in and taken out there. The window clears every view in that list itself when it closes or redraws.</summary>
    private static void AfterCreateModSlots(ItemSpecificationPanel __instance, CompoundItem __0)
    {
        var p = Instance;
        if (p is null || __instance == null || __0 is not Magazine mag || mag is CylinderMagazine || mag.Slots == null || mag.Slots.Length == 0) return;
        try
        {
            if (SlotViewPanel?.GetValue(__instance) is Transform panel && !panel.gameObject.activeSelf) return;      // not examined yet: the window shows no slots
            if (SlotViewPrefab?.GetValue(__instance) is not ModSlotView prefab || SlotViewHolder?.GetValue(__instance) is not RectTransform holder) return;
            foreach (var slot in mag.Slots)
            {
                if (!p.magSlots.ContainsKey(slot.ID)) continue;
                var view = Instantiate(prefab, holder, false);
                view.transform.localPosition = Vector3.zero;
                view.transform.localScale = Vector3.one;
                view.Show(slot, PanelContext?.GetValue(__instance) as ItemContext, PanelController?.GetValue(__instance) as ItemController, PanelUi?.GetValue(__instance) as ItemUiContext);
                view.SetLocked(__instance.GetModLockedState(slot), slot, mag);
            }
        }
        catch (Exception error) { p.ReportOnce("tape slot view", error); }
    }

    /// <summary>The picture behind an empty slot is a sprite the game loads by the slot's name ("Slots/&lt;name&gt;") and keeps in a small
    /// cache; with none it logs an error every time the slot is drawn. The tape slot's name is a model node, so this puts a drawn roll of
    /// tape in that cache under the tape slots' names.</summary>
    private void SeedSlotIcon()
    {
        try
        {
            if (AccessTools.Field(typeof(EFT.Utilities.ResourcesCache), "_storage")?.GetValue(null) is not Dictionary<string, object> storage) return;
            // 64 px like the game's own slot pictures, drawn dim (they are grey silhouettes) and well inside the frame; the first try
            // (128 px, solid white) spilled over the slot's edge as a white blob (fixture run Wraps_20261110_171042)
            const int n = 64, ss = 4;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < ss; sy++)
                        for (int sx = 0; sx < ss; sx++)
                        {
                            float u = x + (sx + 0.5f) / ss - 30f, v = y + (sy + 0.5f) / ss - 35f;
                            float r = Mathf.Sqrt(u * u + v * v);
                            bool ring = r > 8.5f && r < 18f;                               // the roll, seen flat on
                            bool end = u > 0f && u < 23f && v < -12.5f && v > -18f;       // the free end, pulled out along the bottom
                            if (ring || end) hit++;
                        }
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(90 * hit / (ss * ss)));
                }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "wwcwraps_slot_tape", hideFlags = HideFlags.DontUnloadUnusedAsset, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            var sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "wwcwraps_slot_tape";
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            foreach (var slot in magSlots.Keys.Concat(wrapSlots.Keys)) storage["Slots/" + slot] = sprite;
        }
        catch (Exception error) { Logger.LogWarning("WWC Wraps: no picture for the tape slot (it works without one). " + error.Message); }
    }

    private bool magazineSlots;
    /// <summary>True when the game has been taught that a WWC mag has a tape slot (read by the QA probe).</summary>
    public bool MagazineSlots => magazineSlots;

    private static void AfterMagContainers(Magazine __instance, ref IEnumerable<IContainer> __result)
    {
        var p = Instance;
        var slots = __instance?.Slots;
        if (p is null || slots == null || slots.Length == 0 || __result == null || __instance is CylinderMagazine) return;
        for (int i = 0; i < slots.Length; i++)
            if (p.magSlots.ContainsKey(slots[i].ID)) { __result = WithTapeSlots(__result, slots, p.magSlots); return; }
    }

    private static IEnumerable<IContainer> WithTapeSlots(IEnumerable<IContainer> stock, Slot[] slots, Dictionary<string, BaseInfo> ours)
    {
        foreach (var c in stock) yield return c;
        foreach (var s in slots) if (ours.ContainsKey(s.ID)) yield return s;
    }

    private static void AfterMagGetContainer(Magazine __instance, string __0, ref IContainer __result)
    {
        var p = Instance;
        var slots = __instance?.Slots;
        if (__result != null || p is null || __0 == null || slots == null || slots.Length == 0 || __instance is CylinderMagazine || !p.magSlots.ContainsKey(__0)) return;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].ID == __0) { __result = slots[i]; return; }
    }

    /// <summary>Stand-in filled by Harmony with CompoundItem.Apply (the stock "fit this item in a free slot that takes it").</summary>
    private static OperationResult FitInSlot(CompoundItem instance, ItemController itemController, Item item, int count, bool simulate) =>
        throw new NotImplementedException("WWC Wraps: reverse patch missing");

    private static bool BeforeMagApply(Magazine __instance, ItemController __0, Item __1, int __2, bool __3, ref OperationResult __result)
    {
        var p = Instance;
        if (p is null || __instance == null || __1 == null || __instance is CylinderMagazine || !p.tapes.ContainsKey(__1.StringTemplateId) || p.BodyOf(__instance) == null) return true;
        try { __result = FitInSlot(__instance, __0, __1, __2, __3); return false; }
        catch (Exception error) { p.ReportOnce("fit tape", error); return true; }
    }

    /// <summary>The first 200 watcher events go to the log, enough to follow a test run without flooding a long session.</summary>
    internal void Note(string what)
    {
        if (notes++ < 200) Logger.LogInfo("WWC Wraps: " + what);
    }

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"WWC Wraps: {key}: {error}");
    }

    /// <summary>Every frame: taped mags' tape follows its mag's visibility and layer. Twice a second: every watched model looks at its slot
    /// (the M4 wrap needs its lower half; a mag's tape slot can be filled or emptied without the game building the model again).</summary>
    internal void Tick()
    {
        if (!updating) { updating = true; Logger.LogInfo("WWC Wraps: watcher running"); }
        if (watched.Count == 0) return;
        foreach (var w in watched) if (w is not null && w.HasTape) w.Follow();
        if (Time.unscaledTime < nextLook) return;
        nextLook = Time.unscaledTime + 0.5f;
        gone.Clear();
        foreach (var w in watched.ToList())
        {
            try { if (!w.Look()) { gone.Add(w); Note("stopped watching a model that is gone"); } }
            catch (Exception error) { gone.Add(w); if (w != null) { w.Restore(); ReportOnce("watch " + (w.item?.StringTemplateId ?? "?"), error); } }
        }
        foreach (var w in gone) watched.Remove(w);
    }

    private bool Ours(Item item)
    {
        var tpl = item.StringTemplateId;
        return wraps.ContainsKey(tpl) || tapes.ContainsKey(tpl) || wrappables.ContainsKey(tpl) || BodyOf(item) != null;
    }

    /// <summary>The mag body a magazine's tape slot was cut for, or null when the item has no tape slot (worked out once per template).</summary>
    private BaseInfo? BodyOf(Item item)
    {
        var tpl = item.StringTemplateId;
        if (magOf.TryGetValue(tpl, out var body)) return body;
        if (magSlots.Count > 0 && item is CompoundItem ci && ci.Slots != null)
            foreach (var slot in ci.Slots)
                if (magSlots.TryGetValue(slot.ID, out var found)) { body = found; break; }
        return magOf[tpl] = body;
    }

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        var p = Instance;
        if (p is null || __0 is null || !p.Ours(__0)) return;
        var item = __0;
        // A finished task is dressed now, so the synchronous callers (the inspect window) see the look straight away.
        if (__result.IsCompleted)
        {
            if (__result.Status == TaskStatus.RanToCompletion && __result.Result != null) p.Apply(__result.Result, item);
            return;
        }
        __result = p.ApplyAsync(__result, item);
    }

    private static void AfterCreateNow(Item __0, GameObject? __result)
    {
        var p = Instance;
        if (p is null || __0 is null || __result == null || !p.Ours(__0)) return;
        p.Apply(__result, __0);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item)
    {
        var model = await task;
        if (model != null) Apply(model, item);
        return model;
    }

    /// <summary>Dresses the model, leaving the stock look if anything goes wrong. Also used by the QA probe.</summary>
    public void Apply(GameObject model, Item item)
    {
        var state = model.GetComponent<WrapVisualState>() ?? model.AddComponent<WrapVisualState>();
        state.Restore();
        var tpl = item.StringTemplateId;
        try
        {
            if (wraps.TryGetValue(tpl, out var entry))
            {
                var info = bases[entry.@base];
                if (!string.IsNullOrEmpty(info.needsSlot))
                {
                    // The M4 wrap goes round both halves: with the lower half missing it would float, so it only shows with the lower fitted.
                    state.Watch(this, item as CompoundItem, info.needsSlot!, model, null);
                    if (!HasPart(item as CompoundItem, info.needsSlot!)) return;
                }
                Dress(model, entry, info, state);
            }
            else if (tapes.TryGetValue(tpl, out var camo))
            {
                DressRoll(model, camo, state, "roll");
                // A roll that sits in a mag's tape slot is on the mag as wound tape: the roll's own model, which the game hangs on the
                // mag, is never drawn (from its first frame; the watcher also shrinks it).
                // (CurrentAddress, not Parent: Parent throws for an item that is nowhere, such as one built for an icon)
                // The same in a Wrappable part's wrap slot: the tape is drawn on the part itself.
                if (item.CurrentAddress?.Container is Slot held && (magSlots.ContainsKey(held.ID) || wrapSlots.ContainsKey(held.ID))) state.HideAll(model);
            }
            else if (wrappables.TryGetValue(tpl, out var part))
            {
                // A Wrappable part shows wrapped in the colour of the Raid Tape roll in its wrap slot, plain without one; the watcher follows changes.
                state.WatchWrap(this, item as CompoundItem, part, model);
                state.Look();
            }
            else if (BodyOf(item) is { } body)
            {
                state.Watch(this, item as CompoundItem, body.slot!, model, body);
                state.Look();
            }
        }
        catch (Exception error)
        {
            state.Restore();
            ReportOnce(tpl, error);
        }
    }

    // Slots is read directly: a magazine does not list its slots among its Containers unless this mod's patch is in
    internal static bool HasPart(CompoundItem? item, string slot) => item?.Slots?.FirstOrDefault(s => s.ID == slot)?.ContainedItem != null;

    /// <summary>The tape key of the roll in a mag's tape slot, or "" when the slot is empty or holds something else.</summary>
    internal string TapeIn(CompoundItem? item, string slot)
    {
        var held = item?.Slots?.FirstOrDefault(s => s.ID == slot)?.ContainedItem;
        return held != null && tapes.TryGetValue(held.StringTemplateId, out var camo) ? camo : "";
    }

    // ---------------------------------------------------------------- wrapped handguards and grips

    internal void Dress(GameObject model, WrapEntry entry, BaseInfo info, WrapVisualState state)
    {
        var done = 0;
        foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = filter.sharedMesh;
            var renderer = filter.GetComponent<MeshRenderer>();
            if (mesh == null || renderer == null) continue;
            int lod = mesh.name == info.lod0 ? 0 : mesh.name == info.lod1 ? 1 : -1;
            if (lod < 0) continue;
            var stock = renderer.sharedMaterials;
            var donor = stock.FirstOrDefault(m => m != null);
            if (donor == null) continue;
            var subs = lod == 0 ? info.lod0_donor_subs : info.lod1_donor_subs;
            var mats = new Material[subs + 1];
            for (int i = 0; i < subs; i++) mats[i] = i < stock.Length && stock[i] != null ? stock[i] : donor;
            mats[subs] = TapeMaterial(donor, info, entry.camo, lod);
            state.Remember(filter, renderer);
            filter.sharedMesh = MeshFor(info, lod, "lod");
            renderer.sharedMaterials = mats;
            if (lod == 0) done++;
        }
        if (done == 0) throw new InvalidDataException($"no {info.lod0} model found");
        Applied++;
    }

    // ---------------------------------------------------------------- Raid Tape rolls

    /// <summary>The roll itself: the game's tape roll model wearing this tape's page (roll/&lt;tape&gt;.jpg).</summary>
    private void DressRoll(GameObject model, string camo, WrapVisualState state, string folder)
    {
        var done = 0;
        foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer == null || !renderer.name.StartsWith("model_LOD", StringComparison.Ordinal)) continue;
            var donor = renderer.sharedMaterials.FirstOrDefault(m => m != null);
            if (donor == null) continue;
            var key = (donor.GetInstanceID(), folder + "|" + camo, 0);
            if (!copies.TryGetValue(key, out var copy) || copy == null)
            {
                copy = new Material(donor) { name = $"{donor.name}_wwcwrap_{folder}_{camo}", hideFlags = HideFlags.DontUnloadUnusedAsset };
                copy.SetTexture("_MainTex", TextureFor(Path.Combine(root, folder, camo + ".jpg"), TextureKind.Colour));
                if (copy.HasProperty("_BumpMap")) copy.SetTexture("_BumpMap", TextureFor(Path.Combine(root, "camo", "raid_normal.png"), TextureKind.Normal));
                if (copy.HasProperty("_Color")) copy.SetColor("_Color", Color.white);
                copies[key] = copy;
            }
            state.Remember(filter, renderer);
            renderer.sharedMaterials = renderer.sharedMaterials.Select(_ => copy).ToArray();
            done++;
        }
        if (done == 0) throw new InvalidDataException("no tape roll model found");
        Applied++;
    }

    // ---------------------------------------------------------------- tape on a WWC mag

    /// <summary>Hangs the wound tape on a mag: one small renderer under the mag's near model and one under its far model, each added to
    /// the mag's own level of detail group so it shows and hides with the mag.</summary>
    internal void ShowTape(WrapVisualState state, GameObject model, BaseInfo body, string camo)
    {
        state.HideTape();
        var pairs = new List<(MeshRenderer Mag, MeshRenderer Tape)>();
        foreach (var magRenderer in model.GetComponentsInChildren<MeshRenderer>(true))
        {
            int lod = magRenderer.name == body.lod0_node ? 0 : magRenderer.name == body.lod1_node ? 1 : -1;
            if (lod < 0) continue;
            // WWC Mags gives a see-through mag glass materials first; the tape copies an opaque one
            var mats = magRenderer.sharedMaterials;
            var donor = mats.FirstOrDefault(m => m != null && m.shader != null && !m.shader.name.Contains("Transparent")) ?? mats.FirstOrDefault(m => m != null);
            if (donor == null) continue;
            var go = new GameObject(TapeObject + lod) { layer = magRenderer.gameObject.layer };
            go.transform.SetParent(magRenderer.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = MeshFor(body, lod, "tape");
            var tape = go.AddComponent<MeshRenderer>();
            tape.sharedMaterial = TapeMaterial(donor, body, camo, lod);
            tape.shadowCastingMode = magRenderer.shadowCastingMode;
            tape.receiveShadows = magRenderer.receiveShadows;
            tape.lightProbeUsage = magRenderer.lightProbeUsage;
            tape.reflectionProbeUsage = magRenderer.reflectionProbeUsage;
            tape.probeAnchor = magRenderer.probeAnchor;
            tape.enabled = magRenderer.enabled;
            state.tape.Add((go, tape, magRenderer));
            pairs.Add((magRenderer, tape));
        }
        if (pairs.Count == 0) throw new InvalidDataException($"no {body.lod0_node} model found for the tape");
        foreach (var group in model.GetComponentsInChildren<LODGroup>(true))
        {
            var original = group.GetLODs();
            var lods = group.GetLODs();
            var changed = false;
            for (int i = 0; i < lods.Length; i++)
            {
                var list = lods[i].renderers;
                if (list == null) continue;
                foreach (var (mag, tape) in pairs)
                    if (Array.IndexOf(list, mag) >= 0) { list = list.Append(tape).ToArray(); changed = true; }
                lods[i].renderers = list;
            }
            if (!changed) continue;
            state.lodGroups.Add((group, original));
            group.SetLODs(lods);
        }
        state.tapeShown = camo;
        Applied++;
    }

    /// <summary>One tape material per stock material, part, tape and level of detail, shared by every model that wears it.</summary>
    private Material TapeMaterial(Material donor, BaseInfo info, string camo, int lod)
    {
        var key = (donor.GetInstanceID(), info.@base + "|" + camo, lod);
        if (copies.TryGetValue(key, out var copy) && copy != null) return copy;
        copy = new Material(donor) { name = $"{donor.name}_wwcwrap_{info.@base}_{camo}_{lod}", hideFlags = HideFlags.DontUnloadUnusedAsset };
        var surface = info.surface ?? "cloth";
        copy.SetTexture("_MainTex", TextureFor(Path.Combine(root, "camo", $"{camo}_{surface}.jpg"), TextureKind.Colour));
        copy.mainTextureScale = Vector2.one;
        copy.mainTextureOffset = Vector2.zero;
        if (lod == 0)
        {
            if (copy.HasProperty("_SpecMap")) copy.SetTexture("_SpecMap", TextureFor(Path.Combine(root, "camo", $"{surface}_gloss.png"), TextureKind.Gloss));
            if (copy.HasProperty("_BumpMap")) copy.SetTexture("_BumpMap", TextureFor(Path.Combine(root, "camo", $"{surface}_normal.png"), TextureKind.Normal));
            if (copy.HasProperty("_Glossness")) copy.SetFloat("_Glossness", info.glossness);
            if (copy.HasProperty("_Specularness")) copy.SetFloat("_Specularness", info.specularness);
            if (copy.HasProperty("_HasTint")) copy.SetFloat("_HasTint", 0f);
        }
        if (copy.HasProperty("_Color")) copy.SetColor("_Color", Color.white);
        if (copy.HasProperty("_SpecColor")) copy.SetColor("_SpecColor", new Color(info.specColor, info.specColor, info.specColor, 1f));
        if (copy.HasProperty("_ReflectColor")) copy.SetColor("_ReflectColor", new Color(info.reflect, info.reflect, info.reflect, 0.5f));
        return copies[key] = copy;
    }

    private enum TextureKind { Colour, Gloss, Normal }

    private Texture2D TextureFor(string path, TextureKind kind)
    {
        if (textures.TryGetValue(path, out var loaded) && loaded != null) return loaded;
        // JPG and plain PNG decode without alpha, so the pixels go into a fresh RGBA texture where the alpha can be set.
        var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, kind == TextureKind.Normal);
        if (!decoded.LoadImage(File.ReadAllBytes(path), false)) { Destroy(decoded); throw new InvalidDataException("Could not read " + path); }
        var px = decoded.GetPixels32();
        int w = decoded.width, h = decoded.height;
        Destroy(decoded);
        if (kind == TextureKind.Normal)
            for (int i = 0; i < px.Length; i++) { var c = px[i]; px[i] = new Color32(255, c.g, c.g, c.r); }      // the game keeps X in alpha, Y in green
        else if (kind == TextureKind.Colour)
            for (int i = 0; i < px.Length; i++) px[i].a = 40;     // colour alpha = how much light it reflects: cloth tape hardly shines
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, kind == TextureKind.Normal) { name = "wwcwraps_" + Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.DontUnloadUnusedAsset };
        tex.SetPixels32(px);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 5;
        tex.Apply(true, false);
        tex.Compress(true);
        tex.Apply(false, true);
        return textures[path] = tex;
    }

    /// <summary>A WRW1 mesh written by tools/build_wraps.py: "lod" = the stock mesh with the tape as its last submesh, "tape" = the tape alone.</summary>
    private Mesh MeshFor(BaseInfo info, int lod, string kind)
    {
        var key = info.@base + "|" + kind + lod;
        if (meshes.TryGetValue(key, out var mesh) && mesh != null) return mesh;
        var data = File.ReadAllBytes(Path.Combine(root, "bases", info.@base, $"{kind}{lod}.bin"));
        if (data.Length < 12 || data[0] != 'W' || data[1] != 'R' || data[2] != 'W' || data[3] != '1') throw new InvalidDataException("Not a WWC wrap mesh: " + key);
        int count = BitConverter.ToInt32(data, 4), subCount = BitConverter.ToInt32(data, 8), off = 12;
        var verts = new Vector3[count]; var normals = new Vector3[count]; var tangents = new Vector4[count]; var uv = new Vector2[count];
        for (int i = 0; i < count; i++, off += 12) verts[i] = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
        for (int i = 0; i < count; i++, off += 12) normals[i] = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
        for (int i = 0; i < count; i++, off += 16) tangents[i] = new Vector4(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8), BitConverter.ToSingle(data, off + 12));
        for (int i = 0; i < count; i++, off += 8) uv[i] = new Vector2(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4));
        var name = kind == "tape" ? "wwcwraps_tape_" + info.@base + "_LOD" + lod : lod == 0 ? info.lod0 : info.lod1;
        mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.DontUnloadUnusedAsset };
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
        return meshes[key] = mesh;
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance == null) return;
        WrapVisualState.Unshrink(__instance.transform);      // a tape roll that sat, shrunk, in a mag's tape slot
        __instance.GetComponent<WrapVisualState>()?.Release();
    }

    public sealed class WrapEntry { public string tpl = ""; public string @base = ""; public string camo = ""; }
    public sealed class BaseInfo
    {
        public string @base = "", lod0 = "", lod1 = "";
        public int lod0_donor_subs = 1, lod1_donor_subs = 1;
        public string? surface, needsSlot;
        public float glossness = 0.6f, specularness = 0.35f, specColor = 0.35f, reflect = 0.08f;
        // mag bodies: the tape is a separate renderer and the slot is named after the near-model node
        public bool tapeOnly;
        public string? slot, lod0_node, lod1_node;
    }
}

/// <summary>Calls the plugin's watcher every frame from a hidden object that scene loads leave alone.</summary>
public sealed class WrapWatcher : MonoBehaviour
{
    private void Update()
    {
        try { Plugin.Instance?.Tick(); }
        catch (Exception error) { enabled = false; Plugin.Instance?.ReportOnce("watcher", error); }
    }
}

/// <summary>Holds what a pooled model looked like before it was dressed, so it can go back unchanged. For the M4 wrap it remembers the
/// lower-half slot to watch; for a mag, its tape slot, the tape renderers hung on it and the level of detail lists they were added to.</summary>
public sealed class WrapVisualState : MonoBehaviour
{
    private readonly List<(MeshFilter Filter, Mesh Mesh, MeshRenderer Renderer, Material[] Materials)> saved = new();
    internal readonly List<(GameObject Object, MeshRenderer Tape, MeshRenderer Mag)> tape = new();
    internal readonly List<(LODGroup Group, LOD[] Original)> lodGroups = new();
    private static readonly Dictionary<Transform, Vector3> Shrunk = new();
    private static readonly Vector3 Tiny = Vector3.one * 0.0001f;
    internal Plugin? plugin;
    internal CompoundItem? item;
    internal string slot = "";
    internal GameObject? model;
    internal Plugin.BaseInfo? magBody;
    internal Plugin.BaseInfo? wrapPart;     // 0.4.0: a Wrappable part, dressed in the colour of the Raid Tape roll in its wrap slot
    internal string wrapShown = "";
    internal bool shown;
    internal string tapeShown = "";

    private readonly List<(Renderer Renderer, bool Off)> hidden = new();

    public void Remember(MeshFilter filter, MeshRenderer renderer) => saved.Add((filter, filter.sharedMesh, renderer, renderer.sharedMaterials));

    /// <summary>Stops every renderer of the model being drawn (a roll in a mag's tape slot), remembering how each was set.</summary>
    internal void HideAll(GameObject root)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            hidden.Add((r, r.forceRenderingOff));
            r.forceRenderingOff = true;
        }
    }

    public bool Dressed => saved.Count > 0;
    public bool HasTape => tape.Count > 0;

    internal void WatchWrap(Plugin owner, CompoundItem? watchedItem, Plugin.BaseInfo part, GameObject root)
    {
        plugin = owner; item = watchedItem; slot = part.lod0; model = root; wrapPart = part; wrapShown = "";
        if (watchedItem is not null) owner.watched.Add(this);
    }

    internal void Watch(Plugin owner, CompoundItem? watchedItem, string slotId, GameObject root, Plugin.BaseInfo? body)
    {
        plugin = owner; item = watchedItem; slot = slotId; model = root; magBody = body;
        shown = Plugin.HasPart(watchedItem, slotId);
        if (watchedItem is not null) owner.watched.Add(this);
    }

    /// <summary>Called by the plugin twice a second (and once when the model is built): true while there is still something to watch.</summary>
    internal bool Look()
    {
        // "is null", not "== null": Unity's == calls a destroyed object null, and the plugin's own object is destroyed in the fixture
        if (this == null || item is null || plugin is null) return false;
        if (model is null || model == null) model = gameObject;          // the state lives on the model it dresses
        if (wrapPart is not null)
        {
            // the look of the wrap in the slot; the M4 wrap goes round both halves, so it also needs the lower half fitted
            var look = plugin.TapeIn(item, slot);
            if (look.Length > 0 && !string.IsNullOrEmpty(wrapPart.needsSlot) && !Plugin.HasPart(item, wrapPart.needsSlot!)) look = "";
            if (look == wrapShown) return true;
            Restore();
            wrapShown = "";
            if (look.Length > 0)
            {
                plugin.Dress(model, new Plugin.WrapEntry { tpl = item.StringTemplateId, @base = wrapPart.@base, camo = look }, wrapPart, this);
                wrapShown = look;
            }
            plugin.Note($"{item.Name.Localized()}: {(look.Length > 0 ? "wrapped (" + look + ")" : "plain")}");
            return true;
        }
        if (magBody is not null)
        {
            var now = plugin.TapeIn(item, slot);
            if (now != tapeShown)
            {
                if (now.Length > 0) plugin.ShowTape(this, model, magBody, now); else HideTape();
                plugin.Note($"{item.Name.Localized()}: {(now.Length > 0 ? "tape on (" + now + ")" : "tape off")}");
            }
            ShrinkHung();
            return true;
        }
        var has = Plugin.HasPart(item, slot);
        if (has == shown) return true;
        shown = has;
        Restore();
        if (has) plugin.Apply(model, item);
        plugin.Note($"{item.Name.Localized()}: lower half {(has ? "back on, tape on" : "off, tape off")}");
        return true;
    }

    /// <summary>Every frame for a taped mag: the tape shows exactly when its mag does, on the same camera layer.</summary>
    internal void Follow()
    {
        foreach (var (go, tapeRenderer, mag) in tape)
        {
            if (go == null || mag == null) continue;
            if (tapeRenderer.enabled != mag.enabled) tapeRenderer.enabled = mag.enabled;
            if (tapeRenderer.forceRenderingOff != mag.forceRenderingOff) tapeRenderer.forceRenderingOff = mag.forceRenderingOff;
            if (go.layer != mag.gameObject.layer) go.layer = mag.gameObject.layer;
        }
    }

    /// <summary>The game hangs the model of whatever sits in a slot on the node the slot is named after, here the mag's near model. The
    /// roll is on the mag as wound tape, so its own model is shrunk out of sight until it leaves (it is a pooled object: never destroyed).</summary>
    private void ShrinkHung()
    {
        if (magBody is null || model == null) return;
        foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r.name != magBody.lod0_node) continue;
            foreach (Transform child in r.transform)
            {
                if (child.name.StartsWith(Plugin.TapeObject, StringComparison.Ordinal)) continue;
                if (!Shrunk.ContainsKey(child)) Shrunk[child] = child.localScale;
                if (child.localScale != Tiny) child.localScale = Tiny;
            }
        }
    }

    internal static void Unshrink(Transform? t)
    {
        if (t == null || !Shrunk.TryGetValue(t, out var scale)) return;
        Shrunk.Remove(t);
        t.localScale = scale;
    }

    internal void HideTape()
    {
        foreach (var (group, original) in lodGroups) if (group != null) group.SetLODs(original);
        lodGroups.Clear();
        foreach (var (go, _, _) in tape) if (go != null) Destroy(go);
        tape.Clear();
        tapeShown = "";
    }

    public void Restore()
    {
        foreach (var (filter, mesh, renderer, materials) in saved)
        {
            if (filter != null) filter.sharedMesh = mesh;
            if (renderer != null) renderer.sharedMaterials = materials;
        }
        saved.Clear();
        foreach (var (r, off) in hidden) if (r != null) r.forceRenderingOff = off;
        hidden.Clear();
    }

    /// <summary>Back to the pool: stock look, tape off, anything shrunk in the slot back to size, and stop watching.</summary>
    public void Release()
    {
        Restore();
        HideTape();
        if (magBody is not null && model != null)
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
                if (r.name == magBody.lod0_node)
                    foreach (Transform child in r.transform) Unshrink(child);
        plugin?.watched.Remove(this);
        item = null; plugin = null; model = null; magBody = null; wrapPart = null; wrapShown = "";
    }
}
