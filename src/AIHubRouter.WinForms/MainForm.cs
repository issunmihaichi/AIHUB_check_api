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
    private readonly AppSettingsStore _settingsStore = new();
    private bool _busy;
    private bool _hasLoadedKeys;
    private bool _keySelectionInitialized;
    private bool _applyingKeys;
    private HashSet<long> _savedSelectedKeyIds = [];
    private AuthSession? _currentSession;
    private MonitorSummary? _summary;
    private IReadOnlyList<GroupInfo> _groups = [];
    private IReadOnlyDictionary<long, double> _userRates = new Dictionary<long, double>();
    private RouteCandidate? _bestCandidate;

    public MainForm()
    {
        InitializeUi();
        LoadSavedSettings();
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
        _refreshButton.Click += async (_, _) => await RefreshDataAsync(loadAccountData: HasCredentials());
        _routeNowButton.Click += async (_, _) => await ExecuteRoutingCycleAsync();
        _autoRouteCheck.CheckedChanged += async (_, _) => await ToggleAutoRoutingAsync();
        _showCredentialsCheck.CheckedChanged += (_, _) => ToggleCredentialVisibility();
        _advancedAuthenticationCheck.CheckedChanged += (_, _) => ToggleAdvancedAuthentication();
        _verticalSyncCheck.CheckedChanged += (_, _) => ApplySmoothRendering();
        _emailText.TextChanged += (_, _) => _currentSession = null;
        _passwordText.TextChanged += (_, _) => _currentSession = null;
        _baseUrlText.TextChanged += (_, _) => _currentSession = null;
        _platformCombo.SelectedIndexChanged += (_, _) => RecalculateCandidate();
        _minimumSuccessInput.ValueChanged += (_, _) => RecalculateCandidate();
        _intervalInput.ValueChanged += (_, _) => UpdateTimerInterval();
        _keyGrid.CellValueChanged += (_, eventArgs) => HandleKeySelectionChanged(eventArgs);
        _autoTimer.Tick += async (_, _) => await ExecuteRoutingCycleAsync();
        UpdateTimerInterval();
    }

    private async Task ValidateAuthenticationAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "正在验证认证...");
        try
        {
            if (!HasCredentials())
            {
                throw new InvalidOperationException("请先输入邮箱和密码，或展开高级认证填写 Token/Cookie。");
            }

            await RunAuthenticatedAsync(async client =>
            {
                var user = await client.ValidateLoginAsync(_shutdown.Token);
                var identity = FindIdentity(user);
                SetStatus(identity is null ? "认证有效。" : $"认证有效：{identity}", success: true);
                await RefreshDataCoreAsync(client, loadAccountData: true, _shutdown.Token);
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

    private async Task RefreshDataAsync(bool loadAccountData)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "正在刷新监测数据...");
        try
        {
            if (loadAccountData)
            {
                await RunAuthenticatedAsync(client =>
                    RefreshDataCoreAsync(client, loadAccountData: true, _shutdown.Token));
            }
            else
            {
                using var client = CreateManualClient();
                await RefreshDataCoreAsync(client, loadAccountData: false, _shutdown.Token);
            }
            var detail = loadAccountData
                ? $"已加载 {_summary?.Apis.Count ?? 0} 个监测项和 {_keyGrid.Rows.Count} 个 Key。"
                : $"已加载 {_summary?.Apis.Count ?? 0} 个公开监测项。";
            SetStatus(detail, success: true);
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

    private async Task RefreshDataCoreAsync(
        AIHubClient client,
        bool loadAccountData,
        CancellationToken cancellationToken)
    {
        _summary = await client.GetProviderSummaryAsync(cancellationToken);

        if (loadAccountData)
        {
            var groupsTask = client.GetAvailableGroupsAsync(cancellationToken);
            var ratesTask = client.GetUserGroupRatesAsync(cancellationToken);
            var keysTask = client.GetAllKeysAsync(cancellationToken);
            await Task.WhenAll(groupsTask, ratesTask, keysTask);

            _groups = await groupsTask;
            _userRates = await ratesTask;
            ApplyKeys(await keysTask);
        }

        RecalculateCandidate();
    }

    private async Task ExecuteRoutingCycleAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "正在计算最低价路由...");
        try
        {
            if (!HasCredentials())
            {
                throw new InvalidOperationException("请先输入邮箱和密码，或展开高级认证填写 Token/Cookie。");
            }

            await RunAuthenticatedAsync(ExecuteRoutingCoreAsync);
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

    private async Task ExecuteRoutingCoreAsync(AIHubClient client)
    {
        await RefreshDataCoreAsync(client, loadAccountData: true, _shutdown.Token);

        if (_bestCandidate is null)
        {
            throw new InvalidOperationException("当前没有同时满足价格、可用率、时效和账号权限的供应商分组。");
        }

        var selectedKeys = CurrentKeyRows().Where(row => row.Selected).ToList();
        if (selectedKeys.Count == 0)
        {
            throw new InvalidOperationException("请在 API Keys 表格中至少勾选一个 Key。首次加载会默认勾选第一个启用的 Key。");
        }

        var changed = 0;
        var failed = new List<string>();
        foreach (var key in selectedKeys.Where(key => key.GroupId != _bestCandidate.Group.Id))
        {
            try
            {
                await client.UpdateKeyGroupAsync(key.Id, _bestCandidate.Group.Id, _shutdown.Token);
                key.GroupId = _bestCandidate.Group.Id;
                key.GroupName = _bestCandidate.Group.Name;
                key.Platform = _bestCandidate.Group.Platform;
                changed++;
            }
            catch (Exception exception)
            {
                if (exception is AIHubApiException { IsAuthenticationFailure: true })
                {
                    throw;
                }

                failed.Add($"{key.Name}: {GetSafeErrorMessage(exception)}");
            }
        }

        _keyGrid.Refresh();
        var route = $"{_bestCandidate.Provider.PlanType} / 分组 {_bestCandidate.Group.Id} / {_bestCandidate.EffectiveMultiplier:0.####}x";
        if (failed.Count > 0)
        {
            SetStatus($"已切换 {changed} 个 Key，失败 {failed.Count} 个。{string.Join("；", failed)}", success: false);
        }
        else if (changed == 0)
        {
            SetStatus($"所选 Key 已在最低价路由：{route}", success: true);
        }
        else
        {
            SetStatus($"已将 {changed} 个 Key 切换到 {route}", success: true);
        }
    }

    private void RecalculateCandidate()
    {
        if (_summary is null)
        {
            return;
        }

        var platform = _platformCombo.SelectedItem?.ToString() ?? "openai";
        var criteria = new RoutingCriteria(
            platform,
            (double)_minimumSuccessInput.Value / 100,
            TimeSpan.FromMinutes(15));

        _bestCandidate = RoutingEngine.SelectCheapest(
            _summary.Apis,
            _groups,
            _userRates,
            criteria,
            DateTimeOffset.UtcNow);

        ApplyProviders(criteria);
        _candidateLabel.Text = _bestCandidate is null
            ? "最低价：无符合项"
            : $"最低价：{_bestCandidate.Provider.PlanType}  {_bestCandidate.EffectiveMultiplier:0.####}x";
    }

    private void ApplyProviders(RoutingCriteria criteria)
    {
        if (_summary is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var groups = _groups.ToDictionary(group => group.Id);
        var rows = _summary.Apis
            .Where(provider => provider.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .Select(provider =>
            {
                var isAuthorized = provider.GroupId is { } groupId && groups.TryGetValue(groupId, out var group);
                var overrideRate = 0d;
                var hasOverride = provider.GroupId is { } id && _userRates.TryGetValue(id, out overrideRate);
                var effectiveRate = hasOverride ? overrideRate : provider.PriceMultiplier;
                var isFresh = provider.CheckedAt is { } checkedAt &&
                    now - checkedAt >= TimeSpan.FromMinutes(-1) &&
                    now - checkedAt <= criteria.MaximumStatusAge;
                var state = !provider.Enabled ? "已停用"
                    : !provider.Available ? "当前异常"
                    : !isFresh ? "数据过期"
                    : (provider.SuccessRate6h ?? 0) < criteria.MinimumSuccessRate6h ? "低于阈值"
                    : _groups.Count > 0 && !isAuthorized ? "账号不可用"
                    : _groups.Count == 0 ? "待认证"
                    : "可路由";

                return new ProviderGridRow
                {
                    Source = provider,
                    IsBest = _bestCandidate?.Provider.Id == provider.Id,
                    EffectiveRate = hasOverride ? $"{effectiveRate:0.####} *" : $"{effectiveRate:0.####}",
                    State = state
                };
            })
            .OrderByDescending(row => row.IsBest)
            .ThenBy(row => row.Source.PriceMultiplier)
            .ThenByDescending(row => row.Source.SuccessRate6h ?? 0)
            .ToList();

        _providerGrid.DataSource = new BindingList<ProviderGridRow>(rows);
    }

    private void ApplyKeys(IReadOnlyList<ApiKeyInfo> keys)
    {
        var selectedIds = _hasLoadedKeys && _keySelectionInitialized
            ? CurrentKeyRows().Where(row => row.Selected).Select(row => row.Id).ToHashSet()
            : KeySelectionPolicy.Resolve(_keySelectionInitialized, _savedSelectedKeyIds, keys).ToHashSet();
        var groupLookup = _groups.ToDictionary(group => group.Id);

        _applyingKeys = true;
        try
        {
            var rows = keys.Select(key =>
            {
                var group = key.Group ?? (key.GroupId is { } id && groupLookup.TryGetValue(id, out var found) ? found : null);
                return new KeyGridRow
                {
                    Selected = selectedIds.Contains(key.Id),
                    Id = key.Id,
                    Name = key.Name,
                    Status = key.Status,
                    GroupId = key.GroupId,
                    GroupName = group?.Name ?? "未绑定",
                    Platform = group?.Platform ?? "-"
                };
            }).ToList();

            _keyGrid.DataSource = new BindingList<KeyGridRow>(rows);
            _hasLoadedKeys = true;
            if (keys.Count > 0 || _keySelectionInitialized)
            {
                CaptureKeySelection();
            }
        }
        finally
        {
            _applyingKeys = false;
        }

        if (_persistCredentialsCheck.Checked)
        {
            SaveCurrentSettings(showStatus: false);
        }
    }

    private IEnumerable<KeyGridRow> CurrentKeyRows()
    {
        if (_keyGrid.DataSource is BindingList<KeyGridRow> rows)
        {
            return rows;
        }

        return [];
    }

    private void HandleKeySelectionChanged(DataGridViewCellEventArgs eventArgs)
    {
        if (_applyingKeys || eventArgs.RowIndex < 0 || eventArgs.ColumnIndex != 0)
        {
            return;
        }

        CaptureKeySelection();
        if (_persistCredentialsCheck.Checked)
        {
            SaveCurrentSettings(showStatus: false);
        }
    }

    private void CaptureKeySelection()
    {
        _savedSelectedKeyIds = CurrentKeyRows()
            .Where(row => row.Selected)
            .Select(row => row.Id)
            .ToHashSet();
        _keySelectionInitialized = true;
    }

    private async Task ToggleAutoRoutingAsync()
    {
        if (_autoRouteCheck.Checked)
        {
            UpdateTimerInterval();
            _autoTimer.Start();
            await ExecuteRoutingCycleAsync();
        }
        else
        {
            _autoTimer.Stop();
            SetStatus("自动路由已关闭。", success: true);
        }
    }

    private void UpdateTimerInterval()
    {
        _autoTimer.Interval = checked((int)_intervalInput.Value * 1000);
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

            var settings = new PersistentAppSettings
            {
                PersistCredentials = _persistCredentialsCheck.Checked,
                BaseUrl = _baseUrlText.Text.Trim(),
                Platform = _platformCombo.SelectedItem?.ToString() ?? "openai",
                MinimumSuccessPercent = (int)_minimumSuccessInput.Value,
                PollingIntervalSeconds = (int)_intervalInput.Value,
                SmoothRendering = _verticalSyncCheck.Checked,
                KeySelectionInitialized = _keySelectionInitialized,
                SelectedKeyIds = _savedSelectedKeyIds.Order().ToArray()
            };
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

    private AIHubClient CreateManualClient()
    {
        return new AIHubClient(
            _baseUrlText.Text,
            _tokenText.Text,
            _cookieText.Text,
            _userAgentText.Text);
    }

    private bool HasCredentials()
    {
        return HasAutomaticCredentials() ||
            !string.IsNullOrWhiteSpace(_tokenText.Text) ||
            !string.IsNullOrWhiteSpace(_cookieText.Text);
    }

    private bool HasAutomaticCredentials()
    {
        return !string.IsNullOrWhiteSpace(_emailText.Text) &&
            !string.IsNullOrWhiteSpace(_passwordText.Text);
    }

    private async Task<AIHubClient> CreateAuthenticatedClientAsync(bool forceRenew)
    {
        var credentials = new LoginCredentials(_emailText.Text.Trim(), _passwordText.Text);
        var canUseSessionCoordinator = credentials.IsComplete ||
            !string.IsNullOrWhiteSpace(_currentSession?.RefreshToken);
        if (!canUseSessionCoordinator)
        {
            return CreateManualClient();
        }

        if (forceRenew && _currentSession is not null)
        {
            _currentSession = _currentSession with { ExpiresAt = DateTimeOffset.MinValue };
        }

        using var sessionClient = new AIHubClient(
            _baseUrlText.Text,
            cookie: _cookieText.Text,
            userAgent: _userAgentText.Text);
        var coordinator = new SessionCoordinator(
            sessionClient.RefreshSessionAsync,
            sessionClient.LoginAsync,
            PersistSessionAsync);
        var session = await coordinator.GetSessionAsync(_currentSession, credentials, _shutdown.Token);
        _currentSession = session;
        _tokenText.Text = session.AccessToken;
        return new AIHubClient(
            _baseUrlText.Text,
            session.AccessToken,
            _cookieText.Text,
            _userAgentText.Text);
    }

    private Task PersistSessionAsync(AuthSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _currentSession = session;
        _tokenText.Text = session.AccessToken;
        if (_persistCredentialsCheck.Checked && !SaveCurrentSettings(showStatus: false))
        {
            throw new InvalidOperationException("认证 session 已更新，但加密保存失败。");
        }

        return Task.CompletedTask;
    }

    private async Task RunAuthenticatedAsync(Func<AIHubClient, Task> operation)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var client = await CreateAuthenticatedClientAsync(forceRenew: attempt > 0);
            try
            {
                await operation(client);
                return;
            }
            catch (AIHubApiException exception)
                when (exception.IsAuthenticationFailure &&
                    attempt == 0 &&
                    CanRenewAutomatically())
            {
                InvalidateCurrentSession();
            }
        }
    }

    private bool CanRenewAutomatically()
    {
        return HasAutomaticCredentials() || !string.IsNullOrWhiteSpace(_currentSession?.RefreshToken);
    }

    private void InvalidateCurrentSession()
    {
        if (_currentSession is not null)
        {
            _currentSession = _currentSession with { ExpiresAt = DateTimeOffset.MinValue };
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        _validateButton.Enabled = !busy;
        _refreshButton.Enabled = !busy;
        _routeNowButton.Enabled = !busy;
        _progressBar.Visible = busy;
        if (message is not null)
        {
            _statusLabel.Text = message;
            _statusLabel.ForeColor = Color.FromArgb(55, 65, 75);
        }
    }

    private void SetStatus(string message, bool success)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = success
            ? Color.FromArgb(20, 110, 65)
            : Color.FromArgb(185, 45, 45);
    }

    private void HandleError(Exception exception)
    {
        if (exception is OperationCanceledException && _shutdown.IsCancellationRequested)
        {
            return;
        }

        var message = GetSafeErrorMessage(exception);
        if (_autoRouteCheck.Checked && exception is AIHubApiException { IsAuthenticationFailure: true })
        {
            _autoRouteCheck.Checked = false;
        }

        SetStatus(message, success: false);
    }

    private static string GetSafeErrorMessage(Exception exception)
    {
        return exception switch
        {
            AIHubApiException apiException => apiException.Message,
            HttpRequestException => "网络连接失败，请检查站点地址和网络。",
            TaskCanceledException => "请求超时，请稍后重试。",
            ArgumentException argumentException => argumentException.Message,
            InvalidOperationException invalidOperationException => invalidOperationException.Message,
            _ => $"操作失败：{exception.Message}"
        };
    }

    private static string? FindIdentity(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "email", "username", "display_name" })
            {
                if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            if (element.TryGetProperty("user", out var user))
            {
                return FindIdentity(user);
            }
        }

        return null;
    }
}
