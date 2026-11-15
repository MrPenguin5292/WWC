using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils.Cloners;

namespace JackalTee.Server;

#if !WWC   // built into WWC, the mod's metadata is WWC's own (mods/WWC/Server/Wwc.cs)
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "local.jackaltee.server";
    public string Name { get; init; } = "Jackal Custom Tee";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.4.4");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new() { ["local.jackal"] = new("^1.0.0") };
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}
#endif

/// <summary>Ids are derived once from fixed text so they never change; a profile that owns the shirt keeps it across rebuilds.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jackaltee|" + key))).ToLowerInvariant()[..24]);
    public static readonly MongoId Body = Of("body"), Hands = Of("hands"), Suit = Of("suit"), Offer = Of("offer");
    public const string Jackal = "672b4a010000000000000001";
    public const string BodyKey = "jackaltee/top_jackaltee.bundle";
    public const string HandsKey = "jackaltee/hands_jackaltee.bundle";
    // Donor: the Saiga t-shirt, for the standard PMC tee mesh, arms and gloves. The kit puts the logo-free Gladiator fabric on it.
    public static readonly MongoId DonorSuit = new("67ac8911f46484d877033ed6");
}

public record TeeConfig
{
    [System.Text.Json.Serialization.JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonPropertyName("priceRoubles")] public double PriceRoubles { get; set; } = 40_000;
    [System.Text.Json.Serialization.JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = "The Jackal custom tee";
    [System.Text.Json.Serialization.JsonPropertyName("description")] public string Description { get; set; } = " ";
    /// <summary>The Black Division quests are on and the shirts stay locked until the last one is done. false: no quests, shirts on sale from the start.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("questRequired")] public bool QuestRequired { get; set; } = true;
}

/// <summary>One shirt on sale. The first is the original; each "Send to The Jackal" adds one to shirts.json.</summary>
internal sealed record ShirtDef(int N, MongoId Body, MongoId Hands, MongoId Suit, MongoId Offer, string BodyKey, string HandsKey, string Name);

public record ShirtEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("n")] public int N { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = "";
}

internal static class Shirts
{
    public static List<ShirtDef> Load<T>(ModHelper helper, string dir, TeeConfig cfg, ISptLogger<T> logger)
    {
        var gone = Removed(helper, dir, logger);
        var list = new List<ShirtDef>();
        if (!gone.Contains(1)) list.Add(new(1, Ids.Body, Ids.Hands, Ids.Suit, Ids.Offer, Ids.BodyKey, Ids.HandsKey, cfg.Name));
        if (!File.Exists(System.IO.Path.Combine(dir, "shirts.json"))) return list;
        List<ShirtEntry>? entries = null;
        try { entries = helper.GetJsonDataFromFile<List<ShirtEntry>>(dir, "shirts.json"); }
        catch (Exception ex) { logger.Warning($"[JackalTee] shirts.json could not be read ({ex.Message}); only the first shirt is available"); }
        foreach (var e in (entries ?? []).Where(e => e.N >= 2 && !gone.Contains(e.N)).OrderBy(e => e.N))
        {
            var k = $"shirt{e.N}|";
            list.Add(new ShirtDef(e.N, Ids.Of(k + "body"), Ids.Of(k + "hands"), Ids.Of(k + "suit"), Ids.Of(k + "offer"),
                $"jackaltee/shirts/shirt{e.N}_top.bundle", $"jackaltee/shirts/shirt{e.N}_hands.bundle",
                string.IsNullOrWhiteSpace(e.Name) ? $"Custom tee {e.N}" : e.Name.Trim()));
        }
        return list;
    }

    /// <summary>Numbers of the shirts taken away with "Remove a shirt". A number is never used again.</summary>
    public static HashSet<int> Removed<T>(ModHelper helper, string dir, ISptLogger<T> logger)
    {
        if (!File.Exists(System.IO.Path.Combine(dir, "removed_shirts.json"))) return [];
        try { return (helper.GetJsonDataFromFile<List<int>>(dir, "removed_shirts.json") ?? []).ToHashSet(); }
        catch (Exception ex) { logger.Warning($"[JackalTee] removed_shirts.json could not be read ({ex.Message})"); return []; }
    }

    public static (MongoId Body, MongoId Hands, MongoId Suit) IdsOf(int n)
    {
        if (n == 1) return (Ids.Body, Ids.Hands, Ids.Suit);
        var k = $"shirt{n}|";
        return (Ids.Of(k + "body"), Ids.Of(k + "hands"), Ids.Of(k + "suit"));
    }
}

/// <summary>Stage 1: the clothing templates. Always registered so a profile wearing a shirt still loads.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 20)]
public class TeeTemplates(ISptLogger<TeeTemplates> logger, ModHelper modHelper, TemplateTable templates, LocaleTable locales, ICloner cloner) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = modHelper.GetJsonDataFromFile<TeeConfig>(dir, "config.json") ?? new TeeConfig();
        var table = templates.Customization;
        if (!table.TryGetValue(Ids.DonorSuit, out var donorSuit) || donorSuit.Properties.Body is null || donorSuit.Properties.Hands is null)
        { logger.Error("[JackalTee] donor shirt not found in customization database; not registering"); return Task.CompletedTask; }

        var strings = new Dictionary<string, string>();
        var shirts = Shirts.Load(modHelper, dir, cfg, logger);
        foreach (var s in shirts)
        {
            if (table.ContainsKey(s.Suit)) { logger.Error($"[JackalTee] {s.Name}: suit id already present; skipped"); continue; }
            var tag = s.N == 1 ? "jackaltee" : $"jackaltee_shirt{s.N}";

            var body = cloner.Clone(table[donorSuit.Properties.Body.Value])!;
            body.Id = s.Body; body.Name = tag + "_body";
            body.Properties.Prefab = new Prefab { Path = s.BodyKey, Rcid = "" };
            table[s.Body] = body;

            var hands = cloner.Clone(table[donorSuit.Properties.Hands.Value])!;
            hands.Id = s.Hands; hands.Name = tag + "_hands";
            hands.Properties.Prefab = new Prefab { Path = s.HandsKey, Rcid = "" };
            table[s.Hands] = hands;

            var suit = cloner.Clone(donorSuit)!;
            suit.Id = s.Suit; suit.Name = tag + "_suit";
            suit.Properties.Body = s.Body; suit.Properties.Hands = s.Hands;
            suit.Properties.Side = ["Bear", "Usec"];
            table[s.Suit] = suit;

            strings[$"{s.Suit} Name"] = s.Name;
            strings[$"{s.Suit} ShortName"] = s.Name;
            strings[$"{s.Suit} Description"] = cfg.Description;
        }
        // SPT refuses to load a profile that lists clothing it has never heard of, and it checks before mods can tidy up. So a removed shirt's ids stay
        // known, pointing at the game's own shirt, and TeeRemovedCleanup then takes it off the character.
        foreach (var n in Shirts.Removed(modHelper, dir, logger))
        {
            var (bodyId, handsId, suitId) = Shirts.IdsOf(n);
            if (table.ContainsKey(suitId)) continue;
            var body = cloner.Clone(table[donorSuit.Properties.Body.Value])!;
            body.Id = bodyId; body.Name = $"jackaltee_removed{n}_body";
            table[bodyId] = body;
            var hands = cloner.Clone(table[donorSuit.Properties.Hands.Value])!;
            hands.Id = handsId; hands.Name = $"jackaltee_removed{n}_hands";
            table[handsId] = hands;
            var suit = cloner.Clone(donorSuit)!;
            suit.Id = suitId; suit.Name = $"jackaltee_removed{n}_suit";
            suit.Properties.Body = bodyId; suit.Properties.Hands = handsId;
            suit.Properties.Side = ["Bear", "Usec"];
            table[suitId] = suit;
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
        logger.Success($"[JackalTee] clothing registered: {shirts.Count} shirt(s)");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 2: The Jackal sells the shirts once he has registered himself (he registers at TraderRegistration + 100).</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 120)]
public class TeeOffer(ISptLogger<TeeOffer> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = modHelper.GetJsonDataFromFile<TeeConfig>(dir, "config.json") ?? new TeeConfig();
        if (!cfg.Enabled) { logger.Info("[JackalTee] disabled in config.json, no offer"); return Task.CompletedTask; }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Base is null) { logger.Warning("[JackalTee] The Jackal not found; no offer"); return Task.CompletedTask; }
        jackal.Suits ??= [];
        var onSale = 0;
        foreach (var s in Shirts.Load(modHelper, dir, cfg, logger))
        {
            if (!templates.Customization.ContainsKey(s.Suit)) continue;
            var missing = new[] { s.BodyKey, s.HandsKey }.FirstOrDefault(key =>
            {
                var path = System.IO.Path.Combine(dir, "bundles", key.Replace('/', System.IO.Path.DirectorySeparatorChar));
                return !File.Exists(path) || new FileInfo(path).Length < 4096;
            });
            if (missing is not null) { logger.Warning($"[JackalTee] {s.Name}: {missing} is missing. It is kept for profiles that own it but is not on sale."); continue; }
            if (jackal.Suits.All(x => x.SuiteId != s.Suit))
            {
                jackal.Suits.Add(new Suit
                {
                    Id = s.Offer,
                    SuiteId = s.Suit,
                    Tid = new MongoId(Ids.Jackal),
                    IsActive = true,
                    IsHiddenInPVE = false,
                    ExternalObtain = false,
                    InternalObtain = true,
                    Requirements = new SuitRequirements
                    {
                        LoyaltyLevel = Math.Clamp(cfg.LoyaltyLevel, 1, 4),
                        ProfileLevel = 1,
                        Standing = 0,
                        SkillRequirements = [],
                        QuestRequirements = cfg.QuestRequired ? [BlackDivision.QuestId(BlackDivision.QuestCount).ToString()] : [],
                        AchievementRequirements = [],
                        RequiredTid = new MongoId(Ids.Jackal),
                        ItemRequirements = [new ItemRequirement { Tpl = new MongoId("5449016a4bdc2d6f028b456f"), Count = Math.Max(1, cfg.PriceRoubles), OnlyFunctional = true, Type = "ItemRequirement" }],
                    },
                });
            }
            onSale++;
        }
        if (onSale > 0) jackal.Base.CustomizationSeller = true;
        logger.Success($"[JackalTee] {onSale} shirt(s) on sale at The Jackal for {cfg.PriceRoubles:0} roubles each, LL{cfg.LoyaltyLevel}");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 3, once profiles are loaded: nobody may be left wearing or owning a shirt whose files are gone, or that character would not load.</summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 20)]
public class TeeRemovedCleanup(ISptLogger<TeeRemovedCleanup> logger, ModHelper modHelper, SaveServer saveServer) : IOnLoad
{
    // The game's own default tops, from SPT's profile templates.
    private static readonly MongoId BearBody = new("5cc0858d14c02e000c6bea66"), BearHands = new("5cc0876314c02e000c6bea6b");
    private static readonly MongoId UsecBody = new("5cde95d97d6c8b647a3769b0"), UsecHands = new("5cde95fa7d6c8b04737c2d13");

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var removed = Shirts.Removed(modHelper, dir, logger).Select(Shirts.IdsOf).ToList();
        if (removed.Count == 0) return;
        var bodies = removed.Select(r => r.Body).ToHashSet();
        var hands = removed.Select(r => r.Hands).ToHashSet();
        var suits = removed.Select(r => r.Suit).ToHashSet();
        foreach (var (id, profile) in saveServer.GetProfiles())
        {
            var changed = false;
            var pmc = profile.CharacterData?.PmcData;
            var worn = pmc?.Customization;
            if (worn is not null && ((worn.Body is { } b && bodies.Contains(b)) || (worn.Hands is { } h && hands.Contains(h))))
            {
                var bear = string.Equals(pmc!.Info?.Side, "Bear", StringComparison.OrdinalIgnoreCase);
                worn.Body = bear ? BearBody : UsecBody;
                worn.Hands = bear ? BearHands : UsecHands;
                changed = true;
                logger.Info($"[JackalTee] {pmc.Info?.Nickname} was wearing a removed shirt; back to the default top");
            }
            if (profile.CustomisationUnlocks is not null && profile.CustomisationUnlocks.RemoveAll(u => suits.Contains(u.Id)) > 0) changed = true;
            if (changed) await saveServer.SaveProfileAsync(id);
        }
    }
}
