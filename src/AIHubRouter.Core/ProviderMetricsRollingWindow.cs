namespace AIHubRouter.Core;

public sealed record ProviderMetricsSnapshot(
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyDictionary<long, double> UserGroupRates);

public sealed class ProviderMetricsRollingWindow
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(20);

    private readonly TimeSpan _window;
    private readonly Dictionary<ProviderKey, List<ProviderSample>> _providerSamples = [];
    private readonly Dictionary<long, List<UserRateSample>> _userRateSamples = [];

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
        Prune(observedAt);

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

        Prune(observedAt);

        var providersByKey = currentGroups
            .Select(group =>
            {
                var key = group.Key;
                return AggregateProvider(SelectRepresentative(group), _providerSamples[key]);
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
        var successRates = latest.SuccessRates is { } rates
            ? new Dictionary<string, double>(rates, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (Median(samples
                .Select(sample => sample.Provider.SuccessRate6h)
                .Where(value => value is { } rate && IsUnitIntervalFinite(rate))
                .Select(value => value!.Value)) is { } successRate)
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
            Available = BooleanMedian(samples.Select(sample => sample.Provider.Available)),
            Enabled = BooleanMedian(samples.Select(sample => sample.Provider.Enabled)),
            CheckedAt = MedianTimestamp(samples.Select(sample => sample.Provider.CheckedAt)),
            LastCallEndedAt = MedianTimestamp(samples.Select(sample => sample.Provider.LastCallEndedAt)),
            LastCallAt = MedianTimestamp(samples.Select(sample => sample.Provider.LastCallAt)),
            FirstTokenLatencyMs = Median(samples
                .Select(sample => sample.Provider.FirstTokenLatencyMs)
                .Where(value => value is { } latency && IsNonNegativeFinite(latency))
                .Select(value => value!.Value)),
            OutputTokensPerSecond = Median(samples
                .Select(sample => sample.Provider.OutputTokensPerSecond)
                .Where(value => value is { } speed && IsPositiveFinite(speed))
                .Select(value => value!.Value)),
            FirstTokenLatencyP90Ms = Percentile(latencySamples, 0.90),
            OutputTokensPerSecondP25 = Percentile(outputSpeedSamples, 0.25),
            PerformanceSampleCount = Math.Min(latencySamples.Length, outputSpeedSamples.Length),
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
            .OrderBy(provider => provider.Id, StringComparer.Ordinal)
            .ThenBy(provider => provider.PlanType, StringComparer.Ordinal)
            .First();

    private static bool BooleanMedian(IEnumerable<bool> values) =>
        (Median(values.Select(value => value ? 1d : 0d)) ?? 0) >= 0.5;

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

    private static bool IsUnitIntervalFinite(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 1;

    private readonly record struct ProviderKey(string Platform, long? GroupId, string FallbackId);

    private sealed record ProviderSample(DateTimeOffset ObservedAt, ProviderStatus Provider);

    private sealed record UserRateSample(DateTimeOffset ObservedAt, double Rate);
}
