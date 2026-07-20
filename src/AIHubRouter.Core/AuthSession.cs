namespace AIHubRouter.Core;

public sealed record AuthSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt)
{
    public bool IsUsable(DateTimeOffset now, TimeSpan renewalMargin)
    {
        return !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAt - now > renewalMargin;
    }
}

public sealed record LoginCredentials(string Email, string Password)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(Password);
}
