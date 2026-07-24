using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{

    private PersistentAppSettings BuildPersistentSettings()
    {
        return new PersistentAppSettings
        {
            PersistCredentials = _persistCredentialsCheck.Checked,
            BaseUrl = _baseUrlText.Text.Trim(),
            Platform = _platformCombo.SelectedItem?.ToString() ?? "openai",
            RoutingMode = CurrentRoutingMode(),
            DurationCategory = CurrentDurationCategory(),
            BalancedDeadlineSoftSeconds = _balancedDeadlineSoftSeconds,
            BalancedExpectedOutputTokens = _balancedExpectedOutputTokens,
            MinimumSuccessPercent = (int)_minimumSuccessInput.Value,
            PollingIntervalSeconds = (int)_intervalInput.Value,
            AccountCacheSeconds = (int)_accountCacheInput.Value,
            SmoothRendering = _verticalSyncCheck.Checked,
            Theme = _themePreference,
            KeySelectionInitialized = _keySelectionInitialized,
            SelectedKeyIds = _savedSelectedKeyIds.Order().ToArray(),
            BlockedGroupIds = _providerBlocklist.BlockedGroupIds.Order().ToArray(),
            BlockedNodePatterns = _providerBlocklist.BlockedNodePatterns.ToArray(),
            ActiveProbeEnabled = _activeProbeCheck.Checked,
            ActiveProbeKeyId = _activeProbeKeyId,
            ActiveProbeModel = _activeProbeModel,
            ActiveProbeIntervalSeconds = 90
        };
    }

    private RoutingMode CurrentRoutingMode()
    {
        return _routingModeCombo.SelectedIndex switch
        {
            1 => RoutingMode.Balanced,
            2 => RoutingMode.Speed,
            _ => RoutingMode.Economy
        };
    }

    private string ModeDisplayName() => _routingModeCombo.SelectedItem?.ToString() ?? "经济";

    private TaskDurationCategory CurrentDurationCategory()
    {
        return _durationCategory is TaskDurationCategory.Short or
            TaskDurationCategory.Medium or TaskDurationCategory.Long
            ? _durationCategory
            : TaskDurationCategory.Medium;
    }

    private void ApplySelectedTheme()
    {
        _themePreference = _themeCombo.SelectedIndex switch
        {
            1 => WinFormsTheme.Light,
            2 => WinFormsTheme.Dark,
            _ => WinFormsTheme.System
        };
        _activePalette = NativeThemeManager.Resolve(_themePreference);
        NativeThemeManager.Apply(this, _activePalette);
    }

    private void UpdateTimerInterval()
    {
        _autoTimer.Interval = checked((int)_intervalInput.Value * 1000);
    }

    private void ApplySmoothRendering(bool showStatus = true)
    {
        var enabled = _verticalSyncCheck.Checked;
        DoubleBuffered = enabled;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, enabled);
        UpdateStyles();
        _providerGrid.SetSmoothRendering(enabled);
        _keyGrid.SetSmoothRendering(enabled);
        Invalidate(true);
        if (showStatus)
        {
            SetStatus(enabled ? "垂直同步/平滑绘制已开启。" : "垂直同步/平滑绘制已关闭。", success: true);
        }
    }

    private void LoadSavedSettings()
    {
        try
        {
            var snapshot = _settingsStore.Load();
            var settings = snapshot.Settings;
            _baseUrlText.Text = string.IsNullOrWhiteSpace(settings.BaseUrl)
                ? "https://aihub.top"
                : settings.BaseUrl;
            _platformCombo.SelectedItem = settings.Platform;
            if (_platformCombo.SelectedIndex < 0)
            {
                _platformCombo.SelectedItem = "openai";
            }

            _minimumSuccessInput.Value = Math.Clamp(settings.MinimumSuccessPercent, 0, 100);
            _intervalInput.Value = Math.Clamp(settings.PollingIntervalSeconds, 30, 3600);
            _accountCacheInput.Value = Math.Clamp(settings.AccountCacheSeconds, 30, 3600);
            _routingModeCombo.SelectedIndex = settings.RoutingMode switch
            {
                RoutingMode.Balanced => 1,
                RoutingMode.Speed => 2,
                _ => 0
            };
            _durationCategory = settings.DurationCategory;

            _balancedDeadlineSoftSeconds = double.IsFinite(settings.BalancedDeadlineSoftSeconds)
                ? Math.Clamp(settings.BalancedDeadlineSoftSeconds, 0, 300)
                : BalancedDeadlineEngine.DefaultSoftDeadlineSeconds;
            _balancedSoftDeadlineInput.Value = (decimal)_balancedDeadlineSoftSeconds;

            _balancedExpectedOutputTokens = double.IsFinite(settings.BalancedExpectedOutputTokens)
                ? Math.Clamp(settings.BalancedExpectedOutputTokens, 0, 10_000_000)
                : BalancedDeadlineEngine.DefaultExpectedOutputTokens;
            _balancedExpectedOutputInput.Value = (decimal)_balancedExpectedOutputTokens;
            _themeCombo.SelectedIndex = settings.Theme switch
            {
                WinFormsTheme.Light => 1,
                WinFormsTheme.Dark => 2,
                _ => 0
            };
            _themePreference = settings.Theme;
            _verticalSyncCheck.Checked = settings.SmoothRendering;
            _persistCredentialsCheck.Checked = settings.PersistCredentials;
            _keySelectionInitialized = settings.KeySelectionInitialized;
            _savedSelectedKeyIds = settings.SelectedKeyIds.ToHashSet();
            _providerBlocklist = new ProviderBlocklist(settings.BlockedGroupIds, settings.BlockedNodePatterns);
            _activeProbeCheck.Checked = settings.ActiveProbeEnabled;
            _activeProbeKeyId = settings.ActiveProbeKeyId;
            _activeProbeModel = settings.ActiveProbeModel;

            if (settings.PersistCredentials && snapshot.Credentials is { } credentials)
            {
                _emailText.Text = credentials.Email;
                _passwordText.Text = credentials.Password;
                _tokenText.Text = credentials.BearerToken;
                _cookieText.Text = credentials.Cookie;
                _userAgentText.Text = credentials.UserAgent;
                _activeProbeApiKey = credentials.ActiveProbeApiKey;
                if (!new LoginCredentials(credentials.Email, credentials.Password).IsComplete &&
                    (!string.IsNullOrWhiteSpace(credentials.BearerToken) ||
                        !string.IsNullOrWhiteSpace(credentials.Cookie) ||
                        !string.IsNullOrWhiteSpace(credentials.UserAgent)))
                {
                    _advancedAuthenticationCheck.Checked = true;
                    ToggleAdvancedAuthentication();
                }

                if (!string.IsNullOrWhiteSpace(credentials.BearerToken) ||
                    !string.IsNullOrWhiteSpace(credentials.RefreshToken))
                {
                    _currentSession = new AuthSession(
                        credentials.BearerToken,
                        credentials.RefreshToken,
                        credentials.AccessTokenExpiresAt ?? DateTimeOffset.MinValue);
                }
            }
        }
        catch (Exception exception)
        {
            _persistCredentialsCheck.Checked = false;
            SetStatus($"已忽略无法读取的本地配置：{exception.Message}", success: false);
        }
    }

    private bool SaveCurrentSettings(bool showStatus)
    {
        try
        {
            if (_hasLoadedKeys)
            {
                CaptureKeySelection();
            }

            var settings = BuildPersistentSettings();
            var credentials = settings.PersistCredentials
                ? new PersistentCredentials
                {
                    Email = _emailText.Text.Trim(),
                    Password = _passwordText.Text,
                    BearerToken = _currentSession?.AccessToken ?? _tokenText.Text,
                    RefreshToken = _currentSession?.RefreshToken ?? string.Empty,
                    AccessTokenExpiresAt = _currentSession?.ExpiresAt,
                    Cookie = _cookieText.Text,
                    UserAgent = _userAgentText.Text,
                    ActiveProbeApiKey = _activeProbeApiKey
                }
                : null;

            _settingsStore.Save(settings, credentials);
            if (showStatus)
            {
                SetStatus(
                    settings.PersistCredentials
                        ? "当前配置已保存，认证字段已使用 Windows DPAPI 加密。"
                        : "界面配置已保存，本地认证数据已清除。",
                    success: true);
            }

            return true;
        }
        catch (Exception exception)
        {
            if (showStatus)
            {
                SetStatus($"配置保存失败：{exception.Message}", success: false);
            }

            return false;
        }
    }

    private void HandlePersistenceChanged()
    {
        if (_persistCredentialsCheck.Checked)
        {
            SaveCurrentSettings(showStatus: true);
            return;
        }

        SaveCurrentSettings(showStatus: true);
    }
}
