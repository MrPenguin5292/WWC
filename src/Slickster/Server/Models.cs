using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;   // TemplateItemProperties
using SPTarkov.Server.Core.Models.Spt.Mod;             // LocaleDetails
using SPTarkov.Server.Core.Models.Spt.Tables;          // Preset

namespace Slickster.Server;

/// <summary>
/// One entry of db/CustomItems/*.json, keyed by the new template id. This is the WTT-CommonLib CustomItems
/// schema as written by tools/sptkit.py (clone, preset). Fields the template does not act on are kept so the
/// file can move to WTT-CommonLib unchanged.
/// </summary>
public record CustomItemEntry
{
    [JsonPropertyName("itemTplToClone")]
    public required string ItemTplToClone { get; set; }

    [JsonPropertyName("parentId")]
    public required string ParentId { get; set; }

    [JsonPropertyName("handbookParentId")]
    public string? HandbookParentId { get; set; }

    [JsonPropertyName("newName")]
    public string? NewName { get; set; }

    [JsonPropertyName("overrideProperties")]
    public TemplateItemProperties? OverrideProperties { get; set; }

    [JsonPropertyName("locales")]
    public Dictionary<string, LocaleDetails> Locales { get; set; } = new();

    [JsonPropertyName("fleaPriceRoubles")]
    public double? FleaPriceRoubles { get; set; }

    [JsonPropertyName("handbookPriceRoubles")]
    public double? HandbookPriceRoubles { get; set; }

    [JsonPropertyName("registerInHandbook")]
    public bool? RegisterInHandbook { get; set; }

    [JsonPropertyName("registerInFleaPrices")]
    public bool? RegisterInFleaPrices { get; set; }

    [JsonPropertyName("addtoSecureFilters")]
    public bool? AddToSecureFilters { get; set; }

    [JsonPropertyName("addtoBots")]
    public bool? AddToBots { get; set; }

    [JsonPropertyName("addtoStaticLootContainers")]
    public bool? AddToStaticLootContainers { get; set; }

    [JsonPropertyName("addtoTraders")]
    public bool? AddToTraders { get; set; }

    [JsonPropertyName("addWeaponPreset")]
    public bool? AddWeaponPreset { get; set; }

    [JsonPropertyName("weaponPresets")]
    public List<Preset>? WeaponPresets { get; set; }

    [JsonPropertyName("masteries")]
    public bool? Masteries { get; set; }

    [JsonPropertyName("masterySections")]
    public List<MasterySection>? MasterySections { get; set; }
}

public record MasterySection
{
    [JsonPropertyName("Name")]
    public required string Name { get; set; }

    [JsonPropertyName("Level2")]
    public int Level2 { get; set; } = 100;

    [JsonPropertyName("Level3")]
    public int Level3 { get; set; } = 200;

    [JsonPropertyName("Templates")]
    public List<string> Templates { get; set; } = [];
}

/// <summary>config.json beside the DLL. Bad values log a warning, keep templates and skip offers.</summary>
public record ModConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Folder under BepInEx/plugins/ that holds the client DLL, bundles and content-ready.json.</summary>
    [JsonPropertyName("pluginFolder")]
    public string PluginFolder { get; set; } = "Template";

    /// <summary>When true, trader offers are only added if content-ready.json matches the client files.</summary>
    [JsonPropertyName("requireClientContent")]
    public bool RequireClientContent { get; set; } = true;

    /// <summary>Alias -> 24-hex trader id, for assort files keyed by alias ("jackal").</summary>
    [JsonPropertyName("traderAliases")]
    public Dictionary<string, string> TraderAliases { get; set; } = new();

    [JsonPropertyName("offersEnabled")]
    public bool OffersEnabled { get; set; } = true;

    /// <summary>Allowed price range for barter rows (roubles or other currency count).</summary>
    [JsonPropertyName("minPrice")]
    public double MinPrice { get; set; } = 1;

    [JsonPropertyName("maxPrice")]
    public double MaxPrice { get; set; } = 10_000_000;

    [JsonPropertyName("maxStack")]
    public double MaxStack { get; set; } = 100_000;
}

/// <summary>
/// content-ready.json shipped with the client plugin (same shape the Bobbleheads mod uses, generalised).
/// The server reads it to gate offers; the client reads it to know which bundle and prefabs to load.
/// </summary>
public record ContentReceipt
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("eftBuild")]
    public int EftBuild { get; set; } = 40743;

    [JsonPropertyName("unityVersion")]
    public string UnityVersion { get; set; } = "2022.3.43f1";

    [JsonPropertyName("clientDll")]
    public string? ClientDll { get; set; }

    [JsonPropertyName("bundles")]
    public List<ReceiptBundle> Bundles { get; set; } = [];

    /// <summary>template id -> prefab name inside the visuals bundle</summary>
    [JsonPropertyName("modelAssets")]
    public Dictionary<string, string> ModelAssets { get; set; } = new();
}

public record ReceiptBundle
{
    [JsonPropertyName("file")]
    public required string File { get; set; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; set; }

    /// <summary>visuals | animations | audio</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "visuals";
}

