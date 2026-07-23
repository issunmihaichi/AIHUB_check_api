using System.Collections.Immutable;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{
    private RoutingUiSettings CaptureRoutingUiSettings()
    {
        return new RoutingUiSettings
        {
            MinimumSuccessPercent = (int)_minimumSuccessInput.Value,
            PollingIntervalSeconds = (int)_intervalInput.Value,
            AccountCacheSeconds = (int)_accountCacheInput.Value,
            AutoRoute = _autoRouteCheck.Checked,
            DurationCategory = CurrentDurationCategory(),
            BalancedSoftDeadlineSeconds = _balancedDeadlineSoftSeconds,
            BalancedExpectedOutputTokens = _balancedExpectedOutputTokens,
            ActiveProbeEnabled = _activeProbeCheck.Checked,
            ActiveProbeKeyId = _activeProbeKeyId,
            ActiveProbeModel = _activeProbeModel,
            Theme = _themePreference,
            SmoothRendering = _verticalSyncCheck.Checked,
            BlockedGroupIds = _providerBlocklist.BlockedGroupIds.ToImmutableArray(),
            BlockedNodePatterns = _providerBlocklist.BlockedNodePatterns.ToImmutableArray()
        }.Normalize();
    }

    private void ShowRoutingSettingsDialog()
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new RoutingSettingsDialog(
            CaptureRoutingUiSettings(),
            _activeProbeApiKey,
            _activePalette,
            CurrentRoutingMode() == RoutingMode.Balanced,
            ApplyRoutingUiSettingsAsync,
            cancellationToken => ExecuteActiveProbeAsync(manual: true, cancellationToken));
        dialog.ShowDialog(this);
    }

    private async Task<bool> ApplyRoutingUiSettingsAsync(RoutingSettingsDraft draft)
    {
        if (_busy)
        {
            SetStatus("当前有任务正在运行，请稍后再应用设置。", success: false);
            return false;
        }

        var settings = draft.Settings.Normalize();
        var activeProbeApiKey = draft.ActiveProbeApiKey.Trim();
        if (settings.ActiveProbeEnabled &&
            (settings.ActiveProbeKeyId is not > 0 ||
                string.IsNullOrWhiteSpace(activeProbeApiKey) ||
                string.IsNullOrWhiteSpace(settings.ActiveProbeModel)))
        {
            SetStatus("启用健康检查前必须填写有效的 Key ID、API Key 和测试模型。", success: false);
            return false;
        }

        var autoRouteChanged = _autoRouteCheck.Checked != settings.AutoRoute;
        _applyingRoutingSettings = true;
        try
        {
            _minimumSuccessInput.Value = settings.MinimumSuccessPercent;
            _intervalInput.Value = settings.PollingIntervalSeconds;
            _accountCacheInput.Value = settings.AccountCacheSeconds;
            _autoRouteCheck.Checked = settings.AutoRoute;

            _durationCategory = settings.DurationCategory;
            _balancedDeadlineSoftSeconds = settings.BalancedSoftDeadlineSeconds;
            _balancedExpectedOutputTokens = settings.BalancedExpectedOutputTokens;
            _balancedSoftDeadlineInput.Value = (decimal)_balancedDeadlineSoftSeconds;
            _balancedExpectedOutputInput.Value = (decimal)_balancedExpectedOutputTokens;

            _activeProbeCheck.Checked = settings.ActiveProbeEnabled;
            _activeProbeKeyId = settings.ActiveProbeKeyId;
            _activeProbeApiKey = activeProbeApiKey;
            _activeProbeModel = settings.ActiveProbeModel.Trim();
            _providerBlocklist = new ProviderBlocklist(settings.BlockedGroupIds, settings.BlockedNodePatterns);

            _themeCombo.SelectedIndex = settings.Theme switch
            {
                WinFormsTheme.Light => 1,
                WinFormsTheme.Dark => 2,
                _ => 0
            };
            _themePreference = settings.Theme;
            _verticalSyncCheck.Checked = settings.SmoothRendering;

            if (_keys.Count > 0)
            {
                ApplyKeys(_keys);
            }

            UpdateTimerInterval();
            UpdateActiveProbeTimer();
            ApplySelectedTheme();
            ApplySmoothRendering(showStatus: false);
            InvalidateRoutingService();
            RecalculateCandidate();
            // Persistence is completed after the optional routing cycle below.
        }
        catch (Exception exception)
        {
            SetStatus($"应用路由设置失败：{SafeErrorPresentation.GetMessage(exception)}", success: false);
            return false;
        }
        finally
        {
            _applyingRoutingSettings = false;
        }

        if (autoRouteChanged)
        {
            _suppressRoutingPersistence = true;
            try
            {
                await ToggleAutoRoutingAsync();
            }
            finally
            {
                _suppressRoutingPersistence = false;
            }
        }

        var saved = SaveCurrentSettings(showStatus: false);
        SetStatus(
            saved ? "路由设置已应用。" : "路由设置已应用，但本地保存失败。",
            success: saved);
        return saved;
    }
}
