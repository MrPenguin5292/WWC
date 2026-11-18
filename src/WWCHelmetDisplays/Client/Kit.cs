using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.Resources;
using EFT;
using EFT.InventoryLogic;
using EFT.Visual;
using UnityEngine;
using WWCHelmetDisplays.Client.Geometry;

namespace WWCHelmetDisplays.Client;

/// <summary>
/// The game's own hideout mannequin, used two ways. Once per session it is built off screen, posed, and its head and body are cut into the bust,
/// the trophy bust and the helmet crown (plain meshes, nothing shipped). Then every bust that is hung gets its own live copy (a <see cref="Rig"/>)
/// that the game dresses with real gear exactly as it dresses the mannequins in the Gear Rack.
/// </summary>
internal static class Kit
{
    public const string HeadSuit = "6644d2da35d958070c02642c", BodySuit = "6644d2ffd85107e63500a61c", FeetSuit = "6644d32235d958070c02642e";
    public const string MenuPlayer = "assets/content/commonprefabs/menuplayer.bundle";
    public const string AnimatorPath = "Hideout/EquipmentPresetsStand/Mannequin_animator_controller";
    public const string Pose = "standing";
    // The game hangs head gear on the head bone turned by this, the same in every slot view (PlayerBody.SlotView.CreateAndParent).
    public static readonly Quaternion GearTurn = new(0.5f, -0.5f, -0.5f, -0.5f);

    public static Mesh? Bust, Trophy, Crown;
    public static Material? MannequinMaterial;
    /// <summary>The head has its own texture; drawing it with the body's puts the body's shoulder socket on the face.</summary>
    public static Material? HeadMaterial;
    /// <summary>Where the gear frame sits in a posed rig's root: rig root = gear frame * inverse of this.</summary>
    public static Vector3 GearPos;
    public static Quaternion GearRot = Quaternion.identity;
    public static bool Ready;

    private static Task? building;
    private static object? bundles;   // the retained bundle token, kept for the session so the mannequin's textures stay loaded

    public static Task Build() => building ??= BuildAsync();

    private static async Task BuildAsync()
    {
        try
        {
            await Retain();
            var rig = await Rig.Create(null, "kit");
            if (rig == null) throw new InvalidOperationException("the mannequin could not be made");
            try
            {
                var root = rig.Root;
                var gearWorldRot = rig.Head.rotation * GearTurn;
                GearRot = Quaternion.Inverse(root.rotation) * gearWorldRot;
                GearPos = root.InverseTransformPoint(rig.Head.position);
                var frame = Matrix4x4.TRS(rig.Head.position, gearWorldRot, Vector3.one).inverse;
                Shape? head = null, body = null;
                foreach (var kv in rig.Body.BodySkins)
                {
                    var smr = kv.Value.GetComponentsInChildren<Diz.Skinning.AbstractSkin>(true).FirstOrDefault()?.SkinnedMeshRenderer;
                    if (smr == null) continue;
                    if (kv.Key == EBodyModelPart.Head) { head = Baked(smr, frame); HeadMaterial = new Material(smr.sharedMaterial) { name = "WWCHelmet_mannequin_head", hideFlags = HideFlags.DontUnloadUnusedAsset }; }
                    else if (kv.Key == EBodyModelPart.Body) { body = Baked(smr, frame); MannequinMaterial = new Material(smr.sharedMaterial) { name = "WWCHelmet_mannequin", hideFlags = HideFlags.DontUnloadUnusedAsset }; }
                }
                if (head == null || body == null || MannequinMaterial == null || HeadMaterial == null) throw new InvalidOperationException("the mannequin has no head or body mesh");
                // Parts of the bust: 0 the body's surface, 1 the flat cut faces, 2 the head's surface (its own material).
                var headPart = new Shape();
                for (int i = 0; i < head.P.Count; i++) headPart.Add(head.P[i], head.N[i], head.T[i]);
                headPart.Sub.Add(new List<int>()); headPart.Sub.Add(new List<int>());
                headPart.Sub.Add(new List<int>(head.Sub.SelectMany(s => s)));
                var both = MeshClip.Merge(body, headPart);
                Bust = ToMesh(MeshClip.Clip(both, Shapes.Bust(), capSub: 1), "WWCHelmet_bust");
                Trophy = ToMesh(MeshClip.Clip(both, Shapes.TrophyBust(), capSub: 1), "WWCHelmet_trophy_bust");
                Crown = ToMesh(MeshClip.Clip(head, Shapes.Crown(), capSub: 1), "WWCHelmet_crown");
                Ready = true;
                Plugin.Instance?.Log($"mannequin kit ready: bust {Bust.vertexCount} verts, crown {Crown.vertexCount} verts, gear frame at {GearPos} {GearRot.eulerAngles}");
            }
            finally { rig.Dispose(); }
        }
        catch (Exception error) { Plugin.Instance?.ReportOnce("mannequin kit", error); }
    }

    public static async Task Retain()
    {
        if (bundles != null) return;
        var solver = Singleton<CustomizationSolver>.Instance;
        var keys = new List<string> { MenuPlayer };
        foreach (var id in new[] { HeadSuit, BodySuit, FeetSuit })
        {
            var k = solver.GetBundle(new MongoID(id));
            if (k == null || string.IsNullOrEmpty(k.path)) throw new InvalidOperationException("no bundle for mannequin part " + id);
            keys.Add(k.path);
        }
        var token = Singleton<IEasyAssets>.Instance.Retain(keys);
        await EasyAssetsExtensions.LoadBundles(token);
        bundles = token;
    }

    /// <summary>A skinned mesh as it stands now, moved into the given frame.</summary>
    public static Shape Baked(SkinnedMeshRenderer smr, Matrix4x4 toFrame)
    {
        var baked = new Mesh();
        try
        {
            smr.BakeMesh(baked, true);
            var m = toFrame * Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            return FromMesh(baked, m);
        }
        finally { UnityEngine.Object.Destroy(baked); }
    }

    public static Shape FromMesh(Mesh mesh, Matrix4x4 m)
    {
        var s = new Shape();
        var v = mesh.vertices; var n = mesh.normals; var uv = mesh.uv;
        bool hasN = n != null && n.Length == v.Length, hasUv = uv != null && uv.Length == v.Length;
        for (int i = 0; i < v.Length; i++)
        {
            var p = m.MultiplyPoint3x4(v[i]);
            var q = hasN ? m.MultiplyVector(n[i]).normalized : Vector3.up;
            var t = hasUv ? uv[i] : Vector2.zero;
            s.Add(new V3(p.x, p.y, p.z), new V3(q.x, q.y, q.z), new V2(t.x, t.y));
        }
        for (int k = 0; k < mesh.subMeshCount; k++) s.Sub.Add(new List<int>(mesh.GetTriangles(k)));
        return s;
    }

    public static Mesh ToMesh(Shape s, string name)
    {
        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (s.P.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = s.P.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray();
        mesh.normals = s.N.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray();
        mesh.uv = s.T.Select(p => new Vector2(p.X, p.Y)).ToArray();
        mesh.subMeshCount = s.Sub.Count;
        for (int k = 0; k < s.Sub.Count; k++) mesh.SetTriangles(s.Sub[k], k, false);
        mesh.RecalculateBounds();
        try { mesh.RecalculateTangents(); } catch { }
        mesh.UploadMeshData(false);
        return mesh;
    }
}

/// <summary>
/// One live mannequin, posed and frozen, dressed through a client-only equipment item that holds copies of the display's gear.
/// The game's PlayerBody does the dressing; nothing here is ever sent to the server.
/// </summary>
internal sealed class Rig
{
    public MenuPlayerPoser Poser = null!;
    public PlayerBody Body = null!;
    public InventoryEquipment Equipment = null!;
    public Transform Root => Poser.transform;
    public Transform Head => Body.PlayerBones.Head.Original;
    private bool disposed;

    public static readonly EquipmentSlot[] Slots = { EquipmentSlot.Headwear, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear, EquipmentSlot.Earpiece, EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest };

    /// <summary>Makes a posed mannequin. Under <paramref name="parent"/> when given, otherwise far below the world.</summary>
    public static async Task<Rig?> Create(Transform? parent, string why)
    {
        await Kit.Retain();
        var poser = Singleton<IEasyAssets>.Instance.InstantiateAsset<MenuPlayerPoser>(Kit.MenuPlayer);
        if (poser == null) return null;
        var rig = new Rig { Poser = poser };
        try
        {
            poser.gameObject.name = "WWCHelmet_rig_" + why;
            if (parent != null) poser.transform.SetParent(parent, false);
            else poser.transform.position = new Vector3(0f, -1000f, 0f);
            foreach (var c in poser.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            rig.Body = poser.GetComponent<PlayerBody>();
            rig.Equipment = (InventoryEquipment)Singleton<ItemFactory>.Instance.CreateItem(new MongoID(true).ToString(), "55d7217a4bdc2d86028b456d", null);
            var custom = new BodyCustomization { { EBodyModelPart.Body, new MongoID(Kit.BodySuit) }, { EBodyModelPart.Feet, new MongoID(Kit.FeetSuit) }, { EBodyModelPart.Head, new MongoID(Kit.HeadSuit) } };
            await rig.Body.Init(custom, rig.Equipment, null, 0, EPlayerSide.Bear);
            var animator = poser.GetComponent<Animator>() ?? poser.GetComponentInChildren<Animator>();
            var controller = Resources.Load<RuntimeAnimatorController>(Kit.AnimatorPath);
            if (animator != null && controller != null)
            {
                animator.runtimeAnimatorController = controller;
                animator.enabled = true;
                animator.Play(Kit.Pose);
                animator.Update(0f);
                animator.Update(0.5f);
                animator.enabled = false;
            }
            poser.enabled = false;
            var squared = rig.SquareUp();
            if (why == "kit") Plugin.Instance?.Log("mannequin pose: " + squared);
            return rig;
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The game's standing pose turns the mannequin's chest about 26 degrees from where its head looks: one shoulder forward, one back (its
    /// skeleton, Helmet_discover_20261110_005321). Busts are cut square to the head, so the arm hanging from the forward shoulder ran down in
    /// front of the chest, and its cut stood beside the vest as a big flat face (WWC trailer v3.2, 2026-11-18: "Improve the boss busts so
    /// nothing is floating"). The head is turned on the neck to look the way the chest faces, and both upper arms are swung out to the sides at
    /// <see cref="Spread"/> degrees, square to the chest, so the bust's side cuts take the arms off cleanly. Done on every mannequin (the one the
    /// bust is cut from and each display's own) before anything is measured, so they all match. Returns what was done, for the log.
    /// </summary>
    public string SquareUp()
    {
        var all = Root.GetComponentsInChildren<Transform>(true);
        Transform? Bone(string name) => all.FirstOrDefault(t => t.name == name);
        var neck = Bone("Base HumanNeck");
        var leftUpper = Bone("Base HumanLUpperarm"); var leftFore = Bone("Base HumanLForearm1");
        var rightUpper = Bone("Base HumanRUpperarm"); var rightFore = Bone("Base HumanRForearm1");
        if (neck == null || leftUpper == null || leftFore == null || rightUpper == null || rightFore == null) return "left as posed (bones not found)";
        var up = Root.up;
        var across = Vector3.ProjectOnPlane(rightUpper.position - leftUpper.position, up).normalized;
        var headForward = Vector3.ProjectOnPlane(Head.rotation * Kit.GearTurn * Vector3.forward, up).normalized;
        var chestForward = Vector3.Cross(across, up).normalized;
        if (Vector3.Dot(chestForward, headForward) < 0f) chestForward = -chestForward;
        float turn = Vector3.SignedAngle(headForward, chestForward, up);
        neck.rotation = Quaternion.AngleAxis(turn, up) * neck.rotation;
        var notes = new List<string> { $"head turned {turn:0} degrees to face the way the chest does" };
        foreach (var (name, upper, fore) in new[] { ("left", leftUpper, leftFore), ("right", rightUpper, rightFore) })
        {
            var outward = Vector3.Project(upper.position - neck.position, across).normalized;
            var want = (-up * Mathf.Cos(Spread * Mathf.Deg2Rad) + outward * Mathf.Sin(Spread * Mathf.Deg2Rad)).normalized;
            var had = (fore.position - upper.position).normalized;
            notes.Add($"{name} arm moved {Vector3.Angle(had, want):0} degrees");
            upper.rotation = Quaternion.FromToRotation(had, want) * upper.rotation;
        }
        return string.Join(", ", notes);
    }

    /// <summary>How far the upper arms hang out from the body, square to the chest (degrees from straight down). The pose had about 19.</summary>
    public const float Spread = 32f;

    /// <summary>Hides the mannequin's own skin: busts draw their cut mesh instead, and mounts never show it.</summary>
    public void HideSkin()
    {
        foreach (var kv in Body.BodySkins) kv.Value.EnableRenderers(false);
    }

    /// <summary>Puts a copy of <paramref name="item"/> (or nothing) in an equipment slot. The game then loads or removes the model by itself.</summary>
    public void Wear(EquipmentSlot slot, Item? item)
    {
        var s = Equipment.GetSlot(slot);
        if (s.ContainedItem != null) s.RemoveItemWithoutRestrictions();
        if (item != null)
        {
            var copy = CopyForBust(item);
            var result = s.AddWithoutRestrictions(copy);
            if (result.Failed) Plugin.Instance?.Log($"could not put {item.TemplateId} on the bust ({slot}): {result.Error}");
        }
        // AddWithoutRestrictions does not tell the slot's watchers; this does, and the PlayerBody starts loading.
        s.ApplyContainedItem();
    }

    /// <summary>
    /// A copy of the gear for the bust, with every part that hangs in a slot. The game's CloneVisibleItem copies nothing at all from inside an item
    /// whose template hides its contents (ItemExtensions.CloneItemInternal), and rigs and plate carriers do, for their pockets. So a modular carrier
    /// came onto the bust bare: the AirLite showed only its shoulder straps, without its front, pouches or collar (WWC trailer, 2026-11-18). Parts in
    /// slots are copied back here; what sits in the pockets stays out, as before.
    /// </summary>
    internal static Item CopyForBust(Item item)
    {
        var copy = item.CloneVisibleItem();
        if (!item.HideEntrails || item is not CompoundItem from || copy is not CompoundItem to) return copy;
        foreach (var slot in from.Slots)
        {
            var part = slot.ContainedItem;
            if (part == null) continue;
            var into = to.Slots.FirstOrDefault(s => s.ID == slot.ID);
            if (into == null || into.ContainedItem != null) continue;
            var added = into.AddWithoutRestrictions(CopyForBust(part));
            if (added.Failed) Plugin.Instance?.Log($"could not copy {part.TemplateId} into {item.TemplateId}'s {slot.ID} for the bust: {added.Error}");
        }
        return copy;
    }

    public bool Loading => SlotViews().Any(v => v.LoadingJob != null && !v.LoadingJob.IsCompleted);

    public IEnumerable<PlayerBody.SlotView> SlotViews()
    {
        var list = new List<PlayerBody.SlotView>();
        if (Body == null) return list;
        foreach (var v in Body.SlotViews.GetValuesEnumerator()) list.Add(v);
        return list;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (Body != null) Body.Dispose(); } catch (Exception e) { Plugin.Instance?.ReportOnce("rig dispose", e); }
        if (Poser != null) UnityEngine.Object.Destroy(Poser.gameObject);
    }
}
