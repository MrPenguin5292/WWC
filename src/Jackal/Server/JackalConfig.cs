using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;

namespace LocalTrader.Server;

public sealed record JackalConfig
{
    public string Name { get; init; } = "The Jackal";
    public int RestockSeconds { get; init; } = 3600;
    public double PriceMultiplier { get; init; } = 1;
    public int StockPerReset { get; init; } = 10;
    public int PurchaseLimit { get; init; } = 5;
    public bool StockLooseWeaponParts { get; init; } = true;
    public bool IncludeFutureModItems { get; init; } = false;
    public List<string> ItemNamePrefixes { get; init; } =
        ["local_ax50_", "local_platatac_", "gs45_", "local_lewis_", "sierra_carrier_", "sierra_patch_"];
    public List<string> ExtraItemIds { get; init; } = [];
    public List<string> ExcludedItemIds { get; init; } = [];
    public List<LoyaltyTier> LoyaltyLevels { get; init; } =
    [
        new(1, 0, 0), new(10, .18, 150_000), new(18, .42, 500_000), new(25, .60, 1_200_000)
    ];

    public static JackalConfig Read(string path)
    {
        var config = JsonSerializer.Deserialize<JackalConfig>(File.ReadAllText(path), new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("The Jackal config is empty.");
        if (string.IsNullOrWhiteSpace(config.Name) || config.RestockSeconds < 60 ||
            !double.IsFinite(config.PriceMultiplier) || config.PriceMultiplier < .1 || config.PriceMultiplier > 100 ||
            config.StockPerReset < 1 || config.StockPerReset > 1_000_000 ||
            config.PurchaseLimit < 1 || config.PurchaseLimit > config.StockPerReset ||
            config.LoyaltyLevels is not { Count: 4 } || config.ItemNamePrefixes is null ||
            config.ExtraItemIds is null || config.ExcludedItemIds is null)
            throw new InvalidDataException("The Jackal config has invalid stock, pricing or loyalty values.");
        for (int i = 0; i < 4; i++)
        {
            var level = config.LoyaltyLevels[i];
            if (level.PlayerLevel < 1 || !double.IsFinite(level.Standing) || level.Standing < 0 || level.Standing > .72 || level.Sales < 0 ||
                (i > 0 && (level.PlayerLevel < config.LoyaltyLevels[i-1].PlayerLevel ||
                    level.Standing < config.LoyaltyLevels[i-1].Standing || level.Sales < config.LoyaltyLevels[i-1].Sales)))
                throw new InvalidDataException("The Jackal loyalty thresholds must rise in order and be reachable by his campaign.");
        }
        if (config.LoyaltyLevels[0] != new LoyaltyTier(1, 0, 0))
            throw new InvalidDataException("The Jackal first loyalty level must be accessible at level 1, zero standing and zero sales.");
        foreach (var id in config.ExtraItemIds.Concat(config.ExcludedItemIds))
            if (id.Length != 24 || !id.All(Uri.IsHexDigit)) throw new InvalidDataException($"Invalid item ID: {id}");
        if (config.ItemNamePrefixes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Item name prefixes cannot be empty.");
        return config;
    }
}

public sealed record LoyaltyTier(int PlayerLevel, double Standing, long Sales);

public static class JackalIds
{
    public static MongoId For(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("local.jackal:" + key)))[..24].ToLowerInvariant());
}
