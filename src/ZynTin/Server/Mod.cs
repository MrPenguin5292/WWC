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
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace ZynTin.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.zyntin.server";
    public string Name { get; init; } = "Zyn tin";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.3.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }   // The Jackal is only needed when config "seller" is "jackal"
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Ids come from fixed text, so a rebuild never changes a template a profile may hold. Never change a flavour's key.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("zyntin|" + key))).ToLowerInvariant()[..24]);
    public static MongoId Item(string flavour) => Of("item|" + flavour);      // coolmint: fcd3946237a6b7bd162763b1
    public static MongoId Offer(string flavour) => Of("offer|" + flavour);    // coolmint: 040084c3a543c484213f4b41
    public static MongoId Craft(string flavour) => Of("craft|" + flavour);
    public const string BuffList = "Buffs_ZynTin";
    public const string Donor = "5755383e24597772cb798966";                 // Vaseline balm
    public const string Jackal = "672b4a010000000000000001";
    public const string Therapist = "54cb57776803fa99248b456e";
    public const int MedStationArea = 7;
    public const string Rouble = "5449016a4bdc2d6f028b456f";
}

public record FlavourSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("shortName")] public string ShortName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("sell")] public bool Sell { get; set; } = true;
    [JsonPropertyName("craft")] public bool Craft { get; set; } = true;
    // optional per-item settings; left out = the shared ones further down in config.json
    [JsonPropertyName("seller")] public string? Seller { get; set; }
    [JsonPropertyName("price")] public int? Price { get; set; }
    [JsonPropertyName("loyaltyLevel")] public int? LoyaltyLevel { get; set; }
    [JsonPropertyName("stock")] public int? Stock { get; set; }
    [JsonPropertyName("buyLimit")] public int? BuyLimit { get; set; }
    /// <summary>true = waits for its brand's quest (db/quests.json "unlocks"); left out = only Therapist's own offers wait.</summary>
    [JsonPropertyName("questLocked")] public bool? QuestLocked { get; set; }
    /// <summary>"zyn", "velo", "killa" or "siberia"; left out = read from the key ("velo_...", "killa_...", "siberia_..."), else Zyn.</summary>
    [JsonPropertyName("brand")] public string? Brand { get; set; }
    [JsonPropertyName("pouches")] public int? Pouches { get; set; }
    [JsonPropertyName("effects")] public List<BuffSpec>? Effects { get; set; }
}

public record ZynConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("flavours")] public List<FlavourSpec> Flavours { get; set; } = Defaults.Flavours();
    [JsonPropertyName("pouches")] public int Pouches { get; set; } = 15;
    [JsonPropertyName("useSeconds")] public double UseSeconds { get; set; } = 4.4;
    [JsonPropertyName("effects")] public List<BuffSpec> Effects { get; set; } =
    [
        new() { Type = "SkillRate", Skill = "StressResistance", Value = 10, Duration = 300 },
        new() { Type = "SkillRate", Skill = "Attention", Value = 10, Duration = 300 },
        new() { Type = "EnergyRate", Value = -0.02, Duration = 300 },
        new() { Type = "HydrationRate", Value = -0.1, Duration = 60 },   // Widowz 03:23 UTC: "Give all items a small dehydration effect"
        new() { Type = "HandsTremor", Value = 0, Delay = 290, Duration = 30 },
    ];
    /// <summary>"therapist" (she develops them in the questline) or "jackal".</summary>
    [JsonPropertyName("seller")] public string Seller { get; set; } = "therapist";
    [JsonPropertyName("offer")] public OfferSpec Offer { get; set; } = new();
    /// <summary>true = the offers and the recipes wait for Therapist's questline; false = they open straight away.</summary>
    [JsonPropertyName("questline")] public bool Questline { get; set; } = true;
    [JsonPropertyName("craft")] public CraftSpec Craft { get; set; } = new();
    [JsonPropertyName("fleaMarket")] public bool FleaMarket { get; set; }
    [JsonPropertyName("keepOutOfOtherLoot")] public bool KeepOutOfOtherLoot { get; set; } = true;

    public ZynConfig Sane()
    {
        Pouches = Math.Clamp(Pouches, 1, 100);
        UseSeconds = Math.Clamp(UseSeconds, 1, 20);
        Offer.LoyaltyLevel = Math.Clamp(Offer.LoyaltyLevel, 1, 4);
        Offer.Price = Math.Max(1, Offer.Price);
        Offer.Stock = Math.Max(1, Offer.Stock);
        Offer.BuyLimit = Math.Max(1, Offer.BuyLimit);
        Seller = string.Equals(Seller?.Trim(), "jackal", StringComparison.OrdinalIgnoreCase) ? "jackal" : "therapist";
        Craft.AreaLevel = Math.Clamp(Craft.AreaLevel, 1, 3);
        Craft.Minutes = Math.Clamp(Craft.Minutes, 1, 24 * 60);
        Craft.Count = Math.Clamp(Craft.Count, 1, 20);
        Flavours = Flavours.Where(f => !string.IsNullOrWhiteSpace(f.Key)).GroupBy(f => f.Key.Trim().ToLowerInvariant()).Select(g => g.Last() with { Key = g.Key }).ToList();
        if (Flavours.Count == 0) Flavours = Defaults.Flavours();
        return this;
    }
}

internal static class Defaults
{
    private static FlavourSpec F(string key, string title, string shortName, string line) => new()
    {
        Key = key, Name = $"Zyn {title} nicotine pouches", ShortName = shortName,
        Description = $"A tin of Zyn {title}, 6 mg, fifteen pouches. {line} One under the lip settles your nerves for a few minutes, and your hands shake a little when it wears off. Leaves you thirsty.",
    };

    public static List<FlavourSpec> Flavours() =>
    [
        F("coolmint", "Cool Mint", "Zyn CM", "The classic. Cold enough to make your eyes water."),
        F("peppermint", "Peppermint", "Zyn PM", "Sweeter and softer than Cool Mint."),
        F("spearmint", "Spearmint", "Zyn SM", "Mild and a little sweet, like chewing gum."),
        F("wintergreen", "Wintergreen", "Zyn WG", "Strong, and it smells like your grandfather's coat."),
        F("cinnamon", "Cinnamon", "Zyn Cin", "Hot and sweet. It burns for the first minute."),
        F("coffee", "Coffee", "Zyn Cof", "Tastes like the coffee nobody in Tarkov has had for months."),
        F("citrus", "Citrus", "Zyn Cit", "Lemon and orange peel, sour at first."),
        F("smooth", "Smooth", "Zyn Smth", "No flavour at all, just the nicotine."),
        F("chill", "Chill", "Zyn Chl", "No real flavour, only a cool tingle."),
        F("menthol", "Menthol", "Zyn Men", "Plain menthol, like an old cigarette without the smoke."),
        F("blackcherry", "Black Cherry", "Zyn BC", "Dark cherry, sweet like cough syrup."),
        F("peach", "Peach", "Zyn Pch", "Soft peach, sweet enough to forget what's in it."),
        F("dragonberry", "Dragonberry", "Zyn DB", "Something pink and fruity. Nobody knows what a dragonberry is."),
    ];
}

public record BuffSpec
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("skill")] public string Skill { get; set; } = "";
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("delay")] public double Delay { get; set; } = 1;
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("chance")] public double Chance { get; set; } = 1;
}

public record CraftSpec
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("areaLevel")] public int AreaLevel { get; set; } = 2;
    [JsonPropertyName("minutes")] public double Minutes { get; set; } = 50;
    [JsonPropertyName("count")] public int Count { get; set; } = 3;
    [JsonPropertyName("inputs")] public Dictionary<string, int> Inputs { get; set; } = new()
    {
        ["573476d324597737da2adc13"] = 1,   // Malboro cigarettes
        ["59e35abd86f7741778269d82"] = 1,   // Pack of sodium bicarbonate
        ["5c13cef886f774072e618e82"] = 1,   // Toilet paper
    };
}

public record OfferSpec
{
    [JsonPropertyName("sell")] public bool Sell { get; set; } = true;
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
    [JsonPropertyName("price")] public int Price { get; set; } = 14000;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 6;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 3;
}

internal static class Shared
{
    // People edit the json by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static ZynConfig? config;

    public static ZynConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (config is not null) return config;
        var path = Path.Combine(dir, "config.json");
        try { config = (File.Exists(path) ? JsonSerializer.Deserialize<ZynConfig>(File.ReadAllText(path), Forgiving) : null) ?? new ZynConfig(); }
        catch (JsonException ex) { logger.Error($"[Zyn] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values until that's fixed. {ex.Message}"); config = new ZynConfig(); }
        catch (Exception ex) { logger.Error($"[Zyn] config.json could not be read ({ex.Message}), so the mod is using its normal values"); config = new ZynConfig(); }
        return config = config.Sane();
    }

    public static string SellerId(ZynConfig cfg) => cfg.Seller == "jackal" ? Ids.Jackal : Ids.Therapist;
    public static string SellerOf(ZynConfig cfg, FlavourSpec f) => string.IsNullOrWhiteSpace(f.Seller) ? cfg.Seller : (f.Seller.Trim().Equals("jackal", StringComparison.OrdinalIgnoreCase) ? "jackal" : "therapist");
    public static string SellerIdOf(ZynConfig cfg, FlavourSpec f) => SellerOf(cfg, f) == "jackal" ? Ids.Jackal : Ids.Therapist;
    public static bool LockedOf(ZynConfig cfg, FlavourSpec f) => cfg.Questline && (f.QuestLocked ?? SellerOf(cfg, f) == "therapist");
    public static string BuffListOf(FlavourSpec f) => f.Effects is { Count: > 0 } ? Ids.BuffList + "_" + f.Key : Ids.BuffList;
    public static string BrandOf(FlavourSpec f) =>
        !string.IsNullOrWhiteSpace(f.Brand) ? f.Brand.Trim().ToLowerInvariant() : f.Key.StartsWith("siberia_", StringComparison.OrdinalIgnoreCase) ? "siberia" : f.Key.StartsWith("velo_", StringComparison.OrdinalIgnoreCase) ? "velo" : f.Key.StartsWith("killa_", StringComparison.OrdinalIgnoreCase) ? "killa" : "zyn";

#if WWC   // built into WWC: its client files are in BepInEx\plugins\WWC\ZynTin and its code is in WWC.Client.dll
    public static string PluginDir(string serverModDir) => WWC.Server.Parts.ClientPart(WWC.Server.Parts.ZynTin);
    private static bool ClientDll(string plugin) => WWC.Server.Parts.ClientPresent;
#else
    public static string PluginDir(string serverModDir) =>
        Path.Combine(Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", "..")), "BepInEx", "plugins", "ZynTin");
    private static bool ClientDll(string plugin) => File.Exists(Path.Combine(plugin, "ZynTin.Client.dll"));
#endif

    /// <summary>True when the client plugin and its bundle are on disk, so no tin is ever sold looking like a Vaseline balm.</summary>
    public static bool ClientHas(string serverModDir)
    {
        var plugin = PluginDir(serverModDir);
        return ClientDll(plugin) && File.Exists(Path.Combine(plugin, "zyn.bundle")) && File.Exists(Path.Combine(plugin, "zyn.json"));
    }

    /// <summary>Audit 2026-11-17: the tins the installed client can dress (its zyn.json "items"), so a newer server next to an older client
    /// (for example after a client-only rollback) never sells a tin that would look like a Vaseline balm. Null when it can't be read.</summary>
    public static HashSet<string>? ClientItems(string serverModDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginDir(serverModDir), "zyn.json")));
            return doc.RootElement.GetProperty("items").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }
    }
}

/// <summary>Stage 1: one template per flavour, the shared buff list and the text. Always registered, so a profile holding a tin still loads
/// without the client.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 74)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, GlobalTable globals,
    ItemConfig itemConfig, RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, TraderConfig traderConfig, AirdropConfig airdropConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        var donorId = new MongoId(Ids.Donor);
        if (!templates.Items.TryGetValue(donorId, out var donor)) { logger.Error("[Zyn] the Vaseline balm template is missing; no tins registered"); return Task.CompletedTask; }
        var handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == donorId);
        var buffTable = globals.Configuration?.Health?.Effects?.Stimulator?.Buffs;
        List<Buff> Buffs(List<BuffSpec> list) => list.Where(b => !string.IsNullOrWhiteSpace(b.Type)).Select(b => new Buff
        {
            BuffType = b.Type.Trim(), SkillName = b.Skill?.Trim() ?? "", Value = b.Value, Delay = Math.Max(0, b.Delay), Duration = Math.Max(0, b.Duration),
            Chance = Math.Clamp(b.Chance, 0, 1), AbsoluteValue = true,
        }).ToList();
        if (buffTable is null) logger.Warning("[Zyn] the game's stim buff table was not found; the pouches have no effect");
        else
        {
            buffTable[Ids.BuffList] = Buffs(cfg.Effects);
            foreach (var f in cfg.Flavours.Where(f => f.Effects is { Count: > 0 })) buffTable[Shared.BuffListOf(f)] = Buffs(f.Effects!);
        }
        var strings = new Dictionary<string, string>();
        var added = new List<MongoId>();
        foreach (var f in cfg.Flavours)
        {
            var id = Ids.Item(f.Key);
            var name = string.IsNullOrWhiteSpace(f.Name) ? $"Zyn {f.Key}" : f.Name.Trim();
            var shortName = string.IsNullOrWhiteSpace(f.ShortName) ? "Zyn" : f.ShortName.Trim();
            var description = f.Description.Trim();
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = donorId,
                ParentId = donor.Parent,
                NewItemName = "zyntin_" + f.Key,
                OverrideProperties = new TemplateItemProperties { ExaminedByDefault = true, CanSellOnRagfair = cfg.FleaMarket, CanRequireOnRagfair = false },
                HandbookParentId = handbook?.ParentId,
                HandbookPriceRoubles = Math.Round((f.Price ?? cfg.Offer.Price) * 0.55),
                FleaPriceRoubles = f.Price ?? cfg.Offer.Price,
                AddToHandbook = handbook is not null,
                AddToFleaPriceDb = true,
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = name, ShortName = shortName, Description = description } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[Zyn] {name}: {string.Join("; ", result.Errors)}"); continue; }
            var props = templates.Items[id].Properties!;
            // set here, not through the clone's overrides, so the balm's own effects are really cleared
            props.MaxHpResource = Math.Clamp(f.Pouches ?? cfg.Pouches, 1, 100);
            props.HpResourceRate = 0;
            props.MedUseTime = cfg.UseSeconds;
            props.Weight = 0.035;
            props.EffectsHealth = new Dictionary<HealthFactor, EffectsHealthProperties>();
            props.EffectsDamage = new Dictionary<DamageEffectType, EffectsDamageProperties>();
            props.StimulatorBuffs = buffTable?.GetValueOrDefault(Shared.BuffListOf(f))?.Any() == true ? Shared.BuffListOf(f) : "";
            strings[$"{id} Name"] = name;
            strings[$"{id} ShortName"] = shortName;
            strings[$"{id} Description"] = description;
            added.Add(id);
        }
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
        if (cfg.KeepOutOfOtherLoot && added.Count > 0)
        {
            itemConfig.RewardItemBlacklist.UnionWith(added);
            pmcConfig.GlobalLootBlacklist.AddRange(added.Except(pmcConfig.GlobalLootBlacklist));
            scavCaseConfig.RewardItemBlacklist.UnionWith(added);
            hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(added.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
            traderConfig.Fence.Blacklist.UnionWith(added);
            foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(added.Except(drop.ItemBlacklist));
        }
        if (!cfg.FleaMarket && added.Count > 0) ragfairConfig.Dynamic.Blacklist.Custom.UnionWith(added);
        logger.Success($"[Zyn] {added.Count} tin(s) registered ({string.Join(", ", cfg.Flavours.Select(f => f.Key))})");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 1b: one Medstation recipe per flavour. With the questline on they are locked until Therapist's last quest (its
/// ProductionScheme rewards find each recipe by area, end product, QuestComplete requirement and level). Inputs are vanilla items.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 75)]
public class Crafts(ISptLogger<Crafts> logger, ModHelper modHelper, HideoutTable hideout, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Craft.Enabled) { logger.Info("[Zyn] the Medstation recipes are off in config.json"); return Task.CompletedTask; }
        var recipes = hideout.Production?.Recipes;
        if (recipes is null) { logger.Warning("[Zyn] hideout recipe list not found; no recipes"); return Task.CompletedTask; }
        var inputs = new List<Requirement>();
        foreach (var (tpl, count) in cfg.Craft.Inputs)
        {
            if (!MongoId.IsValidMongoId(tpl) || !templates.Items.ContainsKey(new MongoId(tpl))) { logger.Warning($"[Zyn] config.json craft input {tpl} is not an item; no recipes"); return Task.CompletedTask; }
            inputs.Add(new Requirement { Type = "Item", TemplateId = new MongoId(tpl), Count = Math.Max(1, count), IsFunctional = false, IsEncoded = false, IsSpawnedInSession = false });
        }
        var made = 0; var gated = 0;
        foreach (var f in cfg.Flavours.Where(f => f.Craft))
        {
            var tpl = Ids.Item(f.Key);
            if (!templates.Items.ContainsKey(tpl)) continue;
            var req = new List<Requirement> { new() { Type = "Area", AreaType = Ids.MedStationArea, RequiredLevel = cfg.Craft.AreaLevel } };
            req.AddRange(inputs.Select(r => new Requirement { Type = r.Type, TemplateId = r.TemplateId, Count = r.Count, IsFunctional = false, IsEncoded = false, IsSpawnedInSession = false }));
            // each brand's recipes open with the quest that unlocks that brand (Zyn: Therapist's Field Trial)
            var unlock = cfg.Questline ? Quests.UnlockQuest(dir, Shared.BrandOf(f)) : null;
            var locked = unlock != null && Shared.LockedOf(cfg, f);
            if (locked) gated++;
            if (locked) req.Add(new Requirement { Type = "QuestComplete", QuestId = new MongoId(unlock!) });
            var id = Ids.Craft(f.Key);
            recipes.RemoveAll(r => r.Id == id);
            recipes.Add(new HideoutProduction
            {
                Id = id, AreaType = HideoutAreas.MedStation, Requirements = req, ProductionTime = Math.Max(60, cfg.Craft.Minutes * 60), EndProduct = tpl, Count = cfg.Craft.Count,
                IsEncoded = false, Locked = locked, NeedFuelForAllProductionTime = false, Continuous = false, ProductionLimitCount = 0, IsCodeProduction = false,
            });
            made++;
        }
        logger.Info($"[Zyn] {made} Medstation {cfg.Craft.AreaLevel} recipe(s): {cfg.Craft.Count} tins in {cfg.Craft.Minutes} min{(gated > 0 ? $", {gated} locked until their brand's quest" : "")}");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 2: the offers, on Therapist (default) or The Jackal (after he builds his stock at TraderRegistration + 100). With the
/// questline on, Quests.cs (+ 175) locks them behind the last quest. Not sold while the client files are missing.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 174)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Offer.Sell) { logger.Info("[Zyn] selling is off in config.json"); return Task.CompletedTask; }
        if (!Shared.ClientHas(dir)) { logger.Warning("[Zyn] the client files are missing (BepInEx/plugins/ZynTin), so nobody sells the tins"); return Task.CompletedTask; }
        var dressed = Shared.ClientItems(dir);
        var unknown = dressed == null ? 0 : cfg.Flavours.Count(f => f.Sell && !dressed.Contains(Ids.Item(f.Key).ToString()));
        if (unknown > 0) logger.Warning($"[Zyn] the installed client does not know {unknown} tin(s) (older zyn.json); those are not sold until the client matches the server");
        foreach (var group in cfg.Flavours.Where(f => f.Sell).GroupBy(f => Shared.SellerOf(cfg, f)))
        {
            var who = group.Key == "jackal" ? "The Jackal" : "Therapist";
            var trader = traders.GetTrader(new MongoId(group.Key == "jackal" ? Ids.Jackal : Ids.Therapist));
            if (trader?.Assort is null) { logger.Warning($"[Zyn] {who} not found; {group.Count()} tin(s) not sold"); continue; }
            var assort = trader.Assort;
            assort.Items ??= [];
            assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
            assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
            var made = 0;
            foreach (var f in group)
            {
                var tpl = Ids.Item(f.Key); var offer = Ids.Offer(f.Key);
                if (!templates.Items.ContainsKey(tpl) || assort.Items.Any(i => i.Id == offer)) continue;
                if (dressed != null && !dressed.Contains(tpl.ToString())) continue;
                assort.Items.Add(new Item
                {
                    Id = offer, Template = tpl, ParentId = "hideout", SlotId = "hideout",
                    Upd = new Upd { UnlimitedCount = false, StackObjectsCount = Math.Max(1, f.Stock ?? cfg.Offer.Stock), BuyRestrictionMax = Math.Max(1, f.BuyLimit ?? cfg.Offer.BuyLimit), BuyRestrictionCurrent = 0 },
                });
                assort.BarterScheme[offer] = [[new BarterScheme { Count = Math.Max(1, f.Price ?? cfg.Offer.Price), Template = new MongoId(Ids.Rouble) }]];
                assort.LoyalLevelItems[offer] = Math.Clamp(f.LoyaltyLevel ?? cfg.Offer.LoyaltyLevel, 1, 4);
                made++;
            }
            logger.Success($"[Zyn] {who} sells {made} tin(s){(group.Any(f => Shared.LockedOf(cfg, f)) ? $" ({group.Count(f => Shared.LockedOf(cfg, f))} wait for their quest)" : "")}");
        }
        return Task.CompletedTask;
    }
}
