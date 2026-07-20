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
    IReadOnlyList<RouteAuditKey> Keys);

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
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
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
