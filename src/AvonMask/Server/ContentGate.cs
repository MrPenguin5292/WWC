using System.Security.Cryptography;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Helpers.Server;

namespace AvonMask.Server;

/// <summary>
/// Checks that the client side of the mod is installed and matches its receipt before offers go on sale.
/// Game root = SPT_Runtime/.. ; client folder = BepInEx/plugins/&lt;pluginFolder&gt;/ ; receipt = content-ready.json there.
/// A failed gate skips offers only; templates stay registered (ItemRegistration never consults this).
/// </summary>
public static class ContentGate
{
    public static string GameRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

#if WWC   // built into WWC: its client files are in BepInEx\plugins\WWC\AvonMask and its code is in WWC.Client.dll
    public static string PluginDir(ModConfig cfg) => WWC.Server.Parts.ClientPart(WWC.Server.Parts.AvonMask);
#else
    public static string PluginDir(ModConfig cfg) => Path.Combine(GameRoot, "BepInEx", "plugins", cfg.PluginFolder);
#endif

    /// <summary>Returns null when the content is ready, otherwise the reason it is not.</summary>
    public static string? Check<T>(ModConfig cfg, ModHelper modHelper, ISptLogger<T> logger)
    {
        var dir = PluginDir(cfg);
        if (!Directory.Exists(dir)) return $"client folder missing: {dir}";

        var receiptPath = Path.Combine(dir, "content-ready.json");
        if (!File.Exists(receiptPath)) return $"content-ready.json missing in {dir}";

        ContentReceipt? receipt;
        try
        {
            receipt = modHelper.GetJsonDataFromFile<ContentReceipt>(dir, "content-ready.json");
        }
        catch (Exception ex)
        {
            return $"content-ready.json unreadable: {ex.Message}";
        }
        if (receipt is null || !receipt.Ready) return "content-ready.json says ready=false";
        if (receipt.EftBuild != 40743) return $"content-ready.json is for EFT build {receipt.EftBuild}, this install is 40743";

        if (!string.IsNullOrEmpty(receipt.ClientDll))
        {
#if WWC
            var dll = Path.Combine(WWC.Server.Parts.ClientDir, WWC.Server.Parts.ClientDll);
#else
            var dll = Path.Combine(dir, receipt.ClientDll);
#endif
            if (!File.Exists(dll) || new FileInfo(dll).Length == 0) return $"client DLL missing or empty: {dll}";
        }

        foreach (var b in receipt.Bundles)
        {
            var path = Path.Combine(dir, b.File);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return $"bundle missing or empty: {path}";
            var hash = Sha256Hex(path);
            if (!hash.Equals(b.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"bundle {b.File} hash {hash[..12]}... does not match receipt {b.Sha256[..Math.Min(12, b.Sha256.Length)]}...";
            }
        }

        logger.Info($"[{ModInfo.Name}] client content verified: {receipt.Bundles.Count} bundle(s), {receipt.ModelAssets.Count} model asset(s)");
        return null;
    }

    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}


