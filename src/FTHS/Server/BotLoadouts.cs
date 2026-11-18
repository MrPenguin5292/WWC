using System.Reflection;
using Path = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace FTHS.Server;

/// <summary>
/// Widowz 2026-11-15 "Give it to bots as well": PMC bots (usec, bear) can wear both FTHS helmets. The ballistic one joins their
/// headwear pool at the stock FAST MT Tan's weight, the bump one at the Tac-Kek replica's (both times config botWeightVsStock).
/// Each helmet gets its own slot pool from its template filters, so bots dress it from the same parts the player can buy (covers,
/// strobe, battery, ComTac, MPLS, chinstrap) plus the stock gear its donor takes; the item conflicts keep the clipping blocks.
/// The new slots get spawn chances (config botSlotChances) only where the bot type has none for that slot name yet.
/// Runs after the items are registered and only when the client content is installed: without the client a bot would wear a
/// plain FAST look-alike.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 21)]
public class BotLoadouts(
    ISptLogger<BotLoadouts> logger,
    ModHelper modHelper,
    TemplateTable templateTable,
    BotTable botTable) : IOnLoad
{
    private const string Ballistic = "7f7da4478d04b6a8e11ac462", Bump = "c83abca542fccd88df995731";
    private const string FastTan = "5ac8d6885acfc400180ae7b0", Replica = "5ea05cf85ad9772e6624305d";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var modDir = PartFolder.Of(modHelper);
            var cfg = File.Exists(Path.Combine(modDir, "config.json")) ? modHelper.GetJsonDataFromFile<ModConfig>(modDir, "config.json") ?? new ModConfig() : new ModConfig();
            if (!cfg.Enabled || !cfg.BotsWearFths) { logger.Info($"[{ModInfo.Name}] bots do not wear the FTHS (config)"); return Task.CompletedTask; }
            if (cfg.RequireClientContent && ContentGate.Check(cfg, modHelper, logger) is { } problem)
            {
                logger.Warning($"[{ModInfo.Name}] bots do not wear the FTHS: {problem}");
                return Task.CompletedTask;
            }
            if (!templateTable.Items.ContainsKey(Ballistic) || !templateTable.Items.ContainsKey(Bump))
            {
                logger.Warning($"[{ModInfo.Name}] bots: FTHS templates not registered");
                return Task.CompletedTask;
            }

            // slot pools from each helmet's own template filters (only templates that exist)
            var pools = new Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>>();
            foreach (var id in new[] { Ballistic, Bump })
            {
                var slots = new Dictionary<string, HashSet<MongoId>>();
                foreach (var s in templateTable.Items[id].Properties?.Slots ?? [])
                {
                    var filter = s.Properties?.Filters?.FirstOrDefault()?.Filter;
                    if (s.Name is null || filter is null || filter.Count == 0) continue;
                    var set = new HashSet<MongoId>(filter.Where(f => templateTable.Items.ContainsKey(f)));
                    if (set.Count > 0) slots[s.Name] = set;
                }
                pools[id] = slots;
            }

            int types = 0;
            foreach (var name in cfg.BotTypes)
            {
                if (!botTable.Types.TryGetValue(name, out var bt) || bt?.BotInventory is null) continue;
                var inv = bt.BotInventory;
                if (!inv.Equipment.TryGetValue(EquipmentSlots.Headwear, out var headwear) || headwear is null) continue;
                double avg = headwear.Count > 0 ? headwear.Values.Average() : 10;
                headwear[Ballistic] = Math.Max(1, Math.Round((headwear.TryGetValue(FastTan, out var wf) ? wf : avg) * cfg.BotWeightVsStock));
                headwear[Bump] = Math.Max(1, Math.Round((headwear.TryGetValue(Replica, out var wr) ? wr : avg * 0.25) * cfg.BotWeightVsStock));
                foreach (var (id, slots) in pools)
                {
                    var target = inv.Mods.TryGetValue(id, out var existing) ? existing : (inv.Mods[id] = new Dictionary<string, HashSet<MongoId>>());
                    foreach (var (slot, set) in slots) target[slot] = new HashSet<MongoId>(set);
                }
                var chances = bt.BotChances?.EquipmentModsChances;
                int added = 0;
                if (chances is not null)
                    foreach (var (slot, chance) in cfg.BotSlotChances)
                        if (!chances.ContainsKey(slot)) { chances[slot] = chance; added++; }
                types++;
                logger.Info($"[{ModInfo.Name}] bots: {name} may wear the FTHS (ballistic weight {headwear[Ballistic]}, bump {headwear[Bump]} of {headwear.Count} helmets; {added} slot chances added)");
            }
            if (types == 0) logger.Warning($"[{ModInfo.Name}] bots: no bot type took the FTHS");
        }
        catch (Exception ex)
        {
            logger.Warning($"[{ModInfo.Name}] bots do not wear the FTHS: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
