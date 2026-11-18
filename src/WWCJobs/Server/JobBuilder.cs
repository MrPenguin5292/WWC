using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils.Json;

namespace WWCJobs.Server;

internal sealed record Built(Quest Quest, Dictionary<string, string> Text, int Level, int Loyalty, List<string> Warnings);

/// <summary>Turns a jobs.json entry into a Jackal quest. Quests are written in the database's own JSON shape and read back through SPT's converters
/// (the same route as Jackal Origin). Every zone a job names is one the base game's own quests use, so no client mod is needed.</summary>
internal static class JobBuilder
{
    private const string Roubles = "5449016a4bdc2d6f028b456f", Marker = "5991b51486f77447b112d44f";

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Map names as players say them: quest location id, and the raid location names counters use.</summary>
    private static readonly Dictionary<string, (string Id, string[] Names)> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["any"] = ("any", []),
        ["Customs"] = ("56f40101d2720b2a4d8b45d6", ["bigmap"]),
        ["Woods"] = ("5704e3c2d2720bac5b8b4567", ["Woods"]),
        ["Shoreline"] = ("5704e554d2720bac5b8b456e", ["Shoreline"]),
        ["Interchange"] = ("5714dbc024597771384a510d", ["Interchange"]),
        ["Reserve"] = ("5704e5fad2720bc05b8b4567", ["RezervBase"]),
        ["Lighthouse"] = ("5704e4dad2720bb55b8b4567", ["Lighthouse"]),
        ["Streets"] = ("5714dc692459777137212e12", ["TarkovStreets"]),
        ["Streets of Tarkov"] = ("5714dc692459777137212e12", ["TarkovStreets"]),
        ["Ground Zero"] = ("653e6760052c01c1c805532f", ["Sandbox", "Sandbox_high"]),
        ["Factory"] = ("55f2d3fd4bdc2d5f408b4567", ["factory4_day", "factory4_night"]),
        ["Labs"] = ("5b0fc42d86f7744a585f9105", ["laboratory"]),
        ["The Lab"] = ("5b0fc42d86f7744a585f9105", ["laboratory"]),
    };

    private static readonly HashSet<string> KillTargets = new(StringComparer.Ordinal) { "Savage", "AnyPmc", "Any", "Usec", "Bear" };
    private static readonly HashSet<string> BodyParts = new(StringComparer.Ordinal) { "Head", "Chest", "Stomach", "LeftArm", "RightArm", "LeftLeg", "RightLeg" };

    /// <summary>
    /// The Jackal loyalty level a job needs before it can be started (every Jackal quest needs a player level and a Jackal
    /// loyalty level). "loyalty" in jobs.json, else the highest loyalty level his own table opens at the job's player level, so the gate never
    /// asks for a level the job itself doesn't. His campaign has no loyalty gate and earns the standing, so no chain can lock itself out.
    /// </summary>
    public static int LoyaltyFor(int? wanted, int level, TraderBase trader)
    {
        if (wanted is int w) return Math.Clamp(w, 1, 4);
        var ll = 1;
        var tiers = trader.LoyaltyLevels ?? [];
        for (var i = 1; i < tiers.Count && i < 4; i++)
            if ((tiers[i].MinLevel is { } m ? (int)m : 1) <= level) ll = i + 1;
        return ll;
    }

    public static Built Build(Job job, int step, int steps, JobsConfig cfg, List<MongoId> requires, List<(MongoId Offer, MongoId Template, int Level)> unlocks, IReadOnlyDictionary<MongoId, TemplateItem> items, TraderBase jackal)
    {
        var key = job.Id.ToLowerInvariant();
        string Id(string part) => Ids.Of($"job:{key}:{part}").ToString();
        var questId = Ids.Quest(job.Id).ToString();
        var text = new Dictionary<string, string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(job.Title)) throw new InvalidDataException("no title");
        if (!Maps.TryGetValue(job.Map ?? "any", out var questMap)) throw new InvalidDataException($"unknown map \"{job.Map}\"");
        if (job.Objectives.Count == 0) throw new InvalidDataException("no objectives");

        List<string> Resolve(IEnumerable<string> wanted, string what)
        {
            var found = new List<string>();
            foreach (var w in wanted ?? [])
            {
                if (w.StartsWith("parent:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsId(w[7..].Trim())) { warnings.Add($"{what}: \"{w}\" is not a class id"); continue; }
                    var parent = new MongoId(w[7..].Trim());
                    var kids = items.Values.Where(t => t.Parent == parent && t.Type == "Item").Select(t => t.Id.ToString()).ToList();
                    if (kids.Count == 0) warnings.Add($"{what}: nothing in class {w[7..]}");
                    found.AddRange(kids);
                }
                else if (IsId(w) && items.ContainsKey(new MongoId(w))) found.Add(w);
                else warnings.Add($"{what}: item {w} is not in the database, left out");
            }
            return found.Distinct().ToList();
        }

        string[] MapNames(List<string> maps)
        {
            var names = new List<string>();
            foreach (var m in maps ?? [])
            {
                if (!Maps.TryGetValue(m, out var map)) throw new InvalidDataException($"unknown map \"{m}\"");
                names.AddRange(map.Names);
            }
            return names.Distinct().ToArray();
        }

        var finish = new JsonArray();
        var started = new JsonArray();
        var markers = new List<string>();
        var objKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool kills = false, explore = false, handover = false, extract = false;
        for (var i = 0; i < job.Objectives.Count; i++)
        {
            var o = job.Objectives[i];
            var ok = string.IsNullOrWhiteSpace(o.Key) ? $"#{i}" : o.Key.Trim();
            if (!objKeys.Add(ok)) throw new InvalidDataException($"objective key \"{ok}\" is used twice");
            var k = $"obj:{ok.ToLowerInvariant()}";
            var label = string.IsNullOrWhiteSpace(o.Text) ? throw new InvalidDataException($"objective {ok} has no text") : o.Text.Trim();
            switch ((o.Type ?? "").ToLowerInvariant())
            {
                case "handover":
                {
                    var tpls = Resolve(o.Items, $"objective {ok}");
                    if (tpls.Count == 0) throw new InvalidDataException($"objective {ok} has no item that exists");
                    finish.Add(Handover(Id(k), i, tpls, Math.Max(1, o.Count), o.FoundInRaid));
                    handover = true;
                    break;
                }
                case "kill":
                {
                    var roles = (o.Roles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
                    var target = roles.Count > 0 ? "Savage" : o.Target;
                    if (!KillTargets.Contains(target)) throw new InvalidDataException($"objective {ok}: kill target \"{o.Target}\" (use Savage, AnyPmc, Usec, Bear or Any)");
                    var parts = (o.BodyParts ?? []).Where(p => BodyParts.Contains(p)).ToList();
                    var wearing = o.Wearing is { Count: > 0 } ? Resolve(o.Wearing, $"objective {ok}") : null;
                    if (wearing is { Count: 0 }) throw new InvalidDataException($"objective {ok}: nothing to wear exists");
                    var weapons = o.Weapons is { Count: > 0 } ? Resolve(o.Weapons, $"objective {ok}") : new List<string>();
                    if (o.Weapons is { Count: > 0 } && weapons.Count == 0) throw new InvalidDataException($"objective {ok}: none of its weapons exist");
                    var day = o.Daytime is { Count: 2 } ? o.Daytime : [0, 0];
                    var conditions = new JsonArray();
                    var names = MapNames(o.Maps);
                    if (names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Id(k + ":location"), ["target"] = Strings(names) });
                    conditions.Add(new JsonObject
                    {
                        ["bodyPart"] = Strings(parts), ["compareMethod"] = ">=", ["conditionType"] = "Kills", ["daytime"] = new JsonObject { ["from"] = Math.Clamp(day[0], 0, 23), ["to"] = Math.Clamp(day[1], 0, 23) },
                        ["distance"] = new JsonObject { ["compareMethod"] = ">=", ["value"] = Math.Max(0, o.Distance) }, ["dynamicLocale"] = false, ["enemyEquipmentExclusive"] = new JsonArray(),
                        ["enemyEquipmentInclusive"] = new JsonArray(), ["enemyHealthEffects"] = new JsonArray(), ["id"] = Id(k + ":kills"), ["resetOnSessionEnd"] = false,
                        ["savageRole"] = Strings(roles), ["target"] = target, ["value"] = 1, ["weapon"] = Strings(weapons), ["weaponCaliber"] = Strings(o.Calibers ?? []),
                        ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray(),
                    });
                    if (wearing is not null)
                        conditions.Add(new JsonObject
                        {
                            ["IncludeNotEquippedItems"] = false, ["conditionType"] = "Equipment", ["dynamicLocale"] = false, ["equipmentExclusive"] = new JsonArray(),
                            ["equipmentInclusive"] = new JsonArray(wearing.Select(w => (JsonNode?)new JsonArray(w)).ToArray()), ["id"] = Id(k + ":equipment"),
                        });
                    finish.Add(Counter(Id(k), Id(k + ":counter"), i, "Elimination", Math.Max(1, o.Count), conditions));
                    kills = true;
                    break;
                }
                case "visit":
                {
                    if (string.IsNullOrWhiteSpace(o.Zone)) throw new InvalidDataException($"objective {ok} has no zone");
                    finish.Add(Counter(Id(k), Id(k + ":counter"), i, "Completion", 1,
                        new JsonArray(new JsonObject { ["conditionType"] = "VisitPlace", ["dynamicLocale"] = false, ["id"] = Id(k + ":visit"), ["target"] = o.Zone, ["value"] = 1 })));
                    explore = true;
                    break;
                }
                case "mark":
                {
                    if (string.IsNullOrWhiteSpace(o.Zone)) throw new InvalidDataException($"objective {ok} has no zone");
                    finish.Add(Placed(Id(k), i, "PlaceBeacon", Marker, o.Zone, Math.Max(1, o.Seconds)));
                    markers.Add(Id($"marker:{ok.ToLowerInvariant()}"));
                    explore = true;
                    break;
                }
                case "stash":
                {
                    if (string.IsNullOrWhiteSpace(o.Zone)) throw new InvalidDataException($"objective {ok} has no zone");
                    var tpls = Resolve(o.Items, $"objective {ok}");
                    if (tpls.Count == 0) throw new InvalidDataException($"objective {ok} has no item that exists");
                    finish.Add(Placed(Id(k), i, "LeaveItemAtLocation", tpls[0], o.Zone, Math.Max(1, o.Seconds)));
                    if (o.Given) started.Add(ItemReward(Id($"given:{ok.ToLowerInvariant()}"), [Id($"given-item:{ok.ToLowerInvariant()}")], tpls[0], 1));
                    explore = true;
                    break;
                }
                case "extract":
                {
                    var names = MapNames(o.Maps);
                    var conditions = new JsonArray();
                    if (names.Length > 0) conditions.Add(new JsonObject { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Id(k + ":location"), ["target"] = Strings(names) });
                    conditions.Add(new JsonObject { ["conditionType"] = "ExitStatus", ["dynamicLocale"] = false, ["id"] = Id(k + ":status"), ["status"] = new JsonArray("Survived") });
                    if (!string.IsNullOrWhiteSpace(o.Exit)) conditions.Add(new JsonObject { ["conditionType"] = "ExitName", ["dynamicLocale"] = false, ["exitName"] = o.Exit, ["id"] = Id(k + ":exitname") });
                    finish.Add(Counter(Id(k), Id(k + ":counter"), i, "Completion", Math.Max(1, o.Count), conditions));
                    extract = true;
                    break;
                }
                case "loyalty":
                case "skill":
                {
                    var isLoyalty = string.Equals(o.Type, "loyalty", StringComparison.OrdinalIgnoreCase);
                    if (!isLoyalty && string.IsNullOrWhiteSpace(o.Skill)) throw new InvalidDataException($"objective {ok}: no skill named");
                    finish.Add(new JsonObject
                    {
                        ["compareMethod"] = ">=", ["conditionType"] = isLoyalty ? "TraderLoyalty" : "Skill", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id(k), ["index"] = i,
                        ["parentId"] = "", ["target"] = isLoyalty ? Ids.Jackal : o.Skill, ["value"] = Math.Clamp(o.Value, 1, isLoyalty ? 4 : 51), ["visibilityConditions"] = new JsonArray(),
                    });
                    break;
                }
                default:
                    throw new InvalidDataException($"objective {ok}: unknown type \"{o.Type}\" (handover, kill, visit, mark, stash, extract, loyalty, skill)");
            }
            text[finish[^1]!["id"]!.GetValue<string>()] = label;
        }
        if (markers.Count > 0) started.Insert(0, ItemReward(Id("markers"), markers, Marker, markers.Count));

        var level = Math.Max(1, job.Level + cfg.LevelOffset);
        var loyalty = LoyaltyFor(job.Loyalty, level, jackal);
        var start = new JsonArray
        {
            new JsonObject { ["compareMethod"] = ">=", ["conditionType"] = "Level", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id("level"), ["index"] = 0, ["parentId"] = "", ["value"] = level, ["visibilityConditions"] = new JsonArray() },
            new JsonObject { ["compareMethod"] = ">=", ["conditionType"] = "TraderLoyalty", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id("loyalty"), ["index"] = 1, ["parentId"] = "", ["target"] = Ids.Jackal, ["value"] = loyalty, ["visibilityConditions"] = new JsonArray() },
        };
        foreach (var r in requires.Distinct())
            start.Add(new JsonObject
            {
                ["availableAfter"] = 0, ["conditionType"] = "Quest", ["dispersion"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id($"after:{r}"), ["index"] = start.Count,
                ["parentId"] = "", ["status"] = new JsonArray(4), ["target"] = r.ToString(), ["visibilityConditions"] = new JsonArray(),
            });

        var success = new JsonArray();
        var xp = (int)Math.Round(Math.Max(0, job.Xp) * cfg.RewardMultiplier);
        var money = (int)Math.Round(Math.Max(0, job.Roubles) * cfg.RewardMultiplier);
        if (xp > 0) success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Id("xp"), ["isHidden"] = false, ["type"] = "Experience", ["unknown"] = false, ["value"] = xp });
        if (cfg.StandingPerJob > 0) success.Add(new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Id("standing"), ["isHidden"] = false, ["target"] = Ids.Jackal, ["type"] = "TraderStanding", ["unknown"] = false, ["value"] = cfg.StandingPerJob });
        if (money > 0) success.Add(ItemReward(Id("money"), [Id("money-item")], Roubles, money));
        foreach (var u in unlocks.GroupBy(u => u.Template).Select(g => g.First()).Take(cfg.UnlockPicturesPerJob))
        {
            var item = Id($"unlock-item:{u.Template}");
            success.Add(new JsonObject
            {
                ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Id($"unlock:{u.Template}"), ["isHidden"] = false,
                ["items"] = new JsonArray(new JsonObject { ["_id"] = item, ["_tpl"] = u.Template.ToString() }), ["loyaltyLevel"] = Math.Clamp(u.Level, 1, 4),
                ["target"] = item, ["traderId"] = Ids.Jackal, ["type"] = "AssortmentUnlock", ["unknown"] = false,
            });
        }

        var kinds = new[] { kills, explore, handover, extract }.Count(b => b);
        var type = kinds > 1 ? "Multi" : kills ? "Elimination" : explore ? "Exploration" : handover ? "PickUp" : "Completion";
        var icon = job.Icon.StartsWith("/files/", StringComparison.Ordinal) ? job.Icon : "/files/quest/icon/" + job.Icon;
        var quest = new JsonObject
        {
            ["QuestName"] = job.Title, ["_id"] = questId, ["acceptPlayerMessage"] = $"{questId} acceptPlayerMessage", ["acceptanceAndFinishingSource"] = "eft", ["arenaLocations"] = new JsonArray(),
            ["canShowNotificationsInGame"] = true, ["changeQuestMessageText"] = $"{questId} changeQuestMessageText", ["completePlayerMessage"] = $"{questId} completePlayerMessage",
            ["conditions"] = new JsonObject { ["AvailableForFinish"] = finish, ["AvailableForStart"] = start, ["Fail"] = new JsonArray() },
            ["declinePlayerMessage"] = $"{questId} declinePlayerMessage", ["description"] = $"{questId} description", ["failMessageText"] = $"{questId} failMessageText", ["gameModes"] = new JsonArray(),
            ["image"] = icon, ["instantComplete"] = false, ["isKey"] = false, ["location"] = questMap.Id, ["name"] = $"{questId} name", ["note"] = $"{questId} note", ["progressSource"] = "eft",
            ["rankingModes"] = new JsonArray(), ["restartable"] = false,
            ["rewards"] = new JsonObject { ["Fail"] = new JsonArray(), ["Started"] = started, ["Success"] = success },
            ["secretQuest"] = false, ["side"] = "Pmc", ["startedMessageText"] = $"{questId} startedMessageText", ["status"] = 0, ["successMessageText"] = $"{questId} successMessageText",
            ["traderId"] = Ids.Jackal, ["type"] = type,
        };
        var story = job.Story.Trim();
        if (unlocks.Count > 0 && !string.IsNullOrWhiteSpace(job.Unlocks)) story += $"\n\nFinish this job and The Jackal sells {job.Unlocks.Trim()}.";
        text[$"{questId} name"] = job.Title;
        text[$"{questId} description"] = story;
        text[$"{questId} note"] = $"WWC job: {job.Line}, {step} of {steps}.";
        text[$"{questId} startedMessageText"] = string.IsNullOrWhiteSpace(job.Started) ? "You know what I need. Go." : job.Started;
        text[$"{questId} successMessageText"] = string.IsNullOrWhiteSpace(job.Done) ? "Good. It's on my shelf now." : job.Done;
        text[$"{questId} failMessageText"] = "It'll keep. Come back when you're ready.";
        text[$"{questId} changeQuestMessageText"] = "Something's changed. Have another look at the job.";
        text[$"{questId} acceptPlayerMessage"] = "Alright. I'll handle it.";
        text[$"{questId} completePlayerMessage"] = "Done. It's all there.";
        text[$"{questId} declinePlayerMessage"] = "Not right now.";
        return new Built(quest.Deserialize<Quest>(JsonOptions)!, text, level, loyalty, warnings);
    }

    private static bool IsId(string s) => s.Length == 24 && s.All(Uri.IsHexDigit);
    private static JsonArray Modes() => new("regular", "pve");
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject ItemReward(string id, List<string> itemIds, string tpl, int count)
    {
        // One stack (money) or one item per id (markers, given items): the shape the base game uses for each.
        var list = itemIds.Count == 1
            ? new JsonArray(new JsonObject { ["_id"] = itemIds[0], ["_tpl"] = tpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = count } })
            : new JsonArray(itemIds.Select(i => (JsonNode?)new JsonObject { ["_id"] = i, ["_tpl"] = tpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = 1 } }).ToArray());
        return new JsonObject
        {
            ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = false, ["gameMode"] = Modes(), ["id"] = id, ["isEncoded"] = false, ["isHidden"] = false,
            ["items"] = list, ["target"] = itemIds[0], ["type"] = "Item", ["unknown"] = false, ["value"] = count,
        };
    }

    private static JsonObject Counter(string id, string counterId, int index, string type, int value, JsonArray conditions) => new()
    {
        ["completeInSeconds"] = 0, ["conditionType"] = "CounterCreator", ["counter"] = new JsonObject { ["conditions"] = conditions, ["id"] = counterId },
        ["doNotResetIfCounterCompleted"] = false, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = id, ["index"] = index, ["isNecessary"] = false,
        ["isResetOnConditionFailed"] = false, ["oneSessionOnly"] = false, ["parentId"] = "", ["type"] = type, ["value"] = value, ["visibilityConditions"] = new JsonArray(),
    };

    private static JsonObject Handover(string id, int index, List<string> tpls, int count, bool foundInRaid) => new()
    {
        ["conditionType"] = "HandoverItem", ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = id, ["index"] = index, ["isEncoded"] = false,
        ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = foundInRaid, ["parentId"] = "", ["target"] = Strings(tpls), ["value"] = count, ["visibilityConditions"] = new JsonArray(),
    };

    private static JsonObject Placed(string id, int index, string type, string tpl, string zone, int seconds) => new()
    {
        ["conditionType"] = type, ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = id, ["index"] = index, ["isEncoded"] = false,
        ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = false, ["parentId"] = "", ["plantTime"] = seconds,
        ["target"] = new JsonArray(tpl), ["value"] = 1, ["visibilityConditions"] = new JsonArray(), ["zoneId"] = zone,
    };
}
