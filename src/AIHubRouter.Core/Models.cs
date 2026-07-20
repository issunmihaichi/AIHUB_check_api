using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

public sealed class MonitorSummary
{
    [JsonPropertyName("apis")]
    public List<ProviderStatus> Apis { get; init; } = [];

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset? GeneratedAt { get; init; }

    [JsonPropertyName("monitoringActive")]
    public bool MonitoringActive { get; init; }
}

public sealed class ProviderStatus
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("group_id")]
    public long? GroupId { get; init; }

    [JsonPropertyName("planType")]
    public string PlanType { get; init; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    [JsonPropertyName("priceMultiplier")]
    public double PriceMultiplier { get; init; }

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("checkedAt")]
    public DateTimeOffset? CheckedAt { get; init; }

    [JsonPropertyName("firstTokenLatencyMs")]
    public double? FirstTokenLatencyMs { get; init; }

    [JsonPropertyName("outputTokensPerSecond")]
    public double? OutputTokensPerSecond { get; init; }

    [JsonPropertyName("successRates")]
    public Dictionary<string, double> SuccessRates { get; init; } = [];

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    public double? SuccessRate6h => SuccessRates.TryGetValue("6h", out var value) ? value : null;
}

public sealed class GroupInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    [JsonPropertyName("rate_multiplier")]
    public double RateMultiplier { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

public sealed class ApiKeyInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("group_id")]
    public long? GroupId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("group")]
    public GroupInfo? Group { get; init; }
}

public sealed class PaginatedResponse<T>
{
    [JsonPropertyName("items")]
    public List<T> Items { get; init; } = [];

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; init; }

    [JsonPropertyName("pages")]
    public int Pages { get; init; }
}

public sealed record RoutingCriteria(
    string Platform,
    double MinimumSuccessRate6h,
    TimeSpan MaximumStatusAge);

public sealed record RouteCandidate(
    ProviderStatus Provider,
    GroupInfo Group,
    double EffectiveMultiplier,
    bool HasUserRateOverride);
