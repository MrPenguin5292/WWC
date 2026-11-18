using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using Systems.Effects;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>
/// The car in the world. It moves itself (kinematic rigidbody; a runtime dynamic rigidbody never moved in raid): speed along its nose, yaw
/// from steering, a sphere cast against walls and people, four wheel rays for height and tilt, gravity off ledges. A separate hitbox on the
/// bullet layer carries a BallisticCollider, so rounds that hit it count; enough hits set it off. The blast is the game's grenade explosion (Grenade.Explosion with this car as the explosive), so damage, kill credit,
/// broken windows and the bots' reaction all come from the game.
/// </summary>
public sealed class RCCar : MonoBehaviour
{
    public Player Owner = null!;
    public Item Kit = null!;
    public bool Alive { get; private set; }
    public Collider? HitCollider { get; private set; }
    public float Speed { get; private set; }
    public bool SignalLost { get; private set; }
    public float Distance { get; private set; }
    public int HitsTaken { get; private set; }
    public int GroundedWheels { get; private set; }
    public bool Grounded => grounded;
    public float SignedSpeed => v;
    CarAudio? sound;
    public float SpawnTime { get; private set; }
    public float SpawnHeight { get; private set; }

    Rigidbody rb = null!;
    BoxCollider body = null!;
    GameObject hitbox = null!;
    readonly Transform[] wheels = new Transform[4];
    readonly Vector3[] wheelMount = new Vector3[4];
    readonly float[] wheelDrop = new float[4];
    readonly Vector3[] wheelHits = new Vector3[4];
    float wheelSpin, steerVisual, nextNoise, ledClock;
    Light? led;
    AudioSource? motor3d, motorFeed;
    static AudioClip? motorClip;
    static float[]? motorSamples;

    const float Mass = 4f, WheelRadius = 0.055f;
    const float Accel = 12f, Brake = 20f;   // m/s²

    public Vector3 AimPoint => transform.position + transform.up * 0.12f;

    public void Init(Player owner, Item kit)
    {
        Owner = owner; Kit = kit; Alive = true; SpawnTime = Time.time;
        var layer = BodyLayer;
        var model = CarModel.Build(transform, wheels);
        for (int i = 0; i < 4; i++) { wheelMount[i] = wheels[i].localPosition; wheelDrop[i] = 0f; }
        foreach (var r in model.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = true; }

        gameObject.layer = layer;
        body = gameObject.AddComponent<BoxCollider>();
        body.center = CarModel.Hilux ? new Vector3(0f, 0.135f, 0f) : new Vector3(0f, 0.125f, 0f);
        body.size = CarModel.Hilux ? new Vector3(0.24f, 0.13f, 0.47f) : new Vector3(0.25f, 0.10f, 0.44f);
        body.material = new PhysicMaterial("rc_car") { dynamicFriction = 0.25f, staticFriction = 0.3f, bounciness = 0.1f, frictionCombine = PhysicMaterialCombine.Minimum, bounceCombine = PhysicMaterialCombine.Minimum };
        rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = Mass;
        rb.isKinematic = true;   // the car moves itself (see Drive); kinematic so other physics objects still bump into it
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        SpawnHeight = transform.position.y;

        // the hitbox for bullets lives on its own object (not part of the rigidbody), on the layer the game's bullets hit
        hitbox = new GameObject("RCBombCarHitbox");
        hitbox.SetActive(false);
        var hitLayer = LayerMask.NameToLayer("HighPolyCollider");
        hitbox.layer = hitLayer >= 0 ? hitLayer : 0;
        var box = hitbox.AddComponent<BoxCollider>();
        box.size = CarModel.Hilux ? new Vector3(0.27f, 0.23f, 0.48f) : new Vector3(0.27f, 0.20f, 0.46f);
        box.center = new Vector3(0f, CarModel.Hilux ? 0.125f : 0.12f, 0f);
        var ballistic = hitbox.AddComponent<BallisticCollider>();
        ballistic.TypeOfMaterial = MaterialType.Plastic;
        ballistic.PenetrationChance = 1f;
        ballistic.PenetrationLevel = 10f;
        ballistic.OnHitAction += OnShot;
        HitCollider = box;
        Physics.IgnoreCollision(body, box);
        hitbox.transform.SetPositionAndRotation(transform.position, transform.rotation);
        hitbox.SetActive(true);

        led = new GameObject("rc_led").AddComponent<Light>();
        led.transform.SetParent(transform, false);
        led.transform.localPosition = CarModel.Hilux ? new Vector3(-0.040f, 0.122f, -0.068f) : new Vector3(0.012f, 0.23f, -0.12f);
        led.type = LightType.Point; led.color = new Color(1f, 0.08f, 0.05f); led.range = 0.18f; led.intensity = 0f; led.shadows = LightShadows.None;
        // headlights: rooms in Tarkov are dark and the feed is all you see
        var head = new GameObject("rc_headlights").AddComponent<Light>();
        head.transform.SetParent(transform, false);
        head.transform.localPosition = CarModel.Hilux ? new Vector3(0f, 0.12f, 0.24f) : new Vector3(0f, 0.13f, 0.21f);
        head.transform.localRotation = Quaternion.Euler(6f, 0f, 0f);
        head.type = LightType.Spot; head.spotAngle = 75f; head.range = 14f; head.intensity = 1.6f; head.color = new Color(1f, 0.95f, 0.85f); head.shadows = LightShadows.None;

        // 0.3.0: the real-life sound set (sounds folder); the old code-made motor only if those files are missing
        try { sound = CarAudio.TryAdd(this); } catch (Exception e) { Plugin.ErrorOnce("car sound setup", e); }
        if (sound == null) MakeMotorSound();
        Plugin.Note($"car built (body layer {LayerMask.LayerToName(layer)}, hitbox layer {LayerMask.LayerToName(hitbox.layer)}, shader {CarModel.ShaderUsed})");
    }

    static int bodyLayer = -1;
    public static int BodyLayer => bodyLayer >= 0 ? bodyLayer : (bodyLayer = PickBodyLayer());

    static int PickBodyLayer()
    {
        int lowPoly = LayerMask.NameToLayer("LowPolyCollider");
        foreach (var name in new[] { "Loot", "Interactive", "Default" })
        {
            int l = LayerMask.NameToLayer(name);
            if (l >= 0 && (lowPoly < 0 || !Physics.GetIgnoreLayerCollision(l, lowPoly))) return l;
        }
        return 0;
    }

    // ------------------------------------------------------------ sound

    void MakeMotorSound()
    {
        try
        {
            // a small brushed motor: a buzzy saw wave with gear whine, made here so the mod needs no audio files.
            // The game's audio reset can empty code-made clips, so the samples are kept and the clip remade for every car.
            if (motorSamples == null)
            {
                const int rate = 44100; var s = new float[rate];
                var rnd = new System.Random(7);
                for (int i = 0; i < s.Length; i++)
                {
                    float t = i / (float)rate;
                    float saw = 2f * ((t * 120f) % 1f) - 1f;
                    float whine = Mathf.Sin(t * 2f * Mathf.PI * 960f) * 0.25f + Mathf.Sin(t * 2f * Mathf.PI * 1440f) * 0.12f;
                    float buzz = Mathf.Sign(Mathf.Sin(t * 2f * Mathf.PI * 240f)) * 0.15f;
                    float hiss = ((float)rnd.NextDouble() * 2f - 1f) * 0.08f;
                    s[i] = (saw * 0.35f + whine + buzz + hiss) * 0.6f;
                }
                motorSamples = s;
            }
            if (motorClip == null || motorClip.samples != motorSamples.Length) { motorClip = AudioClip.Create("rc_motor", motorSamples.Length, 1, 44100, false); }
            motorClip.SetData(motorSamples, 0);
            motor3d = gameObject.AddComponent<AudioSource>();
            motor3d.clip = motorClip; motor3d.loop = true; motor3d.spatialBlend = 1f; motor3d.rolloffMode = AudioRolloffMode.Linear;
            motor3d.minDistance = 1f; motor3d.maxDistance = Mathf.Max(10f, Plugin.Settings.noiseRange * 1.2f); motor3d.volume = 0f; motor3d.dopplerLevel = 0f;
            try
            {
                // through the game's own mixer, so the sound volume setting applies (the feed copy too)
                var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (audio != null) { motor3d.outputAudioMixerGroup = audio.SimpleOccludedMixerGroup ?? audio.MasterMixerGroup; }
            }
            catch (Exception e) { Plugin.ErrorOnce("motor mixer", e); }
            motor3d.Play();
            motorFeed = gameObject.AddComponent<AudioSource>();
            motorFeed.clip = motorClip; motorFeed.loop = true; motorFeed.spatialBlend = 0f; motorFeed.volume = 0f;
            try { var audio = MonoBehaviourSingleton<BetterAudio>.Instance; if (audio != null) motorFeed.outputAudioMixerGroup = audio.MasterMixerGroup; } catch { }
            motorFeed.Play();
        }
        catch (Exception e) { Plugin.ErrorOnce("motor sound", e); }
    }

    // ------------------------------------------------------------ driving (own ground-following movement)
    // Run 2 (2026-11-17): a dynamic rigidbody made at runtime never moved in raid (velocity stayed 0 with forces applied every step), so
    // the car moves itself: speed along its nose, yaw from steering, a sphere cast against walls (and people), four wheel rays for the
    // ground height and tilt, and simple gravity off ledges. The rigidbody is kinematic, so loot and grenades still bump into it.

    float v, vy;
    bool grounded = true;
    Vector3 smoothUp = Vector3.up;
    public static int DriveFrames;
    float nextDiag;
    const float StepUp = 0.12f, Gravity = 9.81f, MaxYawDeg = 170f, CarRadius = 0.11f;
    static int wallMask;

    void Update()
    {
        if (!Alive) return;
        try { Drive(Mathf.Min(Time.deltaTime, 0.05f)); }
        catch (Exception e) { Plugin.ErrorOnce("drive", e); }
    }

    void Drive(float dt)
    {
        if (dt <= 0f) return;
        if (wallMask == 0) wallMask = Remote.Ground | LayerMask.GetMask("Player");
        Distance = Owner != null ? Vector3.Distance(Owner.Position, transform.position) : 0f;
        SignalLost = Distance > Plugin.Settings.signalRange;
        var input = SignalLost ? Vector2.zero : Remote.DriveInput;
        float top = Remote.BoostInput ? Plugin.Settings.boostSpeed : Plugin.Settings.topSpeed;
        float throttle = Mathf.Clamp(input.y, -1f, 1f), steer = Mathf.Clamp(input.x, -1f, 1f);
        float target = throttle * (throttle < 0f ? top * 0.5f : top);
        if (grounded)
        {
            if (Mathf.Abs(throttle) < 0.01f) v = Mathf.MoveTowards(v, 0f, 5f * dt);
            else v = Mathf.MoveTowards(v, target, (Mathf.Sign(target) != Mathf.Sign(v) && Mathf.Abs(v) > 0.2f ? Brake : Accel) * dt);
        }
        // heading from the transform each frame (so anything that turns the car, like the QA probe, is respected)
        var flat = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (flat.sqrMagnitude < 0.0001f) flat = Vector3.ProjectOnPlane(-transform.up, Vector3.up);
        float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        if (grounded) yaw += steer * MaxYawDeg * Mathf.Clamp01(Mathf.Abs(v) / 1.5f) * Mathf.Sign(v) * dt;
        var fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        var pos = transform.position;

        // walls: slide along them, lose speed on a head-on hit
        var move = Vector3.ProjectOnPlane(fwd, grounded ? smoothUp : Vector3.up).normalized * v * dt;
        if (move.sqrMagnitude > 1e-8f)
        {
            var dir = move.normalized; float len = move.magnitude;
            if (Physics.SphereCast(pos + Vector3.up * (CarRadius + 0.03f), CarRadius, dir, out var wall, len + 0.03f, wallMask, QueryTriggerInteraction.Ignore))
            {
                float free = Mathf.Max(0f, wall.distance - 0.03f);
                var rest = move - dir * free;
                var slide = Vector3.ProjectOnPlane(rest, wall.normal); slide.y = 0f;
                move = dir * free + slide * 0.6f;
                float headOn = Vector3.Dot(wall.normal, -dir);
                sound?.WallHit(Mathf.Abs(v) * Mathf.Clamp01(headOn));
                v *= headOn > 0.7f ? 0.15f : 0.85f;
            }
        }
        var next = pos + move;
        // ground: walkable if no higher than a small step above the car
        float gy = float.NaN; Vector3 gNormal = Vector3.up;
        if (Physics.Raycast(next + Vector3.up * (StepUp + 0.05f), Vector3.down, out var floor, StepUp + 0.05f + 2f, Remote.Ground, QueryTriggerInteraction.Ignore))
        {
            if (floor.point.y <= pos.y + StepUp) { gy = floor.point.y; gNormal = floor.normal; if (grounded && gy - pos.y > 0.02f) sound?.SteppedUp(gy - pos.y); }
            else { sound?.WallHit(Mathf.Abs(v)); next = pos; v = 0f; if (Physics.Raycast(pos + Vector3.up * (StepUp + 0.05f), Vector3.down, out var here, StepUp + 2.05f, Remote.Ground, QueryTriggerInteraction.Ignore)) { gy = here.point.y; gNormal = here.normal; } }
        }
        bool wasGrounded = grounded; float fall = -vy;
        if (!float.IsNaN(gy) && next.y <= gy + 0.03f) { next.y = gy; vy = 0f; grounded = true; }
        else
        {
            vy -= Gravity * dt; next.y += vy * dt; grounded = false;
            if (!float.IsNaN(gy) && next.y <= gy) { fall = -vy; next.y = gy; vy = 0f; grounded = true; }
        }
        if (grounded && !wasGrounded) sound?.Landed(fall);
        // tilt from the four wheels
        var up = grounded ? WheelPlane(next, Quaternion.LookRotation(fwd, Vector3.up), gNormal) : Vector3.up;
        smoothUp = Vector3.Slerp(smoothUp, up, 1f - Mathf.Exp(-14f * dt));
        var rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(fwd, smoothUp).normalized, smoothUp);
        transform.SetPositionAndRotation(next, rot);
        if (Time.time - SpawnTime > 1f && next.y < SpawnHeight - 60f) { Plugin.Warn("the car fell out of the map"); Remove(); return; }
        Speed = Mathf.Abs(v);
        GroundedWheels = grounded ? 4 : 0;
        steerVisual = Mathf.MoveTowards(steerVisual, steer * 28f, dt * 160f);
        wheelSpin += v / WheelRadius * Mathf.Rad2Deg * dt;
        DriveFrames++;
        if (Plugin.Verbose && Time.time >= nextDiag && Mathf.Abs(throttle) > 0.01f) { nextDiag = Time.time + 1f; Plugin.Note($"driving: input {input} v {v:F2} m/s grounded {grounded} at {next}"); }
    }

    /// <summary>The ground plane under the four wheels (falls back to the floor normal under the middle); also sets each wheel's drop.</summary>
    Vector3 WheelPlane(Vector3 at, Quaternion heading, Vector3 fallback)
    {
        var p = wheelHits; int hits = 0;
        for (int i = 0; i < 4; i++)
        {
            var origin = at + heading * new Vector3(wheelMount[i].x, 0f, wheelMount[i].z) + Vector3.up * (StepUp + 0.05f);
            if (Physics.Raycast(origin, Vector3.down, out var h, StepUp + 0.4f, Remote.Ground, QueryTriggerInteraction.Ignore))
            {
                p[i] = h.point; hits++;
                wheelDrop[i] = Mathf.Clamp(h.point.y - at.y, -0.03f, 0.04f) * -1f;
            }
            else { p[i] = at + heading * new Vector3(wheelMount[i].x, 0f, wheelMount[i].z) + Vector3.down * 0.05f; wheelDrop[i] = 0.04f; }
        }
        if (hits < 3) return fallback;
        var front = (p[0] + p[1]) * 0.5f; var back = (p[2] + p[3]) * 0.5f; var left = (p[0] + p[2]) * 0.5f; var right = (p[1] + p[3]) * 0.5f;
        var n = Vector3.Cross(front - back, right - left).normalized;
        return Vector3.Dot(n, Vector3.up) > 0.5f ? n : fallback;
    }

    void LateUpdate()
    {
        if (!Alive) return;
        try
        {
            for (int i = 0; i < 4; i++)
            {
                if (wheels[i] == null) continue;
                wheels[i].localPosition = wheelMount[i] + new Vector3(0f, -wheelDrop[i], 0f);
                float yaw = i < 2 ? steerVisual : 0f;   // 0,1 = front
                wheels[i].localRotation = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(wheelSpin, 0f, 0f);
            }
            if (hitbox != null) hitbox.transform.SetPositionAndRotation(transform.position, transform.rotation);
            ledClock += Time.deltaTime;
            if (led != null) led.intensity = (ledClock % 0.8f) < 0.12f ? 0.8f : 0f;
            float load = Mathf.Clamp01(Speed / Mathf.Max(1f, Plugin.Settings.topSpeed));
            float throttle = Mathf.Abs(Remote.DriveInput.y);
            if (motor3d != null) { motor3d.pitch = 0.55f + load * 1.1f + throttle * 0.15f; motor3d.volume = 0.12f + load * 0.55f + throttle * 0.15f; }
            if (motorFeed != null) { motorFeed.pitch = 0.55f + load * 1.1f + throttle * 0.15f; motorFeed.volume = Remote.Driving ? 0.10f + load * 0.25f : 0f; }
            if (Time.time >= nextNoise) { nextNoise = Time.time + 0.5f; MakeNoise(load); }
        }
        catch (Exception e) { Plugin.ErrorOnce("car update", e); }
    }

    /// <summary>The motor is loud: bots in earshot hear "you" where the car is and come to look.</summary>
    void MakeNoise(float load)
    {
        if (Plugin.Settings.noiseRange <= 0f || Speed < 0.4f || Owner == null) return;
        if (!Singleton<GlobalEventDispatcher>.Instantiated) return;
        float power = Plugin.Settings.noiseRange * Mathf.Lerp(0.6f, 1f, load);
        Singleton<GlobalEventDispatcher>.Instance.PlaySound(Owner, AimPoint, power, AISoundType.step);
        Remote.NoiseEvents++;
    }

    // ------------------------------------------------------------ getting shot

    void OnShot(DamageInfo info)
    {
        if (!Alive) return;
        HitsTaken++; Remote.Hits++;
        string who = "?";
        try { who = info.Player?.Nickname ?? "?"; } catch { }
        Plugin.Note($"car hit by {who} ({HitsTaken}/{Plugin.Settings.hitsToDestroy})");
        if (Plugin.Settings.hitsToDestroy > 0 && HitsTaken >= Plugin.Settings.hitsToDestroy) Detonate("shot by " + who);
    }

    // ------------------------------------------------------------ the blast

    public void Detonate(string why)
    {
        if (!Alive) return;
        Alive = false;
        var pos = transform.position + Vector3.up * 0.15f;
        Remote.Detonations++; Remote.LastDetonation = $"{why} at {pos}";
        Plugin.Note($"BOOM ({why}) at {pos}, {Distance:F1} m from you");
        var effects = Singleton<Effects>.Instantiated ? Singleton<Effects>.Instance : null;
        try
        {
            if (effects != null)
            {
                var effect = Plugin.Settings.blast.effect;
                if (!EffectExists(effects, effect)) { Plugin.Warn($"explosion effect '{effect}' not found, using Grenade_new"); effect = "Grenade_new"; }
                effects.EmitGrenade(effect, pos, Vector3.up, 1f);
                // the mortar effect may carry no sound of its own (the game plays shelling sounds elsewhere): add a grenade bang if so
                if (!EffectHasSound(effects, effect))
                    foreach (var loud in new[] { "big_explosion", "Grenade_new", "Grenade_new2" })
                        if (loud != effect && EffectHasSound(effects, loud)) { effects.EmitGrenade(loud, pos, Vector3.up, 1f); SoundFrom = loud; break; }
            }
        }
        catch (Exception e) { Plugin.ErrorOnce("explosion effect", e); }
        try
        {
            var gw = Singleton<GameWorld>.Instance;
            var explosive = new CarExplosive(Plugin.Settings.blast);
            Grenade.Explosion(null, explosive, pos, Owner.ProfileId, gw.SharedBallisticsCalculator, Kit, Vector3.zero, false);
        }
        catch (Exception e) { Plugin.ErrorOnce("explosion", e); }
        Remote.OnCarGone(this, exploded: true);
        Remove();
    }

    public static string SoundFrom = "";
    static System.Reflection.FieldInfo? soundField;

    /// <summary>True when the named effect has its own explosion sound (Effects.Effect.Sound).</summary>
    public static bool EffectHasSound(Effects effects, string name)
    {
        try
        {
            namesField ??= HarmonyLib.AccessTools.Field(typeof(Effects), "_names");
            if (!(namesField?.GetValue(effects) is System.Collections.IDictionary d) || !d.Contains(name)) return false;
            var effect = d[name];
            soundField ??= HarmonyLib.AccessTools.Field(effect.GetType(), "Sound");
            return soundField?.GetValue(effect) is UnityEngine.Object o && o != null;
        }
        catch { return false; }
    }

    static System.Reflection.FieldInfo? namesField;
    static bool EffectExists(Effects effects, string name)
    {
        try
        {
            namesField ??= HarmonyLib.AccessTools.Field(typeof(Effects), "_names");
            return namesField?.GetValue(effects) is System.Collections.IDictionary d ? d.Contains(name) : true;
        }
        catch { return true; }
    }

    public void Remove()
    {
        Alive = false;
        if (Remote.Car == this) Remote.OnCarGone(this, exploded: false);
        if (hitbox != null) Destroy(hitbox);
        if (this != null && gameObject != null) Destroy(gameObject);
    }

    void OnDestroy()
    {
        if (hitbox != null) Destroy(hitbox);
        if (Remote.Car == this) Remote.OnCarGone(this, exploded: false);
    }
}
