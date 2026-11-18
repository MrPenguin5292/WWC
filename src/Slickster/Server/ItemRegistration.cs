using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace Slickster.Server;

/// <summary>
/// Stage 1: templates, presets, locales, secure-container filters, mastery. Runs at Preload + 20 so every
/// item exists before profiles load (CustomItemService throws otherwise since 4.1.3). This stage never
/// depends on client files: a profile that already holds the items must load even when the plugin is gone.
///
/// Reads, relative to the DLL:
///   db/CustomItems/*.json       WTT-CommonLib CustomItems schema (tools/sptkit.py clone / preset)
///   db/CustomLocales/*.json     flat "<id> Name|ShortName|Description" (tools/sptkit.py locales)
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 20)]
public class ItemRegistration(
    ISptLogger<ItemRegistration> logger,
    ModHelper modHelper,
    CustomItemService customItemService,
    TemplateTable templateTable,
    GlobalTable globalTable,
    LocaleTable localeTable,
    TraderConfig traderConfig) : IOnLoad
{
    public static readonly List<MongoId> RegisteredIds = [];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modDir = ModFolder.Of(modHelper);
        var itemsDir = Path.Combine(modDir, "db", "CustomItems");
        var localesDir = Path.Combine(modDir, "db", "CustomLocales");

        var entries = new Dictionary<string, CustomItemEntry>(StringComparer.Ordinal);
        foreach (var file in ListJson(itemsDir))
        {
            var part = modHelper.GetJsonDataFromFile<Dictionary<string, CustomItemEntry>>(itemsDir, file);
            foreach (var (id, entry) in part ?? new Dictionary<string, CustomItemEntry>())
            {
                if (!entries.TryAdd(id, entry))
                {
                    logger.Error($"[{ModInfo.Name}] {id} is defined twice (second time in {file}); aborting item load");
                    return Task.CompletedTask;
                }
            }
        }

        if (entries.Count == 0)
        {
            logger.Warning($"[{ModInfo.Name}] no item files under {itemsDir}; nothing registered");
            return Task.CompletedTask;
        }

        // Rule: abort the whole load if any new id already exists or is malformed. Partial registration
        // leaves a profile holding ids that will not exist on the next start.
        foreach (var (id, entry) in entries)
        {
            if (!MongoId.IsValidMongoId(id))
            {
                logger.Error($"[{ModInfo.Name}] {id} is not a 24-hex MongoId; aborting item load");
                return Task.CompletedTask;
            }
            if (templateTable.Items.ContainsKey(id))
            {
                logger.Error($"[{ModInfo.Name}] {id} already exists in templates ({templateTable.Items[id].Name}); aborting item load");
                return Task.CompletedTask;
            }
            if (!templateTable.Items.ContainsKey(entry.ItemTplToClone))
            {
                logger.Error($"[{ModInfo.Name}] {id}: donor {entry.ItemTplToClone} is not in the database; aborting item load");
                return Task.CompletedTask;
            }
        }

        foreach (var (id, entry) in entries)
        {
            var en = entry.Locales.TryGetValue("en", out var loc) ? loc : new LocaleDetails();
            var details = new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = entry.ItemTplToClone,
                ParentId = entry.ParentId,
                NewItemName = entry.NewName ?? en.ShortName ?? en.Name ?? id,
                OverrideProperties = entry.OverrideProperties,
                HandbookParentId = entry.HandbookParentId,
                HandbookPriceRoubles = entry.HandbookPriceRoubles,
                FleaPriceRoubles = entry.FleaPriceRoubles,
                AddToHandbook = entry.RegisterInHandbook ?? entry.HandbookParentId is not null,
                AddToFleaPriceDb = (entry.RegisterInFleaPrices ?? true) && entry.FleaPriceRoubles is > 0,
                AddToWeaponShelf = true,
                Locales = entry.Locales,
            };

            var result = customItemService.CreateItemFromClone(details, Assembly.GetExecutingAssembly());
            if (!result.Success)
            {
                logger.Error($"[{ModInfo.Name}] {id} not created: {string.Join("; ", result.Errors)}");
                continue;
            }
            RegisteredIds.Add(id);

            if (entry.AddToSecureFilters ?? true)
            {
                AddToSecureContainers(id);
            }

            foreach (var preset in entry.WeaponPresets ?? [])
            {
                AddPreset(id, preset);
            }

            if (entry.Masteries == true)
            {
                foreach (var section in entry.MasterySections ?? [])
                {
                    AddMastery(section, id);
                }
            }

            if (entry.AddToBots == true)
            {
                // Deliberately not automatic: bot loadouts need weights per bot type and slot.
                // Use customItemService.AddCustomWeaponToPMCs(id, weight, slot) in a mod-specific class if wanted.
                logger.Info($"[{ModInfo.Name}] {id}: addtoBots is set but the template does not add bot loadouts");
            }
        }

        AddReciprocalConflicts();
        AddLocales(localesDir);

        // audit 2026-11-18 (WWC 2.1): Fence filled his random stock from every template, so he sold the Slickster's own patches.
        // Collectibles stay off Fence; keep the carrier and its patches off his shelves like the other WWC lines do.
        traderConfig.Fence.Blacklist.UnionWith(RegisteredIds);
        logger.Info($"[{ModInfo.Name}] {RegisteredIds.Count} templates kept off Fence");

        logger.Success($"[{ModInfo.Name}] registered {RegisteredIds.Count}/{entries.Count} templates");
        return Task.CompletedTask;
    }

    /// <summary>Native items list the donor by id in their own conflict lists, and a clone inherits none of that. Each
    /// clone names its conflicts in its JSON; this adds the clone to the other side so the block works in either fitting order.</summary>
    private void AddReciprocalConflicts()
    {
        var added = 0;
        foreach (var id in RegisteredIds)
        {
            var conflicts = templateTable.Items[id].Properties?.ConflictingItems;
            if (conflicts is null) continue;
            foreach (var other in conflicts.ToList())
            {
                if (!templateTable.Items.TryGetValue(other, out var template) || template.Properties is null) continue;
                template.Properties.ConflictingItems ??= [];
                if (template.Properties.ConflictingItems.Add(id)) added++;
            }
        }
        logger.Info($"[{ModInfo.Name}] {added} reciprocal conflict entries added");
    }

    private void AddPreset(MongoId weaponTpl, Preset preset)
    {
        if (string.IsNullOrEmpty(preset.Type)) preset.Type = "Preset";
        preset.Encyclopedia ??= weaponTpl;
        if (preset.Items is null || preset.Items.Count == 0)
        {
            logger.Warning($"[{ModInfo.Name}] preset {preset.Id} for {weaponTpl} has no items; skipped");
            return;
        }
        if (preset.Parent == default(MongoId)) preset.Parent = preset.Items[0].Id;
        if (!globalTable.ItemPresets.TryAdd(preset.Id, preset))
        {
            logger.Warning($"[{ModInfo.Name}] preset id {preset.Id} already exists; skipped");
        }
    }

    private void AddMastery(MasterySection section, MongoId weaponTpl)
    {
        List<string> templates = section.Templates.Count > 0 ? section.Templates : new List<string> { weaponTpl };
        var existing = globalTable.Configuration.Mastering.FirstOrDefault(m => m.Name == section.Name);
        if (existing is not null)
        {
            var set = new List<MongoId>(existing.Templates ?? []);
            foreach (var t in templates)
            {
                if (!set.Contains(t)) set.Add(t);
            }
            existing.Templates = set;
            return;
        }
        globalTable.Configuration.Mastering =
        [
            .. globalTable.Configuration.Mastering,
            new Mastering
            {
                Name = section.Name,
                Level2 = section.Level2,
                Level3 = section.Level3,
                Templates = templates.Select(t => (MongoId)t).ToList(),
            },
        ];
    }

    /// <summary>Let the item into every secure container (same effect as WTT's addtoSecureFilters).</summary>
    private void AddToSecureContainers(MongoId id)
    {
        foreach (var (tpl, item) in templateTable.Items)
        {
            if (item.Parent != BaseClasses.MOB_CONTAINER || item.Properties?.Grids is null) continue;
            foreach (var grid in item.Properties.Grids)
            {
                foreach (var filter in grid.Properties?.Filters ?? [])
                {
                    // Only grids with an explicit allow-list need the id; an empty Filter means "anything".
                    if (filter.Filter is { Count: > 0 }) filter.Filter.Add(id);
                    filter.ExcludedFilter?.Remove(id);
                }
            }
        }
    }

    /// <summary>Flat locale files: add every key to every language that does not have it (en strings as fallback).</summary>
    private void AddLocales(string localesDir)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in ListJson(localesDir))
        {
            foreach (var (k, v) in modHelper.GetJsonDataFromFile<Dictionary<string, string>>(localesDir, file) ?? new Dictionary<string, string>())
            {
                strings[k] = v;
            }
        }
        if (strings.Count == 0) return;

        foreach (var (lang, lazy) in localeTable.Global)
        {
            var overwrite = lang == "en";
            lazy.AddTransformer(dict =>
            {
                if (dict is null) return dict;
                foreach (var (k, v) in strings)
                {
                    if (overwrite) dict[k] = v;
                    else dict.TryAdd(k, v);
                }
                return dict;
            });
        }
        logger.Info($"[{ModInfo.Name}] {strings.Count} locale strings added to {localeTable.Global.Count} languages");
    }

    private static IEnumerable<string> ListJson(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.json").Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal);
    }
}

internal static class ModInfo
{
#if WWC
    public static readonly string Name = "Slickster Plate Carriers";   // the standalone ModMetadata name, so log lines read the same
#else
    public static readonly string Name = new ModMetadata().Name;
#endif
}

