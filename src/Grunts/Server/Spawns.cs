using System.Collections.Concurrent;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.InRaid;

namespace Grunts.Server;

/// <summary>
/// Grunts come into a raid only while one of the Jackal's Grunt jobs for that map is running (config spawnsNeedJob), and for a Labs job only
/// when the character is wearing the Avon mask as the raid starts (Widowz: "if you aren't wearing it the enemies won't spawn"). The raid's
/// location is generated per raid as a clone of the map (LocationLifecycleService.GenerateLocationAndLoot), so the Grunts are added to that
/// clone's BossLocationSpawn list, one entry per Grunt (no boss/follower groups: the infected brain has no boss logic). Runs at PostLoad + 32.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 32)]
public class Spawns(ISptLogger<Spawns> logger, ModHelper modHelper, ProfileHelper profileHelper) : IOnLoad
{
    internal static ISptLogger<Spawns>? Log;
    internal static ProfileHelper? Profiles;
    internal static ModConfig Config = new();
    internal static readonly ConcurrentDictionary<string, string> RaidSide = new();
    private static bool patched;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Config = GruntBot.LoadConfig(modHelper);
            if (!Config.Enabled) return Task.CompletedTask;
            Log = logger; Profiles = profileHelper;
            if (!patched)
            {
                new RaidStartPatch().Enable();
                new LocationPatch().Enable();
                patched = true;
            }
            logger.Info($"[{ModInfo.Name}] spawns: {(Config.SpawnsNeedJob ? "only while a Grunt job runs" : "every raid on the job maps")}" +
                        $"{(Config.PmcRaidsOnly ? ", PMC raids only" : "")}" +
                        $"{(Config.FreeRoamChance > 0 ? $", {Config.FreeRoamChance}% chance per raid of roaming Grunts on {string.Join("/", Config.FreeRoamMaps)}{(string.IsNullOrWhiteSpace(Config.FreeRoamAfter) ? "" : $" once \"{Config.FreeRoamAfter}\" is done")}, no mask needed" : "")}" +
                        $"; {Quests.Jobs.Count} job(s) known");
        }
        catch (Exception ex) { logger.Error($"[{ModInfo.Name}] spawns not set up: {ex}"); }
        return Task.CompletedTask;
    }

    /// <summary>Called for every generated raid location. Never throws (a failure only means no Grunts).</summary>
    internal static void AddGrunts(MongoId sessionId, string mapName, LocationBase? location)
    {
        try
        {
            if (location is null || Profiles is null || string.IsNullOrEmpty(mapName)) return;
            var map = mapName.ToLowerInvariant();
            RaidSide.TryRemove(sessionId.ToString(), out var side);
            var isScav = string.Equals(side, "Savage", StringComparison.OrdinalIgnoreCase);
            var pmc = Profiles.GetPmcProfile(sessionId);
            var waves = new List<(WaveDef Wave, string Why)>();
            var blocked = new List<string>();
            // job waves: PMC raids only (config), Labs jobs need the mask worn
            if (pmc?.Quests is not null && !(Config.PmcRaidsOnly && isScav))
                foreach (var job in Quests.Jobs)
                {
                    if (!job.Maps.Contains(map)) continue;
                    var status = pmc.Quests.FirstOrDefault(q => q.QId.ToString() == job.QuestId)?.Status;
                    if (status != QuestStatusEnum.Started) continue;
                    if (job.RequireWorn.Count > 0 && !Wears(pmc, job.RequireWorn)) { blocked.Add(job.Title); continue; }
                    waves.AddRange(job.Waves.Select(w => (w, job.Title)));
                }
            if (!Config.SpawnsNeedJob && !(Config.PmcRaidsOnly && isScav) && waves.Count == 0 && blocked.Count == 0 && Quests.Jobs.Any(j => j.Maps.Contains(map)))
                waves.AddRange(Quests.Jobs.First(j => j.Maps.Contains(map)).Waves.Select(w => (w, "no job needed (config)")));
            // rare roaming Grunts once the questline is done (freeRoamAfter): any raid on freeRoamMaps, no mask needed
            if (waves.Count == 0 && blocked.Count == 0 && RoamUnlocked(pmc) && (!isScav || Config.FreeRoamScavRaids)
                && Config.FreeRoamMaps.Contains(map, StringComparer.OrdinalIgnoreCase))
            {
                var roll = Random.Shared.NextDouble() * 100;
                if (roll < Config.FreeRoamChance) waves.AddRange(Config.FreeRoamWaves.Select(w => (w, "roaming")));
                else Log?.Debug($"[{ModInfo.Name}] {map}: no roaming Grunts this raid (rolled {roll:0.0}, needs under {Config.FreeRoamChance})");
            }
            foreach (var title in blocked)
                Log?.Info($"[{ModInfo.Name}] {map}: \"{title}\" is running but the mask is not worn, so no Grunts this raid");
            if (waves.Count == 0) return;

            var zone = Config.Zones.TryGetValue(map, out var z) ? z : "";
            location.BossLocationSpawn ??= [];
            int added = 0;
            foreach (var (wave, _) in waves)
                for (var i = 0; i < Math.Max(0, wave.Count); i++)
                {
                    location.BossLocationSpawn.Add(new BossLocationSpawn
                    {
                        BossName = Config.GruntRole, BossChance = 100, BossDifficulty = Config.Difficulty, BossZone = zone,
                        BossEscortType = Config.GruntRole, BossEscortAmount = "0", BossEscortDifficulty = Config.Difficulty,
                        IsBossPlayer = false, IsRandomTimeSpawn = false, ShowOnTarkovMap = false, ShowOnTarkovMapPvE = false,
                        Time = wave.Time, TriggerId = "", TriggerName = "", IgnoreMaxBots = true, ForceSpawn = false,
                        SpawnMode = ["regular", "pve"], SptId = Ids.Hash($"grunts|wave|{map}|{added}").ToString(),
                    });
                    added++;
                }
            Log?.Success($"[{ModInfo.Name}] {map}: {added} Grunt(s) coming ({string.Join(", ", waves.Select(w => w.Why).Distinct())}; waves at {string.Join("/", waves.Select(w => w.Wave.Time < 0 ? "start" : $"{w.Wave.Time:0}s").Distinct())})");
        }
        catch (Exception ex) { Log?.Warning($"[{ModInfo.Name}] no Grunts this raid, spawn setup failed: {ex.Message}"); }
    }

    /// <summary>Roaming is on (chance above 0) and, when freeRoamAfter names a job, that job is finished on this profile.</summary>
    internal static bool RoamUnlocked(SPTarkov.Server.Core.Models.Eft.Common.PmcData? pmc)
    {
        if (Config.FreeRoamChance <= 0) return false;
        if (string.IsNullOrWhiteSpace(Config.FreeRoamAfter)) return true;
        var id = Ids.Quest(Config.FreeRoamAfter);
        return pmc?.Quests?.Any(q => q.QId.ToString() == id && q.Status == QuestStatusEnum.Success) == true;
    }

    /// <summary>One of the templates sits directly in an equipment slot (worn), not inside a bag, rig or pocket.</summary>
    private static bool Wears(SPTarkov.Server.Core.Models.Eft.Common.PmcData pmc, List<string> templates)
    {
        var inv = pmc.Inventory;
        if (inv?.Items is null) return false;
        var equipment = inv.Equipment.ToString();
        return inv.Items.Any(i => i.ParentId == equipment && templates.Contains(i.Template.ToString()));
    }
}

/// <summary>Remembers which side (Pmc or Savage) each session is starting a raid as, for the location patch that runs inside the same call.</summary>
public class RaidStartPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() => typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.StartLocalRaidAsync));

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, StartLocalRaidRequestData request)
    {
        try { Spawns.RaidSide[sessionId.ToString()] = request?.PlayerSide ?? ""; } catch { /* never block a raid */ }
    }
}

public class LocationPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() => typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.GenerateLocationAndLoot));

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, string name, LocationBase __result) => Spawns.AddGrunts(sessionId, name, __result);
}
