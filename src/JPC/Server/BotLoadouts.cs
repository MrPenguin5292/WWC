using System.Reflection;
using Path = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace JPC.Server;

/// <summary>
/// Like the ARC V2 (Widowz 2026-11-16 picked "PMC bots wearing them" there): usec and bear get the carrier in their tactical-vest pool at the stock Eagle MMAC's
/// weight (times config botWeightVsStock). Plates and soft armor come from the bot type's own MMAC pool (the carrier clones its slots);
/// the part slots take every front, chest piece, side item and radio, and only the USMC patch (kit patches stay a player thing). The part
/// slots get spawn chances from config botSlotChances where the bot type has none for that slot name yet (custom names, so nothing else
/// shares them). Runs only when the client content is installed: without it a bot would wear a plain Tasmanian Tiger look-alike.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 26)]
public class BotLoadouts(
    ISptLogger<BotLoadouts> logger,
    ModHelper modHelper,
    TemplateTable templateTable,
    BotTable botTable,
    BotConfig botConfig) : IOnLoad
{
    private const string Mmac = "61bc85697113f767765c7fe7";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var modDir = PartFolder.Of(modHelper);
            var cfg = File.Exists(Path.Combine(modDir, "config.json")) ? modHelper.GetJsonDataFromFile<ModConfig>(modDir, "config.json") ?? new ModConfig() : new ModConfig();
            if (!cfg.Enabled || !cfg.BotsWearCarrier) { logger.Info($"[{ModInfo.Name}] bots do not wear the carrier (config)"); return Task.CompletedTask; }
            if (cfg.RequireClientContent && ContentGate.Check(cfg, modHelper, logger) is { } problem)
            {
                logger.Warning($"[{ModInfo.Name}] bots do not wear the carrier: {problem}");
                return Task.CompletedTask;
            }
            var carrier = PatchSlot.Carrier;
            if (!templateTable.Items.TryGetValue(carrier, out var template))
            {
                logger.Warning($"[{ModInfo.Name}] bots: carrier template not registered");
                return Task.CompletedTask;
            }
            var usmcPatch = PatchSlot.Hash("jpc|patch_usmc");
            var partSlots = new Dictionary<string, HashSet<MongoId>>();
            foreach (var s in template.Properties?.Slots ?? [])
            {
                // the back patch slot (0.3.0) holds only the player's own patches: no bot pool for it, and its spawn chance is 0 below
                if (s.Name is null || !s.Name.StartsWith("mod_jpc_") || s.Name == PatchSlot.BackSlotName) continue;
                var filter = s.Properties?.Filters?.FirstOrDefault()?.Filter;
                if (filter is null) continue;
                var set = new HashSet<MongoId>(filter.Where(f => templateTable.Items.ContainsKey(f) && (s.Name != PatchSlot.PatchSlotName || f == usmcPatch)));
                if (set.Count > 0) partSlots[s.Name] = set;
            }

            int types = 0;
            foreach (var name in cfg.BotTypes)
            {
                if (!botTable.Types.TryGetValue(name, out var bt) || bt?.BotInventory is null) continue;
                var inv = bt.BotInventory;
                if (!inv.Equipment.TryGetValue(EquipmentSlots.TacticalVest, out var vests) || vests is null) continue;
                double avg = vests.Count > 0 ? vests.Values.Average() : 20;
                vests[carrier] = Math.Max(1, Math.Round((vests.TryGetValue(Mmac, out var wm) ? wm : avg) * cfg.BotWeightVsStock));
                var target = inv.Mods.TryGetValue(carrier, out var existing) ? existing : (inv.Mods[carrier] = new Dictionary<string, HashSet<MongoId>>());
                if (inv.Mods.TryGetValue(Mmac, out var mmacMods))
                    foreach (var (slot, set) in mmacMods) target[slot] = new HashSet<MongoId>(set);
                else
                    foreach (var s in template.Properties?.Slots ?? [])
                    {
                        var plate = s.Properties?.Filters?.FirstOrDefault()?.Plate;
                        if (s.Name is not null && !s.Name.StartsWith("mod_jpc_") && plate is not null) target[s.Name] = [plate.Value];
                    }
                foreach (var (slot, set) in partSlots) target[slot] = new HashSet<MongoId>(set);
                var chances = bt.BotChances?.EquipmentModsChances;
                int added = 0;
                if (chances is not null)
                    foreach (var (slot, chance) in cfg.BotSlotChances)
                        if (!chances.ContainsKey(slot)) { chances[slot] = chance; added++; }
                // From bot level 15 SPT fills armored-rig slots from the template's filters and an all-blacklisted slot is reverted ("resulted
                // in 0 mods"), so the back patch slot's chance is what keeps patches off bots (the AirLite way, checked at level 20 there).
                if (chances is not null && !cfg.BotSlotChances.ContainsKey(PatchSlot.BackSlotName)) chances[PatchSlot.BackSlotName] = 0;
                types++;
                logger.Info($"[{ModInfo.Name}] bots: {name} may wear the carrier (weight {vests[carrier]} of {vests.Count} vests; {partSlots.Count} part slots; {added} slot chances added)");
            }
            if (types == 0) logger.Warning($"[{ModInfo.Name}] bots: no bot type took the carrier");

            // From bot level 15 SPT draws a PMC's TacticalVest parts from the template's slot filters ("randomisedArmorSlots"), not from
            // the pool above, so bots would pick up the player's kit patches. Blacklist every patch but the USMC one for that slot.
            var patchSlot = template.Properties?.Slots?.FirstOrDefault(s => s.Name == PatchSlot.PatchSlotName);
            var others = patchSlot?.Properties?.Filters?.FirstOrDefault()?.Filter?.Where(f => f != usmcPatch).ToHashSet() ?? [];
            int ranges = 0;
            if (others.Count > 0 && botConfig.Equipment.TryGetValue("pmc", out var pmcEquipment) && pmcEquipment?.Blacklist is { } blacklist)
                foreach (var entry in blacklist)
                {
                    entry.Equipment ??= new();
                    if (!entry.Equipment.TryGetValue(PatchSlot.PatchSlotName, out var set) || set is null) entry.Equipment[PatchSlot.PatchSlotName] = set = new();
                    foreach (var p in others) set.Add(p);
                    ranges++;
                }
            logger.Info($"[{ModInfo.Name}] bots: {others.Count} player patches kept off bots' carriers ({ranges} PMC level range(s))");
        }
        catch (Exception ex)
        {
            logger.Warning($"[{ModInfo.Name}] bots do not wear the carrier: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
