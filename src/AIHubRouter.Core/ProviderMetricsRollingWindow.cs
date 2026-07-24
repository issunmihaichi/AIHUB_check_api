namespace AIHubRouter.Core;

public sealed record ProviderMetricsSnapshot(
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyDictionary<long, double> UserGroupRates);

public sealed class ProviderMetricsRollingWindow
{
    public static readonly TimeSpan DefaultWindow = RoutingEngine.DefaultMaximumStatusAge;

    private readonly TimeSpan _window;
    private readonly Dictionary<ProviderKey, List<ProviderSample>> _providerSamples = [];
    private readonly Dictionary<long, List<UserRateSample>> _userRateSamples = [];
    private readonly Dictionary<ProviderKey, List<ActiveProbeObservation>> _activeProbeSamples = [];
    private IReadOnlyList<ProviderStatus> _latestProviders = [];
    private IReadOnlyDictionary<long, double> _latestUserGroupRates = new Dictionary<long, double>();
    private DateTimeOffset? _latestProviderObservedAt;
    private DateTimeOffset? _observationWatermark;

    public ProviderMetricsRollingWindow(TimeSpan? window = null)
    {
        _window = window ?? DefaultWindow;
        if (_window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
    }

    public ProviderMetricsSnapshot Observe(
        DateTimeOffset observedAt,
        IEnumerable<ProviderStatus> providers,
        IReadOnlyDictionary<long, double> userGroupRates)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(userGroupRates);

        var currentProviders = providers.Where(provider => provider is not null).ToArray();
        if (_latestProviderObservedAt is null || observedAt >= _latestProviderObservedAt.Value)
        {
            _latestProviders = currentProviders;
            _latestUserGroupRates = new Dictionary<long, double>(userGroupRates);
            _latestProviderObservedAt = observedAt;
        }

        var watermark = AdvanceWatermark(observedAt);
        Prune(watermark);

        var currentGroups = currentProviders.GroupBy(CreateKey).ToArray();
        foreach (var group in currentGroups)
        {
            var key = group.Key;
            if (!_providerSamples.TryGetValue(key, out var samples))
            {
                samples = [];
                _providerSamples[key] = samples;
            }

            samples.AddRange(group.Select(provider => new ProviderSample(observedAt, provider)));
        }

        foreach (var (groupId, rate) in userGroupRates)
        {
            if (!IsNonNegativeFinite(rate))
            {
                continue;
            }

            if (!_userRateSamples.TryGetValue(groupId, out var samples))
            {
                samples = [];
                _userRateSamples[groupId] = samples;
            }

            samples.Add(new UserRateSample(observedAt, rate));
        }

        Prune(watermark);

        return CreateSnapshot(_latestProviders.GroupBy(CreateKey), _latestUserGroupRates);
    }

    public ProviderMetricsSnapshot RecordActiveProbes(IEnumerable<ActiveProbeMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        return RecordActiveProbeObservations(measurements.Select(measurement =>
        {
            measurement.Validate();
            return new ActiveProbeObservation(
                measurement.Platform,
                measurement.GroupId,
                measurement.ObservedAt,
                Success: true,
                measurement.FirstTokenLatencyMs);
        }));
    }

    public ProviderMetricsSnapshot RecordActiveProbeObservations(IEnumerable<ActiveProbeObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var currentObservations = observations.ToArray();
        foreach (var observation in currentObservations)
        {
            observation.Validate();
        }

        foreach (var observation in currentObservations)
        {
            var key = new ProviderKey(observation.Platform.Trim().ToUpperInvariant(), observation.GroupId, string.Empty);
            if (!_activeProbeSamples.TryGetValue(key, out var samples))
            {
                samples = [];
                _activeProbeSamples[key] = samples;
            }

            samples.Add(observation);
        }

        if (currentObservations.Length > 0)
        {
            var watermark = AdvanceWatermark(currentObservations.Max(observation => observation.ObservedAt));
            Prune(watermark);
        }

        var currentGroups = _latestProviders.GroupBy(CreateKey).ToArray();
        return CreateSnapshot(currentGroups, _latestUserGroupRates);
    }

    private ProviderMetricsSnapshot CreateSnapshot(
        IEnumerable<IGrouping<ProviderKey, ProviderStatus>> currentGroups,
        IReadOnlyDictionary<long, double> userGroupRates)
    {
        var groups = currentGroups.ToArray();
        var providersByKey = groups
            .Select(group =>
            {
                var key = group.Key;
                var latest = SelectRepresentative(group);
                IReadOnlyList<ProviderSample> samples = _providerSamples.TryGetValue(key, out var retainedSamples)
                    ? retainedSamples
                    : group.Select(provider => new ProviderSample(
                        _latestProviderObservedAt ?? DateTimeOffset.MinValue,
                        provider)).ToArray();
                return AggregateProvider(latest, samples);
            })
            .OrderBy(provider => provider.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.GroupId)
            .ThenBy(provider => provider.Id, StringComparer.Ordinal)
            .ToArray();

        var rates = new Dictionary<long, double>();
        foreach (var groupId in userGroupRates.Keys.Distinct())
        {
            if (_userRateSamples.TryGetValue(groupId, out var samples) &&
                Median(samples.Select(sample => sample.Rate)) is { } rate)
            {
                rates[groupId] = rate;
            }
        }

        return new ProviderMetricsSnapshot(providersByKey, rates);
    }

    public void Clear()
    {
        _providerSamples.Clear();
        _userRateSamples.Clear();
        _activeProbeSamples.Clear();
        _latestProviders = [];
        _latestUserGroupRates = new Dictionary<long, double>();
        _latestProviderObservedAt = null;
        _observationWatermark = null;
    }

    private ProviderStatus AggregateProvider(ProviderStatus latest, IReadOnlyList<ProviderSample> samples)
    {
        var latencySamples = samples
            .Select(sample => sample.Provider.FirstTokenLatencyMs)
            .Where(value => value is { } latency && IsNonNegativeFinite(latency))
            .Select(value => value!.Value)
            .ToArray();
        var outputSpeedSamples = samples
            .Select(sample => sample.Provider.OutputTokensPerSecond)
            .Where(value => value is { } speed && IsPositiveFinite(speed))
            .Select(value => value!.Value)
            .ToArray();
        var probeSamples = _activeProbeSamples.TryGetValue(CreateKey(latest), out var activeSamples)
            ? activeSamples
            : [];
        var activeLatencySamples = probeSamples
            .Where(sample => sample.Success)
            .Select(sample => sample.FirstTokenLatencyMs)
            .Where(value => value is { } latency && IsNonNegativeFinite(latency))
            .Select(value => value!.Value)
            .ToArray();
        var activeProbeLatency = Median(activeLatencySamples);
        var activeProbeP90Latency = Percentile(activeLatencySamples, 0.90);
        var latestProbe = probeSamples
            .Select((sample, index) => (Sample: sample, Index: index))
            .OrderByDescending(item => item.Sample.ObservedAt)
            .ThenByDescending(item => item.Index)
            .Select(item => item.Sample)
            .FirstOrDefault();
        var providerLatency = Median(latencySamples);
        var providerP90Latency = Percentile(latencySamples, 0.90);
        var successRates = latest.SuccessRates is { } rates
            ? new Dictionary<string, double>(rates, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (Median(samples
                .Select(sample => sample.Provider.SuccessRate6h)
                .Where(value => value is { } rate && double.IsFinite(rate))
                .Select(value => RoutingEngine.NormalizeSuccessRate(value))) is { } successRate)
        {
            successRates["6h"] = successRate;
        }
        else
        {
            successRates.Remove("6h");
        }

        return new ProviderStatus
        {
            Id = latest.Id,
            GroupId = latest.GroupId,
            PlanType = latest.PlanType,
            Platform = latest.Platform,
            PriceMultiplier = Median(samples
                    .Select(sample => sample.Provider.PriceMultiplier)
                    .Where(IsNonNegativeFinite)) ?? latest.PriceMultiplier,
            Available = latest.Available,
            Enabled = latest.Enabled,
            CheckedAt = latest.CheckedAt,
            LastCallEndedAt = MedianTimestamp(samples.Select(sample => sample.Provider.LastCallEndedAt)),
            LastCallAt = MedianTimestamp(samples.Select(sample => sample.Provider.LastCallAt)),
            FirstTokenLatencyMs = providerLatency,
            OutputTokensPerSecond = Median(samples
                .Select(sample => sample.Provider.OutputTokensPerSecond)
                .Where(value => value is { } speed && IsPositiveFinite(speed))
                .Select(value => value!.Value)),
            FirstTokenLatencyP90Ms = providerP90Latency,
            OutputTokensPerSecondP25 = Percentile(outputSpeedSamples, 0.25),
            PerformanceSampleCount = Math.Min(latencySamples.Length, outputSpeedSamples.Length),
            ActiveProbeFirstTokenLatencyMs = activeProbeLatency,
            ActiveProbeFirstTokenLatencyP90Ms = activeProbeP90Latency,
            ActiveProbeHealthy = latestProbe?.Success,
            ActiveProbeCheckedAt = latestProbe?.ObservedAt,
            ActiveProbeSampleCount = probeSamples.Count,
            SuccessRates = successRates,
            ErrorMessage = latest.ErrorMessage,
            WarningReasons = latest.WarningReasons?.Select(reason => new ProviderWarningReason
            {
                Type = reason.Type,
                Message = reason.Message,
                Count = reason.Count
            }).ToList() ?? []
        };
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - _window;
        Prune(_providerSamples, cutoff, sample => sample.ObservedAt);
        Prune(_userRateSamples, cutoff, sample => sample.ObservedAt);
        Prune(_activeProbeSamples, cutoff, sample => sample.ObservedAt);
    }

    private DateTimeOffset AdvanceWatermark(DateTimeOffset observedAt)
    {
        if (_observationWatermark is null || observedAt > _observationWatermark.Value)
        {
            _observationWatermark = observedAt;
        }

        return _observationWatermark.Value;
    }

    private static void Prune<TKey, TSample>(
        Dictionary<TKey, List<TSample>> samplesByKey,
        DateTimeOffset cutoff,
        Func<TSample, DateTimeOffset> getObservedAt)
        where TKey : notnull
    {
        foreach (var (key, samples) in samplesByKey.ToArray())
        {
            samples.RemoveAll(sample => getObservedAt(sample) < cutoff);
            if (samples.Count == 0)
            {
                samplesByKey.Remove(key);
            }
        }
    }

    private static ProviderKey CreateKey(ProviderStatus provider)
    {
        var platform = provider.Platform.Trim().ToUpperInvariant();
        return provider.GroupId is { } groupId
            ? new ProviderKey(platform, groupId, string.Empty)
            : new ProviderKey(platform, null, provider.Id);
    }

    private static ProviderStatus SelectRepresentative(IEnumerable<ProviderStatus> providers) =>
        providers
            .OrderByDescending(provider => provider.CheckedAt ?? DateTimeOffset.MinValue)
            .ThenBy(provider => provider.Id, StringComparer.Ordinal)
            .ThenBy(provider => provider.PlanType, StringComparer.Ordinal)
            .First();

    private static double? Median(IEnumerable<double> values)
    {
        var ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        var upper = ordered.Length / 2;
        if (ordered.Length % 2 != 0)
        {
            return ordered[upper];
        }

        var lower = ordered[upper - 1];
        return lower + (ordered[upper] - lower) / 2;
    }

    private static double? Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (ordered.Length == 0 || !double.IsFinite(percentile) || percentile is < 0 or > 1)
        {
            return null;
        }

        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static DateTimeOffset? MedianTimestamp(IEnumerable<DateTimeOffset?> values)
    {
        var ordered = values
            .Where(value => value.HasValue)
            .Select(value => value!.Value.UtcDateTime.Ticks)
            .OrderBy(value => value)
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        var upper = ordered.Length / 2;
        var ticks = ordered.Length % 2 != 0
            ? ordered[upper]
            : ordered[upper - 1] + (ordered[upper] - ordered[upper - 1]) / 2;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static bool IsNonNegativeFinite(double value) => double.IsFinite(value) && value >= 0;

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private readonly record struct ProviderKey(string Platform, long? GroupId, string FallbackId);

    private sealed record ProviderSample(DateTimeOffset ObservedAt, ProviderStatus Provider);

    private sealed record UserRateSample(DateTimeOffset ObservedAt, double Rate);
}
