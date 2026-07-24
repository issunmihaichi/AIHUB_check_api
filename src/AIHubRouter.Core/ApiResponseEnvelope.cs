using System.Globalization;
using System.Text.Json;

namespace AIHubRouter.Core;

internal enum ApiResponseEnvelopeOutcome
{
    Direct,
    Success,
    Error,
    MissingPayload
}

internal readonly record struct ApiResponseEnvelopeResult(
    ApiResponseEnvelopeOutcome Outcome,
    JsonElement Payload,
    string? ApiCode)
{
    public bool IsFailure =>
        Outcome is ApiResponseEnvelopeOutcome.Error or ApiResponseEnvelopeOutcome.MissingPayload;
}

internal static class ApiResponseEnvelope
{
    private static readonly string[] PayloadPropertyNames = ["data", "result", "payload"];

    public static ApiResponseEnvelopeResult Classify(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Direct(root);
        }

        var hasCode = TryGetProperty(root, "code", out var code);
        var hasSuccess = TryGetProperty(root, "success", out var success);
        var hasStatus = TryGetProperty(root, "status", out var status);
        var hasErrorProperty = TryGetProperty(root, "error", out var error);
        var hasError = hasErrorProperty && HasErrorValue(error);
        var payload = FindPayload(root);
        var hasEnvelopeContext = hasCode || hasSuccess || hasError || payload.HasWrapper;
        var statusIsFailure = hasStatus && hasEnvelopeContext && !IsSuccessScalar(status);

        if (hasSuccess)
        {
            if (!TryReadBoolean(success, out var succeeded) || !succeeded)
            {
                return Error(root, statusIsFailure);
            }
        }

        if (hasError)
        {
            return Error(root, statusIsFailure);
        }

        if (hasCode && !IsSuccessScalar(code))
        {
            return Error(root, statusIsFailure);
        }

        if (statusIsFailure)
        {
            return Error(root, includeTopStatus: true);
        }

        var hasSuccessSignal =
            (hasCode && IsSuccessScalar(code)) ||
            hasSuccess ||
            (hasStatus && hasEnvelopeContext && IsSuccessScalar(status));

        if (!hasSuccessSignal && !payload.HasWrapper)
        {
            return Direct(root);
        }

        if (!payload.HasValue)
        {
            return new ApiResponseEnvelopeResult(
                ApiResponseEnvelopeOutcome.MissingPayload,
                default,
                ResolveApiCode(root, statusIsFailure));
        }

        return new ApiResponseEnvelopeResult(
            ApiResponseEnvelopeOutcome.Success,
            payload.Value,
            ResolveApiCode(root));
    }

    private static ApiResponseEnvelopeResult Direct(JsonElement root) =>
        new(ApiResponseEnvelopeOutcome.Direct, root, null);

    private static ApiResponseEnvelopeResult Error(JsonElement root, bool includeTopStatus) =>
        new(ApiResponseEnvelopeOutcome.Error, default, ResolveApiCode(root, includeTopStatus));

    private static (bool HasWrapper, bool HasValue, JsonElement Value) FindPayload(JsonElement root)
    {
        var hasWrapper = false;
        foreach (var propertyName in PayloadPropertyNames)
        {
            if (!TryGetProperty(root, propertyName, out var value))
            {
                continue;
            }

            hasWrapper = true;
            if (value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                return (true, true, value);
            }
        }

        return (hasWrapper, false, default);
    }

    private static bool HasErrorValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Object => value.EnumerateObject().Any(),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => !value.TryGetDecimal(out var number) || number != 0,
            _ => true
        };
    }

    private static bool TryReadBoolean(JsonElement value, out bool result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                result = false;
                return true;
            case JsonValueKind.String when bool.TryParse(value.GetString(), out result):
                return true;
            case JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1:
                result = number == 1;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool IsSuccessScalar(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var numericValue))
        {
            return IsSuccessNumber(numericValue);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString()?.Trim();
        if (string.Equals(text, "OK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out numericValue) &&
               IsSuccessNumber(numericValue);
    }

    private static bool IsSuccessNumber(decimal value) => value == 0 || value is >= 200 and <= 299;

    private static string? ResolveApiCode(JsonElement root, bool includeTopStatus = false)
    {
        if (TryGetProperty(root, "error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(error, "code", out var nestedCode))
            {
                var value = ReadScalar(nestedCode);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (TryGetProperty(error, "status", out var nestedStatus) && !IsSuccessScalar(nestedStatus))
            {
                return ReadScalar(nestedStatus);
            }
        }

        if (TryGetProperty(root, "code", out var code))
        {
            return ReadScalar(code);
        }

        if (includeTopStatus && TryGetProperty(root, "status", out var status))
        {
            return ReadScalar(status);
        }

        return null;
    }

    private static string? ReadScalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
