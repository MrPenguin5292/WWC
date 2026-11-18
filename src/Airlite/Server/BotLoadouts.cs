using System.Reflection;
using Path = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Airlite.Server;

/// <summary>
/// PMC bots wear the rig like the USMC PC Gen III (Widowz picked "PMC bots wearing them" for that one; the AirLite was asked for "like
/// the other vests"): usec and bear get it in their tactical-vest pool at the stock Haley Strategic D3CRX's weight (the chest rig it
/// clones; times config botWeightVsStock), so they wear it over their body armor like any chest rig; the part slots take every front,
/// side, pocket item, radio and yoke. The patch slot holds only the player's own patches, so bots get none. The part slots get spawn chances
/// from config botSlotChances where the bot type has none for that slot name yet (custom names, so nothing else shares them). Runs only
/// when the client content is installed: without it a bot would wear a plain Tasmanian Tiger look-alike.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 25)]
public class BotLoadouts(
    ISptLogger<BotLoadouts> logger,
    ModHelper modHelper,
    TemplateTable templateTable,
    BotTable botTable) : IOnLoad
{
    private const string Donor = "5d5d85c586f774279a21cbdb";   // Haley Strategic D3CRX

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var modDir = PartFolder.Of(modHelper);
            var cfg = File.Exists(Path.Combine(modDir, "config.json")) ? modHelper.GetJsonDataFromFile<ModConfig>(modDir, "config.json") ?? new ModConfig() : new ModConfig();
            if (!cfg.Enabled || !cfg.BotsWearCarrier) { logger.Info($"[{ModInfo.Name}] bots do not wear the rig (config)"); return Task.CompletedTask; }
            if (cfg.RequireClientContent && ContentGate.Check(cfg, modHelper, logger) is { } problem)
            {
                logger.Warning($"[{ModInfo.Name}] bots do not wear the rig: {problem}");
                return Task.CompletedTask;
            }
            var carrier = PatchSlot.Carrier;
            if (!templateTable.Items.TryGetValue(carrier, out var template))
            {
                logger.Warning($"[{ModInfo.Name}] bots: rig template not registered");
                return Task.CompletedTask;
            }
            var partSlots = new Dictionary<string, HashSet<MongoId>>();
            foreach (var s in template.Properties?.Slots ?? [])
            {
                if (s.Name is null || !s.Name.StartsWith(PatchSlot.SlotPrefix) || s.Name == PatchSlot.PatchSlotName) continue;
                var filter = s.Properties?.Filters?.FirstOrDefault()?.Filter;
                if (filter is null) continue;
                var set = new HashSet<MongoId>(filter.Where(f => templateTable.Items.ContainsKey(f)));
                if (set.Count > 0) partSlots[s.Name] = set;
            }

            int types = 0;
            foreach (var name in cfg.BotTypes)
            {
                if (!botTable.Types.TryGetValue(name, out var bt) || bt?.BotInventory is null) continue;
                var inv = bt.BotInventory;
                if (!inv.Equipment.TryGetValue(EquipmentSlots.TacticalVest, out var vests) || vests is null) continue;
                double avg = vests.Count > 0 ? vests.Values.Average() : 20;
                vests[carrier] = Math.Max(1, Math.Round((vests.TryGetValue(Donor, out var wd) ? wd : avg) * cfg.BotWeightVsStock));
                var target = inv.Mods.TryGetValue(carrier, out var existing) ? existing : (inv.Mods[carrier] = new Dictionary<string, HashSet<MongoId>>());
                foreach (var (slot, set) in partSlots) target[slot] = new HashSet<MongoId>(set);
                var chances = bt.BotChances?.EquipmentModsChances;
                int added = 0;
                if (chances is not null)
                {
                    foreach (var (slot, chance) in cfg.BotSlotChances)
                        if (!chances.ContainsKey(slot)) { chances[slot] = chance; added++; }
                    // The patch slot holds only the player's own patches. From bot level 15 SPT fills a PMC's TacticalVest slots from the
                    // template's slot filters ("randomisedArmorSlots", USMC PC Gen III finding 2026-11-16), and a blacklist that empties the
                    // slot is undone ("resulted in 0 mods. Reverting to original set"), so its spawn chance is what keeps patches off bots.
                    if (!cfg.BotSlotChances.ContainsKey(PatchSlot.PatchSlotName)) chances[PatchSlot.PatchSlotName] = 0;
                }
                types++;
                logger.Info($"[{ModInfo.Name}] bots: {name} may wear the rig (weight {vests[carrier]} of {vests.Count} vests; {partSlots.Count} part slots; {added} slot chances added)");
            }
            if (types == 0) logger.Warning($"[{ModInfo.Name}] bots: no bot type took the rig");
            if (types > 0)
            {
                // Bots that roll body armor only get rigs without slots from SPT; the AirLite's slots are cosmetic, so keep it in that pool.
                RigOverArmorPatch.Rig = carrier;
                try
                {
                    var patch = new RigOverArmorPatch();
                    patch.Enable();
                    logger.Info(patch.IsActive
                        ? $"[{ModInfo.Name}] bots: the rig stays in the pool of bots that wear body armor"
                        : $"[{ModInfo.Name}] bots: rig-over-armor patch not active; bots that wear body armor will not get the rig");
                }
                catch (Exception ex) { logger.Warning($"[{ModInfo.Name}] bots: rig-over-armor patch failed ({ex.Message}); bots that wear body armor will not get the rig"); }
            }

            var patchSlot = template.Properties?.Slots?.FirstOrDefault(s => s.Name == PatchSlot.PatchSlotName);
            var patches = patchSlot?.Properties?.Filters?.FirstOrDefault()?.Filter?.Count ?? 0;
            logger.Info(cfg.BotSlotChances.ContainsKey(PatchSlot.PatchSlotName)
                ? $"[{ModInfo.Name}] bots: patch slot chance set in config.json ({cfg.BotSlotChances[PatchSlot.PatchSlotName]}%), bots may wear the {patches} patches"
                : $"[{ModInfo.Name}] bots: patch slot chance 0, the {patches} player patches stay off bots' rigs");
        }
        catch (Exception ex)
        {
            logger.Warning($"[{ModInfo.Name}] bots do not wear the rig: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}

