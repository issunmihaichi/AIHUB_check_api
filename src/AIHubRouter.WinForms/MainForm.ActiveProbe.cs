using System.ComponentModel;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{
    private long? _keyContextMenuKeyId;

    private bool IsActiveProbeKey(long keyId) =>
        _activeProbeCheck.Checked && _activeProbeKeyId == keyId;

    private void HandleActiveProbeEnabledChanged()
    {
        if (_applyingRoutingSettings)
        {
            return;
        }

        ApplyActiveProbeConfigurationChanged();
        if (_activeProbeCheck.Checked && !TryCreateActiveProbeConfiguration(out _, out var error))
        {
            SetStatus(error, success: false);
        }
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

        if (!_persistCredentialsCheck.Checked || _keys.Count == 0)
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
            error = "请先设置要检查的 Key ID。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_activeProbeApiKey))
        {
            error = "请在健康检查设置中填写该 Key 的 API Key。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_activeProbeModel))
        {
            error = "请在健康检查设置中填写测试模型。";
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

    private async Task ExecuteActiveProbeAsync(
        bool manual,
        CancellationToken externalCancellationToken = default)
    {
        if (_busy)
        {
            if (manual)
            {
                SetStatus("当前有任务正在运行，稍后再执行健康检查。", success: false);
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
                SetStatus("Key 健康检查需要有效的 AIHub 登录认证，用于读取该 Key 当前所在分组。", success: false);
            }

            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            externalCancellationToken);
        _activeProbeCancellation = cancellation;
        _activeProbeTimer.Stop();
        SetBusy(true, "正在检查指定 Key 是否存活...");
        try
        {
            await RunAuthenticatedAsync(async accountClient =>
            {
                using var upstream = new OpenAiStreamingProbeClient();
                var service = new ActiveProviderProbeService(upstream);
                var result = await service.CheckSelectedKeyAsync(
                    accountClient,
                    configuration!,
                    cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var status = result.Success && result.Measurement is { } measurement
                    ? $"Key {configuration!.TestKeyId} 存活，当前分组 {result.GroupId}，首 Token {measurement.FirstTokenLatencyMs:0} ms。"
                    : $"Key {configuration!.TestKeyId} 健康检查失败（当前分组 {result.GroupId}）。";
                SetStatus(status, success: result.Success);
            }, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                SetStatus("本轮健康检查已取消。", success: true);
            }
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
        finally
        {
            if (ReferenceEquals(_activeProbeCancellation, cancellation))
            {
                _activeProbeCancellation = null;
            }

            SetBusy(false);
            if (!_shutdown.IsCancellationRequested)
            {
                UpdateActiveProbeTimer();
            }
        }
    }

    private void CancelActiveProbe()
    {
        var cancellation = _activeProbeCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        _cancelActiveProbeButton.Enabled = false;
        _statusLabel.Text = "正在取消健康检查...";
        _statusLabel.ForeColor = _activePalette.MutedText;
        cancellation.Cancel();
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
            ? $"已设为健康检查 Key（ID {_keyContextMenuKeyId.Value}）"
            : $"设为健康检查 Key（ID {_keyContextMenuKeyId.Value}）";
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
        SetStatus($"已设置健康检查 Key ID {_keyContextMenuKeyId.Value}；请在健康检查设置中填写其 API Key 和测试模型。", success: true);
    }

}
