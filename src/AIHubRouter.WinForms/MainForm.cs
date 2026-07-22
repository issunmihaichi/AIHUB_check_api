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
    private ProviderBlocklist _providerBlocklist = ProviderBlocklist.Empty;
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
            _providerContextMenu.Dispose();
        };
        _authGuideButton.Click += (_, _) => ShowAuthenticationGuide();
        _openLoginButton.Click += (_, _) => OpenLoginPage();
        _pasteTokenButton.Click += (_, _) => PasteCredential(_tokenText, "Token");
        _pasteCookieButton.Click += (_, _) => PasteCredential(_cookieText, "Cookie");
        _pasteUserAgentButton.Click += (_, _) => PasteCredential(_userAgentText, "User-Agent");
        _resetBaseUrlButton.Click += (_, _) => _baseUrlText.Text = "https://aihub.top";
        _saveSettingsButton.Click += (_, _) => SaveCurrentSettings(showStatus: true);
        _manageBlocklistButton.Click += (_, _) => ShowBlocklistDialog();
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

}
