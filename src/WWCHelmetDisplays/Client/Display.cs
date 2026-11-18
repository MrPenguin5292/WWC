using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.AssetsManager;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using WWCHelmetDisplays.Client.Geometry;

namespace WWCHelmetDisplays.Client;

/// <summary>What the player's look ray hits on a hung display; the actions themselves come from <see cref="Plugin"/>.</summary>
public sealed class DisplayInteractive : InteractableObject
{
    public DisplayRoot Display = null!;
}

/// <summary>
/// A display in the world: its gear frames (where the game would hang a helmet), the cut mannequin parts, the gear itself, a light and the box you look at.
/// It only reads the item's grids; gear goes on and off through the game's normal item window.
/// </summary>
public sealed class DisplayRoot : MonoBehaviour
{
    // Which display grid feeds which equipment slot of a bust's mannequin.
    private static readonly Dictionary<string, EquipmentSlot> BustSlots = new()
    {
        ["helmet"] = EquipmentSlot.Headwear, ["mask"] = EquipmentSlot.FaceCover, ["glasses"] = EquipmentSlot.Eyewear,
        ["headset"] = EquipmentSlot.Earpiece, ["armor"] = EquipmentSlot.ArmorVest, ["rig"] = EquipmentSlot.TacticalVest,
    };
    // The rack's hooks in layout order run from -x to +x, and +x is the viewer's left.
    private static readonly string[] RackGrids = { "right", "middle", "left" };

    internal string Key = "";
    internal CompoundItem Item = null!;
    internal Layout Layout = null!;
    internal int Wood, Finish;
    internal bool LightOn = true;
    public bool IsBust => Layout?.bust != null;

    private Transform anchor = null!;
    private Vector3 anchorBase;
    private Vector3 anchorScale = Vector3.one;
    private Material donor = null!;
    private readonly List<Transform> frames = new();
    private readonly List<GameObject> statics = new();
    private MeshRenderer? bustRenderer;
    private readonly Dictionary<int, Hook> hooks = new();
    private Rig? rig;
    private bool rigBuilding;
    private readonly Dictionary<EquipmentSlot, string> worn = new();
    private GameObject? body;
    private Light? lamp;
    private Coroutine? watching;
    private string current = "";
    private int generation;
    private bool placed;

    private sealed class Hook { public string ItemId = ""; public GameObject? Model; }

    internal bool IconLook;

    internal void Bind(Transform t, CompoundItem item, string key, Layout layout, Material donorMaterial, bool iconLook)
    {
        anchor = t; Item = item; Key = key; Layout = layout; donor = donorMaterial; IconLook = iconLook;
        anchorBase = t.localPosition; anchorScale = t.localScale;
        var states = Plugin.Instance!.States;
        Wood = states.Wood(item.Id); Finish = states.Finish(item.Id); LightOn = states.Light(item.Id);
        BuildStatics();
    }

    /// <summary>The gear frames and the parts cut from the mannequin: these show in the stash icon and inspect window too.</summary>
    private void BuildStatics()
    {
        if (frames.Count == 0)
        {
            int i = 0;
            foreach (var spot in Layout.gear)
            {
                var frame = new GameObject("WWCHelmet_gear_" + i++) { layer = anchor.gameObject.layer }.transform;
                frame.SetParent(anchor, false);
                frame.localPosition = new Vector3(spot.x, spot.y, spot.z);
                frame.localRotation = Quaternion.identity;
                frames.Add(frame);
            }
        }
        EnsureStatics();
    }

    /// <summary>Adds the crown or the bust once the mannequin kit is ready (it is made in the background when the game starts).</summary>
    private void EnsureStatics()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || !Kit.Ready || statics.Count > 0) return;
        for (int i = 0; i < Layout.gear.Count && i < frames.Count; i++)
            if (Layout.gear[i].crown && Kit.Crown != null && plugin.CrownMaterial(IconLook) is Material crown)
                statics.Add(Part(frames[i], "WWCHelmet_crown", Kit.Crown, crown));
        if (Layout.bust != null && frames.Count > 0)
        {
            var mesh = Layout.bust.kind == "trophy" ? Kit.Trophy : Kit.Bust;
            var mat = plugin.BustMaterial(Finish, IconLook);
            if (mesh != null && mat != null)
            {
                var go = Part(frames[0], "WWCHelmet_bust", mesh, mat);
                bustRenderer = go.GetComponent<MeshRenderer>();
                bustRenderer.sharedMaterials = BustMaterials(plugin, mesh.subMeshCount);
                statics.Add(go);
            }
        }
    }

    // Part 0 is the body's surface, part 1 the flat faces of the cut, part 2 the head (its own texture).
    private Material[] BustMaterials(Plugin plugin, int parts)
    {
        var surface = plugin.BustMaterial(Finish, IconLook)!;
        var cap = plugin.CapMaterial(Finish, IconLook) ?? surface;
        var head = plugin.BustMaterial(Finish, IconLook, head: true) ?? surface;
        var list = new Material[parts];
        for (int i = 0; i < parts; i++) list[i] = i == 1 ? cap : i == 2 ? head : surface;
        return list;
    }

    private static GameObject Part(Transform parent, string name, Mesh mesh, Material material)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterials = Enumerable.Repeat(material, mesh.subMeshCount).ToArray();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return go;
    }

    // ---------- placed in the hideout ----------

    public void OnPlaced()
    {
        if (Plugin.Instance is null || anchor == null || Item == null) return;
        placed = true;
        // The game centres a model's box on its spot. Our origin is the spot itself: the wall behind a wall display, the shelf under a bust.
        var spot = SpotTransform();
        anchor.localScale = anchorScale;
        anchor.localPosition = anchorBase;
        if (spot != null) anchor.position = spot.position;
        if (!Layout.wall) FitToSurface();
        else FitOnWall();
        EnsureStatics();
        EnsureBody();
        EnsureLamp();
        current = "";
        Refresh();
        if (watching == null && isActiveAndEnabled) watching = StartCoroutine(Watch());
    }

    private Transform? SpotTransform()
    {
        for (var t = transform; t != null; t = t.parent)
        {
            var model = t.GetComponent<EFT.Hideout.StashItemModel>();
            if (model != null) return model.transform;
        }
        return null;
    }

    private string SpotName()
    {
        for (var t = transform; t != null; t = t.parent) if (t.name.StartsWith("Statuette_", StringComparison.Ordinal) || t.name.StartsWith("Poster_", StringComparison.Ordinal) || t.name.IndexOf("rophies", StringComparison.Ordinal) >= 0) return t.name;
        return transform.name;
    }

    // A statuette spot is sized for a small figure. The bust keeps its full size when there is room above it, and shrinks to the headroom when a shelf is in the way.
    private void FitToSurface()
    {
        try
        {
            const float fullHeight = 0.74f;     // socle, bust and a helmet with a mount on it
            var basePoint = anchor.position;
            float headroom = 3f;
            foreach (var offset in new[] { Vector3.zero, anchor.right * 0.15f, -anchor.right * 0.15f, anchor.forward * 0.12f, -anchor.forward * 0.1f })
            {
                foreach (var hit in Physics.RaycastAll(basePoint + offset + Vector3.up * 0.03f, Vector3.up, 2f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.transform.IsChildOf(transform) || hit.collider.name == "Quad") continue;
                    headroom = Mathf.Min(headroom, hit.distance + 0.03f);
                }
            }
            float scale = Mathf.Clamp((headroom - 0.02f) / fullHeight, 0.4f, 1f);
            anchor.localScale = anchorScale * scale;
            Plugin.Instance?.Log($"{Key} on {SpotName()}: headroom {headroom:0.00} m, shown at {scale:P0}");
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("bust fit", error); }
    }

    // A wall display stands up to 30 cm out of the wall. Over a bed or a counter the trophy bust's chest went down into the blanket (WWC trailer,
    // Poster_RestSpace_2, 2026-11-18), so a wall display moves up (or, failing that, down) in 1 cm steps until nothing solid is inside the room
    // it takes in front of the wall. Nothing clear within 40 cm up or 20 cm down leaves it where the game put it.
    private void FitOnWall()
    {
        try
        {
            var b = Layout.box;
            float front = b.cz + b.d / 2, back = 0.02f;          // from 2 cm off the wall (not the wall itself) to the display's front
            var centre = new Vector3(b.cx, b.cy, (front + back) / 2);
            var size = new Vector3(Mathf.Max(0.02f, b.w - 0.02f), Mathf.Max(0.02f, b.h - 0.02f), front - back);
            var start = anchor.position;
            var scale = anchor.lossyScale;
            var half = Vector3.Scale(size, scale) * 0.5f;
            string? blocker = null;
            bool Clear(float dy)
            {
                var c = anchor.TransformPoint(centre) + anchor.up * dy;
                foreach (var col in Physics.OverlapBox(c, half, anchor.rotation, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (col == null || col.transform.IsChildOf(transform) || col.GetComponentInParent<Player>() != null) continue;
                    blocker ??= col.name;
                    return false;
                }
                return true;
            }
            if (Clear(0f)) return;
            for (int k = 1; k <= 40; k++)
            {
                float dy = k * 0.01f;
                if (Clear(dy)) { anchor.position = start + anchor.up * dy; Plugin.Instance?.Log($"{Key} on {SpotName()}: moved {k} cm up, clear of {blocker}"); return; }
                if (k <= 20 && Clear(-dy)) { anchor.position = start - anchor.up * dy; Plugin.Instance?.Log($"{Key} on {SpotName()}: moved {k} cm down, clear of {blocker}"); return; }
            }
            Plugin.Instance?.Log($"{Key} on {SpotName()}: {blocker} is in the way and no spot within 40 cm up or 20 cm down is clear; left where it is");
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("wall display fit", error); }
    }

    private void EnsureBody()
    {
        if (body != null || anchor == null) return;
        body = new GameObject("WWCHelmet_interact");
        int layer = LayerMask.NameToLayer("Interactive");
        body.layer = layer >= 0 ? layer : anchor.gameObject.layer;
        body.transform.SetParent(anchor, false);
        var b = Layout.box;
        body.transform.localPosition = new Vector3(b.cx, b.cy, b.cz);
        body.AddComponent<BoxCollider>().size = new Vector3(b.w, b.h, b.d);
        body.AddComponent<DisplayInteractive>().Display = this;
    }

    private void EnsureLamp()
    {
        if (lamp != null || anchor == null) return;
        var go = new GameObject("WWCHelmet_light") { layer = 0 };
        go.transform.SetParent(anchor, false);
        var l = Layout.light;
        go.transform.localPosition = new Vector3(l.x, l.y, l.z);
        lamp = go.AddComponent<Light>();
        lamp.type = LightType.Point;
        lamp.range = l.range;
        lamp.intensity = l.intensity * Settings.Current.LightBrightness;
        lamp.color = new Color(1f, 0.86f, 0.68f);
        lamp.shadows = LightShadows.None;
        lamp.renderMode = LightRenderMode.ForcePixel;
        lamp.enabled = LightOn;
    }

    private void OnEnable() { if (placed && watching == null && isActiveAndEnabled) watching = StartCoroutine(Watch()); }
    private void OnDisable() { watching = null; }

    private IEnumerator Watch()
    {
        var wait = new WaitForSeconds(1.2f);
        while (true) { yield return wait; EnsureStatics(); Refresh(); }
    }

    // ---------- what's in the grids ----------

    private Item? InGrid(string name)
    {
        var grid = Item?.Grids?.FirstOrDefault(g => g.ID == name);
        return grid?.Items.FirstOrDefault();
    }

    private static string Signature(Item? item) => item == null ? "-" : item.Id + "/" + item.GetAllItems().Count();

    internal void Refresh()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || Item == null || !placed) return;
        try
        {
            string wanted;
            if (IsBust) wanted = string.Join("|", BustSlots.Keys.Select(g => g + ":" + Signature(InGrid(g))));
            else wanted = string.Join("|", GridsInOrder().Select(g => g + ":" + Signature(InGrid(g))));
            if (wanted == current) return;
            current = wanted;
            generation++;
            if (IsBust) UpdateBust(plugin);
            else UpdateHooks();
        }
        catch (Exception error) { plugin.ReportOnce("display contents", error); }
    }

    private IEnumerable<string> GridsInOrder() => Key == "rack" ? RackGrids : new[] { "helmet" };

    // ---------- helmets on hooks ----------

    private void UpdateHooks()
    {
        int i = 0;
        foreach (var grid in GridsInOrder())
        {
            int index = i++;
            if (index >= frames.Count) break;
            var item = InGrid(grid);
            var id = item == null ? "" : Signature(item);
            if (hooks.TryGetValue(index, out var h) && h.ItemId == id) continue;
            if (h?.Model != null) AssetPoolObject.ReturnToPool(h.Model);
            hooks[index] = h = new Hook { ItemId = id };
            if (item != null) SpawnHelmet(h, item, frames[index], generation);
        }
    }

    private async void SpawnHelmet(Hook h, Item item, Transform frame, int gen)
    {
        try
        {
            await EasyAssetsExtensions.WaitForAllBundlesJob(item.GetAllBundleTokens(), () => { });
            var model = await Singleton<ObjectsFactory>.Instance.CreateItemAsync(item, ECameraType.Default, null, false, JobYieldPriority.General);
            if (model == null) return;
            if (this == null || frame == null || gen != generation && hooks.Values.All(x => x != h) || h.Model != null) { AssetPoolObject.ReturnToPool(model); return; }
            h.Model = model;
            model.transform.SetParent(frame, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = frame.gameObject.layer;
            var dress = model.GetComponent<DressItem>();
            if (dress != null)
            {
                // Headwear the game skins to the body (a balaclava, a hood) has no rigid shape to hang; its loot model is set on top of the crown instead.
                dress.EnableLoot(true);
                model.SetActive(true);
                var bounds = EFT.UI.WeaponModding.WeaponPreview.GetBounds(model);
                var bottom = frame.InverseTransformPoint(bounds.min).y;
                var centre = frame.InverseTransformPoint(bounds.center);
                model.transform.localPosition = new Vector3(-centre.x, 0.176f - bottom, 0.03f - centre.z);
            }
            model.SetActive(true);
            LightsOff.Apply(model);
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("helmet on hook", error); }
    }

    // ---------- the bust's mannequin ----------

    private void UpdateBust(Plugin plugin)
    {
        if (rig == null)
        {
            if (!rigBuilding) BuildRig();
            return;
        }
        foreach (var kv in BustSlots)
        {
            var item = InGrid(kv.Key);
            var sig = Signature(item);
            if (worn.TryGetValue(kv.Value, out var had) && had == sig) continue;
            worn[kv.Value] = sig;
            rig.Wear(kv.Value, item);
        }
        StartCoroutine(CutWhenLoaded(generation));
    }

    private async void BuildRig()
    {
        rigBuilding = true;
        try
        {
            if (frames.Count == 0) return;
            if (!Kit.Ready) await Kit.Build();
            if (!Kit.Ready) return;     // no mannequin this session: the display shows its hardware only
            var made = await Rig.Create(frames[0], "bust");
            if (made == null) return;
            if (this == null || frames.Count == 0 || frames[0] == null || !placed) { made.Dispose(); return; }
            rig = made;
            var turn = Quaternion.Inverse(Kit.GearRot);
            rig.Root.localRotation = turn;
            rig.Root.localPosition = -(turn * Kit.GearPos);
            rig.Root.localScale = Vector3.one;
            rig.HideSkin();
            foreach (var t in rig.Root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = frames[0].gameObject.layer;
            worn.Clear();
            current = "";
            Refresh();
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("bust mannequin", error); }
        finally { rigBuilding = false; }
    }

    private IEnumerator CutWhenLoaded(int gen)
    {
        // The game loads gear in the background; the cut is made once every piece is in place.
        float until = Time.realtimeSinceStartup + 30f;
        yield return null;
        while (rig != null && rig.Loading && Time.realtimeSinceStartup < until) yield return new WaitForSeconds(0.1f);
        // Some gear changes its own look a moment after it loads, from what else is worn (the AirLite rig drops its flotation collar under
        // the Death Knight or Avon mask, checking every half second), and the cut freezes what it sees, so it waits for that first.
        yield return new WaitForSecondsRealtime(1.5f);
        yield return null;
        if (gen != generation || rig == null) yield break;
        try { CutGear(); }
        catch (Exception error) { Plugin.Instance?.ReportOnce("cutting bust gear", error); }
    }

    /// <summary>
    /// Armor, rigs and some masks are skinned to the whole body. Each one is frozen in the bust's pose and cut along the bust's own lines, so it ends where the bust ends.
    /// The game's model is hidden, not changed, and comes back as it was when it goes back to the pool.
    /// </summary>
    internal void CutGear()
    {
        if (rig == null || frames.Count == 0) return;
        var frame = frames[0];
        var cuts = Layout.bust?.kind == "trophy" ? Shapes.TrophyBust() : Shapes.Bust();
        // Baking a skinned mesh under a scaled parent gives its points without that scale, so the cut is made at full size (a bust shrunk to fit a shelf is put back to full size for a moment).
        var scaled = anchor.localScale;
        anchor.localScale = anchorScale;
        try { CutAll(frame, cuts); }
        finally { anchor.localScale = scaled; }
    }

    private void CutAll(Transform frame, List<Cut> cuts)
    {
        foreach (var view in rig!.SlotViews())
        {
            var model = view.Model;
            if (model != null) LightsOff.Apply(model);
            if (model == null || model.GetComponent<GearCut>() != null) continue;
            var dress = model.GetComponent<DressItem>();
            if (dress == null || dress.DressPrefab == null) continue;
            var cut = model.AddComponent<GearCut>();
            var lod0 = Lod0(dress.DressPrefab);
            var toFrame = frame.worldToLocalMatrix;
            foreach (var smr in lod0)
            {
                if (smr.sharedMesh == null) continue;
                var shape = Kit.Baked(smr, toFrame);
                // Body gear only (rigs, armor: skinned from the spine or pelvis); a mask's dreadlocks are thin pieces too and stay.
                var root = smr.rootBone != null ? smr.rootBone.name : "";
                bool body = !(root.Contains("Spine3") || root.Contains("Neck") || root.Contains("Head"));
                string note = "";
                if (body) GearPieces.LeaveOffLoose(shape, cuts, out note);
                if (note.Length > 0) Plugin.Instance?.Log($"bust cut {smr.name}: {note}");
                var clipped = MeshClip.Clip(shape, cuts, -1, 0.25f, doubleSided: true);
                if (clipped.TriangleCount == 0) continue;
                var mesh = Kit.ToMesh(clipped, "WWCHelmet_cut_" + smr.name);
                var go = new GameObject("WWCHelmet_cut_" + smr.name) { layer = frame.gameObject.layer };
                go.transform.SetParent(frame, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = smr.sharedMaterials;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                cut.Made(go, mesh);
            }
            foreach (var smr in dress.DressPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)) cut.Hide(smr);
            HideOffBust(dress.DressPrefab, cut, toFrame, cuts);
            foreach (var lod in dress.DressPrefab.GetComponentsInChildren<LODGroup>(true)) cut.Hide(lod);
        }
    }

    /// <summary>
    /// Rigid parts of skinned gear (a rig's radio and its antenna, a hard case on a pouch) hang on the posed body's bones and are not cut. One that
    /// does not lie wholly on the bust is hidden: the Knight's CPC Goons Edition carries its radio at the hip, so on a bust its antenna came up
    /// out of the shelf and past the mask (WWC trailer, 2026-11-18). The rest keep only their first LOD: the LOD group is switched off with the
    /// cut, which would otherwise draw every level at once.
    /// </summary>
    private static void HideOffBust(GameObject prefab, GearCut cut, Matrix4x4 toFrame, List<Cut> cuts)
    {
        var first = new HashSet<Renderer>(); var later = new HashSet<Renderer>();
        foreach (var g in prefab.GetComponentsInChildren<LODGroup>(true))
        {
            var lods = g.GetLODs();
            for (int i = 0; i < lods.Length; i++)
                foreach (var r in lods[i].renderers) if (r is MeshRenderer) (i == 0 ? first : later).Add(r);
        }
        foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null || !r.enabled) continue;
            if (later.Contains(r) && !first.Contains(r)) { cut.Hide(r); continue; }
            if (!OnBust(r, toFrame, cuts)) cut.Hide(r);
        }
    }

    /// <summary>Every corner of the part's mesh box is on the kept side of every cut (2 cm of slack).</summary>
    private static bool OnBust(MeshRenderer r, Matrix4x4 toFrame, List<Cut> cuts)
    {
        var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
        var box = mesh != null ? mesh.bounds : new Bounds(r.transform.InverseTransformPoint(r.bounds.center), r.transform.InverseTransformVector(r.bounds.size));
        var toFrameLocal = toFrame * r.localToWorldMatrix;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? box.min.x : box.max.x, (i & 2) == 0 ? box.min.y : box.max.y, (i & 4) == 0 ? box.min.z : box.max.z);
            var p = toFrameLocal.MultiplyPoint3x4(c);
            var v = new V3(p.x, p.y, p.z);
            foreach (var cutPlane in cuts) if (cutPlane.Dist(v) < -0.02f) return false;
        }
        return true;
    }

    /// <summary>
    /// The skinned pieces to freeze: the first level of every LOD group, plus any shown piece outside all groups. A rig mod can add one at run time
    /// (the AirLite lays its front patch on the MK5 panel as a skinned piece next to the rig's own), and the cut hides it with the rest.
    /// </summary>
    private static List<SkinnedMeshRenderer> Lod0(GameObject prefab)
    {
        var groups = prefab.GetComponentsInChildren<LODGroup>(true);
        var list = new List<SkinnedMeshRenderer>();
        var grouped = new HashSet<Renderer>();
        foreach (var g in groups)
        {
            var lods = g.GetLODs();
            for (int i = 0; i < lods.Length; i++)
                foreach (var r in lods[i].renderers)
                {
                    if (r == null) continue;
                    grouped.Add(r);
                    if (i == 0 && r is SkinnedMeshRenderer s) list.Add(s);
                }
        }
        if (list.Count == 0) list.AddRange(prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.enabled));
        else
            list.AddRange(prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => !grouped.Contains(r) && r.enabled && !r.forceRenderingOff
                && r.gameObject.activeInHierarchy && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly));
        return list.Distinct().ToList();
    }

    // ---------- the player's choices ----------

    public void NextWood()
    {
        Wood = (Wood + 1) % Plugin.Woods.Length;
        var plugin = Plugin.Instance;
        if (plugin is null) return;
        var r = anchor != null ? anchor.GetComponent<MeshRenderer>() : null;
        if (r != null) r.sharedMaterials = plugin.HardwareMaterials(donor, Wood);
        plugin.States.Set(Item.Id, wood: Wood);
    }

    public void NextFinish()
    {
        Finish = (Finish + 1) % Plugin.Finishes.Length;
        var plugin = Plugin.Instance;
        if (plugin is null) return;
        if (bustRenderer != null) bustRenderer.sharedMaterials = BustMaterials(plugin, bustRenderer.sharedMaterials.Length);
        plugin.States.Set(Item.Id, finish: Finish);
    }

    public void ToggleLight()
    {
        LightOn = !LightOn;
        if (lamp != null) lamp.enabled = LightOn;
        // "is null", not "== null": the plugin object may be gone (a destroyed Unity object compares equal to null) while its data is still fine to use.
        var plugin = Plugin.Instance;
        if (plugin is not null && Item != null) plugin.States.Set(Item.Id, light: LightOn);
    }

    // For the QA probe: what is shown right now.
    public int HooksShown => hooks.Values.Count(h => h.Model != null);
    internal Rig? CurrentRig => rig;
    public int CutPieces => frames.Count == 0 ? 0 : frames[0].GetComponentsInChildren<Transform>(true).Count(t => t.name.StartsWith("WWCHelmet_cut_", StringComparison.Ordinal));
    public Transform? Frame(int i) => i < frames.Count ? frames[i] : null;
    public Transform Anchor => anchor;
    public string DisplayKey => Key;
    public int WoodIndex => Wood;
    public int FinishIndex => Finish;
    public bool Light => LightOn;
    public bool HasRig => rig != null;
    public bool RigLoading => rig != null && rig.Loading;
    public int RigModels => rig == null ? 0 : rig.SlotViews().Count(v => v.Model != null);
    public float Scale => anchor == null ? 0f : anchor.localScale.x / Mathf.Max(anchorScale.x, 1e-6f);
    public string Spot => SpotName();
    public CompoundItem DisplayItem => Item;

    /// <summary>Removes everything this component added, before the poster donor goes back to the pool.</summary>
    public void Clear()
    {
        generation++;
        placed = false;
        foreach (var h in hooks.Values) if (h.Model != null) AssetPoolObject.ReturnToPool(h.Model);
        hooks.Clear();
        rig?.Dispose();
        rig = null;
        worn.Clear();
        foreach (var g in statics) if (g != null) Destroy(g);
        statics.Clear();
        foreach (var f in frames) if (f != null) Destroy(f.gameObject);
        frames.Clear();
        bustRenderer = null;
        if (body != null) Destroy(body);
        body = null;
        if (lamp != null) Destroy(lamp.gameObject);
        lamp = null;
        if (anchor != null) { anchor.localPosition = anchorBase; anchor.localScale = anchorScale; }
        current = "";
    }

    private void OnDestroy() => Clear();
}

/// <summary>
/// Switches off the lights gear brings along: the FTHS's MPLS helmet light on a wall mount threw a hard cone up the wall and the mount plate's
/// shadow along it (WWC trailer, Poster_RestSpace_1, 2026-11-18). A display has its own lamp. Checked every half second, since a light's own
/// controller may switch it back on; the ones switched off are put back before the game reuses the model.
/// </summary>
public sealed class LightsOff : MonoBehaviour
{
    private readonly List<Light> off = new();
    private float next;

    public static void Apply(GameObject model) => (model.GetComponent<LightsOff>() ?? model.AddComponent<LightsOff>()).Sweep();

    private void Sweep()
    {
        foreach (var l in GetComponentsInChildren<Light>(true))
            if (l != null && l.enabled) { l.enabled = false; if (!off.Contains(l)) off.Add(l); }
    }

    private void LateUpdate()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.5f;
        Sweep();
    }

    public void Restore()
    {
        foreach (var l in off) if (l != null) l.enabled = true;
        off.Clear();
        Destroy(this);
    }
}

/// <summary>Marks a piece of skinned gear the bust has cut a copy of; undone before the game reuses the model.</summary>
public sealed class GearCut : MonoBehaviour
{
    private readonly List<Action> restore = new();
    private readonly List<GameObject> made = new();
    private readonly List<Mesh> meshes = new();

    public void Made(GameObject go, Mesh mesh) { made.Add(go); meshes.Add(mesh); }
    public void Hide(Renderer r) { var on = r.enabled; restore.Add(() => { if (r != null) r.enabled = on; }); r.enabled = false; }
    public void Hide(LODGroup g) { var on = g.enabled; restore.Add(() => { if (g != null) g.enabled = on; }); g.enabled = false; }

    public void Restore()
    {
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
        foreach (var g in made) if (g != null) Destroy(g);
        made.Clear();
        foreach (var m in meshes) if (m != null) Destroy(m);
        meshes.Clear();
        Destroy(this);
    }

    private void OnDestroy()
    {
        foreach (var g in made) if (g != null) Destroy(g);
        foreach (var m in meshes) if (m != null) Destroy(m);
    }
}

/// <summary>Records every change made to a pooled object so it can be put back exactly.</summary>
public sealed class VisualState : MonoBehaviour
{
    private readonly List<Action> restore = new();

    public void Remember(Renderer renderer, MeshFilter filter)
    {
        var mesh = filter.sharedMesh;
        var materials = renderer.sharedMaterials;
        var shadow = renderer.shadowCastingMode;
        restore.Add(() => { if (renderer != null) { renderer.sharedMaterials = materials; renderer.shadowCastingMode = shadow; } if (filter != null) filter.sharedMesh = mesh; });
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
        var display = GetComponent<DisplayRoot>();
        if (display != null) display.Clear();
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
    }

    private void OnDestroy() => Restore();
}
