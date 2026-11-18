using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Grunts.Server;

/// <summary>config.json next to the DLL. Every field has a working default.</summary>
public sealed class ModConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("questlineEnabled")] public bool QuestlineEnabled { get; set; } = true;

    /// <summary>Which of the game's infected bot roles becomes the Grunt. The infected roles share one zombie brain and animator; the
    /// Halloween event (if it ever runs) would bring this role's own zombies with the Grunt's stats, and their kills would count.</summary>
    [JsonPropertyName("gruntRole")] public string GruntRole { get; set; } = "infectedLaborant";
    /// <summary>easy = slow walker, normal = fast melee runner, hard = zombie with a gun (the game's EZombieMode by difficulty).</summary>
    [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = "normal";
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "Grunt";
    [JsonPropertyName("health")] public Dictionary<string, double> Health { get; set; } = new()
    {
        ["Head"] = 45, ["Chest"] = 350, ["Stomach"] = 280, ["LeftArm"] = 140, ["RightArm"] = 140, ["LeftLeg"] = 160, ["RightLeg"] = 160,
    };
    /// <summary>Melee weapon pool (template id: weight). Default: the UVSR Taiga-1 survival machete.</summary>
    [JsonPropertyName("weapons")] public Dictionary<string, double> Weapons { get; set; } = new() { ["601948682627df266209af05"] = 1 };
    /// <summary>Extra gear the Grunts may wear: equipment slot -> { template id: weight }, with a chance per slot. Templates that are
    /// not in the database are skipped (for gear from other mods, such as the Avon mask).</summary>
    [JsonPropertyName("gear")] public Dictionary<string, Dictionary<string, double>> Gear { get; set; } = new();
    [JsonPropertyName("gearChance")] public Dictionary<string, double> GearChance { get; set; } = new();
    [JsonPropertyName("xpPerKill")] public int XpPerKill { get; set; } = 300;

    /// <summary>The Avon gas mask (mods/AvonMask, FaceCover). "@mask" in quests.json means this template: the last Factory job gives it,
    /// the Labs jobs need it worn (no Grunts on Labs without it). Empty or not installed: no mask reward and no mask needed.</summary>
    [JsonPropertyName("maskTpl")] public string MaskTpl { get; set; } = "b1f00d509254419311745076";

    /// <summary>The Avon mask's back-mounted PAPR blower and hose (separate piece from the mask thread). "@papr" in quests.json: the reward
    /// for finishing Labs (last Labs job). Empty or not installed: left out of the reward.</summary>
    [JsonPropertyName("paprTpl")] public string PaprTpl { get; set; } = "";

    /// <summary>true: Grunts only come while one of the Jackal's Grunt jobs for that map is running (status Started).</summary>
    [JsonPropertyName("spawnsNeedJob")] public bool SpawnsNeedJob { get; set; } = true;
    /// <summary>true: only PMC raids get them (a Scav raid cannot move a PMC job forward).</summary>
    [JsonPropertyName("pmcRaidsOnly")] public bool PmcRaidsOnly { get; set; } = true;
    /// <summary>Spawn zones per map id (the boss-zone names in each map's base.json; several are separated by commas, the game picks one).</summary>
    [JsonPropertyName("zones")] public Dictionary<string, string> Zones { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["factory4_day"] = "BotZone", ["factory4_night"] = "BotZone", ["laboratory"] = "BotZoneFloor1,BotZoneFloor2,BotZoneBasement",
    };
    /// <summary>Chance (0-100) per raid that Grunts roam one of freeRoamMaps with no job running; uses freeRoamWaves. 0 = never. Widowz:
    /// after the last Labs job they spawn "really really rare", and no gas mask is needed for these.</summary>
    [JsonPropertyName("freeRoamChance")] public double FreeRoamChance { get; set; } = 2;
    /// <summary>Roaming only starts once this job (quests.json key) is finished. Empty = from the start.</summary>
    [JsonPropertyName("freeRoamAfter")] public string FreeRoamAfter { get; set; } = "labs_end_of_trials";
    /// <summary>true: roaming Grunts may also turn up in Scav raids (they give no job progress there anyway).</summary>
    [JsonPropertyName("freeRoamScavRaids")] public bool FreeRoamScavRaids { get; set; } = true;
    [JsonPropertyName("freeRoamWaves")] public List<WaveDef> FreeRoamWaves { get; set; } = [new() { Time = -1, Count = 1 }];
    [JsonPropertyName("freeRoamMaps")] public List<string> FreeRoamMaps { get; set; } = ["factory4_day", "factory4_night", "laboratory"];
}

public sealed class WaveDef
{
    /// <summary>Seconds into the raid; -1 = at the start.</summary>
    [JsonPropertyName("time")] public double Time { get; set; } = -1;
    [JsonPropertyName("count")] public int Count { get; set; } = 1;
}

public sealed class QuestFile
{
    [JsonPropertyName("quests")] public List<QuestDef> Quests { get; set; } = [];
}

public sealed class QuestDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>Factory or Labs (also used for the quest's location in the task list).</summary>
    [JsonPropertyName("map")] public string Map { get; set; } = "Factory";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    /// <summary>The Jackal loyalty level needed to start it (1-4); left out = the level his loyalty table opens at "level".</summary>
    [JsonPropertyName("loyalty")] public int? Loyalty { get; set; }
    [JsonPropertyName("xp")] public int Xp { get; set; }
    [JsonPropertyName("roubles")] public int Roubles { get; set; }
    [JsonPropertyName("standing")] public double Standing { get; set; } = 0.02;
    /// <summary>Optional reward items: template id + count. Skipped (with a log line) when the template is not in the database.</summary>
    [JsonPropertyName("rewardItems")] public List<RewardItemDef> RewardItems { get; set; } = [];
    [JsonPropertyName("story")] public string Story { get; set; } = "";
    /// <summary>Used instead of story when none of requireWorn is installed (so the text never asks for gear that does not exist).</summary>
    [JsonPropertyName("storyWithoutGear")] public string? StoryWithoutGear { get; set; }
    /// <summary>One of these must be worn when the raid starts, or this job's Grunts do not come. "@mask" = config maskTpl.</summary>
    [JsonPropertyName("requireWorn")] public List<string> RequireWorn { get; set; } = [];
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    [JsonPropertyName("done")] public string Done { get; set; } = "";
    [JsonPropertyName("objectives")] public List<ObjectiveDef> Objectives { get; set; } = [];
    /// <summary>Grunt waves while this job runs, on each map its kill objectives name.</summary>
    [JsonPropertyName("waves")] public List<WaveDef> Waves { get; set; } = [];
}

public sealed class RewardItemDef
{
    [JsonPropertyName("tpl")] public string Tpl { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; } = 1;
    [JsonPropertyName("foundInRaid")] public bool FoundInRaid { get; set; } = true;
}

public sealed class ObjectiveDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    /// <summary>grunts (kill Grunts) is the only kind for now.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "grunts";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; } = 1;
    /// <summary>Factory, Labs or any.</summary>
    [JsonPropertyName("map")] public string Map { get; set; } = "Factory";
    /// <summary>Optional: kills only count while wearing one of these templates (skipped if none of them is in the database).</summary>
    [JsonPropertyName("wearing")] public List<string>? Wearing { get; set; }
    [JsonPropertyName("textWithoutGear")] public string? TextWithoutGear { get; set; }
}

internal static class Ids
{
    public static MongoId Hash(string text) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..24]);
    public static string Quest(string key) => Hash("grunts|quest:" + key.ToLowerInvariant()).ToString();
    public static string Part(string questKey, string part) => Hash($"grunts|quest:{questKey.ToLowerInvariant()}:{part}").ToString();

    /// <summary>Map names as in quests.json -> (location template id for the quest card, location ids for conditions and spawns).</summary>
    public static readonly Dictionary<string, (string Id, string[] Names)> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["any"] = ("any", []),
        ["Factory"] = ("55f2d3fd4bdc2d5f408b4567", ["factory4_day", "factory4_night"]),
        ["Labs"] = ("5b0fc42d86f7744a585f9105", ["laboratory"]),
    };
}
