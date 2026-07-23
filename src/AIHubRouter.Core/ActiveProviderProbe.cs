using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AIHubRouter.Core;

public sealed record ActiveProbeRequest(
    string BaseUrl,
    string ApiKey,
    string Model,
    string Platform,
    long GroupId);

public sealed record ActiveProbeMeasurement(
    string Platform,
    long GroupId,
    DateTimeOffset ObservedAt,
    double FirstTokenLatencyMs)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Platform) || GroupId <= 0 ||
            !double.IsFinite(FirstTokenLatencyMs) || FirstTokenLatencyMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FirstTokenLatencyMs));
        }
    }
}

public sealed record ActiveProbeConfiguration(
    string BaseUrl,
    string ApiKey,
    string Model,
    long TestKeyId,
    string Platform)
{
    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("A valid HTTP or HTTPS base URL is required.", nameof(BaseUrl));
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("A dedicated test API Key is required.", nameof(ApiKey));
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("An upstream model is required.", nameof(Model));
        }

        if (TestKeyId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TestKeyId));
        }

        if (string.IsNullOrWhiteSpace(Platform))
        {
            throw new ArgumentException("A platform is required.", nameof(Platform));
        }
    }
}

public sealed record ActiveProbeResult(
    long GroupId,
    bool Success,
    ActiveProbeMeasurement? Measurement,
    string Detail);

public sealed record ActiveProbeCycleResult(
    IReadOnlyList<ActiveProbeResult> Results,
    bool TestKeyRestored);

public sealed class ActiveProbeRestoreException(Exception innerException) : InvalidOperationException(
    "The dedicated active-probe Key could not be restored.",
    innerException);

public interface IUpstreamProbeClient : IDisposable
{
    Task<ActiveProbeMeasurement> ProbeAsync(ActiveProbeRequest request, CancellationToken cancellationToken);
}

public sealed class OpenAiStreamingProbeClient : IUpstreamProbeClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<long> _timestamp;
    private readonly Func<long, long, double> _elapsedMilliseconds;

    public OpenAiStreamingProbeClient(
        HttpMessageHandler? messageHandler = null,
        Func<long>? timestamp = null,
        Func<long, long, double>? elapsedMilliseconds = null)
    {
        var handler = messageHandler ?? new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = false
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _elapsedMilliseconds = elapsedMilliseconds ?? ((started, completed) =>
            Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds);
    }

    public async Task<ActiveProbeMeasurement> ProbeAsync(
        ActiveProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("A valid HTTP or HTTPS base URL is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey) || string.IsNullOrWhiteSpace(request.Model) ||
            string.IsNullOrWhiteSpace(request.Platform) || request.GroupId <= 0)
        {
            throw new ArgumentException("The active probe request is incomplete.", nameof(request));
        }

        var origin = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "v1/chat/completions"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeApiKey(request.ApiKey));
        message.Content = JsonContent.Create(new
        {
            model = request.Model.Trim(),
            messages = new[] { new { role = "user", content = "ping" } },
            stream = true,
            max_tokens = 1,
            temperature = 0
        });

        var started = _timestamp();
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Active probe request failed with HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Equals("[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            if (HasContentToken(payload))
            {
                var completed = _timestamp();
                return new ActiveProbeMeasurement(
                    request.Platform.Trim(),
                    request.GroupId,
                    DateTimeOffset.UtcNow,
                    Math.Max(0, _elapsedMilliseconds(started, completed)));
            }
        }

        throw new InvalidOperationException("Active probe did not receive a content token.");
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool HasContentToken(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(content.GetString()))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Ignore non-SSE data lines until the upstream sends a usable token.
        }

        return false;
    }

    private static string NormalizeApiKey(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? normalized[7..].Trim()
            : normalized;
    }
}

public sealed class ActiveProviderProbeService
{
    private readonly IUpstreamProbeClient _upstream;
    private readonly Func<DateTimeOffset> _utcNow;

    public ActiveProviderProbeService(IUpstreamProbeClient upstream, Func<DateTimeOffset>? utcNow = null)
    {
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ActiveProbeResult> CheckSelectedKeyAsync(
        IActiveProbeKeyReader accountClient,
        ActiveProbeConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountClient);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();

        var keys = await accountClient.GetAllKeysAsync(cancellationToken);
        var testKey = keys.SingleOrDefault(key => key.Id == configuration.TestKeyId);
        if (testKey is null || !testKey.Status.Equals("active", StringComparison.OrdinalIgnoreCase) || testKey.GroupId is not { } groupId)
        {
            throw new InvalidOperationException("The selected health-check Key is not an active routed Key.");
        }

        try
        {
            var measurement = await _upstream.ProbeAsync(
                new ActiveProbeRequest(
                    configuration.BaseUrl,
                    configuration.ApiKey,
                    configuration.Model,
                    configuration.Platform,
                    groupId),
                cancellationToken);
            measurement.Validate();
            return new ActiveProbeResult(
                groupId,
                true,
                measurement with { ObservedAt = _utcNow() },
                "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ActiveProbeResult(groupId, false, null, "probe-failed");
        }
    }

    public async Task<ActiveProbeCycleResult> RunCycleAsync(
        IAIHubApiClient accountClient,
        ActiveProbeConfiguration configuration,
        IEnumerable<ProviderStatus> providers,
        IEnumerable<GroupInfo> groups,
        ProviderBlocklist? blocklist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountClient);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(groups);
        configuration.Validate();

        var selectedGroups = SelectProbeGroups(providers, groups, configuration.Platform, blocklist).ToArray();
        if (selectedGroups.Length == 0)
        {
            return new ActiveProbeCycleResult([], true);
        }

        var keys = await accountClient.GetAllKeysAsync(cancellationToken);
        var testKey = keys.SingleOrDefault(key => key.Id == configuration.TestKeyId);
        if (testKey is null || !testKey.Status.Equals("active", StringComparison.OrdinalIgnoreCase) || testKey.GroupId is not { } originalGroupId)
        {
            throw new InvalidOperationException("The dedicated active-probe Key is not an active routed Key.");
        }

        var currentGroupId = originalGroupId;
        var remoteGroupMayHaveChanged = false;
        var results = new List<ActiveProbeResult>(selectedGroups.Length);
        var restored = false;
        try
        {
            foreach (var group in selectedGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (currentGroupId != group.Id)
                    {
                        remoteGroupMayHaveChanged = true;
                        await accountClient.UpdateKeyGroupAsync(testKey.Id, group.Id, cancellationToken);
                        currentGroupId = group.Id;
                        remoteGroupMayHaveChanged = false;
                    }

                    var measurement = await _upstream.ProbeAsync(
                        new ActiveProbeRequest(
                            configuration.BaseUrl,
                            configuration.ApiKey,
                            configuration.Model,
                            configuration.Platform,
                            group.Id),
                        cancellationToken);
                    measurement.Validate();
                    results.Add(new ActiveProbeResult(group.Id, true, measurement with { ObservedAt = _utcNow() }, "ok"));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    results.Add(new ActiveProbeResult(group.Id, false, null, "probe-failed"));
                }
            }
        }
        finally
        {
            if (remoteGroupMayHaveChanged || currentGroupId != originalGroupId)
            {
                try
                {
                    await accountClient.UpdateKeyGroupAsync(testKey.Id, originalGroupId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    throw new ActiveProbeRestoreException(exception);
                }
            }

            restored = true;
        }

        return new ActiveProbeCycleResult(results, restored);
    }

    private static IEnumerable<GroupInfo> SelectProbeGroups(
        IEnumerable<ProviderStatus> providers,
        IEnumerable<GroupInfo> groups,
        string platform,
        ProviderBlocklist? blocklist)
    {
        var groupLookup = groups
            .Where(group => group.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase))
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var effectiveBlocklist = blocklist ?? ProviderBlocklist.Empty;

        return providers
            .Where(provider => provider.Enabled && provider.Available)
            .Where(provider => provider.GroupId is > 0 && groupLookup.ContainsKey(provider.GroupId.Value))
            .Where(provider => provider.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase))
            .Where(provider => !effectiveBlocklist.IsBlocked(provider, groupLookup[provider.GroupId!.Value]))
            .Select(provider => groupLookup[provider.GroupId!.Value])
            .GroupBy(group => group.Id)
            .Select(group => group.First())
            .OrderBy(group => group.Id);
    }
}
