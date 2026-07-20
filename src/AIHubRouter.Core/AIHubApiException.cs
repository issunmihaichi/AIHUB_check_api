using System.Net;

namespace AIHubRouter.Core;

public sealed class AIHubApiException : Exception
{
    public AIHubApiException(string message, HttpStatusCode? statusCode = null, string? apiCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        ApiCode = apiCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ApiCode { get; }
}
