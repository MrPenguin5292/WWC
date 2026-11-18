using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Grunts.Client;

/// <summary>Sound hooks. Every patch checks Grunt.Is first and does nothing for anyone else; every body is wrapped so a fault never
/// reaches the game loop (the game's own sound then plays).</summary>
internal static class Patches
{
    // The infected brain's phrases (InfectedAttackLayer says 17 while chasing, InfectedPatrolLayer 28, BotMeleeWeaponData.TryHit 79 on
    // every swing) and the usual hurt/death/contact triggers, mapped onto the pack's voice categories (first found wins).
    private static readonly Dictionary<EPhraseTrigger, string[]> Lines = new()
    {
        [EPhraseTrigger.OnMutter] = new[] { "idle", "disturb_ono", "interrupt" },
        [EPhraseTrigger.OnBreath] = new[] { "idle", "disturb_ono" },
        [EPhraseTrigger.MumblePhrase] = new[] { "idle", "disturb_ono" },
        [EPhraseTrigger.Mooing] = new[] { "idle", "disturb_ono" },
        [EPhraseTrigger.OnEnemyConversation] = new[] { "chase", "chase_ono" },
        [EPhraseTrigger.OnFight] = new[] { "chase", "chase_ono" },
        [EPhraseTrigger.OnRepeatedContact] = new[] { "chase", "chase_ono" },
        [EPhraseTrigger.Provocation] = new[] { "chase", "chase_ono" },
        [EPhraseTrigger.OnFirstContact] = new[] { "alert", "found", "alert_ono" },
        [EPhraseTrigger.Warning] = new[] { "alert", "alert_ono" },
        [EPhraseTrigger.KnifeKill] = new[] { "attack", "attack_ono" },
        [EPhraseTrigger.OnSwitchToMeleeWeapon] = new[] { "chase", "chase_ono" },
        [EPhraseTrigger.OnBeingHurt] = new[] { "pain", "pain_ono" },
        [EPhraseTrigger.OnBeingHurtDissapoinment] = new[] { "pain", "pain_ono" },
        [EPhraseTrigger.HurtLight] = new[] { "pain", "pain_ono" },
        [EPhraseTrigger.HurtMedium] = new[] { "pain", "pain_ono" },
        [EPhraseTrigger.HurtHeavy] = new[] { "pain_ono", "pain" },
        [EPhraseTrigger.HurtNearDeath] = new[] { "pain_ono", "pain" },
        [EPhraseTrigger.Hit] = new[] { "pain", "pain_ono" },
        [EPhraseTrigger.Bleeding] = new[] { "pain_ono", "pain" },
        [EPhraseTrigger.HandBroken] = new[] { "pain_ono", "pain" },
        [EPhraseTrigger.LegBroken] = new[] { "pain_ono", "pain" },
        [EPhraseTrigger.OnAgony] = new[] { "stunned", "knocked", "pain_ono" },
        [EPhraseTrigger.OnDeath] = new[] { "death", "pain_ono" },
        [EPhraseTrigger.OnEnemyDown] = new[] { "kill", "chase_ono" },
        [EPhraseTrigger.EnemyDown] = new[] { "kill", "chase_ono" },
        [EPhraseTrigger.ScavDown] = new[] { "kill", "chase_ono" },
        [EPhraseTrigger.OnLostVisual] = new[] { "lost", "giveup", "interrupt" },
        [EPhraseTrigger.LostVisual] = new[] { "lost", "giveup", "interrupt" },
        [EPhraseTrigger.NoisePhrase] = new[] { "investigate", "disturb", "disturb_ono" },
        [EPhraseTrigger.CheckHim] = new[] { "investigate", "disturb", "disturb_ono" },
    };
    private static readonly string[] Fallback = { "chase", "chase_ono" };
    private static AccessTools.FieldRef<BotData, BotOwner>? owner;
    internal static int VoicesSwapped, Swings, Hits, Steps, Deaths;

    public static void Apply(Harmony h, ManualLogSource log)
    {
        owner = AccessTools.FieldRefAccess<BotData, BotOwner>("_owner");
        h.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnPhraseTold)), prefix: new HarmonyMethod(typeof(Patches), nameof(BeforePhrase)));
        h.Patch(AccessTools.Method(typeof(BotMeleeWeaponData), nameof(BotMeleeWeaponData.TryHit)), postfix: new HarmonyMethod(typeof(Patches), nameof(AfterTryHit)));
        h.Patch(AccessTools.Method(typeof(BotMeleeWeaponData), nameof(BotMeleeWeaponData.HitCurrentEnemy)), postfix: new HarmonyMethod(typeof(Patches), nameof(AfterHit)));
        h.Patch(AccessTools.Method(typeof(Player), nameof(Player.PlayStepSound)), prefix: new HarmonyMethod(typeof(Patches), nameof(BeforeStep)));
        h.Patch(AccessTools.Method(typeof(Player), nameof(Player.PlayDeathSound)), prefix: new HarmonyMethod(typeof(Patches), nameof(BeforeDeathSound)));
        if (GruntAnims.Ready)
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.BodyUpdate)), postfix: new HarmonyMethod(typeof(Patches), nameof(AfterBodyUpdate)));
        if (GruntModel.Ready || GruntAnims.Ready)
            h.Patch(AccessTools.Method(typeof(EFT.AssetsManager.AssetPoolObject), "ReturnToPool", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Patches), nameof(BeforeReturnToPool)));
        if (GruntModel.Ready)
        {
            h.Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.Update)), postfix: new HarmonyMethod(typeof(Patches), nameof(AfterWorldUpdate)));
        }
    }

    /// <summary>Right after the game posed a Grunt's body: the pack's animation on top.</summary>
    private static void AfterBodyUpdate(Player __instance, float deltaTime)
    {
        try
        {
            if (!Grunt.Is(__instance)) return;
            GruntMotion.For(__instance)?.Apply(deltaTime);
        }
        catch (Exception e) { Plugin.Instance?.Note("animation failed: " + e.Message); }
    }

    private static float nextDress;

    /// <summary>Every half second in a raid: Grunts that are not dressed yet get their model (the body is ready a few frames after spawn).</summary>
    private static void AfterWorldUpdate(GameWorld __instance)
    {
        if (Time.time < nextDress) return;
        nextDress = Time.time + 0.5f;
        try { DressGrunts(); } catch (Exception e) { Plugin.Instance?.Note("dressing pass failed: " + e.Message); }
    }

    private static void DressGrunts()
    {
        if (!Singleton<GameWorld>.Instantiated) return;
        var list = Singleton<GameWorld>.Instance.AllAlivePlayersList;
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p == null || !Grunt.Is(p)) continue;
            try { GruntModel.Dress(p); } catch (Exception e) { Plugin.Instance?.Note("dressing failed: " + e.Message); }
        }
    }

    private static void BeforeReturnToPool(EFT.AssetsManager.AssetPoolObject __instance)
    {
        try
        {
            if (__instance == null) return;
            GruntModel.Undress(__instance.gameObject);
            foreach (var m in __instance.GetComponentsInChildren<GruntMotion>(true)) UnityEngine.Object.Destroy(m);
        }
        catch (Exception e) { Plugin.Instance?.Note("undress failed: " + e.Message); }
    }

    /// <summary>The zombie line the speaker picked is swapped for a Grunt line of the same kind, before the player's speech source plays it.</summary>
    private static void BeforePhrase(Player __instance, EPhraseTrigger @event, ref TaggedClip clip)
    {
        try
        {
            if (!Grunt.Is(__instance)) return;
            try { GruntMotion.For(__instance)?.Say(@event); } catch { }
            if (!Plugin.Voices.Value || GruntAudio.Count == 0 || clip == null) return;
            var cats = Lines.TryGetValue(@event, out var c) ? c : Fallback;
            var a = GruntAudio.Pick(Grunt.Voice(__instance), cats);
            if (a == null) return;
            clip = new TaggedClip { Clip = a, Mask = clip.Mask, NetId = clip.NetId, Falloff = clip.Falloff > 0 ? clip.Falloff : 60, Volume = Plugin.VoiceVolume.Value, Length = a.length, Exclude = false };
            VoicesSwapped++;
        }
        catch (Exception e) { Plugin.Instance?.Note("voice swap failed: " + e.Message); }
    }

    private static Player? PlayerOf(BotData data)
    {
        try { return owner != null ? owner(data)?.GetPlayer : null; } catch { return null; }
    }

    /// <summary>A swing started: the blade whoosh just before it lands (the pack plays it 0.16 s before the hit; the zombie swing lands ~0.6 s in).</summary>
    private static void AfterTryHit(BotMeleeWeaponData __instance, bool __result)
    {
        try
        {
            if (!__result) return;
            var p = PlayerOf(__instance);
            if (!Grunt.Is(p)) return;
            Swings++;
            try { GruntMotion.For(p!)?.Attack(); } catch { }
            if (!Plugin.Sfx.Value || GruntAudio.Count == 0) return;
            Runner.After(0.45f, () =>
            {
                if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return;
                var pos = p.PlayerBones != null && p.PlayerBones.RightPalm != null ? p.PlayerBones.RightPalm.position : p.Position + Vector3.up * 1.4f;
                GruntAudio.PlayAt(GruntAudio.Sfx("whoosh_"), pos, 25f, Plugin.SfxVolume.Value);
            });
        }
        catch (Exception e) { Plugin.Instance?.Note("swing sound failed: " + e.Message); }
    }

    private static void AfterHit(BotMeleeWeaponData __instance, Player target)
    {
        try
        {
            if (!Plugin.Sfx.Value || GruntAudio.Count == 0) return;
            var p = PlayerOf(__instance);
            if (!Grunt.Is(p)) return;
            Hits++;
            var pos = target != null && target.PlayerBones != null && target.PlayerBones.Ribcage != null ? target.PlayerBones.Ribcage.Original.position : (p!.Position + p.LookDirection);
            GruntAudio.PlayAt(GruntAudio.Sfx("blade_hit_") ?? GruntAudio.Sfx("stab_"), pos, 30f, Plugin.SfxVolume.Value);
            GruntAudio.PlayAt(GruntAudio.Sfx("gore_") ?? GruntAudio.Sfx("body_hit_"), pos, 20f, Plugin.SfxVolume.Value * 0.8f);
        }
        catch (Exception e) { Plugin.Instance?.Note("hit sound failed: " + e.Message); }
    }

    private static bool BeforeStep(Player __instance)
    {
        try
        {
            if (!Plugin.Footsteps.Value || !GruntAudio.Has("footstep") || !Grunt.Is(__instance)) return true;
            GruntAudio.PlayAt(GruntAudio.Pick("", "footstep"), __instance.Position, 35f, Plugin.SfxVolume.Value);
            Steps++;
            return false;
        }
        catch (Exception e) { Plugin.Instance?.Note("step sound failed: " + e.Message); return true; }
    }

    private static bool BeforeDeathSound(Player __instance)
    {
        try
        {
            if (!Plugin.Voices.Value || GruntAudio.Count == 0 || !Grunt.Is(__instance)) return true;
            var a = GruntAudio.Pick(Grunt.Voice(__instance), "death", "pain_ono");
            if (a == null) return true;
            var pos = __instance.PlayerBones != null && __instance.PlayerBones.Head != null ? __instance.PlayerBones.Head.Original.position : __instance.Position + Vector3.up * 1.6f;
            GruntAudio.PlayAt(a, pos, 50f, Plugin.VoiceVolume.Value);
            Deaths++;
            return false;
        }
        catch (Exception e) { Plugin.Instance?.Note("death sound failed: " + e.Message); return true; }
    }
}

/// <summary>Delayed actions on a GameObject of our own, made when first needed (one made in plugin Awake may never update).</summary>
internal sealed class Runner : MonoBehaviour
{
    private static Runner? instance;
    private readonly List<(float At, Action Do)> queue = new();
    private readonly List<(float Period, Action Do)> repeat = new();
    private float nextRepeat;

    private static Runner Get()
    {
        if (instance == null)
        {
            var go = new GameObject("Grunts_Runner");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Runner>();
        }
        return instance;
    }

    public static void After(float seconds, Action action) => Get().queue.Add((Time.time + seconds, action));

    public static void Every(float seconds, Action action) => Get().repeat.Add((seconds, action));

    private void Update()
    {
        if (repeat.Count > 0 && Time.time >= nextRepeat)
        {
            nextRepeat = Time.time + repeat[0].Period;
            foreach (var (_, a) in repeat) { try { a(); } catch (Exception e) { Plugin.Instance?.Note("repeated action failed: " + e.Message); } }
        }
        if (queue.Count == 0) return;
        var now = Time.time;
        for (int i = queue.Count - 1; i >= 0; i--)
        {
            if (queue[i].At > now) continue;
            var a = queue[i].Do;
            queue.RemoveAt(i);
            try { a(); } catch (Exception e) { Plugin.Instance?.Note("delayed action failed: " + e.Message); }
        }
    }
}
