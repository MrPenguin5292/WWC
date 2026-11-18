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

namespace WWCWraps.Server;

#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.wwcwraps.server";
    public string Name { get; init; } = "WWC Wraps";
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
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wwcwraps|" + key))).ToLowerInvariant()[..24]);
    public static MongoId Wrap(string bas, string camo) => Of($"{bas}|{camo}");
    public static MongoId Offer(string bas, string camo) => Of($"offer|{bas}|{camo}");
    public static MongoId Tape(string camo) => Of($"tape|{camo}");
    public static MongoId TapeOffer(string camo) => Of($"offer|tape|{camo}");
    public static MongoId TapeSlot(MongoId mag) => Of($"slot|{mag}");
    /// <summary>0.4.0: a new copy of a stock part, sold unwrapped, with a wrap slot (Widowz 2026-11-18: "Just sell the handguard unwrapped and
    /// have it marked Wrappable make sure these are new handguards").</summary>
    public static MongoId Wrappable(string bas) => Of($"wrappable|{bas}");
    public static MongoId WrappableOffer(string bas) => Of($"offer|wrappable|{bas}");
    public static MongoId WrapSlot(string bas) => Of($"wrapslot|{bas}");
    /// <summary>The wrap slot is named after the part's near-model node: the game hangs whatever sits in a slot on the node of that name, and
    /// the client hides the hung roll there (the same trick as the tape slot on WWC mags).</summary>
    public static readonly Dictionary<string, string> WrapSlotName = new()
    {
        ["m4hg"] = "handguard_ar15_colt_m4_length_std_LOD0",
        ["moesl"] = "handguard_ar15_magpul_moe_sl_carabine_length_LOD0",
        ["mp5trop"] = "handguard_mp5_hk_wide_tropical_LOD0",
        ["a2grip"] = "pistolgrip_ar15_colt_a2_LOD0",
    };
    public const string SlotProto = "55d30c4c4bdc2db4468b457e";
    /// <summary>WWC Mags names every magazine template it registers "wwcmag_&lt;n&gt;"; that is how its mags, present and future, are found.</summary>
    public const string WwcMagPrefix = "wwcmag_";
    public const string Jackal = "672b4a010000000000000001";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
}

/// <summary>wraps.json, written by the build (tools/build_wraps.py + tools/write_data.py): the parts that take a wrap, the camos, and
/// everything the clipping check found too close to a wrap.</summary>
public record WrapData
{
    [JsonPropertyName("bases")] public List<BaseEntry> Bases { get; set; } = [];
    [JsonPropertyName("camos")] public List<CamoEntry> Camos { get; set; } = [];
    /// <summary>The stock magazine bodies the tape is cut for. A WWC mag built on one of them gets a tape slot.</summary>
    [JsonPropertyName("mags")] public List<MagBody> Mags { get; set; } = [];
    [JsonPropertyName("tape")] public TapeData Tape { get; set; } = new();
}

public record MagBody
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("donor")] public string Donor { get; set; } = "";
    [JsonPropertyName("prefab")] public string Prefab { get; set; } = "";
    /// <summary>The slot's name: the name of the body's near-model node, which is where the game hangs whatever sits in a slot.</summary>
    [JsonPropertyName("slot")] public string Slot { get; set; } = "";
}

/// <summary>Raid Tape rolls: one item per tape colour or print, fitted in the tape slot of a WWC mag.</summary>
public record TapeData
{
    [JsonPropertyName("maker")] public string Maker { get; set; } = "";
    [JsonPropertyName("donor")] public string Donor { get; set; } = "";
    [JsonPropertyName("prefab")] public string Prefab { get; set; } = "";
    [JsonPropertyName("items")] public List<TapeEntry> Items { get; set; } = [];
}

public record TapeEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("short")] public string Short { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

public record BaseEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("donor")] public string Donor { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("thing")] public string Thing { get; set; } = "";
    [JsonPropertyName("short")] public string Short { get; set; } = "";
    [JsonPropertyName("wrapWord")] public string WrapWord { get; set; } = "wrap";
    /// <summary>Which tape the client draws (cloth, raid, grain): its camo pages are named camo/&lt;camo&gt;_&lt;surface&gt;.jpg.</summary>
    [JsonPropertyName("surface")] public string Surface { get; set; } = "";
    /// <summary>Guns or parts whose own model comes within the clearance of the tape: the wrapped item does not fit their slot.</summary>
    [JsonPropertyName("blockedHosts")] public List<string> BlockedHosts { get; set; } = [];
    /// <summary>Parts that would cut into the tape when fitted in the wrapped item's own slots: taken out of those slots.</summary>
    [JsonPropertyName("blockedChildren")] public Dictionary<string, List<string>> BlockedChildren { get; set; } = new();
    /// <summary>Parts elsewhere on the gun that would cut into the tape (barrels, gas blocks): made conflicts, both ways.</summary>
    [JsonPropertyName("conflicts")] public List<string> Conflicts { get; set; } = [];
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    /// <summary>Stock parts sold already fitted in the wrapped item's slots (the M4 wrap goes round the stock lower half too).</summary>
    [JsonPropertyName("with")] public Dictionary<string, string> With { get; set; } = new();
}

public record CamoEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("short")] public string Short { get; set; } = "";
}

public record WrapsConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("jackal")] public JackalSettings Jackal { get; set; } = new();
    [JsonPropertyName("fleaMarket")] public FleaSettings Flea { get; set; } = new();
    [JsonPropertyName("items")] public ItemSettings Items { get; set; } = new();

    public WrapsConfig Sane()
    {
        Jackal.Stock = Math.Clamp(Jackal.Stock, 1, 9_999_999);
        Jackal.BuyLimit = Math.Clamp(Jackal.BuyLimit, 1, 9_999_999);
        Jackal.LoyaltyLevel = Math.Clamp(Jackal.LoyaltyLevel, 1, 4);
        // a config from an older version may list only some prices: the rest keep the built-in price
        foreach (var (k, v) in new JackalSettings().PriceRoubles) Jackal.PriceRoubles.TryAdd(k, v);
        foreach (var k in Jackal.PriceRoubles.Keys.ToList()) Jackal.PriceRoubles[k] = Math.Clamp(Jackal.PriceRoubles[k], 1, 100_000_000);
        return this;
    }

    /// <summary>
    /// Whether The Jackal sells this part in this look: the part must be in "parts", the look in "camos" (all parts) and, when the part has a
    /// list in "camosByPart", in that list too. Anything not sold stays in the game (owned ones, loot); it is only left off his shelf.
    /// </summary>
    public bool Sells(string bas, string camo)
    {
        if (!SellsPart(bas)) return false;
        if (bas != "tape" && !Jackal.SellPrewrapped) return false;   // 0.4.0: parts come plain (Wrappable), Raid Tape wraps them
        if (Jackal.Camos.Count > 0 && !Jackal.Camos.Contains("all") && !Jackal.Camos.Contains(camo)) return false;
        return Jackal.CamosByPart is null || !Jackal.CamosByPart.TryGetValue(bas, out var mine) || mine is null || mine.Count == 0
            || mine.Contains("all") || mine.Contains(camo);
    }

    public bool SellsPart(string bas) => Jackal.Parts.Count == 0 || Jackal.Parts.Contains("all") || Jackal.Parts.Contains(bas);


    public double PriceOf(string bas) => Math.Max(1, Jackal.PriceRoubles.TryGetValue(bas, out var p) ? p : 10_000);
}

public record JackalSettings
{
    [JsonPropertyName("sell")] public bool Sell { get; set; } = true;
    [JsonPropertyName("parts")] public List<string> Parts { get; set; } = ["all"];
    [JsonPropertyName("camos")] public List<string> Camos { get; set; } = ["all"];
    /// <summary>
    /// Per part, the looks he sells (Widowz 2026-11-18: "only sell some ... so its not bloated", then "same with grips"). The default is a
    /// varied eight for each wrapped part; a part with no list here (or ["all"]) sells every look "camos" allows.
    /// </summary>
    [JsonPropertyName("camosByPart")] public Dictionary<string, List<string>>? CamosByPart { get; set; } = DefaultSelection();
    /// <summary>0.4.0: false = the parts made wrapped in one look each (the 0.3 way) stay in the game but aren't sold.</summary>
    [JsonPropertyName("sellPrewrapped")] public bool SellPrewrapped { get; set; }
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 1;
    [JsonPropertyName("stock")] public int Stock { get; set; } = 10;
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 10;
    [JsonPropertyName("priceRoubles")] public Dictionary<string, double> PriceRoubles { get; set; } = new()
    {
        ["tape"] = 4500, ["m4hg"] = 16000, ["moesl"] = 19000, ["mp5trop"] = 15000, ["a2grip"] = 8000,
    };

    /// <summary>Two plain tapes and six well-known prints (green, desert, jungle, digital and European), the same for every wrapped part so
    /// a handguard and a grip can match.</summary>
    public static readonly string[] OnSale = ["raid_black", "raid_odg", "woodland", "blobs", "tiger", "dcu", "digital", "flecktarn"];

    public static Dictionary<string, List<string>> DefaultSelection() => new()
    {
        ["m4hg"] = [.. OnSale], ["moesl"] = [.. OnSale], ["mp5trop"] = [.. OnSale], ["a2grip"] = [.. OnSale],
    };
}

public record FleaSettings
{
    [JsonPropertyName("sell")] public bool Sell { get; set; }
}

public record ItemSettings
{
    [JsonPropertyName("fitWhereStockFits")] public bool FitWhereStockFits { get; set; } = true;
    [JsonPropertyName("keepOutOfLoot")] public bool KeepOutOfLoot { get; set; } = true;
    /// <summary>Ergonomics a roll of Raid Tape adds to the gun its mag is in.</summary>
    [JsonPropertyName("tapeErgonomics")] public double TapeErgonomics { get; set; } = 1;
}

internal static class Shared
{
    // People edit config.json by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static WrapsConfig? loaded;
    private static WrapData? data;

    public static WrapData Data<T>(string dir, ISptLogger<T> logger)
    {
        if (data is not null) return data;
        var path = Path.Combine(dir, "wraps.json");
        try { data = File.Exists(path) ? JsonSerializer.Deserialize<WrapData>(File.ReadAllText(path), Forgiving) ?? new WrapData() : new WrapData(); }
        catch (Exception ex) { logger.Error($"[WWCWraps] wraps.json could not be read ({ex.Message}); no wraps registered"); data = new WrapData(); }
        data.Bases = data.Bases.Where(b => !string.IsNullOrWhiteSpace(b.Key) && System.Text.RegularExpressions.Regex.IsMatch(b.Donor ?? "", "^[0-9a-f]{24}$")).ToList();
        data.Camos = data.Camos.Where(c => !string.IsNullOrWhiteSpace(c.Key)).ToList();
        data.Mags = data.Mags.Where(m => !string.IsNullOrWhiteSpace(m.Slot) && !string.IsNullOrWhiteSpace(m.Prefab)).ToList();
        data.Tape.Items = data.Tape.Items.Where(t => !string.IsNullOrWhiteSpace(t.Key)).ToList();
        return data;
    }

    public static WrapsConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (loaded is not null) return loaded;
        var path = Path.Combine(dir, "config.json");
        try { loaded = (File.Exists(path) ? JsonSerializer.Deserialize<WrapsConfig>(File.ReadAllText(path), Forgiving) : null) ?? new WrapsConfig(); }
        catch (JsonException ex) { logger.Error($"[WWCWraps] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values until that's fixed. {ex.Message}"); loaded = new WrapsConfig(); }
        catch (Exception ex) { logger.Error($"[WWCWraps] config.json could not be read ({ex.Message}), so the mod is using its normal values"); loaded = new WrapsConfig(); }
        return loaded.Sane();
    }

#if WWC   // built into WWC: its client files are in BepInEx\plugins\WWC\Wraps and its code is in WWC.Client.dll
    private static string PluginDir(string serverModDir) => WWC.Server.Parts.ClientPart(WWC.Server.Parts.Wraps);
    private static bool ClientDll(string plugin) => WWC.Server.Parts.ClientPresent;
#else
    private static string PluginDir(string serverModDir) =>
        Path.Combine(Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", "..")), "BepInEx", "plugins", "WWCWraps");
    private static bool ClientDll(string plugin) => File.Exists(Path.Combine(plugin, "WWCWraps.Client.dll"));
#endif

    /// <summary>True when the client plugin, the tape mesh for this part and this camo's texture are on disk, so nothing is sold that would
    /// only look like the stock item.</summary>
    public static bool ClientHas(string serverModDir, BaseEntry b, string camo)
    {
        var plugin = PluginDir(serverModDir);
        var surface = string.IsNullOrWhiteSpace(b.Surface) ? (b.Kind == "grip" ? "grain" : "cloth") : b.Surface;
        return ClientDll(plugin) && File.Exists(Path.Combine(plugin, "bases", b.Key, "lod0.bin"))
            && File.Exists(Path.Combine(plugin, "camo", $"{camo}_{surface}.jpg"));
    }

    /// <summary>True when the client plugin, the tape meshes for the mag bodies and this tape's textures are on disk.</summary>
    public static bool ClientHasTape(string serverModDir, WrapData d, string camo)
    {
        var plugin = PluginDir(serverModDir);
        return ClientDll(plugin) && d.Mags.All(m => File.Exists(Path.Combine(plugin, "bases", m.Key, "tape0.bin")))
            && File.Exists(Path.Combine(plugin, "camo", $"{camo}_raid.jpg")) && File.Exists(Path.Combine(plugin, "roll", $"{camo}.jpg"));
    }


    /// <summary>Gives every WWC mag built on a body the tape is cut for a tape slot that takes the registered Raid Tape rolls and nothing
    /// else. Safe to call again: a mag that already has the slot is left alone. Returns how many mags got one this time.</summary>
    public static int AddTapeSlots(TemplateTable templates, WrapData d)
    {
        var tapes = d.Tape.Items.Select(t => Ids.Tape(t.Key)).Where(templates.Items.ContainsKey).ToList();
        if (tapes.Count == 0 || d.Mags.Count == 0) return 0;
        var slotted = 0;
        foreach (var (magId, item) in templates.Items)
        {
            var props = item.Properties;
            if (props is null || item.Name is null || !item.Name.StartsWith(Ids.WwcMagPrefix, StringComparison.Ordinal)) continue;
            var body = d.Mags.FirstOrDefault(m => string.Equals(m.Prefab, props.Prefab?.Path, StringComparison.OrdinalIgnoreCase));
            if (body is null) continue;
            var slots = (props.Slots ?? []).ToList();
            if (slots.Any(s => s.Name == body.Slot)) continue;
            slots.Add(new Slot
            {
                Name = body.Slot, Id = Ids.TapeSlot(magId), Parent = magId, Prototype = Ids.SlotProto, Required = false, MergeSlotWithChildren = false,
                Properties = new SlotProperties { Filters = [new SlotFilter { Shift = 0, Filter = new HashSet<MongoId>(tapes) }] },
            });
            props.Slots = [.. slots];
            slotted++;
        }
        return slotted;
    }

    public static IEnumerable<(BaseEntry Base, CamoEntry Camo, MongoId Id)> All(WrapData d)
    {
        foreach (var b in d.Bases)
            foreach (var c in d.Camos)
                yield return (b, c, Ids.Wrap(b.Key, c.Key));
    }
}

/// <summary>Stage 1: templates, every part in every camo. Always registered, so a profile holding a wrap still loads when the client is gone.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 36)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, ItemConfig itemConfig,
    RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, TraderConfig traderConfig, AirdropConfig airdropConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        var data = Shared.Data(dir, logger);
        var strings = new Dictionary<string, string>();
        var added = new List<MongoId>();
        foreach (var (b, c, id) in Shared.All(data))
        {
            if (!templates.Items.TryGetValue(new MongoId(b.Donor), out var donor)) { logger.Error($"[WWCWraps] stock item {b.Donor} not found; {b.Key} wraps not registered"); break; }
            var word = string.IsNullOrWhiteSpace(b.WrapWord) ? "wrap" : b.WrapWord.Trim();
            var name = $"{b.Thing} (WWC {c.Label} {word})";
            var shortName = $"{b.Short} {c.Short}".Trim();
            var print = c.Key.StartsWith("raid_", StringComparison.Ordinal) ? c.Label.ToLowerInvariant() : c.Label.ToLowerInvariant() + " camo";
            var description = $"WWC (Wet Works Company) finished this {b.Thing} with {print} {word} for The Jackal's customers. "
                + "It handles exactly like the stock one; guns and parts that would cut into the tape won't take it."
                + (string.IsNullOrWhiteSpace(b.Note) ? "" : " " + b.Note.Trim());
            var handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == b.Donor);
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = b.Donor,
                ParentId = donor.Parent,
                NewItemName = $"wwcwrap_{b.Key}_{c.Key}",
                OverrideProperties = new TemplateItemProperties
                {
                    ExaminedByDefault = true,
                    CanSellOnRagfair = cfg.Flea.Sell,
                    CanRequireOnRagfair = false,
                },
                HandbookParentId = handbook?.ParentId,
                HandbookPriceRoubles = handbook?.Price ?? 3000,
                FleaPriceRoubles = cfg.PriceOf(b.Key),
                AddToHandbook = handbook is not null,
                AddToFleaPriceDb = true,
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = name, ShortName = shortName, Description = description } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[WWCWraps] {name}: {string.Join("; ", result.Errors)}"); continue; }
            strings[$"{id} Name"] = name;
            strings[$"{id} ShortName"] = shortName;
            strings[$"{id} Description"] = description;
            added.Add(id);
        }
        var wrapped = added.Count;
        // Raid Tape rolls: clones of a slotless weapon part, so the game treats tape on a mag like any other mod, shown with the game's own
        // tape roll model (the client repaints it). Named the way their maker names them (wraps.json carries the text).
        var tapes = new List<MongoId>();
        if (data.Tape.Items.Count > 0 && templates.Items.TryGetValue(new MongoId(data.Tape.Donor), out var ribbon))
        {
            var hb = templates.Handbook.Items.FirstOrDefault(h => h.Id == data.Tape.Donor);
            foreach (var t in data.Tape.Items)
            {
                var id = Ids.Tape(t.Key);
                var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
                {
                    NewId = id,
                    ItemTplToClone = data.Tape.Donor,
                    ParentId = ribbon.Parent,
                    NewItemName = $"wwcwrap_tape_{t.Key}",
                    OverrideProperties = new TemplateItemProperties
                    {
                        Prefab = new Prefab { Path = data.Tape.Prefab, Rcid = "" },
                        Ergonomics = cfg.Items.TapeErgonomics,
                        HeatFactor = 1,
                        CoolFactor = 1,
                        Weight = 0.03,
                        BackgroundColor = "default",
                        ExaminedByDefault = true,
                        CanSellOnRagfair = cfg.Flea.Sell,
                        CanRequireOnRagfair = false,
                    },
                    HandbookParentId = hb?.ParentId,
                    HandbookPriceRoubles = cfg.PriceOf("tape"),
                    FleaPriceRoubles = cfg.PriceOf("tape"),
                    AddToHandbook = hb is not null,
                    AddToFleaPriceDb = true,
                    AddToWeaponShelf = false,
                    Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = t.Name, ShortName = t.Short, Description = RollWords.For(t.Description) } },
                }, Assembly.GetExecutingAssembly());
                if (!result.Success) { logger.Error($"[WWCWraps] {t.Name}: {string.Join("; ", result.Errors)}"); continue; }
                strings[$"{id} Name"] = t.Name;
                strings[$"{id} ShortName"] = t.Short;
                strings[$"{id} Description"] = RollWords.For(t.Description);
                tapes.Add(id);
                added.Add(id);
            }
        }
        else if (data.Tape.Items.Count > 0) logger.Error($"[WWCWraps] stock item {data.Tape.Donor} not found; no tape rolls registered");
        // 0.4.0 Wrappable parts: new copies of the stock parts (the game's own are never touched), sold plain, each with a wrap slot that takes
        // a Raid Tape roll (Widowz 2026-11-18: "Raid tape"). The client draws the part wrapped in the colour of the roll in that slot.
        var wrappables = 0;
        foreach (var b in data.Bases)
        {
            if (!Ids.WrapSlotName.TryGetValue(b.Key, out var slotName)) continue;
            if (!templates.Items.TryGetValue(new MongoId(b.Donor), out var donor)) { logger.Error($"[WWCWraps] stock item {b.Donor} not found; no Wrappable {b.Key}"); continue; }
            var id = Ids.Wrappable(b.Key);
            var name = $"{b.Thing} (Wrappable)";
            var shortName = $"{b.Short} WRP".Trim();
            var description = $"A WWC (Wet Works Company) {b.Thing} made to take a wrap: it has a wrap slot. Put a roll of Raid Tape in it "
                + "and the part is wrapped in that tape; take the roll out and it is plain again. It handles like the stock one (the tape adds its own "
                + "ergonomics); guns and parts that would cut into a wrap won't take it." + (string.IsNullOrWhiteSpace(b.Note) ? "" : " " + b.Note.Trim());
            var handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == b.Donor);
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = b.Donor,
                ParentId = donor.Parent,
                NewItemName = $"wwcwrap_wrappable_{b.Key}",
                OverrideProperties = new TemplateItemProperties { ExaminedByDefault = true, CanSellOnRagfair = cfg.Flea.Sell, CanRequireOnRagfair = false },
                HandbookParentId = handbook?.ParentId,
                HandbookPriceRoubles = handbook?.Price ?? 3000,
                FleaPriceRoubles = cfg.PriceOf(b.Key),
                AddToHandbook = handbook is not null,
                AddToFleaPriceDb = true,
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = name, ShortName = shortName, Description = description } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[WWCWraps] {name}: {string.Join("; ", result.Errors)}"); continue; }
            var props = templates.Items[id].Properties;
            if (props is not null && tapes.Count > 0)
            {
                var slots = (props.Slots ?? []).ToList();
                if (!slots.Any(s => s.Name == slotName))
                    slots.Add(new Slot
                    {
                        Name = slotName, Id = Ids.WrapSlot(b.Key), Parent = id, Prototype = Ids.SlotProto, Required = false, MergeSlotWithChildren = false,
                        Properties = new SlotProperties { Filters = [new SlotFilter { Shift = 0, Filter = new HashSet<MongoId>(tapes) }] },
                    });
                props.Slots = [.. slots];
            }
            strings[$"{id} Name"] = name; strings[$"{id} ShortName"] = shortName; strings[$"{id} Description"] = description;
            strings[slotName] = "Wrap"; strings[slotName.ToUpperInvariant()] = "WRAP";
            added.Add(id);
            wrappables++;
        }
        // The tape slot. Every WWC mag built on a body the tape is cut for gets one, found by the name WWC Mags gives its templates and read
        // fresh at every start, so a mag made later with the WWC Mags kit takes tape with nothing else to change. Stock mags get nothing:
        // the tape only works with WWC mags (Widowz, 2026-11-10). The slot is named after the body's near-model node, which is where the
        // game hangs whatever sits in a slot.
        var slotted = Shared.AddTapeSlots(templates, data);
        if (tapes.Count > 0)
            foreach (var m in data.Mags) { strings[m.Slot] = "Tape"; strings[m.Slot.ToUpperInvariant()] = "TAPE"; }
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
        logger.Success($"[WWCWraps] {wrapped} wrapped item(s) registered ({data.Bases.Count} parts x {data.Camos.Count} looks), {tapes.Count} Raid Tape roll(s), tape slot on {slotted} WWC mag(s)");
        logger.Success($"[WWCWraps] {wrappables} Wrappable part(s) with a wrap slot for the {tapes.Count} Raid Tape roll(s)");
        return Task.CompletedTask;
    }
}

internal static class RollWords
{
    /// <summary>0.4.0: a roll of Raid Tape also wraps The Jackal's Wrappable handguards and grip, so its description says so.</summary>
    public static string For(string text)
    {
        const string magsOnly = "so it fits WWC mags only.";
        const string both = "so it fits WWC mags and the Wrappable handguards and grip from The Jackal, and nothing else.";
        return text.Contains(magsOnly) ? text.Replace(magsOnly, both)
            : text + " It also wraps the Wrappable handguards and grip from The Jackal: put the roll in their wrap slot.";
    }
}

/// <summary>Stage 2, after every mod has registered its items at Preload: each wrap fits wherever its stock item fits (guns, pouches, rigs,
/// cases), except the hosts whose own model would cut into the tape; parts that would cut into it come out of the wrap's own slots; and
/// barrels or gas blocks that would cut into it become conflicts, both ways. Only adds ids to lists that already name the stock item.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 12)]
public class Fit(ISptLogger<Fit> logger, ModHelper modHelper, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        var data = Shared.Data(dir, logger);
        var byDonor = new Dictionary<MongoId, (BaseEntry Base, List<MongoId> Ids)>();
        foreach (var (b, _, id) in Shared.All(data).Concat(data.Bases.Select(b => (b, new CamoEntry(), Ids.Wrappable(b.Key)))))
        {
            if (!templates.Items.ContainsKey(id)) continue;
            var donor = new MongoId(b.Donor);
            if (!byDonor.TryGetValue(donor, out var e)) byDonor[donor] = e = (b, []);
            e.Ids.Add(id);
        }
        // a second look for WWC mags: any registered after this mod's own Preload stage (a later WWC Mags build, another mag pack on the
        // same naming) still gets its tape slot
        var late = Shared.AddTapeSlots(templates, data);
        if (late > 0) logger.Info($"[WWCWraps] tape slot added to {late} more WWC mag(s) registered after the first pass");
        if (byDonor.Count == 0) return Task.CompletedTask;
        int slots = 0, grids = 0, conflicts = 0, blockedHosts = 0, removed = 0, crossConflicts = 0;
        if (cfg.Items.FitWhereStockFits)
        {
            void Widen(HashSet<MongoId>? set, ref int counter, string? host = null)
            {
                if (set is null) return;
                foreach (var (donor, (b, mine)) in byDonor)
                {
                    if (!set.Contains(donor)) continue;
                    if (host is not null && b.BlockedHosts.Contains(host)) { blockedHosts++; continue; }
                    foreach (var id in mine) if (set.Add(id)) counter++;
                }
            }
            foreach (var (tpl, item) in templates.Items)
            {
                var props = item.Properties;
                if (props is null) continue;
                var host = tpl.ToString();
                foreach (var slot in props.Slots ?? []) foreach (var f in slot.Properties?.Filters ?? []) Widen(f.Filter, ref slots, host);
                foreach (var chamber in props.Chambers ?? []) foreach (var f in chamber.Properties?.Filters ?? []) Widen(f.Filter, ref slots, host);
                foreach (var grid in props.Grids ?? [])
                    foreach (var f in grid.Properties?.Filters ?? [])
                    {
                        Widen(f.Filter, ref grids);
                        Widen(f.ExcludedFilter, ref grids);
                    }
                Widen(props.ConflictingItems, ref conflicts);
            }
        }
        foreach (var (_, (b, mine)) in byDonor)
        {
            foreach (var id in mine)
            {
                var props = templates.Items[id].Properties;
                if (props is null) continue;
                // the wrap's own slots: parts that would cut into the tape come out (the clone has its own copy of the slot lists)
                foreach (var slot in props.Slots ?? [])
                {
                    if (slot.Name is null || !b.BlockedChildren.TryGetValue(slot.Name, out var bad) || bad.Count == 0) continue;
                    foreach (var f in slot.Properties?.Filters ?? [])
                    {
                        if (f.Filter is null) continue;
                        foreach (var t in bad) if (f.Filter.Remove(new MongoId(t))) removed++;
                    }
                }
                if (b.Conflicts.Count == 0) continue;
                props.ConflictingItems ??= [];
                foreach (var t in b.Conflicts)
                {
                    var other = new MongoId(t);
                    if (!templates.Items.TryGetValue(other, out var o) || o.Properties is null) continue;
                    if (props.ConflictingItems.Add(other)) crossConflicts++;
                    o.Properties.ConflictingItems ??= [];
                    o.Properties.ConflictingItems.Add(id);
                }
            }
        }
        logger.Info($"[WWCWraps] wraps fit {slots} slots, {grids} container lists and {conflicts} conflict lists that name their stock item; "
            + $"{blockedHosts} host slot(s) left out, {removed} part(s) taken out of wrap slots, {crossConflicts} conflict(s) added where a part would cut into a wrap");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 3: The Jackal's offers, after he registers. Skips any wrap whose client files aren't on disk.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 136)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = PartFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        var data = Shared.Data(dir, logger);
        if (!cfg.ModEnabled || !cfg.Jackal.Sell) { logger.Info("[WWCWraps] selling is off in config.json, no offers"); return Task.CompletedTask; }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Assort is null) { logger.Warning("[WWCWraps] The Jackal not found; no offers"); return Task.CompletedTask; }
        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
        int offers = 0, skipped = 0, rolls = 0, offShelf = 0;
        foreach (var (b, c, tpl) in Shared.All(data))
        {
            if (!templates.Items.ContainsKey(tpl)) continue;
            if (!cfg.Sells(b.Key, c.Key)) { offShelf++; continue; }
            if (!Shared.ClientHas(dir, b, c.Key)) { skipped++; continue; }
            var root = Ids.Offer(b.Key, c.Key);
            if (assort.Items.Any(i => i.Id == root)) continue;
            assort.Items.Add(new Item
            {
                Id = root,
                Template = tpl,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = cfg.Jackal.Stock, BuyRestrictionMax = cfg.Jackal.BuyLimit, BuyRestrictionCurrent = 0 },
            });
            foreach (var (slot, child) in b.With)
            {
                if (!templates.Items.ContainsKey(new MongoId(child))) continue;
                assort.Items.Add(new Item { Id = Ids.Of($"offer|{b.Key}|{c.Key}|{slot}"), Template = new MongoId(child), ParentId = root, SlotId = slot });
            }
            assort.BarterScheme[root] = [[new BarterScheme { Count = cfg.PriceOf(b.Key), Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[root] = cfg.Jackal.LoyaltyLevel;
            offers++;
        }
        foreach (var t in data.Tape.Items)
        {
            var tpl = Ids.Tape(t.Key);
            if (!cfg.Sells("tape", t.Key) || !templates.Items.ContainsKey(tpl)) continue;
            if (!Shared.ClientHasTape(dir, data, t.Key)) { skipped++; continue; }
            var root = Ids.TapeOffer(t.Key);
            if (assort.Items.Any(i => i.Id == root)) continue;
            assort.Items.Add(new Item
            {
                Id = root,
                Template = tpl,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = cfg.Jackal.Stock, BuyRestrictionMax = cfg.Jackal.BuyLimit, BuyRestrictionCurrent = 0 },
            });
            assort.BarterScheme[root] = [[new BarterScheme { Count = cfg.PriceOf("tape"), Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[root] = cfg.Jackal.LoyaltyLevel;
            offers++;
            rolls++;
        }
        // 0.4.0: the Wrappable parts, plain, with a wrap slot for a Raid Tape roll (the rolls are sold above)
        int wrappableOffers = 0;
        foreach (var b in data.Bases)
        {
            var tpl = Ids.Wrappable(b.Key);
            if (!cfg.SellsPart(b.Key) || !templates.Items.ContainsKey(tpl)) continue;
            if (!Shared.ClientHas(dir, b, "raid_black")) { skipped++; continue; }
            var root = Ids.WrappableOffer(b.Key);
            if (assort.Items.Any(i => i.Id == root)) continue;
            assort.Items.Add(new Item
            {
                Id = root, Template = tpl, ParentId = "hideout", SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = cfg.Jackal.Stock, BuyRestrictionMax = cfg.Jackal.BuyLimit, BuyRestrictionCurrent = 0 },
            });
            foreach (var (slot, child) in b.With)
            {
                if (!templates.Items.ContainsKey(new MongoId(child))) continue;
                assort.Items.Add(new Item { Id = Ids.Of($"offer|wrappable|{b.Key}|{slot}"), Template = new MongoId(child), ParentId = root, SlotId = slot });
            }
            assort.BarterScheme[root] = [[new BarterScheme { Count = cfg.PriceOf(b.Key), Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[root] = cfg.Jackal.LoyaltyLevel;
            wrappableOffers++;
        }
        logger.Success($"[WWCWraps] The Jackal sells {wrappableOffers} Wrappable part(s) (wrap them with Raid Tape)");
        if (skipped > 0) logger.Warning($"[WWCWraps] {skipped} wrap(s) or tape roll(s) have no client files on disk; not sold");
        logger.Success($"[WWCWraps] The Jackal now sells {offers - rolls} WWC wrap(s) and {rolls} Raid Tape roll(s)"
            + (offShelf > 0 ? $"; {offShelf} pre-wrapped part(s) stay in the game but off his shelf (jackal.sellPrewrapped / camosByPart in config.json)" : ""));
        return Task.CompletedTask;
    }
}
