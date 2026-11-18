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
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace WWCHelmetDisplays.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.wwchelmetdisplays.server";
    public string Name { get; init; } = "WWC Helmet Displays";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Ids come from fixed text, so a rebuild never changes a template a profile may hold.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wwchelmet|" + key))).ToLowerInvariant()[..24]);
    public const string Jackal = "672b4a010000000000000001";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
    public const string HideoutCustomization = "673c7b00cbf4b984b5099181";
    public const string Donor = "590c60fc86f77412b13fddcf";          // documents case: a real container, so every display has grids for its gear
    public const string Prefab = "assets/content/items/barter/item_barter_flyers/item_barter_flyers_girl1.bundle";   // a poster's model until the client dresses it, same as the gun displays
    public const string GridPrototype = "55d329c24bdc2d892f8b4567";
    public static readonly string[] HallOfFameStashes = ["63dbd45917fff4dee40fe16e", "65424185a57eea37ed6562e9", "6542435ea57eea37ed6562f0"];

    // Item classes (parent ids) each kind of gear belongs to.
    public const string Headwear = "5a341c4086f77401f2541505";
    public const string FaceCover = "5a341c4686f77469e155819e";
    public const string Eyewear = "5448e5724bdc2ddf718b4568";
    public const string Headphones = "5645bcb74bdc2ded0b8b4578";
    public const string Armor = "5448e54d4bdc2dcc718b4568";
    public const string Vest = "5448e5284bdc2dcb718b4567";
}

/// <summary>One display: its key (id seed, config section), names, inventory size, where it goes, and its gear grids in the order the window shows them.</summary>
internal sealed record Display(string Key, string ConfigKey, string Name, string ShortName, int Width, int Height, string Spot, string Description, (string Grid, string Kind)[] Grids)
{
    public MongoId Id => Ids.Of(Key);
    public MongoId Offer => Ids.Of("offer|" + Key);
    public MongoId GridId(string grid) => Ids.Of("grid|" + Key + "|" + grid);

    public static readonly Display[] All =
    [
        new("mount", "mount", "WWC helmet wall mount", "Helmet mount", 2, 2, "poster",
            "A walnut wall plate with a blackened steel hook and a padded crown, made by Wet Works Company. Hang it on a poster spot from Hideout Customization, walk up to it and press your interact key to hang a helmet on it.",
            [("helmet", "helmet")]),
        new("rack", "rack", "WWC helmet rack", "Helmet rack", 4, 2, "poster",
            "A long walnut board with three steel hooks and padded crowns, for three helmets. Made by Wet Works Company. Hang it on a poster spot from Hideout Customization, walk up to it and press your interact key to hang helmets on it.",
            [("left", "helmet"), ("middle", "helmet"), ("right", "helmet")]),
        new("bust", "bust", "WWC boss bust", "Boss bust", 2, 3, "statuette",
            "A head-and-shoulders bust on a turned walnut socle that wears real gear: a helmet, a mask, glasses, a headset, body armor and a rig, for showing off what you took from a boss. Made by Wet Works Company. Stand it on a statuette spot from Hideout Customization or on a big trophy shelf in the Hall of Fame, walk up to it and press your interact key to dress it.",
            [("helmet", "helmet"), ("mask", "faceCover"), ("glasses", "glasses"), ("headset", "headset"), ("armor", "bodyArmor"), ("rig", "rig")]),
        new("trophy", "trophyBust", "WWC trophy bust", "Trophy bust", 3, 3, "poster",
            "A bust that comes out of a walnut shield, hung like a hunting trophy, and wears real gear: a helmet, a mask, glasses, a headset, body armor and a rig. Made by Wet Works Company. Hang it on a poster spot from Hideout Customization, walk up to it and press your interact key to dress it.",
            [("helmet", "helmet"), ("mask", "faceCover"), ("glasses", "glasses"), ("headset", "headset"), ("armor", "bodyArmor"), ("rig", "rig")]),
    ];
}

/// <summary>Everything in config.json. A value left out keeps the default written here, and <see cref="Sane"/> pulls anything out of range back in, so an edit can't break a profile.</summary>
public record DisplayConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("gear")] public GearSettings Gear { get; set; } = new();
    [JsonPropertyName("look")] public LookSettings Look { get; set; } = new();
    [JsonPropertyName("hideout")] public HideoutSettings Hideout { get; set; } = new();
    [JsonPropertyName("mount")] public SaleSettings Mount { get; set; } = new() { PriceRoubles = 45_000, WeightKg = 1.8 };
    [JsonPropertyName("rack")] public SaleSettings Rack { get; set; } = new() { PriceRoubles = 95_000, WeightKg = 4.5 };
    [JsonPropertyName("bust")] public SaleSettings Bust { get; set; } = new() { PriceRoubles = 120_000, WeightKg = 6.0 };
    [JsonPropertyName("trophyBust")] public SaleSettings TrophyBust { get; set; } = new() { PriceRoubles = 140_000, WeightKg = 7.0 };

    public SaleSettings Sale(string key) => key switch { "rack" => Rack, "bust" => Bust, "trophyBust" => TrophyBust, _ => Mount };

    public DisplayConfig Sane()
    {
        foreach (var s in new[] { Mount, Rack, Bust, TrophyBust })
        {
            s.PriceRoubles = Math.Clamp(s.PriceRoubles, 1, 1_000_000_000);
            s.LoyaltyLevel = Math.Clamp(s.LoyaltyLevel, 1, 4);
            s.Stock = Math.Clamp(s.Stock, 1, 9_999_999);
            s.BuyLimit = Math.Clamp(s.BuyLimit, 1, 9_999_999);
            s.WeightKg = Math.Clamp(s.WeightKg, 0, 1000);
        }
        Gear.ExtraAllowedItemIds ??= [];
        Gear.BlockedItemIds ??= [];
        return this;
    }
}

public record GearSettings
{
    [JsonPropertyName("helmets")] public bool Helmets { get; set; } = true;
    [JsonPropertyName("faceCovers")] public bool FaceCovers { get; set; } = true;
    [JsonPropertyName("glasses")] public bool Glasses { get; set; } = true;
    [JsonPropertyName("headsets")] public bool Headsets { get; set; } = true;
    [JsonPropertyName("bodyArmor")] public bool BodyArmor { get; set; } = true;
    [JsonPropertyName("rigs")] public bool Rigs { get; set; } = true;
    [JsonPropertyName("extraAllowedItemIds")] public List<string> ExtraAllowedItemIds { get; set; } = [];
    [JsonPropertyName("blockedItemIds")] public List<string> BlockedItemIds { get; set; } = [];

    public bool Allows(string kind) => kind switch
    {
        "helmet" => Helmets, "faceCover" => FaceCovers, "glasses" => Glasses, "headset" => Headsets, "bodyArmor" => BodyArmor, "rig" => Rigs, _ => false,
    };
}

public record LookSettings
{
    [JsonPropertyName("wood")] public string Wood { get; set; } = "walnut";
    [JsonPropertyName("bustFinish")] public string BustFinish { get; set; } = "mannequin";
    [JsonPropertyName("lightOnByDefault")] public bool LightOnByDefault { get; set; } = true;
    [JsonPropertyName("lightBrightness")] public double LightBrightness { get; set; } = 1.0;
}

public record HideoutSettings
{
    [JsonPropertyName("bustOnStatuetteSpots")] public bool BustOnStatuetteSpots { get; set; } = true;
    [JsonPropertyName("bustInHallOfFame")] public bool BustInHallOfFame { get; set; } = true;
}

public record SaleSettings
{
    [JsonPropertyName("soldByTheJackal")] public bool SoldByTheJackal { get; set; } = true;
    [JsonPropertyName("priceRoubles")] public double PriceRoubles { get; set; } = 45_000;
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 2;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 1;
    [JsonPropertyName("weightKg")] public double WeightKg { get; set; } = 2.0;
}

internal static class Shared
{
    // People edit this file by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static DisplayConfig? loaded;
    /// <summary>True when config.json could not be read: templates stay registered, offers are skipped until it is fixed.</summary>
    public static bool Broken { get; private set; }

    public static DisplayConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (loaded is not null) return loaded;
        var path = Path.Combine(dir, "config.json");
        try { loaded = (File.Exists(path) ? JsonSerializer.Deserialize<DisplayConfig>(File.ReadAllText(path), Forgiving) : null) ?? new DisplayConfig(); }
        catch (JsonException ex) { Broken = true; logger.Error($"[WWCHelmetDisplays] config.json has a mistake near line {ex.LineNumber + 1}; the displays stay registered but The Jackal won't sell them until it's fixed. {ex.Message}"); loaded = new DisplayConfig(); }
        catch (Exception ex) { Broken = true; logger.Error($"[WWCHelmetDisplays] config.json could not be read ({ex.Message}); the displays stay registered but The Jackal won't sell them until it's fixed"); loaded = new DisplayConfig(); }
        return loaded.Sane();
    }

    /// <summary>True when the client plugin is present, so a half-installed mod never sells a display that would look like a poster.</summary>
    public static bool ClientPresent(string serverModDir)
    {
#if WWC
        return WWC.Server.Parts.ClientPresent && Directory.Exists(WWC.Server.Parts.ClientPart(WWC.Server.Parts.HelmetDisplays));
#else
        var gameRoot = Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", ".."));
        return File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "WWCHelmetDisplays", "WWCHelmetDisplays.Client.dll"));
#endif
    }

    public static void AddLocale(LocaleTable locales, Dictionary<string, string> strings)
    {
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
    }
}

/// <summary>Stage 1: the four display templates, their grids and their spots. Always registered so a profile that holds one still loads when the client is gone.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 41)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, ItemConfig itemConfig,
    RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, AirdropConfig airdropConfig, TraderConfig traderConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        if (!templates.Items.TryGetValue(Ids.Donor, out var donor)) { logger.Error("[WWCHelmetDisplays] the documents case was not found; nothing registered"); return Task.CompletedTask; }
        var cfg = Shared.Config(dir, logger);
        var handbookParent = templates.Handbook.Items.FirstOrDefault(h => h.Id == Ids.Donor)?.ParentId;
        var strings = new Dictionary<string, string>();
        var registered = new List<MongoId>();
        var blocked = Valid(cfg.Gear.BlockedItemIds, "blockedItemIds");
        var extra = Valid(cfg.Gear.ExtraAllowedItemIds, "extraAllowedItemIds");

        foreach (var d in Display.All)
        {
            var sale = cfg.Sale(d.ConfigKey);
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = d.Id,
                ItemTplToClone = Ids.Donor,
                ParentId = donor.Parent,
                NewItemName = "wwchelmet_" + d.Key,
                OverrideProperties = new TemplateItemProperties
                {
                    Width = d.Width, Height = d.Height, Weight = sale.WeightKg,
                    ExaminedByDefault = true, CanSellOnRagfair = false, CanRequireOnRagfair = false,
                    Prefab = new Prefab { Path = Ids.Prefab, Rcid = "" },
                },
                HandbookParentId = handbookParent,
                HandbookPriceRoubles = sale.PriceRoubles,
                FleaPriceRoubles = sale.PriceRoubles,
                AddToHandbook = handbookParent is not null,
                AddToFleaPriceDb = false,
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = d.Name, ShortName = d.ShortName, Description = d.Description } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[WWCHelmetDisplays] {d.Name}: {string.Join("; ", result.Errors)}"); continue; }
            var props = templates.Items[d.Id].Properties;
            if (props is null) { logger.Error($"[WWCHelmetDisplays] {d.Name} has no properties to configure"); continue; }
            // One grid per place on the display, each taking one piece of its kind. A kind switched off in config.json takes nothing.
            props.Grids = d.Grids.Select(g =>
            {
                var (w, h) = Cells(g.Kind);
                var allowed = cfg.Gear.Allows(g.Kind) ? new HashSet<MongoId>(Classes(g.Kind).Select(c => new MongoId(c))) : new HashSet<MongoId>();
                if (g.Kind == "helmet" && cfg.Gear.Helmets) allowed.UnionWith(extra);
                if (allowed.Count == 0) allowed.Add(d.Id);     // an empty allow-list would accept anything, so a closed place only "accepts" the display itself, which never fits
                return new Grid
                {
                    Name = g.Grid,
                    Id = d.GridId(g.Grid),
                    Parent = d.Id,
                    Prototype = Ids.GridPrototype,
                    Properties = new GridProperties
                    {
                        CellsH = w, CellsV = h, MinCount = 0, MaxCount = 1, MaxWeight = 0, IsSortingTable = false,
                        Filters = [new GridFilter { Filter = allowed, ExcludedFilter = new HashSet<MongoId>(blocked) }],
                    },
                };
            }).ToList();
            strings[$"{d.Id} Name"] = d.Name; strings[$"{d.Id} ShortName"] = d.ShortName; strings[$"{d.Id} Description"] = d.Description;
            registered.Add(d.Id);
            var spots = d.Spot == "poster" ? AddToSpots(d.Id, "Poster_") : cfg.Hideout.BustOnStatuetteSpots ? AddToSpots(d.Id, "Statuette_") : 0;
            if (d.Spot == "statuette" && cfg.Hideout.BustInHallOfFame) spots += AddToHallOfFame(d.Id);
            logger.Info($"[WWCHelmetDisplays] {d.Name}: {d.Grids.Length} gear place(s), allowed on {spots} hideout spot(s)");
        }
        Shared.AddLocale(locales, strings);

        // Off the flea and out of random loot, rewards and Fence: The Jackal sells them.
        ragfairConfig.Dynamic.Blacklist.Custom.UnionWith(registered);
        itemConfig.RewardItemBlacklist.UnionWith(registered);
        pmcConfig.GlobalLootBlacklist.AddRange(registered.Except(pmcConfig.GlobalLootBlacklist));
        scavCaseConfig.RewardItemBlacklist.UnionWith(registered);
        hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(registered.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
        traderConfig.Fence.Blacklist.UnionWith(registered);
        foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(registered.Except(drop.ItemBlacklist));
        logger.Success($"[WWCHelmetDisplays] {registered.Count} displays registered");
        return Task.CompletedTask;
    }

    private static (int w, int h) Cells(string kind) => kind switch
    {
        "helmet" => (3, 3),          // a 2x2 helmet grows a cell with goggles or a mount on it
        "faceCover" => (2, 2),
        "glasses" => (2, 1),
        "headset" => (2, 2),
        _ => (4, 4),                 // the biggest armor and rigs are 4x4
    };

    private static string[] Classes(string kind) => kind switch
    {
        "helmet" => [Ids.Headwear],
        "faceCover" => [Ids.FaceCover],
        "glasses" => [Ids.Eyewear],
        "headset" => [Ids.Headphones],
        "bodyArmor" => [Ids.Armor],
        "rig" => [Ids.Vest],
        _ => [],
    };

    private List<MongoId> Valid(List<string> ids, string setting)
    {
        var list = new List<MongoId>();
        foreach (var s in ids)
        {
            if (!MongoId.IsValidMongoId(s)) { logger.Warning($"[WWCHelmetDisplays] config.json: \"{s}\" in {setting} is not an item id, so it was skipped"); continue; }
            var id = new MongoId(s);
            if (!templates.Items.ContainsKey(id)) { logger.Warning($"[WWCHelmetDisplays] config.json: no item {s} in the database ({setting}), so it was skipped"); continue; }
            list.Add(id);
        }
        return list;
    }

    /// <summary>Adds the display to the allow-list of every native spot with this prefix. Only adds an id, so everything already allowed keeps working.</summary>
    private int AddToSpots(MongoId id, string prefix)
    {
        if (!templates.Items.TryGetValue(Ids.HideoutCustomization, out var stash) || stash.Properties?.Slots is null) return 0;
        int count = 0;
        foreach (var slot in stash.Properties.Slots)
        {
            if (slot.Name is null || !slot.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            foreach (var f in slot.Properties?.Filters ?? []) { if (f.Filter is not null && f.Filter.Add(id)) count++; }
        }
        return count;
    }

    /// <summary>The Hall of Fame's big trophy shelves take the bust, at all three levels of the room. Additive.</summary>
    private int AddToHallOfFame(MongoId id)
    {
        int count = 0;
        foreach (var stashId in Ids.HallOfFameStashes)
        {
            if (!templates.Items.TryGetValue(stashId, out var stash) || stash.Properties?.Slots is null) continue;
            foreach (var slot in stash.Properties.Slots)
            {
                if (slot.Name is null || !slot.Name.StartsWith("bigTrophies", StringComparison.Ordinal)) continue;
                foreach (var f in slot.Properties?.Filters ?? []) { if (f.Filter is not null && f.Filter.Add(id)) count++; }
            }
        }
        return count;
    }
}

/// <summary>Stage 2: The Jackal sells the displays. Skipped when the client is missing or config.json is broken.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 139)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled) { logger.Info("[WWCHelmetDisplays] disabled in config.json, no offers"); return Task.CompletedTask; }
        if (Shared.Broken) { logger.Warning("[WWCHelmetDisplays] config.json needs fixing; templates kept, offers skipped"); return Task.CompletedTask; }
        if (!Shared.ClientPresent(dir)) { logger.Warning("[WWCHelmetDisplays] client files are not installed; templates kept, offers skipped"); return Task.CompletedTask; }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Assort is null) { logger.Warning("[WWCHelmetDisplays] The Jackal not found; no offers"); return Task.CompletedTask; }
        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        int count = 0;
        foreach (var d in Display.All)
        {
            var sale = cfg.Sale(d.ConfigKey);
            if (!sale.SoldByTheJackal || !templates.Items.ContainsKey(d.Id) || assort.Items.Any(i => i.Id == d.Offer)) continue;
            assort.Items.Add(new Item { Id = d.Offer, Template = d.Id, ParentId = "hideout", SlotId = "hideout", Upd = new Upd { UnlimitedCount = false, StackObjectsCount = sale.Stock, BuyRestrictionMax = sale.BuyLimit, BuyRestrictionCurrent = 0 } });
            assort.BarterScheme[d.Offer] = [[new BarterScheme { Count = sale.PriceRoubles, Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[d.Offer] = sale.LoyaltyLevel;
            count++;
        }
        logger.Success($"[WWCHelmetDisplays] The Jackal sells {count} display(s)");
        return Task.CompletedTask;
    }
}
