using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace AvonMask.Server;

/// <summary>
/// Optional (config pmcBotsWearMask, off by default): USEC and BEAR bots may wear the mask, at the stock GP-7's weight in their face
/// cover pool times pmcBotWeightVsGp7. The item's conflicts stop bots pairing it with a helmet or hat it clips. Face covers are not part of
/// SPT's level-15 armour-slot randomisation, so the pool is what decides. Skipped without the client content (bots would wear a GP-7 look).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 26)]
public class BotLoadouts(ISptLogger<BotLoadouts> logger, ModHelper modHelper, BotTable botTable) : IOnLoad
{
    private const string Mask = "b1f00d509254419311745076", Gp7 = "60363c0c92ec1c31037959f5";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cfg = Config.Load(modHelper, logger);
            if (cfg is null || !cfg.PmcBotsWearMask) { logger.Info($"[{ModInfo.Name}] PMC bots do not wear the mask (config)"); return Task.CompletedTask; }
            if (!ItemRegistration.RegisteredIds.Contains(new MongoId(Mask))) return Task.CompletedTask;
            if (cfg.RequireClientContent && ContentGate.Check(cfg, modHelper, logger) is { } problem)
            {
                logger.Warning($"[{ModInfo.Name}] PMC bots do not wear the mask: {problem}");
                return Task.CompletedTask;
            }
            foreach (var name in new[] { "usec", "bear" })
            {
                if (!botTable.Types.TryGetValue(name, out var bt) || bt?.BotInventory is null) continue;
                if (!bt.BotInventory.Equipment.TryGetValue(EquipmentSlots.FaceCover, out var pool) || pool is null) continue;
                double avg = pool.Count > 0 ? pool.Values.Average() : 10;
                pool[Mask] = Math.Max(1, Math.Round((pool.TryGetValue(Gp7, out var w) ? w : avg) * cfg.PmcBotWeightVsGp7));
                logger.Info($"[{ModInfo.Name}] {name} bots may wear the mask (weight {pool[Mask]} of {pool.Count} face covers)");
            }
        }
        catch (Exception ex) { logger.Warning($"[{ModInfo.Name}] PMC bots do not wear the mask: {ex.Message}"); }
        return Task.CompletedTask;
    }
}
