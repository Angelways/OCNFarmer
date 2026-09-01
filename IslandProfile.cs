using System.Numerics;

namespace NorthIslandChestPlugin;

public enum IslandTarget
{
    NorthHorn = 1,
    SouthHorn = 2,
}

internal sealed class IslandProfile
{
    internal const uint NorthTerritoryId = 1346;
    internal const uint SouthTerritoryId = 1252;

    public required IslandTarget Target { get; init; }
    public required uint TerritoryId { get; init; }
    public required string ChapterName { get; init; }
    public required string EntryCommand { get; init; }
    public required Vector3 CrystalMoveTarget { get; init; }
    public required Vector3 CurrencyExchangeAnchor { get; init; }
    public required string[] ShardKeywords { get; init; }
    public required uint SilverCurrencyItemId { get; init; }
    public required uint GoldCurrencyItemId { get; init; }
    public required uint HealthCheckCurrencyItemId { get; init; }
    public required string SilverCurrencyName { get; init; }
    public required string GoldCurrencyName { get; init; }
    public required string HealthCheckCurrencyName { get; init; }
    public required uint SilverEventId { get; init; }
    public required uint GoldEventId { get; init; }
    public bool SupportsFixative { get; init; }
    public bool SupportsTower { get; init; }

    internal static IslandProfile North { get; } = new()
    {
        Target = IslandTarget.NorthHorn,
        TerritoryId = NorthTerritoryId,
        ChapterName = "蜃景幻界新月岛 北征之章",
        EntryCommand = "/pdrfe ocn",
        CrystalMoveTarget = new(882f, 258.5f, 882f),
        CurrencyExchangeAnchor = new(882f, 258.5f, 882f),
        ShardKeywords = ["妖火", "城塞", "圣堂", "遗迹", "街道"],
        SilverCurrencyItemId = 51975,
        GoldCurrencyItemId = 51976,
        HealthCheckCurrencyItemId = 51975,
        SilverCurrencyName = "十二城邦白银币",
        GoldCurrencyName = "十二城邦白金币",
        HealthCheckCurrencyName = "十二城邦白银币",
        SilverEventId = 0x1B0614,
        GoldEventId = 0x1B0615,
        SupportsFixative = true,
        SupportsTower = true,
    };

    internal static IslandProfile South { get; } = new()
    {
        Target = IslandTarget.SouthHorn,
        TerritoryId = SouthTerritoryId,
        ChapterName = "蜃景幻界新月岛 南征之章",
        EntryCommand = "/pdrfe ocs",
        CrystalMoveTarget = new(834f, 73f, -696f),
        CurrencyExchangeAnchor = new(834f, 73f, -696f),
        ShardKeywords = ["遗迹", "洞窟", "古树", "石塔"],
        SilverCurrencyItemId = 45043,
        GoldCurrencyItemId = 45044,
        HealthCheckCurrencyItemId = 45043,
        SilverCurrencyName = "十二城邦银币",
        GoldCurrencyName = "十二城邦金币",
        HealthCheckCurrencyName = "十二城邦银币",
        SilverEventId = 0x1B05B0,
        GoldEventId = 0x1B05B2,
        SupportsFixative = false,
        SupportsTower = false,
    };

    internal static IslandProfile Resolve(IslandTarget target) =>
        target == IslandTarget.SouthHorn ? South : North;
}
