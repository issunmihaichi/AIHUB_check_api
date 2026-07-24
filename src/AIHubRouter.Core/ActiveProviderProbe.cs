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

public sealed record ActiveProbeObservation(
    string Platform,
    long GroupId,
    DateTimeOffset ObservedAt,
    bool Success,
    double? FirstTokenLatencyMs = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Platform))
        {
            throw new ArgumentException("An active-probe platform is required.", nameof(Platform));
        }

        if (GroupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GroupId));
        }

        if (FirstTokenLatencyMs is { } latency && (!double.IsFinite(latency) || latency < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(FirstTokenLatencyMs));
        }

        if (Success && FirstTokenLatencyMs is null)
        {
            throw new ArgumentException(
                "A successful active-probe observation requires TTFT data.",
                nameof(FirstTokenLatencyMs));
        }

        if (!Success && FirstTokenLatencyMs is not null)
        {
            throw new ArgumentException(
                "A failed active-probe observation cannot contain TTFT data.",
                nameof(FirstTokenLatencyMs));
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

public sealed class ActiveProbeProtocolException : InvalidOperationException
{
    public ActiveProbeProtocolException(string? apiCode = null)
        : base("The active probe received an invalid or failed upstream response.")
    {
        ApiCode = NormalizeApiCode(apiCode);
        IsGlobalConfigurationFailure = IsGlobalConfigurationCode(apiCode);
    }

    public string? ApiCode { get; }

    public string Detail => ApiCode is { } apiCode ? $"api:{apiCode}" : "probe-failed";

    public bool IsGlobalConfigurationFailure { get; }

    private static string? NormalizeApiCode(string? apiCode)
    {
        var value = apiCode?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 64 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
        {
            return null;
        }

        if (!IsGlobalConfigurationCode(value) &&
            IsCredentialShapedCode(value))
        {
            return null;
        }

        return value.ToLowerInvariant();
    }

    private static bool IsCredentialShapedCode(string value)
    {
        if (value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("bearer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = value.Split('.');
        return parts.Length == 3 && parts.All(part =>
            part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    private static bool IsGlobalConfigurationCode(string? apiCode)
    {
        if (string.IsNullOrWhiteSpace(apiCode))
        {
            return false;
        }

        return apiCode.Trim().Replace('-', '_').ToLowerInvariant() switch
        {
            "invalid_api_key" or
            "authentication_error" or
            "unauthorized" or
            "forbidden" or
            "model_not_found" or
            "invalid_model" => true,
            _ => false
        };
    }
}

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
            var protocolException = await TryReadJsonFailureAsync(response.Content, cancellationToken);
            throw new HttpRequestException(
                $"Active probe request failed with HTTP {(int)response.StatusCode}.",
                protocolException,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (IsJsonMediaType(response.Content.Headers.ContentType?.MediaType))
        {
            await ThrowJsonResponseAsync(stream, cancellationToken);
        }

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

            if (payload.Length == 0)
            {
                throw new ActiveProbeProtocolException();
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

        throw new ActiveProbeProtocolException();
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool HasContentToken(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw new ActiveProbeProtocolException();
        }

        using (document)
        {
            var envelope = ApiResponseEnvelope.Classify(document.RootElement);
            if (envelope.IsFailure)
            {
                throw new ActiveProbeProtocolException(envelope.ApiCode);
            }

            var root = envelope.Payload;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ActiveProbeProtocolException();
            }

            if (!root.TryGetProperty("choices", out var choices) ||
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

        return false;
    }

    private static bool IsJsonMediaType(string? mediaType) =>
        mediaType is not null &&
        (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static async Task ThrowJsonResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new ActiveProbeProtocolException();
        }

        using (document)
        {
            var envelope = ApiResponseEnvelope.Classify(document.RootElement);
            throw new ActiveProbeProtocolException(envelope.IsFailure ? envelope.ApiCode : null);
        }
    }

    private static async Task<ActiveProbeProtocolException?> TryReadJsonFailureAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (!IsJsonMediaType(content.Headers.ContentType?.MediaType))
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var envelope = ApiResponseEnvelope.Classify(document.RootElement);
            return envelope.IsFailure ? new ActiveProbeProtocolException(envelope.ApiCode) : null;
        }
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
        cancellationToken.ThrowIfCancellationRequested();

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
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetRecoverableProbeDetail(exception, cancellationToken, out var detail))
            {
                throw;
            }

            return new ActiveProbeResult(groupId, false, null, detail);
        }
    }

    public async Task<ActiveProbeResult> ProbeTargetAsync(
        IAIHubApiClient accountClient,
        ActiveProbeConfiguration configuration,
        long targetGroupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountClient);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        if (targetGroupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetGroupId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var keys = await accountClient.GetAllKeysAsync(cancellationToken);
        var testKey = keys.SingleOrDefault(key => key.Id == configuration.TestKeyId);
        if (testKey is null ||
            !testKey.Status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
            testKey.GroupId is not { } originalGroupId)
        {
            throw new InvalidOperationException("The dedicated active-probe Key is not an active routed Key.");
        }

        var currentGroupId = originalGroupId;
        var remoteGroupMayHaveChanged = false;
        try
        {
            if (currentGroupId != targetGroupId)
            {
                remoteGroupMayHaveChanged = true;
                try
                {
                    await accountClient.UpdateKeyGroupAsync(testKey.Id, targetGroupId, cancellationToken);
                }
                catch (AIHubApiException exception) when (exception.IsAuthenticationFailure)
                {
                    // An account-auth rejection cannot have changed the remote Key group.
                    remoteGroupMayHaveChanged = false;
                    throw;
                }

                currentGroupId = targetGroupId;
                remoteGroupMayHaveChanged = false;
            }

            try
            {
                var measurement = await _upstream.ProbeAsync(
                    new ActiveProbeRequest(
                        configuration.BaseUrl,
                        configuration.ApiKey,
                        configuration.Model,
                        configuration.Platform,
                        targetGroupId),
                    cancellationToken);
                measurement.Validate();
                if (measurement.GroupId != targetGroupId ||
                    !string.Equals(
                        measurement.Platform.Trim(),
                        configuration.Platform.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The active probe returned an unexpected target identity.");
                }

                return new ActiveProbeResult(
                    targetGroupId,
                    true,
                    measurement with { ObservedAt = _utcNow() },
                    "ok");
            }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetRecoverableProbeDetail(exception, cancellationToken, out var detail))
                {
                    throw;
                }

                return new ActiveProbeResult(targetGroupId, false, null, detail);
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
        cancellationToken.ThrowIfCancellationRequested();

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
                if (currentGroupId != group.Id)
                {
                    remoteGroupMayHaveChanged = true;
                    await accountClient.UpdateKeyGroupAsync(testKey.Id, group.Id, cancellationToken);
                    currentGroupId = group.Id;
                    remoteGroupMayHaveChanged = false;
                }

                try
                {
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
                catch (Exception exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryGetRecoverableProbeDetail(exception, cancellationToken, out var detail))
                    {
                        throw;
                    }

                    results.Add(new ActiveProbeResult(group.Id, false, null, detail));
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

    private static bool TryGetRecoverableProbeDetail(
        Exception exception,
        CancellationToken cancellationToken,
        out string detail)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            detail = string.Empty;
            return false;
        }

        if (exception is HttpRequestException
            {
                InnerException: ActiveProbeProtocolException { IsGlobalConfigurationFailure: true }
            })
        {
            detail = string.Empty;
            return false;
        }

        if (exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } ||
            exception is ActiveProbeProtocolException { IsGlobalConfigurationFailure: true })
        {
            detail = string.Empty;
            return false;
        }

        detail = exception switch
        {
            HttpRequestException { StatusCode: null } => "probe-failed",
            HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout } => "http-408",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "http-429",
            HttpRequestException { StatusCode: { } statusCode } when (int)statusCode >= 500 => $"http-{(int)statusCode}",
            ActiveProbeProtocolException protocolException => protocolException.Detail,
            OperationCanceledException => "probe-failed",
            IOException => "probe-failed",
            _ => "probe-failed"
        };
        return exception is ActiveProbeProtocolException or OperationCanceledException or IOException ||
            exception is HttpRequestException
            {
                StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            } ||
            exception is HttpRequestException { StatusCode: { } httpStatus } && (int)httpStatus >= 500;
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
