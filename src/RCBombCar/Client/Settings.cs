using System;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RCBombCar.Client;

public sealed class BlastSettings
{
    public float killRadius = 5f;
    public float maxRadius = 10f;
    public float strength = 160f;
    public int fragments = 30;
    public string effect = "Artillery_mine";
}

/// <summary>The "car" block of the server mod's config.json (one file for the whole mod). Missing or broken = these defaults.</summary>
public sealed class CarSettings
{
    public bool singleUse = true;
    public float topSpeed = 8f;
    public float boostSpeed = 11f;
    public float signalRange = 150f;
    public int hitsToDestroy = 3;
    public BlastSettings blast = new();
    public bool botsReact = true;
    public float botSeeDistance = 70f;
    public float botReactSeconds = 0.7f;
    public float noiseRange = 30f;
    public float botsRunWithin = 4f;
    public int botLayerPriority = 82;
    public bool verboseLog = false;
    public float soundVolume = 1f;   // the car's own sounds (motor, tyres, servo, knocks, beeps, remote clicks)
    public float feedVolume = 1f;    // how loud the car sounds on the camera feed while you drive

#if WWC   // built into WWC: its config.json is in WWC's RCBombCar folder
    public const string ServerFolder = "Widowz-WWC.Server\\RCBombCar";
#else
    public const string ServerFolder = "Widowz-RCBombCar.Server";
#endif

    public static CarSettings Load(string gameRoot, ManualLogSource log)
    {
        var path = Path.Combine(gameRoot, "SPT_Runtime", "user", "mods", ServerFolder, "config.json");
        var s = new CarSettings();
        try
        {
            if (!File.Exists(path)) { log.LogWarning($"RC car: {path} not found, using the normal car settings"); return s.Sane(); }
            var root = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            if (root["car"] is JObject car) s = car.ToObject<CarSettings>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Ignore })) ?? new CarSettings();
        }
        catch (Exception e) { log.LogError($"RC car: config.json could not be read ({e.Message}), using the normal car settings"); s = new CarSettings(); }
        return s.Sane();
    }

    CarSettings Sane()
    {
        blast ??= new BlastSettings();
        topSpeed = Mathf.Clamp(topSpeed, 1f, 30f);
        boostSpeed = Mathf.Clamp(boostSpeed, topSpeed, 40f);
        signalRange = Mathf.Clamp(signalRange, 10f, 2000f);
        hitsToDestroy = Mathf.Clamp(hitsToDestroy, 0, 100);
        blast.killRadius = Mathf.Clamp(blast.killRadius, 0.5f, 30f);
        blast.maxRadius = Mathf.Clamp(blast.maxRadius, blast.killRadius, 60f);
        blast.strength = Mathf.Clamp(blast.strength, 0f, 1000f);
        blast.fragments = Mathf.Clamp(blast.fragments, 0, 200);
        if (string.IsNullOrWhiteSpace(blast.effect)) blast.effect = "Artillery_mine";
        botSeeDistance = Mathf.Clamp(botSeeDistance, 5f, 300f);
        botReactSeconds = Mathf.Clamp(botReactSeconds, 0f, 10f);
        noiseRange = Mathf.Clamp(noiseRange, 0f, 200f);
        botsRunWithin = Mathf.Clamp(botsRunWithin, 0f, 30f);
        botLayerPriority = Mathf.Clamp(botLayerPriority, 1, 1000);
        soundVolume = Mathf.Clamp(soundVolume, 0f, 3f);
        feedVolume = Mathf.Clamp(feedVolume, 0f, 3f);
        return this;
    }

    public override string ToString() =>
        $"singleUse={singleUse} speed={topSpeed}/{boostSpeed} range={signalRange} hits={hitsToDestroy} blast={blast.killRadius}/{blast.maxRadius}/{blast.strength}/{blast.fragments}/{blast.effect} bots={botsReact} see={botSeeDistance} react={botReactSeconds} noise={noiseRange} run={botsRunWithin} prio={botLayerPriority}";
}
