using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Json;

namespace JackalOrigin.Server;

#if !WWC   // built into WWC, the mod's metadata is WWC's own (mods/WWC/Server/Wwc.cs)
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "local.jackalorigin.server";
    public string Name { get; init; } = "Jackal Origin";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new() { ["local.jackal"] = new("^1.0.0") };
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}
#endif

/// <summary>Ids come from fixed text, so they never change once a profile holds them.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jackalorigin|" + key))).ToLowerInvariant()[..24]);
    public const string Jackal = "672b4a010000000000000001";
    public const string Fence = "579dc571d53a0658a154fbec";
    /// <summary>Route of the square portrait. A new address means the game cannot reuse an old cached copy of the wide one.</summary>
    public const string PortraitRoute = "/files/trader/avatar/jackal_origin_portrait_v1";
}

public record PartConfig
{
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("xp")] public int Xp { get; set; }
    [JsonPropertyName("roubles")] public int Roubles { get; set; }
    [JsonPropertyName("standing")] public double Standing { get; set; }
    [JsonPropertyName("kills")] public int Kills { get; set; }
    [JsonPropertyName("bitcoinReward")] public bool BitcoinReward { get; set; } = true;
}

public record Part2Config : PartConfig
{
    /// <summary>While Part 2 is running, Pier Boat is open in every Shoreline raid of that character instead of about half of them.
    /// Defaults to on when the key is missing, because an update keeps the config.json already installed.</summary>
    [JsonPropertyName("pierBoatAlwaysOpenDuringQuest")] public bool PierBoatAlwaysOpenDuringQuest { get; set; } = true;
}

public record OriginConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>New characters start with the Jackal locked; finishing Part 3 unlocks him. Characters that already have him keep him.</summary>
    [JsonPropertyName("lockJackalUntilTrusted")] public bool LockJackalUntilTrusted { get; set; } = true;
    /// <summary>His first network job, First Contact, waits for Part 3.</summary>
    [JsonPropertyName("firstContactNeedsOrigin")] public bool FirstContactNeedsOrigin { get; set; } = true;
    [JsonPropertyName("usePortrait")] public bool UsePortrait { get; set; } = true;
    [JsonPropertyName("part1")] public PartConfig Part1 { get; set; } = new() { Level = 3, Xp = 2000, Roubles = 25000, Standing = 0.02, Kills = 2 };
    [JsonPropertyName("part2")] public Part2Config Part2 { get; set; } = new() { Level = 5, Xp = 3500, Roubles = 40000, Standing = 0.03, Kills = 4 };
    [JsonPropertyName("part3")] public PartConfig Part3 { get; set; } = new() { Level = 8, Xp = 5000, Roubles = 50000, Standing = 0.05, BitcoinReward = true };
}

/// <summary>Runs after The Jackal (TraderRegistration + 100) and the Black Division quests (+ 125) are in.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 130)]
public class OriginLoader(ISptLogger<OriginLoader> logger, ModHelper modHelper, CustomQuestService questService, LocaleTable locales,
    TradersTable traders, TemplateTable templates, ImageRouter imageRouter, ProfileHelper profileHelper, LocationTable locationTable) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        OriginConfig cfg;
        try { cfg = modHelper.GetJsonDataFromFile<OriginConfig>(dir, "config.json") ?? new OriginConfig(); }
        catch (Exception ex) { logger.Warning($"[JackalOrigin] config.json could not be read ({ex.Message}); using the defaults"); cfg = new OriginConfig(); }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Base is null) { logger.Warning("[JackalOrigin] The Jackal is not installed; nothing added"); return Task.CompletedTask; }

        if (cfg.UsePortrait)
        {
            var portrait = System.IO.Path.Combine(dir, "jackal_portrait.png");
            if (File.Exists(portrait))
            {
                imageRouter.AddRoute(Ids.PortraitRoute, portrait);
                jackal.Base.Avatar = Ids.PortraitRoute + ".png";
                logger.Info("[JackalOrigin] square portrait in use");
            }
            else logger.Warning("[JackalOrigin] jackal_portrait.png is missing; keeping the old portrait");
        }

        if (!cfg.Enabled) { logger.Info("[JackalOrigin] origin quests are off in config.json"); return Task.CompletedTask; }

        var langs = locales.Global.Keys.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        var added = 0;
        var part2Added = false;
        foreach (var (quest, text) in OriginStory.Build(cfg))
        {
            var result = questService.CreateQuest(new NewQuestDetails
            {
                NewQuest = quest,
                Locales = langs.ToDictionary(l => l, _ => new Dictionary<string, string>(text)),
                LockedToSide = null,
            });
            if (!result.Success) { logger.Error($"[JackalOrigin] {quest.QuestName} not added: {string.Join(", ", result.Errors)}"); continue; }
            added++;
            if (quest.Id == OriginStory.QuestId(2)) part2Added = true;
        }

        if (part2Added && !cfg.Part2.PierBoatAlwaysOpenDuringQuest) logger.Info("[JackalOrigin] Part 2: Pier Boat keeps its normal Shoreline chance (pierBoatAlwaysOpenDuringQuest is off)");
        else if (part2Added)
        {
            try
            {
                var chance = PierBoat.DatabaseChance(locationTable);
                if (PierBoat.Enable(profileHelper, locationTable, logger, OriginStory.QuestId(2)))
                    logger.Info($"[JackalOrigin] Part 2: Pier Boat will be open in every Shoreline raid while Part 2 is running (database chance {chance?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; pierBoatAlwaysOpenDuringQuest)");
                else logger.Warning("[JackalOrigin] Part 2: Pier Boat hook not active; the exit keeps its normal Shoreline chance");
            }
            catch (Exception ex) { logger.Warning($"[JackalOrigin] Part 2: Pier Boat hook not active ({ex.Message}); the exit keeps its normal Shoreline chance"); }
        }

        if (cfg.LockJackalUntilTrusted) jackal.Base.UnlockedByDefault = false;

        var gated = "";
        if (cfg.FirstContactNeedsOrigin && added == OriginStory.PartCount)
        {
            var first = FindFirstContact();
            if (first is null) logger.Warning("[JackalOrigin] First Contact not found; it stays open without the origin story");
            else
            {
                var start = first.Conditions.AvailableForStart ??= [];
                var gateId = Ids.Of("gate:first-contact");
                if (start.All(c => c.Id != gateId))
                {
                    var node = new JsonObject
                    {
                        ["availableAfter"] = 0, ["conditionType"] = "Quest", ["dispersion"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "",
                        ["id"] = gateId.ToString(), ["index"] = start.Count, ["parentId"] = "", ["status"] = new JsonArray(4),
                        ["target"] = OriginStory.QuestId(OriginStory.PartCount).ToString(), ["visibilityConditions"] = new JsonArray(),
                    };
                    start.Add(node.Deserialize<QuestCondition>(OriginStory.JsonOptions)!);
                }
                gated = $", First Contact ({first.Id}) now waits for Part 3";
            }
        }
        logger.Success($"[JackalOrigin] origin story: {added} of {OriginStory.PartCount} quests at Fence{(cfg.LockJackalUntilTrusted ? ", the Jackal starts locked on new characters" : "")}{gated}");
        return Task.CompletedTask;
    }

    /// <summary>The Jackal mod's first campaign job: by name, else his campaign quest (its own icon) that waits on no other quest.</summary>
    private Quest? FindFirstContact()
    {
        var mine = templates.Quests.Values.Where(q => q.TraderId == new MongoId(Ids.Jackal)).ToList();
        return mine.FirstOrDefault(q => q.QuestName == "First Contact")
            ?? mine.FirstOrDefault(q => q.Image == "/files/quest/icon/596b465486f77457ca186188.jpg"
                && (q.Conditions.AvailableForStart ?? []).All(c => c.ConditionType != "Quest"));
    }
}
