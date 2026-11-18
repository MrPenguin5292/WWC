using System;
using System.Collections.Generic;
using System.Text;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace RCBombCar.Client;

/// <summary>Only touched when BigBrain is loaded (this file is the only one that names its types).</summary>
internal static class BotLayerSetup
{
    // every brain that carries a gun; the infected (melee), the BTR and the peaceful ones are left out
    static readonly List<string> Brains = new()
    {
        "PMC", "PmcUsec", "PmcBear", "Assault", "CursAssault", "ExUsec", "Marksman", "Obdolbs", "ArenaFighter",
        "BossBully", "FollowerBully", "BossGluhar", "FollowerGluharAssault", "FollowerGluharProtect", "FollowerGluharScout",
        "BossKojaniy", "FollowerKojaniy", "BossSanitar", "FollowerSanitar", "Tagilla", "TagillaAgro", "TagillaFollower",
        "Killa", "KillaAgro", "Knight", "BigPipe", "BirdEye", "BossBoar", "BoarSniper", "KolonSec", "BossKolontay",
        "SectantPriest", "SectantWarrior", "BossPartisan", "FlKlnAslt", "BossZryachiy", "Fl_Zraychiy", "RavangeZryachiy", "HelperAgro", "PrizrakSt", "SctPredvst",
    };

    public static void Register(int priority) => BrainManager.AddCustomLayer(typeof(RCCarLayer), Brains, priority);
}

/// <summary>A bot that sees the car (in front of it, in range, nothing in between) reacts after a short moment: it stops and shoots the car,
/// or runs if the car is already right next to it. A real fight with a visible enemy wins unless the car is close.</summary>
internal sealed class RCCarLayer : CustomLayer
{
    float nextCheck, seenSince = -1f, lastSeen = -99f, activeUntil = -1f, fightSince = -1f;
    public static int Activations;

    public RCCarLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

    public override string GetName() => "RCBombCar";

    public override bool IsActive()
    {
        try
        {
            var car = Remote.Car;
            if (car == null || !car.Alive || BotOwner == null || BotOwner.IsDead) { seenSince = -1f; activeUntil = -1f; return false; }
            if (Time.time >= nextCheck)
            {
                nextCheck = Time.time + 0.2f + UnityEngine.Random.value * 0.1f;
                if (Sees(car)) { if (seenSince < 0f) seenSince = Time.time; lastSeen = Time.time; }
                else if (Time.time - lastSeen > 2.5f) seenSince = -1f;
            }
            // audit 2026-11-17 (run 10: the layer started 166 times in one raid, flipping every frame): decide with hysteresis. A real fight
            // only wins once the enemy has been in view for a second, and once on, the layer stays on at least a second.
            var enemy = BotOwner.Memory?.GoalEnemy;
            float d = Vector3.Distance(BotOwner.Position, car.AimPoint);
            bool fighting = enemy != null && enemy.IsVisible && d > 15f;
            if (fighting) { if (fightSince < 0f) fightSince = Time.time; } else fightSince = -1f;
            bool want = seenSince >= 0f && Time.time - seenSince >= Plugin.Settings.botReactSeconds && !(fightSince >= 0f && Time.time - fightSince > 1f);
            if (want) activeUntil = Time.time + 1f;
            return Time.time < activeUntil;
        }
        catch (Exception e) { Plugin.ErrorOnce("bot layer", e); return false; }
    }

    bool Sees(RCCar car)
    {
        if (car.Owner != null && BotOwner.BotsGroup != null && !BotOwner.BotsGroup.IsPlayerEnemy(car.Owner)) return false;   // bots on your side leave it alone
        var head = BotOwner.MyHead.position;
        var to = car.AimPoint - head;
        float d = to.magnitude;
        float sight = Plugin.Settings.botSeeDistance;
        try { float clear = BotOwner.LookSensor.ClearVisibleDist; if (clear > 1f) sight = Mathf.Min(sight, clear); } catch { }
        if (d > sight) return false;
        bool close = d < 6f && car.Speed > 0.4f;   // a buzzing car at your feet gets noticed from any side
        if (!close && Vector3.Angle(BotOwner.LookDirection, to) > 70f) return false;
        if (Physics.Raycast(head, to / d, out var hit, d + 0.3f, LayersMaskController.HighPolyWithTerrainMaskAI, QueryTriggerInteraction.Ignore) && hit.collider != car.HitCollider) return false;
        return true;
    }

    public override Action GetNextAction() => new(typeof(RCCarLogic), "RC car spotted");

    public override bool IsCurrentActionEnding() => false;

    public override void Start() { Activations++; Remote.BotLayerStarts++; }
}

internal sealed class RCCarLogic : CustomLogic
{
    Vector3 scatter; float nextScatter, nextPath;
    bool running;
    static readonly Dictionary<int, float> lastShout = new();

    public RCCarLogic(BotOwner botOwner) : base(botOwner) { }

    public override void Start()
    {
        try
        {
            BotOwner.Mover?.Stop();
            BotOwner.Mover?.Sprint(false, true);
            // one shout per bot every 12 s, not one per start
            if (!lastShout.TryGetValue(BotOwner.Id, out var said) || Time.time - said > 12f) { lastShout[BotOwner.Id] = Time.time; BotOwner.BotTalk?.TrySay(EPhraseTrigger.OnEnemyGrenade); }
        }
        catch (Exception e) { Plugin.ErrorOnce("bot logic start", e); }
    }

    public override void Stop()
    {
        try { BotOwner.ShootData?.EndShoot(); BotOwner.Mover?.Sprint(false, true); }
        catch (Exception e) { Plugin.ErrorOnce("bot logic stop", e); }
    }

    public override void Update(CustomLayer.ActionData data)
    {
        try
        {
            var car = Remote.Car;
            if (car == null || !car.Alive) return;
            var aim = car.AimPoint;
            float d = Vector3.Distance(BotOwner.Position, aim);
            if (Plugin.Settings.botsRunWithin > 0f && d < Plugin.Settings.botsRunWithin)
            {
                if (!running || Time.time > nextPath)
                {
                    nextPath = Time.time + 1f;
                    var away = BotOwner.Position + Vector3.ProjectOnPlane(BotOwner.Position - aim, Vector3.up).normalized * 9f;
                    if (NavMesh.SamplePosition(away, out var nav, 5f, NavMesh.AllAreas))
                    {
                        BotOwner.Mover.GoToPoint(nav.position, false, 1f, false, false, false, false);
                        BotOwner.Mover.Sprint(true, true);
                        if (!running) Remote.BotRuns++;
                        running = true;
                    }
                }
                BotOwner.Steering.LookToMovingDirection();
                return;
            }
            if (running) { running = false; BotOwner.Mover.Stop(); BotOwner.Mover.Sprint(false, true); }
            // aim with a little wobble that tightens the closer the car is, so not every round lands
            if (Time.time > nextScatter)
            {
                nextScatter = Time.time + 0.35f;
                scatter = UnityEngine.Random.insideUnitSphere * Mathf.Lerp(0.04f, 0.35f, Mathf.Clamp01(d / 50f));
            }
            var point = aim + scatter;
            BotOwner.Steering.LookToPoint(point);
            var from = BotOwner.MyHead.position;
            if (Vector3.Angle(BotOwner.LookDirection, point - from) < 7f && BotOwner.ShootData.Shoot()) Remote.BotShots++;
        }
        catch (Exception e) { Plugin.ErrorOnce("bot logic", e); }
    }

    public override void BuildDebugText(StringBuilder sb) => sb.AppendLine("RC car: shooting it");
}
