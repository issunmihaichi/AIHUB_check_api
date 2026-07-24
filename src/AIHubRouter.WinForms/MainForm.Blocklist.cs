using System.ComponentModel;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{
    private long? _providerContextMenuGroupId;
    private bool _providerContextMenuIsRoutable;

    private void HandleProviderGridMouseDown(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = _providerGrid.HitTest(eventArgs.X, eventArgs.Y);
        if (hit.RowIndex >= 0 &&
            _providerGrid.Rows[hit.RowIndex].DataBoundItem is ProviderGridRow row)
        {
            _providerContextMenuGroupId = row.GroupId;
            _providerContextMenuIsRoutable = row.IsRoutable;
        }
        else
        {
            _providerContextMenuGroupId = null;
            _providerContextMenuIsRoutable = false;
        }
    }

    private void HandleProviderGridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Right || eventArgs.RowIndex < 0)
        {
            return;
        }

        _providerGrid.ClearSelection();
        var row = _providerGrid.Rows[eventArgs.RowIndex];
        row.Selected = true;
        if (eventArgs.ColumnIndex >= 0)
        {
            _providerGrid.CurrentCell = row.Cells[eventArgs.ColumnIndex];
        }
    }

    private void HandleProviderContextMenuOpening(object? sender, CancelEventArgs eventArgs)
    {
        if (_providerContextMenuGroupId is not > 0)
        {
            eventArgs.Cancel = true;
            return;
        }

        _toggleGroupBlocklistMenuItem.Text = _providerBlocklist.BlockedGroupIds.Contains(_providerContextMenuGroupId.Value)
            ? $"取消拉黑分组 {_providerContextMenuGroupId.Value}"
            : $"拉黑分组 {_providerContextMenuGroupId.Value}";
        _toggleGroupBlocklistMenuItem.Enabled = !_busy;
        var isForced = _forcedGroupId == _providerContextMenuGroupId.Value;
        _forceGroupMenuItem.Text = isForced
            ? $"取消强制使用分组 {_providerContextMenuGroupId.Value}"
            : $"强制使用分组 {_providerContextMenuGroupId.Value}，直到不可用";
        _forceGroupMenuItem.Enabled = !_busy &&
            (isForced || (_providerContextMenuIsRoutable && CanApplyForcedRoute()));
    }

    private async Task ToggleForcedGroupAsync()
    {
        if (_providerContextMenuGroupId is not > 0 || _busy)
        {
            return;
        }

        var groupId = _providerContextMenuGroupId.Value;
        long? nextForcedGroupId = _forcedGroupId == groupId ? null : groupId;
        if (nextForcedGroupId is not null &&
            (!_providerContextMenuIsRoutable || !CanApplyForcedRoute()))
        {
            SetStatus("请先完成登录、勾选一个可用路由 Key，再强制使用当前可路由分组。", success: false);
            return;
        }

        try
        {
            var storageDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIHubRouter");
            var store = new JsonRouteStateStore(storageDirectory);
            if (!TryPersistForcedGroupState(store, nextForcedGroupId))
            {
                return;
            }
            _forcedGroupId = nextForcedGroupId;
            RecalculateCandidate();

            if (!HasCredentials())
            {
                SetStatus(nextForcedGroupId is null
                    ? "已取消强制分组。"
                    : $"已固定分组 {groupId}；登录后将立即应用。", success: true);
                return;
            }

            await ExecuteRoutingCycleAsync(forceAccountRefresh: true);
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
    }

    private void ToggleCurrentGroupBlocklist()
    {
        if (_providerContextMenuGroupId is not > 0 || _busy)
        {
            return;
        }

        var blockedGroupIds = _providerBlocklist.BlockedGroupIds.ToHashSet();
        var removed = blockedGroupIds.Remove(_providerContextMenuGroupId.Value);
        if (!removed)
        {
            blockedGroupIds.Add(_providerContextMenuGroupId.Value);
        }

        ApplyProviderBlocklist(new ProviderBlocklist(blockedGroupIds, _providerBlocklist.BlockedNodePatterns), showStatus: true);
    }

    private void ApplyProviderBlocklist(ProviderBlocklist blocklist, bool showStatus)
    {
        if (_busy)
        {
            return;
        }

        _providerBlocklist = blocklist;
        InvalidateRoutingService();
        RecalculateCandidate();
        var saved = SaveCurrentSettings(showStatus: false);
        if (showStatus)
        {
            var message = saved
                ? $"黑名单已更新：{blocklist.BlockedGroupIds.Count} 个分组，{blocklist.BlockedNodePatterns.Count} 条规则。"
                : "黑名单已更新，但本地保存失败。";
            SetStatus(message, saved);
        }
    }

    private bool CanApplyForcedRoute() =>
        _hasAuthenticatedAccountData &&
        HasCredentials() &&
        CurrentKeyRows().Any(row =>
            row.Selected &&
            !row.IsProbeKey &&
            row.Status.Equals("active", StringComparison.OrdinalIgnoreCase));

    private bool TryPersistForcedGroupState(JsonRouteStateStore store, long? forcedGroupId)
    {
        try
        {
            var state = store.Load();
            store.Save(state.ReleaseForcedGroup() with { ForcedGroupId = forcedGroupId });
            return true;
        }
        catch (Exception exception)
        {
            SetStatus($"强制分组状态保存失败：{SafeErrorPresentation.GetMessage(exception)}", success: false);
            return false;
        }
    }

    private static string BlockReasonText(ProviderBlockReason reason) => reason switch
    {
        ProviderBlockReason.GroupId => "分组",
        ProviderBlockReason.Pattern => "规则",
        _ => string.Empty
    };
}
