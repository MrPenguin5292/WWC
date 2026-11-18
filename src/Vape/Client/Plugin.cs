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

namespace Vape.Client;

/// <summary>
/// Gives the disposable vape its look and its use animation. Each vape is a clone of Analgin, so the game builds Analgin's models for
/// it: the loot / stash icon / inspect model gets the vape mesh, and the in-hand model gets the vape on the box renderer, the pills
/// hidden, a hit-counter screen and an LED as children, and an AnimatorOverrideController that plays vape_use in place of Analgin's use
/// clip. The game's animation-event emitter is re-bound after the swap (WeaponPrefab.RebindAnimator). VapeDriver follows the Hands
/// layer's USE time: LED while drawing, inhale and exhale sounds through the game's own sound player, the vapour cloud at the mouth,
/// the screen counting down. Every pooled model is put back (meshes, materials, hidden renderers, sounds, controller) before it
/// returns to the pool, since the pool also serves real Analgin. Any failure leaves Analgin's own look and animation.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Disposable vape", "0.1.4")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Vape");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("Vape");
#endif
    public const string Id = "local.vape.client";
    internal static Plugin? Instance;
    internal VapeData Data = new();
    internal readonly Dictionary<string, Mesh> Meshes = new();
    internal readonly Dictionary<string, Texture> Textures = new();
    internal readonly Dictionary<string, Material> Materials = new();
    internal readonly Dictionary<string, AudioClip> Clips = new();
    internal AnimationClip? UseClip;
    internal VapeTiming Timing = new();
    internal GameObject? CloudPrefab;
    readonly Dictionary<(int, string), Material> copies = new();
    readonly Dictionary<int, AnimatorOverrideController> overrides = new();
    readonly HashSet<string> errors = new();
    AssetBundle? bundle;
    Harmony? harmony;
    static FieldInfo? objectInstanceField, playerField;
    public int AppliedLoot { get; private set; }
    public int AppliedHands { get; private set; }
    public int Restored { get; private set; }
    public int Puffs { get; internal set; }
    public string CloudShader { get; private set; } = "";
    Material? cloudMaterial; bool cloudTried;

    /// <summary>The vapour's material, made on first use (the game's shader collection is loaded by then).</summary>
    internal Material? CloudMaterial()
    {
        if (cloudTried) return cloudMaterial;
        cloudTried = true;
        Materials.TryGetValue(Data.cloud.material, out var bundled);
        var tex = bundled != null ? bundled.mainTexture : null;
        foreach (var name in Data.cloud.shaders)
        {
            var sh = Shader.Find(name);
            if (sh == null || tex == null) continue;
            cloudMaterial = new Material(sh) { name = "vape_cloud (" + name + ")", mainTexture = tex, renderQueue = 3000, hideFlags = HideFlags.DontUnloadUnusedAsset };
            var t = Data.cloud.tint;
            if (cloudMaterial.HasProperty("_TintColor") && t.Length == 4) cloudMaterial.SetColor("_TintColor", new Color(t[0], t[1], t[2], t[3]));
            if (cloudMaterial.HasProperty("_InvFade")) cloudMaterial.SetFloat("_InvFade", 1f);
            var ml = Data.cloud.minimalLight;
            if (cloudMaterial.HasProperty("_LocalMinimalAmbientLight") && ml.Length == 4) cloudMaterial.SetColor("_LocalMinimalAmbientLight", new Color(ml[0], ml[1], ml[2], ml[3]));
            CloudShader = name;
            break;
        }
        if (cloudMaterial == null && bundled != null)
        {
            cloudMaterial = new Material(bundled) { name = "vape_cloud (bundled)", hideFlags = HideFlags.DontUnloadUnusedAsset };
            var t = Data.cloud.fallbackTint;
            if (cloudMaterial.HasProperty("_TintColor") && t.Length == 4) cloudMaterial.SetColor("_TintColor", new Color(t[0], t[1], t[2], t[3]));
            CloudShader = bundled.shader != null ? bundled.shader.name : "?";
        }
        Note("cloud-material", "vapour drawn with " + (CloudShader.Length > 0 ? CloudShader : "nothing (no material)"));
        return cloudMaterial;
    }

    void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build needs EFT 40743 / Unity 2022.3.43f1.");
            var root = Path.GetDirectoryName(Info.Location)!;
            Data = JsonConvert.DeserializeObject<VapeData>(File.ReadAllText(Path.Combine(root, "vape.json"))) ?? throw new InvalidDataException("vape.json is empty");
            bundle = AssetBundle.LoadFromFile(Path.Combine(root, Data.bundle)) ?? throw new FileNotFoundException("vape assets are missing.", Data.bundle);
            foreach (var name in Data.hands.Values.Concat(Data.loot.Values).Append(Data.screen.mesh).Append(Data.led.mesh).Distinct())
                Meshes[name] = bundle.LoadAsset<Mesh>(name) ?? throw new InvalidDataException("mesh " + name + " not in the bundle");
            foreach (var name in Data.textures.Values.Concat(Data.items.Values.Select(i => i.albedo)).Distinct())
                Textures[name] = bundle.LoadAsset<Texture2D>(name) ?? throw new InvalidDataException("texture " + name + " not in the bundle");
            foreach (var name in new[] { Data.screen.material, Data.led.material, Data.cloud.material }.Distinct())
                Materials[name] = bundle.LoadAsset<Material>(name) ?? throw new InvalidDataException("material " + name + " not in the bundle");
            foreach (var name in new[] { Data.sounds.inhale, Data.sounds.exhale })
                Clips[name] = bundle.LoadAsset<AudioClip>(name) ?? throw new InvalidDataException("sound " + name + " not in the bundle");
            CloudPrefab = bundle.LoadAsset<GameObject>(Data.cloud.prefab) ?? throw new InvalidDataException("prefab " + Data.cloud.prefab + " not in the bundle");
            UseClip = bundle.LoadAsset<AnimationClip>(Data.clip) ?? throw new InvalidDataException("clip " + Data.clip + " not in the bundle");
            var timing = bundle.LoadAsset<TextAsset>(Data.timing) ?? throw new InvalidDataException("timing " + Data.timing + " not in the bundle");
            Timing = JsonConvert.DeserializeObject<VapeTiming>(timing.text) ?? throw new InvalidDataException("timing is empty");
            if (Timing.length <= 0.1f) throw new InvalidDataException("timing has no clip length");
            ApplyPictures(root);
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
            Logger.LogInfo($"Disposable vape ready ({Data.items.Count} flavours, clip {Timing.length:0.00} s).");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Instance = null;
            Logger.LogError("Disposable vape unavailable; the vapes look and move like the Analgin they are built on. " + error);
        }
    }

    internal void ReportOnce(string key, Exception error)
    {
        if (errors.Add(key)) Logger.LogError($"Disposable vape: {key}: {error}");
    }

    internal void Note(string key, string text)
    {
        if (errors.Add("note:" + key)) Logger.LogInfo("Disposable vape: " + text);
    }

    // ---------------------------------------------------------------- the player's own pictures

    /// <summary>Pictures in art/ named after a flavour replace part of its paint: <key>.png = the front below the screen, <key>_back.png =
    /// the back, <key>_wrap.png = the whole colour map (same layout as art/template_wrap.png). Pictures are cropped from the centre to fit;
    /// see-through parts keep the drawn paint. A bad picture is skipped and the drawn paint stays. None ship with the mod.</summary>
    void ApplyPictures(string root)
    {
        var dir = Path.Combine(root, Data.artFolder);
        if (!Directory.Exists(dir)) return;
        int used = 0;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var made = new HashSet<Texture>();   // a flavour with two pictures is painted twice: the in-between texture is let go
        foreach (var item in Data.items.Values)
        {
            var key = item.albedo.StartsWith("a_vape_", StringComparison.Ordinal) ? item.albedo.Substring(7) : item.albedo;
            foreach (var (suffix, rect) in new[] { ("_wrap", (float[]?)null), ("", Data.frontPanel), ("_back", Data.backPanel) })
            {
                known.Add(key + suffix);
                var file = new[] { ".png", ".jpg", ".jpeg" }.Select(e => Path.Combine(dir, key + suffix + e)).FirstOrDefault(File.Exists);
                if (file == null || !Textures.TryGetValue(item.albedo, out var drawn)) continue;
                try
                {
                    // a phone photo is fine; a 100 MB scan would stall the game while it loads
                    if (new FileInfo(file).Length > 32L << 20) throw new InvalidDataException("bigger than 32 MB; save it smaller (2048 px is plenty)");
                    var painted = WithPicture(drawn, File.ReadAllBytes(file), rect, item.albedo + suffix + "_picture");
                    Textures[item.albedo] = painted; made.Add(painted);
                    if (made.Remove(drawn)) Destroy(drawn);
                    used++;
                    Logger.LogInfo($"Disposable vape: picture {Path.GetFileName(file)} on {key}");
                }
                catch (Exception e) { Logger.LogWarning($"Disposable vape: picture {Path.GetFileName(file)} not used ({e.Message}); {key} keeps its paint"); }
            }
        }
        foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(f).StartsWith("template", StringComparison.OrdinalIgnoreCase)
                     && !known.Contains(Path.GetFileNameWithoutExtension(f))))
            Logger.LogWarning($"Disposable vape: {Data.artFolder}/{Path.GetFileName(f)} matches no flavour name (see {Data.artFolder}/README.txt)");
        if (used > 0) Logger.LogInfo($"Disposable vape: {used} picture(s) in use");
    }

    Texture2D WithPicture(Texture drawn, byte[] bytes, float[]? rect, string name)
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
            var r = rect ?? new[] { 0f, 0f, 1f, 1f };
            int x0 = Mathf.RoundToInt(r[0] * w), y0 = Mathf.RoundToInt(r[1] * h), x1 = Mathf.RoundToInt(r[2] * w), y1 = Mathf.RoundToInt(r[3] * h);
            float aspect = (x1 - x0) / (float)Mathf.Max(1, y1 - y0), picAspect = pic.width / (float)pic.height;
            // cover: crop the picture's centre to the panel's shape
            float su = 1f, sv = 1f;
            if (rect != null) { if (picAspect > aspect) su = aspect / picAspect; else sv = picAspect / aspect; }
            byte spec = (byte)Mathf.Clamp(Data.pictureSpecular, 0, 255);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    float u = (x + 0.5f - x0) / (x1 - x0), v = (y + 0.5f - y0) / (y1 - y0);
                    var c = pic.GetPixelBilinear(0.5f + (u - 0.5f) * su, 0.5f + (v - 0.5f) * sv);
                    var o = px[y * w + x];
                    px[y * w + x] = new Color32((byte)Mathf.Lerp(o.r, c.r * 255f, c.a), (byte)Mathf.Lerp(o.g, c.g * 255f, c.a), (byte)Mathf.Lerp(o.b, c.b * 255f, c.a),
                        rect == null ? o.a : (byte)Mathf.Lerp(o.a, spec, c.a));
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

    /// <summary>A model built for some other item that still wears vape changes (pool hand-back missed): put it back.</summary>
    static void RestoreStale(GameObject model)
    {
        try { var st = model.GetComponent<VapeState>(); if (st != null && st.Restore()) Instance?.Note("stale-loot", "a pooled model still dressed as a vape was restored"); }
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
        var state = model.GetComponent<VapeState>() ?? model.AddComponent<VapeState>();
        state.Restore();
        try
        {
            int done = 0;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var parent = mf.transform.parent != null ? mf.transform.parent.name : "";
                string? meshName = null;
                if (parent == "mod_item" && Data.hands.TryGetValue(mf.name, out var h)) meshName = h;           // a hands model built as loot
                else if (Data.loot.TryGetValue(mf.name, out var l)) meshName = l;
                var r = mf.GetComponent<Renderer>();
                if (meshName == null)
                {
                    if (r != null && Data.hide.Contains(mf.name)) state.Hide(r);
                    continue;
                }
                state.SetMesh(mf, Meshes[meshName]);
                if (r != null) state.SetMaterials(r, BodyMaterials(r, tpl));
                done++;
            }
            foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
            // Analgin's collider is a 14 x 6.7 x 0.65 cm blister; the vape is 9.5 x 4.4 x 2.3 cm and would sink into the floor
            if (Data.loot.TryGetValue(Data.lootLod0, out var lod0) && Meshes.TryGetValue(lod0, out var body))
                foreach (var box in model.GetComponentsInChildren<BoxCollider>(true))
                    if (box.gameObject.name == Data.lootCollider) state.SetBox(box, body.bounds.center, body.bounds.size);
            if (done > 0) AppliedLoot++;
            return done > 0;
        }
        catch (Exception error) { state.Restore(); ReportOnce("loot", error); return false; }
    }

    Material[] BodyMaterials(Renderer r, string tpl) => r.sharedMaterials.Select(m => m == null ? m! : Copy(m, tpl)).ToArray();

    /// <summary>One copy of each Analgin material per flavour, wearing the vape's maps (same shader, so the game's lighting holds).</summary>
    Material Copy(Material donor, string tpl)
    {
        var key = (donor.GetInstanceID(), tpl);
        if (copies.TryGetValue(key, out var copy) && copy != null) return copy;
        var item = Data.items[tpl];
        copy = new Material(donor) { name = donor.name + "_vape_" + item.albedo, hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (copy.HasProperty(Data.albedoProperty)) copy.SetTexture(Data.albedoProperty, Textures[item.albedo]);
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
                // real Analgin on a pooled model that somehow kept the vape's changes: put it back first (cheap to guard)
                var stale = wp.GetComponent<VapeState>();
                if (stale != null && stale.Restore())
                {
                    var stalePlayer = playerField?.GetValue(__instance) as IPlayer;
                    if (stalePlayer != null) wp.RebindAnimator(stalePlayer);
                    p.Note("stale-hands", "an Analgin got a pooled model still dressed as a vape; restored");
                }
                return;
            }
            var player = playerField?.GetValue(__instance) as IPlayer;
            int hits = -1;
            try { var mk = item.GetItemComponent<MedKitComponent>(); if (mk != null) hits = Mathf.RoundToInt(mk.HpResource); } catch { }
            if (p.ApplyHands(wp, player, item.StringTemplateId, hits)) p.AppliedHands++;
        }
        catch (Exception error) { p.ReportOnce("hands", error); }
    }

    internal bool ApplyHands(WeaponPrefab wp, IPlayer? player, string tpl, int hits)
    {
        var instance = objectInstanceField!.GetValue(wp) as GameObject;
        if (instance == null) throw new InvalidOperationException("Analgin's hands model is not built yet");
        var state = wp.GetComponent<VapeState>() ?? wp.gameObject.AddComponent<VapeState>();
        state.Restore();
        try
        {
            Renderer? body = null;
            foreach (var mf in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                var r = mf.GetComponent<Renderer>();
                if (Data.hands.TryGetValue(mf.name, out var meshName))
                {
                    state.SetMesh(mf, Meshes[meshName]);
                    if (r != null) { state.SetMaterials(r, BodyMaterials(r, tpl)); body = r; }
                }
                else if (r != null && Data.hide.Contains(mf.name)) state.Hide(r);
            }
            if (body == null) throw new InvalidOperationException("no " + string.Join("/", Data.hands.Keys) + " renderer in the hands model");
            var item = Data.items[tpl];
            var screen = state.AddChild(body.transform, "vape_screen", Meshes[Data.screen.mesh], new Material(Materials[Data.screen.material]) { name = "vape_screen (instance)" }, body, true);
            var led = state.AddChild(body.transform, "vape_led", Meshes[Data.led.mesh], new Material(Materials[Data.led.material]) { name = "vape_led (instance)" }, body, true);
            led.enabled = false;
            // the box's own handling sounds (blister open, pill pop) make no sense on a vape; the driver plays its own
            var sounds = wp.GetComponent<BaseSoundPlayer>();
            if (sounds != null) foreach (var se in sounds.AdditionalSounds) if (se != null && Data.sounds.silence.Contains(se.EventName)) state.Silence(se);
            var animator = instance.GetComponent<Animator>() ?? throw new InvalidOperationException("Analgin's hands model has no animator");
            var original = animator.runtimeAnimatorController ?? throw new InvalidOperationException("Analgin's animator has no controller");
            if (!overrides.TryGetValue(original.GetInstanceID(), out var ov) || ov == null)
            {
                ov = new AnimatorOverrideController(original) { name = "vape_use override" };
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                ov.GetOverrides(pairs);
                int n = 0;
                for (int i = 0; i < pairs.Count; i++)
                    if (pairs[i].Key != null && pairs[i].Key.name == Data.replaces) { pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(pairs[i].Key, UseClip!); n++; }
                if (n == 0) throw new InvalidOperationException($"{Data.replaces} not found in Analgin's controller ({pairs.Count} clips)");
                ov.ApplyOverrides(pairs);
                overrides[original.GetInstanceID()] = ov;
                Note("override", $"use clip swapped ({n} of {pairs.Count} clips in {original.name})");
            }
            state.SetController(animator, original, ov);
            // the new controller brings new state behaviours: re-subscribe the game's animation-event emitter to them
            if (player != null) wp.RebindAnimator(player);
            else animator.Rebind();
            var camBone = FindChild(instance.transform, "Camera_animated");
            bool mine = (player as Player)?.IsYourPlayer ?? false;
            state.Drive(this, animator, screen, led, item.led, hits, sounds, camBone, mine);
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
            var state = __instance.GetComponent<VapeState>();
            if (state != null && state.Restore() && Instance != null) Instance.Restored++;
        }
        catch (Exception error) { Instance?.ReportOnce("return", error); }
    }
}

/// <summary>Everything changed on one pooled model, so it can be put back exactly.</summary>
public sealed class VapeState : MonoBehaviour
{
    readonly List<(MeshFilter mf, Mesh mesh)> meshes = new();
    readonly List<(Renderer r, Material[] mats)> materials = new();
    readonly List<(BoxCollider box, Vector3 center, Vector3 size)> boxes = new();
    readonly List<Renderer> hidden = new();
    readonly List<GameObject> children = new();
    readonly List<Material> instances = new();
    readonly List<(BaseSoundPlayer.SoundElement se, AudioClip[] clips)> silenced = new();
    VapeDriver? driver;
    public Animator? Animator; RuntimeAnimatorController? original; public AnimatorOverrideController? Override;
    public bool HandsApplied; public string Tpl = "";

    public void SetMesh(MeshFilter mf, Mesh mesh)
    {
        if (!meshes.Any(x => x.mf == mf)) meshes.Add((mf, mf.sharedMesh));
        mf.sharedMesh = mesh;
    }

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

    public void Hide(Renderer r)
    {
        if (!r.forceRenderingOff && !hidden.Contains(r)) { r.forceRenderingOff = true; hidden.Add(r); }
    }

    public void Silence(BaseSoundPlayer.SoundElement se)
    {
        if (silenced.Any(x => x.se == se)) return;
        silenced.Add((se, se.SoundClips));
        se.SoundClips = Array.Empty<AudioClip>();
    }

    public MeshRenderer AddChild(Transform parent, string name, Mesh mesh, Material mat, Renderer like, bool ownMaterial)
    {
        var go = new GameObject(name) { layer = like.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        children.Add(go);
        if (ownMaterial) instances.Add(mat);
        return mr;
    }

    public void SetController(Animator animator, RuntimeAnimatorController originalController, AnimatorOverrideController ov)
    {
        Animator = animator; original = originalController; Override = ov;
        animator.runtimeAnimatorController = ov;
    }

    public void Drive(Plugin p, Animator animator, MeshRenderer screen, MeshRenderer led, float[] ledColor, int hits, BaseSoundPlayer? sounds, Transform? camBone, bool mine)
    {
        driver = gameObject.AddComponent<VapeDriver>();
        driver.Init(p, animator, screen, led, ledColor, hits, sounds, camBone, mine);
    }

    /// <summary>Puts the model back the way the game made it. Returns true when something was changed.</summary>
    public bool Restore()
    {
        bool any = meshes.Count + materials.Count + boxes.Count + hidden.Count + children.Count + silenced.Count > 0 || Animator != null || driver != null;
        if (driver != null) { Destroy(driver); driver = null; }
        foreach (var (box, center, size) in boxes) if (box != null) { box.center = center; box.size = size; }
        boxes.Clear();
        foreach (var go in children) if (go != null) Destroy(go);
        children.Clear();
        foreach (var m in instances) if (m != null) Destroy(m);
        instances.Clear();
        foreach (var r in hidden) if (r != null) r.forceRenderingOff = false;
        hidden.Clear();
        foreach (var (se, clips) in silenced) if (se != null) se.SoundClips = clips;
        silenced.Clear();
        foreach (var (mf, mesh) in meshes) if (mf != null) mf.sharedMesh = mesh;
        foreach (var (r, mats) in materials) if (r != null) r.sharedMaterials = mats;
        meshes.Clear(); materials.Clear();
        if (Animator != null && original != null && Animator.runtimeAnimatorController == Override) Animator.runtimeAnimatorController = original;
        Animator = null; original = null; Override = null; HandsApplied = false; Tpl = "";
        return any;
    }
}

/// <summary>
/// Follows the Hands layer's USE state (normalized time x clip length): the LED glows while you draw, the inhale and exhale sounds play
/// through the game's own weapon-sound source, the vapour cloud leaves the mouth, and the screen drops one hit once the draw is done.
/// Cancelling (any other state) puts the LED out. Never throws into the update loop: on a problem it switches itself off.
/// </summary>
public sealed class VapeDriver : MonoBehaviour
{
    Plugin? p; Animator? animator; MeshRenderer? screen, led; Material? screenMat, ledMat; Color ledColor = Color.white;
    BaseSoundPlayer? sounds; Transform? camBone; bool mine;
    int stateHash, layer = -1, hits = -1; float last = -1f, ledLevel;
    bool inhaled, exhaled, stepped;
    static readonly int TintColor = Shader.PropertyToID("_TintColor");

    public void Init(Plugin plugin, Animator a, MeshRenderer scr, MeshRenderer l, float[] colour, int hitsLeft, BaseSoundPlayer? player, Transform? cam, bool yours)
    {
        p = plugin; animator = a; screen = scr; led = l; screenMat = scr.sharedMaterial; ledMat = l.sharedMaterial; sounds = player; camBone = cam; mine = yours;
        if (colour != null && colour.Length >= 3) ledColor = new Color(colour[0], colour[1], colour[2], 1f);
        stateHash = Animator.StringToHash(plugin.Data.state);
        hits = hitsLeft;
        ShowHits(hits);
    }

    void ShowHits(int n)
    {
        if (screenMat == null || p == null) return;
        var s = p.Data.screen;
        if (n < 0) { if (screen != null) screen.enabled = false; return; }
        n = Mathf.Clamp(n, 0, s.grid * s.grid - 1);
        float c = s.cell / (float)s.size;
        screenMat.mainTextureScale = new Vector2(c, c);
        screenMat.mainTextureOffset = new Vector2((n % s.grid) * c, 1f - (n / s.grid + 1) * c);
        if (screen != null) screen.enabled = true;
    }

    void LateUpdate()
    {
        try
        {
            if (p == null || animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) { Led(0f); return; }
            if (layer < 0 || layer >= animator.layerCount) { layer = animator.GetLayerIndex(p.Data.layer); if (layer < 0) { enabled = false; return; } }
            var st = animator.GetCurrentAnimatorStateInfo(layer);
            var tm = p.Timing;
            bool inUse = st.shortNameHash == stateHash;
            float time = inUse ? Mathf.Clamp01(st.normalizedTime) * tm.length : -1f;
            if (inUse && last >= 0f && time + 0.25f < last) { inhaled = exhaled = stepped = false; }   // the clip started over
            Led(inUse && time >= tm.ledOn && time <= tm.ledOff ? 1f : 0f);
            if (inUse)
            {
                if (!inhaled && time >= tm.inhale && time < tm.inhale + 1.5f) { inhaled = true; Play(p.Data.sounds.inhale); }
                if (!stepped && time >= tm.screenStep) { stepped = true; if (hits > 0) ShowHits(hits - 1); }
                if (!exhaled && time >= tm.exhale && time < tm.exhale + 1.5f) { exhaled = true; Play(p.Data.sounds.exhale); Cloud(); p.Puffs++; }
            }
            last = time;
        }
        catch (Exception e) { p?.ReportOnce("driver", e); Led(0f); enabled = false; }
    }

    void Led(float target)
    {
        if (led == null || ledMat == null) return;
        ledLevel = Mathf.MoveTowards(ledLevel, target, Time.deltaTime / 0.08f);
        bool on = ledLevel > 0.01f;
        if (led.enabled != on) led.enabled = on;
        if (!on) return;
        float flicker = 1f + 0.06f * Mathf.Sin(Time.time * 37f) * Mathf.Sin(Time.time * 11f);
        var c = ledColor * (p != null ? p.Data.led.brightness : 1f) * flicker;
        ledMat.SetColor(TintColor, new Color(c.r, c.g, c.b, 0.9f * ledLevel));
    }

    void Play(string clip)
    {
        if (sounds == null || p == null || !p.Clips.TryGetValue(clip, out var ac)) return;
        try { sounds.PlayClip(ac, p.Data.sounds.rolloff, p.Data.sounds.volume); }
        catch (Exception e) { p.ReportOnce("sound", e); }
    }

    /// <summary>The cloud follows the head while it streams out (particles stay in the world) and cleans itself up.</summary>
    void Cloud()
    {
        if (p?.CloudPrefab == null) return;
        Transform? anchor = null; Vector3 right, fwd, up, origin;
        var cam = Camera.main;
        // your own character: the camera you look through; anyone else: the head bone of their own hands model
        if (mine && cam != null && cam.isActiveAndEnabled && (camBone == null || (cam.transform.position - camBone.position).sqrMagnitude < 0.36f)) anchor = cam.transform;
        if (anchor != null) { origin = anchor.position; right = anchor.right; fwd = anchor.forward; up = anchor.up; }
        else if (camBone != null) { origin = camBone.position; right = camBone.TransformDirection(Vector3.right); fwd = camBone.TransformDirection(Vector3.down); up = camBone.TransformDirection(Vector3.forward); }
        else return;
        var m = p.Data.cloud.mouth;
        var pos = origin + right * m[0] + fwd * m[1] + up * m[2];
        var rot = Quaternion.AngleAxis(-p.Data.cloud.aim, right) * Quaternion.LookRotation(fwd, up);
        var go = Instantiate(p.CloudPrefab, pos, rot);
        go.name = "vape_cloud";
        if (anchor != null) go.transform.SetParent(anchor, true);
        var r = go.GetComponent<ParticleSystemRenderer>();
        var mat = p.CloudMaterial();
        if (r != null && mat != null) r.sharedMaterial = mat;
        var ps = go.GetComponent<ParticleSystem>();
        if (ps != null) ps.Play(true);
        Destroy(go, p.Data.cloud.life);
        p.Note("cloud", $"vapour at {(anchor != null ? "the camera" : "the hands' camera bone")}, layer {go.layer}");
    }

    void OnDestroy() { if (led != null) led.enabled = false; }
}

public sealed class VapeTiming
{
    public float length, ledOn, ledOff, inhale, exhale, screenStep;
    public float[] mouth = { 0f, 0.1f, -0.07f };
}

public sealed class ItemLook
{
    public string albedo = "";
    public float[] led = { 0.3f, 0.6f, 1f };
}

public sealed class VapeData
{
    /// <summary>template id -> colour map and LED colour (one per flavour)</summary>
    public Dictionary<string, ItemLook> items = new();
    public string albedoProperty = "_MainTex";
    public string bundle = "vape.bundle";
    public string clip = "vape_use";
    public string timing = "vape_timing";
    public string replaces = "item_analgin_use";
    public string state = "USE";
    public string layer = "Hands";
    public Dictionary<string, string> hands = new();
    public List<string> hide = new();
    public Dictionary<string, string> loot = new();
    public string lootLod0 = "item_analgin_LOD0";
    public string lootCollider = "item_analgin_COLLIDER";
    public Dictionary<string, string> textures = new();
    public float[] color = Array.Empty<float>();
    public Dictionary<string, float> floats = new();
    public ScreenData screen = new();
    public LedData led = new();
    public CloudData cloud = new();
    public SoundData sounds = new();
    public string artFolder = "art";
    /// <summary>Picture panels in the colour map (u0, v0, u1, v1; v up as Unity samples it).</summary>
    public float[] frontPanel = { 0f, 0f, 1f, 1f };
    public float[] backPanel = { 0f, 0f, 1f, 1f };
    public int pictureSpecular = 95;
}

public sealed class ScreenData { public string mesh = "vape_screen", material = "vape_screen"; public int cell = 96, grid = 10, size = 1024; }
public sealed class LedData { public string mesh = "vape_led", material = "vape_led"; public float brightness = 1f; }
public sealed class CloudData
{
    public string prefab = "vape_cloud", material = "vape_cloud_unlit";
    /// <summary>The game's own lit particle shaders, tried in order; the vapour then takes the scene's light like the game's dust and smoke.
    /// The bundle's material (with fallbackTint) is used only when none is loaded. Built-in lit particles blew out to a glowing white in the
    /// dark Factory rooms (fixture run 2026-11-17 06:44 UTC).</summary>
    public string[] shaders = { "Global Fog/Alpha Blended Lighted", "Global Fog/Particle Alpha Blended Lighted" };
    /// <summary>The lit shader's floor of light (_LocalMinimalAmbientLight), so the vapour still shows as a grey haze in a dark room
    /// (fixture run 2: with no floor it was invisible in the dark Factory washroom).</summary>
    public float[] minimalLight = { 0.24f, 0.24f, 0.25f, 1f };
    public float[] tint = { 0.45f, 0.46f, 0.48f, 0.45f };
    public float[] fallbackTint = { 0.2f, 0.21f, 0.22f, 0.3f };
    public float[] mouth = { 0f, 0.1f, -0.07f };
    public float aim = 10f, life = 7f;
}
public sealed class SoundData { public string inhale = "vape_inhale", exhale = "vape_exhale"; public float volume = 1f; public int rolloff = 30; public List<string> silence = new() { "SndOpen", "SndUse" }; }



