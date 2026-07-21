using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

public sealed record RouteAuditCandidate(
    long GroupId,
    double Multiplier,
    double? LatencyMs,
    double Score,
    bool Recommended);

public sealed record RouteAuditKey(
    long KeyId,
    bool Changed,
    bool Success,
    string? ErrorCode);

public sealed record RouteAuditEntry(
    DateTimeOffset Timestamp,
    RoutingMode Mode,
    RouteDecisionReason Reason,
    long? CurrentGroupId,
    long? TargetGroupId,
    bool DryRun,
    IReadOnlyList<RouteAuditCandidate> Candidates,
    IReadOnlyList<RouteAuditKey> Keys)
{
    public AdaptivePreference? EffectivePreference { get; init; }
    public TaskDurationCategory? DurationCategory { get; init; }
    public double? CurrentIntervalSeconds { get; init; }
    public AdaptiveDecisionReason? AdaptiveReason { get; init; }
    public double? PenaltyUsd { get; init; }
    public double? NetSavingUsd { get; init; }
    public double? OldCompletionSeconds { get; init; }
    public double? NewCompletionSeconds { get; init; }
    public double? DeltaSeconds { get; init; }
}

public sealed class AuditLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;

    public AuditLogWriter(string path, long maximumBytes = 5 * 1024 * 1024, int retainedFiles = 5)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Audit log path is required.", nameof(path));
        }

        _path = Path.GetFullPath(path);
        _maximumBytes = Math.Clamp(maximumBytes, 128, 1024L * 1024 * 1024);
        _retainedFiles = Math.Clamp(retainedFiles, 1, 30);
        EnsureDirectory();
    }

    public void Write(RouteAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var safeEntry = entry with
        {
            CurrentIntervalSeconds = NormalizeNonNegative(entry.CurrentIntervalSeconds),
            PenaltyUsd = NormalizeNonNegative(entry.PenaltyUsd),
            NetSavingUsd = NormalizeFinite(entry.NetSavingUsd),
            OldCompletionSeconds = NormalizeNonNegative(entry.OldCompletionSeconds),
            NewCompletionSeconds = NormalizeNonNegative(entry.NewCompletionSeconds),
            DeltaSeconds = NormalizeFinite(entry.DeltaSeconds),
            Candidates = entry.Candidates.Select(candidate => candidate with
            {
                Multiplier = NormalizeFinite(candidate.Multiplier),
                LatencyMs = NormalizeLatency(candidate.LatencyMs),
                Score = NormalizeFinite(candidate.Score)
            }).ToArray()
        };
        var line = JsonSerializer.Serialize(safeEntry, JsonOptions) + Environment.NewLine;
        RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
        using var stream = new FileStream(_path, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan
        });
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(line);
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static double NormalizeFinite(double value) => double.IsFinite(value) ? value : 0;

    private static double? NormalizeFinite(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static double? NormalizeNonNegative(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

    private static double? NormalizeLatency(double? value) =>
        value is { } latency && double.IsFinite(latency) && latency >= 0 ? latency : null;

    private void RotateIfNeeded(int incomingBytes)
    {
        var current = new FileInfo(_path);
        if (!current.Exists || current.Length + incomingBytes <= _maximumBytes)
        {
            return;
        }

        for (var index = _retainedFiles; index >= 2; index--)
        {
            var source = $"{_path}.{index - 1}";
            var destination = $"{_path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        File.Move(_path, _path + ".1", overwrite: true);
    }
}
