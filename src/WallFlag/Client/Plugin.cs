using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using EFT;
using EFT.AssetsManager;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace WallFlag.Client;

// Three jobs, all reversible and pool-safe:
//  0. A flag is a folded square in the inventory, in the inspect window and when dropped. It only opens out flat where it hangs: a hideout poster spot or a raid wall spot.
//  1. A flag with a torn or dirty kit in its slot wears that look. Restored when the object returns to the pool.
//  2. Flags are attachment-class items, so the game would hang them half a flag too high on hideout poster spots; this centres them as it does posters.
// The kit's model is mounted under the flag's mesh by the game, so the kit is recognised by that model's name.
// If anything fails the plugin unpatches itself and flags stay clean. Nothing throws into the game's own code.
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Guid, ModName, Version)]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Wall Flags");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.Part;
#endif
    private const string Guid = "local.wallflag.client";
    private const string ModName = "Jackal Wall Flag";
    private const string Version = "0.1.1";
    private const string RequiredUnity = "2022.3.43f1";
    private const string MeshObject = "mod_wear";                 // the flag's mesh object; also the slot's bone in the bundle
    private const int Size = 1024;
    private static readonly (string model, string kind)[] Kits = { ("ducttape", "dirty"), ("insulation", "torn") };

    // The game can destroy BepInEx's manager object while the plugin keeps working, so compare this by reference (is null), never with == null.
    private static Plugin Instance;
    private Harmony _harmony;
    private float _dirt = 1f, _ripEdges = 1f;
    private bool _holes = true;
    private static readonly AccessTools.FieldRef<StashItemModel, GameObject> PrefabOf = AccessTools.FieldRefAccess<StashItemModel, GameObject>("_prefab");

    private sealed class Dressed
    {
        public Renderer Renderer;
        public Material[] Original;
        public Material[] Instances;
        public List<Renderer> Hidden = new List<Renderer>();
    }

    private readonly Dictionary<int, Dressed> _dressed = new Dictionary<int, Dressed>();
    private readonly Dictionary<(int, string), Texture2D> _cache = new Dictionary<(int, string), Texture2D>();
    private readonly Dictionary<string, Color32[]> _masks = new Dictionary<string, Color32[]>();
    private readonly Dictionary<int, Mesh> _folded = new Dictionary<int, Mesh>();
    private readonly HashSet<string> _reported = new HashSet<string>();

    private void Awake()
    {
        if (Application.unityVersion != RequiredUnity) { Logger.LogError($"{ModName}: Unity {Application.unityVersion} is not {RequiredUnity}; not loading"); return; }
        try
        {
            Instance = this;
            ReadLooks();
            _harmony = new Harmony(Guid);
            var returns = typeof(AssetPoolObject).GetMethods(AccessTools.all).Where(m => m.Name == "ReturnToPool" && m.GetParameters().Length >= 1).ToList();
            if (returns.Count == 0) throw new MissingMethodException("AssetPoolObject.ReturnToPool");
            foreach (var m in returns) _harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(OnReturnToPool)));
            _harmony.Patch(AccessTools.Method(typeof(AssetPoolObject), "ReturnToPool", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePooled)));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)) { priority = Priority.Last });
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            // Plain reflection, not AccessTools.DeclaredMethod: most GameWorld types don't declare CreateStaticLoot, and HarmonyX
            // logs a "Could not find method" warning for every miss.
            const System.Reflection.BindingFlags declared = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var type in typeof(GameWorld).Assembly.GetTypes().Where(t => typeof(GameWorld).IsAssignableFrom(t)))
            {
                var hang = type.GetMethod(nameof(GameWorld.CreateStaticLoot), declared);
                if (hang != null) _harmony.Patch(hang, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeHungInRaid)));
            }
            _harmony.Patch(AccessTools.Method(typeof(StashItemModel), nameof(StashItemModel.Init)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeStashModel)), postfix: new HarmonyMethod(typeof(Plugin), nameof(OnStashModel)));
            _harmony.Patch(AccessTools.Method(typeof(StashItemModel), nameof(StashItemModel.ConfigureRotationPivotPosition)), postfix: new HarmonyMethod(typeof(Plugin), nameof(CentreOnSpot)));
            _harmony.Patch(AccessTools.Method(typeof(HideoutAreaStashController), nameof(HideoutAreaStashController.PositionGameObject)), postfix: new HarmonyMethod(typeof(Plugin), nameof(FitToSpot)));
            _harmony.Patch(AccessTools.Method(typeof(LootItem), nameof(LootItem.Init)), postfix: new HarmonyMethod(typeof(Plugin), nameof(OnLootItem)));
            Logger.LogInfo($"{ModName}: ready");
        }
        catch (Exception ex)
        {
            Logger.LogError($"{ModName}: could not hook the game ({ex.Message}); flags will look clean");
            try { _harmony?.UnpatchSelf(); } catch { }
            Instance = null;
        }
    }

    // The "looks" section of the mod's one settings file, which lives with the server mod. Any problem leaves the full looks in place.
    private void ReadLooks()
    {
        try
        {
            var game = new DirectoryInfo(Path.GetDirectoryName(Info.Location)).Parent?.Parent?.Parent?.FullName;
#if WWC
            var path = game == null ? null : Path.Combine(game, "SPT_Runtime", "user", "mods", "Widowz-WWC.Server", "Flags", "config.json");
#else
            var path = game == null ? null : Path.Combine(game, "SPT_Runtime", "user", "mods", "Widowz-WallFlag.Server", "config.json");
#endif
            if (path == null || !File.Exists(path)) return;
            var text = File.ReadAllText(path);
            _dirt = Percent(text, "dirtStrengthPercent");
            _ripEdges = Percent(text, "ripEdgeDarknessPercent");
            var holes = Regex.Match(text, "\"ripHolesShowTheWall\"\\s*:\\s*(true|false)");
            if (holes.Success) _holes = holes.Groups[1].Value == "true";
            Logger.LogInfo($"{ModName}: looks: dirt {_dirt:P0}, rip edges {_ripEdges:P0}, holes {_holes}");
        }
        catch (Exception ex) { Logger.LogWarning($"{ModName}: could not read the looks settings ({ex.Message}); using the full looks"); }
    }

    private static float Percent(string text, string key)
    {
        var m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*([0-9.]+)");
        return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Mathf.Clamp01(v / 100f) : 1f;
    }

    private static bool IsFlag(Item item)
    {
        var name = item?.Template?.Name;
        return name != null && name.StartsWith("wallflag_", StringComparison.Ordinal) && !name.StartsWith("wallflag_wear_", StringComparison.Ordinal);
    }

    private static void AfterCreate(Item __0, ref Task<GameObject> __result)
    {
        if (Instance is not null && IsFlag(__0)) __result = Instance.FoldWhenReady(__result);
    }

    private static void AfterCreateNow(Item __0, GameObject __result)
    {
        if (Instance is not null && IsFlag(__0) && __result != null) Instance.Fold(__result);
    }

    private async Task<GameObject> FoldWhenReady(Task<GameObject> task)
    {
        var model = await task;
        if (model != null) Fold(model);
        return model;
    }

    private void Report(string key, Exception ex)
    {
        if (_reported.Add(key)) Logger.LogError($"{ModName}: {key}: {ex.Message}");
    }

    // As an item a flag is folded. Whatever happens, it falls back to the flat flag.
    private void Fold(GameObject root)
    {
        var state = root.GetComponent<FoldState>() ?? root.AddComponent<FoldState>();
        try
        {
            state.Undo();
            var cloth = root.transform.Find(MeshObject);
            var filter = cloth != null ? cloth.GetComponent<MeshFilter>() : null;
            var renderer = cloth != null ? cloth.GetComponent<MeshRenderer>() : null;
            if (filter == null || renderer == null || filter.sharedMesh == null) return;
            var mesh = FoldedMesh(renderer, filter.sharedMesh);
            state.Keep(filter);
            filter.sharedMesh = mesh;
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true)) state.Off(group);
            foreach (var box in root.GetComponentsInChildren<BoxCollider>(true)) state.Fit(box, mesh.bounds);
            foreach (var hull in root.GetComponentsInChildren<MeshCollider>(true)) state.Fit(hull, mesh);
            var pivot = root.GetComponent<PreviewPivot>();
            if (pivot != null) state.Centre(pivot, root.transform.InverseTransformPoint(cloth.TransformPoint(mesh.bounds.center)));
            state.Folded = true;
            Apply(root);
        }
        catch (Exception ex)
        {
            state.Undo();
            Report("folding a flag", ex);
        }
    }

    // Opens a flag out flat before it is hung, because the game centres a hanging item on its mesh.
    private void Unfold(GameObject root)
    {
        if (root == null) return;
        var state = root.GetComponent<FoldState>();
        if (state == null || !state.Folded) return;
        Restore(root);
        state.Undo();
    }

    private static void BeforeStashModel(GameObject prefab)
    {
        try { if (Instance is not null) Instance.Unfold(prefab); }
        catch (Exception ex) { Instance?.Report("hanging a flag", ex); }
    }

    private static void BeforeHungInRaid(GameObject lootObject)
    {
        try { if (Instance is not null) Instance.Unfold(lootObject); }
        catch (Exception ex) { Instance?.Report("hanging a flag in a raid", ex); }
    }

    private static void BeforePooled(AssetPoolObject __instance)
    {
        try
        {
            if (Instance is null || __instance == null) return;
            Instance.Restore(__instance.gameObject);
            __instance.GetComponent<FoldState>()?.Undo();
        }
        catch (Exception ex) { Instance?.Report("putting a flag away", ex); }
    }

    private Mesh FoldedMesh(MeshRenderer renderer, Mesh flat)
    {
        var art = renderer.sharedMaterial != null ? renderer.sharedMaterial.mainTexture as Texture2D : null;
        var wide = flat.bounds.size.x >= flat.bounds.size.y;
        var key = (art != null ? art.GetInstanceID() : 0) * 2 + (wide ? 1 : 0);
        if (_folded.TryGetValue(key, out var done) && done != null) return done;
        return _folded[key] = FoldedFlag.Build(art, flat.bounds.size.x, flat.bounds.size.y);
    }

    // Hung on a hideout poster spot.
    private static void OnStashModel(GameObject prefab)
    {
        try { if (Instance is not null && prefab != null) Instance.Apply(prefab); }
        catch (Exception ex) { Instance?.Logger.LogError($"{ModName}: hideout hook: {ex.Message}"); }
    }

    // Lying or hanging loose in a raid.
    private static void OnLootItem(LootItem __instance)
    {
        try { if (Instance is not null && __instance != null) Instance.Apply(__instance.gameObject); }
        catch (Exception ex) { Instance?.Logger.LogError($"{ModName}: loot hook: {ex.Message}"); }
    }

    private static void OnReturnToPool(object __0)
    {
        try
        {
            if (Instance is null || __0 == null) return;
            // A component the game has already destroyed still arrives here as a C# object, and ?. can't tell; only Unity's own
            // null check can (seen in the WWC hideout run: NullReferenceException in Component.get_gameObject). Nothing to restore then.
            var go = __0 as GameObject;
            if (go == null && __0 is Component component && component != null) go = component.gameObject;
            if (go != null)
            {
                Instance.Restore(go);
                go.GetComponent<FoldState>()?.Undo();
            }
        }
        catch (Exception ex) { Instance?.Logger.LogError($"{ModName}: pool hook: {ex.Message}"); }
    }

    // 3. A flag (1.05 x 0.70 m) is bigger than most posters. Where a poster spot has furniture standing out from the wall over part of it
    //    (the kitchen's range hood and wall shelf at Poster_Kitchen_1, a pile of cloth under Poster_RestSpace_4: WWC trailer, 2026-11-17),
    //    the flag is shrunk and moved, in small steps, until it clears everything standing out from the wall where it hangs.
    private static void FitToSpot(StashItemModel model, StashItemModelSpawnPoint spawnPoint)
    {
        try
        {
            if (Instance is null || model == null || spawnPoint == null) return;
            var prefab = PrefabOf(model);
            var cloth = prefab != null ? prefab.transform.Find(MeshObject) : null;
            if (cloth != null) Instance.Fit(model.transform, cloth, spawnPoint.transform);
        }
        catch (Exception ex) { Instance?.Report("fitting a flag to its spot", ex); }
    }

    private void Fit(Transform model, Transform cloth, Transform spot)
    {
        model.localScale = Vector3.one;
        var filter = cloth.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return;
        var flag = BoxIn(spot, cloth.localToWorldMatrix, filter.sharedMesh.bounds);
        // The wall: the nearer of the two hits straight out of the flag's middle along the spot's z.
        var centre = spot.TransformPoint(flag.center);
        var z = spot.TransformDirection(Vector3.forward).normalized;
        float back = WallHit(centre, -z, model), front = WallHit(centre, z, model);
        if (float.IsInfinity(back) && float.IsInfinity(front)) return;
        float room = back <= front ? 1f : -1f;                       // the room is on the spot's +z when the wall is behind
        float wallZ = flag.center.z - room * Mathf.Min(back, front);
        const float reach = 0.35f;
        var area = new Rect(flag.min.x - reach, flag.min.y - reach, flag.size.x + 2 * reach, flag.size.y + 2 * reach);
        var mid = new Vector2(flag.center.x, flag.center.y);
        var blocks = new List<(Rect r, string name)>();
        bool Ours(Transform t) => t.IsChildOf(model) || t.IsChildOf(spot) || t.GetComponentInParent<Player>() != null;
        // 1. Solid things: colliders crossing the cloth's own layer (it hangs 35 mm off the wall), found cell by cell over the flag and round
        //    it. This sees a range hood or a shelf as it is; a box round the whole model (below) would also take in a stove's pipe running
        //    up the wall behind the flag.
        const float cell = 0.025f;
        var layerZ = wallZ + room * 0.035f;
        var half = new Vector3(cell / 2, cell / 2, 0.01f);
        for (float y = area.yMin + cell / 2; y < area.yMax; y += cell)
        {
            float runStart = float.NaN; string runName = "";
            for (float x = area.xMin + cell / 2; x < area.xMax + cell; x += cell)
            {
                string hitName = "";
                if (x < area.xMax)
                    foreach (var col in Physics.OverlapBox(spot.TransformPoint(new Vector3(x, y, layerZ)), half, spot.rotation, ~0, QueryTriggerInteraction.Ignore))
                        if (col != null && !Ours(col.transform)) { hitName = col.name; break; }
                if (hitName.Length > 0 && float.IsNaN(runStart)) { runStart = x - cell / 2; runName = hitName; }
                else if (hitName.Length == 0 && !float.IsNaN(runStart))
                {
                    blocks.Add((new Rect(runStart, y - cell / 2, x - cell / 2 - runStart, cell), "collider " + runName));
                    runStart = float.NaN;
                }
            }
        }
        // 2. Small things with no collider (a pile of clothes): the box round the model, when it reaches through the cloth's layer. Flat
        //    things on the wall (signs, other posters, thin pipes) stay behind the cloth, things out in the room (a pot on the stove) only
        //    in front of it; shadow-only stand-ins and decals (paint cracks: no shadow, a few cm thick) draw nothing it could run into.
        foreach (var r in FindObjectsOfType<Renderer>())
        {
            if (r == null || !r.enabled || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
            if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly || IsDecal(r) || Ours(r.transform)) continue;
            var b = BoxIn(spot, Matrix4x4.identity, r.bounds);
            float near = room > 0 ? b.min.z - wallZ : wallZ - b.max.z, far = room > 0 ? b.max.z - wallZ : wallZ - b.min.z;
            if (far < 0.03f || near > 0.045f) continue;
            if ((r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off || Overlay(r)) && far - near < 0.1f) continue;
            var rect = new Rect(b.min.x, b.min.y, b.size.x, b.size.y);
            if (!rect.Overlaps(area) || rect.width > 1f || rect.height > 1f || far - near > 0.8f || rect.Contains(mid)) continue;
            blocks.Add((rect, r.name));
        }
        float w = flag.size.x, h = flag.size.y;
        const float margin = 0.015f, step = 0.01f, shift = 0.25f;
        var offsets = new List<Vector2>();
        for (float dx = -shift; dx <= shift + 1e-4f; dx += step)
            for (float dy = -shift; dy <= shift + 1e-4f; dy += step) offsets.Add(new Vector2(dx, dy));
        bool Clear(float s, Vector2 d)
        {
            var rect = new Rect(mid.x + d.x - s * w / 2 - margin, mid.y + d.y - s * h / 2 - margin, s * w + 2 * margin, s * h + 2 * margin);
            foreach (var bl in blocks) if (bl.r.Overlaps(rect)) return false;
            return true;
        }
        if (blocks.Count == 0 || Clear(1f, Vector2.zero)) return;
        // The cheapest change that clears: each 10 % smaller costs as much as 12.5 cm moved, so a flag shrinks a little rather than wander.
        float bestCost = float.PositiveInfinity, bestS = 1f; var bestD = Vector2.zero;
        for (float s = 1f; s >= 0.449f; s -= 0.05f)
            foreach (var d in offsets)
            {
                float cost = (1f - s) + 0.8f * d.magnitude;
                if (cost < bestCost && Clear(s, d)) { bestCost = cost; bestS = s; bestD = d; }
            }
        if (!float.IsInfinity(bestCost))
        {
            // Scale about the model's own origin, then put the flag's middle where it should go.
            var origin = model.localPosition;
            var c = flag.center;
            model.localScale = Vector3.one * bestS;
            model.localPosition = c + new Vector3(bestD.x, bestD.y, 0f) - bestS * (c - origin);
            var hit = blocks.Where(bl => bl.r.Overlaps(new Rect(mid.x - w / 2, mid.y - h / 2, w, h)))
                .Select(bl => bl.name).Distinct();
            Logger.LogInfo($"{ModName}: flag on {spot.name}: size {bestS:P0}, moved {bestD.x * 100:0} cm across and {bestD.y * 100:0} cm up, clear of {string.Join(", ", hit)}");
            return;
        }
        Logger.LogInfo($"{ModName}: flag on {spot.name}: no smaller size clears {string.Join(", ", blocks.Select(bl => $"{bl.name} [{bl.r.xMin - mid.x:0.00}..{bl.r.xMax - mid.x:0.00}, {bl.r.yMin - mid.y:0.00}..{bl.r.yMax - mid.y:0.00}]").Distinct())}; left as it is");
    }

    /// <summary>Every material cut out or see-through (paint cracks, grime, stains laid over a wall).</summary>
    private static bool Overlay(Renderer r)
    {
        var ms = r.sharedMaterials;
        if (ms == null || ms.Length == 0) return false;
        foreach (var m in ms) if (m == null || m.renderQueue < 2450) return false;
        return true;
    }

    private static bool IsDecal(Renderer r)
    {
        foreach (var m in r.sharedMaterials)
            if (m != null && m.shader != null && m.shader.name.IndexOf("decal", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>A box (in <paramref name="toWorld"/>'s space) as an axis-aligned box in <paramref name="space"/>'s axes.</summary>
    private static Bounds BoxIn(Transform space, Matrix4x4 toWorld, Bounds box)
    {
        var result = new Bounds();
        bool first = true;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? box.min.x : box.max.x, (i & 2) == 0 ? box.min.y : box.max.y, (i & 4) == 0 ? box.min.z : box.max.z);
            var p = space.InverseTransformPoint(toWorld.MultiplyPoint3x4(corner));
            if (first) { result = new Bounds(p, Vector3.zero); first = false; } else result.Encapsulate(p);
        }
        return result;
    }

    /// <summary>How far the nearest surface (not the flag) is along a ray, up to 30 cm.</summary>
    private static float WallHit(Vector3 from, Vector3 dir, Transform model)
    {
        float best = float.PositiveInfinity;
        foreach (var hit in Physics.RaycastAll(from, dir, 0.3f, ~0, QueryTriggerInteraction.Ignore))
            if (hit.collider != null && !hit.collider.transform.IsChildOf(model) && hit.distance < best) best = hit.distance;
        return best;
    }

    // Stock posters use a pivot at the middle of the model; any other item gets one half its height up. Flags are centred like posters.
    private static void CentreOnSpot(StashItemModel __instance, ref Vector3 __result)
    {
        try
        {
            var prefab = PrefabOf(__instance);
            if (prefab != null && prefab.transform.Find(MeshObject) != null) __result = Vector3.zero;
        }
        catch { }
    }

    private void Apply(GameObject root)
    {
        var mesh = root.transform.Find(MeshObject);
        if (mesh == null) return;                                      // not a flag
        var key = root.GetInstanceID();
        if (_dressed.ContainsKey(key)) return;

        string kind = null;
        foreach (Transform child in mesh)
        {
            foreach (var (model, k) in Kits)
                if (child.name.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0) kind = k;
        }
        if (kind == null) return;                                      // no kit in the slot: a clean flag

        var renderer = mesh.GetComponent<MeshRenderer>();
        if (renderer == null) return;
        var state = new Dressed { Renderer = renderer, Original = renderer.sharedMaterials };
        // The game mounts the kit's own model on the flag. Never show it.
        foreach (var r in mesh.GetComponentsInChildren<Renderer>(true))
        {
            if (r == renderer || !r.enabled) continue;
            r.enabled = false;
            state.Hidden.Add(r);
        }
        _dressed[key] = state;

        var source = renderer.sharedMaterial.mainTexture as Texture2D;
        var texture = source != null ? Composite(source, kind) : null;
        if (texture == null) return;
        var folded = root.GetComponent<FoldState>()?.Folded == true;
        var torn = kind == "torn" && _holes && !folded;         // holes would show straight through the layers of a folded flag
        var cutout = torn ? Shader.Find("p0/Cutout/Bumped Diffuse") : null;
        if (torn && cutout == null) Logger.LogWarning($"{ModName}: cutout shader not found, so rips show as dark patches");
        var mats = renderer.sharedMaterials.Select(m =>
        {
            var copy = new Material(m) { mainTexture = texture };
            if (cutout != null) { copy.shader = cutout; copy.SetFloat("_Cutoff", 0.5f); }
            return copy;
        }).ToArray();
        renderer.sharedMaterials = mats;
        state.Instances = mats;
    }

    private void Restore(GameObject root)
    {
        var key = root.GetInstanceID();
        if (!_dressed.TryGetValue(key, out var state)) return;
        _dressed.Remove(key);
        if (state.Renderer != null) state.Renderer.sharedMaterials = state.Original;
        foreach (var r in state.Hidden) if (r != null) r.enabled = true;
        if (state.Instances != null) foreach (var m in state.Instances) if (m != null) Destroy(m);
    }

    private Color32[] Mask(string kind)
    {
        if (_masks.TryGetValue(kind, out var cached)) return cached;
        var path = Path.Combine(Path.GetDirectoryName(Info.Location), $"wear_{kind}.png");
        if (!File.Exists(path)) { Logger.LogWarning($"{ModName}: {path} is missing"); return _masks[kind] = null; }
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        t.LoadImage(File.ReadAllBytes(path));
        var result = t.width == Size && t.height == Size ? t.GetPixels32() : null;
        Destroy(t);
        return _masks[kind] = result;
    }

    private static byte Tint(byte art, byte mask, float strength) => (byte)(art * (255f - (255 - mask) * strength) / 255f);

    // Art times the wear map, cached per flag and wear. The flag's art texture is marked readable in its bundle for this.
    private Texture2D Composite(Texture2D source, string kind)
    {
        var key = (source.GetInstanceID(), kind);
        if (_cache.TryGetValue(key, out var done)) return done;
        var mask = Mask(kind);
        if (mask == null) return null;
        if (!source.isReadable) { Logger.LogWarning($"{ModName}: {source.name} is not readable"); return null; }
        var art = source.GetPixels32();
        int step = Mathf.Max(1, source.width / Size);
        var strength = kind == "torn" ? _ripEdges : _dirt;
        var pixels = new Color32[Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            var a = art[Mathf.Min(y * step, source.height - 1) * source.width + Mathf.Min(x * step, source.width - 1)];
            var m = mask[y * Size + x];
            byte alpha = kind == "torn" ? m.a : a.a;
            pixels[y * Size + x] = new Color32(Tint(a.r, m.r, strength), Tint(a.g, m.g, strength), Tint(a.b, m.b, strength), alpha);
        }
        var result = new Texture2D(Size, Size, TextureFormat.RGBA32, true, false) { name = source.name + "_" + kind };
        result.SetPixels32(pixels);
        result.Apply(true, false);
        Logger.LogInfo($"{ModName}: made the {kind} look for {source.name}");
        return _cache[key] = result;
    }
}

// What folding changed on one model, so it can all be put back.
public sealed class FoldState : MonoBehaviour
{
    internal bool Folded;
    private readonly List<Action> _undo = new List<Action>();

    internal void Keep(MeshFilter filter)
    {
        var mesh = filter.sharedMesh;
        _undo.Add(() => { if (filter != null) filter.sharedMesh = mesh; });
    }

    internal void Off(LODGroup group)
    {
        var was = group.enabled;
        group.enabled = false;
        _undo.Add(() => { if (group != null) group.enabled = was; });
    }

    internal void Fit(BoxCollider box, Bounds bounds)
    {
        var (c, s) = (box.center, box.size);
        _undo.Add(() => { if (box != null) { box.center = c; box.size = s; } });
        box.center = bounds.center;
        box.size = bounds.size;
    }

    internal void Fit(MeshCollider hull, Mesh mesh)
    {
        var (was, convex) = (hull.sharedMesh, hull.convex);
        _undo.Add(() => { if (hull != null) { hull.sharedMesh = was; hull.convex = convex; } });
        hull.sharedMesh = mesh;
        hull.convex = true;
    }

    internal void Centre(PreviewPivot pivot, Vector3 point)
    {
        var was = pivot.pivotPosition;
        _undo.Add(() => { if (pivot != null) pivot.pivotPosition = was; });
        pivot.pivotPosition = point;
    }

    internal void Undo()
    {
        for (int i = _undo.Count - 1; i >= 0; i--) { try { _undo[i](); } catch { } }
        _undo.Clear();
        Folded = false;
    }

    private void OnDestroy() => Undo();
}

// A flag folded into a square: a stack of cloth layers, with the flag's busiest square of art on top.
internal static class FoldedFlag
{
    private const float Side = 0.2f, Thickness = 0.044f;
    private const int Layers = 8, Grid = 22, Along = 28;
    private const float ArtShare = 0.8f;      // how much of the flag's short side the top panel shows

    public static Mesh Build(Texture2D art, float clothW, float clothH)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var faces = new List<int>();
        var edges = new List<int>();
        var shortSide = Mathf.Min(clothW, clothH) * ArtShare;
        var du = shortSide / clothW;
        var dv = shortSide / clothH;
        var (u0, v0) = BusiestSquare(art, du, dv);
        Panel(verts, uvs, faces, 1f, u0, v0, du, dv);
        Panel(verts, uvs, faces, -1f, Mathf.Clamp01(1f - u0 - du), v0, du, dv);
        Wall(verts, uvs, edges, new Vector2(1, 0), new Vector2(0, 1), true);
        Wall(verts, uvs, edges, new Vector2(-1, 0), new Vector2(0, -1), true);
        Wall(verts, uvs, edges, new Vector2(0, 1), new Vector2(-1, 0), false);
        Wall(verts, uvs, edges, new Vector2(0, -1), new Vector2(1, 0), false);
        var mesh = new Mesh { name = "wallflag_folded", hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(faces, 0);
        mesh.SetTriangles(edges, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static float Puff(float x, float y)
    {
        // A slight dome with soft wrinkles, flat at the rim so it meets the sides.
        var nx = x / (Side * 0.5f);
        var ny = y / (Side * 0.5f);
        var rim = Mathf.Cos(nx * Mathf.PI * 0.5f) * Mathf.Cos(ny * Mathf.PI * 0.5f);
        var wrinkle = Mathf.Sin(nx * 7.3f + ny * 2.1f) * 0.0006f + Mathf.Sin(ny * 9.1f - nx * 3.7f) * 0.0004f;
        return rim * 0.0042f + wrinkle * Mathf.Clamp01(rim * 3f);
    }

    // Art faces +Z with its U running towards +X, so it reads the right way round from the front.
    private static void Panel(List<Vector3> v, List<Vector2> uv, List<int> tris, float face, float u0, float v0, float du, float dv)
    {
        var start = v.Count;
        for (int j = 0; j <= Grid; j++)
        for (int i = 0; i <= Grid; i++)
        {
            var a = i / (float)Grid;
            var b = j / (float)Grid;
            var x = (a - 0.5f) * Side * face;
            var y = (b - 0.5f) * Side;
            v.Add(new Vector3(x, y, face * (Thickness * 0.5f + Puff(x, y))));
            uv.Add(new Vector2(u0 + a * du, v0 + b * dv));
        }
        for (int j = 0; j < Grid; j++)
        for (int i = 0; i < Grid; i++)
        {
            int p = start + j * (Grid + 1) + i, q = p + 1, r = p + Grid + 1, s = r + 1;
            tris.AddRange(new[] { p, r, q, q, r, s });
        }
    }

    // One side of the stack. Folded sides bulge once per layer; the open sides show the layers' edges, a little out of line.
    private static void Wall(List<Vector3> v, List<Vector2> uv, List<int> tris, Vector2 outward, Vector2 along, bool foldedSide)
    {
        var start = v.Count;
        const int perLayer = 5;
        int rows = Layers * perLayer;
        for (int j = 0; j <= rows; j++)
        {
            var layer = Mathf.Min(j / perLayer, Layers - 1);
            var within = (j % perLayer) / (float)perLayer;
            var h = j / (float)rows;
            for (int i = 0; i <= Along; i++)
            {
                var t = i / (float)Along;
                var line = (t - 0.5f) * Side;
                var bulge = foldedSide ? Mathf.Sin((j % perLayer == 0 ? 0f : within) * Mathf.PI) * 0.0028f : Mathf.Sin(within * Mathf.PI) * 0.0013f + Hash(layer) * 0.0035f;
                bulge *= Mathf.Sin(Mathf.Clamp01(h) * Mathf.PI) * 0.35f + 0.65f;
                bulge *= Mathf.Clamp01(Mathf.Cos(t * Mathf.PI - Mathf.PI * 0.5f) * 3f);
                var at = outward * (Side * 0.5f + bulge) + along * line;
                v.Add(new Vector3(at.x, at.y, (h - 0.5f) * Thickness));
                // A thin strip along the flag's own hem, so the edges are in the flag's colours.
                uv.Add(new Vector2(0.05f + t * 0.9f, 0.015f + within * 0.03f));
            }
        }
        for (int j = 0; j < rows; j++)
        for (int i = 0; i < Along; i++)
        {
            int p = start + j * (Along + 1) + i, q = p + 1, r = p + Along + 1, s = r + 1;
            tris.AddRange(new[] { p, r, q, q, r, s });
        }
    }

    private static float Hash(int n)
    {
        var x = Mathf.Sin(n * 12.9898f) * 43758.5453f;
        return x - Mathf.Floor(x);
    }

    // Where the top panel should look: the square of the art with the most going on in it, so a logo off to one side is what shows.
    private static (float, float) BusiestSquare(Texture2D art, float du, float dv)
    {
        var fallback = (0.5f - du * 0.5f, 0.5f - dv * 0.5f);
        try
        {
            if (art == null || !art.isReadable) return fallback;
            int mip = Mathf.Clamp(art.mipmapCount - 1, 0, 3);
            var px = art.GetPixels32(mip);
            int w = Mathf.Max(1, art.width >> mip), h = Mathf.Max(1, art.height >> mip);
            if (px.Length != w * h) return fallback;
            const int cells = 48;
            var energy = new float[cells, cells];
            for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                var a = px[y * w + x];
                var b = px[y * w + x + 1];
                var c = px[(y + 1) * w + x];
                var edge = Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.r - c.r) + Mathf.Abs(a.g - c.g) + Mathf.Abs(a.b - c.b);
                energy[x * cells / w, y * cells / h] += edge;
            }
            var sum = new float[cells + 1, cells + 1];
            for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
                sum[x + 1, y + 1] = energy[x, y] + sum[x, y + 1] + sum[x + 1, y] - sum[x, y];
            int cw = Mathf.Max(1, Mathf.RoundToInt(du * cells)), ch = Mathf.Max(1, Mathf.RoundToInt(dv * cells));
            float best = -1f;
            var pick = fallback;
            for (int y = 1; y + ch < cells; y++)
            for (int x = 1; x + cw < cells; x++)
            {
                var score = sum[x + cw, y + ch] - sum[x, y + ch] - sum[x + cw, y] + sum[x, y];
                var off = new Vector2((x + cw * 0.5f) / cells - 0.5f, (y + ch * 0.5f) / cells - 0.5f).magnitude;
                score *= 1f - off * 0.45f;               // the middle wins a close call
                if (score > best)
                {
                    best = score;
                    pick = (x / (float)cells, y / (float)cells);
                }
            }
            return pick;
        }
        catch
        {
            return fallback;
        }
    }
}
