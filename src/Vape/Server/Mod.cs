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
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace Vape.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.vape.server";
    public string Name { get; init; } = "Disposable vape";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.4");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }   // The Jackal comes from WWC; without him nobody sells them
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Ids come from fixed text, so a rebuild never changes a template a profile may hold. Never change a flavour's key.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("vape|" + key))).ToLowerInvariant()[..24]);
    public static MongoId Item(string flavour) => Of("item|" + flavour);
    public static MongoId Offer(string flavour) => Of("offer|" + flavour);
    public const string BuffList = "Buffs_Vape";
    public const string Donor = "544fb37f4bdc2dee738b4567";                 // Analgin painkillers (Drug, one resource point per use)
    public const string Jackal = "672b4a010000000000000001";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
}

public record FlavourSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("shortName")] public string ShortName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("sell")] public bool Sell { get; set; } = true;
    [JsonPropertyName("price")] public int? Price { get; set; }
    [JsonPropertyName("hits")] public int? Hits { get; set; }
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

public record OfferSpec
{
    [JsonPropertyName("sell")] public bool Sell { get; set; } = true;
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
    [JsonPropertyName("price")] public int Price { get; set; } = 9000;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 6;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 3;
}

public record VapeConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("flavours")] public List<FlavourSpec> Flavours { get; set; } = Defaults.Flavours();
    /// <summary>Hits per vape (the item's resource; one used per hit). The screen counts down from this.</summary>
    [JsonPropertyName("hits")] public int Hits { get; set; } = 25;
    /// <summary>Seconds until the hit counts and its buffs start. The clip is 6.2 s and the game ends the use when the clip ends: if the
    /// effect has not finished by then it counts as interrupted (hit used up, no buffs; fixture run 2, 2026-11-17, first hit with 5.6 s).
    /// 4.8 s leaves the same kind of margin Analgin has (3 s effect, 3.83 s clip).</summary>
    [JsonPropertyName("useSeconds")] public double UseSeconds { get; set; } = 4.8;
    [JsonPropertyName("effects")] public List<BuffSpec> Effects { get; set; } =
    [
        new() { Type = "SkillRate", Skill = "StressResistance", Value = 6, Duration = 150 },
        new() { Type = "SkillRate", Skill = "Attention", Value = 4, Duration = 150 },
        new() { Type = "HydrationRate", Value = -0.05, Duration = 60 },
    ];
    [JsonPropertyName("offer")] public OfferSpec Offer { get; set; } = new();
    [JsonPropertyName("fleaMarket")] public bool FleaMarket { get; set; }
    [JsonPropertyName("keepOutOfOtherLoot")] public bool KeepOutOfOtherLoot { get; set; } = true;

    /// <summary>The use animation is 6.2 s; the effect has to finish before it ends (see UseSeconds).</summary>
    public const double MaxUseSeconds = 5.2;

    public VapeConfig Sane()
    {
        Hits = Math.Clamp(Hits, 1, 99);
        UseSeconds = Math.Clamp(UseSeconds, 1, MaxUseSeconds);   // longer than the animation = every hit used up without its effect
        Offer.LoyaltyLevel = Math.Clamp(Offer.LoyaltyLevel, 1, 4);
        Offer.Price = Math.Clamp(Offer.Price, 1, 10_000_000);
        Offer.Stock = Math.Clamp(Offer.Stock, 1, 100_000);
        Offer.BuyLimit = Math.Max(1, Offer.BuyLimit);
        Flavours = Flavours.Where(f => !string.IsNullOrWhiteSpace(f.Key)).GroupBy(f => f.Key.Trim().ToLowerInvariant()).Select(g => g.Last() with { Key = g.Key }).ToList();
        foreach (var f in Flavours) { if (f.Price is not null) f.Price = Math.Clamp(f.Price.Value, 1, 10_000_000); if (f.Hits is not null) f.Hits = Math.Clamp(f.Hits.Value, 1, 99); }
        Effects ??= [];
        if (Flavours.Count == 0) Flavours = Defaults.Flavours();
        return this;
    }
}

internal static class Defaults
{
    private static FlavourSpec F(string key, string title, string shortName, string line) => new()
    {
        Key = key, Name = $"Dymok disposable vape ({title})", ShortName = shortName,
        Description = $"A Dymok disposable in {title}. {line} Dymok vapes get put together in a garage near the port from old batteries and whatever liquid makes it past the checkpoints. " +
                      "The little screen counts down the hits, about twenty-five to a vape. " +
                      "One hit calms you down for a couple of minutes and leaves your mouth dry.",
    };

    public static List<FlavourSpec> Flavours() =>
    [
        F("blue_razz_ice", "Blue Razz Ice", "Dymok BR", "Blue raspberry with menthol. Tastes like a freezer pop that melted in your pocket."),
        F("watermelon_ice", "Watermelon Ice", "Dymok WI", "Sweet watermelon with a cold finish. Smells like a summer nobody here remembers."),
        F("miami_mint", "Miami Mint", "Dymok MM", "Mint with a bit of sweetness. The safe pick if you don't trust the fruit ones."),
        F("sour_apple_ice", "Sour Apple Ice", "Dymok SA", "Green apple candy, sour enough to make your jaw ache."),
        F("strawberry_banana", "Strawberry Banana", "Dymok SB", "Strawberry and banana like a cheap smoothie. Sticky sweet."),
        F("mexico_mango", "Mexico Mango", "Dymok MG", "Ripe mango, heavy and sweet. These go first whenever a box turns up."),
        F("grape_ice", "Grape Ice", "Dymok GI", "Grape soda with ice. Leaves your tongue the same colour."),
        F("tropical_rainbow_blast", "Tropical Rainbow Blast", "Dymok TR", "Every fruit at once. Nobody can name one flavour in it and nobody cares."),
    ];
}

internal static class Shared
{
    // People edit the json by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static VapeConfig? config;

    public static VapeConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (config is not null) return config;
        var path = Path.Combine(dir, "config.json");
        try { config = (File.Exists(path) ? JsonSerializer.Deserialize<VapeConfig>(File.ReadAllText(path), Forgiving) : null) ?? new VapeConfig(); }
        catch (JsonException ex) { logger.Error($"[Vape] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values until that's fixed. {ex.Message}"); config = new VapeConfig(); }
        catch (Exception ex) { logger.Error($"[Vape] config.json could not be read ({ex.Message}), so the mod is using its normal values"); config = new VapeConfig(); }
        return config = config.Sane();
    }

#if WWC   // built into WWC: its client files are in BepInEx\plugins\WWC\Vape and its code is in WWC.Client.dll
    public static string PluginDir(string serverModDir) => WWC.Server.Parts.ClientPart(WWC.Server.Parts.Vape);
    private static bool ClientDll(string plugin) => WWC.Server.Parts.ClientPresent;
#else
    public static string PluginDir(string serverModDir) =>
        Path.Combine(Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", "..")), "BepInEx", "plugins", "Vape");
    private static bool ClientDll(string plugin) => File.Exists(Path.Combine(plugin, "Vape.Client.dll"));
#endif

    /// <summary>True when the client plugin and its bundle are on disk, so no vape is ever sold looking like a pack of Analgin.</summary>
    public static bool ClientHas(string serverModDir)
    {
        var plugin = PluginDir(serverModDir);
        return ClientDll(plugin) && File.Exists(Path.Combine(plugin, "vape.bundle")) && File.Exists(Path.Combine(plugin, "vape.json"));
    }

    /// <summary>The item ids the client has a model for (vape.json "items"), or null when vape.json is missing or unreadable. A flavour
    /// added to config.json without a model would look like Analgin in game, so it is not sold.</summary>
    public static HashSet<string>? ClientItems(string serverModDir)
    {
        try
        {
            var path = Path.Combine(PluginDir(serverModDir), "vape.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                ? items.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToHashSet()
                : null;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>The game client reads the buff list into fixed lists of names: a buff type it does not know stops the whole globals file from
/// loading (the game would not start), and an unknown skill does nothing. So config.json "effects" are checked against the client's
/// lists (EFT 40743, EStimulatorBuffType and ESkillId) and anything else is left out with a warning.</summary>
internal static class BuffCheck
{
    static readonly string[] Types =
    [
        "HealthRate", "EnergyRate", "HydrationRate", "SkillRate", "MaxStamina", "StaminaRate", "StomachBloodloss", "ContusionBlur",
        "ContusionWiggle", "Pain", "HandsTremor", "QuantumTunnelling", "RemoveNegativeEffects", "RemoveAllBuffs", "RemoveAllBloodLosses",
        "DamageModifier", "WeightLimit", "UnknownToxin", "LethalToxin", "Antidote", "BodyTemperature", "LightBleeding", "HeavyBleeding",
        "Fracture", "Contusion", "HalloweenBuff", "MisfireEffect", "ZombieInfection", "FrostbiteBuff",
    ];
    static readonly string[] Skills =
    [
        "Endurance", "Strength", "Vitality", "Health", "StressResistance", "Metabolism", "Immunity", "Perception", "Intellect", "Attention",
        "Charisma", "Memory", "MagDrills", "Pistol", "Revolver", "SMG", "Assault", "Shotgun", "Sniper", "LMG", "HMG", "Launcher",
        "AttachedLauncher", "Throwing", "Misc", "Melee", "DMR", "DrawMaster", "AimMaster", "RecoilControl", "TroubleShooting", "Sniping",
        "CovertMovement", "ProneMovement", "FirstAid", "FieldMedicine", "Surgery", "LightVests", "HeavyVests", "WeaponModding",
        "AdvancedModding", "NightOps", "SilentOps", "Lockpicking", "Search", "WeaponTreatment", "Freetrading", "Auctions", "Cleanoperations",
        "Barter", "Shadowconnections", "Taskperformance", "BearAssaultoperations", "BearAuthority", "BearAksystems", "BearHeavycaliber",
        "BearRawpower", "UsecArsystems", "UsecDeepweaponmodding", "UsecLongrangeoptics", "UsecNegotiations", "UsecTactics", "BotReload",
        "BotSound", "AimDrills", "HideoutManagement", "Crafting",
    ];

    static string? Find(string[] names, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : names.FirstOrDefault(n => n.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));

    public static List<BuffSpec> Checked(IEnumerable<BuffSpec?> effects, ISptLogger<Templates> logger)
    {
        var ok = new List<BuffSpec>();
        foreach (var b in effects)
        {
            if (b is null) continue;
            var type = Find(Types, b.Type);
            if (type is null) { logger.Warning($"[Vape] effect type '{b.Type}' is not one the game knows, so it is left out (it would stop the game from loading)"); continue; }
            var skill = "";
            if (type == "SkillRate")
            {
                skill = Find(Skills, b.Skill) ?? "";
                if (skill.Length == 0) { logger.Warning($"[Vape] skill '{b.Skill}' is not one the game knows, so that SkillRate effect is left out"); continue; }
            }
            if (!double.IsFinite(b.Value) || !double.IsFinite(b.Duration) || !double.IsFinite(b.Delay)) { logger.Warning($"[Vape] effect {type} has a number that is not a number; left out"); continue; }
            ok.Add(b with { Type = type, Skill = skill });
        }
        return ok;
    }
}

/// <summary>Stage 1 (Dymok, a made-up Tarkov brand, Widowz 2026-11-17 06:31 UTC): one template per flavour (a clone of Analgin: Drug class, one resource point per use), the buff list and the
/// text. Always registered, so a profile holding a vape still loads without the client.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 86)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, GlobalTable globals,
    ItemConfig itemConfig, RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, TraderConfig traderConfig, AirdropConfig airdropConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        var donorId = new MongoId(Ids.Donor);
        if (!templates.Items.TryGetValue(donorId, out var donor)) { logger.Error("[Vape] the Analgin template is missing; no vapes registered"); return Task.CompletedTask; }
        var handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == donorId);
        var buffTable = globals.Configuration?.Health?.Effects?.Stimulator?.Buffs;
        if (buffTable is null) logger.Warning("[Vape] the game's stim buff table was not found; a hit has no effect");
        else buffTable[Ids.BuffList] = BuffCheck.Checked(cfg.Effects, logger).Select(b => new Buff
        {
            BuffType = b.Type, SkillName = b.Skill, Value = b.Value, Delay = Math.Max(0, b.Delay), Duration = Math.Max(0, b.Duration),
            Chance = Math.Clamp(b.Chance, 0, 1), AbsoluteValue = true,
        }).ToList();
        var drawn = Shared.ClientItems(dir);
        if (drawn is not null)
            foreach (var f in cfg.Flavours.Where(f => !drawn.Contains(Ids.Item(f.Key).ToString())))
                logger.Warning($"[Vape] flavour '{f.Key}' has no model in the client's vape.json; it is registered (so a profile holding one loads) but looks like Analgin and is not sold");
        var strings = new Dictionary<string, string>();
        var added = new List<MongoId>();
        foreach (var f in cfg.Flavours)
        {
            var id = Ids.Item(f.Key);
            var name = string.IsNullOrWhiteSpace(f.Name) ? $"Disposable vape ({f.Key})" : f.Name.Trim();
            var shortName = string.IsNullOrWhiteSpace(f.ShortName) ? "Vape" : f.ShortName.Trim();
            var description = f.Description.Trim();
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = donorId,
                ParentId = donor.Parent,
                NewItemName = "vape_" + f.Key,
                OverrideProperties = new TemplateItemProperties { ExaminedByDefault = true, CanSellOnRagfair = cfg.FleaMarket, CanRequireOnRagfair = false },
                HandbookParentId = handbook?.ParentId,
                HandbookPriceRoubles = Math.Round((f.Price ?? cfg.Offer.Price) * 0.55),
                FleaPriceRoubles = f.Price ?? cfg.Offer.Price,
                AddToHandbook = handbook is not null,
                AddToFleaPriceDb = true,
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = name, ShortName = shortName, Description = description } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[Vape] {name}: {string.Join("; ", result.Errors)}"); continue; }
            var props = templates.Items[id].Properties!;
            // set here, not through the clone's overrides, so the painkiller's own effects are really cleared
            props.MaxHpResource = Math.Clamp(f.Hits ?? cfg.Hits, 1, 99);
            props.HpResourceRate = 0;
            props.MedUseTime = cfg.UseSeconds;
            props.Weight = 0.045;
            props.ItemSound = "item_plastic_generic";
            props.EffectsHealth = new Dictionary<HealthFactor, EffectsHealthProperties>();
            props.EffectsDamage = new Dictionary<DamageEffectType, EffectsDamageProperties>();
            props.StimulatorBuffs = buffTable?.GetValueOrDefault(Ids.BuffList)?.Any() == true ? Ids.BuffList : "";
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
        logger.Success($"[Vape] {added.Count} vape(s) registered ({string.Join(", ", cfg.Flavours.Select(f => f.Key))})");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 2: the offers on The Jackal (after he builds his stock at TraderRegistration + 100). Not sold while the client files
/// are missing.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 186)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Offer.Sell) { logger.Info("[Vape] selling is off in config.json"); return Task.CompletedTask; }
        if (!Shared.ClientHas(dir)) { logger.Warning("[Vape] the client files are missing (BepInEx/plugins/Vape), so nobody sells the vapes"); return Task.CompletedTask; }
        var trader = traders.GetTrader(new MongoId(Ids.Jackal));
        if (trader?.Assort is null) { logger.Warning("[Vape] The Jackal is not installed; the vapes are registered but nobody sells them"); return Task.CompletedTask; }
        var assort = trader.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        var drawn = Shared.ClientItems(dir);
        if (drawn is null) { logger.Warning("[Vape] the client's vape.json could not be read, so nobody sells the vapes"); return Task.CompletedTask; }
        var made = 0;
        foreach (var f in cfg.Flavours.Where(f => f.Sell))
        {
            var tpl = Ids.Item(f.Key); var offer = Ids.Offer(f.Key);
            if (!templates.Items.ContainsKey(tpl) || !drawn.Contains(tpl.ToString()) || assort.Items.Any(i => i.Id == offer)) continue;
            assort.Items.Add(new Item
            {
                Id = offer, Template = tpl, ParentId = "hideout", SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = cfg.Offer.Stock, BuyRestrictionMax = cfg.Offer.BuyLimit, BuyRestrictionCurrent = 0 },
            });
            assort.BarterScheme[offer] = [[new BarterScheme { Count = Math.Max(1, f.Price ?? cfg.Offer.Price), Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[offer] = cfg.Offer.LoyaltyLevel;
            made++;
        }
        logger.Success($"[Vape] The Jackal sells {made} vape(s) at loyalty {cfg.Offer.LoyaltyLevel}");
        return Task.CompletedTask;
    }
}




