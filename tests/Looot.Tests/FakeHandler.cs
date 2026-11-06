using System.Net;
using System.Text;

namespace Looot.Tests;

/// <summary>One request as the fake handler saw it.</summary>
public sealed class Captured
{
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    public string PathAndQuery { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Body { get; init; }

    public bool Has(string header) => Headers.ContainsKey(header);

    public string Header(string name) => Headers[name];

    /// <summary>The query string decoded into pairs, in order.</summary>
    public List<KeyValuePair<string, string>> Query()
    {
        var list = new List<KeyValuePair<string, string>>();
        var i = PathAndQuery.IndexOf('?');
        if (i < 0) return list;
        foreach (var part in PathAndQuery.Substring(i + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part.Substring(0, eq);
            var value = eq < 0 ? "" : part.Substring(eq + 1);
            list.Add(new(Uri.UnescapeDataString(key), Uri.UnescapeDataString(value)));
        }
        return list;
    }

    public string? Q(string key) => Query().FirstOrDefault(p => p.Key == key).Value;

    public string Path => PathAndQuery.Split('?')[0];
}

/// <summary>A scripted HttpMessageHandler. The last scripted response repeats.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<Func<Captured, HttpResponseMessage>> _script = new();
    public List<Captured> Requests { get; } = new();

    public FakeHandler Reply(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        _script.Add(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        });
        return this;
    }

    public FakeHandler Bytes(byte[] data, string contentType)
    {
        _script.Add(_ =>
        {
            var content = new ByteArrayContent(data);
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        return this;
    }

    public FakeHandler Throw(Exception ex)
    {
        _script.Add(_ => throw ex);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var captured = new Captured
        {
            Method = request.Method,
            PathAndQuery = request.RequestUri!.PathAndQuery,
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
        };
        foreach (var h in request.Headers) captured.Headers[h.Key] = string.Join(h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : ",", h.Value);
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers) captured.Headers[h.Key] = string.Join(",", h.Value);
        }
        Requests.Add(captured);

        if (_script.Count == 0) throw new InvalidOperationException("No scripted response.");
        var step = _script[Math.Min(Requests.Count - 1, _script.Count - 1)];
        return step(captured);
    }
}

public static class Make
{
    public const string Token = "test-token";

    public static LoootClient Client(FakeHandler handler, Action<LoootClientOptions>? tweak = null)
    {
        var options = new LoootClientOptions
        {
            Token = Token,
            BaseUrl = "https://gateway.test",
            HttpClient = new HttpClient(handler),
            RetryBaseDelay = TimeSpan.Zero,
        };
        tweak?.Invoke(options);
        return new LoootClient(options);
    }
}
