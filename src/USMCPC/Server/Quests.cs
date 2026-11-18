using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

namespace USMCPC.Server;

/// <summary>
/// Widowz 2026-11-16: "Make a questline for jackal where you steal a shipment of the rigs from one of the bosses and try to get it before the
/// scavs do". Four Jackal quests from db/quests.json (Paper Trail, Under New Management, Before the Scavs Do, Field Test), built in the
/// database's own JSON shape and read back through SPT's converters (same route as WWC Jobs). The carrier loadouts unlock with the third
/// quest, the loose parts with the fourth, through The Jackal's quest assort (config lockBehindQuests). Runs at TraderRegistration + 157,
/// after this mod's own offers (+ 156). The shipment case's spawn in the Customs dorms is added by <see cref="ShipmentSpawn"/>.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 157)]
public class Quests(ISptLogger<Quests> logger, ModHelper modHelper, CustomQuestService questService, LocaleTable locales, TradersTable traders,
    TemplateTable templates) : IOnLoad
{
    public const string Jackal = "672b4a010000000000000001";
    private const string Roubles = "5449016a4bdc2d6f028b456f", Marker = "5991b51486f77447b112d44f";

    public static MongoId Id(string key) => PatchSlot.Hash("usmcpc|" + key);
    public static string Sid(string key) => Id(key).ToString();

    public static readonly JsonSerializerOptions JsonOptions = Options();
    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true };
        foreach (var c in new SptJsonConverterRegistrator().GetJsonConverters()) o.Converters.Add(c);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    private static readonly Dictionary<string, (string Id, string[] Names)> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["any"] = ("any", []), ["Customs"] = ("56f40101d2720b2a4d8b45d6", ["bigmap"]), ["Woods"] = ("5704e3c2d2720bac5b8b4567", ["Woods"]),
        ["Shoreline"] = ("5704e554d2720bac5b8b456e", ["Shoreline"]), ["Interchange"] = ("5714dbc024597771384a510d", ["Interchange"]),
        ["Reserve"] = ("5704e5fad2720bc05b8b4567", ["RezervBase"]), ["Lighthouse"] = ("5704e4dad2720bb55b8b4567", ["Lighthouse"]),
        ["Streets"] = ("5714dc692459777137212e12", ["TarkovStreets"]),
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { logger.Error($"[{ModInfo.Name}] questline not added, everything stays on sale: {ex}"); }
        return Task.CompletedTask;
    }

    private void Run()
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = File.Exists(Path.Combine(dir, "config.json")) ? modHelper.GetJsonDataFromFile<ModConfig>(dir, "config.json") ?? new ModConfig() : new ModConfig();
        if (!cfg.Enabled || !cfg.QuestlineEnabled) { logger.Info($"[{ModInfo.Name}] questline switched off in config.json"); return; }
        var file = JsonSerializer.Deserialize<QuestFile>(File.ReadAllText(Path.Combine(dir, "db", "quests.json")), JsonOptions) ?? throw new InvalidDataException("quests.json is empty");
        var jackal = traders.GetTrader(new MongoId(Jackal));
        if (jackal?.Base is null || jackal.Assort?.Items is null) { logger.Warning($"[{ModInfo.Name}] The Jackal is not installed; no questline"); return; }
        var assort = jackal.Assort;
        var questAssort = jackal.QuestAssort;
        foreach (var k in new[] { "started", "success", "fail" }) if (!questAssort.ContainsKey(k)) questAssort[k] = new Dictionary<MongoId, MongoId>();
        var carrier = Id("carrier");
        var roots = assort.Items.Where(i => i.ParentId == "hideout").ToList();
        var loadouts = roots.Where(r => r.Template == carrier).ToList();
        var parts = roots.Where(r => r.Template != carrier && IsOurs(r.Template)).ToList();
        var langs = locales.Global.Keys.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        int made = 0, locked = 0;
        string? previous = null;
        var report = new List<string>();
        foreach (var q in file.Quests)
        {
            var offers = q.Opens == "loadouts" ? loadouts : q.Opens == "parts" ? parts : [];
            var loyalty = LoyaltyFor(q.Loyalty, q.Level, jackal.Base);
            var (quest, text) = Build(q, previous, loyalty, offers, assort);
            var result = questService.CreateQuest(new NewQuestDetails { NewQuest = quest, Locales = langs.ToDictionary(l => l, _ => new Dictionary<string, string>(text)), LockedToSide = null });
            if (!result.Success) { logger.Error($"[{ModInfo.Name}] \"{q.Title}\" not added: {string.Join(", ", result.Errors)}"); break; }
            made++;
            int n = 0;
            if (cfg.LockBehindQuests)
                foreach (var o in offers) { questAssort["success"][o.Id] = quest.Id; n++; }
            locked += n;
            report.Add($"{q.Title} (lvl {q.Level}, LL{loyalty}{(n > 0 ? $", opens {n}" : "")})");
            previous = quest.Id.ToString();
        }
        logger.Info($"[{ModInfo.Name}] questline: {string.Join(" > ", report)}");
        logger.Success($"[{ModInfo.Name}] {made} Jackal quest(s), {locked} offer(s) wait for them{(cfg.LockBehindQuests ? "" : " (lockBehindQuests is off: everything sells straight away)")}");
    }

    private bool IsOurs(MongoId tpl) => templates.Items.TryGetValue(tpl, out var t) && (t.Name ?? "").StartsWith("usmcpc_", StringComparison.Ordinal);

    /// <summary>
    /// The Jackal loyalty level a quest needs before it can be started (Widowz 2026-11-17: every Jackal quest needs a player level and a Jackal
    /// loyalty level). "loyalty" in quests.json, else the highest loyalty level his own table opens at the quest's player level, so the gate never
    /// asks for a level the quest itself doesn't. His campaign has no loyalty gate and earns the standing, so no chain can lock itself out.
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

    private (Quest, Dictionary<string, string>) Build(QuestDef q, string? previous, int loyalty, List<Item> unlocks, TraderAssort assort)
    {
        var key = q.Key.ToLowerInvariant();
        string Cid(string part) => Sid($"quest:{key}:{part}");
        var questId = Sid($"quest:{key}");
        if (!Maps.TryGetValue(q.Map ?? "any", out var map)) throw new InvalidDataException($"unknown map {q.Map}");
        var text = new Dictionary<string, string>();
        var finish = new JsonArray();
        var started = new JsonArray();
        bool kills = false, explore = false, pickup = false;
        string? findId = null;
        for (var i = 0; i < q.Objectives.Count; i++)
        {
            var o = q.Objectives[i];
            var k = $"obj:{o.Key.ToLowerInvariant()}";
            switch (o.Type.ToLowerInvariant())
            {
                case "kill":
                {
                    var roles = o.Roles ?? [];
                    var target = roles.Count > 0 ? "Savage" : o.Target ?? "Savage";
                    var conditions = new JsonArray();
                    var names = (o.Maps ?? []).SelectMany(m => Maps[m].Names).Distinct().ToArray();
                    if (names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Cid(k + ":location"), ["target"] = Strings(names) });
                    conditions.Add(new JsonObject
                    {
                        ["bodyPart"] = new JsonArray(), ["compareMethod"] = ">=", ["conditionType"] = "Kills", ["daytime"] = new JsonObject { ["from"] = 0, ["to"] = 0 },
                        ["distance"] = new JsonObject { ["compareMethod"] = ">=", ["value"] = 0 }, ["dynamicLocale"] = false, ["enemyEquipmentExclusive"] = new JsonArray(),
                        ["enemyEquipmentInclusive"] = new JsonArray(), ["enemyHealthEffects"] = new JsonArray(), ["id"] = Cid(k + ":kills"), ["resetOnSessionEnd"] = false,
                        ["savageRole"] = Strings(roles), ["target"] = target, ["value"] = 1, ["weapon"] = new JsonArray(), ["weaponCaliber"] = new JsonArray(),
                        ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray(),
                    });
                    if (o.Wearing is { Count: > 0 })
                        conditions.Add(new JsonObject
                        {
                            ["IncludeNotEquippedItems"] = false, ["conditionType"] = "Equipment", ["dynamicLocale"] = false, ["equipmentExclusive"] = new JsonArray(),
                            ["equipmentInclusive"] = new JsonArray(o.Wearing.Select(w => (JsonNode?)new JsonArray(Sid(w))).ToArray()), ["id"] = Cid(k + ":equipment"),
                        });
                    finish.Add(Counter(Cid(k), Cid(k + ":counter"), i, "Elimination", Math.Max(1, o.Count), conditions));
                    kills = true;
                    break;
                }
                case "mark":
                {
                    finish.Add(new JsonObject
                    {
                        ["conditionType"] = "PlaceBeacon", ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid(k), ["index"] = i, ["isEncoded"] = false,
                        ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = false, ["parentId"] = "", ["plantTime"] = Math.Max(1, o.Seconds),
                        ["target"] = new JsonArray(Marker), ["value"] = 1, ["visibilityConditions"] = new JsonArray(), ["zoneId"] = o.Zone,
                    });
                    started.Add(ItemReward(Cid("marker"), [Cid("marker-item")], Marker, 1));
                    explore = true;
                    break;
                }
                case "visit":
                    finish.Add(Counter(Cid(k), Cid(k + ":counter"), i, "Exploration", 1, new JsonArray(
                        new JsonObject { ["conditionType"] = "VisitPlace", ["dynamicLocale"] = false, ["id"] = Cid(k + ":visit"), ["target"] = o.Zone, ["value"] = 1 })));
                    explore = true;
                    break;
                case "extract":
                {
                    var conditions = new JsonArray();
                    var names = (o.Maps ?? []).SelectMany(m => Maps[m].Names).Distinct().ToArray();
                    if (names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Cid(k + ":location"), ["target"] = Strings(names) });
                    conditions.Add(new JsonObject { ["conditionType"] = "ExitStatus", ["dynamicLocale"] = false, ["id"] = Cid(k + ":status"), ["status"] = Strings(["Survived", "Runner", "Transit"]) });
                    var c = Counter(Cid(k), Cid(k + ":counter"), i, "Completion", 1, conditions);
                    // Like the game's own case quests: the extract line shows once the item is picked up.
                    if (findId != null) c["visibilityConditions"] = new JsonArray(new JsonObject { ["conditionType"] = "CompleteCondition", ["id"] = Cid(k + ":after-find"), ["target"] = findId });
                    finish.Add(c);
                    break;
                }
                case "find":
                {
                    findId = Cid(k);
                    finish.Add(new JsonObject
                    {
                        ["conditionType"] = "FindItem", ["countInRaid"] = false, ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = findId, ["index"] = i,
                        ["isEncoded"] = false, ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = false, ["parentId"] = "", ["target"] = new JsonArray(Sid(o.Item!)),
                        ["value"] = 1, ["visibilityConditions"] = new JsonArray(),
                    });
                    pickup = true;
                    break;
                }
                case "handover":
                {
                    var visibility = new JsonArray();
                    if (findId != null) visibility.Add(new JsonObject { ["conditionType"] = "CompleteCondition", ["id"] = Cid(k + ":after-find"), ["target"] = findId });
                    finish.Add(new JsonObject
                    {
                        ["conditionType"] = "HandoverItem", ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid(k), ["index"] = i, ["isEncoded"] = false,
                        ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = o.FoundInRaid, ["parentId"] = "",
                        ["target"] = Strings((o.Items ?? []).Select(Sid)), ["value"] = Math.Max(1, o.Count), ["visibilityConditions"] = visibility,
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
        success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid("standing"), ["isHidden"] = false, ["target"] = Jackal, ["type"] = "TraderStanding", ["unknown"] = false, ["value"] = 0.03 });
        if (q.Roubles > 0) success.Add(ItemReward(Cid("money"), [Cid("money-item")], Roubles, q.Roubles));
        if (!string.IsNullOrEmpty(q.RewardCarrier)) success.Add(CarrierReward(Cid("carrier"), q.RewardCarrier!, assort));
        foreach (var u in unlocks.GroupBy(u => u.Template).Select(g => g.First()).Take(3))
        {
            var item = Cid($"unlock-item:{u.Template}");
            success.Add(new JsonObject
            {
                ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid($"unlock:{u.Template}"), ["isHidden"] = false,
                ["items"] = new JsonArray(new JsonObject { ["_id"] = item, ["_tpl"] = u.Template.ToString() }), ["loyaltyLevel"] = 1,
                ["target"] = item, ["traderId"] = Jackal, ["type"] = "AssortmentUnlock", ["unknown"] = false,
            });
        }
        var kinds = new[] { kills, explore, pickup }.Count(b => b);
        var type = kinds > 1 ? "Multi" : kills ? "Elimination" : explore ? "Exploration" : "PickUp";
        var quest = new JsonObject
        {
            ["QuestName"] = q.Title, ["_id"] = questId, ["acceptPlayerMessage"] = $"{questId} acceptPlayerMessage", ["acceptanceAndFinishingSource"] = "eft", ["arenaLocations"] = new JsonArray(),
            ["canShowNotificationsInGame"] = true, ["changeQuestMessageText"] = $"{questId} changeQuestMessageText", ["completePlayerMessage"] = $"{questId} completePlayerMessage",
            ["conditions"] = new JsonObject { ["AvailableForFinish"] = finish, ["AvailableForStart"] = start, ["Fail"] = new JsonArray() },
            ["declinePlayerMessage"] = $"{questId} declinePlayerMessage", ["description"] = $"{questId} description", ["failMessageText"] = $"{questId} failMessageText", ["gameModes"] = new JsonArray(),
            ["image"] = "/files/quest/icon/" + q.Icon, ["instantComplete"] = false, ["isKey"] = false, ["location"] = map.Id, ["name"] = $"{questId} name", ["note"] = $"{questId} note",
            ["progressSource"] = "eft", ["rankingModes"] = new JsonArray(), ["restartable"] = false,
            ["rewards"] = new JsonObject { ["Fail"] = new JsonArray(), ["Started"] = started, ["Success"] = success },
            ["secretQuest"] = false, ["side"] = "Pmc", ["startedMessageText"] = $"{questId} startedMessageText", ["status"] = 0, ["successMessageText"] = $"{questId} successMessageText",
            ["traderId"] = Jackal, ["type"] = type,
        };
        text[$"{questId} name"] = q.Title;
        text[$"{questId} description"] = q.Story.Trim();
        text[$"{questId} note"] = "";
        text[$"{questId} startedMessageText"] = q.Started;
        text[$"{questId} successMessageText"] = q.Done;
        text[$"{questId} failMessageText"] = "It'll keep. Come back when you're ready.";
        text[$"{questId} changeQuestMessageText"] = "Something's changed. Have another look.";
        text[$"{questId} acceptPlayerMessage"] = "Alright. I'll handle it.";
        text[$"{questId} completePlayerMessage"] = "Done. It's all there.";
        text[$"{questId} declinePlayerMessage"] = "Not right now.";
        return (quest.Deserialize<Quest>(JsonOptions)!, text);
    }

    /// <summary>A complete carrier as a reward: the loadout offer's tree (plates, soft armor, parts) with new ids.</summary>
    private static JsonObject CarrierReward(string id, string loadout, TraderAssort assort)
    {
        var root = assort.Items.First(i => i.Id == Id($"offer|carrier|{loadout}"));
        var kids = assort.Items.Where(i => i.ParentId == root.Id.ToString()).ToList();
        var rootId = Sid($"reward|carrier|{loadout}");
        var items = new JsonArray(new JsonObject { ["_id"] = rootId, ["_tpl"] = root.Template.ToString(), ["upd"] = new JsonObject { ["StackObjectsCount"] = 1 } });
        foreach (var k in kids)
        {
            var row = new JsonObject { ["_id"] = Sid($"reward|carrier|{loadout}|{k.SlotId}"), ["_tpl"] = k.Template.ToString(), ["parentId"] = rootId, ["slotId"] = k.SlotId };
            if (k.Upd?.Repairable is { } rep) row["upd"] = new JsonObject { ["Repairable"] = new JsonObject { ["Durability"] = rep.Durability, ["MaxDurability"] = rep.MaxDurability } };
            items.Add(row);
        }
        return new JsonObject
        {
            ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = false, ["gameMode"] = Modes(), ["id"] = id, ["isEncoded"] = false, ["isHidden"] = false,
            ["items"] = items, ["target"] = rootId, ["type"] = "Item", ["unknown"] = false, ["value"] = 1,
        };
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

/// <summary>
/// The shipment case in the Customs dorms: a forced loose-loot spawn on bigmap, a copy of the game's own Bank case spawn in the dorms
/// ("quest_case203") moved to the spot in db/quests.json. The case is a quest item, so only a character with "Before the Scavs Do" can pick it up.
/// Runs at Preload + 68, after the items are registered (+ 67).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 68)]
public class ShipmentSpawn(ISptLogger<ShipmentSpawn> logger, ModHelper modHelper, LocationTable locations) : IOnLoad
{
    private const string BankCase = "590dde5786f77405e71908b2";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dir = PartFolder.Of(modHelper);
            var cfg = File.Exists(Path.Combine(dir, "config.json")) ? modHelper.GetJsonDataFromFile<ModConfig>(dir, "config.json") ?? new ModConfig() : new ModConfig();
            if (!cfg.Enabled || !cfg.QuestlineEnabled) return Task.CompletedTask;
            var file = JsonSerializer.Deserialize<QuestFile>(File.ReadAllText(Path.Combine(dir, "db", "quests.json")), Quests.JsonOptions);
            var spot = file?.Shipment ?? throw new InvalidDataException("quests.json has no shipment spot");
            var location = locations.GetDictionary().Select(p => p.Value).FirstOrDefault(l => string.Equals(l?.Base?.Id, spot.Map, StringComparison.OrdinalIgnoreCase));
            if (location?.LooseLoot is null) { logger.Warning($"[{ModInfo.Name}] map {spot.Map} not found; the shipment case will not spawn"); return Task.CompletedTask; }
            var caseTpl = Quests.Sid("shipment_case");
            location.LooseLoot.AddTransformer(loot =>
            {
                try
                {
                    if (loot?.SpawnpointsForced is null) return loot;
                    var forced = loot.SpawnpointsForced.ToList();
                    if (forced.Any(f => f.Template?.Items?.Any(i => i.Template == caseTpl) == true)) return loot;
                    var donor = forced.First(f => f.Template?.Items?.Any(i => i.Template == BankCase) == true);
                    var node = JsonSerializer.SerializeToNode(donor, Quests.JsonOptions)!.AsObject();
                    var t = node["template"]!.AsObject();
                    var rootId = Quests.Sid("shipment_case|spawn");
                    t["Id"] = $"usmcpc_shipment_case [{rootId}]";
                    t["Position"] = new JsonObject { ["x"] = spot.Position[0], ["y"] = spot.Position[1], ["z"] = spot.Position[2] };
                    if (spot.Rotation is { Length: 3 } r) t["Rotation"] = new JsonObject { ["x"] = r[0], ["y"] = r[1], ["z"] = r[2] };
                    else
                    {
                        var rotSource = loot.Spawnpoints?.FirstOrDefault(s => s.Template?.Id?.StartsWith(spot.CopyRotationFrom + " [", StringComparison.Ordinal) == true);
                        if (rotSource?.Template is not null) t["Rotation"] = JsonSerializer.SerializeToNode(rotSource.Template, Quests.JsonOptions)!["Rotation"]!.DeepClone();
                    }
                    t["useGravity"] = spot.UseGravity;
                    t["Root"] = rootId;
                    t["Items"] = new JsonArray(new JsonObject { ["_id"] = rootId, ["_tpl"] = caseTpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = 1 } });
                    node["locationId"] = $"({spot.Position[0]}, {spot.Position[1]}, {spot.Position[2]})";
                    forced.Add((Spawnpoint)node.Deserialize(donor.GetType(), Quests.JsonOptions)!);
                    loot.SpawnpointsForced = forced;
                }
                catch (Exception ex) { logger.Warning($"[{ModInfo.Name}] shipment case spawn not added: {ex.Message}"); }
                return loot;
            });
            logger.Info($"[{ModInfo.Name}] shipment case spawn queued on {spot.Map} at ({string.Join(", ", spot.Position)})");
        }
        catch (Exception ex) { logger.Warning($"[{ModInfo.Name}] shipment case spawn not added: {ex.Message}"); }
        return Task.CompletedTask;
    }
}

public sealed class QuestFile
{
    [JsonPropertyName("quests")] public List<QuestDef> Quests { get; set; } = [];
    [JsonPropertyName("shipment")] public ShipmentDef? Shipment { get; set; }
}

public sealed class ShipmentDef
{
    [JsonPropertyName("map")] public string Map { get; set; } = "bigmap";
    [JsonPropertyName("position")] public double[] Position { get; set; } = [0, 0, 0];
    /// <summary>Euler angles in degrees; when set, copyRotationFrom is not used.</summary>
    [JsonPropertyName("rotation")] public double[]? Rotation { get; set; }
    [JsonPropertyName("copyRotationFrom")] public string CopyRotationFrom { get; set; } = "";
    [JsonPropertyName("useGravity")] public bool UseGravity { get; set; }
}

public sealed class QuestDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    /// <summary>The Jackal loyalty level needed to start it (1-4); left out = the level his loyalty table opens at "level".</summary>
    [JsonPropertyName("loyalty")] public int? Loyalty { get; set; }
    [JsonPropertyName("xp")] public int Xp { get; set; }
    [JsonPropertyName("roubles")] public int Roubles { get; set; }
    [JsonPropertyName("rewardCarrier")] public string? RewardCarrier { get; set; }
    [JsonPropertyName("opens")] public string? Opens { get; set; }
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
    [JsonPropertyName("roles")] public List<string>? Roles { get; set; }
    [JsonPropertyName("wearing")] public List<string>? Wearing { get; set; }
    [JsonPropertyName("zone")] public string? Zone { get; set; }
    [JsonPropertyName("seconds")] public int Seconds { get; set; } = 10;
    [JsonPropertyName("item")] public string? Item { get; set; }
    [JsonPropertyName("items")] public List<string>? Items { get; set; }
    [JsonPropertyName("foundInRaid")] public bool FoundInRaid { get; set; }
}
