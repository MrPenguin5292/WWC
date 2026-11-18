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

namespace WWCMags.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.wwcmags.server";
    public string Name { get; init; } = "WWC Mags";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.4.0");
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
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wwcmags|" + key))).ToLowerInvariant()[..24]);
    public static MongoId Mag(int n) => Of($"mag|{n}");
    public static MongoId Offer(int n) => Of($"offer|{n}");
    /// <summary>A WWC Wraps Raid Tape roll, by the key WWC Wraps names it with (its ids come from the same kind of fixed text).</summary>
    public static MongoId WrapsTape(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wwcwraps|tape|" + key))).ToLowerInvariant()[..24]);
    public const string Jackal = "672b4a010000000000000001";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
    /// <summary>The stock mag each body is cloned from. These are never changed.</summary>
    public static readonly Dictionary<string, string> Donors = new()
    {
        ["stanag"] = "55d4887d4bdc2d962f8b4570",   // AR-15 5.56x45 Colt STANAG 30-round magazine
        ["pmag"] = "5aaa5dfee5b5b000140293d3",     // AR-15 5.56x45 Magpul PMAG 30 GEN M3 STANAG 30-round magazine
        ["pmagw"] = "55802d5f4bdc2dac148b458e",    // AR-15 5.56x45 Magpul PMAG 30 GEN M3 W STANAG 30-round magazine
    };
}

public record MagsConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("jackal")] public JackalSettings Jackal { get; set; } = new();
    [JsonPropertyName("fleaMarket")] public FleaSettings Flea { get; set; } = new();
    [JsonPropertyName("items")] public ItemSettings Items { get; set; } = new();

    public MagsConfig Sane()
    {
        Jackal.Stock = Math.Clamp(Jackal.Stock, 1, 9_999_999);
        Jackal.BuyLimit = Math.Clamp(Jackal.BuyLimit, 1, 9_999_999);
        Jackal.LoyaltyLevel = Math.Clamp(Jackal.LoyaltyLevel, 1, 4);
        foreach (var k in Jackal.PriceRoubles.Keys.ToList()) Jackal.PriceRoubles[k] = Math.Clamp(Jackal.PriceRoubles[k], 1, 100_000_000);
        foreach (var k in Jackal.FinishPriceMultiplier.Keys.ToList()) Jackal.FinishPriceMultiplier[k] = Math.Clamp(Jackal.FinishPriceMultiplier[k], 0.01, 1000);
        return this;
    }

    public double PriceOf(MagEntry e)
    {
        var basePrice = Jackal.PriceRoubles.TryGetValue(e.Body, out var p) ? p : 10_000;
        var mult = Jackal.FinishPriceMultiplier.TryGetValue(e.Finish, out var m) ? m : 1.0;
        return Math.Max(1, Math.Round(basePrice * mult / 50.0) * 50);
    }
}

public record JackalSettings
{
    [JsonPropertyName("sell")] public bool Sell { get; set; } = true;
    [JsonPropertyName("sellReadyMades")] public bool SellReadyMades { get; set; } = true;
    [JsonPropertyName("sellYourOwn")] public bool SellYourOwn { get; set; } = true;
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 10;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 10;
    /// <summary>Which GØØN Raid Tape roll (WWC Wraps) comes wound on every mag he sells: raid_black, raid_odg, or none.</summary>
    [JsonPropertyName("tapeFitted")] public string TapeFitted { get; set; } = "raid_black";
    [JsonPropertyName("priceRoubles")] public Dictionary<string, double> PriceRoubles { get; set; } = new() { ["stanag"] = 9000, ["pmag"] = 11000, ["pmagw"] = 12000 };
    [JsonPropertyName("finishPriceMultiplier")] public Dictionary<string, double> FinishPriceMultiplier { get; set; } = new()
    {
        ["factory"] = 1.0, ["gi"] = 0.9, ["anodized"] = 1.4, ["cerakote"] = 1.3, ["battleworn"] = 1.5, ["spray"] = 1.0, ["hydro"] = 1.6, ["translucent"] = 1.8,
    };
}

public record FleaSettings
{
    [JsonPropertyName("sell")] public bool Sell { get; set; }
}

public record ItemSettings
{
    [JsonPropertyName("fitWhereStockMagFits")] public bool FitWhereStockMagFits { get; set; } = true;
    [JsonPropertyName("keepOutOfLoot")] public bool KeepOutOfLoot { get; set; } = true;
}

public record MagEntry
{
    [JsonPropertyName("n")] public int N { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("short")] public string Short { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("finish")] public string Finish { get; set; } = "";
    /// <summary>Off sale: the item stays registered so anything already owned keeps working.</summary>
    [JsonPropertyName("retired")] public bool Retired { get; set; }
    [JsonPropertyName("readyMade")] public bool ReadyMade { get; set; }
}

internal static class Shared
{
    // People edit config.json by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static MagsConfig? loaded;

    public static List<MagEntry> Read<T>(string dir, ISptLogger<T> logger)
    {
        var path = Path.Combine(dir, "mags.json");
        if (!File.Exists(path)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<MagEntry>>(File.ReadAllText(path), Forgiving) ?? [])
                .Where(e => e.N >= 1 && Ids.Donors.ContainsKey(e.Body)).GroupBy(e => e.N).Select(g => g.Last()).OrderBy(e => e.N).ToList();
        }
        catch (Exception ex) { logger.Warning($"[WWCMags] mags.json could not be read ({ex.Message}); no mags registered"); return []; }
    }

    public static MagsConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (loaded is not null) return loaded;
        var path = Path.Combine(dir, "config.json");
        try { loaded = (File.Exists(path) ? JsonSerializer.Deserialize<MagsConfig>(File.ReadAllText(path), Forgiving) : null) ?? new MagsConfig(); }
        catch (JsonException ex) { logger.Error($"[WWCMags] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values until that's fixed. {ex.Message}"); loaded = new MagsConfig(); }
        catch (Exception ex) { logger.Error($"[WWCMags] config.json could not be read ({ex.Message}), so the mod is using its normal values"); loaded = new MagsConfig(); }
        return loaded.Sane();
    }

    /// <summary>True when the client plugin and this mag's textures are on disk, so nothing is sold that would only look like a stock mag.</summary>
    public static bool ClientHas(string serverModDir, int n)
    {
#if WWC
        var plugin = WWC.Server.Parts.ClientPart(WWC.Server.Parts.Mags);
        return WWC.Server.Parts.ClientPresent && File.Exists(Path.Combine(plugin, "mags", n.ToString(), "colour_a.png")) && File.Exists(Path.Combine(plugin, "mags", n.ToString(), "material.json"));
#else
        var plugin = Path.Combine(Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", "..")), "BepInEx", "plugins", "WWCMags");
        return File.Exists(Path.Combine(plugin, "WWCMags.Client.dll")) && File.Exists(Path.Combine(plugin, "mags", n.ToString(), "colour_a.png")) && File.Exists(Path.Combine(plugin, "mags", n.ToString(), "material.json"));
#endif
    }
}

/// <summary>Stage 1: templates. Always registered, even retired ones, so a profile holding a mag still loads when the client is gone.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 34)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, ItemConfig itemConfig,
    RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, TraderConfig traderConfig, AirdropConfig airdropConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        var strings = new Dictionary<string, string>();
        var added = new List<MongoId>();
        foreach (var e in Shared.Read(dir, logger))
        {
            var donorId = Ids.Donors[e.Body];
            if (!templates.Items.TryGetValue(donorId, out var donor)) { logger.Error($"[WWCMags] stock mag {donorId} not found; {e.Name} not registered"); continue; }
            var id = Ids.Mag(e.N);
            var name = string.IsNullOrWhiteSpace(e.Name) ? $"WWC mag {e.N}" : e.Name.Trim();
            var shortName = string.IsNullOrWhiteSpace(e.Short) ? name : e.Short.Trim();
            var description = string.IsNullOrWhiteSpace(e.Description) ? "A magazine refinished by WWC (Wet Works Company) for The Jackal's customers." : e.Description.Trim();
            var handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == donorId);
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = donorId,
                ParentId = donor.Parent,
                NewItemName = $"wwcmag_{e.N}",
                OverrideProperties = new TemplateItemProperties
                {
                    ExaminedByDefault = true,
                    CanSellOnRagfair = cfg.Flea.Sell,
                    CanRequireOnRagfair = false,
                },
                HandbookParentId = handbook?.ParentId,
                HandbookPriceRoubles = handbook?.Price ?? 3000,
                FleaPriceRoubles = cfg.PriceOf(e),
                AddToHandbook = handbook is not null,
                AddToFleaPriceDb = true,
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = name, ShortName = shortName, Description = description } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[WWCMags] {name}: {string.Join("; ", result.Errors)}"); continue; }
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
                foreach (var (k, v) in strings)
                {
                    if (overwrite) dict[k] = v; else dict.TryAdd(k, v);
                }
                return dict;
            });
        }
        if (cfg.Items.KeepOutOfLoot && added.Count > 0)
        {
            itemConfig.RewardItemBlacklist.UnionWith(added);
            pmcConfig.GlobalLootBlacklist.AddRange(added.Except(pmcConfig.GlobalLootBlacklist));
            scavCaseConfig.RewardItemBlacklist.UnionWith(added);
            hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(added.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
            traderConfig.Fence.Blacklist.UnionWith(added);
            foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(added.Except(drop.ItemBlacklist));
        }
        if (!cfg.Flea.Sell && added.Count > 0) ragfairConfig.Dynamic.Blacklist.Custom.UnionWith(added);
        logger.Success($"[WWCMags] {added.Count} mag(s) registered");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 2: each mag fits wherever its stock mag fits (weapons, and any pouch, rig or case that names the stock mag), after every mod
/// has registered its items at Preload. Only adds ids to lists that already name the stock mag, so nothing else changes.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 10)]
public class Fit(ISptLogger<Fit> logger, ModHelper modHelper, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.Items.FitWhereStockMagFits) { logger.Info("[WWCMags] fitWhereStockMagFits is off; mags only fit where their clone already did"); return Task.CompletedTask; }
        var byDonor = new Dictionary<MongoId, List<MongoId>>();
        foreach (var e in Shared.Read(dir, logger))
        {
            var id = Ids.Mag(e.N);
            if (!templates.Items.ContainsKey(id)) continue;
            var donor = new MongoId(Ids.Donors[e.Body]);
            if (!byDonor.TryGetValue(donor, out var list)) byDonor[donor] = list = [];
            list.Add(id);
        }
        if (byDonor.Count == 0) return Task.CompletedTask;
        int slots = 0, grids = 0, conflicts = 0;
        void Widen(HashSet<MongoId>? set, ref int counter)
        {
            if (set is null) return;
            foreach (var (donor, mine) in byDonor)
            {
                if (!set.Contains(donor)) continue;
                foreach (var id in mine) if (set.Add(id)) counter++;
            }
        }
        foreach (var (_, item) in templates.Items)
        {
            var props = item.Properties;
            if (props is null) continue;
            foreach (var slot in props.Slots ?? []) foreach (var f in slot.Properties?.Filters ?? []) Widen(f.Filter, ref slots);
            foreach (var chamber in props.Chambers ?? []) foreach (var f in chamber.Properties?.Filters ?? []) Widen(f.Filter, ref slots);
            foreach (var grid in props.Grids ?? [])
                foreach (var f in grid.Properties?.Filters ?? [])
                {
                    Widen(f.Filter, ref grids);
                    Widen(f.ExcludedFilter, ref grids);
                }
            Widen(props.ConflictingItems, ref conflicts);
        }
        logger.Info($"[WWCMags] mags fit {slots} weapon slots, {grids} container lists and {conflicts} conflict lists that name their stock mag");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 3: The Jackal's offers, after he registers at TraderRegistration + 100. Skips any mag whose textures aren't on disk.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 134)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Jackal.Sell) { logger.Info("[WWCMags] selling is off in config.json, no offers"); return Task.CompletedTask; }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Assort is null) { logger.Warning("[WWCMags] The Jackal not found; no offers"); return Task.CompletedTask; }
        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        int offers = 0, skipped = 0, taped = 0;
        var tapeKey = TapeKey(cfg.Jackal.TapeFitted);
        foreach (var e in Shared.Read(dir, logger))
        {
            if (e.Retired) continue;
            if (e.ReadyMade ? !cfg.Jackal.SellReadyMades : !cfg.Jackal.SellYourOwn) continue;
            var tpl = Ids.Mag(e.N);
            if (!templates.Items.ContainsKey(tpl)) continue;
            if (!Shared.ClientHas(dir, e.N)) { skipped++; continue; }
            var root = Ids.Offer(e.N);
            if (assort.Items.Any(i => i.Id == root)) continue;
            assort.Items.Add(new Item
            {
                Id = root,
                Template = tpl,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = cfg.Jackal.Stock, BuyRestrictionMax = cfg.Jackal.BuyLimit, BuyRestrictionCurrent = 0 },
            });
            var price = cfg.PriceOf(e);
            // Sold with a roll of GØØN Raid Tape wound on (Widowz, 2026-11-16: "add goon tape to them"), in the tape slot WWC Wraps gives
            // every WWC mag. The roll can be taken off or swapped like any part. Without WWC Wraps the mag is sold plain.
            if (tapeKey is not null && FittedTape(templates, tpl, tapeKey) is { } tape)
            {
                assort.Items.Add(new Item { Id = Ids.Of($"offer|{e.N}|tape"), Template = tape.Tpl, ParentId = root.ToString(), SlotId = tape.Slot });
                price += tape.Price;
                taped++;
            }
            assort.BarterScheme[root] = [[new BarterScheme { Count = price, Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[root] = cfg.Jackal.LoyaltyLevel;
            offers++;
        }
        if (skipped > 0) logger.Warning($"[WWCMags] {skipped} mag(s) have no textures on disk (client files missing); not sold");
        var tapeNote = tapeKey is null ? "" : taped > 0 ? $", {taped} with GØØN Raid Tape ({tapeKey}) wound on" : offers > 0 ? $", none with tape ({tapeKey}: WWC Wraps or its tape slot not found)" : "";
        logger.Success($"[WWCMags] The Jackal now sells {offers} WWC mag(s){tapeNote}");
        return Task.CompletedTask;
    }

    /// <summary>The config's tape choice as WWC Wraps names it, or null for none.</summary>
    internal static string? TapeKey(string? setting)
    {
        var s = (setting ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        return s switch
        {
            "" or "none" or "no" or "false" or "off" => null,
            "raidblack" or "black" or "classicblack" => "raid_black",
            "raidodg" or "odg" or "odgreen" or "green" or "olive" => "raid_odg",
            _ => (setting ?? "").Trim(),
        };
    }

    /// <summary>The tape roll, the mag's tape slot (the slot whose filter takes that roll) and the roll's price, or null when there is no such slot.</summary>
    internal static (MongoId Tpl, string Slot, double Price)? FittedTape(TemplateTable templates, MongoId mag, string key)
    {
        var tape = Ids.WrapsTape(key);
        if (!templates.Items.ContainsKey(tape) || !templates.Items.TryGetValue(mag, out var m)) return null;
        var slot = (m.Properties?.Slots ?? []).FirstOrDefault(s => (s.Properties?.Filters ?? []).Any(f => f.Filter?.Contains(tape) == true));
        if (slot?.Name is null) return null;
        var price = templates.Handbook.Items.FirstOrDefault(h => h.Id == tape)?.Price ?? 4500;
        return (tape, slot.Name, price);
    }
}
