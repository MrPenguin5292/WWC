using System.Reflection;
using Path = System.IO.Path;   // SPTarkov.Server.Core.Models.Eft.Common.Tables also defines Path
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace AvonMask.Server;

internal static class Config
{
    /// <summary>config.json beside the DLL; defaults when it is missing; null (and a warning) when it is unreadable or out of range.</summary>
    public static ModConfig? Load<T>(ModHelper modHelper, ISptLogger<T> logger)
    {
        var modDir = PartFolder.Of(modHelper);
        try
        {
            var cfg = File.Exists(Path.Combine(modDir, "config.json"))
                ? modHelper.GetJsonDataFromFile<ModConfig>(modDir, "config.json") ?? new ModConfig()
                : new ModConfig();
            if (cfg.BuyBackPriceRoubles is < 1 or > 10_000_000 || cfg.BuyBackLoyaltyLevel is < 1 or > 4 || cfg.BuyBackStock is < 1 or > 1000
                || cfg.BuyBackLimit is < 0 or > 1000 || cfg.PmcBotWeightVsGp7 is < 0 or > 100
                || (cfg.BuyBackUnlockQuestId.Length > 0 && !MongoId.IsValidMongoId(cfg.BuyBackUnlockQuestId)))
            {
                logger.Warning($"[{ModInfo.Name}] config.json has a value out of range (price 1-10,000,000, loyalty 1-4, stock 1-1000, limit 0-1000, " +
                               "bot weight 0-100, quest id 24 hex or empty); the mask stays registered, the buy-back and bot changes are skipped");
                return null;
            }
            return cfg;
        }
        catch (Exception ex)
        {
            logger.Warning($"[{ModInfo.Name}] config.json unreadable ({ex.Message}); the mask stays registered, the buy-back and bot changes are skipped");
            return null;
        }
    }
}

/// <summary>
/// The Jackal sells the mask back, so a mask lost in a raid does not lock the Grunts' Labs jobs for good. The row waits in his quest
/// assort under "success" of the unlock quest (default: "Overtime", the Factory job that hands out the first mask), so SPT shows it only
/// once that quest is done. Runs at TraderRegistration + 170, after The Jackal exists. Skipped when the client content is not installed
/// (no model) or config says so; the template stays registered either way.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 170)]
public class BuyBack(
    ISptLogger<BuyBack> logger,
    ModHelper modHelper,
    TradersTable tradersTable) : IOnLoad
{
    public const string Jackal = "672b4a010000000000000001";
    public const string Roubles = "5449016a4bdc2d6f028b456f";
    public static MongoId? OfferRoot;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var cfg = Config.Load(modHelper, logger);
        if (cfg is null) return Task.CompletedTask;
        if (!cfg.JackalBuyBack) { logger.Info($"[{ModInfo.Name}] Jackal buy-back is off (config)"); return Task.CompletedTask; }
        if (ItemRegistration.RegisteredIds.Count == 0) { logger.Warning($"[{ModInfo.Name}] mask not registered; no buy-back"); return Task.CompletedTask; }
        if (cfg.RequireClientContent && ContentGate.Check(cfg, modHelper, logger) is { } problem)
        {
            logger.Warning($"[{ModInfo.Name}] client content not ready ({problem}); mask kept, buy-back skipped");
            return Task.CompletedTask;
        }
        if (!tradersTable.TryGetValue(new MongoId(Jackal), out var jackal) || jackal is null)
        {
            logger.Warning($"[{ModInfo.Name}] The Jackal is not loaded; no buy-back");
            return Task.CompletedTask;
        }

        var modDir = PartFolder.Of(modHelper);
        var dir = Path.Combine(modDir, "db", "CustomAssortSchemes");
        var schemes = modHelper.GetJsonDataFromFile<Dictionary<string, TraderAssort>>(dir, "jackal.json");
        if (schemes is null || !schemes.TryGetValue("jackal", out var scheme) || scheme.Items is not { Count: > 0 })
        {
            logger.Warning($"[{ModInfo.Name}] db/CustomAssortSchemes/jackal.json is missing or empty; no buy-back");
            return Task.CompletedTask;
        }

        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        var root = scheme.Items[0];
        if (assort.Items.Any(i => i.Id == root.Id))
        {
            logger.Warning($"[{ModInfo.Name}] offer {root.Id} is already on The Jackal; skipped");
            return Task.CompletedTask;
        }
        root.Upd ??= new Upd();
        root.Upd.StackObjectsCount = cfg.BuyBackStock;
        root.Upd.UnlimitedCount = false;
        root.Upd.BuyRestrictionMax = cfg.BuyBackLimit > 0 ? cfg.BuyBackLimit : null;
        root.Upd.BuyRestrictionCurrent = cfg.BuyBackLimit > 0 ? 0 : null;
        assort.Items.Add(root);
        assort.BarterScheme[root.Id] = [[new BarterScheme { Template = new MongoId(Roubles), Count = cfg.BuyBackPriceRoubles }]];
        assort.LoyalLevelItems[root.Id] = cfg.BuyBackLoyaltyLevel;
        OfferRoot = root.Id;

        if (cfg.BuyBackUnlockQuestId.Length > 0)
        {
            var questAssort = jackal.QuestAssort;
            foreach (var k in new[] { "started", "success", "fail" })
                if (!questAssort.ContainsKey(k)) questAssort[k] = new Dictionary<MongoId, MongoId>();
            questAssort["success"][root.Id] = new MongoId(cfg.BuyBackUnlockQuestId);
            logger.Success($"[{ModInfo.Name}] Jackal buy-back added: {cfg.BuyBackPriceRoubles} RUB, LL{cfg.BuyBackLoyaltyLevel}, stock {cfg.BuyBackStock}, " +
                           $"limit {cfg.BuyBackLimit}, shows after quest {cfg.BuyBackUnlockQuestId}");
        }
        else
        {
            logger.Success($"[{ModInfo.Name}] Jackal buy-back added: {cfg.BuyBackPriceRoubles} RUB, LL{cfg.BuyBackLoyaltyLevel}, on sale from the start");
        }
        return Task.CompletedTask;
    }
}

/// <summary>After every mod has loaded: says plainly when the unlock quest is not in the database (the Grunts mod not installed), because
/// the buy-back then never shows.</summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 25)]
public class BuyBackCheck(ISptLogger<BuyBackCheck> logger, ModHelper modHelper, TemplateTable templateTable) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var cfg = Config.Load(modHelper, logger);
        if (cfg is null || !cfg.JackalBuyBack || BuyBack.OfferRoot is null || cfg.BuyBackUnlockQuestId.Length == 0) return Task.CompletedTask;
        if (templateTable.Quests.ContainsKey(new MongoId(cfg.BuyBackUnlockQuestId)))
            logger.Info($"[{ModInfo.Name}] unlock quest {cfg.BuyBackUnlockQuestId} is loaded");
        else
            logger.Warning($"[{ModInfo.Name}] unlock quest {cfg.BuyBackUnlockQuestId} is not in the database (is the Grunts mod installed?); " +
                           "the buy-back stays hidden. Set buyBackUnlockQuestId to \"\" in config.json to sell it from the start.");
        return Task.CompletedTask;
    }
}
