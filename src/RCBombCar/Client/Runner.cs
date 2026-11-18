using System;
using System.Collections;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>Made on the first raid frame (objects made in the plugin's Awake never get Update in this install). Ticks the remote, places the
/// car camera and draws the camera-feed overlay.</summary>
internal sealed class Runner : MonoBehaviour
{
    Texture2D? white, noise, scan;
    Color32[]? noisePixels;
    float noiseClock;
    GUIStyle? big, small, tiny;
    readonly System.Random rnd = new(11);

    public void After(float seconds, Action act) => StartCoroutine(Later(seconds, act));
    static IEnumerator Later(float s, Action act) { yield return new WaitForSeconds(s); try { act(); } catch (Exception e) { Plugin.ErrorOnce("delayed", e); } }

    void Update()
    {
        try { CarCamera.SafetyRestore(); Remote.Tick(); }
        catch (Exception e) { Plugin.ErrorOnce("tick", e); }
    }

    void LateUpdate()
    {
        try { CarCamera.UpdatePose(); }
        catch (Exception e) { Plugin.ErrorOnce("camera pose", e); }
    }

    void Textures()
    {
        if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
        if (scan == null)
        {
            scan = new Texture2D(1, 4) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
            scan.SetPixels(new[] { new Color(0, 0, 0, 0.22f), new Color(0, 0, 0, 0f), new Color(0, 0, 0, 0f), new Color(0, 0, 0, 0.08f) }); scan.Apply();
        }
        if (noise == null) { noise = new Texture2D(160, 90, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat }; }
    }

    void RefreshNoise(float strength)
    {
        if (noise == null) return;
        var px = noisePixels ??= noise.GetPixels32();
        for (int i = 0; i < px.Length; i++)
        {
            byte g = (byte)rnd.Next(0, 256);
            px[i] = new Color32(g, g, g, (byte)(strength * rnd.Next(0, 256)));
        }
        noise.SetPixels32(px); noise.Apply(false);
    }

    void Styles()
    {
        if (big != null) return;
        big = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(18, Screen.height / 22), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(13, Screen.height / 48), fontStyle = FontStyle.Bold };
        tiny = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(11, Screen.height / 62) };
    }

    static void Shadowed(Rect r, string text, GUIStyle style, Color color)
    {
        var old = GUI.color;
        GUI.color = new Color(0, 0, 0, 0.75f * color.a); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
        GUI.color = color; GUI.Label(r, text, style);
        GUI.color = old;
    }

    void OnGUI()
    {
        try
        {
            var car = Remote.Car;
            if (car == null || !car.Alive) return;
            if (Event.current.type != EventType.Repaint) return;
            Textures(); Styles();
            float w = Screen.width, h = Screen.height;
            if (!Remote.Driving)
            {
                if (Remote.HoldingKit(car.Owner))
                    Shadowed(new Rect(w * 0.5f - 300, h - 120, 600, 30), $"RC car out, {car.Distance:F0} m away.   LMB  set it off     RMB  car view", small!, new Color(1f, 0.85f, 0.4f, 0.95f));
                return;
            }
            // the feed: scanlines, grain that thickens toward the edge of the signal range, corner marks and readouts
            float range = Mathf.Max(1f, Plugin.Settings.signalRange);
            float weak = Mathf.Clamp01((car.Distance - range * 0.6f) / (range * 0.4f));
            float grain = car.SignalLost ? 0.9f : 0.06f + weak * 0.5f;
            noiseClock += Time.unscaledDeltaTime;
            if (noiseClock > 0.05f) { noiseClock = 0f; RefreshNoise(grain); }
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(new Rect(0, 0, w, h), scan!, new Rect(0, 0, 1, h / 4f));
            GUI.DrawTexture(new Rect(0, 0, w, h), noise!, ScaleMode.StretchToFill, true);
            var line = new Color(0.85f, 1f, 0.85f, 0.7f);
            GUI.color = line;
            float m = h * 0.06f, L = h * 0.07f, th = 2f;
            foreach (var (x, y, sx, sy) in new[] { (m, m, 1, 1), (w - m, m, -1, 1), (m, h - m, 1, -1), (w - m, h - m, -1, -1) })
            {
                GUI.DrawTexture(new Rect(sx > 0 ? x : x - L, sy > 0 ? y : y - th, L, th), white!);
                GUI.DrawTexture(new Rect(sx > 0 ? x : x - th, sy > 0 ? y : y - L, th, L), white!);
            }
            // centre mark
            GUI.DrawTexture(new Rect(w * 0.5f - 10, h * 0.5f, 20, 1), white!);
            GUI.DrawTexture(new Rect(w * 0.5f, h * 0.5f - 10, 1, 20), white!);
            GUI.color = Color.white;
            bool blink = (Time.unscaledTime % 1f) < 0.6f;
            Shadowed(new Rect(m + 10, m + 6, 300, 30), (blink ? "● " : "   ") + "REC   CAM 1", small!, new Color(1f, 0.25f, 0.2f, 0.95f));
            int bars = car.SignalLost ? 0 : Mathf.Clamp(4 - Mathf.FloorToInt(weak * 4f), 1, 4);
            Shadowed(new Rect(w - m - 260, m + 6, 250, 30), $"{car.Distance,4:F0} m   SIG {new string('|', bars)}{new string('.', 4 - bars)}", small!, line);
            Shadowed(new Rect(m + 10, h - m - 40, 300, 30), $"{car.Speed * 3.6f,3:F0} km/h{(Remote.BoostInput ? "  BOOST" : "")}", small!, line);
            Shadowed(new Rect(w * 0.5f - 320, h - m - 40, 640, 30), "WASD drive    Shift boost    LMB set it off    RMB your view", tiny!, new Color(1f, 1f, 1f, 0.8f));
            if (car.HitsTaken > 0) Shadowed(new Rect(w - m - 260, h - m - 40, 250, 30), $"DAMAGE {car.HitsTaken}/{Mathf.Max(1, Plugin.Settings.hitsToDestroy)}", small!, new Color(1f, 0.45f, 0.3f, 0.95f));
            if (car.SignalLost) Shadowed(new Rect(0, h * 0.4f, w, h * 0.2f), "NO SIGNAL", big!, new Color(1f, 1f, 1f, 0.9f));
        }
        catch (Exception e) { Plugin.ErrorOnce("overlay", e); }
    }
}
