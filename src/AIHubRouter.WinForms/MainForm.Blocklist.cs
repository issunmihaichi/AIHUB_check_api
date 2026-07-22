using System.ComponentModel;
using System.Drawing;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{
    private long? _providerContextMenuGroupId;

    private void HandleProviderGridMouseDown(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = _providerGrid.HitTest(eventArgs.X, eventArgs.Y);
        _providerContextMenuGroupId = hit.RowIndex >= 0 &&
            _providerGrid.Rows[hit.RowIndex].DataBoundItem is ProviderGridRow row
            ? row.GroupId
            : null;
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
    }

    private void ToggleCurrentGroupBlocklist()
    {
        if (_providerContextMenuGroupId is not > 0)
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

    private void ShowBlocklistDialog()
    {
        using var dialog = new Form
        {
            Text = "路由黑名单",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(560, 390)
        };
        var groupIdsInput = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "例如：12, 34",
            Text = string.Join(", ", _providerBlocklist.BlockedGroupIds.Order())
        };
        var rulesInput = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Text = string.Join(Environment.NewLine, _providerBlocklist.BlockedNodePatterns)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label
        {
            Text = "拉黑分组 ID（逗号、空格或换行分隔）",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 5)
        }, 0, 0);
        layout.Controls.Add(groupIdsInput, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "匹配规则（每行一条，不区分大小写）\r\n匹配节点 ID、方案、平台和分组名称。",
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 5)
        }, 0, 2);
        layout.Controls.Add(rulesInput, 0, 3);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0)
        };
        var save = new Button { Text = "应用", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            if (!TryParseBlockedGroupIds(groupIdsInput.Text, out var groupIds))
            {
                MessageBox.Show(dialog, "分组 ID 只能为正整数，请删除无效内容。", "路由黑名单",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ApplyProviderBlocklist(new ProviderBlocklist(groupIds, rulesInput.Lines), showStatus: true);
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 4);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        NativeThemeManager.Apply(dialog, _activePalette);
        dialog.ShowDialog(this);
    }

    private void ApplyProviderBlocklist(ProviderBlocklist blocklist, bool showStatus)
    {
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

    private static bool TryParseBlockedGroupIds(string value, out long[] groupIds)
    {
        var values = new List<long>();
        var tokens = value.Split([',', ';', '\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (!long.TryParse(token, out var groupId) || groupId <= 0)
            {
                groupIds = [];
                return false;
            }

            values.Add(groupId);
        }

        groupIds = values.Distinct().Order().ToArray();
        return true;
    }

    private static string BlockReasonText(ProviderBlockReason reason) => reason switch
    {
        ProviderBlockReason.GroupId => "分组",
        ProviderBlockReason.Pattern => "规则",
        _ => string.Empty
    };
}
