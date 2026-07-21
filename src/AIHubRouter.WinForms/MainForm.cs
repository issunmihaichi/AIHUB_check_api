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
    private bool _applyingSessionCredentials;
    private HashSet<long> _savedSelectedKeyIds = [];
    private AuthSession? _currentSession;
    private RoutingService? _routingService;
    private RouteEvaluation? _lastEvaluation;
    private WinFormsTheme _themePreference = WinFormsTheme.System;
    private MonitorSummary? _summary;
    private IReadOnlyList<GroupInfo> _groups = [];
    private IReadOnlyDictionary<long, double> _userRates = new Dictionary<long, double>();
    private IReadOnlyList<AdaptiveCandidateRanking> _adaptiveRankings = [];
    private RouteCandidate? _bestCandidate;

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
                _currentSession = null;
                InvalidateRoutingService();
            }
        };
        _cookieText.TextChanged += (_, _) => InvalidateRoutingService();
        _userAgentText.TextChanged += (_, _) => InvalidateRoutingService();
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
        _durationCombo.SelectedIndexChanged += (_, _) =>
        {
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

    private async Task ExecuteRoutingCycleAsync(bool dryRun = false, bool forceAccountRefresh = false)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, dryRun ? "正在模拟路由决策..." : "正在执行路由...");
        try
        {
            if (!HasCredentials())
            {
                throw new InvalidOperationException("请先输入邮箱和密码，或展开高级认证填写 Token/Cookie。");
            }

            var service = EnsureRoutingService();
            var result = await service.RunOnceAsync(dryRun, forceAccountRefresh, _shutdown.Token);
            ApplyRoutingCycleResult(result);
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

    private RoutingService EnsureRoutingService()
    {
        if (_routingService is not null)
        {
            return _routingService;
        }

        var storageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIHubRouter");
        var settings = BuildPersistentSettings();
        var credentials = new PersistentCredentials
        {
            Email = _emailText.Text.Trim(),
            Password = _passwordText.Text,
            BearerToken = _currentSession?.AccessToken ?? _tokenText.Text,
            RefreshToken = _currentSession?.RefreshToken ?? string.Empty,
            AccessTokenExpiresAt = _currentSession?.ExpiresAt,
            Cookie = _cookieText.Text,
            UserAgent = _userAgentText.Text
        };
        _routingService = new RoutingService(
            settings,
            credentials,
            new JsonRouteStateStore(storageDirectory),
            persistCredentials: PersistRoutingCredentialsAsync);
        return _routingService;
    }

    private Task PersistRoutingCredentialsAsync(
        PersistentCredentials credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _currentSession = new AuthSession(
            credentials.BearerToken,
            credentials.RefreshToken,
            credentials.AccessTokenExpiresAt ?? DateTimeOffset.MinValue);
        _applyingSessionCredentials = true;
        try
        {
            _tokenText.Text = credentials.BearerToken;
        }
        finally
        {
            _applyingSessionCredentials = false;
        }
        if (_persistCredentialsCheck.Checked && !SaveCurrentSettings(showStatus: false))
        {
            throw new InvalidOperationException("Session refreshed but encrypted persistence failed.");
        }

        return Task.CompletedTask;
    }

    private void ApplyRoutingCycleResult(RoutingCycleResult result)
    {
        _lastEvaluation = result.Evaluation;
        _summary = new MonitorSummary { Apis = result.Providers.ToList() };
        _groups = result.Groups;
        _userRates = result.UserGroupRates;
        _adaptiveRankings = result.Decision.AdaptiveRankings;
        _bestCandidate = result.Decision.Target;
        ApplyKeys(result.Keys);

        var groupLookup = _groups.ToDictionary(group => group.Id);
        var candidateLookup = result.Evaluation.EligibleCandidates.ToDictionary(candidate => candidate.Group.Id);
        var adaptiveRankingLookup = result.Decision.AdaptiveRankings.ToDictionary(ranking => ranking.GroupId);
        var minimumSuccessRate6h = (double)_minimumSuccessInput.Value / 100;
        var maximumStatusAge = TimeSpan.FromMinutes(15);
        var rows = result.Providers
            .Where(provider => provider.Platform.Equals(_platformCombo.SelectedItem?.ToString() ?? "openai", StringComparison.OrdinalIgnoreCase))
            .Select(provider =>
            {
                var groupId = provider.GroupId;
                var score = groupId is { } id && result.Evaluation.CandidateScores.TryGetValue(id, out var value)
                    ? value.ToString("0.###")
                    : "-";
                var adaptiveRank = ResolveAdaptiveRank(groupId, adaptiveRankingLookup, out var adaptiveRankValue);
                var decisionState = groupId is { } decisionGroupId && decisionGroupId == result.Decision.Target?.Group.Id ? "推荐"
                    : groupId is { } currentGroupId && currentGroupId == result.Decision.Current?.Group.Id ? "当前"
                    : groupId is { } baselineGroupId && baselineGroupId == result.Evaluation.Baseline?.Group.Id ? "最低价"
                    : string.Empty;
                var overrideRate = 0d;
                var hasOverride = groupId is { } overrideGroupId && _userRates.TryGetValue(overrideGroupId, out overrideRate);
                var effectiveMultiplier = hasOverride ? overrideRate : provider.PriceMultiplier;
                if (groupId is { } effectiveGroup && candidateLookup.TryGetValue(effectiveGroup, out var candidate))
                {
                    effectiveMultiplier = candidate.EffectiveMultiplier;
                    hasOverride = candidate.HasUserRateOverride;
                }

                var effective = $"{effectiveMultiplier:0.####}{(hasOverride ? " *" : string.Empty)}";
                return new ProviderGridRow
                {
                    Source = provider,
                    IsBest = groupId is { } targetGroupId && targetGroupId == result.Decision.Target?.Group.Id,
                    EffectiveRate = effective,
                    WeightedScore = score,
                    AdaptiveRank = adaptiveRank,
                    AdaptiveRankValue = adaptiveRankValue,
                    DecisionState = decisionState,
                    State = ProviderStatusPresentation.ResolveRoutingState(
                        provider,
                        hasAccountData: true,
                        isAuthorized: groupId is { } authorizedId && groupLookup.ContainsKey(authorizedId),
                        effectiveMultiplier: effectiveMultiplier,
                        minimumSuccessRate6h: minimumSuccessRate6h,
                        now: result.Decision.EvaluatedAt,
                        maximumStatusAge: maximumStatusAge)
                };
            })
            .OrderBy(row => row.AdaptiveRankValue)
            .ThenByDescending(row => row.IsBest)
            .ThenBy(row => row.WeightedScore)
            .ToList();
        _providerGrid.DataSource = new BindingList<ProviderGridRow>(rows);
        var effectiveMode = PreferenceDisplayName(result.Decision.EffectivePreference);
        var modeText = effectiveMode == ModeDisplayName()
            ? effectiveMode
            : $"{ModeDisplayName()}→{effectiveMode}";
        _candidateLabel.Text = result.Decision.Target is { } target
            ? $"{modeText}：{target.Provider.PlanType}  {target.EffectiveMultiplier:0.####}x"
            : "当前模式：无符合项";

        var reason = string.IsNullOrWhiteSpace(result.Decision.Detail)
            ? DecisionReasonText(result.Decision.Reason)
            : result.Decision.Detail;
        var changeText = result.DryRun
            ? $"模拟：{result.ChangedKeyCount} 个 Key 将切换"
            : $"已切换 {result.ChangedKeyCount} 个 Key";
        var failedNames = result.KeyResults
            .Where(key => !key.Success)
            .Select(key => key.KeyName)
            .ToArray();
        if (failedNames.Length > 0)
        {
            changeText += $"，失败 {failedNames.Length} 个：{string.Join("、", failedNames)}";
        }
        SetStatus($"{reason}；{changeText}", result.FailedKeyCount == 0);
        WriteAudit(result);
    }

    private void WriteAudit(RoutingCycleResult result)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIHubRouter", "logs");
            var writer = new AuditLogWriter(Path.Combine(directory, "routing.jsonl"));
            var adaptive = result.Decision.AdaptiveDecision;
            writer.Write(new RouteAuditEntry(
                result.CompletedAt,
                CurrentRoutingMode(),
                result.Decision.Reason,
                result.Decision.Current?.Group.Id,
                result.Decision.Target?.Group.Id,
                result.DryRun,
                result.Evaluation.EligibleCandidates.Select(candidate => new RouteAuditCandidate(
                    candidate.Group.Id,
                    candidate.EffectiveMultiplier,
                    candidate.Provider.FirstTokenLatencyMs,
                    result.Evaluation.CandidateScores.TryGetValue(candidate.Group.Id, out var score) ? score : 0,
                    candidate.Group.Id == result.Decision.Target?.Group.Id)).ToArray(),
                result.KeyResults.Select(key => new RouteAuditKey(
                    key.KeyId,
                    key.Changed,
                    key.Success,
                    key.Error is null ? null : "update-failed")).ToArray())
            {
                EffectivePreference = result.Decision.EffectivePreference,
                DurationCategory = result.Decision.DurationCategory,
                CurrentIntervalSeconds = result.Decision.CurrentIntervalSeconds,
                AdaptiveReason = adaptive?.Reason,
                PenaltyUsd = adaptive?.PenaltyUsd,
                NetSavingUsd = adaptive?.NetSavingUsd,
                OldCompletionSeconds = adaptive?.OldCompletionSeconds,
                NewCompletionSeconds = adaptive?.NewCompletionSeconds,
                DeltaSeconds = adaptive?.DeltaSeconds
            });
        }
        catch
        {
            // Audit failure must not interrupt a successful route cycle.
        }
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
        return _durationCombo.SelectedIndex switch
        {
            0 => TaskDurationCategory.Short,
            2 => TaskDurationCategory.Long,
            _ => TaskDurationCategory.Medium
        };
    }

    private static string PreferenceDisplayName(AdaptivePreference? preference) => preference switch
    {
        AdaptivePreference.Balanced => "均衡",
        AdaptivePreference.Speed => "速度",
        _ => "经济"
    };

    private static string ResolveAdaptiveRank(
        long? groupId,
        IReadOnlyDictionary<long, AdaptiveCandidateRanking> rankings,
        out int rankValue)
    {
        rankValue = int.MaxValue;
        if (groupId is not { } id || !rankings.TryGetValue(id, out var ranking))
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

    private void ResetAuthenticationAndRoutingService()
    {
        _currentSession = null;
        InvalidateRoutingService();
    }

    private void InvalidateRoutingService()
    {
        _routingService?.Dispose();
        _routingService = null;
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

                failed.Add($"{key.Name}: {SafeErrorPresentation.GetMessage(exception)}");
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
        _adaptiveRankings = [];
        if (_summary is null)
        {
            return;
        }

        var platform = _platformCombo.SelectedItem?.ToString() ?? "openai";
        var criteria = new RoutingCriteria(
            platform,
            (double)_minimumSuccessInput.Value / 100,
            TimeSpan.FromMinutes(15));

        var now = DateTimeOffset.UtcNow;
        var selectedGroupIds = CurrentKeyRows()
            .Where(row => row.Selected && row.GroupId is > 0)
            .Where(row => row.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.GroupId!.Value)
            .Distinct()
            .ToArray();
        var observedGroupId = selectedGroupIds.Length == 1 ? selectedGroupIds[0] : (long?)null;
        var previewCurrentGroupId = observedGroupId;
        if (previewCurrentGroupId is null && selectedGroupIds.Length > 1)
        {
            var storageDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIHubRouter");
            previewCurrentGroupId = new JsonRouteStateStore(storageDirectory).Load().CurrentGroupId;
        }
        var policy = new BalancedRoutingPolicy
        {
            Platform = platform,
            Mode = CurrentRoutingMode(),
            MinimumSuccessRate6h = criteria.MinimumSuccessRate6h,
            MaximumStatusAge = criteria.MaximumStatusAge
        };
        var currentInterval = previewCurrentGroupId is { } groupId
            ? AdaptiveSwitchDecisionEngine.ResolveCurrentIntervalSeconds(
                _summary.Apis,
                groupId,
                platform,
                now)
            : null;
        var effectivePreference = AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(
            currentInterval,
            AdaptiveSwitchDecisionEngine.ToPreference(policy.Mode));
        var effectivePolicy = policy with
        {
            Mode = AdaptiveSwitchDecisionEngine.ToRoutingMode(effectivePreference)
        };
        _lastEvaluation = RoutingEngine.Evaluate(
            _summary.Apis,
            _groups,
            _userRates,
            effectivePolicy,
            now);
        _bestCandidate = _lastEvaluation.Recommended;
        if (previewCurrentGroupId is { } currentGroup &&
            _lastEvaluation.EligibleCandidates.Any(candidate => candidate.Group.Id == currentGroup))
        {
            var preview = RouteDecisionEngine.Decide(
                _lastEvaluation,
                new RouteState { CurrentGroupId = currentGroup },
                effectivePolicy,
                new AdaptiveRoutingContext(CurrentRoutingMode(), CurrentDurationCategory(), currentInterval),
                now,
                observedGroupId);
            _adaptiveRankings = preview.Decision.AdaptiveRankings;
            _bestCandidate = preview.Decision.Target ?? _bestCandidate;
        }

        ApplyProviders(criteria);
        _candidateLabel.Text = _bestCandidate is null
            ? $"{ModeDisplayName()}：无符合项"
            : $"{ModeDisplayName()}：{_bestCandidate.Provider.PlanType}  {_bestCandidate.EffectiveMultiplier:0.####}x";
    }

    private void ApplyProviders(RoutingCriteria criteria)
    {
        if (_summary is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var groups = _groups.ToDictionary(group => group.Id);
        var adaptiveRankingLookup = _adaptiveRankings.ToDictionary(ranking => ranking.GroupId);
        var rows = _summary.Apis
            .Where(provider => provider.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .Select(provider =>
            {
                var isAuthorized = provider.GroupId is { } groupId && groups.ContainsKey(groupId);
                var overrideRate = 0d;
                var hasOverride = provider.GroupId is { } id && _userRates.TryGetValue(id, out overrideRate);
                var effectiveRate = hasOverride ? overrideRate : provider.PriceMultiplier;
                var adaptiveRank = ResolveAdaptiveRank(provider.GroupId, adaptiveRankingLookup, out var adaptiveRankValue);
                if (provider.GroupId is { } candidateGroup &&
                    _lastEvaluation?.EligibleCandidates.FirstOrDefault(candidate => candidate.Group.Id == candidateGroup) is { } evaluatedCandidate)
                {
                    effectiveRate = evaluatedCandidate.EffectiveMultiplier;
                    hasOverride = evaluatedCandidate.HasUserRateOverride;
                }
                return new ProviderGridRow
                {
                    Source = provider,
                    IsBest = _bestCandidate?.Provider.Id == provider.Id,
                    EffectiveRate = hasOverride ? $"{effectiveRate:0.####} *" : $"{effectiveRate:0.####}",
                    WeightedScore = provider.GroupId is { } scoreGroupId &&
                        _lastEvaluation?.CandidateScores.TryGetValue(scoreGroupId, out var score) == true
                            ? score.ToString("0.###")
                            : "-",
                    AdaptiveRank = adaptiveRank,
                    AdaptiveRankValue = adaptiveRankValue,
                    DecisionState = provider.GroupId is { } recommendedGroupId &&
                        recommendedGroupId == _lastEvaluation?.Recommended?.Group.Id ? "推荐"
                        : provider.GroupId is { } baselineGroupId &&
                            baselineGroupId == _lastEvaluation?.Baseline?.Group.Id ? "最低价"
                        : string.Empty,
                    State = ProviderStatusPresentation.ResolveRoutingState(
                        provider,
                        hasAccountData: _groups.Count > 0,
                        isAuthorized: isAuthorized,
                        effectiveMultiplier: effectiveRate,
                        minimumSuccessRate6h: criteria.MinimumSuccessRate6h,
                        now: now,
                        maximumStatusAge: criteria.MaximumStatusAge)
                };
            })
            .OrderBy(row => row.AdaptiveRankValue)
            .ThenByDescending(row => row.IsBest)
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
        InvalidateRoutingService();
        RecalculateCandidate();
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
            _durationCombo.SelectedIndex = settings.DurationCategory switch
            {
                TaskDurationCategory.Short => 0,
                TaskDurationCategory.Long => 2,
                _ => 1
            };
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
        _applyingSessionCredentials = true;
        try
        {
            _tokenText.Text = session.AccessToken;
        }
        finally
        {
            _applyingSessionCredentials = false;
        }
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
