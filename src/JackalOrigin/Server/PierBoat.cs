using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.InRaid;

namespace JackalOrigin.Server;

/// <summary>Part 2 asks for an extract through Pier Boat, which the Shoreline database opens in about half of raids (Chance 50; the client rolls
/// <c>Random.Range(0, 100) &lt;= Chance</c> per exit when the raid loads). While the character has Part 2 running, this sets that one exit to Chance 100
/// in the raid's own copy of the location: <see cref="LocationLifecycleService.GenerateLocationAndLoot"/> returns a clone of the database base, and the
/// exit is swapped for a changed copy in a new list, so the database and every other raid keep the normal chance. PMC raids only; any error leaves the
/// raid as SPT built it.</summary>
internal static class PierBoat
{
    public const string ExitName = "Pier Boat";
    private const string Shoreline = "shoreline";
    private static ProfileHelper? profiles;
    private static LocationTable? locations;
    private static ISptLogger<OriginLoader>? log;
    private static MongoId part2;
    private static int failures, warnedMissing, warnedShared;

    /// <summary>The side of the raid being started, set by <see cref="RaidSidePatch"/> for the same request. Null when the location is built some other way.</summary>
    internal static readonly AsyncLocal<string?> RaidSide = new();

    /// <summary>The database's own chance for the exit, for the startup log.</summary>
    public static double? DatabaseChance(LocationTable table)
    {
        try { return table.GetLocation(Shoreline)?.Base?.Exits?.FirstOrDefault(e => e?.Name == ExitName)?.Chance; }
        catch { return null; }
    }

    public static bool Enable(ProfileHelper profileHelper, LocationTable locationTable, ISptLogger<OriginLoader> logger, MongoId part2Id)
    {
        profiles = profileHelper; locations = locationTable; log = logger; part2 = part2Id;
        var location = new PierBoatPatch();
        location.Enable();
        if (!location.IsActive) return false;
        // The side check is a refinement; without it every Shoreline raid of a character on Part 2 gets the open exit.
        try { var side = new RaidSidePatch(); side.Enable(); if (!side.IsActive) logger.Warning("[JackalOrigin] raid side check not active; Scav raids of a character on Part 2 also get the open Pier Boat (it is a PMC exit, so nothing changes for the Scav)"); }
        catch (Exception ex) { logger.Warning($"[JackalOrigin] raid side check not active ({ex.Message}); Scav raids of a character on Part 2 also get the open Pier Boat (it is a PMC exit, so nothing changes for the Scav)"); }
        return true;
    }

    internal static void Apply(MongoId sessionId, string? name, LocationBase? location)
    {
        try
        {
            if (location?.Exits is null || profiles is null) return;
            if (!string.Equals(name, Shoreline, StringComparison.OrdinalIgnoreCase) && !string.Equals(location.Id, Shoreline, StringComparison.OrdinalIgnoreCase)) return;
            var side = RaidSide.Value;
            if (side is not null && !string.Equals(side, "pmc", StringComparison.OrdinalIgnoreCase)) return;
            var status = profiles.GetPmcProfile(sessionId)?.Quests?.FirstOrDefault(q => q.QId == part2)?.Status;
            if (status != QuestStatusEnum.Started) return;
            // Never the database base itself, even if another mod handed it out instead of the clone.
            if (ReferenceEquals(locations?.GetLocation(name ?? Shoreline)?.Base, location))
            {
                if (Interlocked.Exchange(ref warnedShared, 1) == 0) log?.Warning("[JackalOrigin] the raid's Shoreline data is the database copy itself; Pier Boat left at its normal chance");
                return;
            }
            var exits = location.Exits.ToList();
            var i = exits.FindIndex(e => e?.Name == ExitName);
            if (i < 0)
            {
                if (Interlocked.Exchange(ref warnedMissing, 1) == 0) log?.Warning("[JackalOrigin] Shoreline has no exit named Pier Boat in this raid; nothing forced");
                return;
            }
            var was = exits[i];
            if (was.Chance >= 100 && (was.ChancePVE is null || was.ChancePVE >= 100)) return;
            exits[i] = was with { Chance = 100, ChancePVE = 100 };
            location.Exits = exits;
            log?.Info($"[JackalOrigin] Part 2 active: Pier Boat forced open for this raid (was chance {was.Chance?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unset"})");
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref failures) == 1) log?.Warning($"[JackalOrigin] Pier Boat check failed; the raid keeps its normal exits ({ex.GetType().Name}: {ex.Message})");
        }
    }
}

/// <summary>Postfix on the method that clones the database location for one raid. Runs last, so it sees the copy after other mods' postfixes.</summary>
internal sealed class PierBoatPatch() : AbstractPatch("local.jackalorigin.pierboat")
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.GenerateLocationAndLoot), [typeof(MongoId), typeof(string), typeof(bool)]);

    [PatchPostfix, HarmonyPriority(Priority.Last)]
    public static void Postfix(MongoId sessionId, string name, LocationBase __result) => PierBoat.Apply(sessionId, name, __result);
}

/// <summary>Reads the side (pmc or savage) of the raid being started. Only reads the request; never skips or changes anything.</summary>
internal sealed class RaidSidePatch() : AbstractPatch("local.jackalorigin.pierboat.side")
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaidAsync), [typeof(MongoId), typeof(StartLocalRaidRequestData), typeof(CancellationToken)]);

    [PatchPrefix]
    public static void Prefix(StartLocalRaidRequestData request)
    {
        try { PierBoat.RaidSide.Value = request?.PlayerSide; } catch { }
    }
}
