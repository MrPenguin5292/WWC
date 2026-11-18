using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Path = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace JackalGunCase.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.jackalguncase.server";
    public string Name { get; init; } = "Jackal Gun Case";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new() { ["local.jackal"] = new("^1.0.0") };
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Ids come from fixed text, so a rebuild never changes a template a profile may hold.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jackalguncase|" + key))).ToLowerInvariant()[..24]);
    public static readonly MongoId Case = Of("case"), MainGrid = Of("case|grid|main"), StickerGrid = Of("case|grid|stickers"), CaseOffer = Of("offer|case");
    public const string Jackal = "672b4a010000000000000001";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
    public const string WeaponCase = "59fb023c86f7746d0d4b423c";
    public const string GridProto = "55d329c24bdc2d892f8b4567";
    /// <summary>The sticker mod adds its stickers to any grid with this name. See docs/sticker-case-contract.md.</summary>
    public const string StickerGridName = "jackal_stickers";
    public const string CaseName = "Jackal hard case";
    public const string CaseShort = "J-Case";
    public const string CaseText = "A long crush-proof rifle case with a gasket seal, sold by The Jackal to people who plan on getting it back. The flat recessed lid has room for stickers: open the case and drop them into the smaller grid, and each cell is a place on the lid. Inside, it holds the same as a normal weapon case.";
}

/// <summary>Everything in config.json. A value left out keeps the default written here, and <see cref="Sane"/> pulls anything out of range back in, so an edit can't break a profile.</summary>
public record CaseConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("jackal")] public JackalSettings Jackal { get; set; } = new();
    [JsonPropertyName("fleaMarket")] public FleaSettings Flea { get; set; } = new();
    [JsonPropertyName("case")] public CaseSettings Case { get; set; } = new();

    public CaseConfig Sane()
    {
        static double Money(double v) => Math.Clamp(v, 1, 1_000_000_000);
        Jackal.PriceRoubles = Money(Jackal.PriceRoubles);
        Jackal.Stock = Math.Clamp(Jackal.Stock, 1, 9_999);
        Jackal.BuyLimit = Math.Clamp(Jackal.BuyLimit, 1, 9_999);
        Jackal.LoyaltyLevel = Math.Clamp(Jackal.LoyaltyLevel, 1, 4);
        Flea.FleaPriceRoubles = Money(Flea.FleaPriceRoubles);
        Flea.TraderValueRoubles = Money(Flea.TraderValueRoubles);
        Case.Columns = Math.Clamp(Case.Columns, 1, 20);
        Case.Rows = Math.Clamp(Case.Rows, 1, 30);
        Case.WeightKg = Math.Clamp(Case.WeightKg, 0, 1000);
        return this;
    }
}

public record JackalSettings
{
    [JsonPropertyName("sell")] public bool Sell { get; set; } = true;
    [JsonPropertyName("priceRoubles")] public double PriceRoubles { get; set; } = 1_250_000;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 2;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 1;
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
}

public record FleaSettings
{
    [JsonPropertyName("sell")] public bool Sell { get; set; }
    [JsonPropertyName("fleaPriceRoubles")] public double FleaPriceRoubles { get; set; } = 1_500_000;
    [JsonPropertyName("traderValueRoubles")] public double TraderValueRoubles { get; set; } = 900_000;
}

public record CaseSettings
{
    [JsonPropertyName("columns")] public int Columns { get; set; } = 5;
    [JsonPropertyName("rows")] public int Rows { get; set; } = 10;
    [JsonPropertyName("weightKg")] public double WeightKg { get; set; } = 9;
}

internal static class Shared
{
    // People edit this file by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static CaseConfig? loaded;

    public static CaseConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (loaded is not null) return loaded;
        var path = Path.Combine(dir, "config.json");
        try { loaded = (File.Exists(path) ? JsonSerializer.Deserialize<CaseConfig>(File.ReadAllText(path), Forgiving) : null) ?? new CaseConfig(); }
        catch (JsonException ex) { logger.Error($"[JackalGunCase] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values until that's fixed. {ex.Message}"); loaded = new CaseConfig(); }
        catch (Exception ex) { logger.Error($"[JackalGunCase] config.json could not be read ({ex.Message}), so the mod is using its normal values"); loaded = new CaseConfig(); }
        return loaded.Sane();
    }

    /// <summary>True when the client plugin is present, so a half-installed mod never sells a case that would show as a plain weapon case.</summary>
    public static bool ClientPresent(string serverModDir)
    {
#if WWC
        return WWC.Server.Parts.ClientPresent && Directory.Exists(WWC.Server.Parts.ClientPart(WWC.Server.Parts.GunCase));
#else
        var gameRoot = Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", ".."));
        return File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "JackalGunCase", "JackalGunCase.Client.dll"));
#endif
    }
}

/// <summary>Stage 1: the template. Always registered so a profile that holds the case still loads when the client is gone. Runs before the sticker mod adds its ids at Preload + 900.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 90)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, ItemConfig itemConfig,
    RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, TraderConfig traderConfig, AirdropConfig airdropConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        if (!templates.Items.TryGetValue(Ids.WeaponCase, out var donor))
        {
            logger.Error("[JackalGunCase] donor item not found; nothing registered");
            return Task.CompletedTask;
        }
        var cfg = Shared.Config(dir, logger);
        var clientThere = cfg.ModEnabled && Shared.ClientPresent(dir);
        var handbookParent = templates.Handbook.Items.FirstOrDefault(h => h.Id == Ids.WeaponCase)?.ParentId;

        var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
        {
            NewId = Ids.Case,
            ItemTplToClone = Ids.WeaponCase,
            ParentId = donor.Parent,
            NewItemName = "jackalguncase_case",
            OverrideProperties = new TemplateItemProperties
            {
                Weight = cfg.Case.WeightKg,
                ExaminedByDefault = true, CanSellOnRagfair = clientThere && cfg.Flea.Sell, CanRequireOnRagfair = false,
            },
            HandbookParentId = handbookParent,
            HandbookPriceRoubles = cfg.Flea.TraderValueRoubles,
            FleaPriceRoubles = cfg.Flea.FleaPriceRoubles,
            AddToHandbook = handbookParent is not null,
            AddToFleaPriceDb = true,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = Ids.CaseName, ShortName = Ids.CaseShort, Description = Ids.CaseText } },
        }, Assembly.GetExecutingAssembly());
        if (!result.Success)
        {
            logger.Error($"[JackalGunCase] the case could not be registered: {string.Join("; ", result.Errors)}");
            return Task.CompletedTask;
        }

        ConfigureGrids(cfg.Case);
        AllowWhereWeaponCaseGoes();
        KeepOutOfLootTables(cfg);

        var strings = new Dictionary<string, string> { [$"{Ids.Case} Name"] = Ids.CaseName, [$"{Ids.Case} ShortName"] = Ids.CaseShort, [$"{Ids.Case} Description"] = Ids.CaseText };
        foreach (var (lang, lazy) in locales.Global)
        {
            var overwrite = lang.Equals("en", StringComparison.OrdinalIgnoreCase);
            lazy.AddTransformer(dict =>
            {
                if (dict is null) return dict;
                foreach (var (k, v) in strings)
                {
                    if (overwrite) dict[k] = v; else dict.TryAdd(k, v);
                }
                return dict;
            });
        }
        logger.Success($"[JackalGunCase] gun case registered: {cfg.Case.Columns}x{cfg.Case.Rows} inside, 5x3 sticker spots");
        return Task.CompletedTask;
    }

    /// <summary>The donor's grid keeps its filter and takes the configured size; a second grid holds the stickers. Its filter starts with only the case's own id, so nothing fits until the sticker mod adds its ids at Preload + 900.</summary>
    private void ConfigureGrids(CaseSettings size)
    {
        var props = templates.Items[Ids.Case].Properties;
        var main = props?.Grids?.FirstOrDefault();
        if (main?.Properties is null) { logger.Error("[JackalGunCase] the case has no grid to configure"); return; }
        main.Id = Ids.MainGrid;
        main.Parent = Ids.Case;
        main.Properties.CellsH = size.Columns;
        main.Properties.CellsV = size.Rows;
        var stickers = new Grid
        {
            Name = Ids.StickerGridName,
            Id = Ids.StickerGrid,
            Parent = Ids.Case,
            Prototype = Ids.GridProto,
            Properties = new GridProperties
            {
                CellsH = 5, CellsV = 3, MinCount = 0, MaxCount = 0, MaxWeight = 0, IsSortingTable = false,
                Filters = [new GridFilter { Filter = [Ids.Case], ExcludedFilter = [] }],
            },
        };
        props!.Grids = [main, stickers];
    }

    /// <summary>Lets the case go everywhere the weapon case can: every container with an allow-list naming it. Only adds an id, so nothing else changes.</summary>
    private void AllowWhereWeaponCaseGoes()
    {
        var added = 0;
        var weaponCase = new MongoId(Ids.WeaponCase);
        foreach (var (_, item) in templates.Items)
        {
            if (item.Id == Ids.Case) continue;
            foreach (var grid in item.Properties?.Grids ?? [])
            {
                foreach (var filter in grid.Properties?.Filters ?? [])
                {
                    if (filter.Filter is null || !filter.Filter.Contains(weaponCase)) continue;
                    if (filter.Filter.Add(Ids.Case)) added++;
                }
            }
        }
        logger.Info($"[JackalGunCase] the case fits in {added} container grids that take the weapon case");
    }

    /// <summary>Sold by The Jackal, not handed out by loot tables, rewards or a random trader's stock.</summary>
    private void KeepOutOfLootTables(CaseConfig cfg)
    {
        MongoId[] ids = [Ids.Case];
        itemConfig.RewardItemBlacklist.UnionWith(ids);
        if (!cfg.Flea.Sell) ragfairConfig.Dynamic.Blacklist.Custom.UnionWith(ids);
        pmcConfig.GlobalLootBlacklist.AddRange(ids.Except(pmcConfig.GlobalLootBlacklist));
        scavCaseConfig.RewardItemBlacklist.UnionWith(ids);
        hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(ids.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
        traderConfig.Fence.Blacklist.UnionWith(ids);
        foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(ids.Except(drop.ItemBlacklist));
    }
}

/// <summary>Stage 2: The Jackal's offer, after he registers at TraderRegistration + 100. Skipped when the client files are missing.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 150)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Jackal.Sell) { logger.Info("[JackalGunCase] off sale in config.json"); return Task.CompletedTask; }
        if (!Shared.ClientPresent(dir)) { logger.Warning("[JackalGunCase] client files are not installed; the case stays registered but is not sold"); return Task.CompletedTask; }
        if (!templates.Items.ContainsKey(Ids.Case)) return Task.CompletedTask;
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Assort is null) { logger.Warning("[JackalGunCase] The Jackal not found; no offer"); return Task.CompletedTask; }
        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        if (assort.Items.Any(i => i.Id == Ids.CaseOffer)) return Task.CompletedTask;
        assort.Items.Add(new Item
        {
            Id = Ids.CaseOffer,
            Template = Ids.Case,
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd { UnlimitedCount = false, StackObjectsCount = cfg.Jackal.Stock, BuyRestrictionMax = cfg.Jackal.BuyLimit, BuyRestrictionCurrent = 0 },
        });
        assort.BarterScheme[Ids.CaseOffer] = [[new BarterScheme { Count = Math.Max(1, cfg.Jackal.PriceRoubles), Template = new MongoId(Ids.Rouble) }]];
        assort.LoyalLevelItems[Ids.CaseOffer] = cfg.Jackal.LoyaltyLevel;
        logger.Success($"[JackalGunCase] The Jackal sells the gun case ({cfg.Jackal.PriceRoubles:0} RUB, loyalty {cfg.Jackal.LoyaltyLevel})");
        return Task.CompletedTask;
    }
}
