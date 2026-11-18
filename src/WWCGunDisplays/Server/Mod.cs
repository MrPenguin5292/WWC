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

namespace WWCGunDisplays.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.wwcgundisplays.server";
    public string Name { get; init; } = "WWC Gun Displays";
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
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wwcgun|" + key))).ToLowerInvariant()[..24]);
    public const string Jackal = "672b4a010000000000000001";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
    public const string HideoutCustomization = "673c7b00cbf4b984b5099181";
    public const string Donor = "590c60fc86f77412b13fddcf";            // docs case: a real container, so a display has grids its guns go in
    public const string Prefab = "assets/content/items/barter/item_barter_flyers/item_barter_flyers_girl1.bundle";  // a poster's model until the client dresses it, so the hideout hangs it like a poster
    public const string GridProto = "55d329c24bdc2d892f8b4567";

    // Weapon classes as the database names them. Machine guns (every LMG in the game), grenade launchers, rocket launchers and flare guns never fit.
    public static readonly (string Key, string Id)[] Classes =
    [
        ("assaultRifles", "5447b5f14bdc2d61278b4567"), ("assaultCarbines", "5447b5fc4bdc2d87278b4567"), ("marksmanRifles", "5447b6194bdc2d67278b4567"),
        ("sniperRifles", "5447b6254bdc2dc3278b4568"), ("smgs", "5447b5e04bdc2d62278b4567"), ("shotguns", "5447b6094bdc2dc3278b4567"),
        ("pistols", "5447b5cf4bdc2d65278b4567"), ("revolvers", "617f1ef5e8b54b0998387733"),
    ];
    public static readonly string[] NeverAllowed = ["5447bed64bdc2d97278b4568", "5447bedf4bdc2d87278b4568", "5447bee84bdc2dc3278b4569", "67446d4f04141c10630604e7"];

    public static MongoId Display(string key) => Of(key);
    public static MongoId Grid(string key, int n) => Of(key + "|grid|" + n);
    public static MongoId Offer(string key) => Of("offer|" + key);
}

/// <summary>The displays: how many guns each holds, what the game calls it, and whether it takes handguns only.</summary>
internal sealed record DisplayKind(string Key, int Guns, string Name, string ShortName, string Description, int Width, int Height, bool HandgunsOnly = false);

internal static class Kinds
{
    public static readonly DisplayKind[] All =
    [
        new("hanger1", 1, "WWC gun hanger", "Gun hanger",
            "A walnut wall board with two rubber-sleeved steel hooks, made by Wet Works Company. Hang it on a poster spot from Hideout Customization, then walk up and press your interact key to put a gun on it. Rifles rest on the hooks and handguns sit on a magnet pad; the board fits itself to the gun. Machine guns don't fit.", 5, 1),
        new("hanger3", 3, "WWC triple gun hanger", "3-gun hanger",
            "A tall walnut wall board from Wet Works Company that holds three guns, one above the other, on rubber-sleeved hooks or magnet pads. Hang it on a poster spot from Hideout Customization and press your interact key to load it. Rifles, shotguns, SMGs and handguns fit; machine guns don't.", 5, 3),
        new("shadowbox", 1, "WWC gun shadow box", "Shadow box",
            "A glass-front shadow box with a moulded wood frame, a felt back and its own light, made by Wet Works Company. It builds itself around the gun you put in it. Hang it on a poster spot from Hideout Customization and press your interact key to open it. Rifles, shotguns, SMGs and handguns fit; machine guns don't.", 5, 2),
        new("pistolbox", 1, "WWC pistol shadow box", "Pistol box",
            "A small glass-front shadow box for one pistol or revolver, with a deep moulded frame, a felt back, an engraved brass name plate and its own light, made by Wet Works Company. Hang it on a poster spot from Hideout Customization and press your interact key to open it. Handguns only.", 3, 2, true),
    ];
}

/// <summary>Everything in config.json. A value left out keeps the default written here, and <see cref="Sane"/> pulls anything out of range back in, so an edit can't break a profile.</summary>
public record DisplayConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("guns")] public GunSettings Guns { get; set; } = new();
    [JsonPropertyName("gunHanger")] public SaleSettings Hanger1 { get; set; } = new() { PriceRoubles = 45_000, WeightKg = 2.5 };
    [JsonPropertyName("tripleGunHanger")] public SaleSettings Hanger3 { get; set; } = new() { PriceRoubles = 110_000, WeightKg = 7 };
    [JsonPropertyName("shadowBox")] public SaleSettings ShadowBox { get; set; } = new() { PriceRoubles = 150_000, WeightKg = 9 };
    [JsonPropertyName("pistolShadowBox")] public SaleSettings PistolBox { get; set; } = new() { PriceRoubles = 85_000, WeightKg = 3.5 };

    public SaleSettings For(string key) => key switch { "hanger3" => Hanger3, "shadowbox" => ShadowBox, "pistolbox" => PistolBox, _ => Hanger1 };

    public DisplayConfig Sane()
    {
        foreach (var s in new[] { Hanger1, Hanger3, ShadowBox, PistolBox })
        {
            s.PriceRoubles = Math.Clamp(s.PriceRoubles, 1, 1_000_000_000); s.LoyaltyLevel = Math.Clamp(s.LoyaltyLevel, 1, 4);
            s.Stock = Math.Clamp(s.Stock, 1, 9_999_999); s.BuyLimit = Math.Clamp(s.BuyLimit, 1, 9_999_999); s.WeightKg = Math.Clamp(s.WeightKg, 0, 1000);
        }
        Guns.GridWidth = Math.Clamp(Guns.GridWidth, 4, 20); Guns.GridHeight = Math.Clamp(Guns.GridHeight, 2, 10);
        return this;
    }
}

public record GunSettings
{
    [JsonPropertyName("assaultRifles")] public bool AssaultRifles { get; set; } = true;
    [JsonPropertyName("assaultCarbines")] public bool AssaultCarbines { get; set; } = true;
    [JsonPropertyName("marksmanRifles")] public bool MarksmanRifles { get; set; } = true;
    [JsonPropertyName("sniperRifles")] public bool SniperRifles { get; set; } = true;
    [JsonPropertyName("smgs")] public bool Smgs { get; set; } = true;
    [JsonPropertyName("shotguns")] public bool Shotguns { get; set; } = true;
    [JsonPropertyName("pistols")] public bool Pistols { get; set; } = true;
    [JsonPropertyName("revolvers")] public bool Revolvers { get; set; } = true;
    [JsonPropertyName("extraAllowedItemIds")] public List<string> ExtraAllowed { get; set; } = [];
    [JsonPropertyName("blockedItemIds")] public List<string> Blocked { get; set; } = [];
    [JsonPropertyName("gridWidth")] public int GridWidth { get; set; } = 12;
    [JsonPropertyName("gridHeight")] public int GridHeight { get; set; } = 4;

    public bool Allows(string key) => key switch
    {
        "assaultRifles" => AssaultRifles, "assaultCarbines" => AssaultCarbines, "marksmanRifles" => MarksmanRifles, "sniperRifles" => SniperRifles,
        "smgs" => Smgs, "shotguns" => Shotguns, "pistols" => Pistols, "revolvers" => Revolvers, _ => false,
    };
}

public record SaleSettings
{
    [JsonPropertyName("soldByTheJackal")] public bool SoldByTheJackal { get; set; } = true;
    [JsonPropertyName("priceRoubles")] public double PriceRoubles { get; set; } = 45_000;
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 2;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 1;
    [JsonPropertyName("weightKg")] public double WeightKg { get; set; } = 3;
}

internal static class Shared
{
    // People edit this file by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static DisplayConfig? loaded;
    public static bool ConfigBroken { get; private set; }

    public static DisplayConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (loaded is not null) return loaded;
        var path = Path.Combine(dir, "config.json");
        try { loaded = (File.Exists(path) ? JsonSerializer.Deserialize<DisplayConfig>(File.ReadAllText(path), Forgiving) : null) ?? new DisplayConfig(); }
        catch (JsonException ex) { logger.Error($"[WWC Gun Displays] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values and selling nothing until that's fixed. {ex.Message}"); loaded = new DisplayConfig(); ConfigBroken = true; }
        catch (Exception ex) { logger.Error($"[WWC Gun Displays] config.json could not be read ({ex.Message}), so the mod is using its normal values and selling nothing"); loaded = new DisplayConfig(); ConfigBroken = true; }
        return loaded.Sane();
    }

    /// <summary>True when the client plugin is present, so a half-installed mod never sells a display that would hang as a plain poster.</summary>
    public static bool ClientPresent(string serverModDir)
    {
#if WWC
        return WWC.Server.Parts.ClientPresent && Directory.Exists(WWC.Server.Parts.ClientPart(WWC.Server.Parts.GunDisplays));
#else
        var gameRoot = Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", ".."));
        return File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "WWCGunDisplays", "WWCGunDisplays.Client.dll"));
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

/// <summary>Stage 1: templates. Always registered, so a profile that holds a display (and the guns in it) still loads when the client is gone.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 41)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, ItemConfig itemConfig,
    RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, AirdropConfig airdropConfig, TraderConfig traderConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        if (!templates.Items.TryGetValue(Ids.Donor, out var donor)) { logger.Error("[WWC Gun Displays] the docs case donor was not found; nothing registered"); return Task.CompletedTask; }
        var cfg = Shared.Config(dir, logger);
        var filter = AllowedGuns(cfg, false);
        var handguns = AllowedGuns(cfg, true);
        var excluded = new HashSet<MongoId>(Ids.NeverAllowed.Select(i => new MongoId(i)));
        foreach (var b in cfg.Guns.Blocked)
        {
            if (MongoId.IsValidMongoId(b)) excluded.Add(new MongoId(b));
            else logger.Warning($"[WWC Gun Displays] config.json: \"{b}\" in blockedItemIds is not an item id, so it was skipped");
        }
        var handbookParent = templates.Handbook.Items.FirstOrDefault(h => h.Id == Ids.Donor)?.ParentId;
        var strings = new Dictionary<string, string>();
        var registered = new List<MongoId>();
        foreach (var kind in Kinds.All)
        {
            var sale = cfg.For(kind.Key);
            var id = Ids.Display(kind.Key);
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = Ids.Donor,
                ParentId = donor.Parent,
                NewItemName = "wwcgun_" + kind.Key,
                OverrideProperties = new TemplateItemProperties
                {
                    Width = kind.Width, Height = kind.Height, Weight = sale.WeightKg,
                    ExaminedByDefault = true, CanSellOnRagfair = false, CanRequireOnRagfair = false,
                    Prefab = new Prefab { Path = Ids.Prefab, Rcid = "" },
                },
                HandbookParentId = handbookParent,
                HandbookPriceRoubles = sale.PriceRoubles,
                FleaPriceRoubles = sale.PriceRoubles,
                AddToHandbook = handbookParent is not null,
                AddToFleaPriceDb = false,
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = kind.Name, ShortName = kind.ShortName, Description = kind.Description } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[WWC Gun Displays] {kind.Name}: {string.Join("; ", result.Errors)}"); continue; }
            var props = templates.Items[id].Properties;
            if (props is null) { logger.Error($"[WWC Gun Displays] {kind.Name} has no properties to configure"); continue; }
            // One grid per gun position, each holding exactly one gun: the client hangs whatever is in grid n at position n.
            var grids = new List<Grid>();
            for (int n = 1; n <= kind.Guns; n++)
            {
                grids.Add(new Grid
                {
                    Name = "gun_" + n, Id = Ids.Grid(kind.Key, n), Parent = id, Prototype = Ids.GridProto,
                    Properties = new GridProperties
                    {
                        CellsH = cfg.Guns.GridWidth, CellsV = cfg.Guns.GridHeight, MinCount = 0, MaxCount = 1, MaxWeight = 0, IsSortingTable = false,
                        Filters = [new GridFilter { Filter = new HashSet<MongoId>(kind.HandgunsOnly ? handguns : filter), ExcludedFilter = new HashSet<MongoId>(excluded) }],
                    },
                });
            }
            props.Grids = grids;
            strings[$"{id} Name"] = kind.Name; strings[$"{id} ShortName"] = kind.ShortName; strings[$"{id} Description"] = kind.Description;
            registered.Add(id);
        }
        Shared.AddLocale(locales, strings);
        // Any poster spot takes them.
        if (templates.Items.TryGetValue(Ids.HideoutCustomization, out var stash) && stash.Properties?.Slots is not null)
        {
            var spots = 0;
            foreach (var slot in stash.Properties.Slots)
            {
                if (slot.Name is null || !slot.Name.StartsWith("Poster_", StringComparison.Ordinal)) continue;
                foreach (var f in slot.Properties?.Filters ?? []) { f.Filter?.UnionWith(registered); spots++; }
            }
            logger.Info($"[WWC Gun Displays] displays allowed on {spots} poster spots");
        }
        // Off the flea and out of random loot: The Jackal sells them.
        ragfairConfig.Dynamic.Blacklist.Custom.UnionWith(registered);
        itemConfig.RewardItemBlacklist.UnionWith(registered);
        pmcConfig.GlobalLootBlacklist.AddRange(registered.Except(pmcConfig.GlobalLootBlacklist));
        scavCaseConfig.RewardItemBlacklist.UnionWith(registered);
        hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(registered.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
        traderConfig.Fence.Blacklist.UnionWith(registered);
        foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(registered.Except(drop.ItemBlacklist));
        var kinds = Ids.Classes.Where(c => cfg.Guns.Allows(c.Key)).Select(c => c.Key);
        logger.Success($"[WWC Gun Displays] {registered.Count} displays registered; they take {string.Join(", ", kinds)}{(cfg.Guns.ExtraAllowed.Count > 0 ? " and " + cfg.Guns.ExtraAllowed.Count + " extra item(s)" : "")}, never machine guns");
        return Task.CompletedTask;
    }

    /// <summary>The grid filter: every switched-on gun class plus any extra items; for the pistol box only pistols and revolvers (and extra items that are handguns).</summary>
    private HashSet<MongoId> AllowedGuns(DisplayConfig cfg, bool handgunsOnly)
    {
        var allowed = new HashSet<MongoId>();
        string[] handgunClasses = ["pistols", "revolvers"];
        foreach (var (key, id) in Ids.Classes) if (cfg.Guns.Allows(key) && (!handgunsOnly || handgunClasses.Contains(key))) allowed.Add(new MongoId(id));
        var handgunIds = Ids.Classes.Where(c => handgunClasses.Contains(c.Key)).Select(c => c.Id).ToArray();
        foreach (var extra in cfg.Guns.ExtraAllowed)
        {
            // Warnings are given once, on the full list; the pistol box quietly takes only the extras that are handguns.
            if (!MongoId.IsValidMongoId(extra)) { if (!handgunsOnly) logger.Warning($"[WWC Gun Displays] config.json: \"{extra}\" in extraAllowedItemIds is not an item id, so it was skipped"); continue; }
            var id = new MongoId(extra);
            if (!templates.Items.TryGetValue(id, out var t)) { if (!handgunsOnly) logger.Warning($"[WWC Gun Displays] config.json: no item {extra} in the database, so it was skipped"); continue; }
            if (IsUnder(t, Ids.NeverAllowed)) { if (!handgunsOnly) logger.Warning($"[WWC Gun Displays] config.json: {extra} is a machine gun or launcher, which displays never take, so it was skipped"); continue; }
            if (handgunsOnly && !IsUnder(t, handgunIds)) continue;
            allowed.Add(id);
        }
        if (allowed.Count == 0) logger.Warning(handgunsOnly ? "[WWC Gun Displays] config.json switches pistols and revolvers off, so the pistol shadow box takes nothing" : "[WWC Gun Displays] config.json switches every kind of gun off, so displays take nothing");
        return allowed;
    }

    private bool IsUnder(TemplateItem item, string[] classes)
    {
        var at = item; var guard = 0;
        while (at is not null && guard++ < 20)
        {
            if (classes.Contains(at.Id.ToString())) return true;
            if (at.Parent == default || !templates.Items.TryGetValue(at.Parent, out at)) break;
        }
        return false;
    }
}

/// <summary>Stage 2: The Jackal sells the displays, once the client files are installed.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 142)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled) { logger.Info("[WWC Gun Displays] switched off in config.json, no offers"); return Task.CompletedTask; }
        if (Shared.ConfigBroken) { logger.Warning("[WWC Gun Displays] config.json needs fixing; templates kept, offers skipped"); return Task.CompletedTask; }
        if (!Shared.ClientPresent(dir)) { logger.Warning("[WWC Gun Displays] client files are not installed; templates kept, offers skipped"); return Task.CompletedTask; }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Assort is null) { logger.Warning("[WWC Gun Displays] The Jackal not found; no offers"); return Task.CompletedTask; }
        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        var sold = new List<string>();
        foreach (var kind in Kinds.All)
        {
            var sale = cfg.For(kind.Key);
            var tpl = Ids.Display(kind.Key);
            var offer = Ids.Offer(kind.Key);
            if (!sale.SoldByTheJackal || !templates.Items.ContainsKey(tpl) || assort.Items.Any(i => i.Id == offer)) continue;
            assort.Items.Add(new Item { Id = offer, Template = tpl, ParentId = "hideout", SlotId = "hideout", Upd = new Upd { UnlimitedCount = false, StackObjectsCount = sale.Stock, BuyRestrictionMax = sale.BuyLimit, BuyRestrictionCurrent = 0 } });
            assort.BarterScheme[offer] = [[new BarterScheme { Count = sale.PriceRoubles, Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[offer] = sale.LoyaltyLevel;
            sold.Add($"{kind.ShortName} {sale.PriceRoubles:N0} RUB at LL{sale.LoyaltyLevel}");
        }
        logger.Success($"[WWC Gun Displays] The Jackal sells: {(sold.Count == 0 ? "nothing (switched off in config.json)" : string.Join(", ", sold))}");
        return Task.CompletedTask;
    }
}
