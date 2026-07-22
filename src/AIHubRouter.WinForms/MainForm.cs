using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm : Form
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly System.Windows.Forms.Timer _autoTimer = new();
    private readonly System.Windows.Forms.Timer _balancedCountdownTimer = new() { Interval = 500 };
    private readonly AppSettingsStore _settingsStore = new();
    private bool _busy;
    private bool _hasLoadedKeys;
    private bool _keySelectionInitialized;
    private bool _applyingKeys;
    private bool _applyingSessionCredentials;
    private HashSet<long> _savedSelectedKeyIds = [];
    private AuthSession? _currentSession;
    private RoutingService? _routingService;
    private readonly ProviderMetricsRollingWindow _providerMetrics = new();
    private RouteEvaluation? _lastEvaluation;
    private WinFormsTheme _themePreference = WinFormsTheme.System;
    private MonitorSummary? _summary;
    private IReadOnlyList<GroupInfo> _groups = [];
    private IReadOnlyDictionary<long, double> _rawUserRates = new Dictionary<long, double>();
    private IReadOnlyDictionary<long, double> _userRates = new Dictionary<long, double>();
    private IReadOnlyList<AdaptiveCandidateRanking> _adaptiveRankings = [];
    private RouteCandidate? _bestCandidate;
    private double _balancedCountdownSeconds = 7_200;
    private double _balancedDeadlineSoftSeconds = BalancedDeadlineEngine.DefaultSoftDeadlineSeconds;
    private double _balancedExpectedOutputTokens = 1_000;
    private DateTimeOffset? _balancedCountdownEndsAtUtc;
    private bool _updatingBalancedCountdown;
    private bool _balancedCountdownExpiredApplied;

    public MainForm()
    {
        InitializeUi();
        LoadSavedSettings();
        ApplySelectedTheme();
        WireEvents();
        ApplySmoothRendering(showStatus: false);
    }

    private void WireEvents()
    {
        Shown += async (_, _) => await RefreshDataAsync(loadAccountData: HasCredentials());
        FormClosing += (_, _) =>
        {
            if (!SaveCurrentSettings(showStatus: false) && _persistCredentialsCheck.Checked)
            {
                MessageBox.Show(
                    "认证配置保存失败，本次修改可能无法在下次启动时恢复。",
                    "AIHub 最低价路由器",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            _autoTimer.Stop();
            _balancedCountdownTimer.Stop();
            _routingService?.Dispose();
            _shutdown.Cancel();
            _toolTip.Dispose();
        };
        _authGuideButton.Click += (_, _) => ShowAuthenticationGuide();
        _openLoginButton.Click += (_, _) => OpenLoginPage();
        _pasteTokenButton.Click += (_, _) => PasteCredential(_tokenText, "Token");
        _pasteCookieButton.Click += (_, _) => PasteCredential(_cookieText, "Cookie");
        _pasteUserAgentButton.Click += (_, _) => PasteCredential(_userAgentText, "User-Agent");
        _resetBaseUrlButton.Click += (_, _) => _baseUrlText.Text = "https://aihub.top";
        _saveSettingsButton.Click += (_, _) => SaveCurrentSettings(showStatus: true);
        _persistCredentialsCheck.CheckedChanged += (_, _) => HandlePersistenceChanged();
        _validateButton.Click += async (_, _) => await ValidateAuthenticationAsync();
        _refreshButton.Click += async (_, _) =>
        {
            if (HasCredentials())
            {
                await ExecuteRoutingCycleAsync(dryRun: true, forceAccountRefresh: true);
            }
            else
            {
                await RefreshDataAsync(loadAccountData: false);
            }
        };
        _simulateButton.Click += async (_, _) => await ExecuteRoutingCycleAsync(dryRun: true);
        _routeNowButton.Click += async (_, _) => await ExecuteRoutingCycleAsync();
        _autoRouteCheck.CheckedChanged += async (_, _) => await ToggleAutoRoutingAsync();
        _showCredentialsCheck.CheckedChanged += (_, _) => ToggleCredentialVisibility();
        _advancedAuthenticationCheck.CheckedChanged += (_, _) => ToggleAdvancedAuthentication();
        _verticalSyncCheck.CheckedChanged += (_, _) => ApplySmoothRendering();
        _emailText.TextChanged += (_, _) => ResetAuthenticationAndRoutingService();
        _passwordText.TextChanged += (_, _) => ResetAuthenticationAndRoutingService();
        _baseUrlText.TextChanged += (_, _) => ResetAuthenticationAndRoutingService();
        _tokenText.TextChanged += (_, _) =>
        {
            if (!_applyingSessionCredentials)
            {
                ResetAuthenticationAndRoutingService();
            }
        };
        _cookieText.TextChanged += (_, _) => ResetAuthenticationAndRoutingService();
        _userAgentText.TextChanged += (_, _) => ResetAuthenticationAndRoutingService();
        _platformCombo.SelectedIndexChanged += (_, _) =>
        {
            InvalidateRoutingService();
            RecalculateCandidate();
        };
        _minimumSuccessInput.ValueChanged += (_, _) =>
        {
            InvalidateRoutingService();
            RecalculateCandidate();
        };
        _intervalInput.ValueChanged += (_, _) => UpdateTimerInterval();
        _routingModeCombo.SelectedIndexChanged += (_, _) =>
        {
            InvalidateRoutingService();
            RecalculateCandidate();
            SaveCurrentSettings(showStatus: false);
        };
        _balancedCountdownInput.ValueChanged += (_, _) =>
        {
            if (!_updatingBalancedCountdown)
            {
                RestartBalancedCountdown();
            }
        };
        _resetBalancedCountdownButton.Click += (_, _) => RestartBalancedCountdown();
        _balancedSoftDeadlineInput.ValueChanged += (_, _) =>
        {
            _balancedDeadlineSoftSeconds = (double)_balancedSoftDeadlineInput.Value;
            InvalidateRoutingService();
            RecalculateCandidate();
            SaveCurrentSettings(showStatus: false);
        };
        _balancedExpectedOutputInput.ValueChanged += (_, _) =>
        {
            _balancedExpectedOutputTokens = (double)_balancedExpectedOutputInput.Value;
            InvalidateRoutingService();
            RecalculateCandidate();
            SaveCurrentSettings(showStatus: false);
        };
        _accountCacheInput.ValueChanged += (_, _) =>
        {
            InvalidateRoutingService();
            SaveCurrentSettings(showStatus: false);
        };
        _themeCombo.SelectedIndexChanged += (_, _) =>
        {
            ApplySelectedTheme();
            SaveCurrentSettings(showStatus: false);
        };
        _keyGrid.CellValueChanged += (_, eventArgs) => HandleKeySelectionChanged(eventArgs);
        _autoTimer.Tick += async (_, _) => await ExecuteRoutingCycleAsync();
        _balancedCountdownTimer.Tick += (_, _) => UpdateBalancedCountdownDisplay();
        UpdateTimerInterval();
        UpdateBalancedCountdownDisplay();
        _balancedCountdownTimer.Start();
    }

    private PersistentAppSettings BuildPersistentSettings()
    {
        return new PersistentAppSettings
        {
            PersistCredentials = _persistCredentialsCheck.Checked,
            BaseUrl = _baseUrlText.Text.Trim(),
            Platform = _platformCombo.SelectedItem?.ToString() ?? "openai",
            RoutingMode = CurrentRoutingMode(),
            DurationCategory = CurrentDurationCategory(),
            BalancedCountdownSeconds = _balancedCountdownSeconds,
            BalancedCountdownEndsAtUtc = _balancedCountdownEndsAtUtc,
            BalancedDeadlineSoftSeconds = _balancedDeadlineSoftSeconds,
            BalancedExpectedOutputTokens = _balancedExpectedOutputTokens,
            MinimumSuccessPercent = (int)_minimumSuccessInput.Value,
            PollingIntervalSeconds = (int)_intervalInput.Value,
            AccountCacheSeconds = (int)_accountCacheInput.Value,
            SmoothRendering = _verticalSyncCheck.Checked,
            Theme = _themePreference,
            KeySelectionInitialized = _keySelectionInitialized,
            SelectedKeyIds = _savedSelectedKeyIds.Order().ToArray()
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
        return _balancedCountdownSeconds <= 3_600
            ? TaskDurationCategory.Short
            : _balancedCountdownSeconds <= 21_600
                ? TaskDurationCategory.Medium
                : TaskDurationCategory.Long;
    }

    private static string PreferenceDisplayName(AdaptivePreference? preference) => preference switch
    {
        AdaptivePreference.Balanced => "均衡",
        AdaptivePreference.Speed => "速度",
        _ => "经济"
    };

    private static string ResolveAdaptiveRank(
        string providerId,
        IReadOnlyDictionary<string, AdaptiveCandidateRanking> rankings,
        out int rankValue)
    {
        rankValue = int.MaxValue;
        if (string.IsNullOrWhiteSpace(providerId) || !rankings.TryGetValue(providerId, out var ranking))
        {
            return "-";
        }

        if (ranking.Rank is { } rank)
        {
            rankValue = rank;
            return $"#{rank}";
        }

        return "不建议";
    }

    private static string DecisionReasonText(RouteDecisionReason reason)
    {
        return reason switch
        {
            RouteDecisionReason.BalancedDeadlineColdStart => "均衡冷启动：选择满足截止时间的最低倍率节点",
            RouteDecisionReason.BalancedDeadlineCurrentWithinDeadline => "均衡：当前节点满足截止时间，保持不切换",
            RouteDecisionReason.BalancedDeadlineSwitched => "均衡：当前节点超时，切换到满足截止时间的最低成本节点",
            RouteDecisionReason.BalancedDeadlineNoFeasibleCandidate => "均衡：没有节点满足截止时间，保持当前路线",
            RouteDecisionReason.BalancedCountdownExpired => "均衡倒计时结束：已切换为严格经济模式",
            RouteDecisionReason.NoCandidate => "没有符合条件的路由",
            RouteDecisionReason.InitialRoute => "建立初始路由",
            RouteDecisionReason.CurrentRouteInvalid => "当前路由已不可用",
            RouteDecisionReason.AlreadyOptimal => "当前路由已是最优",
            RouteDecisionReason.ScoreAdvantageTooSmall => "优势较小，保持当前路由",
            RouteDecisionReason.BetterPrice => "发现更低价格",
            RouteDecisionReason.FasterForWeightedTradeoff => "速度收益超过价格增幅",
            RouteDecisionReason.AdaptiveCostAccepted => "净收益满足省钱切换条件",
            RouteDecisionReason.AdaptiveBalancedAccepted => "净收益和完成时间满足均衡条件",
            RouteDecisionReason.AdaptiveSpeedAccepted => "速度提升满足切换条件",
            RouteDecisionReason.AdaptivePriceNotLower => "新价格未降低",
            RouteDecisionReason.AdaptiveShortTaskProtected => "短任务保持当前路由",
            RouteDecisionReason.AdaptiveRemainingWorkTooSmall => "剩余工作量极少",
            RouteDecisionReason.AdaptiveCostRejected => "净省不足或新节点过慢",
            RouteDecisionReason.AdaptiveBalancedRejected => "净省、时间或降价未达标",
            RouteDecisionReason.AdaptiveSpeedRejected => "速度提升不足或涨价过多",
            RouteDecisionReason.AdaptiveUnknownPreference => "未知路由偏好",
            _ => "路由评估完成"
        };
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

    private double GetBalancedRemainingSeconds(DateTimeOffset now)
    {
        if (_balancedCountdownEndsAtUtc is not { } end)
        {
            return 0;
        }

        var remaining = (end - now).TotalSeconds;
        return double.IsFinite(remaining) ? Math.Max(0, remaining) : 0;
    }

    private void RestartBalancedCountdown()
    {
        _balancedCountdownSeconds = Math.Max(0, (double)_balancedCountdownInput.Value * 60);
        _balancedCountdownEndsAtUtc = DateTimeOffset.UtcNow.AddSeconds(_balancedCountdownSeconds);
        _balancedCountdownExpiredApplied = false;
        UpdateBalancedCountdownDisplay();
        InvalidateRoutingService();
        RecalculateCandidate();
        SaveCurrentSettings(showStatus: false);
    }

    private void UpdateBalancedCountdownDisplay()
    {
        var remaining = GetBalancedRemainingSeconds(DateTimeOffset.UtcNow);
        if (CurrentRoutingMode() == RoutingMode.Balanced && remaining <= 0 && !_balancedCountdownExpiredApplied)
        {
            _balancedCountdownExpiredApplied = true;
            InvalidateRoutingService();
            RecalculateCandidate();
        }
        else if (remaining > 0)
        {
            _balancedCountdownExpiredApplied = false;
        }
        var totalSeconds = Math.Max(0, Math.Round(remaining));
        var hours = (int)(totalSeconds / 3600);
        var minutes = (int)((totalSeconds % 3600) / 60);
        var seconds = (int)(totalSeconds % 60);
        _balancedCountdownLabel.Text = hours > 0
            ? $"剩余 {hours:00}:{minutes:00}:{seconds:00}"
            : $"剩余 {minutes:00}:{seconds:00}";
    }

    private void ToggleCredentialVisibility()
    {
        var hidden = !_showCredentialsCheck.Checked;
        _passwordText.UseSystemPasswordChar = hidden;
        _tokenText.UseSystemPasswordChar = hidden;
        _cookieText.UseSystemPasswordChar = hidden;
        _userAgentText.UseSystemPasswordChar = hidden;
    }

    private void ShowAuthenticationGuide()
    {
        using var dialog = new AuthGuideDialog(_baseUrlText.Text);
        NativeThemeManager.Apply(dialog, _activePalette);
        dialog.ShowDialog(this);
    }

    private void OpenLoginPage()
    {
        try
        {
            if (!Uri.TryCreate(_baseUrlText.Text.Trim(), UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("站点地址无效。");
            }

            var loginUri = new Uri(new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/"), "login");
            Process.Start(new ProcessStartInfo(loginUri.ToString()) { UseShellExecute = true });
            SetStatus("已在浏览器中打开 AIHub 登录页。", success: true);
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
    }

    private void PasteCredential(TextBox target, string fieldName)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                throw new InvalidOperationException("剪贴板中没有文本。");
            }

            target.Text = Clipboard.GetText().Trim();
            SetStatus($"已粘贴 {fieldName}。", success: true);
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
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
            var configuredCountdownSeconds = settings.BalancedCountdownSeconds;
            if (!double.IsFinite(configuredCountdownSeconds) || configuredCountdownSeconds < 0)
            {
                configuredCountdownSeconds = settings.DurationCategory switch
                {
                    TaskDurationCategory.Short => 3_600,
                    TaskDurationCategory.Long => 21_600,
                    _ => 7_200
                };
            }

            _balancedCountdownSeconds = Math.Clamp(configuredCountdownSeconds, 0, 86_400);
            _updatingBalancedCountdown = true;
            try
            {
                _balancedCountdownInput.Value = (decimal)(_balancedCountdownSeconds / 60);
            }
            finally
            {
                _updatingBalancedCountdown = false;
            }

            _balancedCountdownEndsAtUtc = settings.BalancedCountdownEndsAtUtc;
            if (_balancedCountdownEndsAtUtc is null)
            {
                _balancedCountdownEndsAtUtc = DateTimeOffset.UtcNow.AddSeconds(_balancedCountdownSeconds);
            }

            _balancedDeadlineSoftSeconds = double.IsFinite(settings.BalancedDeadlineSoftSeconds)
                ? Math.Clamp(settings.BalancedDeadlineSoftSeconds, 0, 300)
                : BalancedDeadlineEngine.DefaultSoftDeadlineSeconds;
            _updatingBalancedCountdown = true;
            try
            {
                _balancedSoftDeadlineInput.Value = (decimal)_balancedDeadlineSoftSeconds;
            }
            finally
            {
                _updatingBalancedCountdown = false;
            }

            _balancedExpectedOutputTokens = double.IsFinite(settings.BalancedExpectedOutputTokens)
                ? Math.Clamp(settings.BalancedExpectedOutputTokens, 0, 10_000_000)
                : 1_000;
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

            if (settings.PersistCredentials && snapshot.Credentials is { } credentials)
            {
                _emailText.Text = credentials.Email;
                _passwordText.Text = credentials.Password;
                _tokenText.Text = credentials.BearerToken;
                _cookieText.Text = credentials.Cookie;
                _userAgentText.Text = credentials.UserAgent;
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
                    UserAgent = _userAgentText.Text
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

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        _validateButton.Enabled = !busy;
        _refreshButton.Enabled = !busy;
        _simulateButton.Enabled = !busy;
        _routeNowButton.Enabled = !busy;
        _progressBar.Visible = busy;
        if (message is not null)
        {
            _statusLabel.Text = message;
            _statusLabel.ForeColor = _activePalette.MutedText;
        }
    }

    private void SetStatus(string message, bool success)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = success
            ? _activePalette.Success
            : _activePalette.Error;
    }

    private void HandleError(Exception exception)
    {
        if (exception is OperationCanceledException && _shutdown.IsCancellationRequested)
        {
            return;
        }

        var message = SafeErrorPresentation.GetMessage(exception);
        if (_autoRouteCheck.Checked && exception is AIHubApiException { IsAuthenticationFailure: true })
        {
            _autoRouteCheck.Checked = false;
        }

        SetStatus(message, success: false);
    }

}
