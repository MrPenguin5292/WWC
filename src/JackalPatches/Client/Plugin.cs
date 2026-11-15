using System;
using System.Collections;
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
using Diz.Skinning;
using EFT.CameraControl;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.Visual;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace JackalPatches.Client;

/// <summary>
/// Gives kit-made patches their stitched mesh and textures (read from plain files, so a new patch needs no Unity build) and turns the
/// patch wall poster into a velcro board that shows every different patch in the player's inventory. All of this happens on donor objects
/// the game already spawns, every change is undone before an object goes back to the pool, and on any failure the donor look stays.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Jackal Patches", "0.9.3")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Patches");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.Part;
#endif
    public const string Id = "local.jackalpatches.client";
    internal static Plugin? Instance;
    internal readonly Dictionary<string, PatchEntry> Patches = new();
    internal readonly List<string> Order = new();
    internal string WallTpl = "";
    internal Mesh EmptyMesh = null!;
    private string root = "";
    private readonly Dictionary<string, PatchAsset> assets = new();
    private readonly Dictionary<string, Texture2D> textures = new();
    private PatchAsset? board;
    private readonly HashSet<string> errors = new();
    private Harmony? harmony;

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            root = Path.GetDirectoryName(Info.Location)!;
            ClientSettings.Load(Path.GetFullPath(Path.Combine(root, "..", "..", "..")), Logger);
            WallTpl = Hash("jackalpatch|wall");
            EmptyMesh = new Mesh { name = "JackalPatch_empty", hideFlags = HideFlags.HideAndDontSave };
            foreach (var extra in new[] { "aggressive_4", "curve2", "hoot2", "totn" }) Order.Add(Hash("slickster|patch|" + extra));   // the Slickster's own four
            var indexPath = Path.Combine(root, "patches", "index.json");
            if (File.Exists(indexPath))
            {
                foreach (var e in JsonConvert.DeserializeObject<List<PatchEntry>>(File.ReadAllText(indexPath)) ?? new List<PatchEntry>())
                {
                    Patches[e.tpl] = e;
                    Order.Add(e.tpl);
                }
            }
            Instance = this;
            harmony = new Harmony(Id);
            // Every way the game makes a model of an item: stash and hideout, loose loot on a raid floor, and in-hand previews.
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    // Last, so a fitted patch is added after the Slickster mod has finished the rig. It claims every mesh it finds on the rig's loot model, ours included if ours is already there.
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)) { priority = Priority.Last });
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)));
            harmony.Patch(AccessTools.Method(typeof(HideoutAreaStashController), nameof(HideoutAreaStashController.PositionGameObject)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterPosition)));
            harmony.Patch(AccessTools.Method(typeof(EFT.UI.WeaponModding.WeaponPreview), nameof(EFT.UI.WeaponModding.WeaponPreview.PositionGameObject)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePreview)));
            harmony.Patch(AccessTools.Method(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableHideoutActions)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeHideoutActions)));
            Logger.LogInfo($"Jackal Patches ready: {Patches.Count} kit patches, wall {WallTpl}.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Logger.LogError("Jackal Patches unavailable; the donor look remains. " + error);
        }
    }

    internal static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2"))).Substring(0, 24);
    }

    internal string Folder => root;
    internal void Logger_Info(string text) => Logger.LogInfo(text);

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"Jackal Patches: {key}: {error}");
    }

    internal bool IsPatch(string tpl) => Order.Contains(tpl);

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        var p = Instance;
        if (p is null) return;
        var tpl = __0.StringTemplateId;
        if (p.Patches.ContainsKey(tpl) || tpl == p.WallTpl || (tpl == RigFitter.RigTpl && ClientSettings.Current.Rigs.Enabled)) __result = p.ApplyAsync(__result, __0, tpl);
    }

    // The same swap for the calls that return straight away, which is how loose loot on a raid floor is made.
    private static void AfterCreateNow(Item __0, GameObject? __result)
    {
        var p = Instance;
        if (p is null || __result == null) return;
        var tpl = __0.StringTemplateId;
        if (!p.Patches.TryGetValue(tpl, out var entry)) return;
        var state = __result.GetComponent<PatchVisualState>() ?? __result.AddComponent<PatchVisualState>();
        state.Restore();
        try { p.ApplyPatch(__result, entry, state); }
        catch (Exception error) { state.Restore(); p.ReportOnce(tpl, error); }
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item, string tpl)
    {
        var model = await task;
        if (model == null) return null;
        var state = model.GetComponent<PatchVisualState>() ?? model.AddComponent<PatchVisualState>();
        state.Restore();
        try
        {
            if (tpl == WallTpl) ApplyWall(model, state, (CompoundItem)item);
            else if (tpl == RigFitter.RigTpl) ApplyRig(model, state, (CompoundItem)item);
            else ApplyPatch(model, Patches[tpl], state);
        }
        catch (Exception error)
        {
            state.Restore();
            ReportOnce(tpl, error);
        }
        return model;
    }

    private void ApplyPatch(GameObject model, PatchEntry entry, PatchVisualState state)
    {
        var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
        if (filters.Length == 0) throw new InvalidDataException("Patch donor has no rigid renderer.");
        // The TATM donor has a base and an arm at different poses: the patch goes on the base, facing the way the item preview looks at it.
        var primary = filters.FirstOrDefault(f => f.name.IndexOf("_base", StringComparison.OrdinalIgnoreCase) >= 0) ?? filters[0];
        var asset = AssetFor(entry, filters[0].GetComponent<MeshRenderer>().sharedMaterial);
        foreach (var filter in filters)
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            state.Remember(renderer, filter);
            filter.sharedMesh = filter == primary ? asset.Mesh : EmptyMesh;
            renderer.sharedMaterials = asset.Materials;
        }
        state.RememberTransform(primary.transform);
        primary.transform.localPosition = Vector3.zero;
        primary.transform.localRotation = Quaternion.identity;
        foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
        // The inspect window turns an item around the donor's own pivot, which is set for the TATM mount and not for a patch.
        var pivot = model.GetComponent<PreviewPivot>();
        if (pivot != null)
        {
            state.RememberPivot(pivot);
            pivot.pivotPosition = model.transform.InverseTransformPoint(primary.transform.TransformPoint(asset.Mesh.bounds.center));
        }
    }

    private void ApplyWall(GameObject model, PatchVisualState state, CompoundItem wall)
    {
        var filter = model.GetComponentsInChildren<MeshFilter>(true).First(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null);
        var renderer = filter.GetComponent<MeshRenderer>();
        var donor = renderer.sharedMaterials.LastOrDefault(m => m != null) ?? throw new InvalidDataException("Wall donor has no material.");
        board ??= LoadBoard(donor);
        state.Remember(renderer, filter);
        filter.sharedMesh = board.Mesh;
        renderer.sharedMaterials = board.Materials;
        foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
        (model.GetComponent<PatchWallDisplay>() ?? model.AddComponent<PatchWallDisplay>()).Bind(filter.transform, wall);
    }

    internal readonly RigFitter Fitter = new();

    private void ApplyRig(GameObject model, PatchVisualState state, CompoundItem rig)
    {
        var dress = model.GetComponent<DressItem>();
        if (dress == null) return;
        var skins = dress.DressPrefab.GetComponentsInChildren<Skin>(true).Select(s => s.SkinnedMeshRenderer).Where(r => r != null).ToArray();
        var filters = dress.LootPrefab.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
        if (skins.Length == 0) return;
        var skin = skins.FirstOrDefault(r => r.name.EndsWith("lod0", StringComparison.OrdinalIgnoreCase)) ?? skins[0];
        var filter = filters.FirstOrDefault(f => f.name.EndsWith("lod0", StringComparison.OrdinalIgnoreCase)) ?? filters.FirstOrDefault();
        var mounts = model.GetComponent<RigMounts>() ?? model.AddComponent<RigMounts>();
        mounts.Bind(this, rig, skin, filter, state);
    }

    /// <summary>The parts a fitted patch is drawn with, for a rig mount.</summary>
    internal PatchAsset? RigAsset(PatchEntry entry, Material donor)
    {
        try { return AssetFor(entry, donor); }
        catch (Exception error) { ReportOnce("rig patch " + entry.tpl, error); return null; }
    }

    /// <summary>Checks every kit patch against both patch points, fitted or not, so the audit covers everything the player could fit. Read by the QA probe.</summary>
    public List<string> AuditAllRigFits(Material donor)
    {
        var lines = new List<string>();
        if (!Fitter.Prepare()) return lines;
        foreach (var entry in Patches.Values.OrderBy(e => e.n))
            foreach (var slot in RigFitter.Slots)
            {
                var asset = RigAsset(entry, donor);
                var fit = asset == null ? null : Fitter.Fit(entry, asset.Mesh, slot);
                if (fit != null) lines.Add(RigFitter.Line(entry, slot, fit));
            }
        Fitter.WriteReport(root);
        return lines;
    }

    /// <summary>Opens the player's normal inventory with the board's slot window on top, so patches can be dragged straight in.</summary>
    internal void OpenBoard(HideoutPlayerOwner owner, PatchWallDisplay display)
    {
        try
        {
            var wall = display.Wall;
            owner.HideoutPlayer.SetInventoryOpened(true);
            Singleton<HideoutRepresentation>.Instance.ShowInventoryScreen(() =>
            {
                owner.HideoutPlayer.SetInventoryOpened(false);
                owner.Player.UpdateInteractionCast();
            });
            // The board runs the wait: BepInEx's own manager object may be gone by now, and a plugin can't start coroutines without it.
            display.StartCoroutine(OpenWindowSoon(wall));
        }
        catch (Exception error) { ReportOnce("open patch board", error); }
    }

    private IEnumerator OpenWindowSoon(CompoundItem wall)
    {
        // The inventory screen sets up the item window system while it opens.
        yield return new WaitForSeconds(0.5f);
        try { ItemUiContext.Instance.OpenItem(wall, new DefaultItemContext(wall, EItemViewType.Inventory)); }
        catch (Exception error) { ReportOnce("open patch board window", error); }
    }

    // Must be a prefix: the original throws "No interactions defined" for any object type it does not know.
    private static bool BeforeHideoutActions(HideoutPlayerOwner owner, IInteractive interactive, ref AvailableInteractionState? __result)
    {
        if (interactive is not PatchBoardInteractive board) return true;
        __result = null;
        if (Instance is null || board.Display == null || !owner.AvailableForInteractions) return false;
        var state = new AvailableInteractionState();
        var display = board.Display;
        state.Actions.Add(new InteractionAction { Name = "Open patch board", Action = () => Instance.OpenBoard(owner, display) });
        state.InitSelected();
        __result = state;
        return false;
    }

    private PatchAsset AssetFor(PatchEntry entry, Material donor)
    {
        if (assets.TryGetValue(entry.tpl, out var found)) return found;
        var folder = Path.Combine(root, "patches", entry.n.ToString());
        var mesh = ReadMesh(Path.Combine(folder, "mesh.bin"), "JackalPatch_" + entry.n);
        // Each patch type carries its own surface numbers in the index (cloth, rubber, felt); older entries fall back to embroidered cloth.
        float frontSpec = entry.front_spec > 0 ? entry.front_spec : 1.6f, edgeSpec = entry.edge_spec > 0 ? entry.edge_spec : 1.6f, reflect = entry.reflect > 0 ? entry.reflect : 0.07f;
        var front = Fabric(donor, "patch" + entry.n + "_front", Path.Combine(folder, "front_a.png"), Path.Combine(folder, "front_n.png"), Path.Combine(folder, "front_s.png"), frontSpec, reflect);
        var edge = Fabric(donor, "patch" + entry.n + "_edge", Path.Combine(folder, "edge_a.png"), Path.Combine(folder, "edge_n.png"), Path.Combine(folder, "edge_s.png"), edgeSpec, reflect);
        var back = Fabric(donor, "velcro", Path.Combine(root, "shared", "velcro_a.png"), Path.Combine(root, "shared", "velcro_n.png"), Path.Combine(root, "shared", "velcro_s.png"), 0.8f);
        var asset = new PatchAsset(mesh, new[] { front, edge, back });
        // Patches made with a newer kit bring a much simpler copy of themselves for when they're far away. Without it the full model is simply used at every distance.
        try
        {
            var farPath = Path.Combine(folder, "mesh_far.bin");
            if (File.Exists(farPath)) asset.FarMesh = ReadMesh(farPath, "JackalPatch_" + entry.n + "_far");
        }
        catch (Exception error) { ReportOnce("far model of patch " + entry.n, error); }
        assets[entry.tpl] = asset;
        return asset;
    }

    /// <summary>The simple far-away model of a kit patch, if it has one and has already been loaded.</summary>
    internal Mesh? FarMeshFor(string tpl) => assets.TryGetValue(tpl, out var asset) ? asset.FarMesh : null;

    /// <summary>The film of an IR patch: just its front face, a hair above the cloth, drawn with an additive shader so it can flash.</summary>
    internal (Mesh mesh, Material material)? IrOverlay(PatchEntry entry, Mesh patch)
    {
        try
        {
            if (irOverlays.TryGetValue(entry.tpl, out var cached)) return cached;
            // Shader.Find misses shaders that only exist inside loaded bundles, so search those too.
            var shader = Shader.Find("Legacy Shaders/Particles/Additive") ?? Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(s => s != null && s.name == "Legacy Shaders/Particles/Additive");
            var map = Path.Combine(root, "patches", entry.n.ToString(), "front_ir.png");
            if (shader == null || !File.Exists(map) || !patch.isReadable)
            {
                ReportOnce("IR film unavailable", new InvalidOperationException($"shader={(shader != null)} map={File.Exists(map)} readable={patch.isReadable}"));
                return null;
            }
            // A plain grid over the patch, so a laser can light just the small spot it lands on. Its map is black outside the patch shape, so the corners add nothing.
            var indices = patch.GetIndices(0);
            var src = patch.vertices; var srcUv = patch.uv;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, topZ = float.MinValue;
            int atMinX = 0, atMaxX = 0, atMinY = 0, atMaxY = 0;
            foreach (int i in indices)
            {
                var p = src[i];
                if (p.x < minX) { minX = p.x; atMinX = i; }
                if (p.x > maxX) { maxX = p.x; atMaxX = i; }
                if (p.y < minY) { minY = p.y; atMinY = i; }
                if (p.y > maxY) { maxY = p.y; atMaxY = i; }
                topZ = Mathf.Max(topZ, p.z);
            }
            const int Cells = 30;
            var verts = new Vector3[(Cells + 1) * (Cells + 1)]; var uvs = new Vector2[verts.Length]; var tris = new int[Cells * Cells * 6];
            for (int y = 0, v = 0; y <= Cells; y++)
                for (int x = 0; x <= Cells; x++, v++)
                {
                    float fx = (float)x / Cells, fy = (float)y / Cells;
                    verts[v] = new Vector3(Mathf.Lerp(minX, maxX, fx), Mathf.Lerp(minY, maxY, fy), topZ + 0.00025f);
                    uvs[v] = new Vector2(Mathf.Lerp(srcUv[atMinX].x, srcUv[atMaxX].x, fx), Mathf.Lerp(srcUv[atMinY].y, srcUv[atMaxY].y, fy));
                }
            for (int y = 0, t = 0; y < Cells; y++)
                for (int x = 0; x < Cells; x++, t += 6)
                {
                    int a = y * (Cells + 1) + x, b = a + 1, c = a + Cells + 1, d = c + 1;
                    tris[t] = a; tris[t + 1] = c; tris[t + 2] = b; tris[t + 3] = b; tris[t + 4] = c; tris[t + 5] = d;
                }
            var mesh = new Mesh { name = "JackalPatch_ir_" + entry.n, indexFormat = UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.vertices = verts; mesh.uv = uvs; mesh.triangles = tris; mesh.colors32 = new Color32[verts.Length]; mesh.RecalculateBounds();
            var material = new Material(shader) { name = "JackalPatch_ir_film" };
            material.SetTexture("_MainTex", Load(map, false, false));
            material.SetTextureScale("_MainTex", Vector2.one);
            material.SetColor("_TintColor", Color.clear);
            var result = (mesh, material);
            irOverlays[entry.tpl] = result;
            return result;
        }
        catch (Exception error) { ReportOnce("IR film", error); return null; }
    }

    private readonly Dictionary<string, (Mesh mesh, Material material)> irOverlays = new();

    private PatchAsset LoadBoard(Material donor)
    {
        var mesh = ReadMesh(Path.Combine(root, "shared", "board.bin"), "JackalPatch_board");
        string S(string file) => Path.Combine(root, "shared", file);
        var colour = ClientSettings.Current.Board.Colour;
        var loop = Fabric(donor, "board_" + colour, S($"board_{colour}_a.png"), S("board_n.png"), S("board_s.png"), 0.8f, 0.04f);
        var metal = Fabric(donor, "grommet", S("metal_a.png"), S("metal_n.png"), S("metal_s.png"), 2.4f, 0.4f);
        var hole = new Material(metal) { name = "JackalPatch_grommet_hole" };   // the shadowed centre of a grommet
        if (hole.HasProperty("_Color")) hole.SetColor("_Color", new Color(0.05f, 0.05f, 0.05f));
        if (hole.HasProperty("_Specularness")) hole.SetFloat("_Specularness", 0.1f);
        return new PatchAsset(mesh, new[] { loop, metal, hole });
    }

    private readonly Dictionary<string, Material> sharedMaterials = new();

    private Material Fabric(Material donor, string key, string albedo, string normal, string gloss, float specularness, float reflect = 0.07f)
    {
        if (sharedMaterials.TryGetValue(key, out var cached)) return cached;
        var m = new Material(donor) { name = "JackalPatch_" + key };
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
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(reflect, reflect, reflect, 0.5f));   // cloth or rubber, not the donor's metal
        sharedMaterials[key] = m;
        return m;
    }

    private Texture2D Load(string path, bool linear, bool isNormal)
    {
        if (textures.TryGetValue(path, out var cached)) return cached;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "JackalPatch_" + Path.GetFileNameWithoutExtension(path) };
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
        mesh.UploadMeshData(false);   // stays readable so the wall can size and turn patches
        return mesh;
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<PatchVisualState>()?.Restore();
    }

    // The inspect window is about to place an item. By now the model is finished, so this also centres the Slickster's patches, which carry the same donor's pivot.
    private static void BeforePreview(EFT.UI.WeaponModding.WeaponPreview __instance, GameObject itemGameObject)
    {
        try
        {
            if (Instance is null || itemGameObject == null) return;
            var item = Traverse.Create(__instance).Field("_currentItem").GetValue<Item>();
            if (item == null || !Instance.IsPatch(item.StringTemplateId)) return;
            var pivot = itemGameObject.GetComponent<PreviewPivot>();
            if (pivot == null) return;
            var bounds = EFT.UI.WeaponModding.WeaponPreview.GetBounds(itemGameObject);
            if (bounds.size.sqrMagnitude <= 0f) return;
            (itemGameObject.GetComponent<PatchVisualState>() ?? itemGameObject.AddComponent<PatchVisualState>()).RememberPivot(pivot);
            pivot.pivotPosition = itemGameObject.transform.InverseTransformPoint(bounds.center);
        }
        catch (Exception error) { Instance?.ReportOnce("inspect centring", error); }
    }

    // The game just placed a stash model on a hideout spot. A container item uses the generic stash model, which lifts it by half its height; a poster does not.
    private static void AfterPosition(StashItemModel model)
    {
        try
        {
            if (Instance is null || model == null) return;
            var display = model.GetComponentInChildren<PatchWallDisplay>(true);
            if (display == null) return;
            var pivot = Traverse.Create(model).Field("_rotationPivot").GetValue<Transform>();
            if (pivot != null) pivot.localPosition = Vector3.zero;
            display.OnPlaced();
        }
        catch (Exception error) { Instance?.ReportOnce("wall placement", error); }
    }

    public sealed class PatchEntry { public int n; public string name = ""; public string tpl = ""; public string offer = ""; public float size_m; public string style = ""; public float front_spec, edge_spec, reflect; public bool ir; }
    internal sealed class PatchAsset { public Mesh Mesh; public Mesh? FarMesh; public Material[] Materials; public PatchAsset(Mesh mesh, Material[] materials) { Mesh = mesh; Materials = materials; } }
}

/// <summary>What the clip check found for one patch on one patch point.</summary>
internal sealed class RigFit
{
    public Mesh Mesh = null!;
    public float Scale, GapMm;
    public int Samples, ClipPoints, Overhanging;
    public bool Fits, Reported;
}

/// <summary>
/// Places patches on the Slickster carrier's patch points and checks them against the rig's real shape. The point and its direction come from where that mod's own patches sit;
/// a patch is then lifted clear of whatever is under its outline, and shrunk until it sits on a reasonably flat area. A final, separate pass over the finished patch counts anything still inside the rig.
/// </summary>
internal sealed class RigFitter
{
    public const string RigTpl = "d2bdedaf3de0b53aa9e7a081";
    public static readonly string[] Slots = { "mod_equipment_000", "mod_equipment_001" };
    public static string SlotName(string slot) => slot == Slots[0] ? "chest" : "dock";

    private sealed class Point { public Vector3 Centre, Right, Up, Normal; public BoneWeight Weight; }
    private readonly Dictionary<string, Point> points = new();
    private readonly Dictionary<(string, string), RigFit> fits = new();
    private readonly Dictionary<(string, string), string> report = new();
    private Vector3[] verts = Array.Empty<Vector3>();
    private int[] rigTriangles = Array.Empty<int>();
    private Matrix4x4[] bindposes = Array.Empty<Matrix4x4>();
    private bool ready, tried;

    /// <summary>Reads the carrier's shape from the Slickster mod. False if that mod isn't there or has changed, and then patches simply don't show on the rig.</summary>
    public bool Prepare()
    {
        if (tried) return ready;
        tried = true;
        try
        {
            var type = AccessTools.TypeByName("Slickster.Client.Plugin");
            var mod = type == null ? null : Traverse.Create(type).Field("instance").GetValue();
            if (mod == null) return false;
            var plugin = Traverse.Create(mod);
            var carrier = (plugin.Field("carriers").GetValue() as System.Collections.IDictionary)?[RigTpl];
            if (carrier == null) return false;
            var lod = (Traverse.Create(carrier).Field("lods").GetValue() as System.Collections.IDictionary)?["0"];
            var mesh = (plugin.Field("meshes").GetValue() as System.Collections.IDictionary)?[Traverse.Create(lod).Field("mesh").GetValue<string>()] as Mesh;
            var patches = plugin.Field("patches").GetValue() as System.Collections.IDictionary;
            if (mesh == null || patches == null || !mesh.isReadable) return false;
            verts = mesh.vertices;
            var uv = mesh.uv;
            var weights = mesh.boneWeights;
            bindposes = mesh.bindposes;
            var perSlot = Slots.ToDictionary(s => s, s => new List<int>());
            var patchSubs = new HashSet<int>();
            foreach (var row in patches.Values)
            {
                var subs = Traverse.Create(row).Field("submeshes").GetValue<Dictionary<string, int>>();
                foreach (var pair in subs)
                {
                    patchSubs.Add(pair.Value);
                    if (perSlot.TryGetValue(pair.Key, out var list)) list.AddRange(mesh.GetIndices(pair.Value));
                }
            }
            var rest = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) if (!patchSubs.Contains(s)) rest.AddRange(mesh.GetIndices(s));
            rigTriangles = rest.ToArray();
            foreach (var slot in Slots)
            {
                var tris = perSlot[slot];
                if (tris.Count < 3) continue;
                // The art's own axes on the rig: U runs to the viewer's right and V runs up, whichever way the triangles are wound.
                Vector3 rightSum = Vector3.zero, upSum = Vector3.zero;
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    Vector3 e1 = verts[tris[t + 1]] - verts[tris[t]], e2 = verts[tris[t + 2]] - verts[tris[t]];
                    float du1 = uv[tris[t + 1]].x - uv[tris[t]].x, dv1 = uv[tris[t + 1]].y - uv[tris[t]].y, du2 = uv[tris[t + 2]].x - uv[tris[t]].x, dv2 = uv[tris[t + 2]].y - uv[tris[t]].y;
                    float det = du1 * dv2 - du2 * dv1;
                    if (Mathf.Abs(det) < 1e-9f) continue;
                    float area = Vector3.Cross(e1, e2).magnitude;
                    rightSum += (e1 * dv2 - e2 * dv1).normalized * area * Mathf.Sign(det);
                    upSum += (e2 * du1 - e1 * du2).normalized * area * Mathf.Sign(det);
                }
                var up = upSum.normalized;
                var normal = Vector3.Cross(up, rightSum.normalized).normalized;
                var right = Vector3.Cross(normal, up).normalized;
                float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
                var origin = verts[tris[0]];
                foreach (int i in tris)
                {
                    var d = verts[i] - origin;
                    float a = Vector3.Dot(d, right), b = Vector3.Dot(d, up);
                    minA = Mathf.Min(minA, a); maxA = Mathf.Max(maxA, a); minB = Mathf.Min(minB, b); maxB = Mathf.Max(maxB, b);
                }
                var centre = origin + right * ((minA + maxA) * 0.5f) + up * ((minB + maxB) * 0.5f);
                // One bone weight for the whole patch, so it moves as a stiff panel with the chest instead of warping.
                int nearest = 0; float best = float.MaxValue;
                foreach (int i in rigTriangles)
                {
                    float dist = (verts[i] - centre).sqrMagnitude;
                    if (dist < best) { best = dist; nearest = i; }
                }
                points[slot] = new Point { Centre = centre, Right = right, Up = up, Normal = normal, Weight = weights[nearest] };
            }
            ready = points.Count == Slots.Length;
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("reading the carrier's patch points", error); }
        return ready;
    }

    /// <summary>The highest point of the rig directly under one spot of the patch, or NaN if the rig doesn't reach there.</summary>
    private static float RigHeight(float a, float b, Vector3[] local, int[] nearby)
    {
        float top = float.NaN;
        for (int t = 0; t + 2 < nearby.Length; t += 3)
        {
            Vector3 p0 = local[nearby[t]], p1 = local[nearby[t + 1]], p2 = local[nearby[t + 2]];
            float denom = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
            if (Mathf.Abs(denom) < 1e-12f) continue;
            float w0 = ((p1.y - p2.y) * (a - p2.x) + (p2.x - p1.x) * (b - p2.y)) / denom;
            float w1 = ((p2.y - p0.y) * (a - p2.x) + (p0.x - p2.x) * (b - p2.y)) / denom;
            float w2 = 1f - w0 - w1;
            if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
            float h = w0 * p0.z + w1 * p1.z + w2 * p2.z;
            if (float.IsNaN(top) || h > top) top = h;
        }
        return top;
    }

    public RigFit? Fit(Plugin.PatchEntry entry, Mesh patch, string slot)
    {
        if (!Prepare() || !points.TryGetValue(slot, out var point)) return null;
        var key = (entry.tpl, slot);
        if (fits.TryGetValue(key, out var known)) return known;
        var settings = ClientSettings.Current.Rigs;
        // The rig in the patch point's own axes: x right, y up, z out of the chest.
        var local = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++) { var d = verts[i] - point.Centre; local[i] = new Vector3(Vector3.Dot(d, point.Right), Vector3.Dot(d, point.Up), Vector3.Dot(d, point.Normal)); }
        var source = patch.vertices;
        var centre = patch.bounds.center;
        float backZ = patch.bounds.min.z, reach = patch.bounds.extents.magnitude + 0.03f;
        // Only triangles near the point, and nothing on the far side of the body.
        var near = new List<int>();
        for (int t = 0; t + 2 < rigTriangles.Length; t += 3)
        {
            Vector3 a = local[rigTriangles[t]], b = local[rigTriangles[t + 1]], c = local[rigTriangles[t + 2]];
            if (Mathf.Max(a.z, b.z, c.z) < -0.06f) continue;
            if (Mathf.Min(Mathf.Abs(a.x), Mathf.Abs(b.x), Mathf.Abs(c.x)) > reach || Mathf.Min(Mathf.Abs(a.y), Mathf.Abs(b.y), Mathf.Abs(c.y)) > reach) continue;
            near.Add(rigTriangles[t]); near.Add(rigTriangles[t + 1]); near.Add(rigTriangles[t + 2]);
        }
        var nearby = near.ToArray();
        int stride = Mathf.Max(1, source.Length / 260);
        var outline = new List<Vector2>();
        for (int i = 0; i < source.Length; i += stride) outline.Add(new Vector2(source[i].x - centre.x, source[i].y - centre.y));
        float smallest = settings.SmallestScalePercent / 100f, scale = 1f, top = 0f, gap = 0f;
        int covered = 0;
        bool fits_ = false;
        for (; ; scale *= 0.95f)
        {
            float high = float.MinValue, low = float.MaxValue;
            covered = 0;
            foreach (var v in outline)
            {
                float h = RigHeight(-v.x * scale, v.y * scale, local, nearby);   // the art's right is the viewer's right, which is -x on the patch mesh
                if (float.IsNaN(h)) continue;
                covered++;
                high = Mathf.Max(high, h); low = Mathf.Min(low, h);
            }
            top = covered > 0 ? high : 0f;
            gap = covered > 0 ? (high - low) * 1000f : 0f;
            fits_ = covered >= outline.Count * 0.85f && gap <= settings.MaxGapMm;
            if (fits_ || scale * 0.95f < smallest) break;
        }
        float lift = top + settings.StandoffMm / 1000f;
        var verticesOut = new Vector3[source.Length];
        var normalsOut = new Vector3[source.Length];
        var normals = patch.normals;
        for (int i = 0; i < source.Length; i++)
        {
            var v = source[i];
            verticesOut[i] = point.Centre + point.Right * (-(v.x - centre.x) * scale) + point.Up * ((v.y - centre.y) * scale) + point.Normal * (lift + (v.z - backZ) * scale);
            var n = normals[i];
            normalsOut[i] = (point.Right * -n.x + point.Up * n.y + point.Normal * n.z).normalized;
        }
        // The separate check: every point of the finished patch against the rig, counting any that ended up inside it.
        int clipping = 0, hanging = 0, samples = 0;
        for (int i = 0; i < source.Length; i += stride)
        {
            var d = verticesOut[i] - point.Centre;
            float h = RigHeight(Vector3.Dot(d, point.Right), Vector3.Dot(d, point.Up), local, nearby);
            samples++;
            if (float.IsNaN(h)) hanging++;
            else if (Vector3.Dot(d, point.Normal) < h - 0.0001f) clipping++;
        }
        var mesh = new Mesh { name = $"JackalPatch_rig_{entry.n}_{SlotName(slot)}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = verticesOut; mesh.normals = normalsOut; mesh.uv = patch.uv;
        mesh.subMeshCount = patch.subMeshCount;
        for (int s = 0; s < patch.subMeshCount; s++) mesh.SetTriangles(patch.GetIndices(s), s, false);
        var weights = new BoneWeight[verticesOut.Length];
        for (int i = 0; i < weights.Length; i++) weights[i] = point.Weight;
        mesh.boneWeights = weights;
        mesh.bindposes = bindposes;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(true);
        var fit = new RigFit { Mesh = mesh, Scale = scale, GapMm = gap, Samples = samples, ClipPoints = clipping, Overhanging = hanging, Fits = fits_ };
        fits[key] = fit;
        report[key] = Line(entry, slot, fit);
        return fit;
    }

    public static string Line(Plugin.PatchEntry entry, string slot, RigFit fit) =>
        $"{entry.name} on the {SlotName(slot)}: {(fit.ClipPoints > 0 ? "CLIPS" : fit.Fits ? "fits" : "fits only roughly")} at {fit.Scale * 100f:0}% size, {fit.ClipPoints} of {fit.Samples} points inside the rig, up to {fit.GapMm:0.0} mm of gap, {fit.Overhanging} points past the edge";

    /// <summary>A plain text list of every patch checked so far, beside the mod's files.</summary>
    public void WriteReport(string folder)
    {
        try { File.WriteAllLines(Path.Combine(folder, "rig_fit_report.txt"), report.Values.OrderBy(l => l)); }
        catch (Exception error) { Plugin.Instance?.ReportOnce("writing the rig report", error); }
    }
}

/// <summary>Shows the patches fitted in a carrier's two slots, following the rig. Added and removed along with the items in those slots.</summary>
public sealed class RigMounts : MonoBehaviour
{
    private readonly Dictionary<string, List<GameObject>> shown = new();
    private readonly List<(SkinnedMeshRenderer ours, SkinnedMeshRenderer source)> skins = new();
    private Plugin plugin = null!;
    private SkinnedMeshRenderer skin = null!;
    private MeshFilter? loot;
    private Slot[] slots = Array.Empty<Slot>();
    private float nextBones;

    internal void Bind(Plugin owner, CompoundItem rig, SkinnedMeshRenderer rigSkin, MeshFilter? lootFilter, PatchVisualState state)
    {
        plugin = owner; skin = rigSkin; loot = lootFilter;
        slots = rig.Containers.OfType<Slot>().Where(s => RigFitter.Slots.Contains(s.ID)).ToArray();
        foreach (var slot in slots)
        {
            var current = slot;
            Action<Item> changed = _ => Show(current);
            slot.OnAddOrRemoveItem += changed;
            state.OnRestore(() => slot.OnAddOrRemoveItem -= changed);
            Show(slot);
        }
        state.OnRestore(ClearAll);
    }

    private void ClearAll()
    {
        foreach (var list in shown.Values) foreach (var go in list) Remove(go);
        shown.Clear(); skins.Clear();
    }

    // Unity only really destroys at the end of the frame. The inspect window can get this same rig back from the pool before then, so the old patch is taken out of the rig straight away.
    private static void Remove(GameObject? go)
    {
        if (go == null) return;
        go.SetActive(false);
        go.transform.SetParent(null, false);
        Destroy(go);
    }

    private void Show(Slot slot)
    {
        try
        {
            if (shown.TryGetValue(slot.ID, out var old))
            {
                foreach (var go in old) { skins.RemoveAll(s => s.ours != null && s.ours.gameObject == go); Remove(go); }
                shown.Remove(slot.ID);
            }
            var item = slot.ContainedItem;
            if (item == null || !plugin.Patches.TryGetValue(item.StringTemplateId, out var entry) || skin == null) return;
            var donor = skin.sharedMaterial;
            var asset = donor == null ? null : plugin.RigAsset(entry, donor);
            var fit = asset == null ? null : plugin.Fitter.Fit(entry, asset.Mesh, slot.ID);
            if (fit == null) return;
            if (!fit.Reported)
            {
                // Once per patch and point: the window rebuilds the rig every time anything on it changes.
                fit.Reported = true;
                plugin.Logger_Info(RigFitter.Line(entry, slot.ID, fit));
                plugin.Fitter.WriteReport(plugin.Folder);
            }
            var made = new List<GameObject>();
            var worn = new GameObject("JackalPatch_on_rig_" + RigFitter.SlotName(slot.ID));
            worn.layer = skin.gameObject.layer;
            worn.transform.SetParent(skin.transform.parent, false);
            var ours = worn.AddComponent<SkinnedMeshRenderer>();
            ours.sharedMesh = fit.Mesh; ours.sharedMaterials = asset!.Materials; ours.localBounds = fit.Mesh.bounds; ours.updateWhenOffscreen = true;
            ours.bones = skin.bones; ours.rootBone = skin.rootBone;
            skins.Add((ours, skin)); made.Add(worn);
            if (loot != null)
            {
                var ground = new GameObject("JackalPatch_on_rig_loot_" + RigFitter.SlotName(slot.ID));
                ground.layer = loot.gameObject.layer;
                ground.transform.SetParent(loot.transform, false);
                ground.AddComponent<MeshFilter>().sharedMesh = fit.Mesh;
                ground.AddComponent<MeshRenderer>().sharedMaterials = asset.Materials;
                made.Add(ground);
            }
            shown[slot.ID] = made;
        }
        catch (Exception error) { plugin.ReportOnce("rig patch display", error); }
    }

    // The game turns the rig off for first person and changes its bones when it's put on, so each patch copies whatever the rig is doing.
    private void LateUpdate()
    {
        bool refresh = Time.unscaledTime >= nextBones;
        if (refresh) nextBones = Time.unscaledTime + 0.5f;
        foreach (var (ours, source) in skins)
        {
            if (ours == null) continue;
            if (source == null) { ours.enabled = false; continue; }
            ours.enabled = source.enabled;
            ours.forceRenderingOff = source.forceRenderingOff;
            ours.shadowCastingMode = source.shadowCastingMode;
            if (ours.gameObject.layer != source.gameObject.layer) ours.gameObject.layer = source.gameObject.layer;
            if (refresh && (ours.rootBone != source.rootBone || !BonesMatch(ours, source))) { ours.bones = source.bones; ours.rootBone = source.rootBone; }
        }
    }

    private static bool BonesMatch(SkinnedMeshRenderer a, SkinnedMeshRenderer b)
    {
        var x = a.bones; var y = b.bones;
        return x.Length == y.Length && (x.Length == 0 || x[0] == y[0]);
    }
}

/// <summary>The board and IR parts of the mod's one config.json, which lives with the server mod. Any problem with the file leaves the normal values in place.</summary>
internal sealed class ClientSettings
{
    internal static ClientSettings Current = new();

    [JsonProperty("board")] public BoardSettings Board = new();
    [JsonProperty("ir")] public IrSettings Ir = new();
    [JsonProperty("rigs")] public RigSettings Rigs = new();

    internal sealed class RigSettings
    {
        [JsonProperty("fitOnSlicksterCarrier")] public bool Enabled = true;
        [JsonProperty("maxGapMm")] public float MaxGapMm = 5f;
        [JsonProperty("smallestScalePercent")] public float SmallestScalePercent = 40f;
        [JsonProperty("standoffMm")] public float StandoffMm = 0.6f;
    }

    internal sealed class BoardSettings
    {
        [JsonProperty("colour")] public string Colour = "ranger";
        [JsonProperty("maxTiltDegrees")] public float MaxTilt = 7f;
        [JsonProperty("nameTapeTiltDegrees")] public float NameTapeTilt = 2.5f;
        [JsonProperty("gapCm")] public float GapCm = 1.2f;
        [JsonProperty("smallestScalePercent")] public float SmallestScalePercent = 20f;
        [JsonProperty("simpleModelsFarAway")] public bool FarModels = true;
        [JsonProperty("simpleModelBelowScreenPercent")] public float FarModelBelowScreenPercent = 6f;
    }

    internal sealed class IrSettings
    {
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("brightness")] public float Brightness = 0.9f;
        [JsonProperty("nvgLightRangeMeters")] public float NvgLightRange = 12f;
        [JsonProperty("laserRangeMeters")] public float LaserRange = 100f;
        [JsonProperty("returnConeDegrees")] public float ReturnCone = 1.1f;
        [JsonProperty("sideScatter")] public float SideScatter = 0.1f;
        [JsonProperty("brightnessRangeMeters")] public float BrightnessRange = 30f;
        [JsonProperty("fadeStartDegrees")] public float FadeStart = 40f;
        [JsonProperty("fadeEndDegrees")] public float FadeEnd = 80f;
    }

    internal static void Load(string gameRoot, BepInEx.Logging.ManualLogSource log)
    {
#if WWC
        var path = Path.Combine(gameRoot, "SPT_Runtime", "user", "mods", "Widowz-WWC.Server", "Patches", "config.json");
#else
        var path = Path.Combine(gameRoot, "SPT_Runtime", "user", "mods", "Widowz-JackalPatches.Server", "config.json");
#endif
        try
        {
            Current = (File.Exists(path) ? JsonConvert.DeserializeObject<ClientSettings>(File.ReadAllText(path)) : null) ?? new ClientSettings();
        }
        catch (Exception error)
        {
            Current = new ClientSettings();
            log.LogWarning("config.json could not be read, so the board and IR film use their normal values. " + error.Message);
        }
        Current.Tidy();
        log.LogInfo($"Settings: board {Current.Board.Colour}, gap {Current.Board.GapCm:0.##} cm, tilt {Current.Board.MaxTilt:0.##}, IR {(Current.Ir.Enabled ? "on" : "off")} at {Current.Ir.Brightness:0.##}");
    }

    private void Tidy()
    {
        Board ??= new BoardSettings();
        Ir ??= new IrSettings();
        Rigs ??= new RigSettings();
        Rigs.MaxGapMm = Mathf.Clamp(Rigs.MaxGapMm, 0.5f, 50f);
        Rigs.SmallestScalePercent = Mathf.Clamp(Rigs.SmallestScalePercent, 10f, 100f);
        Rigs.StandoffMm = Mathf.Clamp(Rigs.StandoffMm, 0f, 10f);
        var colour = (Board.Colour ?? "").Trim().ToLowerInvariant();
        Board.Colour = colour is "coyote" or "black" ? colour : "ranger";
        Board.MaxTilt = Mathf.Clamp(Board.MaxTilt, 0f, 45f);
        Board.NameTapeTilt = Mathf.Clamp(Board.NameTapeTilt, 0f, 45f);
        Board.GapCm = Mathf.Clamp(Board.GapCm, 0f, 10f);
        Board.SmallestScalePercent = Mathf.Clamp(Board.SmallestScalePercent, 5f, 100f);
        Board.FarModelBelowScreenPercent = Mathf.Clamp(Board.FarModelBelowScreenPercent, 0.5f, 50f);
        Ir.Brightness = Mathf.Clamp(Ir.Brightness, 0f, 2f);
        Ir.NvgLightRange = Mathf.Clamp(Ir.NvgLightRange, 0f, 500f);
        Ir.LaserRange = Mathf.Clamp(Ir.LaserRange, 0f, 2000f);
        Ir.ReturnCone = Mathf.Clamp(Ir.ReturnCone, 0.05f, 90f);
        Ir.SideScatter = Mathf.Clamp01(Ir.SideScatter);
        Ir.BrightnessRange = Mathf.Clamp(Ir.BrightnessRange, 0.1f, 5000f);
        Ir.FadeStart = Mathf.Clamp(Ir.FadeStart, 0f, 89f);
        Ir.FadeEnd = Mathf.Clamp(Ir.FadeEnd, Ir.FadeStart + 0.1f, 90f);
    }
}

/// <summary>
/// A hung IR patch, behaving like real retroreflective IR film (the kind on IR flags and glint tape):
/// dull cloth to the naked eye; through night vision it returns IR light back toward wherever that light came from. So it is brightest for someone standing at the source
/// (a helmet illuminator, an NVG's own light), falls off fast as the viewer moves away from the beam (a sharp cone of about a degree, with a faint scatter left over), fades at very
/// shallow angles, and a laser lights only the small spot it actually lands on.
/// </summary>
public sealed class IrReflectivePatch : MonoBehaviour
{
    private static readonly List<IrReflectivePatch> All = new();
    private static IkLight[] ikLights = Array.Empty<IkLight>();
    private static LaserBeam[] lasers = Array.Empty<LaserBeam>();
    private static NightVisionDevice[] devices = Array.Empty<NightVisionDevice>();
    private static float nextScan;
    private static bool hooked;
    private static int blockMask;
    private static readonly int TintId = Shader.PropertyToID("_TintColor");
    private MeshRenderer overlay = null!;
    private MaterialPropertyBlock block = null!;
    private Mesh film = null!;
    private Vector3[] verts = null!;
    private Color32[] colors = null!;
    private readonly List<(Vector3 local, float radius, float power)> spots = new();
    private float strength, nextSample;
    private float halfSize;

    /// <summary>How brightly it is reflecting right now, 0 when nothing IR reaches it. Read by the QA probe.</summary>
    public float Strength => strength;

    public static void Attach(GameObject hungPatch, Mesh overlayMesh, Material material)
    {
        var filmGo = new GameObject("JackalPatch_ir_film");
        filmGo.layer = hungPatch.layer;
        filmGo.transform.SetParent(hungPatch.transform, false);
        var mesh = Instantiate(overlayMesh);          // its own copy, because each patch's lit spot is different
        mesh.name = overlayMesh.name + "_film";
        filmGo.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = filmGo.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.enabled = false;
        var patch = hungPatch.AddComponent<IrReflectivePatch>();
        patch.overlay = renderer;
        patch.block = new MaterialPropertyBlock();
        patch.film = mesh;
        patch.verts = mesh.vertices;
        patch.colors = new Color32[patch.verts.Length];
        patch.halfSize = overlayMesh.bounds.extents.magnitude * hungPatch.transform.lossyScale.x;
    }

    private void OnDestroy()
    {
        if (film != null) Destroy(film);
    }

    private void OnEnable()
    {
        All.Add(this);
        if (!hooked)
        {
            hooked = true;
            blockMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain");
            Camera.onPreCull = (Camera.CameraCallback)Delegate.Combine(Camera.onPreCull, new Camera.CameraCallback(BeforeCameraDraws));
        }
    }

    private void OnDisable()
    {
        All.Remove(this);
        if (overlay != null) overlay.enabled = false;
        strength = 0f;
    }

    private static void Rescan()
    {
        nextScan = Time.unscaledTime + 1f;
        ikLights = FindObjectsOfType<IkLight>();
        lasers = FindObjectsOfType<LaserBeam>().Where(l => l.BeamMaterial != null && l.BeamMaterial.name.IndexOf("Ik", StringComparison.Ordinal) >= 0).ToArray();
        devices = FindObjectsOfType<NightVisionDevice>();
    }

    // IR is invisible unless the camera drawing right now is looking through night vision, so the film is switched per camera, as the game does for its IR lights.
    private static void BeforeCameraDraws(Camera cam)
    {
        try
        {
            if (cam == null || All.Count == 0 || !CameraManager.Exist) return;
            var manager = CameraManager.Instance;
            bool main = cam == manager.Camera;
            if (!main) return;
            bool nightVision = manager.NightVision != null && manager.NightVision.On;
            if (nightVision && Time.unscaledTime >= nextScan) Rescan();
            foreach (var patch in All) patch.Present(cam, nightVision);
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("IR reflection", error); }
    }

    private void Present(Camera cam, bool nightVision)
    {
        if (!nightVision || overlay == null) { strength = 0f; if (overlay != null) overlay.enabled = false; return; }
        if (Time.unscaledTime >= nextSample)
        {
            nextSample = Time.unscaledTime + 0.1f;
            Measure(cam.transform.position);
        }
        overlay.enabled = strength > 0.01f;
        if (!overlay.enabled) return;
        var brightness = ClientSettings.Current.Ir.Brightness;
        block.SetColor(TintId, new Color(brightness, brightness, brightness, 1f));
        overlay.SetPropertyBlock(block);
    }

    private void Measure(Vector3 eye)
    {
        var centre = transform.position;
        var facing = transform.forward;
        float wash = 0f;
        spots.Clear();
        foreach (var ik in ikLights)
        {
            if (ik == null || !ik.isActiveAndEnabled) continue;
            var light = ik.Light;
            if (light != null && light.isActiveAndEnabled) wash += FromLight(light, centre, facing, eye);
        }
        // A worn NVG's built-in IR illuminator. The game only gives it a cosmetic 5 cm light, so use the working range of a real one instead.
        foreach (var device in devices)
            if (device != null && device._light != null && device._light.isActiveAndEnabled) wash += FromLight(device._light, centre, facing, eye, ClientSettings.Current.Ir.NvgLightRange);
        foreach (var laser in lasers)
        {
            if (laser == null || !laser.isActiveAndEnabled) continue;
            FromLaser(laser.transform, centre, facing, eye);
        }
        wash = Mathf.Clamp01(wash);
        float peak = wash;
        for (int i = 0; i < verts.Length; i++)
        {
            float v = wash;
            foreach (var (local, radius, power) in spots)
            {
                var d = verts[i] - local;
                d.z = 0f;
                v += power * Mathf.Exp(-d.sqrMagnitude / (radius * radius));
            }
            v = Mathf.Clamp01(v);
            peak = Mathf.Max(peak, v);
            byte b = (byte)(v * 255f);
            colors[i] = new Color32(b, b, b, b);
        }
        film.colors32 = colors;
        strength = peak;
    }

    private float FromLight(Light light, Vector3 centre, Vector3 facing, Vector3 eye, float minimumRange = 1f)
    {
        var from = light.transform.position;
        var toPatch = centre - from;
        float distance = toPatch.magnitude;
        if (distance > Mathf.Max(light.range, minimumRange) || distance < 1e-3f) return 0f;
        var dir = toPatch / distance;
        float beam = 1f;
        if (light.type == LightType.Spot)
        {
            float half = light.spotAngle * 0.5f, off = Vector3.Angle(light.transform.forward, dir);
            if (off > half) return 0f;
            beam = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(half * 0.7f, half, off));   // the soft edge of the beam
        }
        float angle = Entrance(facing, dir);
        if (angle <= 0f) return 0f;
        if (Physics.Linecast(from, centre + facing * 0.01f, blockMask)) return 0f;
        return Retro(from, centre, eye) * angle * beam * Spread(distance);
    }

    private void FromLaser(Transform laser, Vector3 centre, Vector3 facing, Vector3 eye)
    {
        var ray = new Ray(laser.position, laser.forward);
        float denom = Vector3.Dot(ray.direction, facing);
        if (denom > -1e-3f) return;
        float t = Vector3.Dot(centre - ray.origin, facing) / denom;
        if (t <= 0f || t > ClientSettings.Current.Ir.LaserRange) return;
        var hit = ray.GetPoint(t);
        if (Vector3.Distance(hit, centre) > halfSize) return;
        float angle = Entrance(facing, ray.direction);
        if (angle <= 0f) return;
        if (Physics.Linecast(ray.origin, hit - ray.direction * 0.01f, blockMask)) return;
        // A laser dot is small: a few millimetres at the emitter, spreading by about a millimetre per metre.
        float world = 0.006f + t * 0.001f;
        spots.Add((film != null ? overlay.transform.InverseTransformPoint(hit) : Vector3.zero, world / Mathf.Max(transform.lossyScale.x, 1e-4f),
                   Retro(ray.origin, hit, eye) * angle));
    }

    // Retroreflective film works best near head-on and loses its return at shallow angles.
    private static float Entrance(Vector3 facing, Vector3 lightDirection)
    {
        float angle = Vector3.Angle(facing, -lightDirection);
        var ir = ClientSettings.Current.Ir;
        return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ir.FadeStart, ir.FadeEnd, angle));
    }

    // Retroreflection keeps its punch at range, unlike ordinary reflected light, so this falls off only gently.
    private static float Spread(float distance)
    {
        var range = ClientSettings.Current.Ir.BrightnessRange;
        return 1f / (1f + distance * distance / (range * range));
    }

    // The defining property: the return goes back along the incoming beam. Real film loses over half its return by about half a degree, measured from the source,
    // so someone beside the beam sees only faint scatter. The returning cone is widened a little so a helmet-mounted source reads properly at the ranges a hideout allows.
    private static float Retro(Vector3 source, Vector3 point, Vector3 eye)
    {
        var ir = ClientSettings.Current.Ir;
        float apart = Vector3.Angle(source - point, eye - point);
        return ir.SideScatter + (1f - ir.SideScatter) * Mathf.Exp(-(apart * apart) / (ir.ReturnCone * ir.ReturnCone));
    }
}

/// <summary>What the player's look ray hits on the board; the action itself comes from <see cref="Plugin"/>.</summary>
public sealed class PatchBoardInteractive : InteractableObject
{
    public PatchWallDisplay Display = null!;
}

/// <summary>Records every change made to a pooled object so it can be put back exactly.</summary>
public sealed class PatchVisualState : MonoBehaviour
{
    private readonly List<Action> restore = new();

    public void Remember(Renderer renderer, MeshFilter filter)
    {
        var mesh = filter.sharedMesh;
        var materials = renderer.sharedMaterials;
        restore.Add(() => { if (renderer != null) renderer.sharedMaterials = materials; if (filter != null) filter.sharedMesh = mesh; });
    }

    public void OnRestore(Action undo) => restore.Add(undo);

    public void RememberPivot(PreviewPivot pivot)
    {
        var position = pivot.pivotPosition;
        restore.Add(() => { if (pivot != null) pivot.pivotPosition = position; });
    }

    public void RememberTransform(Transform t)
    {
        var p = t.localPosition; var r = t.localRotation;
        restore.Add(() => { if (t != null) { t.localPosition = p; t.localRotation = r; } });
    }

    public void Restore()
    {
        var display = GetComponent<PatchWallDisplay>();
        if (display != null) display.Clear();
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
    }

    private void OnDestroy() => Restore();
}

/// <summary>Shows the patches stored in the wall item's slots on the velcro board, packed by their real size so bigger ones never overlap smaller ones.
/// Reads the wall's contents only; patches go in and out through the game's normal item window.</summary>
public sealed class PatchWallDisplay : MonoBehaviour
{
    // The panel is 65 x 95 cm; patches stay inside the sewn binding and clear of the grommets.
    private const float PanelW = 0.60f, PanelH = 0.90f, PanelZ = 0.0025f;
    private static float Gap => ClientSettings.Current.Board.GapCm / 100f;
    private Transform anchor = null!;
    private CompoundItem wall = null!;
    private readonly List<GameObject> hung = new();
    private readonly Dictionary<string, Visual> visuals = new();
    private List<string> current = new();
    private GameObject? interact;
    private bool running, again;
    private Coroutine? watcher;

    public int HungCount => hung.Count;
    public CompoundItem Wall => wall;

    internal void Bind(Transform boardTransform, CompoundItem wallItem) { anchor = boardTransform; wall = wallItem; }

    /// <summary>Called whenever the game places the board on its spot: wire up the interaction and the patches.</summary>
    public void OnPlaced()
    {
        EnsureInteractive();
        Refresh();
        if (watcher == null && isActiveAndEnabled) watcher = StartCoroutine(Watch());
    }

    private void EnsureInteractive()
    {
        if (interact != null || anchor == null) return;
        interact = new GameObject("JackalPatch_board_interact");
        int layer = LayerMask.NameToLayer("Interactive");
        interact.layer = layer >= 0 ? layer : anchor.gameObject.layer;
        interact.transform.SetParent(anchor, false);
        interact.transform.localPosition = new Vector3(0f, 0f, 0.012f);
        interact.AddComponent<BoxCollider>().size = new Vector3(0.65f, 0.95f, 0.03f);
        interact.AddComponent<PatchBoardInteractive>().Display = this;
    }

    private IEnumerator Watch()
    {
        var wait = new WaitForSeconds(3f);
        while (true)
        {
            yield return wait;
            Refresh();
        }
    }

    private void OnDisable() => watcher = null;

    /// <summary>Removes everything this component added, before the donor object goes back to the pool.</summary>
    public void Clear()
    {
        ClearHung();
        if (interact != null) Destroy(interact);
        interact = null;
    }

    private void ClearHung()
    {
        foreach (var go in hung) if (go != null) Destroy(go);
        hung.Clear();
        current = new List<string>();
    }

    private void Refresh()
    {
        var p = Plugin.Instance;
        if (p is null || wall == null) return;
        if (running) { again = true; return; }
        try
        {
            // The grid order is the player's own arrangement; duplicates are allowed and each one hangs up.
            var items = wall.Grids.SelectMany(g => g.Items).Where(i => p.IsPatch(i.StringTemplateId)).ToList();
            var wanted = items.Select(i => i.StringTemplateId).ToList();
            if (wanted.SequenceEqual(current)) return;
            running = true;
            BuildAsync(wanted, items).ContinueWith(t =>
            {
                if (t.Exception != null) p.ReportOnce("patch wall", t.Exception);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception error) { running = false; p.ReportOnce("patch wall scan", error); }
    }

    private async Task BuildAsync(List<string> wanted, List<Item> items)
    {
        try
        {
            for (int i = 0; i < wanted.Count; i++)
            {
                var tpl = wanted[i];
                if (visuals.ContainsKey(tpl)) continue;
                var item = items[i];
                await EasyAssetsExtensions.WaitForAllBundlesJob(item.GetAllBundleTokens(), () => { });
                if (this == null || !enabled) return;
                var model = await Singleton<ObjectsFactory>.Instance.CreateItemAsync(item, ECameraType.Default, null, isAnimated: false, JobYieldPriority.General);
                if (this == null || !enabled) { if (model != null) AssetPoolObject.ReturnToPool(model); return; }
                try { visuals[tpl] = Capture(model, tpl); }
                finally { if (model != null) AssetPoolObject.ReturnToPool(model); }
            }
            Rebuild(wanted);
        }
        finally
        {
            running = false;
            if (again) { again = false; Refresh(); }
        }
    }

    private Visual Capture(GameObject model, string tpl)
    {
        var pair = model.GetComponentsInChildren<MeshFilter>(true)
            .Where(f => f.sharedMesh != null && f.sharedMesh.vertexCount > 0 && f.GetComponent<MeshRenderer>() != null)
            .OrderByDescending(f => f.sharedMesh.vertexCount).First();
        var mesh = pair.sharedMesh;
        var mats = pair.GetComponent<MeshRenderer>().sharedMaterials;
        var orient = Quaternion.identity;
        bool kit = Plugin.Instance!.Patches.ContainsKey(tpl);
        if (!kit && mesh.isReadable) orient = FaceForward(mesh);
        return new Visual(mesh, mats, orient) { Far = kit ? Plugin.Instance.FarMeshFor(tpl) : null };
    }

    /// <summary>Turns a one-sided patch mesh so its printed side faces the room and its art is upright, using the mesh's own normals and UVs.</summary>
    private static Quaternion FaceForward(Mesh mesh)
    {
        // Winding cannot be trusted (these meshes can be two-sided), but the art's own axes can: U runs to the viewer's right and V runs up.
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
        var face = Vector3.Cross(up, rightSum.normalized);   // Unity is left-handed: with V up and U to the viewer's right, this points at the viewer
        if (face.sqrMagnitude < 1e-8f) return Quaternion.identity;
        return Quaternion.Inverse(Quaternion.LookRotation(face.normalized, up));
    }

    /// <summary>Packs the patches the way people hang them on a real panel: tallest first, in staggered rows from the top, each at a small hand-placed angle.
    /// Space is measured on the tilted outline, so nothing overlaps. Everything stays real size unless the board is full, when all shrink together until they fit.</summary>
    private void Rebuild(List<string> wanted)
    {
        ClearHung();
        current = wanted;
        int n = wanted.Count;
        if (n == 0 || anchor == null) return;
        var boxes = new List<Box>(n);
        var seen = new Dictionary<string, int>();
        foreach (var tpl in wanted)
        {
            var v = visuals[tpl];
            seen.TryGetValue(tpl, out int copy);
            seen[tpl] = copy + 1;
            uint hash = Fnv(tpl + "|" + copy);
            var flat = OutlineSize(v, v.Orient);
            float aspect = Mathf.Max(flat.Item1, flat.Item2) / Mathf.Max(Mathf.Min(flat.Item1, flat.Item2), 1e-4f);
            var look = ClientSettings.Current.Board;
            float maxTilt = aspect > 1.6f ? look.NameTapeTilt : look.MaxTilt;   // name tapes hang nearly straight on a real panel; round and shaped patches wander more
            float tilt = ((hash & 0xffff) / 65535f * 2f - 1f) * maxTilt;
            var rot = Quaternion.Euler(0f, 0f, tilt) * v.Orient;
            var (w, h) = OutlineSize(v, rot);
            boxes.Add(new Box { Tpl = tpl, Visual = v, W = w, H = h, Rotation = rot, Jitter = ((hash >> 16) & 0xff) / 255f });
        }
        // Tallest first, then widest, then by name so the same set always lays out the same way.
        boxes.Sort((a, b) => { int c = b.H.CompareTo(a.H); if (c == 0) c = b.W.CompareTo(a.W); return c != 0 ? c : string.CompareOrdinal(a.Tpl, b.Tpl); });
        float low = ClientSettings.Current.Board.SmallestScalePercent / 100f, high = 1f;
        if (Fits(boxes, high)) low = high;
        else for (int i = 0; i < 24; i++) { float mid = (low + high) * 0.5f; if (Fits(boxes, mid)) low = mid; else high = mid; }
        float scale = low;
        var rows = Pack(boxes, scale);
        float y = PanelH / 2f;   // rows fill from the top, like a panel people add to over time
        foreach (var row in rows)
        {
            float x = -(row.Width) / 2f;
            float centreY = y - row.Height / 2f;
            foreach (var b in row.Boxes)
            {
                float w = b.W * scale, slack = row.Height - b.H * scale;
                Place(b.Visual, b.Rotation, new Vector2(x + w / 2f, centreY + (b.Jitter - 0.5f) * slack), scale, b.Tpl);
                x += w + Gap;
            }
            y -= row.Height + Gap;
        }
    }

    private static uint Fnv(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) { hash ^= c; hash *= 16777619; }
        return hash;
    }

    private void Place(Visual v, Quaternion rot, Vector2 centre, float scale, string tpl)
    {
        var localCentre = rot * v.Mesh.bounds.center * scale;
        var back = MinZ(v.Mesh.bounds, rot) * scale;
        var child = new GameObject("JackalPatch_on_wall");
        child.layer = anchor.gameObject.layer;
        child.transform.SetParent(anchor, false);
        child.transform.localPosition = new Vector3(centre.x - localCentre.x, centre.y - localCentre.y, PanelZ - back + 0.0004f);
        child.transform.localRotation = rot;
        child.transform.localScale = Vector3.one * scale;
        child.AddComponent<MeshFilter>().sharedMesh = v.Mesh;
        var r = child.AddComponent<MeshRenderer>();
        r.sharedMaterials = v.Materials;
        hung.Add(child);
        if (v.Far != null && ClientSettings.Current.Board.FarModels)
        {
            try
            {
                // Once a patch is small on screen its simple model is drawn instead. A full board is 80 patches, so this is most of the board's cost from across the room.
                var farGo = new GameObject("JackalPatch_far");
                farGo.layer = child.layer;
                farGo.transform.SetParent(child.transform, false);
                farGo.AddComponent<MeshFilter>().sharedMesh = v.Far;
                var farRenderer = farGo.AddComponent<MeshRenderer>();
                farRenderer.sharedMaterials = v.Materials;
                var group = child.AddComponent<LODGroup>();
                group.fadeMode = LODFadeMode.None;
                group.SetLODs(new[] { new LOD(ClientSettings.Current.Board.FarModelBelowScreenPercent / 100f, new Renderer[] { r }), new LOD(0.0005f, new Renderer[] { farRenderer }) });
                group.RecalculateBounds();
            }
            catch (Exception error) { Plugin.Instance?.ReportOnce("far model on the wall", error); }
        }
        var plugin = Plugin.Instance;
        // "is not null", not "!= null": Unity reports this object as null once BepInEx's manager object is gone, even though the plugin still works.
        if (ClientSettings.Current.Ir.Enabled && plugin is not null && plugin.Patches.TryGetValue(tpl, out var entry) && (entry.ir || entry.style == "ir"))
        {
            var overlay = plugin.IrOverlay(entry, v.Mesh);
            if (overlay != null) IrReflectivePatch.Attach(child, overlay.Value.mesh, overlay.Value.material);
        }
    }

    private static bool Fits(List<Box> boxes, float scale)
    {
        var rows = Pack(boxes, scale);
        return rows.Sum(r => r.Height) + Gap * (rows.Count - 1) <= PanelH && rows.All(r => r.Width <= PanelW);
    }

    private static List<Row> Pack(List<Box> boxes, float scale)
    {
        var rows = new List<Row>();
        Row? current = null;
        foreach (var b in boxes)
        {
            float w = b.W * scale, h = b.H * scale;
            if (current == null || current.Width + Gap + w > PanelW)
            {
                current = new Row { Width = w, Height = h };
                current.Boxes.Add(b);
                rows.Add(current);
            }
            else
            {
                current.Width += Gap + w;
                current.Height = Mathf.Max(current.Height, h);
                current.Boxes.Add(b);
            }
        }
        return rows;
    }

    private static (float, float) OutlineSize(Visual v, Quaternion rot)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        var b = v.Mesh.bounds;
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

    private void OnDestroy() => Clear();

    private sealed class Box { public string Tpl = ""; public Visual Visual = null!; public float W, H, Jitter; public Quaternion Rotation = Quaternion.identity; }
    private sealed class Row { public float Width, Height; public readonly List<Box> Boxes = new(); }

    private sealed class Visual { public Mesh Mesh; public Mesh? Far; public Material[] Materials; public Quaternion Orient; public Visual(Mesh m, Material[] mats, Quaternion o) { Mesh = m; Materials = mats; Orient = o; } }
}
