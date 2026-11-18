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
using EFT;
using EFT.AssetsManager;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;

namespace JackalPatchCase.Client;

/// <summary>
/// Gives the patch case its own model, and wears whatever patch is in its cover slot on the front. The case is a clone of the dog tag case, so without this plugin it simply looks like one.
/// Every change is made on the model the game already spawned and undone before that model goes back to the pool, and on any failure the donor look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Jackal Patch Case", "0.2.0")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Patch Case");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("PatchCase");
#endif
    public const string Id = "local.jackalpatchcase.client";
    // Named after the donor's LOD0 mesh on purpose: the game hangs a slot's item on the transform of the same name, and the dog tag case has no spare one.
    public const string CoverSlot = "item_container_smallitems_LOD0";
    internal static Plugin? Instance;
    private string root = "";
    private string caseTpl = "";
    private Mesh? mesh;
    private Material[]? materials;
    private bool loadFailed;
    private readonly Dictionary<string, Texture2D> textures = new();
    private readonly HashSet<string> errors = new();
    private Harmony? harmony;

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            root = Path.Combine(Path.GetDirectoryName(Info.Location)!, "model");
            if (!File.Exists(Path.Combine(root, "case.bin"))) throw new FileNotFoundException("model\\case.bin is missing");
            caseTpl = Hash("jackalpatchcase|case");
            Instance = this;
            harmony = new Harmony(Id);
            // Every way the game makes a model of an item: stash and hideout, loose loot on a raid floor, and in-hand previews.
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)));
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)));
            harmony.Patch(AccessTools.Method(typeof(EFT.UI.WeaponModding.WeaponPreview), nameof(EFT.UI.WeaponModding.WeaponPreview.PositionGameObject)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePreview)));
            Logger.LogInfo($"Jackal Patch Case ready: {caseTpl}.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Logger.LogError("Jackal Patch Case unavailable; the donor look remains. " + error);
        }
    }

    private static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2"))).Substring(0, 24);
    }

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"Jackal Patch Case: {key}: {error}");
    }

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        var p = Instance;
        if (p is null || __0.StringTemplateId != p.caseTpl) return;
        __result = p.ApplyAsync(__result, __0);
    }

    // The same swap for the calls that return straight away, which is how loose loot on a raid floor is made. The cover patch follows a moment later.
    private static void AfterCreateNow(Item __0, GameObject? __result)
    {
        var p = Instance;
        if (p is null || __result == null || __0.StringTemplateId != p.caseTpl) return;
        p.Dress(__result, __0);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item)
    {
        var model = await task;
        if (model == null) return null;
        // Nothing here may wait: the game also builds models through this call when it needs one at once (the inspect window does), and an unfinished task gives it nothing.
        Dress(model, item);
        return model;
    }

    private void Dress(GameObject model, Item item)
    {
        var state = model.GetComponent<CaseVisualState>() ?? model.AddComponent<CaseVisualState>();
        // One model comes back through several of the game's calls (the sync ones wrap the async one); dress it once.
        if (ReferenceEquals(state.DressedFor, item)) return;
        state.Restore();
        try
        {
            // Only the case's own two LOD meshes: the patch the game hangs in the cover slot sits underneath them and must be left alone.
            var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null && f.name.StartsWith("item_container_smallitems_LOD", StringComparison.Ordinal)).ToArray();
            if (filters.Length == 0) throw new InvalidDataException("the donor has no rigid renderer");
            var donor = filters[0].GetComponent<MeshRenderer>().sharedMaterials.FirstOrDefault(m => m != null) ?? throw new InvalidDataException("the donor has no material");
            if (mesh == null || materials == null) Load(donor);
            if (mesh == null || materials == null) return;
            foreach (var filter in filters)
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                state.Remember(renderer, filter);
                filter.sharedMesh = mesh;
                renderer.sharedMaterials = materials;
            }
            foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
            if (item is CompoundItem compound)
            {
                var bone = filters.FirstOrDefault(f => f.name == CoverSlot) ?? filters[0];
                var cover = model.GetComponent<CoverPatch>() ?? model.AddComponent<CoverPatch>();
                cover.Bind(compound, bone.transform);
            }
            state.DressedFor = item;
        }
        catch (Exception error)
        {
            state.Restore();
            ReportOnce("dressing the case", error);
        }
    }

    private void Load(Material donor)
    {
        if (loadFailed) return;
        try
        {
            mesh = ReadMesh(Path.Combine(root, "case.bin"), "JackalPatchCase");
            // The icon camera fits an item to its bounds but crops this flat, square-cornered shape at one edge. A little room around it keeps the whole case in the picture.
            var b = mesh.bounds;
            mesh.bounds = new Bounds(b.center, Vector3.Scale(b.size, new Vector3(1.12f, 1.12f, 1.12f)));
            string S(string file) => Path.Combine(root, file);
            materials = new[]
            {
                Fabric(donor, "cover", S("cover_a.png"), S("cover_n.png"), S("cover_s.png"), 0.8f, 0.04f, 0.02f),
                Fabric(donor, "side", S("side_a.png"), S("side_n.png"), S("side_s.png"), 0.8f, 0.04f, 0.02f),
                Fabric(donor, "metal", S("metal_a.png"), S("metal_n.png"), S("metal_s.png"), 2.4f, 0.4f, 0.3f),
            };
        }
        catch (Exception error)
        {
            loadFailed = true;
            mesh = null;
            materials = null;
            ReportOnce("loading the model", error);
        }
    }

    // The dog tag case is shiny plastic: its own specular colour is nearly white, and left alone that washes the cloth out to grey.
    private Material Fabric(Material donor, string key, string albedo, string normal, string gloss, float specularness, float reflect, float specular)
    {
        var m = new Material(donor) { name = "JackalPatchCase_" + key };
        void Tex(string prop, string path, bool linear, bool isNormal)
        {
            if (!m.HasProperty(prop)) return;
            m.SetTexture(prop, Load(path, linear, isNormal));
            m.SetTextureScale(prop, Vector2.one);
            m.SetTextureOffset(prop, Vector2.zero);
        }
        Tex("_MainTex", albedo, false, false);
        Tex("_BumpMap", normal, true, true);
        Tex("_SpecMap", gloss, true, false);
        if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", specularness);
        if (m.HasProperty("_Glossness")) m.SetFloat("_Glossness", 1f);
        if (m.HasProperty("_NormalIntensity")) m.SetFloat("_NormalIntensity", 1f);
        if (m.HasProperty("_TRIPLANAR")) m.SetFloat("_TRIPLANAR", 0f);
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(reflect, reflect, reflect, 0.5f));
        if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(specular, specular, specular, 1f));
        return m;
    }

    private Texture2D Load(string path, bool linear, bool isNormal)
    {
        if (textures.TryGetValue(path, out var cached)) return cached;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "JackalPatchCase_" + Path.GetFileNameWithoutExtension(path) };
        if (!tex.LoadImage(File.ReadAllBytes(path), false)) throw new InvalidDataException("Could not read " + path);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 8;
        if (isNormal)
        {
            // Same layout the game's own compressed normal maps use (X in alpha, Y in green).
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

    private static Mesh ReadMesh(string path, string name)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 12 || data[0] != 'J' || data[1] != 'P' || data[2] != 'M' || data[3] != '1') throw new InvalidDataException("Not a patch mesh: " + path);
        int vertexCount = BitConverter.ToInt32(data, 4), subCount = BitConverter.ToInt32(data, 8), off = 12;
        var verts = new Vector3[vertexCount]; var normals = new Vector3[vertexCount]; var uv = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++, off += 12) verts[i] = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
        for (int i = 0; i < vertexCount; i++, off += 12) normals[i] = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
        for (int i = 0; i < vertexCount; i++, off += 8) uv[i] = new Vector2(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4));
        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
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

    // The inspect window turns an item around the donor's own pivot, which doesn't sit in the middle of this shape. Only the inspect window: the stash icon places the model its own way.
    private static void BeforePreview(EFT.UI.WeaponModding.WeaponPreview __instance, GameObject itemGameObject)
    {
        try
        {
            var p = Instance;
            if (p is null || itemGameObject == null) return;
            var item = Traverse.Create(__instance).Field("_currentItem").GetValue<Item>();
            if (item == null || item.StringTemplateId != p.caseTpl) return;
            var pivot = itemGameObject.GetComponent<PreviewPivot>();
            if (pivot == null) return;
            // The case's own centre, not the patch's: the patch on the cover would pull it off to one side.
            var filter = itemGameObject.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(f => f.sharedMesh != null && f.sharedMesh.name == "JackalPatchCase");
            if (filter == null) return;
            (itemGameObject.GetComponent<CaseVisualState>() ?? itemGameObject.AddComponent<CaseVisualState>()).RememberPivot(pivot);
            pivot.pivotPosition = itemGameObject.transform.InverseTransformPoint(filter.transform.TransformPoint(filter.sharedMesh.bounds.center));
        }
        catch (Exception error) { Instance?.ReportOnce("inspect centring", error); }
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance == null) return;
        __instance.GetComponent<CaseVisualState>()?.Restore();
        CoverPatch.Unshrink(__instance.transform);
    }
}

/// <summary>
/// The patch worn on the cover: a copy of the model the game makes for whatever is in the cover slot, kit patches and the Slickster's alike, laid on the loop panel.
/// It looks at the slot a few times a second, so a patch fitted or taken off in the inspect window shows straight away.
/// </summary>
public sealed class CoverPatch : MonoBehaviour
{
    // The loop panel in the case model's own axes: the cover faces +Y and the art's up is +X, so the panel's own +Z points out of the cover and its +Y up the art.
    private static readonly Vector3 PanelCentre = new(0.005f, 0.0141f, 0f);
    private static readonly Quaternion PanelTurn = Quaternion.LookRotation(Vector3.up, Vector3.right);
    private const float MaxWidth = 0.098f, MaxHeight = 0.080f, TiltDegrees = 3f;
    private static readonly Dictionary<string, Visual> Visuals = new();
    // The patch model the game hangs in the slot sits in the middle of the case; it's shrunk out of sight while the copy on the cover shows, and put back before it goes to its pool.
    private static readonly Dictionary<Transform, Vector3> Shrunk = new();
    private static readonly Vector3 Tiny = Vector3.one * 0.0001f;
    private CompoundItem? caseItem;
    private Transform? parent;
    private GameObject? shown;
    private string current = "";
    private bool busy;
    private float nextLook;

    public string ShownTemplate => shown != null ? current : "";

    internal void Bind(CompoundItem item, Transform meshParent)
    {
        caseItem = item; parent = meshParent; current = "\u0001"; enabled = true;
        ShowNow();
        ShrinkSlotModels();
    }

    internal void Clear()
    {
        if (parent != null) foreach (Transform child in parent) Unshrink(child);
        if (shown != null) Destroy(shown);
        shown = null; caseItem = null; parent = null; current = ""; enabled = false;
    }

    private void ShrinkSlotModels()
    {
        if (parent == null) return;
        foreach (Transform child in parent)
        {
            if (child.name.StartsWith("JackalPatchCase_", StringComparison.Ordinal)) continue;
            if (!Shrunk.ContainsKey(child)) Shrunk[child] = child.localScale;
            if (child.localScale != Tiny) child.localScale = Tiny;
        }
    }

    internal static void Unshrink(Transform? t)
    {
        if (t == null || !Shrunk.TryGetValue(t, out var scale)) return;
        Shrunk.Remove(t);
        t.localScale = scale;
    }

    private Item? InSlot() => caseItem?.Containers.OfType<Slot>().FirstOrDefault(s => s.ID == Plugin.CoverSlot)?.ContainedItem;

    /// <summary>The patch model the game itself hung in the slot, if it has.</summary>
    private Transform? AttachedModel()
    {
        if (parent == null) return null;
        foreach (Transform child in parent)
        {
            if (child.name.StartsWith("JackalPatchCase_", StringComparison.Ordinal)) continue;
            if (child.GetComponentsInChildren<MeshFilter>(true).Any(f => f.sharedMesh != null && f.sharedMesh.vertexCount > 0)) return child;
        }
        return null;
    }

    private void Update()
    {
        if (caseItem == null || busy || Time.unscaledTime < nextLook) return;
        nextLook = Time.unscaledTime + 0.25f;
        if ((InSlot()?.StringTemplateId ?? "") != current) ShowNow();
        ShrinkSlotModels();
        // A window moves the whole model onto its own camera's layer; the patch has to follow.
        if (shown != null && parent != null && shown.layer != parent.gameObject.layer) SetLayer(shown, parent.gameObject.layer);
    }

    /// <summary>Draws the slot's patch straight away when it can, from the model the game hung in the slot or one seen before; otherwise builds it in the background.</summary>
    private void ShowNow()
    {
        var patch = InSlot();
        var tpl = patch?.StringTemplateId ?? "";
        if (tpl == current) return;
        if (shown != null) Destroy(shown);
        shown = null;
        current = tpl;
        if (patch == null || parent == null) return;
        try
        {
            var attached = AttachedModel();
            if (attached != null && !Visuals.ContainsKey(tpl))
            {
                var fromSlot = Capture(attached.gameObject);
                // The slot model is only usable once the patch mod has dressed it; a bare TATM mount means it hasn't, so build our own instead.
                if (fromSlot.Mesh.name.IndexOf("tatm", StringComparison.OrdinalIgnoreCase) < 0) Visuals[tpl] = fromSlot;
            }
            if (Visuals.TryGetValue(tpl, out var visual)) { shown = Place(visual); return; }
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("cover patch " + tpl, error); return; }
        _ = BuildAsync(patch, tpl);
    }

    private async Task BuildAsync(Item patch, string tpl)
    {
        busy = true;
        try
        {
            await EasyAssetsExtensions.WaitForAllBundlesJob(patch.GetAllBundleTokens(), () => { });
            if (this == null || caseItem == null) return;
            var model = await Singleton<ObjectsFactory>.Instance.CreateItemAsync(patch, ECameraType.Default, null, isAnimated: false, JobYieldPriority.General);
            try { if (model != null) Visuals[tpl] = Capture(model); }
            finally { if (model != null) AssetPoolObject.ReturnToPool(model); }
            if (this == null || caseItem == null || parent == null || current != tpl || shown != null) return;
            if (Visuals.TryGetValue(tpl, out var visual)) shown = Place(visual);
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("cover patch " + tpl, error); }
        finally { busy = false; }
    }

    private static Visual Capture(GameObject model)
    {
        var pair = model.GetComponentsInChildren<MeshFilter>(true)
            .Where(f => f.sharedMesh != null && f.sharedMesh.vertexCount > 0 && f.GetComponent<MeshRenderer>() != null)
            .OrderByDescending(f => f.sharedMesh.vertexCount).First();
        var mesh = pair.sharedMesh;
        var mats = pair.GetComponent<MeshRenderer>().sharedMaterials;
        // Kit patches are built facing +Z with the art upright; anything else is turned by its own UVs, the same way the patch wall does it.
        var orient = mesh.name.StartsWith("JackalPatch_", StringComparison.Ordinal) || !mesh.isReadable ? Quaternion.identity : FaceForward(mesh);
        return new Visual(mesh, mats, orient);
    }

    private static Quaternion FaceForward(Mesh mesh)
    {
        var verts = mesh.vertices; var uv = mesh.uv; var tris = mesh.triangles;
        Vector3 rightSum = Vector3.zero, upSum = Vector3.zero;
        for (int t = 0; t < tris.Length; t += 3)
        {
            Vector3 e1 = verts[tris[t + 1]] - verts[tris[t]], e2 = verts[tris[t + 2]] - verts[tris[t]];
            float du1 = uv[tris[t + 1]].x - uv[tris[t]].x, dv1 = uv[tris[t + 1]].y - uv[tris[t]].y;
            float du2 = uv[tris[t + 2]].x - uv[tris[t]].x, dv2 = uv[tris[t + 2]].y - uv[tris[t]].y;
            float det = du1 * dv2 - du2 * dv1;
            if (Mathf.Abs(det) < 1e-9f) continue;
            float area = Vector3.Cross(e1, e2).magnitude;
            rightSum += (e1 * dv2 - e2 * dv1).normalized * area * Mathf.Sign(det);
            upSum += (e2 * du1 - e1 * du2).normalized * area * Mathf.Sign(det);
        }
        if (rightSum.sqrMagnitude < 1e-14f || upSum.sqrMagnitude < 1e-14f) return Quaternion.identity;
        var up = upSum.normalized;
        var face = Vector3.Cross(up, rightSum.normalized);
        if (face.sqrMagnitude < 1e-8f) return Quaternion.identity;
        return Quaternion.Inverse(Quaternion.LookRotation(face.normalized, up));
    }

    private GameObject Place(Visual v)
    {
        int layer = parent!.gameObject.layer;
        var anchor = new GameObject("JackalPatchCase_cover_patch") { layer = layer };
        anchor.transform.SetParent(parent, false);
        anchor.transform.localPosition = PanelCentre;
        anchor.transform.localRotation = PanelTurn;
        var rot = Quaternion.Euler(0f, 0f, TiltDegrees) * v.Orient;
        var (w, h) = Outline(v.Mesh.bounds, rot);
        // Real size, unless the patch is bigger than the cover can take.
        float scale = Mathf.Min(1f, MaxWidth / Mathf.Max(w, 1e-4f), MaxHeight / Mathf.Max(h, 1e-4f));
        var centre = rot * v.Mesh.bounds.center * scale;
        float back = MinZ(v.Mesh.bounds, rot) * scale;
        var child = new GameObject("JackalPatchCase_cover_patch_mesh") { layer = layer };
        child.transform.SetParent(anchor.transform, false);
        child.transform.localPosition = new Vector3(-centre.x, -centre.y, -back + 0.0002f);
        child.transform.localRotation = rot;
        child.transform.localScale = Vector3.one * scale;
        child.AddComponent<MeshFilter>().sharedMesh = v.Mesh;
        child.AddComponent<MeshRenderer>().sharedMaterials = v.Materials;
        return anchor;
    }

    private static (float, float) Outline(Bounds b, Quaternion rot)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
        {
            var c = rot * (b.center + Vector3.Scale(b.extents, new Vector3(x, y, z)));
            minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
            minY = Mathf.Min(minY, c.y); maxY = Mathf.Max(maxY, c.y);
        }
        return (maxX - minX, maxY - minY);
    }

    private static float MinZ(Bounds b, Quaternion q)
    {
        float min = float.MaxValue;
        for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
            min = Mathf.Min(min, (q * (b.center + Vector3.Scale(b.extents, new Vector3(x, y, z)))).z);
        return min;
    }

    private static void SetLayer(GameObject go, int layer)
    {
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    private void OnDestroy() { if (parent != null) foreach (Transform child in parent) Unshrink(child); if (shown != null) Destroy(shown); }

    private sealed class Visual { public Mesh Mesh; public Material[] Materials; public Quaternion Orient; public Visual(Mesh m, Material[] mats, Quaternion o) { Mesh = m; Materials = mats; Orient = o; } }
}

/// <summary>Remembers what the donor looked like, so a pooled object goes back exactly as it came.</summary>
public sealed class CaseVisualState : MonoBehaviour
{
    private readonly List<Action> restore = new();
    internal Item? DressedFor;

    public void Remember(MeshRenderer renderer, MeshFilter filter)
    {
        var materials = renderer.sharedMaterials; var mesh = filter.sharedMesh;
        restore.Add(() => { if (renderer != null) renderer.sharedMaterials = materials; if (filter != null) filter.sharedMesh = mesh; });
    }

    public void RememberPivot(PreviewPivot pivot)
    {
        var position = pivot.pivotPosition;
        restore.Add(() => { if (pivot != null) pivot.pivotPosition = position; });
    }

    public void Restore()
    {
        DressedFor = null;
        foreach (var a in restore) { try { a(); } catch { } }
        restore.Clear();
        GetComponent<CoverPatch>()?.Clear();
    }

    private void OnDestroy() => Restore();
}






