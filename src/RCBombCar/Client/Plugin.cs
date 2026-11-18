using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using EFT;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>
/// RC bomb car. The item is a clone of the Vortex Ranger 1500, so it sits in a pocket special slot and on a quick slot like the signal flares;
/// taken out, the player holds it like the rangefinder (the remote). Left click sets the car down and switches the view to it, WASD drives,
/// right click flips between the car and your own eyes, left click again sets it off with the game's own grenade explosion code.
/// Bots hear the motor (the game's sound event) and, with BigBrain present, a behaviour layer makes the ones that see it stop and shoot it.
/// Everything is local to raid objects made by this plugin; any failure in Awake unpatches everything and the item stays a plain rangefinder.
/// </summary>
#if WWC_FORGE
// Forge build: one BepInPlugin for all of WWC (WWC.Client.ForgePlugin) adds this part as a component.
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin(Id, "RC bomb car", "0.3.0")]
[BepInDependency("xyz.drakia.bigbrain", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
#endif
{
#if WWC_FORGE
    private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("WWC RC Bomb Car");
    private BepInEx.Logging.ManualLogSource Logger => log;
    private WWC.Client.PartInfo Info => WWC.Client.ForgePlugin.PartAt("RCBombCar");
#endif
    public const string Id = "local.rcbombcar.client";
    public static readonly string CarTpl = IdOf("item|car");
    internal static Plugin? Instance;
    internal static CarSettings Settings = new();
    internal static bool BotLayerRegistered;
    internal static bool Verbose => Settings.verboseLog;
    Harmony? harmony;

    static string IdOf(string key)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("rcbombcar|" + key)).Select(b => b.ToString("x2"))).Substring(0, 24);
    }

    void Awake()
    {
        try
        {
            if (Application.unityVersion != "2022.3.43f1") throw new InvalidOperationException("This build needs EFT 40743 / Unity 2022.3.43f1.");
            Settings = CarSettings.Load(BepInEx.Paths.GameRootPath, Logger);
            Instance = this;
            harmony = new Harmony(Id);
            harmony.Patch(AccessTools.DeclaredMethod(typeof(UsableItemInputTranslator), nameof(UsableItemInputTranslator.TranslateCommand)) ?? throw new MissingMethodException("UsableItemInputTranslator.TranslateCommand"),
                prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.HandsCommand)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(MoveInputTranslator), nameof(MoveInputTranslator.TranslateAxes)) ?? throw new MissingMethodException("MoveInputTranslator.TranslateAxes"),
                prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.MoveAxes)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(MoveInputTranslator), nameof(MoveInputTranslator.TranslateCommand)) ?? throw new MissingMethodException("MoveInputTranslator.TranslateCommand"),
                prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.MoveCommand)));
            GameWorld.OnDispose += Remote.Cleanup;
            CarCamera.Hook();
            Logger.LogInfo($"RC bomb car ready (item {CarTpl}; {Settings})");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Instance = null;
            Logger.LogError("RC bomb car unavailable; the item works as a plain rangefinder. " + error);
            return;
        }
        if (Settings.botsReact) TryRegisterBotLayer();
    }

    void TryRegisterBotLayer()
    {
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "DrakiaXYZ-BigBrain"))
        {
            Logger.LogWarning("RC car: BigBrain is not installed, so bots only hear the car; they won't stop to shoot it");
            return;
        }
#if WWC_FORGE
        // WWC's Forge build: the bot layer is in WWC.BigBrain.dll next to WWC.Client.dll (a type deriving from BigBrain's would break type
        // scans of WWC.Client for players without BigBrain), loaded only here, with BigBrain present.
        try
        {
            var dll = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "WWC.BigBrain.dll");
            var setup = System.Reflection.Assembly.LoadFrom(dll).GetType("RCBombCar.Client.BotLayerSetup", true)!;
            setup.GetMethod("Register")!.Invoke(null, new object[] { Settings.botLayerPriority });
            BotLayerRegistered = true; Logger.LogInfo($"RC car: bot layer added at priority {Settings.botLayerPriority}");
        }
#else
        try { BotLayerSetup.Register(Settings.botLayerPriority); BotLayerRegistered = true; Logger.LogInfo($"RC car: bot layer added at priority {Settings.botLayerPriority}"); }
#endif
        catch (Exception e) { Logger.LogError("RC car: the bot layer could not be added; bots only hear the car. " + e); }
    }

    internal static void Note(string text) => Instance?.Logger.LogInfo("RC car: " + text);
    internal static void Warn(string text) => Instance?.Logger.LogWarning("RC car: " + text);
    static readonly System.Collections.Generic.HashSet<string> reported = new();
    internal static void ErrorOnce(string key, Exception e) { if (reported.Add(key)) Instance?.Logger.LogError($"RC car: {key}: {e}"); }
}

internal static class Patches
{
    public static bool HandsCommand(UsableItemInputTranslator __instance, ECommand command, ref InputNode.ETranslateResult __result)
    {
        try { return Remote.OnHandsCommand(__instance._controller, command, ref __result); }
        catch (Exception e) { Plugin.ErrorOnce("hands command", e); return true; }
    }

    public static void MoveAxes(ref float[] axes)
    {
        try { Remote.OnAxes(axes); }
        catch (Exception e) { Plugin.ErrorOnce("axes", e); }
    }

    public static bool MoveCommand(ECommand command, ref InputNode.ETranslateResult __result)
    {
        try { return Remote.OnMoveCommand(command, ref __result); }
        catch (Exception e) { Plugin.ErrorOnce("move command", e); return true; }
    }


}
