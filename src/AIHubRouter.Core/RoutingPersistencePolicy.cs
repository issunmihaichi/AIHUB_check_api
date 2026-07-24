namespace AIHubRouter.Core;

public static class RoutingPersistencePolicy
{
    public static bool ShouldPersistCredentials(
        bool persistCredentials,
        bool applyingRoutingSettings,
        bool suppressRoutingPersistence) =>
        persistCredentials && !applyingRoutingSettings && !suppressRoutingPersistence;
}
