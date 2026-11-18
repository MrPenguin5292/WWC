using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

namespace Grunts.Server;

/// <summary>
/// Widowz 2026-11-16: a Jackal questline where taking a job makes the Grunts spawn in raid (Factory early, Labs later), each job "kill a
/// certain number of them"; rewards to come later. Then: the Avon gas mask is the reward for the Factory job and is required on Labs
/// ("if you aren't wearing it the enemies won't spawn"). Quests come from db/quests.json, built in the database's own JSON shape and read
/// back through SPT's converters (the WWC Jobs / USMCPC route), at TraderRegistration + 161. "@mask" in quests.json stands for config maskTpl.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 161)]
public class Quests(ISptLogger<Quests> logger, ModHelper modHelper, CustomQuestService questService, LocaleTable locales, TradersTable traders,
    TemplateTable templates) : IOnLoad
{
    public const string Jackal = "672b4a010000000000000001";
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    /// <summary>Filled at load: the chain as the spawn patch needs it (quest id -> maps, waves, required worn templates).</summary>
    internal static readonly List<JobSpawn> Jobs = [];

    public static readonly JsonSerializerOptions JsonOptions = Options();
    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true };
        foreach (var c in new SptJsonConverterRegistrator().GetJsonConverters()) o.Converters.Add(c);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { logger.Error($"[{ModInfo.Name}] questline not added: {ex}"); }
        return Task.CompletedTask;
    }

    internal static QuestFile LoadFile(ModHelper modHelper)
    {
        var dir = PartFolder.Of(modHelper);
        return JsonSerializer.Deserialize<QuestFile>(File.ReadAllText(Path.Combine(dir, "db", "quests.json")), JsonOptions) ?? throw new InvalidDataException("quests.json is empty");
    }

    private void Run()
    {
        var cfg = GruntBot.LoadConfig(modHelper);
        if (!cfg.Enabled || !cfg.QuestlineEnabled) { logger.Info($"[{ModInfo.Name}] questline switched off in config.json"); return; }
        var file = LoadFile(modHelper);
        var jackal = traders.GetTrader(new MongoId(Jackal));
        if (jackal?.Base is null) { logger.Warning($"[{ModInfo.Name}] The Jackal is not installed; no questline"); return; }
        var mask = Resolve(cfg.MaskTpl, out var maskNote);
        if (maskNote is not null) logger.Info($"[{ModInfo.Name}] {maskNote}");
        papr = !string.IsNullOrWhiteSpace(cfg.PaprTpl) && MongoId.IsValidMongoId(cfg.PaprTpl) && templates.Items.ContainsKey(new MongoId(cfg.PaprTpl)) ? cfg.PaprTpl : null;
        if (papr is null) logger.Info($"[{ModInfo.Name}] no PAPR blower installed{(string.IsNullOrWhiteSpace(cfg.PaprTpl) ? " (paprTpl empty)" : $" ({cfg.PaprTpl})")}: End of the Trials gives no blower");
        var langs = locales.Global.Keys.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        string? previous = null;
        var report = new List<string>();
        Jobs.Clear();
        foreach (var q in file.Quests)
        {
            var loyalty = LoyaltyFor(q.Loyalty, q.Level, jackal.Base);
            var (quest, text, job) = Build(q, previous, loyalty, cfg, mask);
            var result = questService.CreateQuest(new NewQuestDetails { NewQuest = quest, Locales = langs.ToDictionary(l => l, _ => new Dictionary<string, string>(text)), LockedToSide = null });
            if (!result.Success) { logger.Error($"[{ModInfo.Name}] \"{q.Title}\" not added: {string.Join(", ", result.Errors)}"); break; }
            Jobs.Add(job);
            report.Add($"{q.Title} (lvl {q.Level}, LL{loyalty}, {q.Map},{q.Objectives.Sum(o => o.Count)} kills{(job.RequireWorn.Count > 0 ? ", mask worn" : "")})");
            previous = quest.Id.ToString();
        }
        logger.Info($"[{ModInfo.Name}] questline: {string.Join(" > ", report)}");
        logger.Success($"[{ModInfo.Name}] {Jobs.Count} Jackal Grunt job(s)");
    }

    /// <summary>"@mask" -> config maskTpl; a template that is not in the database comes back null (the caller drops what needs it).</summary>
    private string? Resolve(string? tpl, out string? note)
    {
        note = null;
        if (string.IsNullOrWhiteSpace(tpl)) { note = "no maskTpl in config.json: the Factory job gives no mask and Labs needs none"; return null; }
        if (!MongoId.IsValidMongoId(tpl) || !templates.Items.ContainsKey(new MongoId(tpl))) { note = $"mask {tpl} is not installed: the Factory job gives no mask and Labs needs none"; return null; }
        return tpl;
    }

    private string? papr;

    /// <summary>"@mask" / "@papr" -> the config templates; anything else must be an installed template; null = leave it out.</summary>
    private string? Tpl(string raw, string? mask) => raw switch
    {
        "@mask" => mask,
        "@papr" => papr,
        _ => MongoId.IsValidMongoId(raw) && templates.Items.ContainsKey(new MongoId(raw)) ? raw : null,
    };

    /// <summary>
    /// The Jackal loyalty level a job needs before it can be started (Widowz 2026-11-17: every Jackal quest needs a player level and a Jackal
    /// loyalty level). "loyalty" in quests.json, else the highest loyalty level his own table opens at the job's player level, so the gate never
    /// asks for a level the job itself doesn't. His campaign has no loyalty gate and earns the standing, so no chain can lock itself out.
    /// </summary>
    internal static int LoyaltyFor(int? wanted, int level, TraderBase trader)
    {
        if (wanted is int w) return Math.Clamp(w, 1, 4);
        var ll = 1;
        var tiers = trader.LoyaltyLevels ?? [];
        for (var i = 1; i < tiers.Count && i < 4; i++)
            if ((tiers[i].MinLevel is { } m ? (int)m : 1) <= level) ll = i + 1;
        return ll;
    }

    private (Quest, Dictionary<string, string>, JobSpawn) Build(QuestDef q, string? previous, int loyalty, ModConfig cfg, string? mask)
    {
        string Cid(string part) => Ids.Part(q.Key, part);
        var questId = Ids.Quest(q.Key);
        if (!Ids.Maps.TryGetValue(q.Map, out var map)) throw new InvalidDataException($"unknown map {q.Map}");
        var text = new Dictionary<string, string>();
        var finish = new JsonArray();
        var job = new JobSpawn { QuestId = questId, Title = q.Title, Waves = q.Waves };
        var worn = q.RequireWorn.Select(t => Tpl(t, mask)).OfType<string>().Distinct().ToList();
        job.RequireWorn.AddRange(worn);
        for (var i = 0; i < q.Objectives.Count; i++)
        {
            var o = q.Objectives[i];
            if (!string.Equals(o.Type, "grunts", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{q.Title}: unknown objective type {o.Type}");
            var k = $"obj:{o.Key.ToLowerInvariant()}";
            if (!Ids.Maps.TryGetValue(o.Map, out var om)) throw new InvalidDataException($"{q.Title}: unknown map {o.Map}");
            foreach (var n in om.Names) if (!job.Maps.Contains(n)) job.Maps.Add(n);
            var conditions = new JsonArray();
            if (om.Names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Cid(k + ":location"), ["target"] = Strings(om.Names) });
            conditions.Add(new JsonObject
            {
                ["bodyPart"] = new JsonArray(), ["compareMethod"] = ">=", ["conditionType"] = "Kills", ["daytime"] = new JsonObject { ["from"] = 0, ["to"] = 0 },
                ["distance"] = new JsonObject { ["compareMethod"] = ">=", ["value"] = 0 }, ["dynamicLocale"] = false, ["enemyEquipmentExclusive"] = new JsonArray(),
                ["enemyEquipmentInclusive"] = new JsonArray(), ["enemyHealthEffects"] = new JsonArray(), ["id"] = Cid(k + ":kills"), ["resetOnSessionEnd"] = false,
                ["savageRole"] = Strings([cfg.GruntRole]), ["target"] = "Savage", ["value"] = 1, ["weapon"] = new JsonArray(), ["weaponCaliber"] = new JsonArray(),
                ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray(),
            });
            var wearing = (o.Wearing ?? []).Select(t => Tpl(t, mask)).OfType<string>().Distinct().ToList();
            if (wearing.Count > 0)
                conditions.Add(new JsonObject
                {
                    ["IncludeNotEquippedItems"] = false, ["conditionType"] = "Equipment", ["dynamicLocale"] = false, ["equipmentExclusive"] = new JsonArray(),
                    ["equipmentInclusive"] = new JsonArray(wearing.Select(w => (JsonNode?)new JsonArray(w)).ToArray()), ["id"] = Cid(k + ":equipment"),
                });
            finish.Add(new JsonObject
            {
                ["completeInSeconds"] = 0, ["conditionType"] = "CounterCreator", ["counter"] = new JsonObject { ["conditions"] = conditions, ["id"] = Cid(k + ":counter") },
                ["doNotResetIfCounterCompleted"] = false, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid(k), ["index"] = i, ["isNecessary"] = false,
                ["isResetOnConditionFailed"] = false, ["oneSessionOnly"] = false, ["parentId"] = "", ["type"] = "Elimination", ["value"] = Math.Max(1, o.Count),
                ["visibilityConditions"] = new JsonArray(),
            });
            text[Cid(k)] = wearing.Count > 0 || o.Wearing is not { Count: > 0 } ? o.Text : o.TextWithoutGear ?? o.Text;
        }

        var start = new JsonArray
        {
            new JsonObject { ["compareMethod"] = ">=", ["conditionType"] = "Level", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("level"), ["index"] = 0, ["parentId"] = "", ["value"] = Math.Max(1, q.Level), ["visibilityConditions"] = new JsonArray() },
            new JsonObject { ["compareMethod"] = ">=", ["conditionType"] = "TraderLoyalty", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("loyalty"), ["index"] = 1, ["parentId"] = "", ["target"] = Jackal, ["value"] = loyalty, ["visibilityConditions"] = new JsonArray() },
        };
        if (previous != null)
            start.Add(new JsonObject
            {
                ["availableAfter"] = 0, ["conditionType"] = "Quest", ["dispersion"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("after"), ["index"] = 2,
                ["parentId"] = "", ["status"] = new JsonArray(4), ["target"] = previous, ["visibilityConditions"] = new JsonArray(),
            });

        var success = new JsonArray();
        if (q.Xp > 0) success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("xp"), ["isHidden"] = false, ["type"] = "Experience", ["unknown"] = false, ["value"] = q.Xp });
        if (q.Standing != 0) success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("standing"), ["isHidden"] = false, ["target"] = Jackal, ["type"] = "TraderStanding", ["unknown"] = false, ["value"] = q.Standing });
        if (q.Roubles > 0) success.Add(ItemReward(Cid("money"), Cid("money-item"), Roubles, q.Roubles, false));
        foreach (var r in q.RewardItems)
        {
            var tpl = Tpl(r.Tpl, mask);
            if (tpl is null) { logger.Info($"[{ModInfo.Name}] \"{q.Title}\": reward {r.Tpl} is not installed, left out"); continue; }
            success.Add(ItemReward(Cid($"reward:{tpl}"), Cid($"reward-item:{tpl}"), tpl, Math.Max(1, r.Count), r.FoundInRaid));
        }

        var quest = new JsonObject
        {
            ["QuestName"] = q.Title, ["_id"] = questId, ["acceptPlayerMessage"] = $"{questId} acceptPlayerMessage", ["acceptanceAndFinishingSource"] = "eft", ["arenaLocations"] = new JsonArray(),
            ["canShowNotificationsInGame"] = true, ["changeQuestMessageText"] = $"{questId} changeQuestMessageText", ["completePlayerMessage"] = $"{questId} completePlayerMessage",
            ["conditions"] = new JsonObject { ["AvailableForFinish"] = finish, ["AvailableForStart"] = start, ["Fail"] = new JsonArray() },
            ["declinePlayerMessage"] = $"{questId} declinePlayerMessage", ["description"] = $"{questId} description", ["failMessageText"] = $"{questId} failMessageText", ["gameModes"] = new JsonArray(),
            ["image"] = "/files/quest/icon/" + q.Icon, ["instantComplete"] = false, ["isKey"] = false, ["location"] = map.Id, ["name"] = $"{questId} name", ["note"] = $"{questId} note",
            ["progressSource"] = "eft", ["rankingModes"] = new JsonArray(), ["restartable"] = false,
            ["rewards"] = new JsonObject { ["Fail"] = new JsonArray(), ["Started"] = new JsonArray(), ["Success"] = success },
            ["secretQuest"] = false, ["side"] = "Pmc", ["startedMessageText"] = $"{questId} startedMessageText", ["status"] = 0, ["successMessageText"] = $"{questId} successMessageText",
            ["traderId"] = Jackal, ["type"] = "Elimination",
        };
        var story = q.Story.Trim();
        if (worn.Count == 0 && !string.IsNullOrWhiteSpace(q.StoryWithoutGear)) story = q.StoryWithoutGear.Trim();
        text[$"{questId} name"] = q.Title;
        text[$"{questId} description"] = story;
        text[$"{questId} note"] = "";
        text[$"{questId} startedMessageText"] = q.Started;
        text[$"{questId} successMessageText"] = q.Done;
        text[$"{questId} failMessageText"] = "It'll keep. Come back when you're ready.";
        text[$"{questId} changeQuestMessageText"] = "Something's changed. Have another look.";
        text[$"{questId} acceptPlayerMessage"] = "Alright. I'll handle it.";
        text[$"{questId} completePlayerMessage"] = "Done. They won't be cutting anyone else.";
        text[$"{questId} declinePlayerMessage"] = "Not right now.";
        return (quest.Deserialize<Quest>(JsonOptions)!, text, job);
    }

    private static JsonArray Modes() => new("regular", "pve");
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject ItemReward(string id, string itemId, string tpl, int count, bool fir) => new()
    {
        ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = fir, ["gameMode"] = Modes(), ["id"] = id, ["isEncoded"] = false, ["isHidden"] = false,
        ["items"] = new JsonArray(new JsonObject { ["_id"] = itemId, ["_tpl"] = tpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = count } }),
        ["target"] = itemId, ["type"] = "Item", ["unknown"] = false, ["value"] = count,
    };
}

/// <summary>What the spawn patch needs to know about one job.</summary>
internal sealed class JobSpawn
{
    public string QuestId = "";
    public string Title = "";
    public List<string> Maps = [];
    public List<WaveDef> Waves = [];
    /// <summary>Templates of which one must be worn (equipped, not carried) when the raid starts, or no Grunts come.</summary>
    public List<string> RequireWorn = [];
}
