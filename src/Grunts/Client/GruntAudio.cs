using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Comfort.Common;
using UnityEngine;

namespace Grunts.Client;

/// <summary>
/// The pack's sounds from grunts_audio.bundle (Vorbis, compressed in memory), indexed by "voice/category" (v04/idle, ono02/pain_ono, ...)
/// and by "sfx", "footstep", "foley". Bundle clips are assets, so the game's audio reset at start-up does not empty them.
/// </summary>
internal static class GruntAudio
{
    private static readonly Dictionary<string, List<AudioClip>> banks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> last = new();
    private static readonly System.Random rng = new(3838);
    private static AssetBundle? bundle;
    public static int Count { get; private set; }

    public static void Load(string path, ManualLogSource log)
    {
        if (!File.Exists(path)) { log.LogWarning("Grunts: grunts_audio.bundle not found; Grunts keep the zombie sounds"); return; }
        bundle = AssetBundle.LoadFromFile(path);
        if (bundle == null) { log.LogWarning("Grunts: grunts_audio.bundle could not be opened"); return; }
        var index = bundle.LoadAsset<TextAsset>("assets/generated/grunts_audio_index.txt");
        if (index == null) { log.LogWarning("Grunts: the sound index is missing from the bundle"); return; }
        foreach (var line in index.text.Split('\n'))
        {
            var cut = line.IndexOf('|');
            if (cut <= 0) continue;
            var key = line.Substring(0, cut).Trim();
            var clip = bundle.LoadAsset<AudioClip>(line.Substring(cut + 1).Trim());
            if (clip == null) continue;
            clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            if (!banks.TryGetValue(key, out var list)) banks[key] = list = new List<AudioClip>();
            list.Add(clip);
            Count++;
        }
        log.LogInfo($"Grunts: {Count} sounds in {banks.Count} banks");
    }

    public static bool Has(string key) => banks.ContainsKey(key);

    /// <summary>A random clip (never the same one twice in a row) from the first category of the voice that has any.</summary>
    public static AudioClip? Pick(string voice, params string[] categories)
    {
        foreach (var c in categories)
        {
            var key = string.IsNullOrEmpty(voice) ? c : voice + "/" + c;
            if (banks.TryGetValue(key, out var list) && list.Count > 0) return From(key, list);
        }
        return null;
    }

    /// <summary>sfx clips whose name starts with the prefix (whoosh_, stab_, blade_hit_, gore_, body_hit_, kick_).</summary>
    public static AudioClip? Sfx(string prefix)
    {
        if (!banks.TryGetValue("sfx", out var list)) return null;
        var key = "sfx:" + prefix;
        var subset = list.Where(c => c.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return subset.Count == 0 ? null : From(key, subset);
    }

    private static AudioClip From(string key, List<AudioClip> list)
    {
        int i = 0;
        if (list.Count > 1)
        {
            last.TryGetValue(key, out var prev);
            i = rng.Next(list.Count - 1);
            if (i >= prev) i++;
        }
        last[key] = i;
        return list[i];
    }

    /// <summary>A sound at a point in the world through the game's own audio (occlusion, volume settings).</summary>
    public static void PlayAt(AudioClip? clip, Vector3 pos, float distance, float volume)
    {
        if (clip == null || !Singleton<BetterAudio>.Instantiated) return;
        try
        {
            Singleton<BetterAudio>.Instance.PlayAtPoint(pos, clip, distance, BetterAudio.AudioSourceGroupType.Character, 30, Mathf.Clamp01(volume), EOcclusionTest.Regular, null, false);
        }
        catch (Exception e) { Plugin.Instance?.Note("sound failed: " + e.Message); }
    }
}
