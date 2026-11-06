using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Looot;

/// <summary>
/// Thrown for every non-2xx answer, for a missing token, and for network failures and timeouts
/// (<see cref="Status"/> 0). A run that failed is not an exception: <c>RunAsync</c> returns the
/// run with Status "failed" and Error set.
/// </summary>
public class LoootException : Exception
{
    /// <summary>The HTTP status, or 0 for a network failure or timeout.</summary>
    public int Status { get; }

    /// <summary>
    /// error.code from the body, else "http_&lt;status&gt;". Local codes: missing_token,
    /// network_error, timeout, invalid_response.
    /// </summary>
    public string Code { get; }

    /// <summary>error.requestId, else the x-request-id header.</summary>
    public string? RequestId { get; init; }

    /// <summary>True when the same request may succeed later (with the same idempotency key).</summary>
    public bool Retryable { get; init; }

    /// <summary>The gateway's one-sentence advice, when it sends one.</summary>
    public string? RetryHint { get; init; }

    /// <summary>The body's retryAfterSeconds, else the Retry-After header. Null when unset.</summary>
    public double? RetryAfterSeconds { get; init; }

    /// <summary>Set on a 402 insufficient_balance: where the user can add credit.</summary>
    public TopUpLink? TopUp { get; init; }

    /// <summary>The decoded JSON body, when the body was JSON.</summary>
    public JsonElement? Body { get; init; }

    /// <summary>The body as text, when there was one.</summary>
    public string? BodyText { get; init; }

    /// <summary>The idempotency key the failed request carried. Reuse it to retry a paid run safely.</summary>
    public string? IdempotencyKey { get; internal set; }

    /// <summary>The HTTP method of the request that failed.</summary>
    public string? Method { get; init; }

    /// <summary>The path of the request that failed.</summary>
    public string? Path { get; init; }

    /// <summary>Creates an error. The SDK sets the optional fields.</summary>
    public LoootException(int status, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Code = code;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = $"LoootException: {Message} ({Code}";
        if (Status != 0) text += ", HTTP " + Status.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(RequestId)) text += ", request " + RequestId;
        return text + ")";
    }

    internal bool ShouldAutoRetry()
    {
        if (!Retryable) return false;
        return Status switch
        {
            429 or 503 => true,
            0 => Code is "network_error" or "timeout",
            _ => false,
        };
    }

    internal static bool RetryableByStatus(int status, string code) => status switch
    {
        429 or 500 => true,
        503 => code != "billing_unavailable",
        _ => false,
    };

    internal static LoootException Transport(string code, string message, string method, string path, Exception? inner) =>
        new(0, code, message, inner) { Retryable = true, Method = method, Path = path };

    internal static LoootException FromResponse(string method, string path, HttpResponseMessage response, byte[] data)
    {
        var status = (int)response.StatusCode;
        var code = "http_" + status.ToString(CultureInfo.InvariantCulture);
        var message = $"{method} {path} failed with HTTP {status}";
        string? requestId = HeaderValue(response, "x-request-id");
        string? hint = null;
        double? retryAfter = null;
        bool? retryable = null;
        JsonElement? body = null;
        TopUpLink? topUp = null;
        string? text = data.Length > 0 ? System.Text.Encoding.UTF8.GetString(data) : null;

        if (data.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement.Clone();
                body = root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var env) && env.ValueKind == JsonValueKind.Object)
                    {
                        code = Str(env, "code") ?? code;
                        message = Str(env, "message") ?? message;
                        requestId = Str(env, "requestId") ?? requestId;
                        hint = Str(env, "retryHint");
                        if (env.TryGetProperty("retryable", out var r)
                            && (r.ValueKind == JsonValueKind.True || r.ValueKind == JsonValueKind.False))
                        {
                            retryable = r.GetBoolean();
                        }
                        retryAfter = Num(env, "retryAfterSeconds") ?? retryAfter;
                    }
                    retryAfter = Num(root, "retryAfterSeconds") ?? retryAfter;
                    hint ??= Str(root, "retryHint");
                    if (root.TryGetProperty("topUp", out var t) && t.ValueKind == JsonValueKind.Object)
                    {
                        try
                        {
                            topUp = t.Deserialize<TopUpLink>(LoootClient.JsonOptions);
                        }
                        catch (JsonException)
                        {
                            // A malformed topUp object must not hide the real error.
                        }
                    }
                }
            }
            catch (JsonException)
            {
                body = null;
            }
        }

        retryAfter ??= ParseRetryAfter(response);
        return new LoootException(status, code, message)
        {
            RequestId = requestId,
            Retryable = retryable ?? RetryableByStatus(status, code),
            RetryHint = hint,
            RetryAfterSeconds = retryAfter,
            TopUp = topUp,
            Body = body,
            BodyText = text,
            Method = method,
            Path = path,
        };
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString())
            ? v.GetString()
            : null;

    private static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d > 0
            ? d
            : null;

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static double? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return delta.TotalSeconds > 0 ? delta.TotalSeconds : null;
        if (ra.Date is { } date)
        {
            var seconds = (date - DateTimeOffset.UtcNow).TotalSeconds;
            return seconds > 0 ? seconds : null;
        }
        return null;
    }
}
