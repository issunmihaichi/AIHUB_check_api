namespace AIHubRouter.Core;

public static class ProviderStatusPresentation
{
    public static string DecorateRoutableState(string state, ProviderStatus provider)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(provider);
        return state == "可路由" && provider.HasWarnings
            ? "可路由（警告）"
            : state;
    }
}
