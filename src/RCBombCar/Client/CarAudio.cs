using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Comfort.Common;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>
/// The sound files (tools/make_sounds.py: made from scratch, no recordings) read from the "sounds" folder next to the car's model.
/// Plain 16-bit WAV, read here into AudioClips. The samples are kept so a clip the game's audio reset emptied can be refilled.
/// </summary>
internal static class SoundBank
{
    static readonly Dictionary<string, float[]> samples = new();
    static readonly Dictionary<string, int> rates = new();
    static readonly Dictionary<string, AudioClip> clips = new();
    static bool tried;
    public static bool Ready { get; private set; }
    public static float RevPerMetre = 57.9f, TyreAnchorSpeed = 5f;
    public static readonly List<(string name, float rev)> Motor = new();
    public static readonly Dictionary<string, float> TyreGain = new() { ["hard"] = 0.45f, ["metal"] = 0.55f, ["soft"] = 0.5f, ["gravel"] = 0.8f };

    public static void Load()
    {
        if (tried) return;
        tried = true;
        try
        {
            var dir = Path.Combine(CarModel.AssetDir(), "sounds");
            if (!Directory.Exists(dir)) { Plugin.Warn("no sounds folder next to the car; it uses its simple built-in motor sound"); return; }
            foreach (var path in Directory.GetFiles(dir, "*.wav"))
            {
                var (data, rate) = ReadWav(path);
                var name = Path.GetFileNameWithoutExtension(path);
                samples[name] = data; rates[name] = rate;
            }
            var manifest = Path.Combine(dir, "sounds.json");
            if (File.Exists(manifest))
            {
                var j = JObject.Parse(File.ReadAllText(manifest));
                RevPerMetre = j.Value<float?>("revPerMetre") ?? RevPerMetre;
                TyreAnchorSpeed = j.Value<float?>("tyreAnchorSpeed") ?? TyreAnchorSpeed;
                if (j["tyreGain"] is JObject tg) foreach (var p in tg.Properties()) TyreGain[p.Name] = (float)p.Value;
                if (j["motor"] is JArray m) foreach (var e in m) Motor.Add((Path.GetFileNameWithoutExtension((string)e["file"]!), (float)e["rev"]!));
            }
            if (Motor.Count == 0) Motor.AddRange(new[] { ("motor_crawl", 60f), ("motor_mid", 210f), ("motor_high", 480f) });
            Motor.Sort((a, b) => a.rev.CompareTo(b.rev));
            Ready = Motor.TrueForAll(m => samples.ContainsKey(m.name)) && samples.ContainsKey("tyre_hard");
            Plugin.Note($"sounds loaded: {samples.Count} files{(Ready ? "" : " (motor or tyre files missing, using the simple motor sound)")}");
        }
        catch (Exception e) { Plugin.ErrorOnce("sounds", e); Ready = false; }
    }

    public static bool Has(string name) => samples.ContainsKey(name);

    /// <summary>The clip, made on first use and refilled if the game emptied it.</summary>
    public static AudioClip? Clip(string name)
    {
        if (!samples.TryGetValue(name, out var data)) return null;
        if (!clips.TryGetValue(name, out var clip) || clip == null)
        {
            clip = AudioClip.Create("rc_" + name, data.Length, 1, rates[name], false);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clips[name] = clip;
            clip.SetData(data, 0);
        }
        return clip;
    }

    /// <summary>Refill every clip (cheap; done for each new car, the way the old motor clip was).</summary>
    public static void Refresh() { foreach (var kv in clips) if (kv.Value != null) kv.Value.SetData(samples[kv.Key], 0); }

    static (float[] data, int rate) ReadWav(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException(path + ": not a WAV");
        r.ReadInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException(path + ": not a WAV");
        int channels = 1, rate = 48000, bits = 16;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4)); int size = r.ReadInt32();
            if (id == "fmt ")
            {
                int format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (format != 1 || bits != 16) throw new InvalidDataException(path + ": only 16-bit PCM WAV");
                r.BaseStream.Position += size - 16;
            }
            else if (id == "data")
            {
                int frames = size / 2 / channels; var data = new float[frames];
                var raw = r.ReadBytes(frames * 2 * channels);
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(raw, (i * channels + c) * 2) / 32768f;
                    data[i] = sum / channels;
                }
                return (data, rate);
            }
            else r.BaseStream.Position += size + (size & 1);
        }
        throw new InvalidDataException(path + ": no audio data");
    }

    // ------------------------------------------------------------ the remote's switches (at the player)

    static readonly System.Random rnd = new(5);
    public static void RemoteClick(Player player, bool detonate)
    {
        try
        {
            if (!Ready) return;
            var clip = Clip(detonate ? "remote_click_2" : "remote_click_1");
            if (clip == null) return;
            var go = new GameObject("rc_remote_click");
            go.transform.position = player.Position + Vector3.up * 1.2f + player.Transform.forward * 0.35f;
            var src = go.AddComponent<AudioSource>();
            src.spatialBlend = 0.6f; src.minDistance = 0.5f; src.maxDistance = 12f; src.rolloffMode = AudioRolloffMode.Linear; src.dopplerLevel = 0f;
            src.pitch = 1f + ((float)rnd.NextDouble() - 0.5f) * 0.06f;
            CarAudio.Route(src, world: true);
            src.PlayOneShot(clip, 0.6f * Plugin.Settings.soundVolume);
            UnityEngine.Object.Destroy(go, clip.length / Mathf.Max(0.5f, src.pitch) + 0.2f);
        }
        catch (Exception e) { Plugin.ErrorOnce("remote click", e); }
    }
}

/// <summary>
/// The car's sound, layered the way a real 1/10 brushed truck sounds (see tools/make_sounds.py): three motor takes crossfaded and pitched by
/// the motor's turning rate, the speed controller's whine at part throttle and when braking, tyres by the ground's own surface type (the
/// game's footstep surface), the steering servo, knocks and landings, start-up beeps. Every layer plays twice: in the world (3D, through the
/// game's occluded mixer) and on the camera feed (2D, filtered like a small onboard mic) while you drive. Same mapping as the preview mix in
/// make_sounds.py, so what the preview mix plays is what plays here.
/// </summary>
internal sealed class CarAudio : MonoBehaviour
{
    sealed class Layer
    {
        public AudioSource World = null!, Feed = null!;
        float vol, feedVol; bool paused;
        public void Set(float worldVolume, float feedVolume, float pitch, float dt, float rate = 12f)
        {
            float k = 1f - Mathf.Exp(-rate * dt);
            vol = Mathf.Lerp(vol, worldVolume, k); feedVol = Mathf.Lerp(feedVol, feedVolume, k);
            bool silent = vol < 0.0005f && feedVol < 0.0005f;
            if (silent && !paused) { World.Pause(); Feed.Pause(); paused = true; }
            else if (!silent && paused) { World.UnPause(); Feed.UnPause(); paused = false; }
            World.volume = vol; Feed.volume = feedVol;
            World.pitch = pitch; Feed.pitch = pitch;
        }
    }

    public enum Ground { Hard, Metal, Soft, Gravel }
    static readonly string[] groundNames = { "hard", "metal", "soft", "gravel" };

    RCCar car = null!;
    readonly List<Layer> motor = new();
    Layer? whine, statik;
    readonly Layer?[] tyres = new Layer?[4];
    readonly float[] tyreMix = new float[4];
    AudioSource oneWorld = null!, oneFeed = null!;
    float load = 0.25f, lastSteer, nextServo, nextKnock, nextSurface, airborneSince = -1f, armAt;
    bool armed;
    int lastServo = -1, lastKnock = -1, lastLand = -1;
    public Ground Surface { get; private set; } = Ground.Hard;
    public static int ServoPlays, KnockPlays, LandPlays, ArmPlays;   // QA probe counters
    static readonly System.Random rnd = new(17);

    public static CarAudio? TryAdd(RCCar car)
    {
        SoundBank.Load();
        if (!SoundBank.Ready) return null;
        SoundBank.Refresh();
        var a = car.gameObject.AddComponent<CarAudio>();
        try { a.Build(car); return a; }
        catch (Exception e) { Plugin.ErrorOnce("car sound setup", e); Destroy(a); return null; }   // the old motor sound takes over
    }

    void Build(RCCar owner)
    {
        car = owner;
        var feedGo = new GameObject("rc_feed_audio");
        feedGo.transform.SetParent(transform, false);
        Layer L(string name, bool worldToo = true)
        {
            var clip = SoundBank.Clip(name)!;
            var w = MakeSource(gameObject, clip, true, true);
            var f = MakeSource(feedGo, clip, true, false);
            if (!worldToo) w.mute = true;
            return new Layer { World = w, Feed = f };
        }
        foreach (var (name, _) in SoundBank.Motor) motor.Add(L(name));
        if (SoundBank.Has("esc_whine")) whine = L("esc_whine");
        for (int i = 0; i < 4; i++) if (SoundBank.Has("tyre_" + groundNames[i])) tyres[i] = L("tyre_" + groundNames[i]);
        if (SoundBank.Has("feed_static")) statik = L("feed_static", worldToo: false);
        oneWorld = MakeSource(gameObject, null, false, true);
        oneFeed = MakeSource(feedGo, null, false, false);
        oneWorld.volume = 1f; oneFeed.volume = 1f;   // PlayOneShot scales by the source's volume (fixture run 2: left at 0, the beeps were silent)
        // the feed: a small onboard mic, thin at both ends and a little gritty. Audio filters only go on an object that already has an
        // AudioSource (fixture run 2026-11-18: added first, AddComponent gave null and the whole set fell back to the old motor).
        var hp = feedGo.AddComponent<AudioHighPassFilter>(); if (hp != null) hp.cutoffFrequency = 170f;
        var lp = feedGo.AddComponent<AudioLowPassFilter>(); if (lp != null) lp.cutoffFrequency = 7200f;
        var dist = feedGo.AddComponent<AudioDistortionFilter>(); if (dist != null) dist.distortionLevel = 0.12f;
        tyreMix[0] = 1f;
        armAt = Time.time + 0.35f;   // the speed controller beeps once it has power
    }

    static AudioSource MakeSource(GameObject on, AudioClip? clip, bool loop, bool world)
    {
        var s = on.AddComponent<AudioSource>();
        s.clip = clip; s.loop = loop; s.playOnAwake = false; s.volume = 0f;
        if (world)
        {
            s.spatialBlend = 1f; s.dopplerLevel = 0.6f; s.minDistance = 1f;
            s.maxDistance = Mathf.Max(20f, Plugin.Settings.noiseRange * 1.35f);
            s.rolloffMode = AudioRolloffMode.Custom;   // a small thing: loud close by, gone by about 40 m
            s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(0.08f, 0.6f), new Keyframe(0.25f, 0.25f), new Keyframe(0.55f, 0.07f), new Keyframe(1f, 0f)));
        }
        else s.spatialBlend = 0f;
        Route(s, world);
        if (loop && clip != null) { s.timeSamples = UnityEngine.Random.Range(0, clip.samples); s.Play(); }
        return s;
    }

    /// <summary>Through the game's mixer so its volume setting applies (world sounds on the occluded group).</summary>
    public static void Route(AudioSource s, bool world)
    {
        try
        {
            var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (audio != null) s.outputAudioMixerGroup = world ? (audio.SimpleOccludedMixerGroup ?? audio.MasterMixerGroup) : audio.MasterMixerGroup;
        }
        catch (Exception e) { Plugin.ErrorOnce("sound mixer", e); }
    }

    static float Smooth(float a, float b, float x) { x = Mathf.Clamp01((x - a) / (b - a)); return x * x * (3f - 2f * x); }

    void LateUpdate()
    {
        if (car == null || !car.Alive) return;
        try { Tick(Time.deltaTime); }
        catch (Exception e) { Plugin.ErrorOnce("car sound", e); }
    }

    void Tick(float dt)
    {
        var st = Plugin.Settings;
        float master = st.soundVolume;
        float top = Mathf.Max(1f, st.topSpeed);
        float speed = car.Speed, v = car.SignedSpeed;
        var input = car.SignalLost ? Vector2.zero : Remote.DriveInput;
        float throttle = input.y;
        bool pushing = Mathf.Abs(throttle) > 0.01f;
        bool driving = pushing && (Mathf.Sign(throttle) == Mathf.Sign(v) || speed < 0.2f);
        bool braking = pushing && !driving;
        bool grounded = car.Grounded;
        if (!grounded && airborneSince < 0f) airborneSince = Time.time; else if (grounded) airborneSince = -1f;

        // the feed: the onboard mic while you watch through the car; static rises as the signal fades
        float range = Mathf.Max(1f, st.signalRange);
        float weak = Mathf.Clamp01((car.Distance - range * 0.6f) / (range * 0.4f));
        bool viewing = Remote.Driving && Remote.Car == car;
        float feed = viewing && !car.SignalLost ? st.feedVolume * 0.6f * (1f - 0.7f * weak) : 0f;
        statik?.Set(0f, viewing ? master * st.feedVolume * (car.SignalLost ? 0.55f : 0.02f + 0.5f * Mathf.Pow(weak, 1.5f)) : 0f, 1f, dt);

        // motor: three takes crossfaded (equal power) in log turning rate, each pitched to the actual rate
        load = Mathf.Lerp(load, driving ? 1f : braking ? 0.6f : 0.25f, 1f - Mathf.Exp(-8f * dt));
        float fr = speed * SoundBank.RevPerMetre * (!grounded && pushing ? 1.25f : 1f);
        float level = Smooth(0.05f, 0.6f, speed) * (0.55f + 0.45f * load) * (0.5f + 0.5f * Mathf.Min(1f, speed / top));
        float lr = Mathf.Log(Mathf.Max(fr, 1f));
        for (int i = 0; i < motor.Count; i++)
        {
            float la = Mathf.Log(SoundBank.Motor[i].rev), g = 0f;
            if (i == 0 && lr <= la) g = 1f;
            else if (i == motor.Count - 1 && lr > la) g = 1f;
            if (i > 0) { float lp = Mathf.Log(SoundBank.Motor[i - 1].rev); if (lr > lp && lr <= la) g = Mathf.Sin(Mathf.PI / 2f * (lr - lp) / (la - lp)); }
            if (i < motor.Count - 1) { float ln = Mathf.Log(SoundBank.Motor[i + 1].rev); if (lr > la && lr < ln) g = Mathf.Cos(Mathf.PI / 2f * (lr - la) / (ln - la)); }
            float pitch = Mathf.Clamp(fr / SoundBank.Motor[i].rev, 0.35f, 2f);
            float vol = 0.9f * level * g * master;
            motor[i].Set(vol, vol * feed, pitch, dt, 20f);
        }

        // speed controller whine: part throttle on launch, pushing against something, drag brake (lower)
        if (whine != null)
        {
            float duty = Mathf.Clamp01(0.25f + 0.75f * speed / top);
            float w = driving ? 0.35f * Mathf.Sin(Mathf.PI * duty) + (speed < 0.3f ? 0.2f : 0f) : braking ? 0.25f : 0f;
            whine.Set(w * master, w * master * feed, braking ? 0.75f : 1f, dt, 10f);
        }

        // tyres: by the ground's own surface type, crossfaded when it changes
        if (Time.time >= nextSurface) { nextSurface = Time.time + 0.15f; Surface = ReadSurface(); }
        float tg = grounded ? 0.8f * Smooth(0.1f, 3f, speed) * (0.6f + 0.4f * Mathf.Min(1f, speed / top)) : 0f;
        float tpitch = Mathf.Clamp(speed / Mathf.Max(0.5f, SoundBank.TyreAnchorSpeed), 0.3f, 2.2f);
        for (int i = 0; i < 4; i++)
        {
            tyreMix[i] = Mathf.MoveTowards(tyreMix[i], (int)Surface == i ? 1f : 0f, dt / 0.15f);
            var t = tyres[i];
            if (t == null) continue;
            float vol = tg * tyreMix[i] * (SoundBank.TyreGain.TryGetValue(groundNames[i], out var gk) ? gk : 0.5f) * master;
            t.Set(vol, vol * feed, tpitch, dt, 15f);
        }

        // one-shots
        // start-up beeps: run 3 measured them at -14 dBFS a metre away, louder than a grenade's second; now about the motor's level
        if (!armed && Time.time >= armAt) { armed = true; Play("esc_arm", 0.15f, feed, 0f); ArmPlays++; }
        if (!car.SignalLost && Mathf.Abs(input.x - lastSteer) >= 0.5f && Time.time >= nextServo)
        {
            nextServo = Time.time + 0.1f; lastSteer = input.x;
            Play(Pick("servo_", 6, ref lastServo), 0.4f, feed, 0.04f); ServoPlays++;
        }
        if (car.SignalLost) lastSteer = 0f;   // the servo centres when the link drops
        feedNow = feed;
    }

    float feedNow;

    Ground ReadSurface()
    {
        try
        {
            if (Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down, out var hit, 1.2f, LayersMaskController.AudioControllerStepLayerMask, QueryTriggerInteraction.Ignore)
                && hit.collider != null && hit.collider.GetComponent<BaseBallistic>() is BaseBallistic b && b != null)
                switch (b.GetSurfaceSound(hit.point))
                {
                    case BaseBallistic.ESurfaceSound.Metal: case BaseBallistic.ESurfaceSound.MetalThin: case BaseBallistic.ESurfaceSound.GarbageMetal: return Ground.Metal;
                    case BaseBallistic.ESurfaceSound.Soil: case BaseBallistic.ESurfaceSound.Grass: case BaseBallistic.ESurfaceSound.Swamp: case BaseBallistic.ESurfaceSound.Snow:
                    case BaseBallistic.ESurfaceSound.Puddle: case BaseBallistic.ESurfaceSound.Garbage: return Ground.Soft;
                    case BaseBallistic.ESurfaceSound.Gravel: return Ground.Gravel;
                    default: return Ground.Hard;
                }
        }
        catch (Exception e) { Plugin.ErrorOnce("surface", e); }
        return Surface;
    }

    static string Pick(string prefix, int count, ref int last)
    {
        int k = rnd.Next(count - 1); if (k >= last) k++;   // never the same one twice in a row
        if (last < 0) k = rnd.Next(count);
        last = k; return prefix + (k + 1);
    }

    void Play(string name, float volume, float feed, float jitter)
    {
        var clip = SoundBank.Clip(name);
        if (clip == null) return;
        float pitch = 1f + ((float)rnd.NextDouble() * 2f - 1f) * jitter;
        oneWorld.pitch = pitch; oneFeed.pitch = pitch;
        float v = volume * Plugin.Settings.soundVolume;
        oneWorld.PlayOneShot(clip, v);
        if (feed > 0f) oneFeed.PlayOneShot(clip, v * feed);
    }

    // ------------------------------------------------------------ events from the driving code

    public void WallHit(float impactSpeed)
    {
        if (impactSpeed < 0.6f || Time.time < nextKnock) return;
        nextKnock = Time.time + 0.15f;
        Play(Pick("knock_", 4, ref lastKnock), Mathf.Clamp(impactSpeed / 6f, 0.2f, 1f), feedNow, 0.05f); KnockPlays++;
    }

    public void Landed(float fallSpeed)
    {
        if (fallSpeed < 1.0f) return;
        Play(Pick("land_", 4, ref lastLand), Mathf.Clamp(fallSpeed / 5f, 0.25f, 1f), feedNow, 0.05f); LandPlays++;
    }

    public void SteppedUp(float height)
    {
        if (height < 0.02f || car.Speed < 1.5f || Time.time < nextKnock) return;
        nextKnock = Time.time + 0.15f;
        Play(Pick("knock_", 4, ref lastKnock), 0.2f, feedNow, 0.08f); KnockPlays++;
    }
}
