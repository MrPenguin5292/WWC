using System;
using System.Collections.Generic;

namespace WWCHelmetDisplays.Client.Geometry;

/// <summary>
/// Where the bust and the helmet crown are cut out of the game's mannequin. Everything is in the gear frame: the frame the game hangs a helmet in
/// (head bone turned by its fixed rotation), x to the mannequin's left as seen from the front... precisely: x right, y up, z toward the face.
/// The mannequin's head spans about x +-0.083, y -0.078..0.176, z -0.078..0.141 in it; the shoulders' tops are near y -0.12.
/// </summary>
public static class Shapes
{
    /// <summary>The bust: flat under the chest, each side one flat slant from the top of the shoulder down to the base, like a carved bust.</summary>
    public static List<Cut> Bust(float bottom = -0.30f, float underArm = UnderArm, float front = Front, float back = Back, float shoulder = Shoulder)
    {
        var cuts = new List<Cut> { new Cut(new V3(0, 1, 0), -bottom) };
        foreach (var side in new[] { 1f, -1f })
        {
            // Shoulders: a cut through the top of the deltoid, leaning out at the top (x per y = shoulder).
            cuts.Add(Cut.Through(new V3(side * 0.200f, -0.150f, 0f), Dir(-side, shoulder)));
            // Under the arm a second slant closing in toward the base (none by default: the shoulder cut runs down the whole side).
            if (underArm > 0f) cuts.Add(Cut.Through(new V3(side * underArm, -0.235f, 0f), Dir(-side, 0.7f)));
        }
        // In front and behind, the gear closes in toward the base the same way (45 degrees), so nothing hangs out past it in the air: pouches
        // low on a vest stuck out 10 cm beyond the stand (WWC trailer v3.2, 2026-11-18). Both stay clear of the mannequin itself.
        if (front > 0f) cuts.Add(Cut.Through(new V3(0f, bottom, front), new V3(0f, 1f, -1f)));
        if (back < 0f) cuts.Add(Cut.Through(new V3(0f, bottom, back), new V3(0f, 1f, 1f)));
        return cuts;
    }

    /// <summary>
    /// The sides (WWC trailer v3.2, 2026-11-18). They were two cuts: the shoulder leaning 18 degrees (x per y 0.325) and under the arm a 0.7 slant
    /// through x 0.165 at y -0.235. In the mannequin's pose one arm hangs in front of the chest and the other behind it, pressed to the side
    /// down to y -0.25, so the 18-degree cut sliced that arm lengthwise: a strip of it stood off the side under the arm (the "ragged sliver" under
    /// Tagilla's and Killa's arm) and its cut face stuck out as a flat fin from the shoulder to the base. Now one 0.7 slant runs from the top of
    /// the shoulder (x 0.2 at y -0.15) to the base, and the arm is cut flush with the side. <see cref="UnderArm"/> of 0 adds no second cut.
    /// </summary>
    public const float Shoulder = 0.7f, UnderArm = 0f;
    /// <summary>Where the front and back cuts meet the bottom (z): just clear of the mannequin's chest (+0.161) and back (-0.154) there.</summary>
    public const float Front = 0.175f, Back = -0.17f;

    /// <summary>The trophy bust is the same bust with its back cut flat, where it meets the shield.</summary>
    public static List<Cut> TrophyBust(float back = -0.085f, float bottom = -0.30f)
    {
        var cuts = Bust(bottom);
        cuts.Add(new Cut(new V3(0, 0, 1), -back));
        return cuts;
    }

    /// <summary>The helmet crown: the top of the mannequin's head above the brow, which every helmet in the game fits over.</summary>
    public static List<Cut> Crown(float brow = 0.055f) => new() { new Cut(new V3(0, 1, 0), -brow) };

    private static V3 Dir(float x, float y) => new V3(x, y, 0f).Normalized;
}
