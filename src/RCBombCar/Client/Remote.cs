using System;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InputSystem;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>Who holds the remote, the one car that is out, and the input that goes to it instead of the player's legs.</summary>
internal static class Remote
{
    internal static RCCar? Car;
    internal static bool Viewing;
    internal static Vector2 Drive;          // x = steering, y = throttle
    internal static Vector2 LookDelta;      // mouse since the last camera update
    internal static bool Boost;
    internal static Vector2? DriveOverride; // QA probe only
    internal static bool? BoostOverride;    // QA probe only
    static Runner? runner;

    // counters the QA probe reads
    public static int Deploys, Detonations, Hits, NoiseEvents, BotLayerStarts, BotShots, BotRuns, Consumed;
    public static string LastDetonation = "";

    static int groundMask;
    /// <summary>What the car can stand on: every layer its body collides with, except players, loot, bullet-only and trigger layers
    /// (Factory floors are not all on LowPolyCollider: run 1 found none in front of the player).</summary>
    internal static int Ground
    {
        get
        {
            if (groundMask != 0) return groundMask;
            // what a player can stand on (run 3: a Factory platform was on none of LowPolyCollider/Terrain/Door, the car fell through it)
            int walker = LayerMask.NameToLayer("Player");
            var skip = new[] { "Player", "Loot", "HighPolyCollider", "HitCollider", "Deadbody", "PlayerSpiritAura", "Shells", "Triggers", "Water", "UI", "Grass", "Foliage", "PlayerCollisionTest", "Ignore Raycast" };
            int mask = 0;
            for (int l = 0; l < 32; l++)
            {
                var name = LayerMask.LayerToName(l);
                if (string.IsNullOrEmpty(name) || Array.IndexOf(skip, name) >= 0) continue;
                if (walker < 0 || !Physics.GetIgnoreLayerCollision(walker, l)) mask |= 1 << l;
            }
            mask |= LayerMask.GetMask("LowPolyCollider", "Terrain", "DoorLowPolyCollider");
            groundMask = mask;
            var names = new System.Collections.Generic.List<string>();
            for (int l = 0; l < 32; l++) if ((mask & (1 << l)) != 0) names.Add(LayerMask.LayerToName(l));
            Plugin.Note("ground layers: " + string.Join(", ", names));
            return groundMask;
        }
    }

    internal static bool IsKit(Item? item) => item != null && item.StringTemplateId == Plugin.CarTpl;
    internal static Player? Local => Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
    internal static bool HoldingKit(Player? p) => p != null && p.HandsController != null && IsKit(p.HandsController.Item);
    internal static bool CarOut => Car != null && Car.Alive;
    internal static bool Driving => Viewing && CarOut && HoldingKit(Car!.Owner) && Car.Owner.HealthController.IsAlive;

    internal static void EnsureRunner()
    {
        if (runner != null) return;
        var go = new GameObject("RCBombCarRunner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<Runner>();
    }

    // ------------------------------------------------------------ input

    internal static bool OnHandsCommand(IUsableItemController? controller, ECommand command, ref InputNode.ETranslateResult result)
    {
        if (controller == null || !IsKit(controller.Item)) return true;
        var player = Local;
        if (player == null || !player.HealthController.IsAlive) return true;
        if (Singleton<GameWorld>.Instance is HideoutGameWorld)   // no car bombs in the hideout: the remote stays a plain rangefinder there
        {
            if (command == ECommand.ToggleShooting) Plugin.Note("the RC car only works in a raid");
            return true;
        }
        switch (command)
        {
            case ECommand.ToggleShooting:
                SoundBank.Load(); SoundBank.RemoteClick(player, detonate: CarOut);   // the transmitter's switch, at your hands
                if (!CarOut) Deploy(player, controller.Item);
                else Car!.Detonate("remote");
                result = InputNode.ETranslateResult.Block;
                return false;
            case ECommand.ToggleAlternativeShooting:
                if (CarOut) { Viewing = !Viewing; Plugin.Note(Viewing ? "car view" : "own view"); }
                result = InputNode.ETranslateResult.Block;
                return false;
            case ECommand.EndAlternativeShooting:
                result = InputNode.ETranslateResult.Block;
                return false;
        }
        return true;
    }

    internal static void OnAxes(float[] axes)
    {
        EnsureRunner();
        if (!Driving) { Drive = Vector2.zero; return; }
        if (axes.Length > 3)
        {
            Drive = new Vector2(axes[0], axes[1]);
            LookDelta += new Vector2(axes[2], axes[3]);
        }
        for (int i = 0; i < axes.Length; i++) axes[i] = 0f;   // the body stands still while you drive
    }

    internal static bool OnMoveCommand(ECommand command, ref InputNode.ETranslateResult result)
    {
        if (!Driving) { Boost = false; return true; }
        switch (command)
        {
            case ECommand.ToggleSprinting: Boost = true; result = InputNode.ETranslateResult.Block; return false;
            case ECommand.EndSprinting: Boost = false; result = InputNode.ETranslateResult.Block; return false;
            case ECommand.Jump:
            case ECommand.Vaulting:
            case ECommand.ToggleStepLeft:
            case ECommand.ToggleStepRight:
            case ECommand.ReturnFromLeftStep:
            case ECommand.ReturnFromRightStep:
            case ECommand.ToggleLeanLeft:
            case ECommand.ToggleLeanRight:
            case ECommand.LeanLeftSlow:
            case ECommand.ToggleBlindAbove:
            case ECommand.ToggleBlindRight:
                result = InputNode.ETranslateResult.Block;
                return false;
        }
        return true;
    }

    internal static Vector2 DriveInput => DriveOverride ?? (Driving ? Drive : Vector2.zero);
    internal static bool BoostInput => BoostOverride ?? (Driving && Boost);

    // ------------------------------------------------------------ deploy / detonate / clean up

    /// <summary>Sets the car down about a metre in front of the player (closer if a wall is in the way) and switches to its camera.</summary>
    internal static RCCar? Deploy(Player player, Item kit)
    {
        EnsureRunner();
        if (CarOut) return Car;
        var cam = CameraManager.Instance?.Camera;
        var look = cam != null ? cam.transform.forward : player.Transform.forward;
        var fwd = Vector3.ProjectOnPlane(look, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) fwd = player.Transform.forward;
        fwd.Normalize();
        var chest = player.Position + Vector3.up * 0.6f;
        float reach = 1.0f;
        if (Physics.SphereCast(chest, 0.15f, fwd, out var wall, reach, Ground, QueryTriggerInteraction.Ignore)) reach = Mathf.Max(0.25f, wall.distance - 0.1f);
        var spot = chest + fwd * reach;
        Vector3 ground;
        if (Physics.Raycast(spot, Vector3.down, out var floor, 2.5f, Ground, QueryTriggerInteraction.Ignore)) ground = floor.point;
        else
        {
            // nothing under that spot on the usual layers: set it down at your own feet height, it settles on whatever is there
            ground = new Vector3(spot.x, player.Position.y, spot.z);
            string what = "nothing";
            if (Physics.Raycast(spot, Vector3.down, out var any, 2.5f, ~LayerMask.GetMask("Player", "HitCollider", "PlayerSpiritAura"), QueryTriggerInteraction.Ignore))
                what = $"'{any.collider.name}' on layer {LayerMask.LayerToName(any.collider.gameObject.layer)} at {any.point.y:F2}";
            Plugin.Warn($"no floor found under {spot} on layers {Ground:X} (all layers: {what}); setting the car at your feet height");
        }
        var go = new GameObject("RCBombCar");
        go.transform.SetPositionAndRotation(ground + Vector3.up * 0.03f, Quaternion.LookRotation(fwd, Vector3.up));
        var car = go.AddComponent<RCCar>();
        car.Init(player, kit);
        Car = car;
        Viewing = true;
        Deploys++;
        Plugin.Note($"car set down at {ground} ({reach:F2} m ahead)");
        return car;
    }

    internal static void OnCarGone(RCCar car, bool exploded)
    {
        if (Car == car) { Car = null; Viewing = false; Drive = Vector2.zero; Boost = false; }
        if (exploded && Plugin.Settings.singleUse) Consume(car.Owner, car.Kit);
    }

    /// <summary>Single use: put the remote away, then take the item out of the inventory (the cultist amulet's way of removing a special-slot item).</summary>
    static void Consume(Player? player, Item? kit)
    {
        if (player == null || kit == null || !player.HealthController.IsAlive) return;
        void Remove()
        {
            try
            {
                var controller = player.InventoryController;
                if (kit.CurrentAddress == null) return;   // already gone
                var discard = ItemManipulator.Discard(kit, controller, true);   // simulate, then run it as an operation (the cultist amulet's way)
                if (discard.Failed) { Plugin.Warn("could not remove the used car: " + discard.Error); return; }
                controller.Execute(new RemoveOperation(controller.GetAndIncrementNextOperationId(), controller, discard.Value), null);
                Consumed++;
                Plugin.Note("the used car was removed from the inventory");
            }
            catch (Exception e) { Plugin.ErrorOnce("consume", e); }
        }
        if (player.HandsController != null && player.HandsController.Item == kit)   // only put the remote away if it is THIS car's remote
        {
            bool done = false;
            player.SetEmptyHands(new Callback<IEmptyHandsController>(_ => { if (!done) { done = true; Remove(); } }));
            runner?.After(3f, () => { if (!done && player.HandsController?.Item != kit) { done = true; Remove(); } });
        }
        else Remove();
    }

    internal static void Cleanup()
    {
        try
        {
            if (Car != null) Car.Remove();
            Car = null; Viewing = false; Drive = Vector2.zero; Boost = false; DriveOverride = null; BoostOverride = null;
        }
        catch (Exception e) { Plugin.ErrorOnce("cleanup", e); }
    }

    /// <summary>Every frame from the runner: drop the car view when the remote leaves the hands or the player dies, and
    /// clear a car left behind by a raid that ended.</summary>
    internal static void Tick()
    {
        if (Car == null) return;
        if (!Singleton<GameWorld>.Instantiated) { Cleanup(); return; }
        if (Viewing && (!HoldingKit(Car.Owner) || !Car.Owner.HealthController.IsAlive)) { Viewing = false; Plugin.Note("the remote left your hands, back to your own view"); }
    }
}
