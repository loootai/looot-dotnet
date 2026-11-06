using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Looot;

/// <summary>Options for <see cref="LoootClient"/>. Every field is optional.</summary>
public sealed class LoootClientOptions
{
    /// <summary>
    /// The bearer token. When null, the client reads LOOOT_TOKEN. An explicit empty string means
    /// no token, even when LOOOT_TOKEN is set.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Another gateway, for example a local one. Default https://api.looot.ai.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Names your app or agent. Sent as X-Looot-Agent (cut to 64 characters) for usage analytics
    /// only. Never defaults to the SDK name.
    /// </summary>
    public string? Agent { get; set; }

    /// <summary>Per request timeout. Default 75 s, enough for a 60 s wait.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// How many times a retryable error (429, a retryable 503, a network error or a timeout) is
    /// retried with the same idempotency key. Default 2. 0 turns retries off.
    /// </summary>
    public int? MaxRetries { get; set; }

    /// <summary>First retry wait when the gateway sends no Retry-After. Default 500 ms, doubling.</summary>
    public TimeSpan? RetryBaseDelay { get; set; }

    /// <summary>Your own HttpClient, for tests or proxies. The client does not dispose it.</summary>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// The looot API client. Thread safe. Every method takes a CancellationToken.
/// HTTP errors are thrown as <see cref="LoootException"/>. A run that failed is not an HTTP
/// error: <see cref="RunAsync"/> returns it and the caller reads Status, Outcome and Error.
/// </summary>
public sealed class LoootClient : IDisposable
{
    /// <summary>The SDK version, sent in the User-Agent header.</summary>
    public const string Version = "0.1.0";

    /// <summary>The production looot API.</summary>
    public const string DefaultBaseUrl = "https://api.looot.ai";

    private const int MaxAgentLength = 64;
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(75);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _baseUrl;
    private readonly string _token;
    private readonly string _agent;
    private readonly string _userAgent;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBase;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Creates a client. Without a token option it reads LOOOT_TOKEN.</summary>
    public LoootClient(LoootClientOptions? options = null)
    {
        options ??= new LoootClientOptions();
        _baseUrl = (string.IsNullOrWhiteSpace(options.BaseUrl) ? DefaultBaseUrl : options.BaseUrl).Trim().TrimEnd('/');
        _token = (options.Token ?? Environment.GetEnvironmentVariable("LOOOT_TOKEN") ?? "").Trim();
        var agent = (options.Agent ?? "").Trim();
        _agent = agent.Length > MaxAgentLength ? agent.Substring(0, MaxAgentLength) : agent;
        _userAgent = "looot-dotnet/" + Version + " dotnet/" + Environment.Version.ToString(3);
        _timeout = options.Timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
        _maxRetries = options.MaxRetries is { } n && n >= 0 ? n : 2;
        _retryBase = options.RetryBaseDelay is { } d && d >= TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(500);
        if (options.HttpClient is { } supplied)
        {
            _http = supplied;
        }
        else
        {
            _http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
    }

    /// <summary>The gateway URL this client calls.</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>True when the client has a token. It never returns the token itself.</summary>
    public bool HasToken => _token.Length > 0;

    /// <summary>Describes the client without the token.</summary>
    public override string ToString() =>
        "LoootClient{" + _baseUrl + ", " + (_token.Length > 0 ? "token set" : "no token") + "}";

    /// <summary>Disposes the HttpClient when the SDK created it.</summary>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ---------- free public routes ----------

    /// <summary>Maps the catalog: categories by default, matching jobs with Topic. Free, no token sent.</summary>
    public Task<CatalogOverview> CatalogOverviewAsync(CatalogOverviewParams? p = null, CancellationToken cancellationToken = default)
    {
        var q = new Query();
        if (p is not null)
        {
            q.Str("depth", p.Depth).Str("category", p.Category).Str("platform", p.Platform).Str("topic", p.Topic);
        }
        return GetAsync<CatalogOverview>("/v1/catalog/overview", q, false, cancellationToken);
    }

    /// <summary>Lists published catalog items. Free, no token sent.</summary>
    public Task<PublicCatalogPage> PublicCatalogAsync(PublicCatalogParams? p = null, CancellationToken cancellationToken = default)
    {
        var q = new Query();
        if (p is not null)
        {
            q.Str("q", p.Q).Str("text", p.Text).Str("capability", p.Capability).Str("category", p.Category)
                .Str("provider", p.Provider).Str("operationId", p.OperationId).Int("limit", p.Limit).Str("cursor", p.Cursor);
        }
        return GetAsync<PublicCatalogPage>("/v1/public-catalog", q, false, cancellationToken);
    }

    /// <summary>Returns one published item. Free, no token sent.</summary>
    public Task<PublicCatalogItem> PublicCatalogItemAsync(string catalogItemId, CancellationToken cancellationToken = default) =>
        GetAsync<PublicCatalogItem>("/v1/public-catalog/" + Seg(catalogItemId), null, false, cancellationToken);

    /// <summary>Compares 2 to 20 public items. Free, no token sent.</summary>
    public Task<CatalogComparison> ComparePublicCatalogAsync(IReadOnlyList<string> catalogItemIds, CancellationToken cancellationToken = default) =>
        SendJsonAsync<CatalogComparison>(HttpMethod.Post, "/v1/public-catalog/compare", null, new CompareBody(catalogItemIds), false, null, cancellationToken);

    // ---------- catalog routes that need a token ----------

    /// <summary>Finds endpoints for a task in plain words. Needs catalog.read. Free.</summary>
    public Task<SearchResult> SearchAsync(SearchParams? p = null, CancellationToken cancellationToken = default)
    {
        var q = new Query();
        if (p is not null)
        {
            q.Str("q", p.Q).Int("limit", p.Limit).Int("offset", p.Offset).Str("category", p.Category)
                .Str("platform", p.Platform).Str("provider", p.Provider).Str("capability", p.Capability)
                .Bool("keyless", p.Keyless).Bool("verified", p.Verified).Bool("mock", p.Mock)
                .Long("maxPriceMicros", p.MaxPriceMicros).Str("prefer", p.Prefer);
        }
        return GetAsync<SearchResult>("/v1/catalog/search", q, true, cancellationToken);
    }

    /// <summary>Returns up to 5 ranked candidates for a task. Needs catalog.read. Free.</summary>
    public Task<DiscoverResult> DiscoverAsync(DiscoverParams? p = null, CancellationToken cancellationToken = default)
    {
        var q = new Query();
        if (p is not null)
        {
            q.Str("query", p.Query).Str("mode", p.Mode).Str("capability", p.Capability).Str("category", p.Category)
                .Str("provider", p.Provider).Str("prefer", p.Prefer).Int("resultLimit", p.ResultLimit)
                .Double("maxPrice", p.MaxPrice).Bool("includeUnavailable", p.IncludeUnavailable);
        }
        return GetAsync<DiscoverResult>("/v1/discover", q, true, cancellationToken);
    }

    /// <summary>Lists endpoints page by page. Needs catalog.read. Free.</summary>
    public Task<CatalogEndpointsPage> CatalogEndpointsAsync(CatalogEndpointsParams? p = null, CancellationToken cancellationToken = default)
    {
        var q = new Query();
        if (p is not null)
        {
            q.Str("capability", p.Capability).Str("category", p.Category).Str("platform", p.Platform)
                .Str("provider", p.Provider).Bool("keyless", p.Keyless).Bool("verified", p.Verified)
                .Bool("mock", p.Mock).Long("maxPriceMicros", p.MaxPriceMicros)
                .Bool("includeUnavailable", p.IncludeUnavailable).Bool("facets", p.Facets)
                .Int("limit", p.Limit).Str("cursor", p.Cursor);
        }
        return GetAsync<CatalogEndpointsPage>("/v1/catalog/endpoints", q, true, cancellationToken);
    }

    /// <summary>Returns one endpoint's inputs, price and hints. Takes an endpoint id, not "job:&lt;id&gt;". Free.</summary>
    public Task<Operation> InspectAsync(string endpointId, CancellationToken cancellationToken = default) =>
        GetAsync<Operation>("/v1/operations/" + Seg(endpointId), null, true, cancellationToken);

    // ---------- runs ----------

    /// <summary>
    /// Runs one endpoint. Paid. Needs runs.execute. A run refused before dispatch comes back as a
    /// run with Status "failed", not as an exception.
    /// </summary>
    public Task<Run> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var key = string.IsNullOrEmpty(request.IdempotencyKey) ? "sdk-" + Guid.NewGuid().ToString() : request.IdempotencyKey;
        object? fallback = request.Fallback is null ? null : request.Fallback.IsDefault() ? true : request.Fallback;
        var body = new RunBody
        {
            EndpointId = request.EndpointId,
            Input = request.Input ?? new Dictionary<string, object>(),
            IdempotencyKey = key,
            Fallback = fallback,
            Output = request.Output,
        };
        var q = new Query();
        if (request.Wait is > 0) q.Int("wait", request.Wait);
        return SendJsonAsync<Run>(HttpMethod.Post, "/v1/runs", q, body, true, key, cancellationToken);
    }

    /// <summary>
    /// Runs a job such as people.email.find; looot picks the provider. Paid. Prefer goes into
    /// fallback.prefer and turns fallback on. Without Prefer and Fallback a job run does not fall back.
    /// </summary>
    public Task<Run> RunJobAsync(JobRunRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var fallback = request.Fallback;
        if (!string.IsNullOrEmpty(request.Prefer))
        {
            fallback = (fallback ?? new FallbackOptions()) with { Prefer = request.Prefer };
        }
        return RunAsync(new RunRequest
        {
            EndpointId = "job:" + request.Job,
            Input = request.Input,
            IdempotencyKey = request.IdempotencyKey,
            Fallback = fallback,
            Output = request.Output,
            Wait = request.Wait,
        }, cancellationToken);
    }

    /// <summary>Returns a run. Needs runs.read. Free.</summary>
    public Task<Run> GetRunAsync(string runId, GetRunParams? p = null, CancellationToken cancellationToken = default)
    {
        var q = new Query();
        if (p is not null)
        {
            if (p.Fields is { Count: > 0 }) q.Str("fields", string.Join(",", p.Fields));
            q.Int("max_items", p.MaxItems);
        }
        return GetAsync<Run>("/v1/runs/" + Seg(runId), q, true, cancellationToken);
    }

    /// <summary>Returns one page of runs, newest first. Needs runs.read. Free.</summary>
    public Task<RunList> ListRunsAsync(ListRunsParams? p = null, CancellationToken cancellationToken = default)
    {
        var q = new Query();
        if (p is not null)
        {
            q.Str("status", p.Status).Str("capability", p.Capability).Int("limit", p.Limit)
                .Str("cursor", p.Cursor).Bool("includeResult", p.IncludeResult);
        }
        return GetAsync<RunList>("/v1/runs", q, true, cancellationToken);
    }

    /// <summary>Follows nextCursor until the last page. Stop enumerating to stop paging.</summary>
    public async IAsyncEnumerable<Run> AllRunsAsync(ListRunsParams? p = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = p ?? new ListRunsParams();
        while (true)
        {
            var list = await ListRunsAsync(page, cancellationToken).ConfigureAwait(false);
            foreach (var run in list.Runs) yield return run;
            if (string.IsNullOrEmpty(list.NextCursor)) yield break;
            page = page with { Cursor = list.NextCursor };
        }
    }

    /// <summary>
    /// Asks the gateway to stop a run and returns it in its current state. A settled run comes
    /// back unchanged. Needs runs.execute. Free.
    /// </summary>
    public Task<Run> CancelRunAsync(string runId, CancellationToken cancellationToken = default) =>
        SendJsonAsync<Run>(HttpMethod.Post, "/v1/runs/" + Seg(runId) + "/cancel", null, null, true, null, cancellationToken);

    /// <summary>Lists every provider attempt of a run. Needs runs.read. Free.</summary>
    public Task<RunAttempts> RunAttemptsAsync(string runId, CancellationToken cancellationToken = default) =>
        GetAsync<RunAttempts>("/v1/runs/" + Seg(runId) + "/attempts", null, true, cancellationToken);

    /// <summary>
    /// Polls GetRunAsync until the run reaches a terminal status. Bound it with the cancellation
    /// token, for example one that cancels after two minutes.
    /// </summary>
    public async Task<Run> WaitForRunAsync(string runId, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        var interval = options?.Interval is { } i && i > TimeSpan.Zero ? i : TimeSpan.FromSeconds(1);
        var maxInterval = options?.MaxInterval is { } m && m > TimeSpan.Zero ? m : TimeSpan.FromSeconds(5);
        while (true)
        {
            var run = await GetRunAsync(runId, null, cancellationToken).ConfigureAwait(false);
            if (run.IsTerminal()) return run;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            interval = TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, maxInterval.Ticks));
        }
    }

    // ---------- money and account ----------

    /// <summary>Returns the workspace balance and the top-up link. Needs usage.read. Free.</summary>
    public Task<Balance> BalanceAsync(CancellationToken cancellationToken = default) =>
        GetAsync<Balance>("/v1/balance", null, true, cancellationToken);

    /// <summary>Names the workspace and role behind the token. Needs organizations.read.</summary>
    public Task<Whoami> WhoamiAsync(CancellationToken cancellationToken = default) =>
        GetAsync<Whoami>("/v1/whoami", null, true, cancellationToken);

    /// <summary>Lists the workspace's top-ups. Needs usage.read. To add credit, show Balance.TopUpLink.GetUrl().</summary>
    public Task<TopUpList> ListTopUpsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<TopUpList>("/v1/top-ups", null, true, cancellationToken);

    /// <summary>Returns one page of ledger entries. Needs usage.read.</summary>
    public Task<LedgerPage> LedgerAsync(LedgerParams? p = null, CancellationToken cancellationToken = default)
    {
        var q = new Query();
        if (p is not null) q.Int("limit", p.Limit).Str("cursor", p.Cursor);
        return GetAsync<LedgerPage>("/v1/ledger", q, true, cancellationToken);
    }

    /// <summary>Asks looot to add a missing provider or job.</summary>
    public Task<CapabilityRequest> CapabilityRequestAsync(CapabilityRequestParams p, CancellationToken cancellationToken = default)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        var body = new CapabilityBody
        {
            RequestId = string.IsNullOrEmpty(p.RequestId) ? "sdk-" + Guid.NewGuid().ToString() : p.RequestId,
            Title = p.Title,
            Description = p.Description,
            DesiredInputs = p.DesiredInputs,
            DesiredOutputs = p.DesiredOutputs,
            IdempotencyKey = string.IsNullOrEmpty(p.IdempotencyKey) ? "sdk-" + Guid.NewGuid().ToString() : p.IdempotencyKey,
        };
        return SendJsonAsync<CapabilityRequest>(HttpMethod.Post, "/v1/capability-requests", null, body, true, null, cancellationToken);
    }

    /// <summary>
    /// Fetches a run's file from result.media.downloadPath. The path must start with a single "/"
    /// so the token never goes to another host.
    /// </summary>
    public async Task<MediaDownload> DownloadMediaAsync(string downloadPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(downloadPath) || !downloadPath.StartsWith('/') || downloadPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The download path must start with a single \"/\".", nameof(downloadPath));
        }
        var split = downloadPath.IndexOf('?');
        var path = split < 0 ? downloadPath : downloadPath.Substring(0, split);
        var query = split < 0 ? null : downloadPath.Substring(split + 1);
        var raw = await SendRawAsync(HttpMethod.Get, path, query, null, true, null, cancellationToken).ConfigureAwait(false);
        return new MediaDownload(raw.Body, raw.ContentType);
    }

    // ---------- transport ----------

    private sealed record RawResponse(int Status, byte[] Body, string? ContentType);

    internal sealed class RunBody
    {
        public string EndpointId { get; set; } = "";
        public object? Input { get; set; }
        public string IdempotencyKey { get; set; } = "";
        public object? Fallback { get; set; }
        public object? Output { get; set; }
    }

    internal sealed record CompareBody(IReadOnlyList<string> CatalogItemIds);

    internal sealed class CapabilityBody
    {
        public string RequestId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public IReadOnlyList<string>? DesiredInputs { get; set; }
        public IReadOnlyList<string>? DesiredOutputs { get; set; }
        public string IdempotencyKey { get; set; } = "";
    }

    private Task<T> GetAsync<T>(string path, Query? query, bool auth, CancellationToken ct) where T : LoootResponse, new() =>
        SendJsonAsync<T>(HttpMethod.Get, path, query, null, auth, null, ct);

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, Query? query, object? body, bool auth, string? idempotencyKey, CancellationToken ct)
        where T : LoootResponse, new()
    {
        byte[]? payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions);
        var raw = await SendRawAsync(method, path, query?.ToString(), payload, auth, idempotencyKey, ct).ConfigureAwait(false);
        return Decode<T>(raw, method.Method, path);
    }

    private static T Decode<T>(RawResponse raw, string method, string path) where T : LoootResponse, new()
    {
        if (raw.Body.Length == 0 || Encoding.UTF8.GetString(raw.Body).Trim().Length == 0) return new T();
        try
        {
            using var doc = JsonDocument.Parse(raw.Body);
            var value = doc.RootElement.Deserialize<T>(JsonOptions);
            if (value is null) throw new JsonException("The body is null.");
            value.Raw = doc.RootElement.Clone();
            return value;
        }
        catch (JsonException ex)
        {
            throw new LoootException(raw.Status, "invalid_response",
                $"{method} {path} returned a body that is not the expected JSON: {ex.Message}", ex)
            {
                BodyText = Encoding.UTF8.GetString(raw.Body),
                Method = method,
                Path = path,
            };
        }
    }

    private async Task<RawResponse> SendRawAsync(HttpMethod method, string path, string? query, byte[]? payload, bool auth, string? idempotencyKey, CancellationToken ct)
    {
        if (auth && _token.Length == 0)
        {
            throw new LoootException(401, "missing_token",
                $"{method.Method} {path} needs a token: set LOOOT_TOKEN or pass Token in LoootClientOptions")
            {
                Method = method.Method,
                Path = path,
            };
        }

        var target = _baseUrl + path + (string.IsNullOrEmpty(query) ? "" : "?" + query);
        for (var attempt = 0; ; attempt++)
        {
            LoootException error;
            try
            {
                using var request = BuildRequest(method, target, payload, auth, idempotencyKey);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_timeout);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status >= 200 && status < 300)
                {
                    return new RawResponse(status, bytes, response.Content.Headers.ContentType?.ToString());
                }
                error = LoootException.FromResponse(method.Method, path, response, bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                error = LoootException.Transport("timeout", $"{method.Method} {path} timed out", method.Method, path, ex);
            }
            catch (HttpRequestException ex)
            {
                error = LoootException.Transport("network_error", $"{method.Method} {path} could not reach looot: {ex.Message}", method.Method, path, ex);
            }
            catch (IOException ex)
            {
                error = LoootException.Transport("network_error", $"{method.Method} {path} could not reach looot: {ex.Message}", method.Method, path, ex);
            }

            if (attempt >= _maxRetries || !error.ShouldAutoRetry()) throw error;
            await Task.Delay(RetryDelay(error, attempt), ct).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string target, byte[]? payload, bool auth, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, target);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        if (auth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (_agent.Length > 0) request.Headers.TryAddWithoutValidation("X-Looot-Agent", _agent);
        if (!string.IsNullOrEmpty(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (payload is not null)
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;
        }
        return request;
    }

    private TimeSpan RetryDelay(LoootException error, int attempt)
    {
        TimeSpan delay;
        if (error.RetryAfterSeconds is { } seconds && seconds > 0)
        {
            delay = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            var ms = _retryBase.TotalMilliseconds * Math.Pow(2, attempt);
            ms += _retryBase.TotalMilliseconds * 0.25 * Random.Shared.NextDouble();
            delay = TimeSpan.FromMilliseconds(ms);
        }
        return delay > MaxRetryWait ? MaxRetryWait : delay;
    }

    /// <summary>Encodes one path segment, so "a/b" or "job:x" cannot change the route.</summary>
    private static string Seg(string value) => Uri.EscapeDataString(value ?? "");

    /// <summary>Builds a query string and drops null and empty values.</summary>
    internal sealed class Query
    {
        private readonly List<string> _parts = new();

        private Query Add(string key, string value)
        {
            _parts.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
            return this;
        }

        public Query Str(string key, string? v) => string.IsNullOrEmpty(v) ? this : Add(key, v);

        public Query Int(string key, int? v) => v is null ? this : Add(key, v.Value.ToString(CultureInfo.InvariantCulture));

        public Query Long(string key, long? v) => v is null ? this : Add(key, v.Value.ToString(CultureInfo.InvariantCulture));

        public Query Double(string key, double? v) => v is null ? this : Add(key, v.Value.ToString("R", CultureInfo.InvariantCulture));

        public Query Bool(string key, bool? v) => v is null ? this : Add(key, v.Value ? "true" : "false");

        public override string ToString() => string.Join("&", _parts);
    }
}
