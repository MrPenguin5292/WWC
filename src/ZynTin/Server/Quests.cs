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

namespace ZynTin.Server;

/// <summary>
/// Widowz 2026-11-17: "Can you have a questline to learn how to make and develop the zyns from therapist and it becomes a buyable and
/// craftable". Four Therapist quests from db/quests.json (Smoke Break, Fine Print, Pouch Work, Field Trial), built in the database's own JSON
/// shape and read back through SPT's converters (same route as the ARC V2 questline). Pouch Work gives the first two tins; Field Trial puts
/// the Zyn tins on the shelf (quest assort) and unlocks their Medstation recipes (ProductionScheme rewards matched by each recipe's
/// QuestComplete requirement). Widowz 03:09 UTC, on Velo and Siberia: "Make them do they are quests after the zyns": two Jackal quests
/// follow Field Trial, Imported Goods (unlocks Velo) and Minus Eighty (unlocks Siberia); 04:25 UTC Killa: Hard Sell sits between them. A quest's "unlocks" names the brand it opens.
/// Runs at TraderRegistration + 175, after the offers (+ 174).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 175)]
public class Quests(ISptLogger<Quests> logger, ModHelper modHelper, CustomQuestService questService, LocaleTable locales, TradersTable traders, TemplateTable templates) : IOnLoad
{
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    public static readonly JsonSerializerOptions JsonOptions = Options();
    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true };
        foreach (var c in new SptJsonConverterRegistrator().GetJsonConverters()) o.Converters.Add(c);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    // map name in quests.json -> (location id for the quest list, names the game's Location condition uses); same table as the JPC questline
    private static readonly Dictionary<string, (string Id, string[] Names)> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["any"] = ("any", []), ["Customs"] = ("56f40101d2720b2a4d8b45d6", ["bigmap"]), ["Woods"] = ("5704e3c2d2720bac5b8b4567", ["Woods"]),
        ["Shoreline"] = ("5704e554d2720bac5b8b456e", ["Shoreline"]), ["Interchange"] = ("5714dbc024597771384a510d", ["Interchange"]),
        ["Reserve"] = ("5704e5fad2720bc05b8b4567", ["RezervBase"]), ["Lighthouse"] = ("5704e4dad2720bb55b8b4567", ["Lighthouse"]),
        ["Streets"] = ("5714dc692459777137212e12", ["TarkovStreets"]),
    };

    public static string Sid(string key) => Ids.Of(key).ToString();
    public static string QuestId(string key) => Sid($"quest:{key.ToLowerInvariant()}");

    private static (string Path, DateTime Stamp, QuestFile File)? loaded;

    /// <summary>quests.json, read once per start (the recipe stage and the questline ask for it once per tin).</summary>
    public static QuestFile Load(string dir)
    {
        var path = Path.Combine(dir, "db", "quests.json");
        var stamp = File.GetLastWriteTimeUtc(path);
        if (loaded is { } c && c.Path == path && c.Stamp == stamp) return c.File;
        var file = JsonSerializer.Deserialize<QuestFile>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("quests.json is empty");
        loaded = (path, stamp, file);
        return file;
    }

    /// <summary>The quest whose completion opens a brand's offers and recipes (its "unlocks"; for Zyn the last quest when no quest names a
    /// brand). Null when no quest opens that brand: then its tins are not locked.</summary>
    public static string? UnlockQuest(string dir, string brand)
    {
        try
        {
            var f = Load(dir);
            var q = f.Quests.FirstOrDefault(x => x.Unlocks == brand) ?? (brand == "zyn" && f.Quests.All(x => x.Unlocks == null) ? f.Quests.LastOrDefault() : null);
            return q == null ? null : QuestId(q.Key);
        }
        catch { return null; }
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { logger.Error($"[Zyn] questline not added, so the tins sell and craft straight away: {ex}"); Unlock(); }
        return Task.CompletedTask;
    }

    /// <summary>On a failed questline nothing should stay locked behind quests that do not exist.</summary>
    private List<string> currentFlavours = [];

    private void Unlock()
    {
        foreach (var id in new[] { Ids.Therapist, Ids.Jackal })
        {
            var t = traders.GetTrader(new MongoId(id));
            if (t?.QuestAssort is null) continue;
            foreach (var k in t.QuestAssort.Keys.ToList()) foreach (var fl in Defaults.Flavours().Select(x => x.Key).Concat(currentFlavours)) t.QuestAssort[k].Remove(Ids.Offer(fl));
        }
    }

    private static bool IsJackals(QuestDef q) => string.Equals(q.Trader?.Trim(), "jackal", StringComparison.OrdinalIgnoreCase);

    private void Run()
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.Questline) { logger.Info("[Zyn] questline switched off in config.json: the tins sell and craft without it"); return; }
        currentFlavours = cfg.Flavours.Select(x => x.Key).ToList();
        if (!cfg.Flavours.Any(x => templates.Items.ContainsKey(Ids.Item(x.Key)))) return;
        var file = Load(dir);
        _ = traders.GetTrader(new MongoId(Ids.Therapist)) ?? throw new InvalidOperationException("Therapist not found");
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal))?.Base is not null;
        // Audit 2026-11-17: without The Jackal (no WWC) his quests are left out. His tins have no seller then, so a Therapist stand-in
        // quest could never be finished (Hard Sell wants Velo cans nobody sells) and would only block the chain.
        if (!jackal && file.Quests.Any(IsJackals))
            logger.Warning($"[Zyn] The Jackal is not installed, so his quests ({string.Join(", ", file.Quests.Where(IsJackals).Select(q => q.Title))}) and his tins are left out");
        var langs = locales.Global.Keys.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        string? previous = null;
        var report = new List<string>();
        var built = 0;
        foreach (var q in file.Quests)
        {
            if (IsJackals(q) && !jackal) continue;
            var traderId = IsJackals(q) ? Ids.Jackal : Ids.Therapist;
            var (quest, text) = Build(q, previous, cfg, traderId);
            var result = questService.CreateQuest(new NewQuestDetails { NewQuest = quest, Locales = langs.ToDictionary(l => l, _ => new Dictionary<string, string>(text)), LockedToSide = null });
            if (!result.Success) throw new InvalidOperationException($"\"{q.Title}\" not added: {string.Join(", ", result.Errors)}");
            report.Add($"{q.Title} ({(traderId == Ids.Jackal ? "Jackal" : "Therapist")}, lvl {q.Level}{(q.RewardTins > 0 ? $", {q.RewardTins} tins" : "")}{(q.Unlocks != null ? $", unlocks {q.Unlocks}" : "")})");
            // each locked tin of this quest's brand waits on its own seller's quest assort for this quest
            foreach (var fl in cfg.Flavours.Where(x => Shared.LockedOf(cfg, x) && UnlockQuest(dir, Shared.BrandOf(x)) == quest.Id.ToString()))
            {
                var t = traders.GetTrader(new MongoId(Shared.SellerIdOf(cfg, fl)));
                if (t?.Assort?.Items is null || t.QuestAssort is null || !t.Assort.Items.Any(i => i.Id == Ids.Offer(fl.Key))) continue;
                foreach (var k in new[] { "started", "success", "fail" }) if (!t.QuestAssort.ContainsKey(k)) t.QuestAssort[k] = new Dictionary<MongoId, MongoId>();
                t.QuestAssort["success"][Ids.Offer(fl.Key)] = quest.Id;
            }
            previous = quest.Id.ToString();
            built++;
        }
        logger.Info($"[Zyn] questline: {string.Join(" > ", report)}");
        logger.Success($"[Zyn] {built} quest(s); each brand's offers and recipes open with its quest");
    }

    private (Quest, Dictionary<string, string>) Build(QuestDef q, string? previous, ZynConfig cfg, string traderId)
    {
        var key = q.Key.ToLowerInvariant();
        string Cid(string part) => Sid($"quest:{key}:{part}");
        var questId = QuestId(key);
        if (!Maps.TryGetValue(string.IsNullOrWhiteSpace(q.Map) ? "any" : q.Map, out var map)) throw new InvalidDataException($"{q.Title}: unknown map {q.Map}");
        string[] MapNames(ObjectiveDef o) => (o.Maps ?? []).Select(m => Maps.TryGetValue(m, out var x) ? x : throw new InvalidDataException($"{q.Title}: unknown map {m}")).SelectMany(x => x.Names).Distinct().ToArray();
        var text = new Dictionary<string, string>();
        var finish = new JsonArray();
        bool kills = false, pickup = false, extract = false;
        for (var i = 0; i < q.Objectives.Count; i++)
        {
            var o = q.Objectives[i];
            var k = $"obj:{o.Key.ToLowerInvariant()}";
            switch (o.Type.ToLowerInvariant())
            {
                case "kill":
                {
                    var conditions = new JsonArray();
                    var names = MapNames(o);
                    if (names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Cid(k + ":location"), ["target"] = Strings(names) });
                    conditions.Add(new JsonObject
                    {
                        ["bodyPart"] = new JsonArray(), ["compareMethod"] = ">=", ["conditionType"] = "Kills", ["daytime"] = new JsonObject { ["from"] = 0, ["to"] = 0 },
                        ["distance"] = new JsonObject { ["compareMethod"] = ">=", ["value"] = 0 }, ["dynamicLocale"] = false, ["enemyEquipmentExclusive"] = new JsonArray(),
                        ["enemyEquipmentInclusive"] = new JsonArray(), ["enemyHealthEffects"] = new JsonArray(), ["id"] = Cid(k + ":kills"), ["resetOnSessionEnd"] = false,
                        ["savageRole"] = new JsonArray(), ["target"] = o.Target ?? "Savage", ["value"] = 1, ["weapon"] = new JsonArray(), ["weaponCaliber"] = new JsonArray(),
                        ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray(),
                    });
                    // like the game's "Cocktail Tasting": the kill only counts while a Zyn pouch's buff list is running (Velo and Siberia have their own)
                    if (o.UnderZyn) conditions.Add(new JsonObject { ["conditionType"] = "HealthBuff", ["dynamicLocale"] = false, ["id"] = Cid(k + ":buff"), ["target"] = new JsonArray(Ids.BuffList) });
                    finish.Add(Counter(Cid(k), Cid(k + ":counter"), i, "Elimination", Math.Max(1, o.Count), conditions));
                    kills = true;
                    break;
                }
                case "extract":
                {
                    var conditions = new JsonArray();
                    var names = MapNames(o);
                    if (names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Cid(k + ":location"), ["target"] = Strings(names) });
                    conditions.Add(new JsonObject { ["conditionType"] = "ExitStatus", ["dynamicLocale"] = false, ["id"] = Cid(k + ":status"), ["status"] = Strings(["Survived", "Runner", "Transit"]) });
                    finish.Add(Counter(Cid(k), Cid(k + ":counter"), i, "Completion", Math.Max(1, o.Count), conditions));
                    extract = true;
                    break;
                }
                case "handover":
                {
                    // a brand name ("zyn", "velo", "killa", "siberia") = every tin of that brand; anything else is a template id
                    var brands = cfg.Flavours.Select(Shared.BrandOf).ToHashSet();
                    var targets = (o.Items ?? []).SelectMany(x => brands.Contains(x) ? cfg.Flavours.Where(fl => Shared.BrandOf(fl) == x).Select(fl => Ids.Item(fl.Key).ToString()) : [x]).Distinct().ToArray();
                    finish.Add(new JsonObject
                    {
                        ["conditionType"] = "HandoverItem", ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid(k), ["index"] = i, ["isEncoded"] = false,
                        ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = o.FoundInRaid, ["parentId"] = "",
                        ["target"] = Strings(targets), ["value"] = Math.Max(1, o.Count), ["visibilityConditions"] = new JsonArray(),
                    });
                    pickup = true;
                    break;
                }
                default: throw new InvalidDataException($"{q.Title}: unknown objective type {o.Type}");
            }
            text[finish[^1]!["id"]!.GetValue<string>()] = o.Text;
        }

        var start = new JsonArray
        {
            new JsonObject { ["compareMethod"] = ">=", ["conditionType"] = "Level", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("level"), ["index"] = 0, ["parentId"] = "", ["value"] = Math.Max(1, q.Level), ["visibilityConditions"] = new JsonArray() },
        };
        // Widowz: every Jackal quest needs a level and a Jackal loyalty requirement ("loyalty" in quests.json, against the quest's own trader)
        if (q.Loyalty > 0)
            start.Add(new JsonObject
            {
                ["compareMethod"] = ">=", ["conditionType"] = "TraderLoyalty", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("loyalty"), ["index"] = start.Count,
                ["parentId"] = "", ["target"] = traderId, ["value"] = Math.Clamp(q.Loyalty, 1, 4), ["visibilityConditions"] = new JsonArray(),
            });
        if (previous != null)
            start.Add(new JsonObject
            {
                ["availableAfter"] = 0, ["conditionType"] = "Quest", ["dispersion"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("after"), ["index"] = start.Count,
                ["parentId"] = "", ["status"] = new JsonArray(4), ["target"] = previous, ["visibilityConditions"] = new JsonArray(),
            });

        var success = new JsonArray();
        if (q.Xp > 0) success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("xp"), ["isHidden"] = false, ["type"] = "Experience", ["unknown"] = false, ["value"] = q.Xp });
        success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("standing"), ["isHidden"] = false, ["target"] = traderId, ["type"] = "TraderStanding", ["unknown"] = false, ["value"] = 0.02 });
        if (q.Roubles > 0) success.Add(ItemReward(Cid("money"), [Cid("money-item")], Roubles, q.Roubles));
        if (q.RewardTins > 0)
        {
            var tinTpl = Ids.Item(cfg.Flavours.Any(x => x.Key == q.RewardFlavour) ? q.RewardFlavour : cfg.Flavours[0].Key).ToString();
            var ids = Enumerable.Range(0, q.RewardTins).Select(n => Cid($"tin-item:{n}")).ToList();
            success.Add(new JsonObject
            {
                ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = false, ["gameMode"] = Modes(), ["id"] = Cid("tins"), ["isEncoded"] = false, ["isHidden"] = false,
                ["items"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["_id"] = id, ["_tpl"] = tinTpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = 1 } }).ToArray()),
                ["target"] = ids[^1], ["type"] = "Item", ["unknown"] = false, ["value"] = q.RewardTins,
            });
        }
        var brand = q.Unlocks;
        if (brand != null)
        {
            var mine = cfg.Flavours.Where(x => Shared.BrandOf(x) == brand && Shared.LockedOf(cfg, x)).ToList();
            // AssortmentUnlock is display only (the quest assort does the unlocking): show the brand's first locked tin, every locked offer opens
            var shownFlavour = mine.FirstOrDefault(x => x.Sell);
            var sellerId = shownFlavour != null ? Shared.SellerIdOf(cfg, shownFlavour) : Shared.SellerId(cfg);
            // (only when the seller exists: a reward naming a missing trader would break the quest window, e.g. seller "jackal" without WWC)
            if (shownFlavour != null && cfg.Offer.Sell && traders.GetTrader(new MongoId(sellerId))?.Assort is not null)
            {
                var shown = Cid("unlock-item");
                success.Add(new JsonObject
                {
                    ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("unlock"), ["isHidden"] = false,
                    ["items"] = new JsonArray(new JsonObject { ["_id"] = shown, ["_tpl"] = Ids.Item(shownFlavour.Key).ToString() }),
                    ["loyaltyLevel"] = Math.Clamp(shownFlavour.LoyaltyLevel ?? cfg.Offer.LoyaltyLevel, 1, 4),
                    ["target"] = shown, ["traderId"] = sellerId, ["type"] = "AssortmentUnlock", ["unknown"] = false,
                });
            }
            // one ProductionScheme per recipe: SPT unlocks each by area, end product, QuestComplete requirement and level
            if (cfg.Craft.Enabled)
                foreach (var fl in mine.Where(x => x.Craft))
                {
                    var craftItem = Cid($"craft-item:{fl.Key}");
                    success.Add(new JsonObject
                    {
                        ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid($"craft:{fl.Key}"), ["isHidden"] = false,
                        ["items"] = new JsonArray(new JsonObject { ["_id"] = craftItem, ["_tpl"] = Ids.Item(fl.Key).ToString(), ["upd"] = new JsonObject { ["StackObjectsCount"] = Math.Max(1, cfg.Craft.Count) } }),
                        ["loyaltyLevel"] = cfg.Craft.AreaLevel, ["target"] = craftItem, ["traderId"] = Ids.MedStationArea, ["type"] = "ProductionScheme", ["unknown"] = false,
                    });
                }
        }
        var kinds = (kills ? 1 : 0) + (pickup ? 1 : 0) + (extract ? 1 : 0);
        var type = kinds > 1 ? "Multi" : kills ? "Elimination" : extract ? "Completion" : "PickUp";
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
            ["traderId"] = traderId, ["type"] = type,
        };
        text[$"{questId} name"] = q.Title;
        text[$"{questId} description"] = q.Story.Trim();
        text[$"{questId} note"] = "";
        text[$"{questId} startedMessageText"] = q.Started;
        text[$"{questId} successMessageText"] = q.Done;
        text[$"{questId} failMessageText"] = "We'll try again another time.";
        text[$"{questId} changeQuestMessageText"] = "Something has changed. Read it again.";
        text[$"{questId} acceptPlayerMessage"] = "Understood. I'll see to it.";
        text[$"{questId} completePlayerMessage"] = "It's done. Everything you asked for.";
        text[$"{questId} declinePlayerMessage"] = "Not right now.";
        return (quest.Deserialize<Quest>(JsonOptions)!, text);
    }

    private static JsonArray Modes() => new("regular", "pve");

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject ItemReward(string id, List<string> itemIds, string tpl, int count) => new()
    {
        ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = false, ["gameMode"] = Modes(), ["id"] = id, ["isEncoded"] = false, ["isHidden"] = false,
        ["items"] = new JsonArray(new JsonObject { ["_id"] = itemIds[0], ["_tpl"] = tpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = count } }),
        ["target"] = itemIds[0], ["type"] = "Item", ["unknown"] = false, ["value"] = count,
    };

    private static JsonObject Counter(string id, string counterId, int index, string type, int value, JsonArray conditions) => new()
    {
        ["completeInSeconds"] = 0, ["conditionType"] = "CounterCreator", ["counter"] = new JsonObject { ["conditions"] = conditions, ["id"] = counterId },
        ["doNotResetIfCounterCompleted"] = false, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = id, ["index"] = index, ["isNecessary"] = false,
        ["isResetOnConditionFailed"] = false, ["oneSessionOnly"] = false, ["parentId"] = "", ["type"] = type, ["value"] = value, ["visibilityConditions"] = new JsonArray(),
    };
}

public sealed class QuestFile
{
    [JsonPropertyName("quests")] public List<QuestDef> Quests { get; set; } = [];
}

public sealed class QuestDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>"therapist" (default) or "jackal".</summary>
    [JsonPropertyName("trader")] public string? Trader { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    /// <summary>Loyalty level needed with the quest's trader before it is offered (0 = none).</summary>
    [JsonPropertyName("loyalty")] public int Loyalty { get; set; }
    [JsonPropertyName("xp")] public int Xp { get; set; }
    [JsonPropertyName("roubles")] public int Roubles { get; set; }
    [JsonPropertyName("rewardTins")] public int RewardTins { get; set; }
    [JsonPropertyName("rewardFlavour")] public string RewardFlavour { get; set; } = "coolmint";
    /// <summary>The brand whose tins open with this quest ("zyn", "velo", "siberia"); an old true means Zyn.</summary>
    [JsonPropertyName("unlocks"), JsonConverter(typeof(BrandConverter))] public string? Unlocks { get; set; }
    [JsonPropertyName("story")] public string Story { get; set; } = "";
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    [JsonPropertyName("done")] public string Done { get; set; } = "";
    [JsonPropertyName("objectives")] public List<ObjectiveDef> Objectives { get; set; } = [];
}

public sealed class ObjectiveDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; } = 1;
    [JsonPropertyName("maps")] public List<string>? Maps { get; set; }
    [JsonPropertyName("underZyn")] public bool UnderZyn { get; set; }
    [JsonPropertyName("items")] public List<string>? Items { get; set; }
    [JsonPropertyName("foundInRaid")] public bool FoundInRaid { get; set; }
}

/// <summary>"unlocks": "velo" / true (= "zyn") / false or null (= nothing).</summary>
public sealed class BrandConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => "zyn",
        JsonTokenType.String => string.IsNullOrWhiteSpace(reader.GetString()) ? null : reader.GetString()!.Trim().ToLowerInvariant(),
        _ => null,
    };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue(); else writer.WriteStringValue(value);
    }
}
