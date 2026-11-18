using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Path = System.IO.Path;

namespace Grunts.Server;

/// <summary>
/// The Grunt is one of the game's infected roles (config gruntRole, default infectedLaborant) turned into a tougher machete fighter: the
/// infected brain (InfectedLayersStrategy, "Fast" melee mode on normal difficulty) and the zombie animator come with the role, so it
/// spawns, moves and fights with the game's own code. This changes that role's bot type: health, the melee weapon pool (no pistol),
/// the name and the XP for a kill. The look and voice are swapped on the client (Grunts plugin).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 31)]
public class GruntBot(ISptLogger<GruntBot> logger, ModHelper modHelper, BotTable botTable, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { logger.Error($"[{ModInfo.Name}] Grunt bot type not changed: {ex}"); }
        return Task.CompletedTask;
    }

    internal static ModConfig LoadConfig(ModHelper modHelper)
    {
        var dir = PartFolder.Of(modHelper);
        return File.Exists(Path.Combine(dir, "config.json")) ? modHelper.GetJsonDataFromFile<ModConfig>(dir, "config.json") ?? new ModConfig() : new ModConfig();
    }

    private void Run()
    {
        var cfg = LoadConfig(modHelper);
        if (!cfg.Enabled) { logger.Info($"[{ModInfo.Name}] switched off in config.json"); return; }
        var key = cfg.GruntRole.ToLowerInvariant();
        if (!botTable.Types.TryGetValue(key, out var bt) || bt is null) { logger.Error($"[{ModInfo.Name}] bot type {key} not found"); return; }

        double H(string part, double fallback) => cfg.Health.TryGetValue(part, out var v) && v > 0 ? v : fallback;
        bt.BotHealth ??= new BotTypeHealth();
        bt.BotHealth.BodyParts =
        [
            new BodyPart
            {
                Head = new MinMax<double>(H("Head", 45), H("Head", 45)), Chest = new MinMax<double>(H("Chest", 350), H("Chest", 350)),
                Stomach = new MinMax<double>(H("Stomach", 280), H("Stomach", 280)),
                LeftArm = new MinMax<double>(H("LeftArm", 140), H("LeftArm", 140)), RightArm = new MinMax<double>(H("RightArm", 140), H("RightArm", 140)),
                LeftLeg = new MinMax<double>(H("LeftLeg", 160), H("LeftLeg", 160)), RightLeg = new MinMax<double>(H("RightLeg", 160), H("RightLeg", 160)),
            },
        ];

        var inv = bt.BotInventory ?? throw new InvalidDataException($"{key} has no inventory");
        var weapons = new Dictionary<MongoId, double>();
        foreach (var (tpl, w) in cfg.Weapons)
            if (MongoId.IsValidMongoId(tpl) && templates.Items.ContainsKey(new MongoId(tpl)) && w > 0) weapons[new MongoId(tpl)] = w;
        if (weapons.Count == 0) throw new InvalidDataException("no valid melee weapon in config weapons");
        inv.Equipment[EquipmentSlots.Scabbard] = weapons;
        // The stock infected keep their holster pistol: the bot's weapon check (BotWeaponSelector.SetSlotItem) needs a firearm slot
        // filled or the bot never starts thinking (fixture raid 2026-11-16: "Bot can't find weapon for self", Grunts stood still).
        // In "Fast" melee mode the infected brain only ever draws the Scabbard (InfectedBotWeaponSelector.TryChangeToSlot).
        var chances = bt.BotChances?.EquipmentChances;
        if (chances is not null) { chances["Scabbard"] = 100; chances["Holster"] = 100; }

        int gear = 0;
        foreach (var (slotName, pool) in cfg.Gear)
        {
            if (!Enum.TryParse<EquipmentSlots>(slotName, true, out var slot)) { logger.Warning($"[{ModInfo.Name}] gear: unknown slot {slotName}"); continue; }
            var valid = pool.Where(p => MongoId.IsValidMongoId(p.Key) && templates.Items.ContainsKey(new MongoId(p.Key)) && p.Value > 0)
                .ToDictionary(p => new MongoId(p.Key), p => p.Value);
            if (valid.Count == 0) { logger.Info($"[{ModInfo.Name}] gear: nothing for {slotName} is installed, skipped"); continue; }
            inv.Equipment[slot] = valid;
            if (chances is not null) chances[slot.ToString()] = cfg.GearChance.TryGetValue(slotName, out var c) ? c : 100;
            gear += valid.Count;
        }

        bt.FirstNames = [cfg.Nickname];
        bt.LastNames = [];
        if (bt.BotExperience is not null)
        {
            bt.BotExperience.Reward ??= new();
            foreach (var d in new[] { "easy", "normal", "hard", "impossible" }) bt.BotExperience.Reward[d] = new MinMax<int>(cfg.XpPerKill, cfg.XpPerKill);
            bt.BotExperience.UseSimpleAnimator = true;
        }
        var total = cfg.Health.Values.Sum();
        logger.Success($"[{ModInfo.Name}] {cfg.GruntRole} is now the Grunt: {total:0} HP, {weapons.Count} melee weapon(s), {gear} extra gear item(s), {cfg.XpPerKill} xp a kill");
    }
}
