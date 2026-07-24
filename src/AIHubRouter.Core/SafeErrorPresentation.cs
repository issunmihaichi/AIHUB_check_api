using System.Net;

namespace AIHubRouter.Core;

public static class SafeErrorPresentation
{
    public static string GetMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            InteractiveAuthenticationRequiredException =>
                "当前账号需要验证码或两步验证，请先在 AIHub 网页完成登录验证。",
            ActiveProbeRestoreException =>
                "测速专用 Key 未能恢复原分组，已停止自动测速。请确认 AIHub 控制台中的 Key 分组后重试。",
            AIHubApiException apiException => GetApiMessage(apiException),
            HttpRequestException => "网络连接失败，请检查站点地址和网络。",
            TaskCanceledException => "请求超时，请稍后重试。",
            ArgumentException => "输入内容无效，请检查站点地址和认证字段。",
            InvalidOperationException => "当前操作无法完成，请检查认证配置后重试。",
            _ => "操作失败，请重试。"
        };
    }

    private static string GetApiMessage(AIHubApiException exception)
    {
        if (exception.StatusCode is { } successfulStatus &&
            (int)successfulStatus is >= 200 and <= 299)
        {
            return exception.IsAuthenticationRequest
                ? "认证响应表示失败或格式不兼容，请重新验证登录信息。"
                : "AIHub 返回了业务错误或无法识别的响应格式，请稍后重试。";
        }

        if (exception.IsAuthenticationRequest)
        {
            return exception.StatusCode switch
            {
                HttpStatusCode.Forbidden =>
                    "认证被平台策略拒绝：账号可能已停用，或站点当前仅允许管理员登录。",
                HttpStatusCode.TooManyRequests =>
                    "登录尝试过于频繁，请等待约 1 分钟后再试。",
                HttpStatusCode.ServiceUnavailable =>
                    "平台认证服务暂时不可用，请稍后重试。",
                _ =>
                    "认证失败：邮箱或密码不正确，或保存的 Token/session 已失效。"
            };
        }

        if (exception.IsAuthenticationFailure)
        {
            return "认证失败：保存的 Token/session 已失效，请重新验证。";
        }

        return exception.StatusCode switch
        {
            HttpStatusCode.Forbidden => "当前账号没有执行该操作的权限。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后重试。",
            HttpStatusCode.ServiceUnavailable => "AIHub 服务暂时不可用，请稍后重试。",
            { } statusCode => $"AIHub 请求失败（HTTP {(int)statusCode}）。",
            null => "AIHub 响应无法处理，请稍后重试。"
        };
    }
}
