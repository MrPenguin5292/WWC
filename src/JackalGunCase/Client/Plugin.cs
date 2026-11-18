using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace JackalGunCase.Client;

/// <summary>
/// Gives the Jackal gun case its own model and textures (read from plain files, no Unity bundle) and draws whatever stickers are in its sticker grid on the lid.
/// Everything happens on the weapon case donor the game already spawns, every change is undone before the object goes back to the pool, and on any failure the donor look stays.
/// How stickers get here is written down in docs/sticker-case-contract.md: the sticker mod is optional and is only ever reached by reflection.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Jackal Gun Case", "0.1.1")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Gun Case");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("GunCase");
#endif
    public const string Id = "local.jackalguncase.client";
    public const string StickerGridName = "jackal_stickers";
    internal static Plugin? Instance;
    internal string CaseTpl = "";
    internal List<Spot> Spots = new();
    private string root = "";
    private Mesh? lod0, lod1;
    private Material? caseMaterial;
    private readonly HashSet<string> errors = new();
    private Harmony? harmony;

    private void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build requires EFT 40743 / Unity 2022.3.43f1.");
            root = Path.GetDirectoryName(Info.Location)!;
            CaseTpl = Hash("jackalguncase|case");
            Spots = JsonConvert.DeserializeObject<List<Spot>>(File.ReadAllText(Path.Combine(root, "spots.json"))) ?? new List<Spot>();
            Instance = this;
            harmony = new Harmony(Id);
            // Every way the game makes a model of an item: stash and hideout, inspect, loose loot on a raid floor.
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)) { priority = Priority.Last });
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturnToPool)));
            // The stash icon. Containers hide their contents from it, so it needs to be told about the stickers.
            harmony.Patch(AccessTools.Method(typeof(IconsHash), nameof(IconsHash.GetItemHash)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterIconHash)));
            harmony.Patch(AccessTools.Method(typeof(ItemIconCreator), "RenderModel"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRenderIcon)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRenderIcon)));
            // The item window: a faint sticker in every empty cell of the sticker grid.
            harmony.Patch(AccessTools.Method(typeof(EFT.UI.DragAndDrop.GridView), nameof(EFT.UI.DragAndDrop.GridView.Show)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterGridShow)));
            Logger.LogInfo($"Jackal Gun Case ready: case {CaseTpl}, {Spots.Count} sticker spots.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Instance = null;
            Logger.LogError("Jackal Gun Case unavailable; the donor look remains. " + error);
        }
    }

    internal static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2"))).Substring(0, 24);
    }

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"Jackal Gun Case: {key}: {error}");
    }

    internal static Grid? StickerGrid(CompoundItem item) => item.Grids?.FirstOrDefault(g => g.ID == StickerGridName);

    // ---------- the sticker side of the item window ----------
    // Widowz 2026-11-18: "add a icon for a sticker so people know the right side is for stickers". Every empty cell of the sticker grid
    // shows a faint sticker (a square with a peeled corner and a star) under where items go, the way empty gear slots show what goes in them.
    private const string HintsName = "JackalGunCase_sticker_hints";
    private static Sprite? hintSprite;

    private static void AfterGridShow(EFT.UI.DragAndDrop.GridView __instance, Grid grid)
    {
        try
        {
            if (__instance == null) return;
            var old = __instance.transform.Find(HintsName);
            var p = Instance;
            bool ours = p != null && grid != null && grid.ID == StickerGridName && grid.ParentItem?.StringTemplateId == p.CaseTpl;
            if (!ours)
            {
                // The game reuses grid views; one that showed the sticker grid before shows something else now.
                if (old != null) Destroy(old.gameObject);
                return;
            }
            var hints = old != null ? old.GetComponent<StickerHints>() : null;
            if (hints == null)
            {
                var go = new GameObject(HintsName, typeof(RectTransform));
                var rt = (RectTransform)go.transform;
                rt.SetParent(__instance.transform, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = Vector2.zero;
                hints = go.AddComponent<StickerHints>();
            }
            // Under the drag highlight and the items, over the grid's own cell pattern.
            var highlight = __instance._highlightPanel != null ? __instance._highlightPanel.transform : null;
            hints.transform.SetSiblingIndex(highlight != null && highlight.parent == __instance.transform ? highlight.GetSiblingIndex() : 0);
            hints.Bind(grid!, HintSprite());
        }
        catch (Exception error) { Instance?.ReportOnce("sticker hints in the item window", error); }
    }

    /// <summary>A 64 px white sticker on clear: rounded square outline, top right corner peeled over, a star in the middle.</summary>
    private static Sprite HintSprite()
    {
        if (hintSprite != null) return hintSprite;
        const int n = 64; const float lo = 11.5f, hi = 52.5f, radius = 6f, peel = 15f, stroke = 1.6f;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "JackalGunCase_sticker_hint", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var star = new Vector2[10];
        for (int k = 0; k < 10; k++) { float a = Mathf.PI / 2 + k * Mathf.PI / 5; float r = k % 2 == 0 ? 10.5f : 4.4f; star[k] = new Vector2(30f + r * Mathf.Cos(a), 30f + r * Mathf.Sin(a)); }
        float cut = hi + hi - peel;                       // the peel line x + y = cut
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                // rounded square, signed distance (negative inside)
                float qx = Mathf.Abs(fx - (lo + hi) / 2) - ((hi - lo) / 2 - radius), qy = Mathf.Abs(fy - (lo + hi) / 2) - ((hi - lo) / 2 - radius);
                float sq = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                float sPeel = (fx + fy - cut) / Mathf.Sqrt(2f);   // > 0 beyond the peel line
                float shape = Mathf.Max(sq, sPeel);
                float a = Mathf.Clamp01(stroke - Mathf.Abs(shape));                   // outline of the sticker
                // the folded-over corner: a triangle inside the peel line
                float fold = Mathf.Max(Mathf.Max(-(fx - (hi - peel)), -(fy - (hi - peel))), sPeel);
                if (fold < 0f) a = Mathf.Max(a, 0.45f);
                a = Mathf.Max(a, Mathf.Clamp01(stroke - Mathf.Abs(fold)) * (fx > hi - peel - 1 && fy > hi - peel - 1 ? 1f : 0f));
                if (Inside(star, new Vector2(fx, fy))) a = Mathf.Max(a, 0.8f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        hintSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        hintSprite.hideFlags = HideFlags.HideAndDontSave;
        return hintSprite;
    }

    private static bool Inside(Vector2[] poly, Vector2 p)
    {
        bool c = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) c = !c;
        return c;
    }

    private static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        var p = Instance;
        if (p is null || __0.StringTemplateId != p.CaseTpl) return;
        __result = p.ApplyAsync(__result, __0);
    }

    private async Task<GameObject?> ApplyAsync(Task<GameObject?> task, Item item)
    {
        var model = await task;
        if (model == null) return null;
        Apply(model, item);
        return model;
    }

    // The same swap for the calls that return straight away, which is how loose loot on a raid floor is made.
    private static void AfterCreateNow(Item __0, GameObject? __result)
    {
        var p = Instance;
        if (p is null || __result == null || __0.StringTemplateId != p.CaseTpl) return;
        p.Apply(__result, __0);
    }

    private void Apply(GameObject model, Item item)
    {
        var state = model.GetComponent<CaseVisualState>() ?? model.AddComponent<CaseVisualState>();
        state.Restore();
        try
        {
            var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).ToArray();
            if (filters.Length == 0) throw new InvalidDataException("Case donor has no rigid renderer.");
            var material = CaseMaterial(filters[0].GetComponent<MeshRenderer>().sharedMaterial);
            var near = lod0 ??= ReadMesh(Path.Combine(root, "lod0.bin"), "JackalGunCase_lod0");
            var far = lod1 ??= ReadMesh(Path.Combine(root, "lod1.bin"), "JackalGunCase_lod1");
            foreach (var filter in filters)
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                state.Remember(renderer, filter);
                // The shadow proxy and the far LOD share the simple mesh.
                var usesSimple = filter.name.IndexOf("LOD1", StringComparison.OrdinalIgnoreCase) >= 0 || filter.name.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0;
                filter.sharedMesh = usesSimple ? far : near;
                renderer.sharedMaterials = new[] { material };
            }
            foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
            // The donor's pivot shows its base first in the inspect window; the lid is the face worth seeing, with the handle at the top.
            var pivot = model.GetComponent<PreviewPivot>();
            if (pivot != null)
            {
                state.RememberPivot(pivot);
                pivot.pivotRotation = new Quaternion(-0.7071068f, 0f, 0f, 0.7071068f);
            }
            if (item is CompoundItem compound)
            {
                var display = model.GetComponent<CaseStickerDisplay>() ?? model.AddComponent<CaseStickerDisplay>();
                display.Bind(compound, material);
                display.Refresh(true);
            }
        }
        catch (Exception error)
        {
            state.Restore();
            ReportOnce("apply " + item.StringTemplateId, error);
        }
    }

    private Material CaseMaterial(Material donor)
    {
        if (caseMaterial != null) return caseMaterial;
        var m = new Material(donor) { name = "JackalGunCase_shell" };
        void Tex(string prop, string file, bool linear, bool isNormal)
        {
            if (!m.HasProperty(prop)) return;
            m.SetTexture(prop, Load(Path.Combine(root, file), linear, isNormal));
            m.SetTextureScale(prop, Vector2.one);
            m.SetTextureOffset(prop, Vector2.zero);
        }
        Tex("_MainTex", "case_a.png", false, false);
        Tex("_BumpMap", "case_n.png", true, true);
        Tex("_SpecMap", "case_s.png", true, false);
        if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        // Moulded plastic: a soft, low sheen. The donor's own numbers read as polished here because this lid is one big flat surface.
        if (m.HasProperty("_Specularness")) m.SetFloat("_Specularness", 0.4f);
        if (m.HasProperty("_Glossness")) m.SetFloat("_Glossness", 0.7f);
        if (m.HasProperty("_NormalIntensity")) m.SetFloat("_NormalIntensity", 1f);
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(0.012f, 0.012f, 0.012f, 0.5f));
        caseMaterial = m;
        return m;
    }

    private static Texture2D Load(string path, bool linear, bool isNormal)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "JackalGunCase_" + Path.GetFileNameWithoutExtension(path) };
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
        return tex;
    }

    private static Mesh ReadMesh(string path, string name)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 12 || data[0] != 'J' || data[1] != 'P' || data[2] != 'M' || data[3] != '1') throw new InvalidDataException("Not a case mesh: " + path);
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
        mesh.UploadMeshData(true);
        return mesh;
    }

    private static void BeforeReturnToPool(AssetPoolObject __instance)
    {
        if (__instance != null) __instance.GetComponent<CaseVisualState>()?.Restore();
    }

    // The case's icon has to change with its stickers. Weapons inside the case don't count: nothing about them shows on the outside.
    private static void AfterIconHash(Item item, ref int __result)
    {
        var p = Instance;
        if (p is null || item.StringTemplateId != p.CaseTpl || item is not CompoundItem compound) return;
        var grid = StickerGrid(compound);
        if (grid == null) return;
        foreach (var pair in grid.ContainedItems) __result ^= pair.Key.TemplateId.GetHashCode() * 397 ^ pair.Value.GetHashCode();
    }

    // The icon is rendered from a clone that drops the contents of anything with hidden contents, which containers are. For a case with stickers, the clone is
    // made from a copy holding only the stickers, and the template lets contents through for that one synchronous step, so the guns inside are never loaded for it.
    private static void BeforeRenderIcon(ref Item item, out ItemTemplate? __state)
    {
        __state = null;
        var p = Instance;
        if (p is null || item.StringTemplateId != p.CaseTpl || item is not CompoundItem compound) return;
        try
        {
            var grid = StickerGrid(compound);
            if (grid == null || !grid.ContainedItems.Any()) return;
            var trimmed = (CompoundItem)item.CloneVisibleItem();
            var target = StickerGrid(trimmed);
            if (target == null) return;
            foreach (var pair in grid.ContainedItems) target.AddItemWithoutRestrictions(pair.Key.CloneVisibleItem(), pair.Value.Clone());
            trimmed.Template.HideEntrails = false;
            __state = trimmed.Template;
            item = trimmed;
        }
        catch (Exception error) { p.ReportOnce("icon stickers", error); }
    }

    // The clone is made before RenderModel's first await, so this runs with it already done.
    private static void AfterRenderIcon(ItemTemplate? __state)
    {
        if (__state != null) __state.HideEntrails = true;
    }

    /// <summary>A place on the lid, in the case model's own space. col and row match the cell of the sticker grid.</summary>
    public sealed class Spot { public int col, row; public float[] pos = new float[3]; public float angle, maxW, maxH; }
}

/// <summary>Remembers what was swapped on a pooled donor so it can be put back.</summary>
public sealed class CaseVisualState : MonoBehaviour
{
    private readonly List<Action> restore = new();

    public void Remember(Renderer renderer, MeshFilter filter)
    {
        var mesh = filter.sharedMesh;
        var materials = renderer.sharedMaterials;
        restore.Add(() => { if (renderer != null) renderer.sharedMaterials = materials; if (filter != null) filter.sharedMesh = mesh; });
    }

    public void RememberPivot(PreviewPivot pivot)
    {
        var rotation = pivot.pivotRotation;
        restore.Add(() => { if (pivot != null) pivot.pivotRotation = rotation; });
    }

    public void Restore()
    {
        var display = GetComponent<CaseStickerDisplay>();
        if (display != null) display.Clear();
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
    }

    private void OnDestroy() => Restore();
}

/// <summary>Draws the stickers in the case's sticker grid on the lid. Reads the grid only; stickers go in and out through the game's normal item window.</summary>
public sealed class CaseStickerDisplay : MonoBehaviour
{
    private CompoundItem? item;
    private Material? donor;
    private readonly List<GameObject> drawn = new();
    private string current = "";
    private bool cleared = true;
    private Coroutine? watcher;
    private static Type? api;
    private static MethodInfo? tryGetParts, tryGetSize, isSticker;

    public int DrawnCount => drawn.Count;

    internal void Bind(CompoundItem compound, Material material)
    {
        item = compound;
        donor = material;
        cleared = false;
    }

    private void OnEnable() { if (!cleared && item != null) watcher = StartCoroutine(Watch()); }
    private void OnDisable() { watcher = null; }

    // The inspect window keeps its model while the case's contents change, so the sticker list is checked now and then.
    private IEnumerator Watch()
    {
        var wait = new WaitForSeconds(2f);
        while (true)
        {
            yield return wait;
            Refresh(false);
        }
    }

    public void Clear()
    {
        cleared = true;
        if (watcher != null) { StopCoroutine(watcher); watcher = null; }
        // Unparented first: the model can be reused from the pool in the same frame, and Destroy only happens at the end of it.
        foreach (var go in drawn) if (go != null) { go.transform.SetParent(null, false); Destroy(go); }
        drawn.Clear();
        current = "";
        item = null;
    }

    internal void Refresh(bool force)
    {
        var p = Plugin.Instance;
        if (p is null || item == null) return;
        try
        {
            var grid = Plugin.StickerGrid(item);
            var list = grid == null ? new List<KeyValuePair<Item, LocationInGrid>>() : grid.ContainedItems.ToList();
            var key = string.Join("|", list.Select(x => $"{x.Key.StringTemplateId}@{x.Value.x},{x.Value.y},{(int)x.Value.r}").OrderBy(s => s, StringComparer.Ordinal));
            if (!force && key == current) return;
            current = key;
            foreach (var go in drawn) if (go != null) { go.transform.SetParent(null, false); Destroy(go); }
            drawn.Clear();
            if (list.Count == 0 || !FindApi()) return;
            var layer = GetComponentInChildren<MeshRenderer>(true).gameObject.layer;
            // Row by row, later ones a hair higher so overlapping stickers don't flicker.
            int order = 0;
            foreach (var pair in list.OrderBy(x => x.Value.y).ThenBy(x => x.Value.x))
            {
                var spot = p.Spots.FirstOrDefault(s => s.col == pair.Value.x && s.row == pair.Value.y);
                if (spot == null) continue;
                var go = Draw(pair.Key.StringTemplateId, spot, pair.Value.r, order, layer);
                if (go != null) { drawn.Add(go); order++; }
            }
        }
        catch (Exception error) { p.ReportOnce("sticker refresh", error); }
    }

    private static bool FindApi()
    {
        if (api != null) return true;
        api = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("JackalStickers.Client.StickerApi", false)).FirstOrDefault(t => t != null);
        if (api == null) return false;
        tryGetParts = api.GetMethod("TryGetParts", BindingFlags.Public | BindingFlags.Static);
        tryGetSize = api.GetMethod("TryGetSize", BindingFlags.Public | BindingFlags.Static);
        isSticker = api.GetMethod("IsSticker", BindingFlags.Public | BindingFlags.Static);
        if (tryGetParts == null || tryGetSize == null || isSticker == null) { api = null; return false; }
        return true;
    }

    private GameObject? Draw(string tpl, Plugin.Spot spot, ItemRotation rotation, int order, int layer)
    {
        if (!(bool)isSticker!.Invoke(null, new object[] { tpl })) return null;
        var sizeArgs = new object[] { tpl, 0f, 0f };
        if (!(bool)tryGetSize!.Invoke(null, sizeArgs)) return null;
        var partArgs = new object?[] { tpl, donor, null, null };
        if (!(bool)tryGetParts!.Invoke(null, partArgs)) return null;
        var mesh = (Mesh)partArgs[2]!;
        var materials = (Material[])partArgs[3]!;
        float w = (float)sizeArgs[1], h = (float)sizeArgs[2];
        bool turned = rotation == ItemRotation.Vertical;
        if (turned) (w, h) = (h, w);
        float scale = Mathf.Min(1f, spot.maxW / Mathf.Max(w, 0.001f), spot.maxH / Mathf.Max(h, 0.001f));
        var go = new GameObject("JackalCaseSticker") { layer = layer };
        go.transform.SetParent(transform, false);
        // The lid faces +Y with the handle side (+Z) up. The spot's own angle and the 90 degree turn both spin about the lid's outward normal.
        var outward = Vector3.up; var up = Vector3.forward;
        go.transform.localRotation = Quaternion.LookRotation(outward, up) * Quaternion.AngleAxis(spot.angle + (turned ? 90f : 0f), Vector3.forward) * FaceForward(mesh);
        go.transform.localPosition = new Vector3(spot.pos[0], spot.pos[1], spot.pos[2]) + outward * (0.0005f + order * 0.0003f);
        go.transform.localScale = Vector3.one * scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    /// <summary>Turns a sticker so its printed side faces local +Z and its art is upright. The side comes from the mesh's normals and "up" from its UVs, so a handedness slip in the mesh can't lay it face-down.</summary>
    private static Quaternion FaceForward(Mesh mesh)
    {
        if (!mesh.isReadable) return Quaternion.identity;
        var verts = mesh.vertices; var uv = mesh.uv; var tris = mesh.triangles; var normals = mesh.normals;
        Vector3 face = Vector3.zero, upSum = Vector3.zero;
        for (int t = 0; t < tris.Length; t += 3)
        {
            Vector3 e1 = verts[tris[t + 1]] - verts[tris[t]], e2 = verts[tris[t + 2]] - verts[tris[t]];
            float du1 = uv[tris[t + 1]].x - uv[tris[t]].x, dv1 = uv[tris[t + 1]].y - uv[tris[t]].y;
            float du2 = uv[tris[t + 2]].x - uv[tris[t]].x, dv2 = uv[tris[t + 2]].y - uv[tris[t]].y;
            float det = du1 * dv2 - du2 * dv1;
            if (Mathf.Abs(det) < 1e-9f) continue;
            float area = Vector3.Cross(e1, e2).magnitude;
            upSum += (e2 * du1 - e1 * du2).normalized * area * Mathf.Sign(det);
            if (normals.Length > 0) face += (normals[tris[t]] + normals[tris[t + 1]] + normals[tris[t + 2]]) * area;
        }
        if (face.sqrMagnitude < 1e-14f || upSum.sqrMagnitude < 1e-14f) return Quaternion.identity;
        var f = face.normalized; var u = (upSum - Vector3.Dot(upSum, f) * f);
        if (u.sqrMagnitude < 1e-10f) return Quaternion.identity;
        return Quaternion.Inverse(Quaternion.LookRotation(f, u.normalized));
    }
}

/// <summary>The faint stickers in the sticker grid's empty cells; a cell with something in it hides its sticker.</summary>
public sealed class StickerHints : MonoBehaviour
{
    private const float Cell = 63f;     // the game's item-window cell (GridView.SetItemViewPosition)
    private Grid? grid;
    private UnityEngine.UI.Image[] cells = Array.Empty<UnityEngine.UI.Image>();
    private int width, height;
    private float next;

    public void Bind(Grid target, Sprite sprite)
    {
        grid = target;
        if (cells.Length != target.GridWidth * target.GridHeight || width != target.GridWidth)
        {
            foreach (var c in cells) if (c != null) Destroy(c.gameObject);
            width = target.GridWidth; height = target.GridHeight;
            cells = new UnityEngine.UI.Image[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var go = new GameObject($"hint_{x}_{y}", typeof(RectTransform));
                    var rt = (RectTransform)go.transform;
                    rt.SetParent(transform, false);
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                    rt.sizeDelta = new Vector2(Cell * 0.72f, Cell * 0.72f);
                    rt.anchoredPosition = new Vector2(x * Cell + Cell * 0.14f, -(y * Cell + Cell * 0.14f));
                    var img = go.AddComponent<UnityEngine.UI.Image>();
                    img.sprite = sprite;
                    img.preserveAspect = true;
                    img.raycastTarget = false;
                    img.color = new Color(1f, 1f, 1f, 0.22f);
                    cells[y * width + x] = img;
                }
        }
        next = 0f;
        Refresh();
    }

    private void Update()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.2f;
        Refresh();
    }

    private void Refresh()
    {
        if (grid == null) return;
        var used = new bool[cells.Length];
        try
        {
            foreach (var kv in grid.ContainedItems)
            {
                var at = kv.Value;
                if (at.x >= 0 && at.x < width && at.y >= 0 && at.y < height) used[at.y * width + at.x] = true;
            }
        }
        catch { return; }
        for (int i = 0; i < cells.Length; i++) if (cells[i] != null && cells[i].enabled == used[i]) cells[i].enabled = !used[i];
    }
}
