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
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

namespace FTHS.Server;

/// <summary>
/// Widowz 2026-11-16: "Create a question for the FTHS helmets as well", then "No need for a boss make a questline that makes sense".
/// Three Jackal quests from db/quests.json: Trial Batch (Woods: the old USEC camp, opens the bump helmet), Lights Out (any map at night
/// wearing an FTHS helmet, opens the loose parts) and The Third Crate (Lighthouse: the old USEC cottage, opens the ballistic helmet and the
/// covers). Built in the database's own JSON shape and read back through SPT's converters, the same route as the carrier questline and
/// WWC Jobs. Offers wait in The Jackal's quest assort under "success" (config lockBehindQuests). Runs at TraderRegistration + 121, right
/// after this mod's offers (+ 120). Every id is made from fixed text, so it never changes once a profile holds it; item and offer ids
/// are untouched.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 121)]
public class Quests(ISptLogger<Quests> logger, ModHelper modHelper, CustomQuestService questService, LocaleTable locales, TradersTable traders) : IOnLoad
{
    public const string Jackal = "672b4a010000000000000001";
    private const string Roubles = "5449016a4bdc2d6f028b456f", Marker = "5991b51486f77447b112d44f";

    /// <summary>Same rule as the item ids (tools/make_items.py): sha256("fths|" + key), first 24 hex.</summary>
    public static string Sid(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fths|" + key))).ToLowerInvariant()[..24];

    private static readonly JsonSerializerOptions JsonOptions = Options();
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
        var ours = ItemRegistration.RegisteredIds.Select(i => i.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = assort.Items.Where(i => i.ParentId == "hideout" && ours.Contains(i.Template.ToString())).ToList();
        if (roots.Count == 0) { logger.Warning($"[{ModInfo.Name}] The Jackal sells no FTHS items (offers skipped?); no questline"); return; }
        var questAssort = jackal.QuestAssort;
        foreach (var k in new[] { "started", "success", "fail" }) if (!questAssort.ContainsKey(k)) questAssort[k] = new Dictionary<MongoId, MongoId>();
        var alreadyLocked = questAssort.Values.SelectMany(d => d.Keys).ToHashSet();

        // Check the whole chain before adding any of it: a quest that can't be built would leave the ones after it unreachable.
        foreach (var q in file.Quests)
        {
            if (string.IsNullOrWhiteSpace(q.Key)) throw new InvalidDataException("a quest has no key");
            if (!Maps.ContainsKey(q.Map ?? "any")) throw new InvalidDataException($"{q.Title}: unknown map {q.Map}");
            if (q.Reward is { Length: > 0 } r && !roots.Any(o => o.Template.ToString() == Sid(r))) throw new InvalidDataException($"{q.Title}: no Jackal offer for the reward {r}");
        }

        var langs = locales.Global.Keys.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        int made = 0, locked = 0;
        string? previous = null;
        var report = new List<string>();
        foreach (var q in file.Quests)
        {
            var wanted = (q.Opens ?? []).Select(Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var offers = roots.Where(o => wanted.Contains(o.Template.ToString())).ToList();
            var loyalty = LoyaltyFor(q.Loyalty, q.Level, jackal.Base);
            var (quest, text) = Build(q, previous, loyalty, cfg.LockBehindQuests ? offers : [], assort, roots);
            var result = questService.CreateQuest(new NewQuestDetails { NewQuest = quest, Locales = langs.ToDictionary(l => l, _ => new Dictionary<string, string>(text)), LockedToSide = null });
            if (!result.Success) { logger.Error($"[{ModInfo.Name}] \"{q.Title}\" not added, so it and the quests after it stay out and their items stay on sale: {string.Join(", ", result.Errors)}"); break; }
            made++;
            int n = 0, skipped = 0;
            if (cfg.LockBehindQuests)
                foreach (var o in offers)
                {
                    if (alreadyLocked.Contains(o.Id)) { skipped++; continue; }
                    questAssort["success"][o.Id] = quest.Id;
                    n++;
                }
            locked += n;
            report.Add($"{q.Title} (lvl {q.Level}, LL{loyalty}{(n > 0 ? $", opens {n}" : "")}{(skipped > 0 ? $", {skipped} locked elsewhere" : "")})");
            previous = quest.Id.ToString();
        }
        logger.Info($"[{ModInfo.Name}] questline: {string.Join(" > ", report)}");
        logger.Success($"[{ModInfo.Name}] {made} Jackal quest(s), {locked} offer(s) wait for them{(cfg.LockBehindQuests ? "" : " (lockBehindQuests is off: everything sells straight away)")}");
    }

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

    private static (Quest, Dictionary<string, string>) Build(QuestDef q, string? previous, int loyalty, List<Item> unlocks, TraderAssort assort, List<Item> roots)
    {
        var key = q.Key.ToLowerInvariant();
        string Cid(string part) => Sid($"quest:{key}:{part}");
        var questId = Sid($"quest:{key}");
        var map = Maps[q.Map ?? "any"];
        var text = new Dictionary<string, string>();
        var finish = new JsonArray();
        var started = new JsonArray();
        var condIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool kills = false, explore = false, survive = false;
        for (var i = 0; i < q.Objectives.Count; i++)
        {
            var o = q.Objectives[i];
            var k = $"obj:{o.Key.ToLowerInvariant()}";
            var visibility = new JsonArray();
            if (!string.IsNullOrEmpty(o.After))
                visibility.Add(new JsonObject { ["conditionType"] = "CompleteCondition", ["id"] = Cid(k + ":after"), ["target"] = condIds.TryGetValue(o.After!, out var a) ? a : throw new InvalidDataException($"{q.Title}: {o.Key} waits for unknown objective {o.After}") });
            switch (o.Type.ToLowerInvariant())
            {
                case "kill":
                {
                    var conditions = new JsonArray();
                    var names = (o.Maps ?? []).SelectMany(m => Maps[m].Names).Distinct().ToArray();
                    if (names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Cid(k + ":location"), ["target"] = Strings(names) });
                    var day = o.Daytime is { Count: 2 } ? o.Daytime : [0, 0];
                    conditions.Add(new JsonObject
                    {
                        ["bodyPart"] = new JsonArray(), ["compareMethod"] = ">=", ["conditionType"] = "Kills", ["daytime"] = new JsonObject { ["from"] = day[0], ["to"] = day[1] },
                        ["distance"] = new JsonObject { ["compareMethod"] = ">=", ["value"] = 0 }, ["dynamicLocale"] = false, ["enemyEquipmentExclusive"] = new JsonArray(),
                        ["enemyEquipmentInclusive"] = new JsonArray(), ["enemyHealthEffects"] = new JsonArray(), ["id"] = Cid(k + ":kills"), ["resetOnSessionEnd"] = false,
                        ["savageRole"] = new JsonArray(), ["target"] = o.Target ?? "Savage", ["value"] = 1, ["weapon"] = new JsonArray(), ["weaponCaliber"] = new JsonArray(),
                        ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray(),
                    });
                    if (o.Wearing is { Count: > 0 })   // outer list = any one of, inner list = all of (base-game shape)
                        conditions.Add(new JsonObject
                        {
                            ["IncludeNotEquippedItems"] = false, ["conditionType"] = "Equipment", ["dynamicLocale"] = false, ["equipmentExclusive"] = new JsonArray(),
                            ["equipmentInclusive"] = new JsonArray(o.Wearing.Select(w => (JsonNode?)new JsonArray(Sid(w))).ToArray()), ["id"] = Cid(k + ":equipment"),
                        });
                    finish.Add(Counter(Cid(k), Cid(k + ":counter"), i, "Elimination", Math.Max(1, o.Count), conditions, visibility));
                    kills = true;
                    break;
                }
                case "visit":
                {
                    var conditions = new JsonArray(new JsonObject { ["conditionType"] = "VisitPlace", ["dynamicLocale"] = false, ["id"] = Cid(k + ":place"), ["target"] = o.Zone, ["value"] = 1 });
                    finish.Add(Counter(Cid(k), Cid(k + ":counter"), i, "Exploration", 1, conditions, visibility));
                    explore = true;
                    break;
                }
                case "mark":
                {
                    var leave = string.Equals(o.Trigger, "leave", StringComparison.OrdinalIgnoreCase);
                    var node = new JsonObject
                    {
                        ["conditionType"] = leave ? "LeaveItemAtLocation" : "PlaceBeacon", ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Cid(k),
                        ["index"] = i, ["isEncoded"] = false, ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = false, ["parentId"] = "",
                        ["plantTime"] = Math.Max(1, o.Seconds), ["target"] = new JsonArray(Marker), ["value"] = 1, ["visibilityConditions"] = visibility, ["zoneId"] = o.Zone,
                    };
                    finish.Add(node);
                    started.Add(ItemReward(Cid(k + ":marker"), Cid(k + ":marker-item"), Marker, 1));
                    explore = true;
                    break;
                }
                case "extract":
                {
                    var conditions = new JsonArray(new JsonObject { ["conditionType"] = "ExitStatus", ["dynamicLocale"] = false, ["id"] = Cid(k + ":status"), ["status"] = Strings(["Survived", "Runner", "Transit"]) });
                    var names = (o.Maps ?? []).SelectMany(m => Maps[m].Names).Distinct().ToArray();
                    if (names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Cid(k + ":location"), ["target"] = Strings(names) });
                    finish.Add(Counter(Cid(k), Cid(k + ":counter"), i, "Completion", 1, conditions, visibility));
                    survive = true;
                    break;
                }
                default: throw new InvalidDataException($"{q.Title}: unknown objective type {o.Type}");
            }
            var id = finish[^1]!["id"]!.GetValue<string>();
            condIds[o.Key] = id;
            text[id] = o.Text;
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
        if (q.Roubles > 0) success.Add(ItemReward(Cid("money"), Cid("money-item"), Roubles, q.Roubles));
        if (q.Reward is { Length: > 0 }) success.Add(HelmetReward(Cid("helmet"), q.Reward, assort, roots));
        foreach (var u in unlocks.GroupBy(u => u.Template).Select(g => g.First()).Take(4))
        {
            var item = Cid($"unlock-item:{u.Template}");
            success.Add(new JsonObject
            {
                ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Cid($"unlock:{u.Template}"), ["isHidden"] = false,
                ["items"] = new JsonArray(new JsonObject { ["_id"] = item, ["_tpl"] = u.Template.ToString() }), ["loyaltyLevel"] = 1,
                ["target"] = item, ["traderId"] = Jackal, ["type"] = "AssortmentUnlock", ["unknown"] = false,
            });
        }
        var kinds = new[] { kills, explore, survive }.Count(b => b);
        var type = kinds > 1 ? "Multi" : kills ? "Elimination" : explore ? "Exploration" : "Completion";
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

    /// <summary>A complete helmet as a reward: its Jackal offer's tree (plates and fitted parts) with new ids made from fixed text.</summary>
    private static JsonObject HelmetReward(string id, string key, TraderAssort assort, List<Item> roots)
    {
        var root = roots.First(o => o.Template.ToString() == Sid(key));
        var rootId = Sid($"reward|{key}");
        var rootRow = new JsonObject { ["_id"] = rootId, ["_tpl"] = root.Template.ToString(), ["upd"] = new JsonObject { ["StackObjectsCount"] = 1 } };
        if (root.Upd?.Repairable is { } rr) rootRow["upd"]!["Repairable"] = new JsonObject { ["Durability"] = rr.Durability, ["MaxDurability"] = rr.MaxDurability };
        var items = new JsonArray(rootRow);
        var queue = new Queue<(string From, string To)>();
        queue.Enqueue((root.Id.ToString(), rootId));
        while (queue.Count > 0)
        {
            var (from, to) = queue.Dequeue();
            foreach (var kid in assort.Items.Where(i => i.ParentId == from))
            {
                var kidId = Sid($"reward|{key}|{to}|{kid.SlotId}");
                var row = new JsonObject { ["_id"] = kidId, ["_tpl"] = kid.Template.ToString(), ["parentId"] = to, ["slotId"] = kid.SlotId };
                if (kid.Upd?.Repairable is { } rep) row["upd"] = new JsonObject { ["Repairable"] = new JsonObject { ["Durability"] = rep.Durability, ["MaxDurability"] = rep.MaxDurability } };
                items.Add(row);
                queue.Enqueue((kid.Id.ToString(), kidId));
            }
        }
        return new JsonObject
        {
            ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = false, ["gameMode"] = Modes(), ["id"] = id, ["isEncoded"] = false, ["isHidden"] = false,
            ["items"] = items, ["target"] = rootId, ["type"] = "Item", ["unknown"] = false, ["value"] = 1,
        };
    }

    private static JsonArray Modes() => new("regular", "pve");
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject ItemReward(string id, string itemId, string tpl, int count) => new()
    {
        ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = false, ["gameMode"] = Modes(), ["id"] = id, ["isEncoded"] = false, ["isHidden"] = false,
        ["items"] = new JsonArray(new JsonObject { ["_id"] = itemId, ["_tpl"] = tpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = count } }),
        ["target"] = itemId, ["type"] = "Item", ["unknown"] = false, ["value"] = count,
    };

    private static JsonObject Counter(string id, string counterId, int index, string type, int value, JsonArray conditions, JsonArray visibility) => new()
    {
        ["completeInSeconds"] = 0, ["conditionType"] = "CounterCreator", ["counter"] = new JsonObject { ["conditions"] = conditions, ["id"] = counterId },
        ["doNotResetIfCounterCompleted"] = false, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = id, ["index"] = index, ["isNecessary"] = false,
        ["isResetOnConditionFailed"] = false, ["oneSessionOnly"] = false, ["parentId"] = "", ["type"] = type, ["value"] = value, ["visibilityConditions"] = visibility,
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
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    /// <summary>The Jackal loyalty level needed to start it (1-4); left out = the level his loyalty table opens at "level".</summary>
    [JsonPropertyName("loyalty")] public int? Loyalty { get; set; }
    [JsonPropertyName("xp")] public int Xp { get; set; }
    [JsonPropertyName("roubles")] public int Roubles { get; set; }
    [JsonPropertyName("opens")] public List<string>? Opens { get; set; }
    [JsonPropertyName("reward")] public string? Reward { get; set; }
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
    [JsonPropertyName("daytime")] public List<int>? Daytime { get; set; }
    [JsonPropertyName("wearing")] public List<string>? Wearing { get; set; }
    [JsonPropertyName("zone")] public string? Zone { get; set; }
    [JsonPropertyName("trigger")] public string? Trigger { get; set; }
    [JsonPropertyName("seconds")] public int Seconds { get; set; } = 10;
    [JsonPropertyName("after")] public string? After { get; set; }
}
