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
        TimeSpan maximumStatusAge)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var isRateValid = provider.PriceMultiplier >= 0 &&
            double.IsFinite(provider.PriceMultiplier) &&
            effectiveMultiplier >= 0 &&
            double.IsFinite(effectiveMultiplier);
        var isFresh = RoutingEngine.HasFreshRoutingEvidence(provider, now, maximumStatusAge);
        return provider.Enabled &&
            provider.Available &&
            isRateValid &&
            isFresh &&
            (provider.SuccessRate6h ?? 0) >= minimumSuccessRate6h &&
            (!hasAccountData || isAuthorized);
    }

    public static string ResolveRoutingState(
        ProviderStatus provider,
        bool hasAccountData,
        bool isAuthorized,
        double effectiveMultiplier,
        double minimumSuccessRate6h,
        DateTimeOffset now,
        TimeSpan maximumStatusAge)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var isRateValid = provider.PriceMultiplier >= 0 &&
            double.IsFinite(provider.PriceMultiplier) &&
            effectiveMultiplier >= 0 &&
            double.IsFinite(effectiveMultiplier);
        var isFresh = RoutingEngine.HasFreshRoutingEvidence(provider, now, maximumStatusAge);
        var state = !provider.Enabled ? "已停用"
            : !provider.Available ? "当前异常"
            : !isRateValid ? "倍率无效"
            : !isFresh ? "数据过期"
            : (provider.SuccessRate6h ?? 0) < minimumSuccessRate6h ? "低于阈值"
            : hasAccountData && !isAuthorized ? "账号不可用"
            : !hasAccountData ? "待认证"
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
