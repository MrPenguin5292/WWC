using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Modding;
using SPTarkov.Server.Core.Utils.Cloners;

namespace WallFlag.Server;

#if !WWC   // built into WWC, the mod's metadata is WWC's own (mods/WWC/Server/Wwc.cs)
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "local.wallflag.server";
    public string Name { get; init; } = "Jackal Wall Flag";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    // No dependency on The Jackal: flags are found in raids. Without him, only the torn and dirty kits go unsold.
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}
#endif

/// <summary>Ids are derived once from fixed text so they never change; a profile that owns a flag keeps it across rebuilds.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wallflag|" + key))).ToLowerInvariant()[..24]);
    public const string Jackal = "672b4a010000000000000001";
    public const string Roubles = "5449016a4bdc2d6f028b456f";
    public const string WearSlot = "mod_wear";                   // also the name of the flag's mesh object in its bundle
    public static readonly MongoId DonorPoster = new("6759e19a8367ee2b0c0f4b92");
    public static readonly MongoId DonorMount = new("55d48a634bdc2d8b2f8b456a");    // an attachment-class item: gives the flag a slot it can use
    // The kits look like these two rolls of tape. The client plugin tells them apart by their model names, so keep them different.
    public static readonly MongoId DonorKitDirty = new("57347c1124597737fb1379e3"), DonorKitTorn = new("5734795124597738002c6176");
    public static readonly MongoId CustomizationStash = new("673c7b00cbf4b984b5099181");
    public static readonly MongoId WearDirty = Of("wear|dirty|item"), WearTorn = Of("wear|torn|item");
    public static readonly MongoId WearDirtyOffer = Of("wear|dirty|offer"), WearTornOffer = Of("wear|torn|offer");
    public static string KeyOf(int n) => n == 1 ? "wallflag/flag_1.bundle" : $"wallflag/flags/flag_{n}.bundle";
}

/// <summary>The names people use for the maps, as written in config.json, and the game's own id for each.</summary>
internal static class Maps
{
    public static readonly Dictionary<string, string> LocationIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Customs"] = "bigmap", ["Factory Day"] = "factory4_day", ["Factory Night"] = "factory4_night", ["Ground Zero"] = "Sandbox", ["Ground Zero 21+"] = "Sandbox_high",
        ["Interchange"] = "Interchange", ["Labs"] = "laboratory", ["Lighthouse"] = "Lighthouse", ["Reserve"] = "RezervBase", ["Shoreline"] = "Shoreline", ["Streets"] = "TarkovStreets", ["Woods"] = "Woods",
    };
}

public record RaidSettings
{
    [JsonPropertyName("percentOfPostersThatAreFlags")] public Dictionary<string, double> Percent { get; set; } = new()
    {
        ["Customs"] = 2, ["Factory Day"] = 1, ["Factory Night"] = 1, ["Ground Zero"] = 1, ["Ground Zero 21+"] = 1, ["Interchange"] = 2,
        ["Labs"] = 3, ["Lighthouse"] = 3, ["Reserve"] = 4, ["Shoreline"] = 2, ["Streets"] = 2, ["Woods"] = 3,
    };
    [JsonPropertyName("multiplyAllMaps")] public double MultiplyAllMaps { get; set; } = 1.0;
    [JsonPropertyName("labsWeaponAreaMultiplier")] public double LabsWeaponAreaMultiplier { get; set; } = 3.0;
    [JsonPropertyName("horizontalFlags")] public bool Horizontal { get; set; } = true;
    [JsonPropertyName("verticalFlags")] public bool Vertical { get; set; } = true;
    [JsonPropertyName("flagFrequency")] public Dictionary<string, double> FlagFrequency { get; set; } = new();
}

public record FlagSettings
{
    [JsonPropertyName("soldByTheJackal")] public bool SoldByJackal { get; set; } = false;
    [JsonPropertyName("jackalPriceRoubles")] public double JackalPrice { get; set; } = 25_000;
    [JsonPropertyName("jackalLoyaltyLevel")] public int JackalLoyalty { get; set; } = 1;
    [JsonPropertyName("worthRoubles")] public double Worth { get; set; } = 50_000;
    [JsonPropertyName("onFleaMarket")] public bool OnFlea { get; set; } = true;
    [JsonPropertyName("fleaPriceRoubles")] public double FleaPrice { get; set; } = 250_000;
    [JsonPropertyName("weightKg")] public double WeightKg { get; set; } = 0.4;
    [JsonPropertyName("inventoryWidth")] public int InventoryWidth { get; set; } = 2;
    [JsonPropertyName("inventoryHeight")] public int InventoryHeight { get; set; } = 2;
    [JsonPropertyName("firstFlagName")] public string FirstFlagName { get; set; } = "Blank wall flag";
    [JsonPropertyName("description")] public string Description { get; set; } = "A cloth flag taken off a wall in Tarkov. Hang it on any poster spot in your hideout from Hideout Customization, under Posters.";
}

public record KitSettings
{
    [JsonPropertyName("soldByTheJackal")] public bool SoldByJackal { get; set; } = true;
    [JsonPropertyName("jackalPriceRoubles")] public double JackalPrice { get; set; } = 15_000;
    [JsonPropertyName("jackalLoyaltyLevel")] public int JackalLoyalty { get; set; } = 1;
    [JsonPropertyName("worthRoubles")] public double Worth { get; set; } = 15_000;
    [JsonPropertyName("onFleaMarket")] public bool OnFlea { get; set; } = false;
    [JsonPropertyName("fleaPriceRoubles")] public double FleaPrice { get; set; } = 30_000;
    [JsonPropertyName("grimeKitWeightKg")] public double GrimeWeightKg { get; set; } = 0.1;
    [JsonPropertyName("rippingKitWeightKg")] public double RippingWeightKg { get; set; } = 0.05;
    [JsonPropertyName("grimeKitName")] public string GrimeName { get; set; } = "Flag grime kit";
    [JsonPropertyName("rippingKitName")] public string RippingName { get; set; } = "Flag ripping kit";
    [JsonPropertyName("grimeKitDescription")] public string GrimeDescription { get; set; } = "Goes in a wall flag's Wear slot. The flag looks dirty and stained for as long as it stays in.";
    [JsonPropertyName("rippingKitDescription")] public string RippingDescription { get; set; } = "Goes in a wall flag's Wear slot. The flag looks torn and frayed for as long as it stays in.";
}

/// <summary>config.json: every value a player might want to change, under plain names. The "looks" section is read by the client plugin.</summary>
public record FlagConfig
{
    [JsonPropertyName("modEnabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("raidSpawns")] public RaidSettings Raid { get; set; } = new();
    [JsonPropertyName("flags")] public FlagSettings Flag { get; set; } = new();
    [JsonPropertyName("tornAndDirtyKits")] public KitSettings Kit { get; set; } = new();
    // Test only, and deliberately not in the shipped file.
    [JsonPropertyName("qaEveryPosterSpotIsAFlag")] public bool QaEveryPosterSpotIsAFlag { get; set; } = false;
}

public record FlagEntry
{
    [JsonPropertyName("n")] public int N { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hang")] public string Hang { get; set; } = "horizontal";
}

/// <summary>One flag. The first is the blank one the kit updates; each new flag from the kit adds one to flags.json.</summary>
internal sealed record FlagDef(int N, MongoId Item, MongoId Offer, string Key, string Name, string Hang);

internal static class Flags
{
    public static List<FlagDef> Load<T>(ModHelper helper, string dir, FlagConfig cfg, ISptLogger<T> logger)
    {
        var list = new List<FlagDef> { new(1, Ids.Of("flag1|item"), Ids.Of("flag1|offer"), Ids.KeyOf(1), cfg.Flag.FirstFlagName, "horizontal") };
        if (!File.Exists(System.IO.Path.Combine(dir, "flags.json"))) return list;
        List<FlagEntry>? entries = null;
        try { entries = helper.GetJsonDataFromFile<List<FlagEntry>>(dir, "flags.json"); }
        catch (Exception ex) { logger.Warning($"[WallFlag] flags.json could not be read ({ex.Message}); only the first flag is available"); }
        foreach (var e in (entries ?? []).Where(e => e.N >= 2).OrderBy(e => e.N))
        {
            var k = $"flag{e.N}|";
            list.Add(new FlagDef(e.N, Ids.Of(k + "item"), Ids.Of(k + "offer"), Ids.KeyOf(e.N), string.IsNullOrWhiteSpace(e.Name) ? $"Wall flag {e.N}" : e.Name.Trim(), string.IsNullOrWhiteSpace(e.Hang) ? "horizontal" : e.Hang));
        }
        return list;
    }

    /// <summary>A still-blank first flag stays out of raids and off the flea market unless there is nothing else, and a flag needs its file.</summary>
    public static bool CanAppear(FlagDef f, IReadOnlyCollection<FlagDef> all, string dir) =>
        !Retired(dir).Contains(f.N) && (f.N >= 2 || all.Count == 1 || File.Exists(System.IO.Path.Combine(dir, "flag_1_painted")))
        && File.Exists(System.IO.Path.Combine(dir, "bundles", f.Key.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    private static HashSet<int>? _retired;

    /// <summary>Flags taken out of circulation from Painter's Remove a flag. They stay registered so any already owned keep working; they just stop appearing.</summary>
    public static HashSet<int> Retired(string dir)
    {
        if (_retired is not null) return _retired;
        try
        {
            var path = System.IO.Path.Combine(dir, "retired_flags.json");
            _retired = File.Exists(path) ? (System.Text.Json.JsonSerializer.Deserialize<List<int>>(File.ReadAllText(path)) ?? []).ToHashSet() : [];
        }
        catch (Exception) { _retired = []; }
        return _retired;
    }

    private static FlagConfig? _config;

    public static FlagConfig ReadConfig<T>(ModHelper helper, string dir, ISptLogger<T> logger)
    {
        if (_config is not null) return _config;
        FlagConfig? cfg = null;
        try { cfg = helper.GetJsonDataFromFile<FlagConfig>(dir, "config.json"); }
        catch (Exception ex) { logger.Error($"[WallFlag] config.json has a mistake in it, so the normal settings are being used instead: {ex.Message}"); }
        cfg ??= new FlagConfig();
        cfg.Raid ??= new RaidSettings();
        cfg.Flag ??= new FlagSettings();
        cfg.Kit ??= new KitSettings();
        cfg.Raid.Percent ??= [];
        cfg.Raid.FlagFrequency ??= [];
        foreach (var (name, percent) in cfg.Raid.Percent)
        {
            if (!Maps.LocationIds.ContainsKey(name)) logger.Warning($"[WallFlag] config.json: \"{name}\" is not a map name, so that line does nothing. Maps: {string.Join(", ", Maps.LocationIds.Keys)}");
            else if (percent > 95) logger.Warning($"[WallFlag] config.json: {name} is set to {percent}, and the most a map can have is 95");
        }
        return _config = cfg;
    }
}

/// <summary>Stage 1: the item templates. Always registered so a profile that owns a flag still loads even if the files are gone.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 20)]
public class FlagTemplates(ISptLogger<FlagTemplates> logger, ModHelper modHelper, TemplateTable templates, LocaleTable locales, ICloner cloner,
    ItemBaseClassService itemBaseClasses, ModItemCacheService modItems, ItemConfig itemConfig, RagfairConfig ragfairConfig, PmcConfig pmcConfig,
    ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, AirdropConfig airdropConfig, TraderConfig traderConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = Flags.ReadConfig(modHelper, dir, logger);
        if (!templates.Items.TryGetValue(Ids.DonorPoster, out var poster) || poster.Properties is null || !templates.Items.TryGetValue(Ids.DonorMount, out var mount) || mount.Properties?.Slots is null || !templates.Items.TryGetValue(Ids.DonorKitDirty, out var kitDirty) || kitDirty.Properties is null || !templates.Items.TryGetValue(Ids.DonorKitTorn, out var kitTorn) || kitTorn.Properties is null)
        { logger.Error("[WallFlag] donor items not found in the item database; not registering"); return Task.CompletedTask; }
        var handbookParent = templates.Handbook.Items.FirstOrDefault(i => i.Id == Ids.DonorPoster)?.ParentId ?? new MongoId("5b47574386f77428ca22b2f4");
        var kitHandbookParent = templates.Handbook.Items.FirstOrDefault(i => i.Id == Ids.DonorKitDirty)?.ParentId ?? handbookParent;

        // The server rebuilds flea prices from handbook value times a multiplier when it starts, so the flea price is set as that multiplier.
        // Do not also write templates.Prices: measured listings came out as that value plus the rebuilt one.
        void SetFleaPrice(MongoId id, double flea, double worth)
        {
            var value = Math.Max(1, worth);
            var price = Math.Max(1, flea);
            ragfairConfig.Dynamic.GenerateBaseFleaPrices.ItemTplMultiplierOverride[id] = price / value;
        }

        if (cfg.Flag.OnFlea && cfg.Flag.FleaPrice > cfg.Flag.Worth * 6) logger.Warning($"[WallFlag] config.json: the flea market will not list a flag above 6 times worthRoubles, so flags will sell for about {cfg.Flag.Worth * 6:0} and not {cfg.Flag.FleaPrice:0}. Raise worthRoubles to match.");
        if (cfg.Kit.OnFlea && cfg.Kit.FleaPrice > cfg.Kit.Worth * 6) logger.Warning($"[WallFlag] config.json: the flea market will not list a kit above 6 times worthRoubles, so kits will sell for about {cfg.Kit.Worth * 6:0} and not {cfg.Kit.FleaPrice:0}. Raise worthRoubles to match.");

        var strings = new Dictionary<string, string> { [Ids.WearSlot] = "Wear", [Ids.WearSlot.ToUpperInvariant()] = "Wear" };
        var flags = Flags.Load(modHelper, dir, cfg, logger);
        foreach (var f in flags)
        {
            if (templates.Items.ContainsKey(f.Item)) { logger.Error($"[WallFlag] {f.Name}: item id already present; skipped"); continue; }
            var item = cloner.Clone(mount)!;         // attachment class, so it can hold the wear item; posters cannot have slots
            item.Id = f.Item;
            item.Name = f.N == 1 ? "wallflag_blank" : $"wallflag_{f.N}";
            var p = item.Properties!;
            var slot = cloner.Clone(mount.Properties.Slots.First())!;
            slot.Name = Ids.WearSlot;
            slot.Id = Ids.Of($"flag{f.N}|slot");
            slot.Parent = f.Item;
            slot.Properties = new SlotProperties { Filters = [new SlotFilter { Shift = 0, Filter = [Ids.WearDirty, Ids.WearTorn] }] };
            p.Slots = [slot];
            p.Name = item.Name;
            p.ShortName = item.Name;
            p.Description = item.Name;
            p.Prefab = new Prefab { Path = f.Key, Rcid = "" };
            p.Width = Math.Clamp(cfg.Flag.InventoryWidth, 1, 10);
            p.Height = Math.Clamp(cfg.Flag.InventoryHeight, 1, 10);
            p.ItemSound = poster.Properties.ItemSound;
            p.ExaminedByDefault = true;
            p.CanSellOnRagfair = cfg.Flag.OnFlea && Flags.CanAppear(f, flags, dir);
            p.CanRequireOnRagfair = false;
            p.Weight = Math.Max(0.0, cfg.Flag.WeightKg);
            p.RaidModdable = true;
            p.ToolModdable = false;
            p.ConflictingItems = [];
            templates.Items[f.Item] = item;
            templates.Handbook.Items.Add(new() { Id = f.Item, ParentId = handbookParent, Price = Math.Max(1, cfg.Flag.Worth) });
            SetFleaPrice(f.Item, cfg.Flag.FleaPrice, cfg.Flag.Worth);
            itemBaseClasses.AddItemToCache(f.Item);
            modItems.AddModItem(Assembly.GetExecutingAssembly(), f.Item);
            strings[$"{f.Item} Name"] = f.Name;
            strings[$"{f.Item} ShortName"] = f.Name.Length <= 20 ? f.Name : "Flag";
            strings[$"{f.Item} Description"] = cfg.Flag.Description;
        }

        // The two slot items. Put one in a flag's Wear slot to tear or dirty it; take it out and the flag is clean again.
        foreach (var (id, kit, tag, name, shortName, text, weightKg) in new[]
        {
            (Ids.WearDirty, kitDirty, "dirty", cfg.Kit.GrimeName, "Grime", cfg.Kit.GrimeDescription, cfg.Kit.GrimeWeightKg),
            (Ids.WearTorn, kitTorn, "torn", cfg.Kit.RippingName, "Rips", cfg.Kit.RippingDescription, cfg.Kit.RippingWeightKg),
        })
        {
            var item = cloner.Clone(kit)!;
            item.Id = id;
            item.Name = "wallflag_wear_" + tag;
            item.Properties!.Name = item.Name;
            item.Properties.ShortName = item.Name;
            item.Properties.Description = item.Name;
            item.Properties.CanSellOnRagfair = cfg.Kit.OnFlea;
            item.Properties.Weight = Math.Max(0.0, weightKg);
            item.Properties.CanRequireOnRagfair = false;
            templates.Items[id] = item;
            templates.Handbook.Items.Add(new() { Id = id, ParentId = kitHandbookParent, Price = Math.Max(1, cfg.Kit.Worth) });
            SetFleaPrice(id, cfg.Kit.FleaPrice, cfg.Kit.Worth);
            itemBaseClasses.AddItemToCache(id);
            modItems.AddModItem(Assembly.GetExecutingAssembly(), id);
            strings[$"{id} Name"] = name;
            strings[$"{id} ShortName"] = shortName;
            strings[$"{id} Description"] = text;
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

        // Let the flags onto every hideout poster spot.
        var slots = 0;
        if (templates.Items.TryGetValue(Ids.CustomizationStash, out var stash) && stash.Properties?.Slots is not null)
        {
            foreach (var slot in stash.Properties.Slots)
            {
                if (slot.Name is null || !slot.Name.StartsWith("Poster_", StringComparison.Ordinal)) continue;
                var filter = slot.Properties?.Filters?.FirstOrDefault()?.Filter;
                if (filter is null) continue;
                foreach (var f in flags) filter.Add(f.Item);
                slots++;
            }
        }
        if (slots == 0) logger.Warning("[WallFlag] no poster spots found; flags cannot be hung");

        // Keep flags and kits out of random loot and reward tables, and off Fence. Flags are added to the poster spots on purpose in FlagWorldLoot.
        var ids = flags.Select(f => f.Item).Append(Ids.WearDirty).Append(Ids.WearTorn).ToList();
        itemConfig.RewardItemBlacklist.UnionWith(ids);
        // The flea market only lists what is off this list. It also clears the sell flag on anything on it.
        ragfairConfig.Dynamic.Blacklist.Custom.UnionWith(flags.Where(f => !cfg.Flag.OnFlea || !Flags.CanAppear(f, flags, dir)).Select(f => f.Item));
        if (!cfg.Kit.OnFlea) ragfairConfig.Dynamic.Blacklist.Custom.UnionWith([Ids.WearDirty, Ids.WearTorn]);
        // Flags and kits inherit durability from their donors, and it should never move their price.
        foreach (var id in ids) ragfairConfig.Dynamic.IgnoreQualityPriceVarianceBlacklist.Add(id);
        pmcConfig.GlobalLootBlacklist.AddRange(ids.Except(pmcConfig.GlobalLootBlacklist));
        scavCaseConfig.RewardItemBlacklist.UnionWith(ids);
        hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(ids.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
        traderConfig.Fence.Blacklist.UnionWith(ids);
        foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(ids.Except(drop.ItemBlacklist));

        logger.Success($"[WallFlag] {flags.Count} flag(s) registered, allowed on {slots} poster spots; {(cfg.Flag.OnFlea ? $"on the flea market at about {cfg.Flag.FleaPrice:0} roubles" : "not on the flea market")}");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 1b: flags hang in raids. The maps already have wall spots where posters spawn; each spot that passed the fit survey gets the flags as extra options in its loot table.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 30)]
public class FlagWorldLoot(ISptLogger<FlagWorldLoot> logger, ModHelper modHelper, LocationTable locations, TemplateTable templates) : IOnLoad
{
    private static bool IsPosterSpot(Spawnpoint s)
    {
        var id = s.Template?.Id ?? "";
        return id.StartsWith("Flyer", StringComparison.OrdinalIgnoreCase) || id.StartsWith("lootFlyer", StringComparison.OrdinalIgnoreCase);
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = Flags.ReadConfig(modHelper, dir, logger);
        if (!cfg.Enabled) { logger.Info("[WallFlag] disabled in config.json, no world flags"); return Task.CompletedTask; }

        var all = Flags.Load(modHelper, dir, cfg, logger).Where(f => templates.Items.ContainsKey(f.Item)).ToList();
        var pool = all.Where(f => Flags.CanAppear(f, all, dir)).Where(f => f.Hang == "vertical" ? cfg.Raid.Vertical : cfg.Raid.Horizontal).ToList();
        if (pool.Count == 0) { logger.Warning("[WallFlag] no flags to hang, so none will spawn in raids (flag files missing, or horizontalFlags and verticalFlags are off)"); return Task.CompletedTask; }

        // spots.json: which flag shapes fit at each spot, from measuring every spot in a raid (qa/SurveyAllMaps.ps1). A spot it does not list gets no flags.
        Dictionary<string, Dictionary<string, List<string>>>? fit = null;
        try { if (File.Exists(System.IO.Path.Combine(dir, "spots.json"))) fit = modHelper.GetJsonDataFromFile<Dictionary<string, Dictionary<string, List<string>>>>(dir, "spots.json"); }
        catch (Exception ex) { logger.Warning($"[WallFlag] spots.json could not be read ({ex.Message}); no flags will spawn in raids"); return Task.CompletedTask; }
        if (fit is null && !cfg.QaEveryPosterSpotIsAFlag) { logger.Warning("[WallFlag] spots.json is missing; no flags will spawn in raids"); return Task.CompletedTask; }

        // favoured_spots.json: the spots beside the weapon crates on Labs. labsWeaponAreaMultiplier in config.json multiplies the share at them.
        Dictionary<string, List<string>>? favoured = null;
        try { if (File.Exists(System.IO.Path.Combine(dir, "favoured_spots.json"))) favoured = modHelper.GetJsonDataFromFile<Dictionary<string, List<string>>>(dir, "favoured_spots.json"); }
        catch (Exception ex) { logger.Warning($"[WallFlag] favoured_spots.json could not be read ({ex.Message}); every spot counts the same"); }

        var percents = new Dictionary<string, double>();
        foreach (var (name, percent) in cfg.Raid.Percent)
            if (Maps.LocationIds.TryGetValue(name, out var id)) percents[id] = percent;
        var allMaps = Math.Max(0.0, cfg.Raid.MultiplyAllMaps);
        var favouredFactor = Math.Clamp(cfg.Raid.LabsWeaponAreaMultiplier, 0.0, 20.0);

        var frequency = new Dictionary<string, double>(cfg.Raid.FlagFrequency, StringComparer.OrdinalIgnoreCase);
        foreach (var name in frequency.Keys.Where(n => all.All(f => !f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
            logger.Warning($"[WallFlag] config.json: flagFrequency names \"{name}\", but no flag has that name");
        double Often(FlagDef f) => frequency.TryGetValue(f.Name, out var v) ? Math.Max(0.0, v) : 1.0;

        int maps = 0, spotsUsed = 0;
        foreach (var (_, location) in locations.GetDictionary())
        {
            if (location?.LooseLoot is null || location.Base?.Id is null) continue;
            var mapId = location.Base.Id;
            var share = Math.Clamp((percents.TryGetValue(mapId, out var p) ? p : 0.0) / 100.0 * allMaps, 0.0, 0.95);
            if (share <= 0 && !cfg.QaEveryPosterSpotIsAFlag) continue;
            maps++;
            var mapFit = fit is not null && fit.TryGetValue(mapId, out var m) ? m : null;
            var mapFavoured = favoured is not null && favoured.TryGetValue(mapId, out var fav) ? fav.ToHashSet() : null;
            location.LooseLoot.AddTransformer(loot =>
            {
                if (loot?.Spawnpoints is null) return loot;
                var index = 0;
                var points = loot.Spawnpoints.ToList();
                foreach (var point in points.Where(IsPosterSpot))
                {
                    var spot = index++;
                    var items = point.Template!.Items!.ToList();
                    var dist = point.ItemDistribution!.ToList();
                    var here = cfg.QaEveryPosterSpotIsAFlag ? pool : pool.Where(f => Often(f) > 0 && mapFit is not null && mapFit.TryGetValue(point.Template.Id!, out var shapes) && shapes.Contains(f.Hang)).ToList();
                    if (here.Count == 0) continue;
                    var spotShare = Math.Clamp(share * (mapFavoured is not null && mapFavoured.Contains(point.Template.Id!) ? favouredFactor : 1.0), 0.0, 0.95);
                    var flagsTotal = dist.Sum(d => (double)(d.RelativeProbability ?? 0f)) * spotShare / (1.0 - spotShare);
                    var oftenTotal = here.Sum(Often);
                    if (cfg.QaEveryPosterSpotIsAFlag)
                    {
                        // Test mode: every other spot always holds a flag, the rest always a stock poster, for side-by-side checks.
                        point.Probability = 1.0f;
                        if (spot % 2 == 1) continue;
                        items.Clear(); dist.Clear(); flagsTotal = here.Count; oftenTotal = here.Count;
                    }
                    if (flagsTotal <= 0) continue;
                    foreach (var f in here)
                    {
                        var flagWeight = flagsTotal * (cfg.QaEveryPosterSpotIsAFlag ? 1.0 : Often(f)) / oftenTotal;
                        var key = $"wallflag-{f.N}";
                        items.Add(new SptLootItem { Id = Ids.Of($"spot|{mapId}|{point.Template.Id}|{f.N}"), Template = f.Item, Upd = new Upd { StackObjectsCount = 1 }, ComposedKey = key });
                        dist.Add(new LooseLootItemDistribution { ComposedKey = new ComposedKey { Key = key }, RelativeProbability = (float)flagWeight });
                    }
                    point.Template.Items = items;
                    point.ItemDistribution = dist;
                }
                loot.Spawnpoints = points;
                return loot;
            });
            if (mapFit is not null) spotsUsed += mapFit.Count;
        }
        logger.Success($"[WallFlag] {pool.Count} flag(s) can hang in raids: {maps} maps, {spotsUsed} checked spots, {favoured?.Values.Sum(v => v.Count) ?? 0} favoured spots at x{favouredFactor:0.##}");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 2: The Jackal sells the torn and dirty kits (and the flags only if soldByJackal is on). He registers at TraderRegistration + 100.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 120)]
public class FlagOffers(ISptLogger<FlagOffers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = Flags.ReadConfig(modHelper, dir, logger);
        if (!cfg.Enabled) { logger.Info("[WallFlag] disabled in config.json, no offers"); return Task.CompletedTask; }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Assort is null) { logger.Warning("[WallFlag] The Jackal not found; the torn and dirty kits are not on sale"); return Task.CompletedTask; }
        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= [];
        assort.LoyalLevelItems ??= [];

        void Sell(MongoId offer, MongoId item, double price, int level)
        {
            if (assort.Items.Any(i => i.Id == offer)) return;
            assort.Items.Add(new Item { Id = offer, Template = item, ParentId = "hideout", SlotId = "hideout", Upd = new Upd { StackObjectsCount = 9_999_999, UnlimitedCount = true, SpawnedInSession = false } });
            assort.BarterScheme[offer] = [[new BarterScheme { Template = new MongoId(Ids.Roubles), Count = Math.Clamp(price, 1, 10_000_000) }]];
            assort.LoyalLevelItems[offer] = Math.Clamp(level, 1, 4);
        }

        if (cfg.Kit.SoldByJackal && templates.Items.ContainsKey(Ids.WearDirty))
        {
            Sell(Ids.WearDirtyOffer, Ids.WearDirty, cfg.Kit.JackalPrice, cfg.Kit.JackalLoyalty);
            Sell(Ids.WearTornOffer, Ids.WearTorn, cfg.Kit.JackalPrice, cfg.Kit.JackalLoyalty);
            logger.Success($"[WallFlag] torn and dirty kits on sale at The Jackal for {cfg.Kit.JackalPrice:0} roubles each, LL{Math.Clamp(cfg.Kit.JackalLoyalty, 1, 4)}");
        }

        if (!cfg.Flag.SoldByJackal) { logger.Info("[WallFlag] flags are not sold by traders"); return Task.CompletedTask; }
        var onSale = 0;
        foreach (var f in Flags.Load(modHelper, dir, cfg, logger))
        {
            if (!templates.Items.ContainsKey(f.Item) || Flags.Retired(dir).Contains(f.N)) continue;
            var path = System.IO.Path.Combine(dir, "bundles", f.Key.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length < 4096)
            { logger.Warning($"[WallFlag] {f.Name}: {f.Key} is missing. It is kept for profiles that own it but is not on sale."); continue; }
            Sell(f.Offer, f.Item, cfg.Flag.JackalPrice, cfg.Flag.JackalLoyalty);
            onSale++;
        }
        logger.Success($"[WallFlag] {onSale} flag(s) on sale at The Jackal, LL{Math.Clamp(cfg.Flag.JackalLoyalty, 1, 4)}");
        return Task.CompletedTask;
    }
}
