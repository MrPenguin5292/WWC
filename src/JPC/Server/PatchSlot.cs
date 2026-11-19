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

namespace JPC.Server;

/// <summary>
/// Like the ARC V2 (Widowz 2026-11-16): his patches fit the chest (in-game text and logs say "patches", never "Jackal patches", his 03:49 UTC ask). At Preload + 907 (after Jackal Patches has built its patch wall, and
/// after the sticker grids at + 900), every id the patch wall's grid takes is added to the carrier's patch slots. Only adds ids; nothing
/// happens when the patches part is not installed. 0.3.0 ("Patches on more gear", 2026-11-17): the Slickster's own four patches are
/// let in too (the client now draws them from the Slickster's loose patch meshes), and the new back patch slot takes the same list
/// (without the USMC patch, which is drawn on the chest spots). The client lays each patch on its spot (PatchDraw, patch_spots.json).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 907)]
public class PatchSlot(ISptLogger<PatchSlot> logger, ModHelper modHelper, TemplateTable templateTable) : IOnLoad
{
    public const string PatchSlotName = "mod_jpc_patch";
    public const string BackSlotName = "mod_jpc_patch_back";

    public static MongoId Hash(string text) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..24]);

    public static MongoId Carrier => Hash("jpc|carrier");

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var modDir = PartFolder.Of(modHelper);
            var cfg = File.Exists(Path.Combine(modDir, "config.json")) ? modHelper.GetJsonDataFromFile<ModConfig>(modDir, "config.json") ?? new ModConfig() : new ModConfig();
            if (!cfg.JackalPatchesFitChest) { logger.Info($"[{ModInfo.Name}] patches kept off the carrier (config)"); return Task.CompletedTask; }
            if (!templateTable.Items.TryGetValue(Carrier, out var carrier) || carrier.Properties?.Slots is null) return Task.CompletedTask;
            if (!templateTable.Items.TryGetValue(Hash("jackalpatch|wall"), out var wall))
            {
                logger.Info($"[{ModInfo.Name}] no patch wall in the database; the patch slot takes the USMC patch only");
                return Task.CompletedTask;
            }
            var accepted = new HashSet<MongoId>();
            foreach (var filter in wall.Properties?.Grids?.SelectMany(g => g.Properties?.Filters ?? []) ?? [])
                foreach (var id in filter.Filter ?? []) if (templateTable.Items.ContainsKey(id)) accepted.Add(id);
            var slickster = new[] { "aggressive_4", "curve2", "hoot2", "totn" }.Select(k => Hash("slickster|patch|" + k)).Count(accepted.Contains);
            foreach (var (name, where) in new[] { (PatchSlotName, "chest"), (BackSlotName, "back") })
            {
                var target = carrier.Properties.Slots.FirstOrDefault(s => s.Name == name)?.Properties?.Filters?.FirstOrDefault()?.Filter;
                if (target is null) { logger.Warning($"[{ModInfo.Name}] the carrier has no {where} patch slot filter"); continue; }
                int added = accepted.Count(id => target.Add(id));
                logger.Info($"[{ModInfo.Name}] {added} patches can go on the carrier's {where} ({slickster} of them the Slickster's)");
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[{ModInfo.Name}] patches not added to the patch slot: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
