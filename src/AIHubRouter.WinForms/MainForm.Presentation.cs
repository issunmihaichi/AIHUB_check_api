using System.Diagnostics;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{

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
            RouteDecisionReason.BalancedDeadlineColdStart => "均衡冷启动：优先最低倍率可行节点",
            RouteDecisionReason.BalancedDeadlineCurrentWithinDeadline => "均衡：当前节点满足截止时间，保持不切换",
            RouteDecisionReason.BalancedDeadlineSwitched => "均衡：当前节点超时，切换到满足截止时间的最低成本节点",
            RouteDecisionReason.BalancedDeadlineFastestFallback => "均衡：没有节点满足截止时间，切换到最快节点",
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
