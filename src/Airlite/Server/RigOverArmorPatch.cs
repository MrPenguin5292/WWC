using System.Reflection;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;

namespace Airlite.Server;

/// <summary>
/// SPT 4.1.6 rolls a bot's body armor first and, when it gets one, narrows the TacticalVest pool to rigs WITHOUT slots
/// (BotInventoryGenerator.FilterRigsToThoseWithoutProtection: `!itemHelper.ItemHasSlots(tpl)`), since rigs with slots are normally the
/// armored ones. The AirLite is a plain chest rig whose slots are cosmetic part slots, so without this every bot that wore it had no
/// body armor (fixture bot check 2026-11-16: 30 of 30). The prefix notes the rig's weight in the pool, the postfix puts it back after
/// the filter. Nothing else in the pool changes; when the rig was not in the pool nothing happens.
/// </summary>
internal sealed class RigOverArmorPatch() : AbstractPatch("local.airlite.rigoverarmor")
{
    internal static MongoId Rig;

    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(BotInventoryGenerator), nameof(BotInventoryGenerator.FilterRigsToThoseWithoutProtection));

    [PatchPrefix]
    public static void Prefix(Dictionary<EquipmentSlots, Dictionary<MongoId, double>> templateEquipment, out double? __state)
    {
        __state = null;
        try
        {
            if (templateEquipment.TryGetValue(EquipmentSlots.TacticalVest, out var pool) && pool is not null && pool.TryGetValue(Rig, out var weight))
                __state = weight;
        }
        catch { }
    }

    [PatchPostfix]
    public static void Postfix(Dictionary<EquipmentSlots, Dictionary<MongoId, double>> templateEquipment, double? __state)
    {
        try
        {
            if (__state is not { } weight) return;
            if (templateEquipment.TryGetValue(EquipmentSlots.TacticalVest, out var pool) && pool is not null) pool.TryAdd(Rig, weight);
        }
        catch { }
    }
}
