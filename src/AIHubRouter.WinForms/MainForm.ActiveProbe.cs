using System.ComponentModel;
using System.Drawing;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{
    private long? _keyContextMenuKeyId;
    private bool _applyingActiveProbeSettings;

    private bool IsActiveProbeKey(long keyId) =>
        _activeProbeCheck.Checked && _activeProbeKeyId == keyId;

    private void HandleActiveProbeEnabledChanged()
    {
        if (_applyingActiveProbeSettings)
        {
            return;
        }

        ApplyActiveProbeConfigurationChanged();
    }

    private void ApplyActiveProbeConfigurationChanged()
    {
        InvalidateRoutingService();
        if (_keys.Count > 0)
        {
            ApplyKeys(_keys);
        }
        else
        {
            RecalculateCandidate();
        }

        if (!_persistCredentialsCheck.Checked)
        {
            SaveCurrentSettings(showStatus: false);
        }

        UpdateActiveProbeTimer();
    }

    private void UpdateActiveProbeTimer()
    {
        _activeProbeTimer.Stop();
        if (_activeProbeCheck.Checked && TryCreateActiveProbeConfiguration(out _, out _))
        {
            _activeProbeTimer.Start();
        }
    }

    private bool TryCreateActiveProbeConfiguration(
        out ActiveProbeConfiguration? configuration,
        out string error)
    {
        configuration = null;
        if (_activeProbeKeyId is not > 0)
        {
            error = "请先设置专用测速 Key ID。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_activeProbeApiKey))
        {
            error = "请在测速设置中填写该专用 Key 的 API Key。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_activeProbeModel))
        {
            error = "请在测速设置中填写测试模型。";
            return false;
        }

        try
        {
            configuration = new ActiveProbeConfiguration(
                _baseUrlText.Text,
                _activeProbeApiKey,
                _activeProbeModel,
                _activeProbeKeyId.Value,
                _platformCombo.SelectedItem?.ToString() ?? "openai");
            configuration.Validate();
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private async Task ExecuteActiveProbeAsync(bool manual)
    {
        if (_busy)
        {
            if (manual)
            {
                SetStatus("当前有任务正在运行，稍后再执行测速。", success: false);
            }

            return;
        }

        if (!TryCreateActiveProbeConfiguration(out var configuration, out var error))
        {
            if (manual || _activeProbeCheck.Checked)
            {
                SetStatus(error, success: false);
            }

            UpdateActiveProbeTimer();
            return;
        }

        if (!HasCredentials())
        {
            if (manual)
            {
                SetStatus("主动测速需要有效的 AIHub 登录认证，才能临时切换专用测速 Key 的分组。", success: false);
            }

            return;
        }

        SetBusy(true, "正在通过专用 Key 测量各节点的首 Token 延迟...");
        try
        {
            await RunAuthenticatedAsync(async accountClient =>
            {
                await RefreshDataCoreAsync(accountClient, loadAccountData: true, _shutdown.Token);

                using var upstream = new OpenAiStreamingProbeClient();
                var service = new ActiveProviderProbeService(upstream);
                var result = await service.RunCycleAsync(
                    accountClient,
                    configuration!,
                    _summary?.Apis ?? [],
                    _groups,
                    _providerBlocklist,
                    _shutdown.Token);
                var measurements = result.Results
                    .Where(item => item.Success && item.Measurement is not null)
                    .Select(item => item.Measurement!)
                    .ToArray();

                if (measurements.Length > 0)
                {
                    var metrics = _providerMetrics.RecordActiveProbes(measurements);
                    var currentSummary = _summary;
                    _summary = new MonitorSummary
                    {
                        Apis = metrics.Providers.ToList(),
                        GeneratedAt = currentSummary?.GeneratedAt,
                        MonitoringActive = currentSummary?.MonitoringActive ?? false
                    };
                    _userRates = metrics.UserGroupRates;
                    RecalculateCandidate();
                }

                var failed = result.Results.Count(item => !item.Success);
                var status = result.Results.Count == 0
                    ? "没有符合条件的可测速节点。"
                    : $"主动测速完成：{measurements.Length} 个节点已更新，{failed} 个节点本轮失败。";
                SetStatus(status, success: measurements.Length > 0 && result.TestKeyRestored);
            });
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowActiveProbeSettings()
    {
        using var dialog = new Form
        {
            Text = "主动测速设置",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(610, 330)
        };
        var enabled = new CheckBox
        {
            Text = "每 60 秒主动测速当前平台的可用节点",
            AutoSize = true,
            Checked = _activeProbeCheck.Checked
        };
        var keyId = new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 1_000_000_000,
            Value = Math.Clamp(_activeProbeKeyId ?? 0, 0, 1_000_000_000)
        };
        var apiKey = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            PlaceholderText = "专用测速 Key 的 sk-... 值",
            Text = _activeProbeApiKey
        };
        var model = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "例如 gpt-4.1-mini",
            Text = _activeProbeModel
        };
        var showApiKey = new CheckBox { Text = "显示 API Key", AutoSize = true };
        showApiKey.CheckedChanged += (_, _) => apiKey.UseSystemPasswordChar = !showApiKey.Checked;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(enabled, 0, 0);
        layout.SetColumnSpan(enabled, 2);
        AddProbeDialogRow(layout, 1, "专用 Key ID", keyId);

        var apiKeyPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 3)
        };
        apiKeyPanel.Controls.Add(apiKey);
        apiKeyPanel.Controls.Add(showApiKey);
        layout.Controls.Add(CreateProbeDialogLabel("上游 API Key"), 0, 2);
        layout.Controls.Add(apiKeyPanel, 1, 2);
        AddProbeDialogRow(layout, 3, "测试模型", model);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "该 Key 会被逐个临时切换至节点分组，发送 max_tokens=1 的流式请求后恢复原分组。它不会参与普通路由；右键 Key 表中的项目也可快速填入 Key ID。\r\n\r\n开启常态化保存认证后，测速 API Key 与登录凭据一起使用 Windows DPAPI 加密保存。",
            ForeColor = Color.FromArgb(75, 85, 95)
        }, 0, 4);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 4)!, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        var save = new Button { Text = "应用", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        NativeThemeManager.Apply(dialog, _activePalette);

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _applyingActiveProbeSettings = true;
        try
        {
            _activeProbeKeyId = (long)keyId.Value;
            _activeProbeApiKey = apiKey.Text.Trim();
            _activeProbeModel = model.Text.Trim();
            _activeProbeCheck.Checked = enabled.Checked;
        }
        finally
        {
            _applyingActiveProbeSettings = false;
        }

        ApplyActiveProbeConfigurationChanged();
        SetStatus(_activeProbeCheck.Checked && !TryCreateActiveProbeConfiguration(out _, out var error)
            ? error
            : "主动测速设置已保存。",
            success: !_activeProbeCheck.Checked || TryCreateActiveProbeConfiguration(out _, out _));
    }

    private void HandleKeyGridMouseDown(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = _keyGrid.HitTest(eventArgs.X, eventArgs.Y);
        _keyContextMenuKeyId = hit.RowIndex >= 0 &&
            _keyGrid.Rows[hit.RowIndex].DataBoundItem is KeyGridRow row
            ? row.Id
            : null;
    }

    private void HandleKeyGridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Right || eventArgs.RowIndex < 0)
        {
            return;
        }

        _keyGrid.ClearSelection();
        var row = _keyGrid.Rows[eventArgs.RowIndex];
        row.Selected = true;
        if (eventArgs.ColumnIndex >= 0)
        {
            _keyGrid.CurrentCell = row.Cells[eventArgs.ColumnIndex];
        }
    }

    private void HandleKeyContextMenuOpening(object? sender, CancelEventArgs eventArgs)
    {
        if (_keyContextMenuKeyId is not > 0)
        {
            eventArgs.Cancel = true;
            return;
        }

        _setActiveProbeKeyMenuItem.Text = _activeProbeKeyId == _keyContextMenuKeyId
            ? $"已设为测速 Key（ID {_keyContextMenuKeyId.Value}）"
            : $"设为测速专用 Key（ID {_keyContextMenuKeyId.Value}）";
        _setActiveProbeKeyMenuItem.Enabled = _activeProbeKeyId != _keyContextMenuKeyId;
    }

    private void SetCurrentKeyAsActiveProbeKey()
    {
        if (_keyContextMenuKeyId is not > 0)
        {
            return;
        }

        _activeProbeKeyId = _keyContextMenuKeyId;
        ApplyActiveProbeConfigurationChanged();
        SetStatus($"已设置测速专用 Key ID {_keyContextMenuKeyId.Value}；请在测速设置中填写其 API Key 和测试模型。", success: true);
    }

    private static Label CreateProbeDialogLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(55, 65, 75)
    };

    private static void AddProbeDialogRow(TableLayoutPanel table, int row, string label, Control input)
    {
        input.Margin = new Padding(0, 3, 0, 3);
        table.Controls.Add(CreateProbeDialogLabel(label), 0, row);
        table.Controls.Add(input, 1, row);
    }
}
