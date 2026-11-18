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

namespace RCBombCar.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.rcbombcar.server";
    public string Name { get; init; } = "RC bomb car";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.3.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }   // The Jackal (WWC) is only needed when the seller is "jackal"
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Ids come from fixed text, so a rebuild never changes a template a profile may hold.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("rcbombcar|" + key))).ToLowerInvariant()[..24]);
    public static readonly MongoId Car = Of("item|car");
    public static readonly MongoId Offer = Of("offer|car");
    public const string Donor = "61605e13ffa6e502ac5e7eef";      // Vortex Ranger 1500 rangefinder: special slots and quick slots take its item type
    public const string Jackal = "672b4a010000000000000001";
    public const string Prapor = "54cb50c76803fa8b248b4571";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
}

public record ItemSpec
{
    [JsonPropertyName("name")] public string Name { get; set; } = "RC bomb car";
    [JsonPropertyName("shortName")] public string ShortName { get; set; } = "RC bomb";
    [JsonPropertyName("description")] public string Description { get; set; } = "A 1/10 scale RC Toyota Hilux with a pound of C4 taped down in the bed and a blasting cap wired into the receiver.";
    [JsonPropertyName("weight")] public double Weight { get; set; } = 2.4;
}

public record CraftSpec
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("areaLevel")] public int AreaLevel { get; set; } = 2;
    [JsonPropertyName("hours")] public double Hours { get; set; } = 4;
    [JsonPropertyName("count")] public int Count { get; set; } = 1;
    [JsonPropertyName("inputs")] public Dictionary<string, int> Inputs { get; set; } = new()
    {
        ["5d1b2fa286f77425227d1674"] = 1,   // Electric motor
        ["590a358486f77429692b2790"] = 2,   // Rechargeable battery
        ["590a3b0486f7743954552bdb"] = 1,   // Printed circuit board
        ["5c06779c86f77426e00dd782"] = 2,   // Bundle of wires
        ["60391b0fb847c71012789415"] = 2,   // TP-200 TNT brick
        ["5e2af29386f7746d4159f077"] = 1,   // KEKTAPE duct tape
    };
    [JsonPropertyName("tools")] public List<string> Tools { get; set; } = ["590c2e1186f77425357b6124"];   // Toolset
}

public record OfferSpec
{
    [JsonPropertyName("sell")] public bool Sell { get; set; } = false;
    [JsonPropertyName("seller")] public string Seller { get; set; } = "jackal";
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 2;
    [JsonPropertyName("price")] public int Price { get; set; } = 35000;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 3;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 2;
}

public record CarConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("item")] public ItemSpec Item { get; set; } = new();
    [JsonPropertyName("offer")] public OfferSpec Offer { get; set; } = new();
    [JsonPropertyName("fleaMarket")] public bool FleaMarket { get; set; }
    [JsonPropertyName("keepOutOfOtherLoot")] public bool KeepOutOfOtherLoot { get; set; } = true;
    [JsonPropertyName("questline")] public bool Questline { get; set; } = true;
    [JsonPropertyName("craft")] public CraftSpec Craft { get; set; } = new();
    // the "car" block is read by the client; the server only passes it through
    [JsonPropertyName("car")] public JsonElement? Car { get; set; }

    public CarConfig Sane()
    {
        Offer.LoyaltyLevel = Math.Clamp(Offer.LoyaltyLevel, 1, 4);
        Offer.Price = Math.Max(1, Offer.Price);
        Offer.Stock = Math.Max(1, Offer.Stock);
        Offer.BuyLimit = Math.Max(1, Offer.BuyLimit);
        Offer.Seller = string.Equals(Offer.Seller?.Trim(), "prapor", StringComparison.OrdinalIgnoreCase) ? "prapor" : "jackal";
        Item.Weight = Math.Clamp(Item.Weight, 0.1, 20);
        Craft.AreaLevel = Math.Clamp(Craft.AreaLevel, 1, 3);
        Craft.Hours = Math.Clamp(Craft.Hours, 0.02, 72);
        Craft.Count = Math.Clamp(Craft.Count, 1, 10);
        Craft.Inputs ??= new();
        Craft.Tools ??= [];
        if (string.IsNullOrWhiteSpace(Item.Name)) Item.Name = "RC bomb car";
        if (string.IsNullOrWhiteSpace(Item.ShortName)) Item.ShortName = "RC bomb";
        return this;
    }
}

internal static class Shared
{
    // People edit the json by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static CarConfig? config;

    public static CarConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (config is not null) return config;
        var path = Path.Combine(dir, "config.json");
        try { config = (File.Exists(path) ? JsonSerializer.Deserialize<CarConfig>(File.ReadAllText(path), Forgiving) : null) ?? new CarConfig(); }
        catch (JsonException ex) { logger.Error($"[RC car] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values until that's fixed. {ex.Message}"); config = new CarConfig(); }
        catch (Exception ex) { logger.Error($"[RC car] config.json could not be read ({ex.Message}), so the mod is using its normal values"); config = new CarConfig(); }
        return config = config.Sane();
    }

#if WWC   // built into WWC: its client files are in BepInEx\plugins\WWC\RCBombCar and its code is in WWC.Client.dll
    public static string PluginDir(string serverModDir) => WWC.Server.Parts.ClientPart(WWC.Server.Parts.RCBombCar);

    /// <summary>True when the client plugin is on disk, so nobody sells a car that would only work as a rangefinder.</summary>
    public static bool ClientHas(string serverModDir) => WWC.Server.Parts.ClientPresent && Directory.Exists(PluginDir(serverModDir));
#else
    public static string PluginDir(string serverModDir) =>
        Path.Combine(Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", "..")), "BepInEx", "plugins", "RCBombCar");

    /// <summary>True when the client plugin is on disk, so nobody sells a car that would only work as a rangefinder.</summary>
    public static bool ClientHas(string serverModDir) => File.Exists(Path.Combine(PluginDir(serverModDir), "RCBombCar.Client.dll"));
#endif
}

/// <summary>Stage 1: the car template (a clone of the Vortex Ranger 1500, so pocket special slots and quick slots take it) and its text.
/// Always registered, so a profile holding a car still loads without the client.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 78)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales,
    ItemConfig itemConfig, RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, TraderConfig traderConfig, AirdropConfig airdropConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        var donorId = new MongoId(Ids.Donor);
        if (!templates.Items.TryGetValue(donorId, out var donor)) { logger.Error("[RC car] the Vortex Ranger 1500 template is missing; no car registered"); return Task.CompletedTask; }
        var handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == donorId);
        var name = cfg.Item.Name.Trim(); var shortName = cfg.Item.ShortName.Trim(); var description = cfg.Item.Description.Trim();
        var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
        {
            NewId = Ids.Car,
            ItemTplToClone = donorId,
            ParentId = donor.Parent,
            NewItemName = "rcbombcar",
            OverrideProperties = new TemplateItemProperties { ExaminedByDefault = true, CanSellOnRagfair = cfg.FleaMarket, CanRequireOnRagfair = false },
            HandbookParentId = handbook?.ParentId,
            HandbookPriceRoubles = Math.Round(cfg.Offer.Price * 0.55),
            FleaPriceRoubles = cfg.Offer.Price,
            AddToHandbook = handbook is not null,
            AddToFleaPriceDb = true,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = name, ShortName = shortName, Description = description } },
        }, Assembly.GetExecutingAssembly());
        if (!result.Success) { logger.Error($"[RC car] {string.Join("; ", result.Errors)}"); return Task.CompletedTask; }
        var props = templates.Items[Ids.Car].Properties!;
        props.Weight = cfg.Item.Weight;
        props.Width = 2;
        props.Height = 1;
        props.BackgroundColor = "red";
        var strings = new Dictionary<string, string> { [$"{Ids.Car} Name"] = name, [$"{Ids.Car} ShortName"] = shortName, [$"{Ids.Car} Description"] = description };
        foreach (var (lang, lazy) in locales.Global)
        {
            var overwrite = lang.Equals("en", StringComparison.OrdinalIgnoreCase);
            lazy.AddTransformer(dict =>
            {
                if (dict is null) return dict;
                foreach (var (k, v) in strings) { if (overwrite) dict[k] = v; else dict.TryAdd(k, v); }
                return dict;
            });
        }
        if (cfg.KeepOutOfOtherLoot)
        {
            List<MongoId> added = [Ids.Car];
            itemConfig.RewardItemBlacklist.UnionWith(added);
            pmcConfig.GlobalLootBlacklist.AddRange(added.Except(pmcConfig.GlobalLootBlacklist));
            scavCaseConfig.RewardItemBlacklist.UnionWith(added);
            hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(added.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
            traderConfig.Fence.Blacklist.UnionWith(added);
            foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(added.Except(drop.ItemBlacklist));
        }
        if (!cfg.FleaMarket) ragfairConfig.Dynamic.Blacklist.Custom.Add(Ids.Car);
        logger.Success($"[RC car] {name} registered ({Ids.Car})");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 2: the offer, on The Jackal (after he builds his stock at TraderRegistration + 100) or Prapor. Not sold while the client is missing.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 178)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Offer.Sell) { logger.Info("[RC car] no trader sells it (craft only, as set in config.json)"); return Task.CompletedTask; }
        if (!templates.Items.ContainsKey(Ids.Car)) return Task.CompletedTask;
        if (!Shared.ClientHas(dir)) { logger.Warning("[RC car] the client files are missing (BepInEx/plugins/RCBombCar), so nobody sells the car"); return Task.CompletedTask; }
        var jackal = cfg.Offer.Seller == "jackal";
        var who = jackal ? "The Jackal" : "Prapor";
        var trader = traders.GetTrader(new MongoId(jackal ? Ids.Jackal : Ids.Prapor));
        if (trader?.Assort is null) { logger.Warning($"[RC car] {who} not found{(jackal ? " (is the WWC mod installed?)" : "")}; the car is not sold"); return Task.CompletedTask; }
        var assort = trader.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        if (assort.Items.Any(i => i.Id == Ids.Offer)) return Task.CompletedTask;
        assort.Items.Add(new Item
        {
            Id = Ids.Offer, Template = Ids.Car, ParentId = "hideout", SlotId = "hideout",
            Upd = new Upd { UnlimitedCount = false, StackObjectsCount = cfg.Offer.Stock, BuyRestrictionMax = cfg.Offer.BuyLimit, BuyRestrictionCurrent = 0 },
        });
        assort.BarterScheme[Ids.Offer] = [[new BarterScheme { Count = cfg.Offer.Price, Template = new MongoId(Ids.Rouble) }]];
        assort.LoyalLevelItems[Ids.Offer] = cfg.Offer.LoyaltyLevel;
        logger.Success($"[RC car] {who} sells it at loyalty {cfg.Offer.LoyaltyLevel} for {cfg.Offer.Price} roubles");
        return Task.CompletedTask;
    }
}
