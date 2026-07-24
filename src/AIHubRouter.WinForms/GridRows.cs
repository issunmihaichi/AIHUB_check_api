using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed class ProviderGridRow
{
    public required ProviderStatus Source { get; init; }
    public bool IsRoutable { get; init; }
    public bool IsBest { get; init; }
    public bool IsBlocked { get; init; }
    public string Best => IsBest ? "最低" : string.Empty;
    public long? GroupId => Source.GroupId;
    public string Plan => Source.PlanType;
    public string Platform => Source.Platform;
    public string PublicRate => Source.PriceMultiplier.ToString("0.####");
    public string EffectiveRate { get; init; } = "-";
    public string WeightedScore { get; init; } = "-";
    public double WeightedScoreValue { get; init; } = double.NegativeInfinity;
    public string AdaptiveRank { get; init; } = "-";
    public int AdaptiveRankValue { get; init; } = int.MaxValue;
    public string DecisionState { get; init; } = string.Empty;
    public string BlockStatus { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public bool UsesActiveProbeLatency { get; init; }
    public string Success6h => Source.SuccessRate6h is not null
        ? $"{RoutingEngine.NormalizeSuccessRate(Source.SuccessRate6h):P1}"
        : "-";
    public string FirstToken => UsesActiveProbeLatency && Source.ActiveProbeFirstTokenLatencyMs is { } activeLatency
        ? $"{activeLatency:0} ms"
        : Source.FirstTokenLatencyMs is { } latency ? $"{latency:0} ms" : "-";
    public string FirstTokenSource => UsesActiveProbeLatency && Source.ActiveProbeFirstTokenLatencyMs is { } latency
        ? $"本机中位数 {latency:0} ms / 共 {Source.ActiveProbeSampleCount} 次探测"
        : "运营商上报";
    public string CheckedAt => Source.CheckedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "-";
}

internal sealed class KeyGridRow
{
    public bool Selected { get; set; }
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public bool IsProbeKey { get; init; }
    public long? GroupId { get; set; }
    public string GroupName { get; set; } = "未绑定";
    public string Platform { get; set; } = "-";
}
