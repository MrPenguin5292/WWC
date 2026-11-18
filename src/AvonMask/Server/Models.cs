using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;   // TemplateItemProperties
using SPTarkov.Server.Core.Models.Spt.Mod;             // LocaleDetails
using SPTarkov.Server.Core.Models.Spt.Tables;          // Preset

namespace AvonMask.Server;

/// <summary>One entry of db/CustomItems/*.json, keyed by the new template id (WTT-CommonLib CustomItems schema, tools/make_items.py).</summary>
public record CustomItemEntry
{
    [JsonPropertyName("itemTplToClone")] public required string ItemTplToClone { get; set; }
    [JsonPropertyName("parentId")] public required string ParentId { get; set; }
    [JsonPropertyName("handbookParentId")] public string? HandbookParentId { get; set; }
    [JsonPropertyName("newName")] public string? NewName { get; set; }
    [JsonPropertyName("overrideProperties")] public TemplateItemProperties? OverrideProperties { get; set; }
    [JsonPropertyName("locales")] public Dictionary<string, LocaleDetails> Locales { get; set; } = new();
    [JsonPropertyName("fleaPriceRoubles")] public double? FleaPriceRoubles { get; set; }
    [JsonPropertyName("handbookPriceRoubles")] public double? HandbookPriceRoubles { get; set; }
    [JsonPropertyName("registerInHandbook")] public bool? RegisterInHandbook { get; set; }
    [JsonPropertyName("registerInFleaPrices")] public bool? RegisterInFleaPrices { get; set; }
    [JsonPropertyName("addtoSecureFilters")] public bool? AddToSecureFilters { get; set; }
    [JsonPropertyName("addtoBots")] public bool? AddToBots { get; set; }
    [JsonPropertyName("addtoStaticLootContainers")] public bool? AddToStaticLootContainers { get; set; }
    [JsonPropertyName("addtoTraders")] public bool? AddToTraders { get; set; }
    [JsonPropertyName("addWeaponPreset")] public bool? AddWeaponPreset { get; set; }
    [JsonPropertyName("weaponPresets")] public List<Preset>? WeaponPresets { get; set; }
    [JsonPropertyName("masteries")] public bool? Masteries { get; set; }
    [JsonPropertyName("masterySections")] public List<MasterySection>? MasterySections { get; set; }
}

public record MasterySection
{
    [JsonPropertyName("Name")] public required string Name { get; set; }
    [JsonPropertyName("Level2")] public int Level2 { get; set; } = 100;
    [JsonPropertyName("Level3")] public int Level3 { get; set; } = 200;
    [JsonPropertyName("Templates")] public List<string> Templates { get; set; } = [];
}

/// <summary>
/// config.json beside the DLL. Only the switches someone would change; the rest is fixed in code.
/// A bad file logs a warning and keeps the mask registered (it is a quest reward and may sit in a profile).
/// </summary>
public record ModConfig
{
    /// <summary>The Jackal sells the mask back (so a mask lost in a raid can be replaced).</summary>
    [JsonPropertyName("jackalBuyBack")] public bool JackalBuyBack { get; set; } = true;

    /// <summary>Quest that has to be finished before the buy-back shows. Default: "Overtime", the last Factory job of the Grunts
    /// questline, which hands the first mask out. Empty = on sale from the start.</summary>
    [JsonPropertyName("buyBackUnlockQuestId")] public string BuyBackUnlockQuestId { get; set; } = "92ffc6ee1f6b2e90992e53a9";

    [JsonPropertyName("buyBackPriceRoubles")] public int BuyBackPriceRoubles { get; set; } = 48000;
    [JsonPropertyName("buyBackLoyaltyLevel")] public int BuyBackLoyaltyLevel { get; set; } = 1;
    [JsonPropertyName("buyBackStock")] public int BuyBackStock { get; set; } = 2;
    /// <summary>Masks one character can buy per Jackal restock.</summary>
    [JsonPropertyName("buyBackLimit")] public int BuyBackLimit { get; set; } = 1;

    /// <summary>PMC bots (USEC, BEAR) may wear the mask. Off unless asked for.</summary>
    [JsonPropertyName("pmcBotsWearMask")] public bool PmcBotsWearMask { get; set; } = false;
    /// <summary>Its weight in the PMC face cover pool, relative to the stock GP-7's weight there (1.0 = as common as a GP-7).</summary>
    [JsonPropertyName("pmcBotWeightVsGp7")] public double PmcBotWeightVsGp7 { get; set; } = 1.0;

    // Fixed (not in the shipped config.json, kept for ContentGate): client folder and the receipt check.
    [JsonIgnore] public string PluginFolder { get; set; } = "AvonMask";
    [JsonIgnore] public bool RequireClientContent { get; set; } = true;
}

/// <summary>content-ready.json shipped with the client plugin; the server reads it to gate the offer and the bot pool.</summary>
public record ContentReceipt
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 2;
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("eftBuild")] public int EftBuild { get; set; } = 40743;
    [JsonPropertyName("unityVersion")] public string UnityVersion { get; set; } = "2022.3.43f1";
    [JsonPropertyName("clientDll")] public string? ClientDll { get; set; }
    [JsonPropertyName("bundles")] public List<ReceiptBundle> Bundles { get; set; } = [];
    [JsonPropertyName("modelAssets")] public Dictionary<string, string> ModelAssets { get; set; } = new();
}

public record ReceiptBundle
{
    [JsonPropertyName("file")] public required string File { get; set; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "visuals";
}
