using System.ComponentModel;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{

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
        var rawSummary = await client.GetProviderSummaryAsync(cancellationToken);

        if (loadAccountData)
        {
            var groupsTask = client.GetAvailableGroupsAsync(cancellationToken);
            var ratesTask = client.GetUserGroupRatesAsync(cancellationToken);
            var keysTask = client.GetAllKeysAsync(cancellationToken);
            await Task.WhenAll(groupsTask, ratesTask, keysTask);

            _groups = await groupsTask;
            _rawUserRates = await ratesTask;
            ApplyKeys(await keysTask);
        }

        var metrics = _providerMetrics.Observe(DateTimeOffset.UtcNow, rawSummary.Apis, _rawUserRates);
        _summary = new MonitorSummary
        {
            Apis = metrics.Providers.ToList(),
            GeneratedAt = rawSummary.GeneratedAt,
            MonitoringActive = rawSummary.MonitoringActive
        };
        _userRates = metrics.UserGroupRates;

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
            UserAgent = _userAgentText.Text,
            ActiveProbeApiKey = _activeProbeApiKey
        };
        _routingService = new RoutingService(
            settings,
            credentials,
            new JsonRouteStateStore(storageDirectory),
            persistCredentials: PersistRoutingCredentialsAsync,
            providerMetrics: _providerMetrics);
        return _routingService;
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
        var candidateLookup = result.Evaluation.EligibleCandidates.ToDictionary(
            candidate => candidate.Provider.Id,
            StringComparer.Ordinal);
        var adaptiveRankingLookup = result.Decision.AdaptiveRankings.ToDictionary(
            ranking => ranking.ProviderId,
            StringComparer.Ordinal);
        var minimumSuccessRate6h = (double)_minimumSuccessInput.Value / 100;
        var maximumStatusAge = TimeSpan.FromMinutes(15);
        var rows = result.Providers
            .Where(provider => provider.Platform.Equals(_platformCombo.SelectedItem?.ToString() ?? "openai", StringComparison.OrdinalIgnoreCase))
            .Select(provider =>
            {
                var groupId = provider.GroupId;
                var hasCandidate = candidateLookup.TryGetValue(provider.Id, out var candidate);
                var candidateScore = double.NegativeInfinity;
                var hasScore = hasCandidate && result.Evaluation.CandidateScores.TryGetValue(
                    candidate!.Group.Id,
                    out candidateScore);
                var score = hasScore ? candidateScore.ToString("0.###") : "-";
                var scoreValue = hasScore ? candidateScore : double.NegativeInfinity;
                var adaptiveRank = ResolveAdaptiveRank(provider.Id, adaptiveRankingLookup, out var adaptiveRankValue);
                var decisionState = groupId is { } decisionGroupId && decisionGroupId == result.Decision.Target?.Group.Id ? "推荐"
                    : groupId is { } currentGroupId && currentGroupId == result.Decision.Current?.Group.Id ? "当前"
                    : groupId is { } baselineGroupId && baselineGroupId == result.Evaluation.Baseline?.Group.Id ? "最低价"
                    : string.Empty;
                var group = groupId is { } routeGroupId && groupLookup.TryGetValue(routeGroupId, out var foundGroup)
                    ? foundGroup
                    : null;
                var blockReason = _providerBlocklist.GetBlockingReason(provider, group);
                var isBlocked = blockReason != ProviderBlockReason.None;
                if (!hasCandidate || isBlocked)
                {
                    decisionState = string.Empty;
                }
                var overrideRate = 0d;
                var hasOverride = groupId is { } overrideGroupId && _userRates.TryGetValue(overrideGroupId, out overrideRate);
                var effectiveMultiplier = hasOverride ? overrideRate : provider.PriceMultiplier;
                if (hasCandidate)
                {
                    effectiveMultiplier = candidate!.EffectiveMultiplier;
                    hasOverride = candidate.HasUserRateOverride;
                }

                var isAuthorized = group is not null;
                var isRoutable = !isBlocked && ProviderStatusPresentation.IsRoutable(
                    provider,
                    hasAccountData: true,
                    isAuthorized,
                    effectiveMultiplier,
                    minimumSuccessRate6h,
                    result.Decision.EvaluatedAt,
                    maximumStatusAge);
                var effective = $"{effectiveMultiplier:0.####}{(hasOverride ? " *" : string.Empty)}";
                return new ProviderGridRow
                {
                    Source = provider,
                    IsRoutable = isRoutable,
                    IsBest = !isBlocked && provider.Id == result.Decision.Target?.Provider.Id,
                    IsBlocked = isBlocked,
                    EffectiveRate = effective,
                    WeightedScore = score,
                    WeightedScoreValue = scoreValue,
                    AdaptiveRank = adaptiveRank,
                    AdaptiveRankValue = adaptiveRankValue,
                    DecisionState = decisionState,
                    BlockStatus = BlockReasonText(blockReason),
                    State = isBlocked ? "已拉黑" : ProviderStatusPresentation.ResolveRoutingState(
                        provider,
                        hasAccountData: true,
                        isAuthorized,
                        effectiveMultiplier: effectiveMultiplier,
                        minimumSuccessRate6h: minimumSuccessRate6h,
                        now: result.Decision.EvaluatedAt,
                        maximumStatusAge: maximumStatusAge)
                };
            })
            .OrderByDescending(row => row.IsRoutable)
            .ThenBy(row => row.IsBlocked)
            .ThenBy(row => row.AdaptiveRankValue)
            .ThenByDescending(row => row.IsBest)
            .ThenByDescending(row => row.WeightedScoreValue)
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
            var balanced = result.Decision.BalancedDeadlineDecision;
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
                    candidate.Group.Id == result.Decision.Target?.Group.Id)
                {
                    LatencyP90Ms = candidate.Provider.FirstTokenLatencyP90Ms,
                    OutputRateP25 = candidate.Provider.OutputTokensPerSecondP25,
                    PerformanceSampleCount = candidate.Provider.PerformanceSampleCount
                }).ToArray(),
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
                DeltaSeconds = adaptive?.DeltaSeconds,
                BalancedDeadlineReason = balanced?.Reason,
                BalancedOutputTokens = balanced?.OutputTokens,
                BalancedCurrentCompletionSeconds = balanced?.CurrentCompletionSeconds,
                BalancedTargetCompletionSeconds = balanced?.TargetCompletionSeconds,
                BalancedTargetCostUsd = balanced?.TargetCostUsd,
                SwitchClass = result.Decision.SwitchClass,
                LastPolicySwitchAt = result.NextRouteState.LastPolicySwitchAt,
                CompletedPolicyEvaluationsSinceLastSwitch =
                    result.NextRouteState.CompletedPolicyEvaluationsSinceLastSwitch,
                PendingPolicyTargetGroupId = result.NextRouteState.PendingPolicyTargetGroupId,
                PendingPolicyTargetObservations = result.NextRouteState.PendingPolicyTargetObservations
            });
        }
        catch
        {
            // Audit failure must not interrupt a successful route cycle.
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
            TimeSpan.FromMinutes(15),
            _providerBlocklist);

        var now = DateTimeOffset.UtcNow;
        var selectedGroupIds = CurrentKeyRows()
            .Where(row => row.Selected && row.GroupId is > 0)
            .Where(row => row.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.GroupId!.Value)
            .Distinct()
            .ToArray();
        var observedGroupId = selectedGroupIds.Length == 1 ? selectedGroupIds[0] : (long?)null;
        var previewCurrentGroupId = observedGroupId;
        var previewState = new RouteState();
        if (previewCurrentGroupId is null && selectedGroupIds.Length > 1)
        {
            var storageDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIHubRouter");
            previewState = new JsonRouteStateStore(storageDirectory).Load();
            previewCurrentGroupId = previewState.CurrentGroupId;
        }
        var policy = new BalancedRoutingPolicy
        {
            Platform = platform,
            Mode = CurrentRoutingMode(),
            MinimumSuccessRate6h = criteria.MinimumSuccessRate6h,
            MaximumStatusAge = criteria.MaximumStatusAge,
            Blocklist = _providerBlocklist
        };
        var snapshot = RouteDecisionCoordinator.Evaluate(
            _summary.Apis,
            _groups,
            _userRates,
            policy,
            CurrentDurationCategory(),
            previewState with { CurrentGroupId = previewCurrentGroupId },
            now,
            observedGroupId,
            CurrentRoutingMode() == RoutingMode.Balanced
                ? GetBalancedRemainingSeconds(now)
                : null,
            CurrentRoutingMode() == RoutingMode.Balanced
                ? _balancedDeadlineSoftSeconds
                : null,
            CurrentRoutingMode() == RoutingMode.Balanced
                ? _balancedExpectedOutputTokens
                : null);
        _lastEvaluation = snapshot.Evaluation;
        _adaptiveRankings = snapshot.Result.Decision.AdaptiveRankings;
        _bestCandidate = snapshot.Result.Decision.Target;

        ApplyProviders(criteria);
        var effectiveMode = PreferenceDisplayName(snapshot.Result.Decision.EffectivePreference);
        var modeText = effectiveMode == ModeDisplayName()
            ? effectiveMode
            : $"{ModeDisplayName()}→{effectiveMode}";
        _candidateLabel.Text = _bestCandidate is null
            ? $"{ModeDisplayName()}：无符合项"
            : $"{ModeDisplayName()}：{_bestCandidate.Provider.PlanType}  {_bestCandidate.EffectiveMultiplier:0.####}x";
        _candidateLabel.Text = _candidateLabel.Text.Replace(
            ModeDisplayName(),
            modeText,
            StringComparison.Ordinal);
    }

    private void ApplyProviders(RoutingCriteria criteria)
    {
        if (_summary is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var groups = _groups.ToDictionary(group => group.Id);
        var adaptiveRankingLookup = _adaptiveRankings.ToDictionary(
            ranking => ranking.ProviderId,
            StringComparer.Ordinal);
        var candidateLookup = _lastEvaluation?.EligibleCandidates.ToDictionary(
            candidate => candidate.Provider.Id,
            StringComparer.Ordinal) ?? new Dictionary<string, RouteCandidate>(StringComparer.Ordinal);
        var rows = _summary.Apis
            .Where(provider => provider.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .Select(provider =>
            {
                var group = provider.GroupId is { } groupId && groups.TryGetValue(groupId, out var foundGroup)
                    ? foundGroup
                    : null;
                var isAuthorized = group is not null;
                var blockReason = (criteria.Blocklist ?? ProviderBlocklist.Empty).GetBlockingReason(provider, group);
                var isBlocked = blockReason != ProviderBlockReason.None;
                var overrideRate = 0d;
                var hasOverride = provider.GroupId is { } id && _userRates.TryGetValue(id, out overrideRate);
                var effectiveRate = hasOverride ? overrideRate : provider.PriceMultiplier;
                var hasCandidate = candidateLookup.TryGetValue(provider.Id, out var evaluatedCandidate);
                var adaptiveRank = ResolveAdaptiveRank(provider.Id, adaptiveRankingLookup, out var adaptiveRankValue);
                if (hasCandidate)
                {
                    effectiveRate = evaluatedCandidate!.EffectiveMultiplier;
                    hasOverride = evaluatedCandidate.HasUserRateOverride;
                }
                var candidateScore = double.NegativeInfinity;
                var hasScore = hasCandidate && _lastEvaluation!.CandidateScores.TryGetValue(
                    evaluatedCandidate!.Group.Id,
                    out candidateScore);
                var score = hasScore ? candidateScore.ToString("0.###") : "-";
                var scoreValue = hasScore ? candidateScore : double.NegativeInfinity;
                var isRoutable = !isBlocked && ProviderStatusPresentation.IsRoutable(
                    provider,
                    hasAccountData: _groups.Count > 0,
                    isAuthorized,
                    effectiveRate,
                    criteria.MinimumSuccessRate6h,
                    now,
                    criteria.MaximumStatusAge);
                return new ProviderGridRow
                {
                    Source = provider,
                    IsRoutable = isRoutable,
                    IsBest = !isBlocked && _bestCandidate?.Provider.Id == provider.Id,
                    IsBlocked = isBlocked,
                    EffectiveRate = hasOverride ? $"{effectiveRate:0.####} *" : $"{effectiveRate:0.####}",
                    WeightedScore = score,
                    WeightedScoreValue = scoreValue,
                    AdaptiveRank = adaptiveRank,
                    AdaptiveRankValue = adaptiveRankValue,
                    DecisionState = hasCandidate && provider.GroupId is { } recommendedGroupId &&
                        recommendedGroupId == _lastEvaluation?.Recommended?.Group.Id ? "推荐"
                        : hasCandidate && provider.GroupId is { } baselineGroupId &&
                            baselineGroupId == _lastEvaluation?.Baseline?.Group.Id ? "最低价"
                        : string.Empty,
                    BlockStatus = BlockReasonText(blockReason),
                    State = isBlocked ? "已拉黑" : ProviderStatusPresentation.ResolveRoutingState(
                        provider,
                        hasAccountData: _groups.Count > 0,
                        isAuthorized: isAuthorized,
                        effectiveMultiplier: effectiveRate,
                        minimumSuccessRate6h: criteria.MinimumSuccessRate6h,
                        now: now,
                        maximumStatusAge: criteria.MaximumStatusAge)
                };
            })
            .OrderByDescending(row => row.IsRoutable)
            .ThenBy(row => row.IsBlocked)
            .ThenBy(row => row.AdaptiveRankValue)
            .ThenByDescending(row => row.IsBest)
            .ThenBy(row => row.Source.PriceMultiplier)
            .ThenByDescending(row => row.Source.SuccessRate6h ?? 0)
            .ToList();

        _providerGrid.DataSource = new BindingList<ProviderGridRow>(rows);
    }

    private void ApplyKeys(IReadOnlyList<ApiKeyInfo> keys)
    {
        _keys = keys;
        var selectedIds = _hasLoadedKeys && _keySelectionInitialized
            ? CurrentKeyRows().Where(row => row.Selected).Select(row => row.Id).ToHashSet()
            : KeySelectionPolicy.Resolve(_keySelectionInitialized, _savedSelectedKeyIds, keys).ToHashSet();
        if (_activeProbeCheck.Checked && _activeProbeKeyId is { } activeProbeKeyId)
        {
            selectedIds.Remove(activeProbeKeyId);
        }
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
                    Purpose = IsActiveProbeKey(key.Id) ? "测速专用" : "路由",
                    IsProbeKey = IsActiveProbeKey(key.Id),
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

        if (_keyGrid.Rows[eventArgs.RowIndex].DataBoundItem is KeyGridRow { IsProbeKey: true } probeKey)
        {
            _applyingKeys = true;
            try
            {
                probeKey.Selected = false;
                _keyGrid.Refresh();
            }
            finally
            {
                _applyingKeys = false;
            }

            SetStatus("测速专用 Key 已从普通路由中排除。", success: true);
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
}
