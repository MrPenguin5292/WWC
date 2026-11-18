using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ZynTin.Client;

/// <summary>
/// Gives the Zyn tin its look and its use animation. The item is a clone of the Vaseline balm, so the game builds the balm's models for it:
/// the loot / stash icon / inspect model gets the closed tin mesh, and the in-hand model gets the tin body, the flip lid and a pouch on the
/// pouch on its own transform under `weapon` (moved by ZynPouchDriver from the USE time), plus an AnimatorOverrideController that plays zyn_use in place of the balm's use clip. The game's animation-event emitter is
/// re-bound after the swap (WeaponPrefab.RebindAnimator), because a new controller brings new state behaviours. Every pooled model is put back
/// (meshes, materials, controller, pouch) before it returns to the pool, since the pool also serves the real Vaseline balm. Any failure leaves
/// the balm's own look and animation.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Zyn tin", "0.3.0")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Zyn Tins");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("ZynTin");
#endif
    public const string Id = "local.zyntin.client";
    internal static Plugin? Instance;
    internal ZynData Data = new();
    internal readonly Dictionary<string, Mesh> Meshes = new();
    internal readonly Dictionary<string, Texture> Textures = new();
    internal AnimationClip? UseClip;
    internal PouchTrack? Track;
    readonly Dictionary<(int, string), Material> copies = new();
    readonly Dictionary<int, AnimatorOverrideController> overrides = new();
    readonly HashSet<string> errors = new();
    AssetBundle? bundle;
    Harmony? harmony;
    static FieldInfo? objectInstanceField, playerField;
    public int AppliedLoot { get; private set; }
    public int AppliedHands { get; private set; }
    public int Restored { get; private set; }

    void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build needs EFT 40743 / Unity 2022.3.43f1.");
            var root = Path.GetDirectoryName(Info.Location)!;
            Data = JsonConvert.DeserializeObject<ZynData>(File.ReadAllText(Path.Combine(root, "zyn.json"))) ?? throw new InvalidDataException("zyn.json is empty");
            bundle = AssetBundle.LoadFromFile(Path.Combine(root, Data.bundle)) ?? throw new FileNotFoundException("Zyn assets are missing.", Data.bundle);
            foreach (var name in Data.hands.Values.Concat(Data.loot.Values).Append(Data.pouch.mesh).Distinct())
                Meshes[name] = bundle.LoadAsset<Mesh>(name) ?? throw new InvalidDataException("mesh " + name + " not in the bundle");
            foreach (var name in Data.textures.Values.Concat(Data.items.Values).Distinct())
                Textures[name] = bundle.LoadAsset<Texture2D>(name) ?? throw new InvalidDataException("texture " + name + " not in the bundle");
            ApplyLidPictures(root);
            UseClip =bundle.LoadAsset<AnimationClip>(Data.clip) ?? throw new InvalidDataException("clip " + Data.clip + " not in the bundle");
            var track = bundle.LoadAsset<TextAsset>(Data.pouch.track);
            if (track != null) Track = JsonConvert.DeserializeObject<PouchTrack>(track.text);
            else Logger.LogWarning("Zyn tin: no pouch track in the bundle; the pouch stays hidden");
            objectInstanceField = AccessTools.Field(typeof(WeaponPrefab), "_objectInstance") ?? throw new MissingFieldException("WeaponPrefab._objectInstance");
            playerField = AccessTools.Field(typeof(Player.MedsController), "_player");
            Instance = this;
            harmony = new Harmony(Id);
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Item)))
            {
                if (m.ReturnType == typeof(Task<GameObject>) && (m.Name == nameof(ObjectsFactory.CreateItemAsync) || m.Name == "CreateCleanLootPrefabAsync"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreate)));
                else if (m.ReturnType == typeof(GameObject) && (m.Name == "CreateLootPrefab" || m.Name == "CreateCleanLootPrefab" || m.Name == "CreateItem"))
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCreateNow)));
            }
            var spawn = AccessTools.DeclaredMethod(typeof(Player.MedsController), nameof(Player.MedsController.Spawn)) ?? throw new MissingMethodException("MedsController.Spawn");
            harmony.Patch(spawn, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeHandsSpawn)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(WeaponPrefab), nameof(WeaponPrefab.ReturnToPool), Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturn)));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(AssetPoolObject)).Where(m => m.Name == "ReturnToPool"))
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReturn)));
            Logger.LogInfo($"Zyn tin ready ({Data.items.Count} flavours).");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Instance = null;
            Logger.LogError("Zyn tin unavailable; the tin looks and moves like the Vaseline balm it is built on. " + error);
        }
    }

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"Zyn tin: {key}: {error}");
    }

    internal void Note(string key, string text)
    {
        if (errors.Add("note:" + key)) Logger.LogInfo("Zyn tin: " + text);
    }

    // ---------------------------------------------------------------- lid pictures

    /// <summary>Widowz 03:41 UTC: "I want them to match all the irl ones exactly". Any picture in lids/ named after a tin (siberia_maxi.png,
    /// coolmint.jpg, ...) replaces that tin's lid art: the picture's centred square is fitted into the lid circle (crop it so the lid's
    /// circle touches the edges). The rest of the tin keeps its own colours. A bad picture is skipped and the drawn lid stays.</summary>
    void ApplyLidPictures(string root)
    {
        var dir = Path.Combine(root, "lids");
        if (!Directory.Exists(dir)) return;
        int used = 0;
        foreach (var albedo in Data.items.Values.Distinct())
        {
            var key = albedo.StartsWith("a_zyn_", StringComparison.Ordinal) ? albedo.Substring(6) : albedo;
            var file = new[] { ".png", ".jpg", ".jpeg" }.Select(e => Path.Combine(dir, key + e)).FirstOrDefault(File.Exists);
            if (file == null || !Textures.TryGetValue(albedo, out var drawn)) continue;
            try
            {
                Textures[albedo] = WithLidPicture(drawn, File.ReadAllBytes(file), albedo + "_lidpicture");
                used++;
                Logger.LogInfo($"Zyn tin: lid picture {Path.GetFileName(file)} on {key}");
            }
            catch (Exception e) { Logger.LogWarning($"Zyn tin: lid picture {Path.GetFileName(file)} not used ({e.Message}); {key} keeps its drawn lid"); }
        }
        var strays = Directory.GetFiles(dir).Where(f => !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            && !Data.items.Values.Any(a => Path.GetFileNameWithoutExtension(f).Equals(a.Substring(Math.Min(6, a.Length)), StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var f in strays) Logger.LogWarning($"Zyn tin: lids/{Path.GetFileName(f)} matches no tin name (see lids/README.txt)");
        if (used > 0) Logger.LogInfo($"Zyn tin: {used} lid picture(s) in use");
    }

    Texture2D WithLidPicture(Texture drawn, byte[] bytes, string name)
    {
        var pic = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(pic, bytes, false)) throw new InvalidDataException("not a PNG or JPG");
            int w = drawn.width, h = drawn.height;
            // the bundle's texture is not readable: copy it through a render texture
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var before = RenderTexture.active;
            Graphics.Blit(drawn, rt);
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { name = name, wrapMode = TextureWrapMode.Clamp, anisoLevel = drawn.anisoLevel, hideFlags = HideFlags.DontUnloadUnusedAsset };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels32();
            float cx = Data.lidDisc[0] * w, cy = Data.lidDisc[1] * h, r = Data.lidDisc[2] * w;
            int side = Math.Min(pic.width, pic.height);
            float u0 = (pic.width - side) / 2f / pic.width, v0 = (pic.height - side) / 2f / pic.height, su = side / (float)pic.width, sv = side / (float)pic.height;
            byte spec = (byte)Mathf.Clamp(Data.lidSpecular, 0, 255);
            for (int y = Mathf.Max(0, (int)(cy - r - 2)); y < Mathf.Min(h, (int)(cy + r + 2)); y++)
                for (int x = Mathf.Max(0, (int)(cx - r - 2)); x < Mathf.Min(w, (int)(cx + r + 2)); x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float cover = Mathf.Clamp01(r + 0.5f - d);
                    if (cover <= 0f) continue;
                    var c = pic.GetPixelBilinear(u0 + (dx / r + 1f) / 2f * su, v0 + (dy / r + 1f) / 2f * sv);
                    var lid = Color.Lerp(Color.white, c, c.a);             // see-through parts of a picture are white plastic
                    var o = px[y * w + x];
                    px[y * w + x] = new Color32((byte)Mathf.Lerp(o.r, lid.r * 255f, cover), (byte)Mathf.Lerp(o.g, lid.g * 255f, cover),
                        (byte)Mathf.Lerp(o.b, lid.b * 255f, cover), (byte)Mathf.Lerp(o.a, spec, cover));
                }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            tex.Compress(true);
            tex.Apply(false, true);
            return tex;
        }
        finally { Destroy(pic); }
    }

    // ---------------------------------------------------------------- loot, stash icon, inspect window

    static void AfterCreate(Item __0, ref Task<GameObject?> __result)
    {
        var p = Instance;
        if (p is null || __0 is null) return;
        var tpl = __0.StringTemplateId;
        if (!p.Data.items.ContainsKey(tpl))
        {
            if (__result.IsCompleted && __result.Status == TaskStatus.RanToCompletion && __result.Result != null) RestoreStale(__result.Result);
            return;
        }
        if (__result.IsCompleted)
        {
            if (__result.Status == TaskStatus.RanToCompletion && __result.Result != null) p.ApplyLoot(__result.Result, tpl);
            return;
        }
        __result = p.ApplyLootAsync(__result, tpl);
    }

    static void AfterCreateNow(Item __0, GameObject? __result)
    {
        var p = Instance;
        if (p is null || __0 is null || __result == null) return;
        if (!p.Data.items.ContainsKey(__0.StringTemplateId)) { RestoreStale(__result); return; }
        p.ApplyLoot(__result, __0.StringTemplateId);
    }

    /// <summary>A model built for some other item that still wears tin changes (pool hand-back missed): put it back.</summary>
    static void RestoreStale(GameObject model)
    {
        try { var st = model.GetComponent<ZynState>(); if (st != null && st.Restore()) Instance?.Note("stale-loot", "a pooled model still dressed as a tin was restored"); }
        catch (Exception error) { Instance?.ReportOnce("stale", error); }
    }

    async Task<GameObject?> ApplyLootAsync(Task<GameObject?> task, string tpl)
    {
        var model = await task;
        if (model != null) ApplyLoot(model, tpl);
        return model;
    }

    internal bool ApplyLoot(GameObject model, string tpl)
    {
        var state = model.GetComponent<ZynState>() ?? model.AddComponent<ZynState>();
        state.Restore();
        try
        {
            int done = 0;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var parent = mf.transform.parent != null ? mf.transform.parent.name : "";
                string? meshName = null;
                if (parent == "mod_item" && Data.hands.TryGetValue(mf.name, out var h)) meshName = h;          // a hands model built as loot
                else if (parent == "cap" && Data.hands.TryGetValue(mf.name, out var c)) meshName = c;
                else if (Data.loot.TryGetValue(mf.name, out var l)) meshName = l;
                if (meshName == null) continue;
                state.SetMesh(mf, Meshes[meshName]);
                var r = mf.GetComponent<Renderer>(); if (r != null) state.SetMaterials(r, Materials(r, tpl));
                done++;
            }
            foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
            // the balm's box collider is 6 x 1.07 cm; the tin is 6.4 x 2 cm and would sink into the floor when dropped
            if (Data.loot.TryGetValue(Data.lootLod0, out var lod0) && Meshes.TryGetValue(lod0, out var closed))
                foreach (var box in model.GetComponentsInChildren<BoxCollider>(true))
                    if (box.gameObject.name == Data.lootCollider) state.SetBox(box, closed.bounds.center, closed.bounds.size);
            if (done > 0) AppliedLoot++;
            return done > 0;
        }
        catch (Exception error) { state.Restore(); ReportOnce("loot", error); return false; }
    }

    Material[] Materials(Renderer r, string tpl) => r.sharedMaterials.Select(m => m == null ? m! : Copy(m, tpl)).ToArray();

    /// <summary>One copy of each Vaseline material per flavour, wearing the tin's maps (same shader, so the game's lighting and reflections hold).</summary>
    Material Copy(Material donor, string tpl)
    {
        var key = (donor.GetInstanceID(), tpl);
        if (copies.TryGetValue(key, out var copy) && copy != null) return copy;
        copy = new Material(donor) { name = donor.name + "_zyn_" + Data.items[tpl], hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (copy.HasProperty(Data.albedoProperty)) copy.SetTexture(Data.albedoProperty, Textures[Data.items[tpl]]);
        foreach (var kv in Data.textures) if (copy.HasProperty(kv.Key)) copy.SetTexture(kv.Key, Textures[kv.Value]);
        if (Data.color.Length == 4 && copy.HasProperty("_Color")) copy.SetColor("_Color", new Color(Data.color[0], Data.color[1], Data.color[2], Data.color[3]));
        foreach (var kv in Data.floats) if (copy.HasProperty(kv.Key)) copy.SetFloat(kv.Key, kv.Value);
        return copies[key] = copy;
    }

    // ---------------------------------------------------------------- in hands

    static void BeforeHandsSpawn(Player.MedsController __instance)
    {
        var p = Instance;
        if (p is null || __instance == null) return;
        try
        {
            var item = __instance.Item;
            if (item == null) return;
            var container = __instance.ControllerGameObject;
            var wp = container != null ? container.GetComponent<WeaponPrefab>() : null;
            if (wp == null) return;
            if (!p.Data.items.ContainsKey(item.StringTemplateId))
            {
                // a real balm on a pooled model that somehow kept the tin's changes: put it back first (never seen, cheap to guard)
                var stale = wp.GetComponent<ZynState>();
                if (stale != null && stale.Restore())
                {
                    var stalePlayer = playerField?.GetValue(__instance) as IPlayer;
                    if (stalePlayer != null) wp.RebindAnimator(stalePlayer);
                    p.Note("stale-hands", "a Vaseline balm got a pooled model still dressed as a tin; restored");
                }
                return;
            }
            var player = playerField?.GetValue(__instance) as IPlayer;
            if (p.ApplyHands(wp, player, item.StringTemplateId)) p.AppliedHands++;
        }
        catch (Exception error) { p.ReportOnce("hands", error); }
    }

    internal bool ApplyHands(WeaponPrefab wp, IPlayer? player, string tpl)
    {
        var instance = objectInstanceField!.GetValue(wp) as GameObject;
        if (instance == null) throw new InvalidOperationException("the balm's hands model is not built yet");
        var state = wp.GetComponent<ZynState>() ?? wp.gameObject.AddComponent<ZynState>();
        if (state.HandsApplied && state.Tpl == tpl && state.Animator != null && state.Animator.runtimeAnimatorController == state.Override) return true;
        state.Restore();
        try
        {
            foreach (var mf in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!Data.hands.TryGetValue(mf.name, out var meshName)) continue;
                state.SetMesh(mf, Meshes[meshName]);
                var r = mf.GetComponent<Renderer>(); if (r != null) state.SetMaterials(r, Materials(r, tpl));
            }
            var parent = FindChild(instance.transform, Data.pouch.parent) ?? throw new InvalidOperationException("no " + Data.pouch.parent + " bone");
            var bodyRenderer = instance.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => Data.hands.ContainsKey(r.name));
            state.AddPouch(parent, Data.pouch.name, Meshes[Data.pouch.mesh], bodyRenderer);
            var animatorForPouch = instance.GetComponent<Animator>();
            if (Track != null && animatorForPouch != null) state.DrivePouch(animatorForPouch, Track, Data.pouch.state, Data.pouch.layer);
            var animator = instance.GetComponent<Animator>() ?? throw new InvalidOperationException("the balm's hands model has no animator");
            var original = animator.runtimeAnimatorController ?? throw new InvalidOperationException("the balm's animator has no controller");
            if (!overrides.TryGetValue(original.GetInstanceID(), out var ov) || ov == null)
            {
                ov = new AnimatorOverrideController(original) { name = "zyn_use override" };
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                ov.GetOverrides(pairs);
                int n = 0;
                for (int i = 0; i < pairs.Count; i++)
                    if (pairs[i].Key != null && pairs[i].Key.name == Data.replaces) { pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(pairs[i].Key, UseClip!); n++; }
                if (n == 0) throw new InvalidOperationException($"{Data.replaces} not found in the balm's controller ({pairs.Count} clips)");
                ov.ApplyOverrides(pairs);
                overrides[original.GetInstanceID()] = ov;
                Note("override", $"use clip swapped ({n} of {pairs.Count} clips in {original.name})");
            }
            state.SetController(animator, original, ov);
            // the new controller brings new state behaviours: re-subscribe the game's animation-event emitter to them
            if (player != null) wp.RebindAnimator(player);
            else animator.Rebind();
            state.HandsApplied = true; state.Tpl = tpl;
            return true;
        }
        catch (Exception error) { state.Restore(); ReportOnce("hands", error); return false; }
    }

    static Transform? FindChild(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    static void BeforeReturn(AssetPoolObject __instance)
    {
        if (__instance == null) return;
        try
        {
            var state = __instance.GetComponent<ZynState>();
            if (state != null && state.Restore() && Instance != null) Instance.Restored++;
        }
        catch (Exception error) { Instance?.ReportOnce("return", error); }
    }
}

/// <summary>Everything changed on one pooled model, so it can be put back exactly.</summary>
public sealed class ZynState : MonoBehaviour
{
    readonly List<(MeshFilter mf, Mesh mesh)> meshes = new();
    readonly List<(Renderer r, Material[] mats)> materials = new();
    GameObject? pouch;
    public Animator? Animator; RuntimeAnimatorController? original; public AnimatorOverrideController? Override;
    public bool HandsApplied; public string Tpl = "";

    public void SetMesh(MeshFilter mf, Mesh mesh)
    {
        if (!meshes.Any(x => x.mf == mf)) meshes.Add((mf, mf.sharedMesh));
        mf.sharedMesh = mesh;
    }

    readonly List<(BoxCollider box, Vector3 center, Vector3 size)> boxes = new();

    public void SetBox(BoxCollider box, Vector3 center, Vector3 size)
    {
        if (!boxes.Any(x => x.box == box)) boxes.Add((box, box.center, box.size));
        box.center = center; box.size = size;
    }

    public void SetMaterials(Renderer r, Material[] mats)
    {
        if (!materials.Any(x => x.r == r)) materials.Add((r, r.sharedMaterials));
        r.sharedMaterials = mats;
    }

    /// <summary>The pouch is its own transform under `weapon`, named as the clip's curves expect; the animator binds it on the re-bind that
    /// follows. Its scale starts at 0, which the animator keeps as the resting value (hidden whenever the clip doesn't show it).</summary>
    public void AddPouch(Transform parent, string name, Mesh mesh, Renderer? like)
    {
        pouch = new GameObject(name);
        pouch.layer = like != null ? like.gameObject.layer : parent.gameObject.layer;
        pouch.transform.SetParent(parent, false);
        pouch.transform.localScale = Vector3.zero;
        pouch.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = pouch.AddComponent<MeshRenderer>();
        if (like != null) { mr.sharedMaterials = like.sharedMaterials; mr.shadowCastingMode = like.shadowCastingMode; }
    }

    public void DrivePouch(Animator animator, PouchTrack track, string state, string layer)
    {
        if (pouch == null) return;
        var d = pouch.AddComponent<ZynPouchDriver>();
        d.Init(animator, track, state, layer);
    }

    public void SetController(Animator animator, RuntimeAnimatorController originalController, AnimatorOverrideController ov)
    {
        Animator = animator; original = originalController; Override = ov;
        animator.runtimeAnimatorController = ov;
    }

    /// <summary>Puts the model back the way the game made it. Returns true when something was changed.</summary>
    public bool Restore()
    {
        bool any = meshes.Count + materials.Count + boxes.Count > 0 || pouch != null || Animator != null;
        foreach (var (box, center, size) in boxes) if (box != null) { box.center = center; box.size = size; }
        boxes.Clear();
        if (pouch != null) { Destroy(pouch); pouch = null; }
        foreach (var (mf, mesh) in meshes) if (mf != null) mf.sharedMesh = mesh;
        foreach (var (r, mats) in materials) if (r != null) r.sharedMaterials = mats;
        meshes.Clear(); materials.Clear();
        if (Animator != null && original != null && Animator.runtimeAnimatorController == Override) Animator.runtimeAnimatorController = original;
        Animator = null; original = null; Override = null; HandsApplied = false; Tpl = "";
        return any;
    }
}

/// <summary>The pouch's pose per frame of zyn_use, local to `weapon` (from ZynCompose): x y z, qx qy qz qw, visible.</summary>
public sealed class PouchTrack
{
    public int fps = 30;
    public float length;
    public float[][] frames = Array.Empty<float[]>();
}

/// <summary>
/// Moves the pouch from the Hands layer's USE time. The game's animator never binds a transform added to the hands model at runtime, not
/// even after a rebind (fixture runs 1 and 2, 2026-11-17), so the pouch's curves in the clip do nothing on their own. Hidden in every other
/// state (draw, cancel, end). Never throws into the update loop: on a problem it hides the pouch and switches itself off.
/// </summary>
public sealed class ZynPouchDriver : MonoBehaviour
{
    Animator? animator; PouchTrack? track; int stateHash, layer = -1; string layerName = "Hands";

    public void Init(Animator a, PouchTrack t, string state, string layerName)
    {
        animator = a; track = t; stateHash = Animator.StringToHash(state); this.layerName = layerName; layer = -1;
        transform.localScale = Vector3.zero;
    }

    void LateUpdate()
    {
        try
        {
            if (animator == null || track == null || track.frames.Length < 2 || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) { Hide(); return; }
            if (layer < 0 || layer >= animator.layerCount) { layer = animator.GetLayerIndex(layerName); if (layer < 0) { Hide(); return; } }
            var st = animator.GetCurrentAnimatorStateInfo(layer);
            if (st.shortNameHash != stateHash) { Hide(); return; }
            float time = Mathf.Clamp01(st.normalizedTime) * track.length;
            float fi = Mathf.Clamp(time * track.fps, 0f, track.frames.Length - 1.001f);
            int i = (int)fi; float w = fi - i;
            var a = track.frames[i]; var b = track.frames[i + 1];
            if (a[7] < 0.5f) { Hide(); return; }
            transform.localPosition = Vector3.Lerp(new Vector3(a[0], a[1], a[2]), new Vector3(b[0], b[1], b[2]), w);
            transform.localRotation = Quaternion.Slerp(new Quaternion(a[3], a[4], a[5], a[6]), new Quaternion(b[3], b[4], b[5], b[6]), w);
            transform.localScale = Vector3.one;
        }
        catch { Hide(); enabled = false; }
    }

    void Hide() { if (transform.localScale != Vector3.zero) transform.localScale = Vector3.zero; }
}

public sealed class ZynData
{
    /// <summary>template id -> colour map (one per flavour)</summary>
    public Dictionary<string, string> items = new();
    public string albedoProperty = "_MainTex";
    /// <summary>The lid circle in the colour map: centre u, centre v (v up, as Unity samples it), radius, in 0-1 of the width.</summary>
    public float[] lidDisc = { 0.25f, 0.75f, 0.235f };
    /// <summary>Colour-map alpha on the lid (the EFT shader's specular): the drawn lids use 85.</summary>
    public int lidSpecular = 85;
    public string bundle = "zyn.bundle";
    public string clip = "zyn_use";
    public string replaces = "item_vaselin_use";
    public Dictionary<string, string> hands = new();
    public Dictionary<string, string> loot = new();
    public string lootLod0 = "item_vaselin_LOD0";
    public string lootCollider = "collider";
    public PouchData pouch = new();
    public Dictionary<string, string> textures = new();
    public float[] color = Array.Empty<float>();
    public Dictionary<string, float> floats = new();
}

public sealed class PouchData
{
    public string mesh = "zyn_pouch";
    public string parent = "weapon";
    public string name = "zyn_pouch";
    public string track = "zyn_pouch_track";
    public string state = "USE";
    public string layer = "Hands";
}
