using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AIHubRouter.Core.Tests;

internal static class TestFixtures
{
    internal static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    internal static ProviderStatus Provider(
        long groupId,
        double rate,
        bool available,
        double success,
        DateTimeOffset checkedAt,
        double? latency = 1000,
        bool warning = false,
        double? outputTps = 20,
        DateTimeOffset? lastCallEndedAt = null,
        bool enabled = true,
        DateTimeOffset? lastCallAt = null,
        string? id = null)
    {
        return new ProviderStatus
        {
            Id = id ?? $"provider-{groupId}",
            GroupId = groupId,
            PlanType = $"Plan {groupId}",
            Platform = "openai",
            PriceMultiplier = rate,
            Available = available,
            Enabled = enabled,
            CheckedAt = checkedAt,
            LastCallEndedAt = lastCallEndedAt,
            LastCallAt = lastCallAt,
            FirstTokenLatencyMs = latency,
            OutputTokensPerSecond = outputTps,
            SuccessRates = new Dictionary<string, double> { ["6h"] = success },
            WarningReasons = warning
                ? [new ProviderWarningReason { Type = "synthetic_warning", Message = "synthetic warning" }]
                : []
        };
    }

    internal static GroupInfo Group(long id)
    {
        return new GroupInfo
        {
            Id = id,
            Name = $"Group {id}",
            Platform = "openai",
            RateMultiplier = 1,
            Status = "active"
        };
    }

    internal static RoutingCriteria Criteria() => new("openai", 0, TimeSpan.FromMinutes(15));

    internal static BalancedRoutingPolicy Policy(RoutingMode mode) => new()
    {
        Platform = "openai",
        Mode = mode,
        MinimumSuccessRate6h = 0,
        MaximumStatusAge = TimeSpan.FromMinutes(15)
    };

    internal static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }
}

internal sealed class MemoryRouteStateStore : IRouteStateStore
{
    private RouteState _state = new();
    public RouteState Load() => _state;
    public void Save(RouteState state) => _state = state;
}

internal sealed class StubRoutingClientFactory(IAIHubApiClient client) : IAIHubClientFactory
{
    public IAIHubApiClient Create(string baseUrl, string? bearerToken, string? cookie, string? userAgent) => client;
}

internal sealed class StubRoutingClient(DateTimeOffset now) : IAIHubApiClient
{
    public int SummaryCalls { get; private set; }
    public int GroupsCalls { get; private set; }
    public int RatesCalls { get; private set; }
    public int KeysCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public List<long> UpdatedGroupIds { get; } = [];
    public int RefreshCalls { get; private set; }
    public int LoginCalls { get; private set; }
    public bool FailFirstSummaryAuth { get; init; }
    public bool ThrowNetwork { get; init; }
    public bool TwoKeys { get; init; }
    public bool KeysAlreadyOnTarget { get; init; }
    public bool MixedGroups { get; init; }
    public int FailUpdateCount { get; init; }
    public double? UserRateOverride { get; init; }
    public IReadOnlyList<ProviderStatus>? ProvidersOverride { get; init; }
    public IReadOnlyList<GroupInfo>? GroupsOverride { get; init; }

    public Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default)
    {
        SummaryCalls++;
        if (ThrowNetwork) throw new HttpRequestException("synthetic network failure");
        if (FailFirstSummaryAuth && SummaryCalls == 1)
            throw new AIHubApiException("Authentication required.", HttpStatusCode.Unauthorized, "401");
        return Task.FromResult(new MonitorSummary
        {
            Apis = ProvidersOverride?.ToList() ?? [ProviderForStub(2, now)]
        });
    }

    public Task<JsonElement> ValidateLoginAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AuthSession> LoginAsync(LoginCredentials credentials, CancellationToken cancellationToken = default)
    {
        LoginCalls++;
        return Task.FromResult(new AuthSession("synthetic-login", "synthetic-refresh-login", now.AddHours(1)));
    }

    public Task<AuthSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        RefreshCalls++;
        return Task.FromResult(new AuthSession("synthetic-refreshed", "synthetic-refresh-rotated", now.AddHours(1)));
    }

    public Task<IReadOnlyList<GroupInfo>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default)
    {
        GroupsCalls++;
        return Task.FromResult(GroupsOverride ?? [GroupForStub(2)]);
    }

    public Task<IReadOnlyDictionary<long, double>> GetUserGroupRatesAsync(CancellationToken cancellationToken = default)
    {
        RatesCalls++;
        IReadOnlyDictionary<long, double> rates = UserRateOverride is { } value
            ? new Dictionary<long, double> { [2] = value }
            : new Dictionary<long, double>();
        return Task.FromResult(rates);
    }

    public Task<IReadOnlyList<ApiKeyInfo>> GetAllKeysAsync(CancellationToken cancellationToken = default)
    {
        KeysCalls++;
        var keys = new List<ApiKeyInfo>
        {
            new() { Id = 10, Name = "Synthetic Key 10", Status = "active", GroupId = KeysAlreadyOnTarget ? 2 : 1 }
        };
        if (TwoKeys)
        {
            keys.Add(new ApiKeyInfo
            {
                Id = 11,
                Name = "Synthetic Key 11",
                Status = "active",
                GroupId = KeysAlreadyOnTarget || !MixedGroups ? (KeysAlreadyOnTarget ? 2 : 1) : 2
            });
        }

        return Task.FromResult<IReadOnlyList<ApiKeyInfo>>(keys);
    }

    public Task<ApiKeyInfo> UpdateKeyGroupAsync(long keyId, long groupId, CancellationToken cancellationToken = default)
    {
        UpdateCalls++;
        UpdatedGroupIds.Add(groupId);
        if (UpdateCalls <= FailUpdateCount)
        {
            throw new InvalidOperationException("synthetic update failure");
        }

        return Task.FromResult(new ApiKeyInfo
        {
            Id = keyId,
            Name = $"Synthetic Key {keyId}",
            Status = "active",
            GroupId = groupId,
            Group = GroupForStub(groupId)
        });
    }

    private static ProviderStatus ProviderForStub(long groupId, DateTimeOffset checkedAt) => new()
    {
        Id = $"provider-{groupId}",
        GroupId = groupId,
        PlanType = "Synthetic",
        Platform = "openai",
        PriceMultiplier = 0.01,
        Available = true,
        Enabled = true,
        CheckedAt = checkedAt,
        FirstTokenLatencyMs = 500,
        OutputTokensPerSecond = 20,
        SuccessRates = new Dictionary<string, double> { ["6h"] = 1 }
    };

    private static GroupInfo GroupForStub(long id) => new()
    {
        Id = id,
        Name = $"Group {id}",
        Platform = "openai",
        RateMultiplier = 1,
        Status = "active"
    };

    public void Dispose()
    {
    }
}
