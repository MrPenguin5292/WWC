using System.Reflection;
using Path = System.IO.Path;   // SPTarkov.Server.Core.Models.Eft.Common.Tables also defines Path
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Slickster.Server;

/// <summary>
/// Stage 2: trader offers. Runs at TraderRegistration + 20, after The Jackal (and every other custom trader
/// registered at + 1) exists. Offers are added only when config allows it and the client content receipt
/// matches the files, so a half-installed mod never sells items that have no model.
///
/// Reads, relative to the DLL:
///   config.json                       pluginFolder, requireClientContent, traderAliases, price bounds
///   db/CustomAssortSchemes/*.json     { "<alias or 24-hex id>": { items, barter_scheme, loyal_level_items } }
///                                     (tools/sptkit.py assort; same shape as a trader's assort.json)
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 120)]
public class TraderOffers(
    ISptLogger<TraderOffers> logger,
    ModHelper modHelper,
    TradersTable tradersTable) : IOnLoad
{
    // Built-in aliases (WTT-CommonLib names). The Jackal is added through config.json traderAliases.
    private static readonly Dictionary<string, string> BuiltinAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mechanic"] = "5a7c2eca46aef81a7ca2145d",
        ["skier"] = "58330581ace78e27b8b10cee",
        ["peacekeeper"] = "5935c25fb3acc3127c3d8cd9",
        ["therapist"] = "54cb57776803fa99248b456e",
        ["prapor"] = "54cb50c76803fa8b248b4571",
        ["jaeger"] = "5c0647fdd443bc2504c2d371",
        ["ragman"] = "5ac3b934156ae10c4430e83c",
        ["fence"] = "579dc571d53a0658a154fbec",
        ["ref"] = "6617beeaa9cfa777ca915b7c",
        ["badger"] = "bd3a8b28356d9c6509966546",
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modDir = ModFolder.Of(modHelper);

        ModConfig cfg;
        try
        {
            cfg = File.Exists(Path.Combine(modDir, "config.json"))
                ? modHelper.GetJsonDataFromFile<ModConfig>(modDir, "config.json") ?? new ModConfig()
                : new ModConfig();
        }
        catch (Exception ex)
        {
            logger.Warning($"[{ModInfo.Name}] config.json unreadable ({ex.Message}); templates kept, offers skipped");
            return Task.CompletedTask;
        }

        if (!cfg.Enabled || !cfg.OffersEnabled)
        {
            logger.Info($"[{ModInfo.Name}] offers disabled in config.json");
            return Task.CompletedTask;
        }
        if (cfg.MinPrice < 1 || cfg.MaxPrice < cfg.MinPrice || cfg.MaxStack < 1)
        {
            logger.Warning($"[{ModInfo.Name}] config.json price/stack bounds are invalid; templates kept, offers skipped");
            return Task.CompletedTask;
        }

        if (cfg.RequireClientContent)
        {
            var problem = ContentGate.Check(cfg, modHelper, logger);
            if (problem is not null)
            {
                logger.Warning($"[{ModInfo.Name}] client content not ready ({problem}); templates kept, offers skipped");
                return Task.CompletedTask;
            }
        }

        if (ItemRegistration.RegisteredIds.Count == 0)
        {
            logger.Warning($"[{ModInfo.Name}] no templates were registered; offers skipped");
            return Task.CompletedTask;
        }

        var assortDir = Path.Combine(modDir, "db", "CustomAssortSchemes");
        if (!Directory.Exists(assortDir))
        {
            logger.Info($"[{ModInfo.Name}] no db/CustomAssortSchemes folder; no offers");
            return Task.CompletedTask;
        }

        var added = 0;
        foreach (var file in Directory.EnumerateFiles(assortDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var schemes = modHelper.GetJsonDataFromFile<Dictionary<string, TraderAssort>>(assortDir, Path.GetFileName(file));
            foreach (var (key, scheme) in schemes ?? new Dictionary<string, TraderAssort>())
            {
                var traderId = ResolveTrader(key, cfg);
                if (traderId is null)
                {
                    logger.Warning($"[{ModInfo.Name}] {Path.GetFileName(file)}: unknown trader key '{key}' (add it to config.json traderAliases); skipped");
                    continue;
                }
                if (!tradersTable.TryGetValue(traderId.Value, out var trader) || trader is null)
                {
                    logger.Warning($"[{ModInfo.Name}] trader {key} ({traderId}) is not in the database (does its mod load before TraderRegistration + 20?); skipped");
                    continue;
                }
                added += Merge(trader, scheme, cfg, key);
            }
        }

        logger.Success($"[{ModInfo.Name}] {added} offer root(s) added");
        return Task.CompletedTask;
    }

    private static MongoId? ResolveTrader(string key, ModConfig cfg)
    {
        if (cfg.TraderAliases.TryGetValue(key, out var fromCfg) && MongoId.IsValidMongoId(fromCfg)) return new MongoId(fromCfg);
        if (BuiltinAliases.TryGetValue(key, out var builtin)) return new MongoId(builtin);
        if (MongoId.IsValidMongoId(key)) return new MongoId(key);
        return null;
    }

    /// <summary>Append the scheme's rows to the trader; idempotent on root ids; validates prices and stacks.</summary>
    private int Merge(Trader trader, TraderAssort scheme, ModConfig cfg, string key)
    {
        var assort = trader.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();

        var existing = new HashSet<MongoId>(assort.Items.Select(i => i.Id));
        var skipped = new HashSet<string>(StringComparer.Ordinal);   // roots (and their subtrees) that were refused
        var roots = 0;

        // Rows are written root first, then its parts (parentId = root or another part), so one pass is enough.
        foreach (var item in scheme.Items ?? [])
        {
            if (item.ParentId is not null && skipped.Contains(item.ParentId))
            {
                skipped.Add(item.Id);
                continue;
            }
            if (existing.Contains(item.Id))
            {
                logger.Warning($"[{ModInfo.Name}] {key}: offer item {item.Id} already exists on the trader; skipped with its parts");
                skipped.Add(item.Id);
                continue;
            }
            var isRoot = item.ParentId == "hideout";
            if (isRoot)
            {
                var reason = RootProblem(item, scheme, cfg);
                if (reason is not null)
                {
                    logger.Warning($"[{ModInfo.Name}] {key}: root {item.Id} {reason}; skipped with its parts");
                    skipped.Add(item.Id);
                    continue;
                }
                assort.BarterScheme[item.Id] = scheme.BarterScheme![item.Id];
                assort.LoyalLevelItems[item.Id] = scheme.LoyalLevelItems is not null && scheme.LoyalLevelItems.TryGetValue(item.Id, out var ll) ? ll : 1;
                roots++;
            }
            assort.Items.Add(item);
            existing.Add(item.Id);
        }
        return roots;
    }

    private static string? RootProblem(Item item, TraderAssort scheme, ModConfig cfg)
    {
        if (scheme.BarterScheme is null || !scheme.BarterScheme.TryGetValue(item.Id, out var barter) || barter.Count == 0 || barter[0].Count == 0)
        {
            return "has no barter scheme";
        }
        var bad = barter.SelectMany(b => b).FirstOrDefault(b => b.Count is null || b.Count < cfg.MinPrice || b.Count > cfg.MaxPrice);
        if (bad is not null)
        {
            return $"price {bad.Count} is outside [{cfg.MinPrice}, {cfg.MaxPrice}]";
        }
        if (item.Upd?.StackObjectsCount is > 0 && item.Upd.StackObjectsCount > cfg.MaxStack)
        {
            return $"stack {item.Upd.StackObjectsCount} is above {cfg.MaxStack}";
        }
        return null;
    }
}


