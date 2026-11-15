using System.Security.Cryptography;
using System.Text;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Utils.Json;

namespace LocalTrader.Server;

/// <summary>Balance settings do not change the persistent quest/condition/reward IDs.</summary>
public sealed record CampaignSettings
{
    public double ReputationPerQuest { get; init; } = 0.06;
    public double RewardMultiplier { get; init; } = 1;
    public int[] MinimumLevels { get; init; } = [1, 2, 4, 5, 7, 9, 11, 13, 15, 18, 20, 22];
}

/// <summary>Native SPT quests, shared by USEC/Bear and added through CustomQuestService.</summary>
public static class Campaign
{
    public const string Roubles = "5449016a4bdc2d6f028b456f";
    public const string Balaclava = "67271f160000000000000001";
    public const string Ax50Magazine = "67271f100000000000000002";
    public const int QuestCount = 12;

    public static MongoId QuestId(int number) => StableId($"quest:{number}");

    public static IReadOnlyList<NewQuestDetails> Build(MongoId traderId,
        IReadOnlyDictionary<MongoId, TemplateItem> items, IEnumerable<string> localeCodes,
        string traderName = "The Jackal", CampaignSettings? settings = null)
    {
        settings ??= new();
        if (settings.MinimumLevels.Length != QuestCount || settings.MinimumLevels.Any(level => level < 1))
            throw new InvalidDataException("The Jackal campaign needs twelve positive minimum levels.");
        if (!double.IsFinite(settings.ReputationPerQuest) || settings.ReputationPerQuest <= 0 ||
            !double.IsFinite(settings.RewardMultiplier) || settings.RewardMultiplier <= 0)
            throw new InvalidDataException("Campaign reputation and reward multipliers must be positive finite numbers.");
        for (int i = 1; i < settings.MinimumLevels.Length; i++)
            if (settings.MinimumLevels[i] < settings.MinimumLevels[i - 1])
                throw new InvalidDataException("Campaign levels must be in ascending order.");

        bool hasMask = items.ContainsKey(new MongoId(Balaclava));
        bool hasMagazine = items.ContainsKey(new MongoId(Ax50Magazine));
        var definitions = new Mission[]
        {
            new("First Contact", "Before I trust you with anything that matters, I want to see you can bring me what I ask for. Two rolls of duct tape and two packs of bolts. That's all.",
                1800, 18000, [Hand("57347c1124597737fb1379e3", 2, "Duct tape"), Hand("57347c5b245977448d35f6e1", 2, "Bolts")]),
            new("Clear the Approach", "Scavs keep stopping my couriers on the road and helping themselves. Kill three, any map. Maybe the rest get the hint, maybe they don't. Either way it's three fewer.",
                2400, 24000, [Kills("Savage", 3, "Eliminate 3 Scavs on any location")]),
            new("Patch Job", "My people don't go to hospitals. Hospitals ask questions. I want three AI-2 kits and a bloodset in the cupboard before the next run goes out.",
                3000, 30000, [Hand("5755356824597772cb798962", 3, "AI-2 medkit"), Hand("5b4335ba86f7744d2837a264", 1, "Medical bloodset")]),
            new("Unmarked Delivery", hasMask
                    ? "My next courier needs to look like nobody in particular. Bring me two plain black balaclavas."
                    : "My next courier needs to look like nobody in particular. Bring me two bandanas.",
                3600, 36000, [Hand(hasMask ? Balaclava : "5b43271c5acfc432ff4dce65", 2,
                    hasMask ? "PLATATAC balaclava (Black)" : "Bandana")]),
            new("Silence on the Wire", "The same Scavs are back on my roads. Apparently last time wasn't clear enough. Six this time, any map.",
                4200, 42000, [Kills("Savage", 6, "Eliminate 6 Scavs on any location")]),
            new("Circuit Breaker", "Radios, timers, chargers. Everything I move runs on something. Get me two circuit boards and two rechargeable batteries and I'll find someone to put them together.",
                5000, 48000, [Hand("590a3b0486f7743954552bdb", 2, "Printed circuit board"), Hand("590a358486f77429692b2790", 2, "Rechargeable battery")]),
            new("Keep Them Moving", "Anyone can go in. I need someone who comes back out with the goods. Survive three raids. Run-throughs don't count, and I'll know.",
                5800, 56000, [Survive(3, "Survive and extract from 3 raids on any location (run-throughs do not count)")]),
            new("Cross the Line", "It's not Scavs anymore. Another outfit is hunting my couriers, and they're properly armed. Take out three PMCs before they find the rest of my routes.",
                6600, 64000, [Kills("AnyPmc", 3, "Eliminate 3 PMCs on any location")]),
            new("Workshop Stock", "The next order's bigger and the benches eat hardware. Five bolts, five screw nuts and three packs of nails.",
                7400, 72000, [Hand("57347c5b245977448d35f6e1", 5, "Bolts"), Hand("57347c77245977448d35f6e2", 5, "Screw nuts"), Hand("590c31c586f774245e3141b2", 3, "Pack of nails")]),
            // Without the AX50 the job asks for power cords, so it gets a title that fits.
            new(hasMagazine ? "Feed the Rifle" : "Power Leads", hasMagazine
                    ? "I want a spare AX50 magazine on the bench to check the new ones against. The shop has them, I don't care where yours comes from. Empty it first."
                    : "The bench needs new power leads. Bring me three cords that still work and we can get the next shipment ready.",
                8200, 80000, [Hand(hasMagazine ? Ax50Magazine : "59e36c6f86f774176c10a2a7", hasMagazine ? 1 : 3,
                    hasMagazine ? "AX50 5-round magazine" : "Power cord")]),
            new("Secure the Corridor", "What I'm moving now is worth real money. Push the Scavs back far enough that my couriers can work without someone holding their hand. Fifteen should do it.",
                9200, 90000, [Kills("Savage", 15, "Eliminate 15 Scavs on any location")]),
            new("A Man of His Word", "Last one. An old friend of mine kept a bronze lion and a cat on his desk. He lost both, and I told him I'd replace them. I keep my word, so now you're helping me keep it.",
                11000, 125000, [Hand("59e3639286f7741777737013", 1, "Bronze lion figurine"), Hand("59e3658a86f7741776641ac4", 1, "Cat figurine")])
        };

        var languages = localeCodes.Append("en").Distinct(StringComparer.Ordinal).ToArray();
        var result = new List<NewQuestDetails>(QuestCount);
        for (int index = 0; index < definitions.Length; index++)
        {
            int number = index + 1;
            var mission = definitions[index];
            var id = QuestId(number);
            string key = id.ToString();
            var text = new Dictionary<string, string>
            {
                [$"{key} name"] = mission.Title,
                [$"{key} description"] = $"{traderName}: {mission.Description}\n\nItems don't have to be found in raid unless the objective says so.",
                [$"{key} startedMessageText"] = "You know what I need. Come back when it's done.",
                [$"{key} successMessageText"] = number == QuestCount ? "You said you'd do it and you did. That's all I ever ask. Everything I've got is open to you now." : "Good. Your money's waiting. I'll have more for you soon.",
                [$"{key} acceptPlayerMessage"] = "On it.",
                [$"{key} completePlayerMessage"] = "It's done.",
                [$"{key} declinePlayerMessage"] = "Not yet.",
                [$"{key} failMessageText"] = "The job's still there. Finish it when you can.",
                [$"{key} changeQuestMessageText"] = "Change of plan. Check the details.",
                [$"{key} note"] = $"{traderName} contract {number} of {QuestCount}."
            };
            var finish = new List<QuestCondition>();
            for (int objectiveIndex = 0; objectiveIndex < mission.Objectives.Length; objectiveIndex++)
            {
                var objective = mission.Objectives[objectiveIndex];
                var condition = MakeObjective(number, objectiveIndex, objective);
                if (objective.Kind == "HandoverItem" && !items.ContainsKey(new MongoId(objective.Target)))
                    throw new InvalidDataException($"Campaign item {objective.Target} is missing from the database.");
                finish.Add(condition);
                text[condition.Id.ToString()] = objective.Kind == "HandoverItem"
                    ? $"Hand over {objective.Count} x {objective.Text} (any source)"
                    : objective.Text;
            }
            var start = new List<QuestCondition>
            {
                new()
                {
                    Id = StableId($"level:{number}"), ConditionType = "Level", CompareMethod = ">=",
                    Value = settings.MinimumLevels[index], Index = 0, DynamicLocale = false,
                    ParentId = "", GlobalQuestCounterId = "", VisibilityConditions = []
                }
            };
            if (number > 1)
                start.Add(new()
                {
                    Id = StableId($"previous:{number}"), ConditionType = "Quest", Index = 1,
                    Target = Single(QuestId(number - 1).ToString()), Status = [QuestStatusEnum.Success],
                    AvailableAfter = 0, Dispersion = 0, DynamicLocale = false, ParentId = "",
                    GlobalQuestCounterId = "", VisibilityConditions = []
                });

            var moneyId = StableId($"money-item:{number}");
            int money = checked((int)Math.Round(mission.Money * settings.RewardMultiplier));
            int experience = checked((int)Math.Round(mission.Experience * settings.RewardMultiplier));
            var quest = new Quest
            {
                Id = id, QuestName = mission.Title, TraderId = traderId,
                Name = $"{key} name", Description = $"{key} description", Note = $"{key} note",
                AcceptPlayerMessage = $"{key} acceptPlayerMessage", DeclinePlayerMessage = $"{key} declinePlayerMessage",
                CompletePlayerMessage = $"{key} completePlayerMessage", StartedMessageText = $"{key} startedMessageText",
                SuccessMessageText = $"{key} successMessageText", FailMessageText = $"{key} failMessageText",
                ChangeQuestMessageText = $"{key} changeQuestMessageText", CanShowNotificationsInGame = true,
                AcceptanceAndFinishingSource = "eft", ProgressSource = "eft", Side = "Pmc", Location = "any",
                Type = mission.Objectives.Any(objective => objective.Kind == "Kills") ? QuestTypeEnum.Elimination : QuestTypeEnum.Completion,
                Image = "/files/quest/icon/596b465486f77457ca186188.jpg", Status = 0,
                IsKey = false, KeyQuest = false, SecretQuest = false, Restartable = false, InstantComplete = false,
                ArenaLocations = [], RankingModes = [], GameModes = [],
                Conditions = new() { AvailableForStart = start, AvailableForFinish = finish, Fail = [] },
                Rewards = new()
                {
                    ["Started"] = [], ["Fail"] = [],
                    ["Success"] =
                    [
                        CommonReward(StableId($"xp:{number}"), RewardType.Experience, experience),
                        CommonReward(StableId($"standing:{number}"), RewardType.TraderStanding, settings.ReputationPerQuest, traderId.ToString()),
                        CommonReward(StableId($"money:{number}"), RewardType.Item, money, moneyId.ToString()) with
                        {
                            FindInRaid = false, IsEncoded = false,
                            Items = [new() { Id = moneyId, Template = new MongoId(Roubles), Upd = new() { StackObjectsCount = money } }]
                        }
                    ]
                }
            };
            result.Add(new NewQuestDetails
            {
                NewQuest = quest,
                Locales = languages.ToDictionary(language => language, _ => new Dictionary<string, string>(text)),
                LockedToSide = null
            });
        }
        return result;
    }

    private static Reward CommonReward(MongoId id, RewardType type, double value, string? target = null) => new()
    {
        Id = id, Type = type, Value = value, Target = target, IsHidden = false, Unknown = false,
        AvailableInGameEditions = [], GameMode = ["regular", "pve"]
    };

    private static QuestCondition MakeObjective(int quest, int index, Objective objective)
    {
        var condition = new QuestCondition
        {
            Id = StableId($"objective:{quest}:{index}"), Index = index, Value = objective.Count,
            ConditionType = objective.Kind == "HandoverItem" ? "HandoverItem" : "CounterCreator",
            DynamicLocale = false, GlobalQuestCounterId = "", ParentId = "", VisibilityConditions = []
        };
        if (objective.Kind == "HandoverItem")
        {
            condition.ConditionType = "HandoverItem";
            condition.Target = new ListOrT<string>([objective.Target], null!);
            condition.DogtagLevel = 0;
            condition.OnlyFoundInRaid = false;
            condition.MinDurability = 0;
            condition.MaxDurability = 100;
            condition.IsEncoded = false;
            return condition;
        }
        condition.ConditionType = "CounterCreator";
        condition.Type = objective.Kind == "Kills" ? "Elimination" : "Completion";
        condition.OneSessionOnly = false;
        condition.IsResetOnConditionFailed = false;
        condition.IsNecessary = false;
        condition.DoNotResetIfCounterCompleted = false;
        condition.CompleteInSeconds = 0;
        condition.Counter = new()
        {
            Id = StableId($"counter:{quest}:{index}").ToString(),
            Conditions = objective.Kind == "Kills"
                ? [new()
                {
                    Id = StableId($"counter-condition:{quest}:{index}"), ConditionType = "Kills",
                    Target = Single(objective.Target), CompareMethod = ">=", Value = 1,
                    DynamicLocale = false, ResetOnSessionEnd = false, BodyPart = [],
                    Daytime = new() { From = 0, To = 0 }, Distance = new() { CompareMethod = ">=", Value = 0 },
                    Weapon = [], WeaponCaliber = [], WeaponModsExclusive = [], WeaponModsInclusive = [],
                    EnemyEquipmentExclusive = [], EnemyEquipmentInclusive = [], EnemyHealthEffects = [], SavageRole = []
                }]
                : [new()
                {
                    Id = StableId($"counter-condition:{quest}:{index}"), ConditionType = "ExitStatus",
                    DynamicLocale = false, Status = ["Survived"]
                }]
        };
        return condition;
    }

    private static MongoId StableId(string name) => new(Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("local.thejackal.campaign.v1:" + name))).ToLowerInvariant()[..24]);
    private static ListOrT<string> Single(string target) => new(null!, target);
    private static Objective Hand(string id, int count, string label) => new("HandoverItem", id, count, label);
    private static Objective Kills(string target, int count, string label) => new("Kills", target, count, label);
    private static Objective Survive(int count, string label) => new("Survive", "", count, label);
    private sealed record Objective(string Kind, string Target, int Count, string Text);
    private sealed record Mission(string Title, string Description, int Experience, int Money, Objective[] Objectives);
}
