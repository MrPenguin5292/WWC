using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Grunts.Client;

/// <summary>
/// Grunts client. The Grunt is a server-side bot type (one of the game's infected roles, named "Grunt"); this plugin gives it the
/// pack's voice and sounds: the zombie's phrase clips are swapped for the Grunt's matching lines at the player's own speech source
/// (Player.OnPhraseTold), machete swings and hits get the pack's whoosh/blade sounds, and steps the pack's footsteps.
/// Fail soft: without the sound bundle, or on any error, the Grunt keeps the game's zombie sounds. Never throws into the game loop.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "Grunts", "0.1.2")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC Grunts");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("Grunts");
    private BepInEx.Configuration.ConfigFile Config => WWC.Client.ForgePlugin.Cfg;
#endif
#if WWC_FORGE
    private const string Sec = "Grunts - ";   // WWC's F12 entry groups each part's settings under its name
#else
    private const string Sec = "";
#endif
    public const string Id = "local.grunts.client";
    internal static Plugin? Instance;
    internal static ConfigEntry<bool> Voices = null!, Sfx = null!, Footsteps = null!, Models = null!, Animations = null!;
    internal static ConfigEntry<float> VoiceVolume = null!, SfxVolume = null!, Shine = null!;
    internal static ConfigEntry<string> RoleName = null!, Nickname = null!;
    private Harmony? harmony;
    private readonly HashSet<string> notes = new();

    private void Awake()
    {
        Instance = this;
        Voices = Config.Bind(Sec + "Sound", "Grunt voices", true, "Grunts speak with the pack's voice lines instead of the zombie's.");
        Sfx = Config.Bind(Sec + "Sound", "Machete sounds", true, "Swing whoosh and blade hits from the pack.");
        Footsteps = Config.Bind(Sec + "Sound", "Grunt footsteps", true, "The pack's footsteps instead of the game's.");
        Models = Config.Bind(Sec + "Look", "Grunt models", true, "Grunts wear the pack's models (four looks) instead of the zombie bodies. Needs a restart.");
        Shine = Config.Bind(Sec + "Look", "Shine", 1f, new ConfigDescription("How much the Grunts' skin and clothes shine, as a share of the stock zombie body's. Needs a restart.", new AcceptableValueRange<float>(0f, 2f)));
        Animations = Config.Bind(Sec + "Look", "Grunt animations", true, "Grunts move with the pack's own animations instead of the zombie ones. Needs a restart.");
        VoiceVolume = Config.Bind(Sec + "Sound", "Voice volume", 1f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
        SfxVolume = Config.Bind(Sec + "Sound", "Machete and step volume", 0.9f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
        RoleName = Config.Bind(Sec + "Identity", "Grunt role", "infectedLaborant", "Must match the server's config gruntRole.");
        Nickname = Config.Bind(Sec + "Identity", "Grunt name", "Grunt", "Must match the server's config nickname.");
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build needs EFT 40743 / Unity 2022.3.43f1.");
            var dir = Path.GetDirectoryName(Info.Location)!;
            GruntAudio.Load(Path.Combine(dir, "grunts_audio.bundle"), Logger);
            if (Models.Value)
                try { GruntModel.Load(Path.Combine(dir, "grunts_model.bundle"), Logger); }
                catch (Exception e) { Logger.LogWarning("Grunts: models not loaded, Grunts keep the zombie look: " + e.Message); }
            if (Animations.Value)
                try { GruntAnims.Load(Path.Combine(dir, "grunts_anims.bin"), Logger); }
                catch (Exception e) { Logger.LogWarning("Grunts: animations not loaded, Grunts move like zombies: " + e.Message); }
            harmony = new Harmony(Id);
            Patches.Apply(harmony, Logger);
            Logger.LogInfo($"Grunts: ready ({GruntAudio.Count} sounds, {(GruntModel.Ready ? "Grunt models" : "zombie look")})");
        }
        catch (Exception e) { Logger.LogError("Grunts: not active, the Grunts keep the zombie look and sound: " + e); }
    }

    /// <summary>Log a problem once (per text) instead of every frame.</summary>
    internal void Note(string text)
    {
        if (notes.Count < 200 && notes.Add(text)) Logger.LogWarning("Grunts: " + text);
    }

    internal void Say(string text) => Logger.LogInfo("Grunts: " + text);
}

/// <summary>Which players are Grunts, and which of the pack's four looks/voices each one has (stable per bot).</summary>
internal static class Grunt
{
    public static readonly string[] Voices = { "v04", "v05", "ono01", "ono02" };
    private static readonly Dictionary<int, int> lookByPlayer = new();
    private static readonly Dictionary<int, bool> isGrunt = new();
    private static WildSpawnType? role;

    public static bool Is(Player? p)
    {
        if (p == null) return false;
        var id = p.GetInstanceID();
        if (isGrunt.TryGetValue(id, out var known)) return known;
        bool yes = false;
        try
        {
            var info = p.Profile?.Info;
            if (p.IsAI && info?.Settings != null)
            {
                role ??= Enum.TryParse<WildSpawnType>(Plugin.RoleName.Value, true, out var r) ? r : WildSpawnType.infectedLaborant;
                yes = info.Settings.Role == role.Value && (info.Nickname ?? "").StartsWith(Plugin.Nickname.Value, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { yes = false; }
        if (isGrunt.Count > 4000) isGrunt.Clear();
        isGrunt[id] = yes;
        return yes;
    }

    /// <summary>0..3: the pack's look a/b/c/d with its own voice (v04, v05, ono01, ono02), chosen from the profile id.</summary>
    public static int Look(Player p)
    {
        var id = p.GetInstanceID();
        if (lookByPlayer.TryGetValue(id, out var l)) return l;
        uint h = 2166136261;
        foreach (var c in p.ProfileId ?? "") h = (h ^ c) * 16777619;
        l = (int)(h % 4);
        if (lookByPlayer.Count > 4000) lookByPlayer.Clear();
        lookByPlayer[id] = l;
        return l;
    }

    public static string Voice(Player p) => Voices[Look(p)];

    /// <summary>A pooled player object is reused for the next bot: forget it.</summary>
    public static void Forget(Player p)
    {
        var id = p.GetInstanceID();
        isGrunt.Remove(id);
        lookByPlayer.Remove(id);
    }
}
