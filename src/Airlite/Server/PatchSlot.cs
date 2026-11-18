using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Path = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Airlite.Server;

/// <summary>
/// His patches fit the rig's front (as on the USMC PC Gen III; in-game text and logs say "patches", never "Jackal patches", Widowz
/// 2026-11-16). At Preload + 906 (after the patch wall exists, Preload + 900 range), every id the patch wall's grid takes is added to the
/// rig's patch slot, which has no patch of its own. Only adds ids; nothing happens when the patch wall is not installed. The client
/// draws a fitted patch on the fitted front's velcro or pouch face, or on the bare belly panel. 0.3.0 ("Patches on more gear",
/// 2026-11-17): the Slickster's own four patches are let in too (the client draws them from the Slickster's loose patch meshes) and every
/// patch is laid on the face it sits on (PatchDraw, patch_spots.json).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 906)]
public class PatchSlot(ISptLogger<PatchSlot> logger, ModHelper modHelper, TemplateTable templateTable) : IOnLoad
{
    public const string SlotPrefix = "mod_airlite_";
    public const string PatchSlotName = "mod_airlite_patch";

    public static MongoId Hash(string text) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..24]);

    public static MongoId Carrier => Hash("airlite|carrier");

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var modDir = PartFolder.Of(modHelper);
            var cfg = File.Exists(Path.Combine(modDir, "config.json")) ? modHelper.GetJsonDataFromFile<ModConfig>(modDir, "config.json") ?? new ModConfig() : new ModConfig();
            if (!cfg.PatchesFitFront) { logger.Info($"[{ModInfo.Name}] patches kept off the rig (config)"); return Task.CompletedTask; }
            if (!templateTable.Items.TryGetValue(Carrier, out var carrier) || carrier.Properties?.Slots is null) return Task.CompletedTask;
            if (!templateTable.Items.TryGetValue(Hash("jackalpatch|wall"), out var wall))
            {
                logger.Info($"[{ModInfo.Name}] no patch wall in the database; the patch slot stays empty");
                return Task.CompletedTask;
            }
            var accepted = new HashSet<MongoId>();
            foreach (var filter in wall.Properties?.Grids?.SelectMany(g => g.Properties?.Filters ?? []) ?? [])
                foreach (var id in filter.Filter ?? []) if (templateTable.Items.ContainsKey(id)) accepted.Add(id);
            var slickster = new[] { "aggressive_4", "curve2", "hoot2", "totn" }.Select(k => Hash("slickster|patch|" + k)).Count(accepted.Contains);
            var slot = carrier.Properties.Slots.FirstOrDefault(s => s.Name == PatchSlotName);
            var target = slot?.Properties?.Filters?.FirstOrDefault()?.Filter;
            if (target is null) { logger.Warning($"[{ModInfo.Name}] the rig has no patch slot filter"); return Task.CompletedTask; }
            int added = accepted.Count(id => target.Add(id));
            logger.Info($"[{ModInfo.Name}] {added} patches can go on the rig's front ({slickster} of them the Slickster's)");
        }
        catch (Exception ex)
        {
            logger.Warning($"[{ModInfo.Name}] patches not added to the patch slot: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
