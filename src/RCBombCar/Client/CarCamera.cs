using System;
using EFT.CameraControl;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>
/// The car view. The game's own first-person camera is moved to a chase position behind the car just before it culls and renders, and put
/// back right after, so the game's post effects, night vision and so on stay and nothing that reads the camera between frames sees a
/// difference. The mouse swings the view around the car; it eases back behind the car when the mouse rests.
/// </summary>
internal static class CarCamera
{
    static bool hooked, moved;
    static Camera? movedCam;
    static Vector3 savedLocalPos; static Quaternion savedLocalRot;
    static Vector3 pose; static Quaternion poseRot = Quaternion.identity;
    static bool poseValid;
    static float yawOffset, pitchOffset = 14f, lastMouse;
    static Vector3 smoothed; static bool hasSmoothed;
    public static int FramesMoved;

    public static void Hook()
    {
        if (hooked) return;
        hooked = true;
        Camera.onPreCull += OnPreCull;
        Camera.onPostRender += OnPostRender;
    }

    /// <summary>From the runner's LateUpdate: where the camera should be this frame.</summary>
    public static void UpdatePose()
    {
        var car = Remote.Car;
        if (!Remote.Driving || car == null) { poseValid = false; hasSmoothed = false; Remote.LookDelta = Vector2.zero; return; }
        var look = Remote.LookDelta; Remote.LookDelta = Vector2.zero;
        if (look.sqrMagnitude > 0.0001f) lastMouse = Time.time;
        yawOffset = Mathf.Clamp(yawOffset + look.x * 1.0f, -170f, 170f);
        pitchOffset = Mathf.Clamp(pitchOffset - look.y * 1.0f, -10f, 60f);
        if (Time.time - lastMouse > 1.2f) { yawOffset = Mathf.MoveTowards(yawOffset, 0f, Time.deltaTime * 90f); pitchOffset = Mathf.MoveTowards(pitchOffset, 14f, Time.deltaTime * 30f); }
        var t = car.transform;
        var flatFwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (flatFwd.sqrMagnitude < 0.01f) flatFwd = Vector3.forward;
        var rot = Quaternion.LookRotation(flatFwd.normalized, Vector3.up) * Quaternion.Euler(pitchOffset, yawOffset, 0f);
        var pivot = t.position + Vector3.up * 0.32f;
        const float distance = 1.15f;
        var dir = rot * Vector3.back;
        float d = distance;
        if (Physics.SphereCast(pivot, 0.08f, dir, out var hit, distance, Remote.Ground, QueryTriggerInteraction.Ignore)) d = Mathf.Max(0.2f, hit.distance - 0.05f);
        var target = pivot + dir * d;
        smoothed = hasSmoothed ? Vector3.Lerp(smoothed, target, 1f - Mathf.Exp(-Time.deltaTime * 14f)) : target;
        hasSmoothed = true;
        pose = smoothed;
        poseRot = Quaternion.LookRotation(pivot + flatFwd.normalized * 0.4f - smoothed, Vector3.up);
        poseValid = true;
    }

    static Camera? Main()
    {
        try { return CameraManager.Instance?.Camera; } catch { return null; }
    }

    static void OnPreCull(Camera cam)
    {
        try
        {
            if (!poseValid || moved || cam == null) return;
            if (cam != Main()) return;
            var tr = cam.transform;
            savedLocalPos = tr.localPosition; savedLocalRot = tr.localRotation;
            tr.SetPositionAndRotation(pose, poseRot);
            moved = true; movedCam = cam; FramesMoved++;
        }
        catch (Exception e) { Plugin.ErrorOnce("camera move", e); poseValid = false; }
    }

    static void OnPostRender(Camera cam)
    {
        if (!moved || cam != movedCam) return;
        try { cam.transform.localPosition = savedLocalPos; cam.transform.localRotation = savedLocalRot; }
        catch (Exception e) { Plugin.ErrorOnce("camera restore", e); }
        moved = false; movedCam = null;
    }

    /// <summary>From the runner's Update: if a render never reported back, put the camera back anyway.</summary>
    public static void SafetyRestore()
    {
        if (!moved || movedCam == null) { moved = false; return; }
        try { movedCam.transform.localPosition = savedLocalPos; movedCam.transform.localRotation = savedLocalRot; } catch { }
        moved = false; movedCam = null;
        MissedRestores++;
        if (MissedRestores == 1) Plugin.Warn("the camera was put back without a render callback");
    }

    public static int MissedRestores;

    public static bool PoseValid => poseValid;
    public static Vector3 Pose => pose;
    public static Quaternion PoseRotation => poseRot;
}
