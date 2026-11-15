using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils.Json;

namespace JackalOrigin.Server;

/// <summary>The Jackal's origin in three parts, given by Fence on his behalf. Every zone and exit is one the base game's own quests use, so no client mod is needed.
/// Quests are written in the database's own JSON shape and read back through SPT's converters.</summary>
internal static class OriginStory
{
    public const int PartCount = 3;
    private const string Roubles = "5449016a4bdc2d6f028b456f", Bitcoin = "59faff1d86f7746c51718c9c", Strike = "5734770f24597738025ee254", Flash = "590c621186f774138d11ea29";
    private static readonly string[] Mosins = ["5bfd297f0db834001a669119", "5ae08f0a5acfc408fb1398a1"];
    private const string GroundZero = "653e6760052c01c1c805532f", Shoreline = "5704e554d2720bac5b8b456e", Customs = "56f40101d2720b2a4d8b45d6";

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static MongoId QuestId(int n) => Ids.Of($"quest:{n}");
    private static string Id(string key) => Ids.Of(key).ToString();

    public static IEnumerable<(Quest Quest, Dictionary<string, string> Text)> Build(OriginConfig cfg)
    {
        // Part 1: A Price on Your Head (Ground Zero)
        {
            var p = cfg.Part1; var n = 1; var text = new Dictionary<string, string>();
            var finish = new JsonArray
            {
                Visit(text, $"p{n}:overpass", 0, "Prospect_mira", "Scout the Mira Avenue overpass on Ground Zero"),
                Visit(text, $"p{n}:office", 1, "Sandbox_5_Office_exploration", "Find the target's office in the TerraGroup complex on Ground Zero"),
                Kills(text, $"p{n}:kills", 2, Math.Max(1, p.Kills), "AnyPmc", ["Sandbox", "Sandbox_high"], $"Eliminate {Math.Max(1, p.Kills)} PMC operatives on Ground Zero"),
                Handover(text, $"p{n}:flash", 3, [Flash], 1, true, "Hand over 1 found in raid Secure Flash drive"),
            };
            yield return MakeQuest(n, "A Price on Your Head", GroundZero, "/files/quest/icon/65899c7b6f65fb099516b25d.jpg", p, finish, new JsonArray(), text,
                "Fence: Someone left a message for you with my people. I don't read other people's mail. Mostly.\n\n" +
                "The Jackal: This week a man offered me a great deal of money to kill you. I said no. I don't take work from clients who won't give me their names, not since this city. He will find somebody who says yes, so you will do what I tell you and stay alive long enough for us to talk.\n\n" +
                "Start where I started. Years ago I lay on the Mira Avenue overpass for two days, waiting for a car to leave the TerraGroup offices. Stand where I stood, then go inside and see the office the man worked in. The ones who said yes are already on Ground Zero, so deal with them on the way. And bring me a flash drive from those offices, one you found yourself. I want to know you can find things.",
                "Fence: Message delivered. What you do about it is your business.",
                "Fence: He says you're still breathing, which puts you ahead of most people he likes. He has another message. He wouldn't tell me what it pays, so it pays well.");
        }
        // Part 2: The Boat That Never Came (Shoreline)
        {
            var p = cfg.Part2; var n = 2; var text = new Dictionary<string, string>();
            var finish = new JsonArray
            {
                Visit(text, $"p{n}:boat", 0, "place_peacemaker_001", "Locate the boat hidden next to the breakwater on Shoreline"),
                Kills(text, $"p{n}:kills", 1, Math.Max(1, p.Kills), "Savage", ["Shoreline"], $"Eliminate {Math.Max(1, p.Kills)} Scavs on Shoreline"),
                Handover(text, $"p{n}:mosin", 2, Mosins, 1, true, "Hand over 1 found in raid Mosin rifle (Infantry or Sniper)"),
                Extract(text, $"p{n}:exit", 3, ["Shoreline"], "Pier Boat", "Survive and extract from Shoreline through Pier Boat"),
            };
            yield return MakeQuest(n, "The Boat That Never Came", Shoreline, "/files/quest/icon/594d241f86f7740d8246218d.jpg", p, finish, new JsonArray(), text,
                "The Jackal (through Fence): I took the shot that afternoon, with the first smoke already over the city. One round, through the glass. My way out was a boat behind the breakwater on Shoreline, with the other half of my fee in it.\n\n" +
                "The boat was there. The pilot was in it with a hole in the back of his head, and four men were waiting in the trees to put one in mine. Their rifles were old Mosins, bought locally so nothing led back to the client. I kept them. I kept their radio too, and listened to them tell my client the job was done and the shooter was dead. I let him believe it. As far as the world knows, I died on that beach.\n\n" +
                "Find the boat. Clear the shore the way I had to. Bring me a Mosin you took off a body yourself, because the first gun I ever sold in this city was one of theirs. Then leave by the pier boat, the way I was supposed to.",
                "Fence: Same sender. He writes like a man who is used to being obeyed.",
                "Fence: He asked whether the boat was still there, and went quiet when I said yes. The next message is the last one he sends through me. After that, he says, you talk to him yourself.");
        }
        // Part 3: Terms of Business (Customs). Passing unlocks the Jackal.
        {
            var p = cfg.Part3; var n = 3; var text = new Dictionary<string, string>();
            var finish = new JsonArray
            {
                Leave(text, $"p{n}:bitcoin", 0, Bitcoin, "mech_41_1", 10, "Stash the Physical bitcoin under the yellow crane at the construction site on Customs"),
                Leave(text, $"p{n}:strike", 1, Strike, "mech_41_2", 10, "Stash a pack of Strike cigarettes behind the trash containers at the new gas station on Customs"),
                Extract(text, $"p{n}:exit", 2, ["bigmap"], null, "Survive and extract from Customs"),
            };
            var started = new JsonArray { ItemReward($"p{n}:started-bitcoin", Bitcoin, 1) };
            var done = p.BitcoinReward
                ? "The Jackal: The drop was full when my courier got there. You could have kept it, so here is one you are allowed to keep. The man who wanted you dead won't be asking again. I sold his name to people who liked him even less than you do. My door is open. The first job is small, some tape and a few bolts. Don't make me regret it."
                : "The Jackal: The drop was full when my courier got there. You could have kept it. The man who wanted you dead won't be asking again. I sold his name to people who liked him even less than you do. My door is open. The first job is small, some tape and a few bolts. Don't make me regret it.";
            var extra = new List<JsonNode> { new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Id($"p{n}:unlock"), ["isHidden"] = false, ["target"] = Ids.Jackal, ["type"] = "TraderUnlock", ["unknown"] = false } };
            if (p.BitcoinReward) extra.Add(ItemReward($"p{n}:reward-bitcoin", Bitcoin, 1));
            yield return MakeQuest(n, "Terms of Business", Customs, "/files/quest/icon/5ac4dbb086f7743e7c61ca09.jpg", p, finish, started, text,
                "The Jackal (through Fence): No exit, no fee and no name. What I had was a dead crew's rifles and fifteen years of finding people who did not want to be found. A man with a hole in his side does not get far on that, and a stranger with a bronze lion on his desk stitched me up and asked no questions. I asked none back. I still owe him.\n\n" +
                "The rifles went first, then the radio, then what I heard on it. That's how I got into this. I sell guns, and when the price is right I sell names. I know who wanted you dead this week. You will hear it on the other side of my counter.\n\n" +
                "One last test. I don't care how well you shoot. I care whether you keep what isn't yours. Fence has given you a bitcoin. Leave it under the yellow crane at the Customs construction site, then leave a pack of Strike cigarettes behind the bins at the new gas station so my courier knows the drop is loaded. Then get out alive, and you can come to me yourself.",
                "Fence: He sent this for you. I would keep it, personally. That is probably why he doesn't trust me.",
                done, extra);
        }
    }

    private static (Quest, Dictionary<string, string>) MakeQuest(int n, string title, string location, string image, PartConfig p, JsonArray finish, JsonArray started,
        Dictionary<string, string> text, string story, string startedText, string doneText, List<JsonNode>? extraRewards = null)
    {
        var id = QuestId(n).ToString();
        var start = new JsonArray
        {
            new JsonObject { ["compareMethod"] = ">=", ["conditionType"] = "Level", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id($"p{n}:level"), ["index"] = 0, ["parentId"] = "", ["value"] = Math.Max(1, p.Level), ["visibilityConditions"] = new JsonArray() },
        };
        if (n > 1)
            start.Add(new JsonObject { ["availableAfter"] = 0, ["conditionType"] = "Quest", ["dispersion"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id($"p{n}:previous"), ["index"] = 1, ["parentId"] = "", ["status"] = new JsonArray(4), ["target"] = QuestId(n - 1).ToString(), ["visibilityConditions"] = new JsonArray() });
        var success = new JsonArray
        {
            new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Id($"p{n}:xp"), ["isHidden"] = false, ["type"] = "Experience", ["unknown"] = false, ["value"] = Math.Max(0, p.Xp) },
            new JsonObject { ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = Modes(), ["id"] = Id($"p{n}:standing"), ["isHidden"] = false, ["target"] = Ids.Jackal, ["type"] = "TraderStanding", ["unknown"] = false, ["value"] = p.Standing },
        };
        if (p.Roubles > 0) success.Add(ItemReward($"p{n}:money", Roubles, p.Roubles));
        foreach (var r in extraRewards ?? []) success.Add(r);
        var quest = new JsonObject
        {
            ["QuestName"] = title, ["_id"] = id, ["acceptPlayerMessage"] = $"{id} acceptPlayerMessage", ["acceptanceAndFinishingSource"] = "eft", ["arenaLocations"] = new JsonArray(),
            ["canShowNotificationsInGame"] = true, ["changeQuestMessageText"] = $"{id} changeQuestMessageText", ["completePlayerMessage"] = $"{id} completePlayerMessage",
            ["conditions"] = new JsonObject { ["AvailableForFinish"] = finish, ["AvailableForStart"] = start, ["Fail"] = new JsonArray() },
            ["declinePlayerMessage"] = $"{id} declinePlayerMessage", ["description"] = $"{id} description", ["failMessageText"] = $"{id} failMessageText", ["gameModes"] = new JsonArray(),
            ["image"] = image, ["instantComplete"] = false, ["isKey"] = false, ["location"] = location, ["name"] = $"{id} name", ["note"] = $"{id} note", ["progressSource"] = "eft",
            ["rankingModes"] = new JsonArray(), ["restartable"] = false,
            ["rewards"] = new JsonObject { ["Fail"] = new JsonArray(), ["Started"] = started, ["Success"] = success },
            ["secretQuest"] = false, ["side"] = "Pmc", ["startedMessageText"] = $"{id} startedMessageText", ["status"] = 0, ["successMessageText"] = $"{id} successMessageText",
            ["traderId"] = Ids.Fence, ["type"] = "Multi",
        };
        text[$"{id} name"] = title;
        text[$"{id} description"] = story;
        text[$"{id} note"] = $"The Jackal, part {n} of {PartCount}.";
        text[$"{id} startedMessageText"] = startedText;
        text[$"{id} successMessageText"] = doneText;
        text[$"{id} failMessageText"] = "Fence: He doesn't like waiting. Neither do I.";
        text[$"{id} changeQuestMessageText"] = "Change of plan. Check the details.";
        text[$"{id} acceptPlayerMessage"] = "Tell him I'm listening.";
        text[$"{id} completePlayerMessage"] = "It's done.";
        text[$"{id} declinePlayerMessage"] = "Not yet.";
        return (quest.Deserialize<Quest>(JsonOptions)!, text);
    }

    private static JsonArray Modes() => new("regular", "pve");

    private static JsonObject ItemReward(string key, string tpl, int count)
    {
        var item = Id(key + ":item");
        return new JsonObject
        {
            ["availableInGameEditions"] = new JsonArray(), ["findInRaid"] = false, ["gameMode"] = Modes(), ["id"] = Id(key), ["isEncoded"] = false, ["isHidden"] = false,
            ["items"] = new JsonArray(new JsonObject { ["_id"] = item, ["_tpl"] = tpl, ["upd"] = new JsonObject { ["StackObjectsCount"] = count } }),
            ["target"] = item, ["type"] = "Item", ["unknown"] = false, ["value"] = count,
        };
    }

    private static JsonObject Counter(Dictionary<string, string> text, string key, int index, string type, int value, JsonArray conditions, string label)
    {
        text[Id(key)] = label;
        return new JsonObject
        {
            ["completeInSeconds"] = 0, ["conditionType"] = "CounterCreator", ["counter"] = new JsonObject { ["conditions"] = conditions, ["id"] = Id(key + ":counter") },
            ["doNotResetIfCounterCompleted"] = false, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id(key), ["index"] = index, ["isNecessary"] = false,
            ["isResetOnConditionFailed"] = false, ["oneSessionOnly"] = false, ["parentId"] = "", ["type"] = type, ["value"] = value, ["visibilityConditions"] = new JsonArray(),
        };
    }

    private static JsonObject Location(string key, string[] maps) => new()
    { ["conditionType"] = "Location", ["dynamicLocale"] = false, ["id"] = Id(key + ":location"), ["target"] = new JsonArray(maps.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()) };

    private static JsonObject Visit(Dictionary<string, string> text, string key, int index, string zone, string label) =>
        Counter(text, key, index, "Completion", 1, new JsonArray(new JsonObject { ["conditionType"] = "VisitPlace", ["dynamicLocale"] = false, ["id"] = Id(key + ":visit"), ["target"] = zone, ["value"] = 1 }), label);

    private static JsonObject Kills(Dictionary<string, string> text, string key, int index, int count, string target, string[] maps, string label) =>
        Counter(text, key, index, "Elimination", count, new JsonArray(Location(key, maps), new JsonObject
        {
            ["bodyPart"] = new JsonArray(), ["compareMethod"] = ">=", ["conditionType"] = "Kills", ["daytime"] = new JsonObject { ["from"] = 0, ["to"] = 0 },
            ["distance"] = new JsonObject { ["compareMethod"] = ">=", ["value"] = 0 }, ["dynamicLocale"] = false, ["enemyEquipmentExclusive"] = new JsonArray(),
            ["enemyEquipmentInclusive"] = new JsonArray(), ["enemyHealthEffects"] = new JsonArray(), ["id"] = Id(key + ":kills"), ["resetOnSessionEnd"] = false,
            ["savageRole"] = new JsonArray(), ["target"] = target, ["value"] = 1, ["weapon"] = new JsonArray(), ["weaponCaliber"] = new JsonArray(),
            ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray(),
        }), label);

    private static JsonObject Extract(Dictionary<string, string> text, string key, int index, string[] maps, string? exitName, string label)
    {
        var conditions = new JsonArray(Location(key, maps), new JsonObject { ["conditionType"] = "ExitStatus", ["dynamicLocale"] = false, ["id"] = Id(key + ":status"), ["status"] = new JsonArray("Survived") });
        if (exitName is not null) conditions.Add(new JsonObject { ["conditionType"] = "ExitName", ["dynamicLocale"] = false, ["exitName"] = exitName, ["id"] = Id(key + ":exitname") });
        return Counter(text, key, index, "Completion", 1, conditions, label);
    }

    private static JsonObject Handover(Dictionary<string, string> text, string key, int index, string[] tpls, int count, bool foundInRaid, string label)
    {
        text[Id(key)] = label;
        return new JsonObject
        {
            ["conditionType"] = "HandoverItem", ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id(key), ["index"] = index, ["isEncoded"] = false,
            ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = foundInRaid, ["parentId"] = "",
            ["target"] = new JsonArray(tpls.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()), ["value"] = count, ["visibilityConditions"] = new JsonArray(),
        };
    }

    private static JsonObject Leave(Dictionary<string, string> text, string key, int index, string tpl, string zone, int plantSeconds, string label)
    {
        text[Id(key)] = label;
        return new JsonObject
        {
            ["conditionType"] = "LeaveItemAtLocation", ["dogtagLevel"] = 0, ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["id"] = Id(key), ["index"] = index, ["isEncoded"] = false,
            ["maxDurability"] = 100, ["minDurability"] = 0, ["onlyFoundInRaid"] = false, ["parentId"] = "", ["plantTime"] = plantSeconds,
            ["target"] = new JsonArray(tpl), ["value"] = 1, ["visibilityConditions"] = new JsonArray(), ["zoneId"] = zone,
        };
    }
}
