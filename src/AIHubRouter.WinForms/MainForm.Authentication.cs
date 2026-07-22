using System.Text.Json;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{

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

    private void ResetAuthenticationAndRoutingService()
    {
        _currentSession = null;
        _providerMetrics.Clear();
        InvalidateRoutingService();
    }

    private void InvalidateRoutingService()
    {
        _routingService?.Dispose();
        _routingService = null;
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
