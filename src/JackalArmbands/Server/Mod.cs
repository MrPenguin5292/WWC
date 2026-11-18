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
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace JackalArmbands.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.jackalarmbands.server";
    public string Name { get; init; } = "Jackal Armbands";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.1");
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
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jackalarmband|" + key))).ToLowerInvariant()[..24]);
    public const string Jackal = "672b4a010000000000000001";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
    public const string Donor = "5b3f16c486f7747c327f55f7";   // Armband (White): the cloth every design is built on
}

public record ArmbandConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("jackal")] public JackalSettings Jackal { get; set; } = new();
    [JsonPropertyName("items")] public ItemSettings Items { get; set; } = new();

    public ArmbandConfig Sane()
    {
        Jackal.PriceRoubles = Math.Clamp(Jackal.PriceRoubles, 1, 1_000_000_000);
        Jackal.Stock = Math.Clamp(Jackal.Stock, 1, 9_999_999);
        Jackal.BuyLimit = Math.Clamp(Jackal.BuyLimit, 1, 9_999_999);
        Jackal.LoyaltyLevel = Math.Clamp(Jackal.LoyaltyLevel, 1, 4);
        Items.WeightKg = Math.Clamp(Items.WeightKg, 0, 1000);
        return this;
    }
}

public record JackalSettings
{
    [JsonPropertyName("sell")] public bool Sell { get; set; } = true;
    [JsonPropertyName("priceRoubles")] public double PriceRoubles { get; set; } = 5_000;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 5;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 5;
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
}

public record ItemSettings
{
    [JsonPropertyName("weightKg")] public double WeightKg { get; set; } = 0.05;
    [JsonPropertyName("lockedWhenWorn")] public bool LockedWhenWorn { get; set; } = true;
}

public record ArmbandEntry
{
    [JsonPropertyName("n")] public int N { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Off sale: the item stays registered so anything already owned keeps working.</summary>
    [JsonPropertyName("retired")] public bool Retired { get; set; }
}

internal static class Shared
{
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static ArmbandConfig? loaded;

    public static List<ArmbandEntry> Read<T>(string dir, ISptLogger<T> logger)
    {
        var path = Path.Combine(dir, "armbands.json");
        if (!File.Exists(path)) return [];
        try { return (JsonSerializer.Deserialize<List<ArmbandEntry>>(File.ReadAllText(path), Forgiving) ?? []).Where(e => e.N >= 1).OrderBy(e => e.N).ToList(); }
        catch (Exception ex) { logger.Warning($"[JackalArmbands] armbands.json could not be read ({ex.Message}); no armbands registered"); return []; }
    }

    public static ArmbandConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (loaded is not null) return loaded;
        var path = Path.Combine(dir, "config.json");
        try { loaded = (File.Exists(path) ? JsonSerializer.Deserialize<ArmbandConfig>(File.ReadAllText(path), Forgiving) : null) ?? new ArmbandConfig(); }
        catch (JsonException ex) { logger.Error($"[JackalArmbands] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values until that's fixed. {ex.Message}"); loaded = new ArmbandConfig(); }
        catch (Exception ex) { logger.Error($"[JackalArmbands] config.json could not be read ({ex.Message}), so the mod is using its normal values"); loaded = new ArmbandConfig(); }
        return loaded.Sane();
    }

    /// <summary>True when this armband's texture is on disk, so nothing is sold that would only show up as a plain white armband.</summary>
    public static bool ClientHas(string serverModDir, int n)
    {
#if WWC
        return WWC.Server.Parts.ClientPresent && File.Exists(Path.Combine(WWC.Server.Parts.ClientPart(WWC.Server.Parts.Armbands), "armbands", n.ToString(), "colour.png"));
#else
        var gameRoot = Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", ".."));
        return File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "JackalArmbands", "JackalArmbands.Client.dll")) && File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "JackalArmbands", "armbands", n.ToString(), "colour.png"));
#endif
    }
}

/// <summary>Stage 1: templates. Always registered so a profile holding an armband still loads when the client is gone.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 32)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, ItemConfig itemConfig,
    RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, AirdropConfig airdropConfig, TraderConfig traderConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        if (!templates.Items.TryGetValue(Ids.Donor, out var donor)) { logger.Error("[JackalArmbands] donor armband not found; nothing registered"); return Task.CompletedTask; }
        var cfg = Shared.Config(dir, logger);
        var handbookParent = templates.Handbook.Items.FirstOrDefault(h => h.Id == Ids.Donor)?.ParentId;
        var strings = new Dictionary<string, string>();
        var added = 0;
        var ids = new List<MongoId>();
        foreach (var e in Shared.Read(dir, logger))
        {
            var id = Ids.Of($"armband|{e.N}");
            var name = string.IsNullOrWhiteSpace(e.Name) ? $"Armband {e.N}" : e.Name.Trim();
            const string description = "A custom armband, made to order for The Jackal's customers.";
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = Ids.Donor,
                ParentId = donor.Parent,
                NewItemName = $"jackalarmband_{e.N}",
                OverrideProperties = new TemplateItemProperties
                {
                    Weight = cfg.Items.WeightKg,
                    ExaminedByDefault = true,
                    CanSellOnRagfair = false,
                    CanRequireOnRagfair = false,
                    Unlootable = cfg.Items.LockedWhenWorn,
                },
                HandbookParentId = handbookParent,
                HandbookPriceRoubles = Math.Max(1, Math.Floor(cfg.Jackal.PriceRoubles / 3.0)),
                FleaPriceRoubles = cfg.Jackal.PriceRoubles,
                AddToHandbook = handbookParent is not null,
                AddToFleaPriceDb = true,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = name, ShortName = name, Description = description } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[JackalArmbands] {name}: {string.Join("; ", result.Errors)}"); continue; }
            strings[$"{id} Name"] = name;
            strings[$"{id} ShortName"] = name;
            strings[$"{id} Description"] = description;
            ids.Add(id);
            added++;
        }
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
        // Armbands are made to order at The Jackal: never from a loot table, Fence, a reward, the scav case, the cultist circle, an airdrop or the flea's own listings.
        itemConfig.RewardItemBlacklist.UnionWith(ids);
        ragfairConfig.Dynamic.Blacklist.Custom.UnionWith(ids);
        pmcConfig.GlobalLootBlacklist.AddRange(ids.Except(pmcConfig.GlobalLootBlacklist));
        scavCaseConfig.RewardItemBlacklist.UnionWith(ids);
        hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(ids.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
        traderConfig.Fence.Blacklist.UnionWith(ids);
        foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(ids.Except(drop.ItemBlacklist));
        logger.Success($"[JackalArmbands] {added} armband(s) registered");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 2: The Jackal's offers, after he registers at TraderRegistration + 100. Skips any armband whose texture isn't on disk.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 132)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Jackal.Sell) { logger.Info("[JackalArmbands] selling is off in config.json, no offers"); return Task.CompletedTask; }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Assort is null) { logger.Warning("[JackalArmbands] The Jackal not found; no offers"); return Task.CompletedTask; }
        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        var offers = 0;
        var skipped = 0;
        foreach (var e in Shared.Read(dir, logger))
        {
            if (e.Retired) continue;
            var tpl = Ids.Of($"armband|{e.N}");
            if (!templates.Items.ContainsKey(tpl)) continue;
            if (!Shared.ClientHas(dir, e.N)) { skipped++; continue; }
            var root = Ids.Of($"offer|{e.N}");
            if (assort.Items.Any(i => i.Id == root)) continue;
            assort.Items.Add(new Item
            {
                Id = root,
                Template = tpl,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = cfg.Jackal.Stock, BuyRestrictionMax = cfg.Jackal.BuyLimit, BuyRestrictionCurrent = 0 },
            });
            assort.BarterScheme[root] = [[new BarterScheme { Count = Math.Max(1, cfg.Jackal.PriceRoubles), Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[root] = cfg.Jackal.LoyaltyLevel;
            offers++;
        }
        if (skipped > 0) logger.Warning($"[JackalArmbands] {skipped} armband(s) have no texture on disk; not sold");
        logger.Success($"[JackalArmbands] The Jackal now sells {offers} armband(s)");
        return Task.CompletedTask;
    }
}
