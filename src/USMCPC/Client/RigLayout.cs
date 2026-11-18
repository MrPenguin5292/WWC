using System;
using System.Collections;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace USMCPC.Client;

// Shared rig layout for the Shaw Concepts ARC V2 (mods/USMCPC), JPC and AirLite clients: the three files are the same apart from the
// namespace line and the Carrier id. Copy changes to all three.
//
// Widowz 2026-11-18: "you need to turn off your rig layouts in unity before you build them". The three carriers have the same eight
// pockets (five 1x2, two 1x1, one 2x2) but borrowed stock layouts drawn for other pockets ("mmac": six 1x2 + two 1x1; the AirLite's
// "D3crx": ten pockets), so their pockets drew on top of each other. The inventory builds a rig's pockets from the layout object named
// in its template (RigLayoutName), found under "UI/Rig Layouts/<name>" in ResourcesCache. Every stock layout is stored switched off
// (root inactive) and the game switches on the copy it shows; a layout left switched on sits on the screen by itself. This makes one
// layout, "widowz_carrier_8", from the game's own "mmac" layout (same frames, tiles and scripts), moves its eight pockets into the
// carriers' shape, keeps it switched off, puts it in the cache, and points this client's carrier at it. Without the client the carrier
// keeps the stock name the server gave it.
internal static class RigLayout
{
    public const string Name = "widowz_carrier_8";
    private const string Base = "mmac";
    private const string Carrier = "9bb8f5e183e76cf3311e862f";

    // Pocket i takes the template's Grids[i]: x, y of the top-left corner, cells wide, cells high. 64 px per cell (+63 per extra cell)
    // and 6 px gaps, as in the stock layouts. Top row the five tall pockets; under it the big one, then the two small ones.
    private static readonly (float x, float y, int w, int h)[] Pockets =
    {
        (2, 1, 1, 2), (72, 1, 1, 2), (142, 1, 1, 2), (212, 1, 1, 2), (282, 1, 1, 2),
        (135, -132, 1, 1), (205, -132, 1, 1), (2, -132, 2, 2),
    };

    private static object? appliedTo;
    private static bool failed;

    /// <summary>Called every frame; cheap once done for the current item factory.</summary>
    public static void Tick(BepInEx.Logging.ManualLogSource log)
    {
        if (failed || !Singleton<ItemFactory>.Instantiated) return;
        var factory = Singleton<ItemFactory>.Instance;
        if (ReferenceEquals(appliedTo, factory)) return;
        try
        {
            if (factory.ItemTemplates == null || !factory.ItemTemplates.TryGetValue(Carrier, out var template)) return;
            if (!EnsureLayout(log)) { failed = true; return; }
            var field = Traverse.Create(template).Field("RigLayoutName");
            if (!field.FieldExists()) { log.LogWarning("Rig layout: the carrier template has no RigLayoutName; the stock layout stays."); failed = true; return; }
            var old = field.GetValue() as string;
            field.SetValue(Name);
            appliedTo = factory;
            log.LogInfo($"Rig layout: carrier pockets drawn with {Name} (was {old}).");
        }
        catch (Exception e) { failed = true; log.LogWarning("Rig layout not applied; the stock layout stays. " + e.Message); }
    }

    private static bool EnsureLayout(BepInEx.Logging.ManualLogSource log)
    {
        var cache = AccessTools.TypeByName("EFT.Utilities.ResourcesCache") ?? AccessTools.TypeByName("ResourcesCache");
        var storage = cache == null ? null : AccessTools.Field(cache, "_storage")?.GetValue(null) as IDictionary;
        var pop = cache == null ? null : AccessTools.GetDeclaredMethods(cache).FirstOrDefault(m => m.Name == "Pop" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
        // The game asks the cache for the layout as its ContainedGridsView component and the cache keeps whatever type it was first
        // asked for: asking for "UI/Rig Layouts/mmac" as a GameObject first left the stock MMAC (and every rig on it) without pockets
        // (fixture run USMCPC_20261118_193003, ContainedGridsView.CreateGrids -> Instantiate(null)). So: same type as the game, always.
        var viewType = AccessTools.TypeByName("EFT.UI.DragAndDrop.ContainedGridsView");
        if (storage == null || pop == null || viewType == null) { log.LogWarning("Rig layout: ResourcesCache or ContainedGridsView not found; the stock layout stays."); return false; }
        var key = "UI/Rig Layouts/" + Name;
        if (storage.Contains(key) && storage[key] is Component existing && existing != null && viewType.IsInstanceOfType(existing)) return true;   // another of the three clients made it
        var source = pop.MakeGenericMethod(viewType).Invoke(null, new object[] { "UI/Rig Layouts/" + Base }) as Component;
        if (source == null) { log.LogWarning($"Rig layout: stock layout {Base} not found; the stock layout stays."); return false; }
        var copy = UnityEngine.Object.Instantiate(source);
        var layout = copy.gameObject;
        layout.SetActive(false);   // stored switched off like every stock layout; the game switches on the copy it shows
        layout.name = Name;
        UnityEngine.Object.DontDestroyOnLoad(layout);
        var views = layout.transform.Cast<Transform>().Where(t => t.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "GridView")).ToList();
        if (views.Count != Pockets.Length)
        {
            UnityEngine.Object.Destroy(layout);
            log.LogWarning($"Rig layout: {Base} has {views.Count} pockets, expected {Pockets.Length}; the stock layout stays.");
            return false;
        }
        float right = 0, bottom = 0;
        for (int i = 0; i < Pockets.Length; i++)
        {
            var p = Pockets[i];
            var rt = (RectTransform)views[i];
            rt.anchoredPosition = new Vector2(p.x, p.y);
            rt.sizeDelta = new Vector2(1 + 63 * p.w, 1 + 63 * p.h);
            right = Mathf.Max(right, p.x + rt.sizeDelta.x);
            bottom = Mathf.Max(bottom, -p.y + rt.sizeDelta.y);
        }
        ((RectTransform)layout.transform).sizeDelta = new Vector2(right + 2, bottom + 1);
        storage[key] = copy;
        log.LogInfo($"Rig layout: {Name} made from {Base} ({Pockets.Length} pockets, {right + 2:F0}x{bottom + 1:F0} px), stored switched off.");
        return true;
    }
}
