using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace LocalTrader.Server;

public sealed record StockEntry(string OfferId, string TemplateId, string Name, int LoyaltyLevel, double Price, string Kind);
public sealed record StockResult(TraderAssort Assort, List<Suit> Suits, List<StockEntry> Entries);

public static class StockBuilder
{
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    private const string M33 = "67dc255ee3028a8b120efc48";
    private const string Gs45 = "67a545000000000000000001";
    private const string Gs45MatchFmj = "5e81f423763d9f754677bf2e";
    private static readonly MongoId AmmoParent = new("5485a8684bdc2da71d8b4567");
    private static readonly HashSet<string> Rifles =
    [
        "67271f100000000000000001", "67271f100000000000000050", "67271f100000000000000051"
    ];

    public static StockResult Build(TradersTable traders, TemplateTable templates, JackalConfig config, ICloner cloner)
    {
        var assort = new TraderAssort { Items = [], BarterScheme = [], LoyalLevelItems = [], NextResupply = 0 };
        var suits = new List<Suit>();
        var entries = new List<StockEntry>();
        var excluded = config.ExcludedItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = config.ExtraItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vanilla = config.IncludeFutureModItems ? ReadVanillaIds() : null;
        var prices = templates.Handbook.Items.GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.First().Price ?? 1);

        bool Selected(TemplateItem template)
        {
            string id = template.Id.ToString();
            string name = template.Name ?? "";
            if (excluded.Contains(id) || template.Properties is null || template.Properties.IsUnbuyable == true ||
                template.Properties.IsUnsaleable == true || name.StartsWith("bobblehead_", StringComparison.OrdinalIgnoreCase))
                return false;
            bool knownAx = id.StartsWith("67271f10", StringComparison.Ordinal) || id.StartsWith("67271f11", StringComparison.Ordinal);
            if (knownAx && !Rifles.Contains(id) && !config.StockLooseWeaponParts) return false;
            bool named = config.ItemNamePrefixes.Any(prefix => !string.IsNullOrWhiteSpace(prefix) &&
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return knownAx || id.StartsWith("67271f1600000000000000", StringComparison.Ordinal) || named ||
                extra.Contains(id) || (vanilla is not null && !vanilla.Contains(id) && prices.ContainsKey(template.Id));
        }

        foreach (var template in templates.Items.Values.Where(Selected).OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
        {
            string tpl = template.Id.ToString();
            var donor = FindOffer(template.Id, traders, excluded);
            bool weapon = Rifles.Contains(tpl) || !string.IsNullOrWhiteSpace(template.Properties?.WeapClass);
            // A functional assembled weapon must retain its native attachment tree.
            if (weapon && (donor is null || donor.Items.Count < 2)) continue;
            double price = donor?.RoublePrice ?? HandbookPrice(template.Id);
            int loyalty = Loyalty(template, price);
            bool isAmmunition = template.Parent == AmmoParent;
            AddOffer(template, donor?.Items, Price(price), loyalty,
                isAmmunition ? 300 : config.StockPerReset, isAmmunition ? 120 : config.PurchaseLimit,
                weapon ? "Complete weapon" : isAmmunition ? "Ammunition" : "Item");
        }

        var ammoId = new MongoId(M33);
        if (!excluded.Contains(M33) && templates.Items.TryGetValue(ammoId, out var ammunition) &&
            !entries.Any(entry => entry.TemplateId == M33))
            AddOffer(ammunition, null, Price(Math.Max(1_000, HandbookPrice(ammoId))), 2, 100, 60, "Ammunition");

        var pistolAmmoId = new MongoId(Gs45MatchFmj);
        if (templates.Items.ContainsKey(new MongoId(Gs45)) && !excluded.Contains(Gs45MatchFmj) &&
            templates.Items.TryGetValue(pistolAmmoId, out var pistolAmmo) &&
            !entries.Any(entry => entry.TemplateId == Gs45MatchFmj))
        {
            double price = FindOffer(pistolAmmoId, traders, excluded)?.RoublePrice ?? HandbookPrice(pistolAmmoId);
            AddOffer(pistolAmmo, null, Price(price), 2, 300, 120, "Ammunition");
        }

        var seenSuits = new HashSet<MongoId>();
        foreach (var trader in traders.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal).Select(pair => pair.Value))
        foreach (var original in trader.Suits ?? [])
        {
            string suite = original.SuiteId.ToString();
            if (!suite.StartsWith("672b0d01000000000000002", StringComparison.Ordinal) || excluded.Contains(suite) ||
                !seenSuits.Add(original.SuiteId) || !templates.Customization.TryGetValue(original.SuiteId, out var customization))
                continue;
            var copy = cloner.Clone(original) ?? throw new InvalidOperationException("Could not copy BDU clothing offer.");
            int variant = int.Parse(suite[^2..], System.Globalization.CultureInfo.InvariantCulture) - 20;
            int loyalty = variant <= 3 ? 1 : 2;
            copy.Id = JackalIds.For("suit|" + suite);
            copy.Tid = new MongoId(JackalMod.TraderIdString);
            copy.IsActive = true;
            copy.IsHiddenInPVE = false;
            copy.InternalObtain = true;
            copy.ExternalObtain = false;
            copy.Requirements ??= new SuitRequirements();
            copy.Requirements.RequiredTid = copy.Tid;
            copy.Requirements.LoyaltyLevel = loyalty;
            copy.Requirements.ProfileLevel = 1;
            copy.Requirements.Standing = 0;
            copy.Requirements.SkillRequirements = [];
            copy.Requirements.QuestRequirements = [];
            copy.Requirements.AchievementRequirements = [];
            double price = Price(25_000);
            copy.Requirements.ItemRequirements =
            [new ItemRequirement { Tpl = Roubles, Count = price, OnlyFunctional = true, Type = "ItemRequirement" }];
            suits.Add(copy);
            entries.Add(new StockEntry(copy.Id.ToString(), suite, customization.Properties.Name ?? customization.Name ?? suite,
                loyalty, price, "Clothing"));
        }

        return new StockResult(assort, suits, entries);

        double HandbookPrice(MongoId id) => prices.TryGetValue(id, out var amount) && double.IsFinite(amount) && amount > 0 ? amount : 1;
        double Price(double amount) => Math.Max(1, Math.Round(amount * config.PriceMultiplier, MidpointRounding.AwayFromZero));

        void AddOffer(TemplateItem template, List<Item>? donorItems, double price, int loyalty, int stock, int limit, string kind)
        {
            MongoId offerId = JackalIds.For("offer|" + template.Id);
            List<Item> items;
            if (donorItems is { Count: > 0 })
            {
                items = cloner.Clone(donorItems) ?? throw new InvalidOperationException("Could not copy trader item tree.");
                var remap = items.ToDictionary(item => item.Id.ToString(), item =>
                    item.Id == donorItems[0].Id ? offerId : JackalIds.For("offer-child|" + template.Id + "|" + item.Id));
                foreach (var item in items)
                {
                    string oldId = item.Id.ToString();
                    item.Id = remap[oldId];
                    item.ParentId = item.ParentId is not null && remap.TryGetValue(item.ParentId, out var parent)
                        ? parent.ToString() : "hideout";
                    if (item.Id != offerId && item.ParentId == "hideout")
                        throw new InvalidOperationException("Copied trader tree contains an orphaned attachment.");
                }
            }
            else items = [new Item { Id = offerId, Template = template.Id, ParentId = "hideout", SlotId = "hideout" }];

            var root = items.Single(item => item.Id == offerId);
            root.ParentId = "hideout";
            root.SlotId = "hideout";
            root.Upd ??= new Upd();
            root.Upd.StackObjectsCount = Math.Max(1, stock);
            root.Upd.OriginalStackObjectsCount = Math.Max(1, stock);
            root.Upd.UnlimitedCount = false;
            root.Upd.BuyRestrictionMax = Math.Max(1, Math.Min(stock, limit));
            root.Upd.BuyRestrictionCurrent = 0;
            root.Upd.SpawnedInSession = false;
            assort.Items.AddRange(items);
            assort.BarterScheme.Add(offerId, [[new BarterScheme { Template = Roubles, Count = price }]]);
            assort.LoyalLevelItems.Add(offerId, loyalty);
            entries.Add(new StockEntry(offerId.ToString(), template.Id.ToString(),
                template.Properties?.Name ?? template.Name ?? template.Id.ToString(), loyalty, price, kind));
        }
    }

    private sealed record DonorOffer(List<Item> Items, double? RoublePrice);

    private static DonorOffer? FindOffer(MongoId template, TradersTable traders, HashSet<string> excluded)
    {
        var candidates = new List<DonorOffer>();
        foreach (var trader in traders.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal).Select(pair => pair.Value))
        {
            if (trader.Assort?.Items is not { } source) continue;
            foreach (var root in source.Where(item => item.Template == template && item.ParentId == "hideout")
                .OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
            {
                var branch = new List<Item> { root };
                var ids = new HashSet<string>(StringComparer.Ordinal) { root.Id.ToString() };
                bool added;
                do
                {
                    added = false;
                    foreach (var child in source)
                        if (child.ParentId is not null && ids.Contains(child.ParentId) && ids.Add(child.Id.ToString()))
                        { branch.Add(child); added = true; }
                } while (added);
                if (branch.Any(item => excluded.Contains(item.Template.ToString()))) continue;
                double? price = null;
                if (trader.Assort.BarterScheme?.TryGetValue(root.Id, out var alternatives) == true)
                {
                    price = alternatives.Where(terms => terms.Count > 0 && terms.All(term => term.Template == Roubles))
                        .Select(terms => terms.Sum(term => term.Count ?? 0)).Where(amount => amount > 0)
                        .Select(amount => (double?)amount).Min();
                }
                candidates.Add(new DonorOffer(branch, price));
            }
        }
        return candidates.OrderByDescending(offer => offer.Items.Count)
            .ThenBy(offer => offer.RoublePrice ?? double.MaxValue).FirstOrDefault();
    }

    private static int Loyalty(TemplateItem item, double price)
    {
        string id = item.Id.ToString();
        string name = (item.Properties?.Name ?? item.Name ?? "").ToLowerInvariant();
        if (id == Gs45) return 2;
        if (id == "67281f200000000000000001" || id == "67281f200000000000000102" ||
            id is "67281f200000000000000169" or "67281f20000000000000016a" or "67281f20000000000000016b") return 3;
        if (id.StartsWith("67281f20", StringComparison.Ordinal))
            return item.Parent == AmmoParent && id != "67281f200000000000000010" ? 3 : 2;
        if (id.StartsWith("672b5101", StringComparison.Ordinal)) return 2;
        if (id.StartsWith("672b5102", StringComparison.Ordinal)) return 1;
        if (id == "67271f100000000000000051") return 4;
        if (id == "67271f100000000000000050") return 3;
        if (id == "67271f100000000000000001") return 2;
        if (id.StartsWith("67271f11", StringComparison.Ordinal)) return 4;
        if (id == "67271f100000000000000002" || id.StartsWith("67271f16", StringComparison.Ordinal) ||
            name.Contains("balaclava") || name.Contains("facecover")) return 1;
        if (id == "67271f100000000000000056" || id is "67271f10000000000000002d" or "67271f10000000000000002e" or
            "67271f10000000000000002f" || name.Contains("scope") || name.Contains("suppressor") || name.Contains("silencer")) return 3;
        if (id.StartsWith("67271f10", StringComparison.Ordinal)) return 2;
        return price <= 10_000 ? 1 : 2;
    }

    private static HashSet<string>? ReadVanillaIds()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "templates", "items.json");
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
