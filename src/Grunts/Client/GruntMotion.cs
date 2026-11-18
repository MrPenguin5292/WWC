using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace Grunts.Client;

/// <summary>
/// Marks the game's melee weapon in a Grunt's hand as hidden (its renderers forced off). The weapon object is pooled and reused, so the
/// renderers come back on as soon as it is switched off (back to the pool, or holstered); GruntMotion hides it again while in his hand.
/// </summary>
internal sealed class GruntHiddenWeapon : MonoBehaviour
{
    private readonly List<(Renderer R, bool Off)> hidden = new();

    public static void Hide(GameObject go)
    {
        if (go.GetComponent<GruntHiddenWeapon>() != null) return;
        var h = go.AddComponent<GruntHiddenWeapon>();
        foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { h.hidden.Add((r, r.forceRenderingOff)); r.forceRenderingOff = true; }
    }

    private void OnDisable() => Show();

    /// <summary>Renderers back as they were (also when he dies: the game drops that weapon as loot).</summary>
    public void Show()
    {
        foreach (var (r, off) in hidden) if (r != null) r.forceRenderingOff = off;
        hidden.Clear();
        Destroy(this);
    }
}

/// <summary>One of the pack's animations, retargeted onto Tarkov's body bones (tools/retarget.py, grunts_anims.bin).</summary>
internal sealed class GruntClip
{
    public string Name = "";
    public float Fps; public int Frames; public bool Loop; public float Speed; public float Hit;
    public Quaternion[] Rot = Array.Empty<Quaternion>();   // frames x bones, Root_Joint space
    public Vector3[] Pelvis = Array.Empty<Vector3>();      // frames, Root_Joint space
    public Quaternion[] MachRot = Array.Empty<Quaternion>(); // frames: the pack's weapon bone in the right palm's frame
    public Vector3[] MachPos = Array.Empty<Vector3>();
    public float Length => Frames > 1 ? (Frames - 1) / Fps : 0f;

    /// <summary>Pose at time t (seconds; looped or clamped) into rot/pelvis and the machete's grip.</summary>
    public void Sample(float t, Quaternion[] rot, out Vector3 pelvis, int bones, out Quaternion mrot, out Vector3 mpos)
    {
        float f = t * Fps;
        if (Loop && Frames > 1) { f %= Frames - 1; if (f < 0) f += Frames - 1; }
        else f = Mathf.Clamp(f, 0, Frames - 1);
        int a = Mathf.FloorToInt(f), b = Mathf.Min(a + 1, Frames - 1);
        float w = f - a;
        for (int i = 0; i < bones; i++) rot[i] = Quaternion.Slerp(Rot[a * bones + i], Rot[b * bones + i], w);
        pelvis = Vector3.Lerp(Pelvis[a], Pelvis[b], w);
        if (MachRot.Length == Frames) { mrot = Quaternion.Slerp(MachRot[a], MachRot[b], w); mpos = Vector3.Lerp(MachPos[a], MachPos[b], w); }
        else { mrot = Quaternion.identity; mpos = Vector3.zero; }
    }
}

internal static class GruntAnims
{
    public static string[] Bones = Array.Empty<string>();
    public static readonly Dictionary<string, GruntClip> Clips = new(StringComparer.OrdinalIgnoreCase);
    public static bool Ready => Clips.Count > 0;

    public static void Load(string path, ManualLogSource log)
    {
        if (!File.Exists(path)) { log.LogWarning("Grunts: grunts_anims.bin not found; Grunts move like zombies"); return; }
        using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "GRNA") { log.LogWarning("Grunts: grunts_anims.bin has an unknown format"); return; }
        var version = r.ReadUInt32();
        if (version < 1 || version > 2) { log.LogWarning("Grunts: grunts_anims.bin version " + version + " is not supported"); return; }
        string Str() => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
        var nb = (int)r.ReadUInt32();
        Bones = new string[nb];
        for (int i = 0; i < nb; i++) Bones[i] = Str();
        var nc = (int)r.ReadUInt32();
        for (int c = 0; c < nc; c++)
        {
            var clip = new GruntClip { Name = Str(), Fps = r.ReadSingle(), Frames = (int)r.ReadUInt32(), Loop = r.ReadByte() != 0, Speed = r.ReadSingle(), Hit = r.ReadSingle() };
            clip.Rot = new Quaternion[clip.Frames * nb];
            for (int i = 0; i < clip.Rot.Length; i++) clip.Rot[i] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            clip.Pelvis = new Vector3[clip.Frames];
            for (int i = 0; i < clip.Frames; i++) clip.Pelvis[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (version >= 2)
            {
                clip.MachRot = new Quaternion[clip.Frames]; clip.MachPos = new Vector3[clip.Frames];
                for (int i = 0; i < clip.Frames; i++) clip.MachRot[i] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                for (int i = 0; i < clip.Frames; i++) clip.MachPos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
            Clips[clip.Name] = clip;
        }
        log.LogInfo($"Grunts: {Clips.Count} Grunt animations on {nb} bones");
    }
}

/// <summary>
/// Plays the pack's own animations on one Grunt: after the game has posed the body (Player.BodyUpdate), every driven bone gets the
/// Grunt pose (world rotations from Root_Joint, pelvis position), so hit boxes, the weapon in his hand and the ragdoll follow it.
/// Locomotion picks idle / walk / stride / jog / run from his real ground speed and scales the playback so the feet keep up; one-shot
/// clips (machete swings, staggers, door kick, taunts, investigate, give-up, idle breaks) blend over it. Stops when he dies.
/// </summary>
internal sealed class GruntMotion : MonoBehaviour
{
    private Player? p;
    private Transform? root;
    private Transform?[] bones = Array.Empty<Transform?>();
    private Transform? palm;
    private int nb;
    private Quaternion[] loco = Array.Empty<Quaternion>(), prev = Array.Empty<Quaternion>(), act = Array.Empty<Quaternion>();
    private string locoName = "idle", prevName = "idle";
    private float locoT, prevT, locoFade = 1f;
    private GruntClip? action;
    private float actT, actFadeIn = 0.12f;
    private float speed;
    private Vector3 lastPos;
    private bool hasLast, dead, subscribed;
    private float calmSince, nextBreak, nextStagger, nextTaunt;
    private string lastState = "";
    internal static int Applied, Actions;
    internal static readonly HashSet<string> StatesSeen = new();
    private static readonly string[] Attacks = { "attack3", "attack4", "attack_low", "attack_low2", "attack5" };
    private static readonly System.Random rng = new(3838);

    public static GruntMotion? For(Player p)
    {
        if (!GruntAnims.Ready || p == null) return null;
        var m = p.GetComponent<GruntMotion>();
        if (m == null) { m = p.gameObject.AddComponent<GruntMotion>(); if (!m.Init(p)) { Destroy(m); return null; } }
        return m;
    }

    private bool Init(Player player)
    {
        p = player;
        root = p.PlayerBones?.RootJoint;
        if (root == null) return false;
        var byName = GruntModel.BodyBones(root);   // not the weapon's own hand bones (see BodyBones)
        nb = GruntAnims.Bones.Length;
        bones = new Transform?[nb];
        for (int i = 0; i < nb; i++) bones[i] = byName.TryGetValue(GruntAnims.Bones[i], out var t) ? t : null;   // the zombie skeleton lacks Spine1/Thigh2
        byName.TryGetValue("Base HumanRPalm", out palm);
        loco = new Quaternion[nb]; prev = new Quaternion[nb]; act = new Quaternion[nb];
        calmSince = Time.time; nextBreak = Time.time + 20f + (float)rng.NextDouble() * 20f;
        try { p.ActiveHealthController.ApplyDamageEvent += OnDamage; subscribed = true; } catch { }
        return true;
    }

    private void OnDestroy()
    {
        try { if (subscribed && p != null && p.ActiveHealthController != null) p.ActiveHealthController.ApplyDamageEvent -= OnDamage; } catch { }
    }

    // ---- triggers
    public void Attack() => Play(Attacks[rng.Next(Attacks.Length)], 0.1f, interrupt: false);

    public void Say(EPhraseTrigger t)
    {
        if (speed > 1.2f || action != null) return;
        if (t == EPhraseTrigger.OnFirstContact && Time.time > nextTaunt && rng.NextDouble() < 0.4) { nextTaunt = Time.time + 25f; Play(rng.Next(2) == 0 ? "taunt1" : "taunt2", 0.2f); }
        else if ((t == EPhraseTrigger.NoisePhrase || t == EPhraseTrigger.CheckHim) && rng.NextDouble() < 0.35) Play(rng.Next(2) == 0 ? "investigate" : "investigate2", 0.25f);
        else if ((t == EPhraseTrigger.OnLostVisual || t == EPhraseTrigger.LostVisual) && rng.NextDouble() < 0.5) Play("giveup", 0.25f);
    }

    private void OnDamage(EBodyPart part, float damage, DamageInfo info)
    {
        try
        {
            if (dead || p == null || damage < 25f || Time.time < nextStagger) return;
            nextStagger = Time.time + 3f;
            var src = -info.Direction; src.y = 0;
            if (src.sqrMagnitude < 1e-4f) return;
            src.Normalize();
            var f = p.Transform.forward; f.y = 0; f.Normalize();
            var r = Vector3.Cross(Vector3.up, f);
            float df = Vector3.Dot(src, f), dr = Vector3.Dot(src, r);
            var clip = Mathf.Abs(df) >= Mathf.Abs(dr) ? (df > 0 ? "stagger_b" : "stagger_f") : (dr > 0 ? "stagger_l" : "stagger_r");
            Play(clip, 0.08f, interrupt: true);
        }
        catch { }
    }

    private void Play(string name, float fadeIn, bool interrupt = true)
    {
        if (!GruntAnims.Clips.TryGetValue(name, out var c)) return;
        if (action != null && !interrupt) return;
        action = c; actT = 0; actFadeIn = fadeIn; Actions++;
    }

    // ---- per frame, right after the game posed the body
    public void Apply(float dt)
    {
        if (p == null || root == null || dead) return;
        if (p.HealthController == null || !p.HealthController.IsAlive)
        {
            dead = true;
            // the game drops the weapon in his hand as loot: show it again and put his own machete away so there are not two
            try
            {
                if (holder != null) holder.GetComponent<GruntHiddenWeapon>()?.Show();
                var own = palm != null ? palm.Find("GruntMachete") : null;
                if (own != null) own.gameObject.SetActive(false);
            }
            catch { }
            return;
        }
        if (dt <= 0f) dt = Time.deltaTime;
        var pos = p.Position;
        if (hasLast)
        {
            var d = pos - lastPos; d.y = 0;
            var v = d.magnitude / Mathf.Max(dt, 1e-4f);
            if (v < 15f) speed = Mathf.Lerp(speed, v, 1f - Mathf.Exp(-dt * 8f));
        }
        lastPos = pos; hasLast = true;
        WatchState();

        bool alert = false;
        try { alert = p.AIData?.BotOwner?.Memory?.HaveEnemy == true; } catch { }
        string want = speed < 0.35f ? (alert ? "idle_alert" : "idle") : speed < 1.9f ? "walk" : speed < 3.3f ? "stride" : speed < 4.8f ? "jog" : "run";
        if (want != locoName)
        {
            prevName = locoName; prevT = locoT; locoFade = 0f;
            var pc = GruntAnims.Clips[prevName]; var nc = GruntAnims.Clips[want];
            locoT = nc.Loop && pc.Loop && pc.Speed > 0 && nc.Speed > 0 && pc.Length > 0 ? (prevT % pc.Length) / pc.Length * nc.Length : 0f;   // keep the step phase
            locoName = want;
        }
        var lc = GruntAnims.Clips[locoName];
        float rate = lc.Speed > 0.1f ? Mathf.Clamp(speed / lc.Speed, 0.6f, 1.7f) : 1f;
        locoT += dt * rate;
        lc.Sample(locoT, loco, out var pel, nb, out var mrot, out var mpos);
        if (locoFade < 1f)
        {
            locoFade = Mathf.Min(1f, locoFade + dt / 0.25f);
            var pc = GruntAnims.Clips[prevName];
            prevT += dt * (pc.Speed > 0.1f ? Mathf.Clamp(speed / pc.Speed, 0.6f, 1.7f) : 1f);
            pc.Sample(prevT, prev, out var ppel, nb, out var pmrot, out var pmpos);
            for (int i = 0; i < nb; i++) loco[i] = Quaternion.Slerp(prev[i], loco[i], locoFade);
            pel = Vector3.Lerp(ppel, pel, locoFade);
            mrot = Quaternion.Slerp(pmrot, mrot, locoFade); mpos = Vector3.Lerp(pmpos, mpos, locoFade);
        }

        // idle breaks while calm and standing
        if (alert || speed > 0.35f) calmSince = Time.time;
        else if (action == null && Time.time > nextBreak && Time.time - calmSince > 15f)
        {
            nextBreak = Time.time + 25f + (float)rng.NextDouble() * 25f;
            if (rng.NextDouble() < 0.5) Play(rng.Next(2) == 0 ? "idlebreak1" : "idlebreak2", 0.3f);
        }

        if (action != null)
        {
            actT += dt;
            float w = Mathf.Clamp01(actT / Mathf.Max(actFadeIn, 0.01f)) * Mathf.Clamp01((action.Length - actT) / 0.18f);
            action.Sample(actT, act, out var apel, nb, out var amrot, out var ampos);
            for (int i = 0; i < nb; i++) loco[i] = Quaternion.Slerp(loco[i], act[i], w);
            pel = Vector3.Lerp(pel, apel, w);
            mrot = Quaternion.Slerp(mrot, amrot, w); mpos = Vector3.Lerp(mpos, ampos, w);
            if (actT >= action.Length) action = null;
        }

        var rr = root.rotation;
        for (int i = 0; i < nb; i++)
        {
            var b = bones[i];
            if (b == null) continue;
            b.rotation = rr * loco[i];
            if (i == 0) b.position = root.TransformPoint(pel);   // bone 0 is the pelvis
        }
        // his machete moves inside his hand the way the pack's weapon bone does (grip turned ~145 degrees from the reference pose)
        if (GruntModel.HasMachete && palm != null)
        {
            if (machete == null) machete = palm.Find("GruntMachete");
            if (machete != null) { machete.localRotation = mrot; machete.localPosition = mpos; }
            AlignWeapon();
        }
        Applied++;
    }

    private Transform? machete;

    // ---- the game's melee weapon: hidden, and lined up with the Grunt's own machete (it still does the hit checks)
    private Transform? holder;
    private Vector3 holderGrip, holderAxis;

    private void AlignWeapon()
    {
        try
        {
            if (palm == null) return;
            var wr = p!.HandsController?.WeaponRoot;
            if (wr == null || !wr.IsChildOf(palm)) return;
            if (holder == null || !wr.IsChildOf(holder))
            {
                // the weapon's top object under the palm's attachment points (weapon_Hand_root/weapon_point_melee/<container>)
                var t = wr;
                while (t.parent != null && t.parent != palm && !t.parent.name.StartsWith("weapon_point", StringComparison.Ordinal) && !t.parent.name.StartsWith("weapon_Hand_root", StringComparison.Ordinal)) t = t.parent;
                if (!Measure(t)) return;
                holder = t;
            }
            GruntHiddenWeapon.Hide(holder.gameObject);
            var frame = machete != null ? machete : palm;   // the machete's own grip frame when it is there
            var blade = frame.rotation * GruntModel.Blade;
            var rot = Quaternion.FromToRotation(holder.rotation * holderAxis, blade) * holder.rotation;
            holder.rotation = rot;
            holder.position = frame.TransformPoint(GruntModel.Grip) - rot * Vector3.Scale(holderGrip, holder.lossyScale);
        }
        catch { }
    }

    /// <summary>The weapon's long axis and grip end (the end nearer the palm) in the holder's own frame, from its meshes' bounds.</summary>
    private bool Measure(Transform t)
    {
        var filters = t.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0) return false;
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        foreach (var f in filters)
        {
            if (f.sharedMesh == null) continue;
            var b = f.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                var l = t.InverseTransformPoint(f.transform.TransformPoint(c));
                min = Vector3.Min(min, l); max = Vector3.Max(max, l);
            }
        }
        var size = max - min;
        int ax = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        var centre = (min + max) * 0.5f;
        Vector3 endA = centre, endB = centre;
        endA[ax] = min[ax]; endB[ax] = max[ax];
        var palmLocal = t.InverseTransformPoint(palm!.position);
        bool aIsGrip = (endA - palmLocal).sqrMagnitude <= (endB - palmLocal).sqrMagnitude;
        holderGrip = aIsGrip ? endA : endB;
        holderAxis = ((aIsGrip ? endB : endA) - holderGrip).normalized;
        // grip a hand's width in from the pommel, like the Grunt's own
        holderGrip += holderAxis * Mathf.Min(0.06f, size[ax] * 0.15f);
        return true;
    }

    /// <summary>The game's door kick shows as a movement state; play the pack's door kick when he enters one.</summary>
    private void WatchState()
    {
        try
        {
            var s = p!.MovementContext?.CurrentState;
            var n = s == null ? "" : s.GetType().Name;
            if (n == lastState) return;
            lastState = n;
            if (StatesSeen.Count < 64 && StatesSeen.Add(n)) Plugin.Instance?.Say("Grunt movement state seen: " + n);
            if (n.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Breach", StringComparison.OrdinalIgnoreCase) >= 0) Play("doorkick", 0.1f);
        }
        catch { }
    }
}
