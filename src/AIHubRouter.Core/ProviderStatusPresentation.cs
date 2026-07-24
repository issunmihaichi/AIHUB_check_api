namespace AIHubRouter.Core;

public static class ProviderStatusPresentation
{
    public static bool IsRoutable(
        ProviderStatus provider,
        bool hasAccountData,
        bool isAuthorized,
        double effectiveMultiplier,
        double minimumSuccessRate6h,
        DateTimeOffset now,
        TimeSpan maximumStatusAge,
        TimeSpan? activeProbeMaximumAge = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var isRateValid = provider.PriceMultiplier >= 0 &&
            double.IsFinite(provider.PriceMultiplier) &&
            effectiveMultiplier >= 0 &&
            double.IsFinite(effectiveMultiplier);
        var hasEvidence = RoutingEngine.HasUsableRoutingEvidence(provider, now, activeProbeMaximumAge);
        return provider.Enabled &&
            provider.Available &&
            !RoutingEngine.HasFreshActiveProbeFailure(provider, now, activeProbeMaximumAge) &&
            isRateValid &&
            hasEvidence &&
            RoutingEngine.NormalizeSuccessRate(provider.SuccessRate6h) >= minimumSuccessRate6h &&
            (!hasAccountData || isAuthorized);
    }

    public static string ResolveRoutingState(
        ProviderStatus provider,
        bool hasAccountData,
        bool isAuthorized,
        double effectiveMultiplier,
        double minimumSuccessRate6h,
        DateTimeOffset now,
        TimeSpan maximumStatusAge,
        TimeSpan? activeProbeMaximumAge = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var isRateValid = provider.PriceMultiplier >= 0 &&
            double.IsFinite(provider.PriceMultiplier) &&
            effectiveMultiplier >= 0 &&
            double.IsFinite(effectiveMultiplier);
        var hasEvidence = RoutingEngine.HasUsableRoutingEvidence(provider, now, activeProbeMaximumAge);
        var evidenceWeight = hasEvidence
            ? RoutingEngine.CalculateEvidenceWeight(
                RoutingEngine.GetLatestEvidenceTimestamp(provider, now, activeProbeMaximumAge),
                now,
                maximumStatusAge)
            : 0;
        var state = !provider.Enabled ? "已停用"
            : !provider.Available ? "当前异常"
            : RoutingEngine.HasFreshActiveProbeFailure(provider, now, activeProbeMaximumAge) ? "健康检查失败"
            : !isRateValid ? "倍率无效"
            : !hasEvidence ? "数据过期"
            : RoutingEngine.NormalizeSuccessRate(provider.SuccessRate6h) < minimumSuccessRate6h ? "低于阈值"
            : hasAccountData && !isAuthorized ? "账号不可用"
            : !hasAccountData ? "待认证"
            : evidenceWeight < 1 ? "数据陈旧（已降权）"
            : "可路由";
        return DecorateRoutableState(state, provider);
    }

    public static string DecorateRoutableState(string state, ProviderStatus provider)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(provider);
        return state == "可路由" && provider.HasWarnings
            ? "可路由（警告）"
            : state;
    }
}
