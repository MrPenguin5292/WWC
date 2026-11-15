using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace LocalTrader.Server;

#if !WWC   // inside WWC the mod's metadata (same guid, local.jackal) is WWC's own
public sealed record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "local.jackal";
    public string Name { get; init; } = "The Jackal - Custom Gear Trader";
    public string Author { get; init; } = "Local mod project";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("4.1.6");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "Private local mod; supplied portrait rights retained by its owners";
}
#endif

[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 100)]
public sealed class JackalMod(TradersTable traders, TemplateTable templates, LocaleTable locales,
    TraderConfig traderConfig, RagfairConfig ragfairConfig, ImageRouter imageRouter,
    CustomQuestService customQuests, ICloner cloner, ISptLogger<JackalMod> logger) : IOnLoad
{
    public const string TraderIdString = "672b4a010000000000000001";
    public static readonly MongoId TraderId = new(TraderIdString);
    public static readonly MongoId Ragman = new("5ac3b934156ae10c4430e83c");
    public static StockResult? LastStock { get; private set; }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
#if WWC
        var directory = WWC.Server.Parts.Dir(WWC.Server.Parts.Jackal);
#else
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
#endif
        var config = JackalConfig.Read(Path.Combine(directory, "Config", "jackal.json"));
        if (traders.ContainsKey(TraderId)) throw new InvalidOperationException("The Jackal trader ID is already registered.");
        var portrait = Path.Combine(directory, "Assets", "jackal.jpg");
        if (!File.Exists(portrait)) throw new FileNotFoundException("The Jackal portrait is missing.", portrait);
        var stock = StockBuilder.Build(traders, templates, config, cloner);
        var traderBase = cloner.Clone(traders[Ragman].Base)!;
        traderBase.Id = TraderId;
        traderBase.Name = config.Name;
        traderBase.Nickname = config.Name;
        traderBase.Surname = "";
        traderBase.Location = "Classified supply network";
        traderBase.Avatar = $"/files/trader/avatar/{TraderId}.jpg";
        traderBase.UnlockedByDefault = true;
        traderBase.CustomizationSeller = stock.Suits.Count > 0;
        traderBase.BalanceRub = 10_000_000;
        traderBase.GridHeight = 2000;
        traderBase.Medic = false;
        traderBase.Insurance!.Availability = false;
        traderBase.Repair!.Availability = false;
        traderBase.ItemsBuy = new ItemBuyData
        {
            // Weapons, gear and barter supplies let trading contribute to loyalty.
            Category = [new("5422acb9af1c889c16000029"), new("543be5f84bdc2dd4348b456a"), new("5448eb774bdc2d0a728b4567")],
            IdList = stock.Assort.Items.Select(i => i.Template).Where(id => templates.Items.ContainsKey(id)).ToHashSet()
        };
        traderBase.ItemsBuyProhibited = cloner.Clone(traders[Ragman].Base.ItemsBuyProhibited)!;
        traderBase.ItemsSell = [];
        traderBase.LoyaltyLevels = config.LoyaltyLevels.Select(tier => new TraderLoyaltyLevel
        {
            MinLevel = tier.PlayerLevel, MinStanding = tier.Standing, MinSalesSum = tier.Sales,
            BuyPriceCoefficient = 70, ExchangePriceCoefficient = 0, HealPriceCoefficient = 0,
            InsurancePriceCoefficient = 0, RepairPriceCoefficient = 0
        }).ToList();
        // This list only knows his own stock at this point; JackalBuyList adds what the other mods put on his shelf later.
        traders.Add(TraderId, new Trader
        {
            Base = traderBase, Assort = stock.Assort, Suits = stock.Suits, Services = [],
            Dialogue = [], QuestAssort = new()
            { ["started"] = [], ["success"] = [], ["fail"] = [] }
        });
        traderConfig.UpdateTime.Add(new UpdateTime
        {
            Name = config.Name, TraderId = TraderId,
            Seconds = new MinMax<int> { Min = config.RestockSeconds, Max = config.RestockSeconds }
        });
        // Buying directly from this trader respects his loyalty requirements.
        ragfairConfig.Traders[TraderId] = false;
        imageRouter.AddRoute($"/files/trader/avatar/{TraderId}", portrait);
        AddLocales(config.Name);
        var quests = Campaign.Build(TraderId, templates.Items, locales.Global.Keys, config.Name);
        foreach (var quest in quests)
        {
            var result = customQuests.CreateQuest(quest);
            if (!result.Success) throw new InvalidOperationException($"The Jackal quest registration failed: {string.Join(", ", result.Errors)}");
        }
        LastStock = stock;
        logger.Info($"[The Jackal] Registered {stock.Assort.LoyalLevelItems.Count} shop offers, {stock.Suits.Count} outfits, {quests.Count} quests and four loyalty levels. Restock: {config.RestockSeconds}s.");
        return Task.CompletedTask;
    }

    /// <summary>Every template on his shelf after all mods have stocked him, for the late buy-list pass.</summary>
    internal static void BuyBackEverythingSold(TradersTable traders, TemplateTable templates, ISptLogger<JackalBuyList> logger)
    {
        var jackal = traders.GetTrader(TraderId);
        if (jackal?.Base?.ItemsBuy is null || jackal.Assort?.Items is null) return;
        var list = jackal.Base.ItemsBuy.IdList ??= [];
        int before = list.Count;
        foreach (var item in jackal.Assort.Items)
            if (templates.Items.ContainsKey(item.Template) && !list.Contains(item.Template)) list.Add(item.Template);
        logger.Info($"[The Jackal] buys back what he sells: {list.Count} item types on his buy list ({list.Count - before} added after the other mods stocked him)");
    }

    private void AddLocales(string name)
    {
        foreach (var locale in locales.Global.Values)
            locale.AddTransformer(data =>
            {
                if (data is null) return data;
                data[$"{TraderId} FullName"] = name;
                data[$"{TraderId} FirstName"] = name;
                data[$"{TraderId} Nickname"] = name;
                data[$"{TraderId} LastName"] = "";
                data[$"{TraderId} Location"] = "Wherever the crates are";
                data[$"{TraderId} Description"] = "Nobody knows his real name, and he likes it that way. He deals in rifles, clothing and gear that come in through people he won't tell you about. Do his contracts and he'll start showing you the good stuff.";
                return data;
            });
    }
}

/// <summary>
/// After every mod has stocked him (offers go in up to TraderRegistration + 150, WWC Jobs locks them at + 200): puts everything on his shelf
/// on his buy list, so he takes back what he sells. His own registration (+ 100) only saw his own stock. Prices and loyalty are untouched.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 210)]
public sealed class JackalBuyList(TradersTable traders, TemplateTable templates, ISptLogger<JackalBuyList> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try { JackalMod.BuyBackEverythingSold(traders, templates, logger); }
        catch (Exception ex) { logger.Warning($"[The Jackal] buy list left as it was ({ex.Message})"); }
        return Task.CompletedTask;
    }
}
