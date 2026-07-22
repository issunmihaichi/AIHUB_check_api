namespace AIHubRouter.Core;

public enum ProviderBlockReason
{
    None,
    GroupId,
    Pattern
}

public sealed class ProviderBlocklist
{
    private readonly HashSet<long> _blockedGroupIds;
    private readonly string[] _blockedNodePatterns;

    public static ProviderBlocklist Empty { get; } = new([], []);

    public ProviderBlocklist(
        IEnumerable<long>? blockedGroupIds,
        IEnumerable<string>? blockedNodePatterns)
    {
        _blockedGroupIds = (blockedGroupIds ?? [])
            .Where(groupId => groupId > 0)
            .ToHashSet();
        _blockedNodePatterns = (blockedNodePatterns ?? [])
            .Select(pattern => pattern?.Trim())
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public IReadOnlySet<long> BlockedGroupIds => _blockedGroupIds;

    public IReadOnlyList<string> BlockedNodePatterns => _blockedNodePatterns;

    public bool IsBlocked(ProviderStatus provider, GroupInfo? group = null) =>
        GetBlockingReason(provider, group) != ProviderBlockReason.None;

    public ProviderBlockReason GetBlockingReason(ProviderStatus provider, GroupInfo? group = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.GroupId is { } groupId && _blockedGroupIds.Contains(groupId))
        {
            return ProviderBlockReason.GroupId;
        }

        if (_blockedNodePatterns.Length == 0)
        {
            return ProviderBlockReason.None;
        }

        var fields = new[]
        {
            provider.Id,
            provider.PlanType,
            provider.Platform,
            group?.Name ?? string.Empty
        };
        return _blockedNodePatterns.Any(pattern => fields.Any(field =>
            field.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            ? ProviderBlockReason.Pattern
            : ProviderBlockReason.None;
    }
}
