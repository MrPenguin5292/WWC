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
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

namespace RCBombCar.Server;

/// <summary>
/// Widowz 2026-11-17: "I want a long quest line for it and it's only craftable afterwords". Eight Jackal quests from db/quests.json, built
/// in the database's own JSON shape and read back through SPT's converters (the Zyn / USMC PC route). Every quest has a level and a Jackal
/// loyalty requirement (LL1 below level 10, LL2 from 10, LL3 from 18, LL4 from 25). The last one gives the first car and opens the
/// Workbench recipe (a ProductionScheme reward matched by the recipe's QuestComplete requirement). Without The Jackal (no WWC) Prapor
/// gives them so the recipe can still be reached. Runs at TraderRegistration + 179, after the (optional) offer at + 178.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 179)]
public class Quests(ISptLogger<Quests> logger, ModHelper modHelper, CustomQuestService questService, LocaleTable locales, TradersTable traders, TemplateTable templates) : IOnLoad
{
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    public const int WorkbenchArea = 10;

    public static readonly JsonSerializerOptions JsonOptions = Options();
    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true };
        foreach (var c in new SptJsonConverterRegistrator().GetJsonConverters()) o.Converters.Add(c);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    // map name in quests.json -> (location id for the quest list, names the game's Location condition uses)
    private static readonly Dictionary<string, (string Id, string[] Names)> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["any"] = ("any", []), ["Customs"] = ("56f40101d2720b2a4d8b45d6", ["bigmap"]), ["Woods"] = ("5704e3c2d2720bac5b8b4567", ["Woods"]),
        ["Shoreline"] = ("5704e554d2720bac5b8b456e", ["Shoreline"]), ["Interchange"] = ("5714dbc024597771384a510d", ["Interchange"]),
        ["Reserve"] = ("5704e5fad2720bc05b8b4567", ["RezervBase"]), ["Lighthouse"] = ("5704e4dad2720bb55b8b4567", ["Lighthouse"]),
        ["Streets"] = ("5714dc692459777137212e12", ["TarkovStreets"]), ["Factory"] = ("55f2d3fd4bdc2d5f408b4567", ["factory4_day", "factory4_night"]),
    };

    public static string Sid(string key) => Ids.Of(key).ToString();
    public static string QuestId(string key) => Sid($"quest:{key.ToLowerInvariant()}");

    public static QuestFile Load(string dir) =>
        JsonSerializer.Deserialize<QuestFile>(File.ReadAllText(Path.Combine(dir, "db", "quests.json")), JsonOptions) ?? throw new InvalidDataException("quests.json is empty");

    /// <summary>The quest that opens the recipe (marked "unlocks", else the last one). Null when the file can't be read.</summary>
    public static string? UnlockQuest(string dir)
    {
        try { var f = Load(dir); var q = f.Quests.FirstOrDefault(x => x.Unlocks) ?? f.Quests.LastOrDefault(); return q == null ? null : QuestId(q.Key); }
        catch { return null; }
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { logger.Error($"[RC car] questline not added, so the Workbench recipe opens without it: {ex}"); Crafts.UnlockAll(); }
        return Task.CompletedTask;
    }

    private void Run()
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        // audit 2026-11-17: with the mod switched off the questline went on handing out a car and a recipe that no longer existed
        if (!cfg.ModEnabled) { logger.Info("[RC car] switched off in config.json: no questline, no recipe, no offer (cars already owned still work)"); return; }
        if (!cfg.Questline) { logger.Info("[RC car] questline switched off in config.json: the Workbench recipe is open from the start"); return; }
        if (!templates.Items.ContainsKey(Ids.Car)) return;
        var file = Load(dir);
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal))?.Base is not null;
        var traderId = jackal ? Ids.Jackal : Ids.Prapor;
        if (!jackal) logger.Warning("[RC car] The Jackal is not installed (WWC), so Prapor gives the questline");
        var langs = locales.Global.Keys.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        string? previous = null;
        var report = new List<string>();
        var unlockId = UnlockQuest(dir);
        foreach (var q in file.Quests)
        {
            var (quest, text) = Build(q, previous, cfg, traderId, QuestId(q.Key) == unlockId);
            var result = questService.CreateQuest(new NewQuestDetails { NewQuest = quest, Locales = langs.ToDictionary(l => l, _ => new Dictionary<string, string>(text)), LockedToSide = null });
            if (!result.Success) throw new InvalidOperationException($"\"{q.Title}\" not added: {string.Join(", ", result.Errors)}");
            report.Add($"{q.Title} (lvl {q.Level}, LL{q.Loyalty}{(q.RewardCars > 0 ? $", {q.RewardCars} car" : "")}{(QuestId(q.Key) == unlockId ? ", unlocks the recipe" : "")})");
            previous = quest.Id.ToString();
        }
        // an optional trader offer (off by default) also waits for the last quest
        if (cfg.Offer.Sell && unlockId != null)
        {
            var t = traders.GetTrader(new MongoId(cfg.Offer.Seller == "prapor" ? Ids.Prapor : Ids.Jackal));
            if (t?.Assort?.Items?.Any(i => i.Id == Ids.Offer) == true && t.QuestAssort is not null)
            {
                foreach (var k in new[] { "started", "success", "fail" }) if (!t.QuestAssort.ContainsKey(k)) t.QuestAssort[k] = new Dictionary<MongoId, MongoId>();
                t.QuestAssort["success"][Ids.Offer] = new MongoId(unlockId);
            }
        }
        logger.Info($"[RC car] questline: {string.Join(" > ", report)}");
        logger.Success($"[RC car] {file.Quests.Count} quests from {(jackal ? "The Jackal" : "Prapor")}; the last one opens the Workbench recipe");
    }

    private (Quest, Dictionary<string, string>) Build(QuestDef q, string? previous, CarConfig cfg, string traderId, bool unlocks)
    {
        var key = q.Key.ToLowerInvariant();
        string Cid(string part) => Sid($"quest:{key}:{part}");
        var questId = QuestId(key);
        if (!Maps.TryGetValue(string.IsNullOrWhiteSpace(q.Map) ? "any" : q.Map, out var map)) throw new InvalidDataException($"{q.Title}: unknown map {q.Map}");
        string[] MapNames(ObjectiveDef o) => (o.Maps ?? []).Select(m => Maps.TryGetValue(m, out var x) ? x : throw new InvalidDataException($"{q.Title}: unknown map {m}")).SelectMany(x => x.Names).Distinct().ToArray();
        if (q.Level < 1 || q.Loyalty < 1) throw new InvalidDataException($"{q.Title}: every quest needs a level and a loyalty level");
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
                        ["savageRole"] = new JsonArray(), ["target"] = o.Target ?? "Savage", ["value"] = 1, ["weapon"] = Strings(o.Weapons ?? []), ["weaponCaliber"] = new JsonArray(),
                        ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray(),
                    });
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
                    var targets = (o.Items ?? []).Where(x => MongoId.IsValidMongoId(x) && templates.Items.ContainsKey(new MongoId(x))).Distinct().ToArray();
                    if (targets.Length == 0) throw new InvalidDataException($"{q.Title}: objective {o.Key} has no known items");
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
            new JsonObject { ["compareMethod"] = ">=", ["conditionType"] = "Level", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("level"), ["index"] = 0, ["parentId"] = "", ["value"] = q.Level, ["visibilityConditions"] = new JsonArray() },
            new JsonObject
            {
                ["compareMethod"] = ">=", ["conditionType"] = "TraderLoyalty", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("loyalty"), ["index"] = 1,
                ["parentId"] = "", ["target"] = traderId, ["value"] = Math.Clamp(q.Loyalty, 1, 4), ["visibilityConditions"] = new JsonArray(),
            },
        };
        if (previous != null)
            start.Add(new JsonObject
            {
                ["availableAfter"] = 0, ["conditionType"] = "Quest", ["dispersion"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid("after"), ["index"] = start.Count,
                ["parentId"] = "", ["status"] = new JsonArray(4), ["target"] = previous, ["visibilityConditions"] = new JsonArray(),
            });

        var success = new JsonArray();
        if (q.Xp > 0) success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("xp"), ["isHidden"] = false, ["type"] = "Experience", ["unknown"] = false, ["value"] = q.Xp });
        success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("standing"), ["isHidden"] = false, ["target"] = traderId, ["type"] = "TraderStanding", ["unknown"] = false, ["value"] = 0.02 });
        if (q.Roubles > 0) success.Add(ItemReward(Cid("money"), Cid("money-item"), Roubles, q.Roubles));
        for (var n = 0; n < q.RewardCars; n++) success.Add(ItemReward(Cid($"car:{n}"), Cid($"car-item:{n}"), Ids.Car.ToString(), 1));
        if (unlocks && cfg.Craft.Enabled)
        {
            // SPT unlocks the recipe by area, end product, QuestComplete requirement and area level
            var craftItem = Cid("craft-item");
            success.Add(new JsonObject
            {
                ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("craft"), ["isHidden"] = false,
                ["items"] = new JsonArray(new JsonObject { ["_id"] = craftItem, ["_tpl"] = Ids.Car.ToString(), ["upd"] = new JsonObject { ["StackObjectsCount"] = Math.Max(1, cfg.Craft.Count) } }),
                ["loyaltyLevel"] = cfg.Craft.AreaLevel, ["target"] = craftItem, ["traderId"] = WorkbenchArea, ["type"] = "ProductionScheme", ["unknown"] = false,
            });
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
        text[$"{questId} failMessageText"] = "Next time, then.";
        text[$"{questId} changeQuestMessageText"] = "Something has changed. Read it again.";
        text[$"{questId} acceptPlayerMessage"] = "Understood. I'll see to it.";
        text[$"{questId} completePlayerMessage"] = "It's done. Everything you asked for.";
        text[$"{questId} declinePlayerMessage"] = "Not right now.";
        return (quest.Deserialize<Quest>(JsonOptions)!, text);
    }

    private static JsonArray Modes() => new("regular", "pve");

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject ItemReward(string id, string itemId, string tpl, int count) => new()
    {
        ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = false, ["gameMode"] = Modes(), ["id"] = id, ["isEncoded"] = false, ["isHidden"] = false,
        ["items"] = new JsonArray(new JsonObject { ["_id"] = itemId, ["_tpl"] = tpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = count } }),
        ["target"] = itemId, ["type"] = "Item", ["unknown"] = false, ["value"] = count,
    };

    private static JsonObject Counter(string id, string counterId, int index, string type, int value, JsonArray conditions) => new()
    {
        ["completeInSeconds"] = 0, ["conditionType"] = "CounterCreator", ["counter"] = new JsonObject { ["conditions"] = conditions, ["id"] = counterId },
        ["doNotResetIfCounterCompleted"] = false, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = id, ["index"] = index, ["isNecessary"] = false,
        ["isResetOnConditionFailed"] = false, ["oneSessionOnly"] = false, ["parentId"] = "", ["type"] = type, ["value"] = value, ["visibilityConditions"] = new JsonArray(),
    };
}

/// <summary>Stage 1b: the Workbench recipe. With the questline on it is locked until the last quest (its ProductionScheme reward finds the
/// recipe by area, end product, QuestComplete requirement and level). Inputs and tools are vanilla items from config.json.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 79)]
public class Crafts(ISptLogger<Crafts> logger, ModHelper modHelper, HideoutTable hideout, TemplateTable templates) : IOnLoad
{
    private static HideoutProduction? made;

    /// <summary>Called when the questline fails to load: nothing may stay locked behind quests that don't exist.</summary>
    public static void UnlockAll()
    {
        if (made is null) return;
        made.Locked = false;
        made.Requirements?.RemoveAll(r => r.Type == "QuestComplete");
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Craft.Enabled) { logger.Info("[RC car] the Workbench recipe is off in config.json"); return Task.CompletedTask; }
        if (!templates.Items.ContainsKey(Ids.Car)) return Task.CompletedTask;
        var recipes = hideout.Production?.Recipes;
        if (recipes is null) { logger.Warning("[RC car] hideout recipe list not found; no recipe"); return Task.CompletedTask; }
        var req = new List<Requirement> { new() { Type = "Area", AreaType = Quests.WorkbenchArea, RequiredLevel = cfg.Craft.AreaLevel } };
        foreach (var (tpl, count) in cfg.Craft.Inputs)
        {
            if (!MongoId.IsValidMongoId(tpl) || !templates.Items.ContainsKey(new MongoId(tpl))) { logger.Warning($"[RC car] config.json craft input {tpl} is not an item; no recipe"); return Task.CompletedTask; }
            req.Add(new Requirement { Type = "Item", TemplateId = new MongoId(tpl), Count = Math.Max(1, count), IsFunctional = false, IsEncoded = false, IsSpawnedInSession = false });
        }
        foreach (var tool in cfg.Craft.Tools)
        {
            if (!MongoId.IsValidMongoId(tool) || !templates.Items.ContainsKey(new MongoId(tool))) { logger.Warning($"[RC car] config.json craft tool {tool} is not an item; no recipe"); return Task.CompletedTask; }
            req.Add(new Requirement { Type = "Tool", TemplateId = new MongoId(tool) });
        }
        var unlock = cfg.Questline ? Quests.UnlockQuest(dir) : null;
        if (unlock != null) req.Add(new Requirement { Type = "QuestComplete", QuestId = new MongoId(unlock) });
        var id = Ids.Of("craft|car");
        recipes.RemoveAll(r => r.Id == id);
        made = new HideoutProduction
        {
            Id = id, AreaType = HideoutAreas.Workbench, Requirements = req, ProductionTime = Math.Max(60, cfg.Craft.Hours * 3600), EndProduct = Ids.Car, Count = cfg.Craft.Count,
            IsEncoded = false, Locked = unlock != null, NeedFuelForAllProductionTime = false, Continuous = false, ProductionLimitCount = 0, IsCodeProduction = false,
        };
        recipes.Add(made);
        logger.Info($"[RC car] Workbench {cfg.Craft.AreaLevel} recipe: {cfg.Craft.Count} car in {cfg.Craft.Hours} h{(unlock != null ? ", unlocked by Special Delivery" : "")}");
        return Task.CompletedTask;
    }
}

public sealed class QuestFile
{
    [JsonPropertyName("quests")] public List<QuestDef> Quests { get; set; } = [];
}

public sealed class QuestDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("loyalty")] public int Loyalty { get; set; }
    [JsonPropertyName("xp")] public int Xp { get; set; }
    [JsonPropertyName("roubles")] public int Roubles { get; set; }
    [JsonPropertyName("rewardCars")] public int RewardCars { get; set; }
    [JsonPropertyName("unlocks")] public bool Unlocks { get; set; }
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
    [JsonPropertyName("weapons")] public List<string>? Weapons { get; set; }
    [JsonPropertyName("items")] public List<string>? Items { get; set; }
    [JsonPropertyName("foundInRaid")] public bool FoundInRaid { get; set; }
}
