using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed class ProviderGridRow
{
    public required ProviderStatus Source { get; init; }
    public bool IsBest { get; init; }
    public string Best => IsBest ? "最低" : string.Empty;
    public long? GroupId => Source.GroupId;
    public string Plan => Source.PlanType;
    public string Platform => Source.Platform;
    public string PublicRate => Source.PriceMultiplier.ToString("0.####");
    public string EffectiveRate { get; init; } = "-";
    public string WeightedScore { get; init; } = "-";
    public string AdaptiveRank { get; init; } = "-";
    public int AdaptiveRankValue { get; init; } = int.MaxValue;
    public string DecisionState { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Success6h => Source.SuccessRate6h is { } rate ? $"{rate:P1}" : "-";
    public string FirstToken => Source.FirstTokenLatencyMs is { } latency ? $"{latency:0} ms" : "-";
    public string CheckedAt => Source.CheckedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "-";
}

internal sealed class KeyGridRow
{
    public bool Selected { get; set; }
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long? GroupId { get; set; }
    public string GroupName { get; set; } = "未绑定";
    public string Platform { get; set; } = "-";
}
