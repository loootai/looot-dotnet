using System.Net;
using System.Text.Json;

namespace Looot.Tests;

public class RequestShapeTests
{
    [Fact]
    public async Task CatalogOverview_sends_topic_and_no_authorization_even_with_a_token()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"revision":"1","topic":"email","matchCount":2,"jobs":[{"id":"people.email.find","providerCount":17}]}""");
        using var client = Make.Client(h);

        var overview = await client.CatalogOverviewAsync(new CatalogOverviewParams { Topic = "email" });

        var r = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Equal("/v1/catalog/overview", r.Path);
        Assert.Equal("email", r.Q("topic"));
        Assert.False(r.Has("Authorization"));
        Assert.False(r.Has("Content-Type"));
        Assert.Equal("application/json", r.Header("Accept"));
        Assert.Equal("email", overview.Topic);
        Assert.Equal("people.email.find", overview.Jobs![0].Id);
        Assert.Equal(17, overview.Jobs[0].ProviderCount);
    }

    [Fact]
    public async Task User_agent_names_the_sdk_and_runtime()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, "{}");
        using var client = Make.Client(h);

        await client.CatalogOverviewAsync();

        Assert.StartsWith("looot-dotnet/" + LoootClient.Version + " dotnet/", h.Requests[0].Header("User-Agent"));
    }

    [Fact]
    public async Task Agent_header_is_sent_only_when_set_and_cut_to_64_characters()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, "{}");
        using (var plain = Make.Client(h)) await plain.CatalogOverviewAsync();
        Assert.False(h.Requests[0].Has("X-Looot-Agent"));

        using (var named = Make.Client(h, o => o.Agent = new string('a', 100))) await named.CatalogOverviewAsync();
        Assert.Equal(64, h.Requests[1].Header("X-Looot-Agent").Length);
    }

    [Fact]
    public async Task Base_url_trailing_slashes_are_stripped()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, "{}");
        using var client = Make.Client(h, o => o.BaseUrl = "https://gateway.test///");

        await client.CatalogOverviewAsync();

        Assert.Equal("https://gateway.test", client.BaseUrl);
        Assert.Equal("/v1/catalog/overview", h.Requests[0].Path);
    }

    [Fact]
    public async Task PublicCatalog_drops_unset_params_and_sends_no_token()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"items":[{"catalogItemId":"a","price":{"state":"known","amountMicros":143880}}],"nextCursor":null}""");
        using var client = Make.Client(h);

        var page = await client.PublicCatalogAsync(new PublicCatalogParams { Capability = "people_email_verify", Limit = 1 });

        var r = h.Requests[0];
        Assert.Equal("/v1/public-catalog", r.Path);
        Assert.Equal("people_email_verify", r.Q("capability"));
        Assert.Equal("1", r.Q("limit"));
        Assert.Equal(2, r.Query().Count);
        Assert.False(r.Has("Authorization"));
        Assert.Equal(143880, page.Items[0].Price!.AmountMicros);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task PublicCatalogItem_encodes_the_id_as_one_segment()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"catalogItemId":"a/b"}""");
        using var client = Make.Client(h);

        var item = await client.PublicCatalogItemAsync("a/b");

        Assert.Equal("/v1/public-catalog/a%2Fb", h.Requests[0].Path);
        Assert.False(h.Requests[0].Has("Authorization"));
        Assert.Equal("a/b", item.CatalogItemId);
    }

    [Fact]
    public async Task ComparePublicCatalog_posts_ids_without_a_token()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"items":[]}""");
        using var client = Make.Client(h);

        await client.ComparePublicCatalogAsync(new[] { "a", "b" });

        var r = h.Requests[0];
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("/v1/public-catalog/compare", r.Path);
        Assert.False(r.Has("Authorization"));
        Assert.Equal("application/json", r.Header("Content-Type"));
        using var doc = JsonDocument.Parse(r.Body!);
        Assert.Equal(2, doc.RootElement.GetProperty("catalogItemIds").GetArrayLength());
    }

    [Fact]
    public async Task Search_sends_bearer_maps_params_and_drops_unset_ones()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"query":"verify an email","total":7,"nextOffset":5,"endpoints":[{"endpointId":"icypeas-email-verify","estimatedPrice":0.0019,"access":"runs_now","works":{"rate":0.9,"runs":10,"p50Ms":380,"thin":false}}]}""");
        using var client = Make.Client(h);

        var result = await client.SearchAsync(new SearchParams
        {
            Q = "verify an email",
            Prefer = Prefer.Cheapest,
            MaxPriceMicros = 5000,
            Keyless = true,
            Verified = false,
        });

        var r = h.Requests[0];
        Assert.Equal("/v1/catalog/search", r.Path);
        Assert.Equal("Bearer test-token", r.Header("Authorization"));
        Assert.Equal("verify an email", r.Q("q"));
        Assert.Equal("cheapest", r.Q("prefer"));
        Assert.Equal("5000", r.Q("maxPriceMicros"));
        Assert.Equal("true", r.Q("keyless"));
        Assert.Equal("false", r.Q("verified"));
        Assert.Null(r.Q("limit"));
        Assert.Null(r.Q("offset"));
        Assert.Null(r.Q("mock"));
        Assert.Equal(7, result.Total);
        Assert.Equal(5, result.NextOffset);
        Assert.Equal("icypeas-email-verify", result.Endpoints[0].EndpointId);
        Assert.Equal(380, result.Endpoints[0].Works!.P50Ms);
    }

    [Fact]
    public async Task Inspect_encodes_job_colon_and_slashes()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"endpoint":{"id":"x","effectClassification":"effectful"},"inputSchema":{"type":"object"}}""");
        using var client = Make.Client(h);

        var op = await client.InspectAsync("job:people.email.find");
        await client.InspectAsync("a/b");

        Assert.Equal("/v1/operations/job%3Apeople.email.find", h.Requests[0].Path);
        Assert.Equal("/v1/operations/a%2Fb", h.Requests[1].Path);
        Assert.Equal("effectful", op.Endpoint!.EffectClassification);
        Assert.Equal("object", op.InputSchema!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Discover_and_CatalogEndpoints_send_their_params()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"candidates":[{"endpointId":"e1"}],"meta":{"k":1},"endpoints":[],"nextCursor":"c2"}""");
        using var client = Make.Client(h);

        var found = await client.DiscoverAsync(new DiscoverParams { Query = "find emails", ResultLimit = 3, IncludeUnavailable = true, MaxPrice = 0.05 });
        var page = await client.CatalogEndpointsAsync(new CatalogEndpointsParams { Facets = true, Limit = 100 });

        Assert.Equal("/v1/discover", h.Requests[0].Path);
        Assert.Equal("find emails", h.Requests[0].Q("query"));
        Assert.Equal("3", h.Requests[0].Q("resultLimit"));
        Assert.Equal("true", h.Requests[0].Q("includeUnavailable"));
        Assert.Equal("0.05", h.Requests[0].Q("maxPrice"));
        Assert.Equal("e1", found.Candidates[0].EndpointId);
        Assert.Equal("/v1/catalog/endpoints", h.Requests[1].Path);
        Assert.Equal("true", h.Requests[1].Q("facets"));
        Assert.Equal("c2", page.NextCursor);
    }

    [Fact]
    public async Task Account_routes_hit_their_paths_with_a_token()
    {
        var h = new FakeHandler()
            .Reply(HttpStatusCode.OK, """{"workspaceId":"w1","currency":"USD","available":94.62,"reserved":0,"topUpLink":{"minimumUsd":5,"suggestedUsd":5,"checkoutUrl":null,"dashboardUrl":"https://looot.ai/usage?top_up=1"}}""")
            .Reply(HttpStatusCode.OK, """{"workspaceId":"w1","role":"owner"}""")
            .Reply(HttpStatusCode.OK, """{"topUps":[{"id":"t1"}]}""")
            .Reply(HttpStatusCode.OK, """{"entries":[],"nextCursor":null}""");
        using var client = Make.Client(h);

        var balance = await client.BalanceAsync();
        var who = await client.WhoamiAsync();
        var tops = await client.ListTopUpsAsync();
        await client.LedgerAsync(new LedgerParams { Limit = 10 });

        Assert.Equal(new[] { "/v1/balance", "/v1/whoami", "/v1/top-ups", "/v1/ledger" }, h.Requests.Select(r => r.Path));
        Assert.All(h.Requests, r => Assert.Equal("Bearer test-token", r.Header("Authorization")));
        Assert.Equal("10", h.Requests[3].Q("limit"));
        Assert.Equal(94.62, balance.Available);
        Assert.Equal("https://looot.ai/usage?top_up=1", balance.TopUpLink!.GetUrl());
        Assert.Equal("owner", who.Role);
        Assert.Single(tops.TopUps);
    }

    [Fact]
    public async Task CapabilityRequest_posts_generated_ids()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"requestId":"r","status":"open"}""");
        using var client = Make.Client(h);

        await client.CapabilityRequestAsync(new CapabilityRequestParams { Title = "t", Description = "d", DesiredInputs = new[] { "domain" } });

        var r = h.Requests[0];
        Assert.Equal("/v1/capability-requests", r.Path);
        using var doc = JsonDocument.Parse(r.Body!);
        Assert.StartsWith("sdk-", doc.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.StartsWith("sdk-", doc.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("domain", doc.RootElement.GetProperty("desiredInputs")[0].GetString());
        Assert.False(doc.RootElement.TryGetProperty("desiredOutputs", out _));
    }

    [Fact]
    public async Task DownloadMedia_fetches_the_path_with_a_token_and_rejects_other_hosts()
    {
        var h = new FakeHandler().Bytes(new byte[] { 1, 2, 3 }, "image/png");
        using var client = Make.Client(h);

        var media = await client.DownloadMediaAsync("/v1/runs/run_1/media?name=a");

        Assert.Equal("/v1/runs/run_1/media", h.Requests[0].Path);
        Assert.Equal("a", h.Requests[0].Q("name"));
        Assert.Equal("Bearer test-token", h.Requests[0].Header("Authorization"));
        Assert.Equal(new byte[] { 1, 2, 3 }, media.Content);
        Assert.Equal("image/png", media.ContentType);
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadMediaAsync("https://evil.test/x"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadMediaAsync("//evil.test/x"));
        Assert.Single(h.Requests);
    }
}

public class RunTests
{
    [Fact]
    public async Task Run_sends_body_wait_query_and_a_generated_key_in_body_and_header()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"completed","outcome":"hit","actualCost":0.0019,"error":null}""");
        using var client = Make.Client(h);

        var run = await client.RunAsync(new RunRequest
        {
            EndpointId = "icypeas-email-verify",
            Input = new Dictionary<string, object> { ["email"] = "jane.doe@example.com" },
            Wait = 20,
            Fallback = new FallbackOptions { MaxAttempts = 2, MaxCostUsd = 0.05 },
        });

        var r = h.Requests[0];
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("/v1/runs", r.Path);
        Assert.Equal("20", r.Q("wait"));
        Assert.Equal("application/json", r.Header("Content-Type"));
        Assert.Equal("Bearer test-token", r.Header("Authorization"));
        using var doc = JsonDocument.Parse(r.Body!);
        var root = doc.RootElement;
        Assert.Equal("icypeas-email-verify", root.GetProperty("endpointId").GetString());
        Assert.Equal("jane.doe@example.com", root.GetProperty("input").GetProperty("email").GetString());
        var key = root.GetProperty("idempotencyKey").GetString()!;
        Assert.StartsWith("sdk-", key);
        Assert.True(Guid.TryParse(key.Substring(4), out _));
        Assert.Equal(key, r.Header("Idempotency-Key"));
        Assert.Equal(2, root.GetProperty("fallback").GetProperty("maxAttempts").GetInt32());
        Assert.Equal(0.05, root.GetProperty("fallback").GetProperty("maxCostUsd").GetDouble());
        Assert.False(root.TryGetProperty("output", out _));
        Assert.Equal("run_1", run.RunId);
        Assert.True(run.IsSuccess());
        Assert.Equal(0.0019, run.ActualCost);
    }

    [Fact]
    public async Task Run_keeps_a_caller_key_and_sends_empty_input_object_when_none()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"queued"}""");
        using var client = Make.Client(h);

        await client.RunAsync(new RunRequest { EndpointId = "e", IdempotencyKey = "lead-4821-email" });

        var r = h.Requests[0];
        Assert.Null(r.Q("wait"));
        Assert.Equal("lead-4821-email", r.Header("Idempotency-Key"));
        using var doc = JsonDocument.Parse(r.Body!);
        Assert.Equal("lead-4821-email", doc.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("input").ValueKind);
        Assert.False(doc.RootElement.TryGetProperty("fallback", out _));
    }

    [Fact]
    public async Task RunJob_posts_job_endpoint_and_merges_prefer_into_fallback()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"completed"}""");
        using var client = Make.Client(h);

        await client.RunJobAsync(new JobRunRequest
        {
            Job = "people.email.verify",
            Input = new { email = "jane.doe@example.com" },
            Prefer = Prefer.Cheapest,
            Fallback = new FallbackOptions { MaxAttempts = 3 },
            Wait = 20,
        });

        var r = h.Requests[0];
        Assert.Equal("20", r.Q("wait"));
        using var doc = JsonDocument.Parse(r.Body!);
        Assert.Equal("job:people.email.verify", doc.RootElement.GetProperty("endpointId").GetString());
        var fb = doc.RootElement.GetProperty("fallback");
        Assert.Equal("cheapest", fb.GetProperty("prefer").GetString());
        Assert.Equal(3, fb.GetProperty("maxAttempts").GetInt32());
    }

    [Fact]
    public async Task RunJob_prefer_alone_becomes_a_fallback_object()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"completed"}""");
        using var client = Make.Client(h);

        await client.RunJobAsync(new JobRunRequest { Job = "people.email.verify", Prefer = Prefer.Fastest });

        using var doc = JsonDocument.Parse(h.Requests[0].Body!);
        var fb = doc.RootElement.GetProperty("fallback");
        Assert.Equal(JsonValueKind.Object, fb.ValueKind);
        Assert.Equal("fastest", fb.GetProperty("prefer").GetString());
    }

    [Fact]
    public async Task RunJob_without_prefer_or_fallback_sends_no_fallback_and_FallbackOn_sends_true()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"completed"}""");
        using var client = Make.Client(h);

        await client.RunJobAsync(new JobRunRequest { Job = "company.enrich" });
        await client.RunJobAsync(new JobRunRequest { Job = "company.enrich", Fallback = FallbackOptions.On });

        using var first = JsonDocument.Parse(h.Requests[0].Body!);
        Assert.False(first.RootElement.TryGetProperty("fallback", out _));
        using var second = JsonDocument.Parse(h.Requests[1].Body!);
        Assert.Equal(JsonValueKind.True, second.RootElement.GetProperty("fallback").ValueKind);
    }

    [Fact]
    public async Task A_201_failed_run_comes_back_as_a_run_not_an_exception()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.Created, """{"runId":"run_9","status":"failed","outcome":"rejected","error":{"code":"validation_error","message":"needs input","whoseError":"customer","retryable":false,"retryHint":"Add the domain."},"result":{"code":"needs_input","needs":["domain"]}}""");
        using var client = Make.Client(h);

        var run = await client.RunJobAsync(new JobRunRequest { Job = "company.enrich", Input = new { } });

        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.False(run.IsSuccess());
        Assert.True(run.IsTerminal());
        Assert.Equal("validation_error", run.Error!.Code);
        Assert.Equal("customer", run.Error.WhoseError);
        Assert.False(run.Error.Retryable);
        Assert.Equal("Add the domain.", run.Error.RetryHint);
        Assert.Equal("needs_input", run.Result!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Run_response_keeps_unknown_keys_in_Extra_and_Raw()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"completed","brandNewField":{"a":1},"route":{"servedBy":"x","attempts":[{"n":1,"endpointId":"x","outcome":"hit","chargedUsd":0.001,"extra":true}],"newRouteKey":"v"}}""");
        using var client = Make.Client(h);

        var run = await client.RunAsync(new RunRequest { EndpointId = "e" });

        Assert.Equal(1, run.Extra!["brandNewField"].GetProperty("a").GetInt32());
        Assert.Equal("v", run.Route!.Extra!["newRouteKey"].GetString());
        Assert.True(run.Route.Attempts![0].Extra!["extra"].GetBoolean());
        Assert.Equal("x", run.Raw.GetProperty("route").GetProperty("servedBy").GetString());
        Assert.Equal(1, run.Raw.GetProperty("brandNewField").GetProperty("a").GetInt32());
    }

    [Fact]
    public async Task An_unknown_status_deserializes_and_counts_as_not_terminal()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"some_new_status"}""");
        using var client = Make.Client(h);

        var run = await client.GetRunAsync("run_1");

        Assert.Equal("some_new_status", run.Status);
        Assert.False(run.IsTerminal());
    }

    [Fact]
    public async Task GetRun_encodes_the_id_and_sends_trim_params()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"a/b","status":"completed"}""");
        using var client = Make.Client(h);

        await client.GetRunAsync("a/b");
        await client.GetRunAsync("run_1", new GetRunParams { Fields = new[] { "data.email", "data.name" }, MaxItems = 5 });

        Assert.Equal("/v1/runs/a%2Fb", h.Requests[0].Path);
        Assert.Equal(0, h.Requests[0].Query().Count);
        Assert.Equal("data.email,data.name", h.Requests[1].Q("fields"));
        Assert.Equal("5", h.Requests[1].Q("max_items"));
    }

    [Fact]
    public async Task CancelRun_and_RunAttempts_use_their_routes_and_cancel_has_no_body()
    {
        var h = new FakeHandler()
            .Reply(HttpStatusCode.OK, """{"status":"stopped"}""")
            .Reply(HttpStatusCode.OK, """{"attempts":[{"providerId":"hunter","status":"succeeded","costMicros":12250}]}""");
        using var client = Make.Client(h);

        var run = await client.CancelRunAsync("run_1");
        var attempts = await client.RunAttemptsAsync("run_1");

        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        Assert.Equal("/v1/runs/run_1/cancel", h.Requests[0].Path);
        Assert.Null(h.Requests[0].Body);
        Assert.False(h.Requests[0].Has("Content-Type"));
        Assert.Equal("stopped", run.Status);
        Assert.Equal("/v1/runs/run_1/attempts", h.Requests[1].Path);
        Assert.Equal(12250, attempts.Attempts[0].CostMicros);
    }

    [Fact]
    public async Task ListRuns_sends_filters_and_AllRuns_follows_the_cursor()
    {
        var h = new FakeHandler()
            .Reply(HttpStatusCode.OK, """{"runs":[{"runId":"r1","status":"completed"},{"runId":"r2","status":"failed"}],"nextCursor":"c2"}""")
            .Reply(HttpStatusCode.OK, """{"runs":[{"runId":"r3","status":"completed"}],"nextCursor":null}""");
        using var client = Make.Client(h);

        var ids = new List<string>();
        await foreach (var run in client.AllRunsAsync(new ListRunsParams { Status = RunStatuses.Completed, Limit = 2 }))
        {
            ids.Add(run.RunId);
        }

        Assert.Equal(new[] { "r1", "r2", "r3" }, ids);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal("completed", h.Requests[0].Q("status"));
        Assert.Equal("2", h.Requests[0].Q("limit"));
        Assert.Null(h.Requests[0].Q("cursor"));
        Assert.Equal("c2", h.Requests[1].Q("cursor"));
        Assert.Equal("completed", h.Requests[1].Q("status"));
    }

    [Fact]
    public async Task WaitForRun_polls_until_a_terminal_status()
    {
        var h = new FakeHandler()
            .Reply(HttpStatusCode.OK, """{"runId":"r","status":"queued"}""")
            .Reply(HttpStatusCode.OK, """{"runId":"r","status":"running"}""")
            .Reply(HttpStatusCode.OK, """{"runId":"r","status":"completed"}""");
        using var client = Make.Client(h);

        var run = await client.WaitForRunAsync("r", new WaitOptions { Interval = TimeSpan.FromMilliseconds(1), MaxInterval = TimeSpan.FromMilliseconds(2) });

        Assert.Equal("completed", run.Status);
        Assert.Equal(3, h.Requests.Count);
    }
}

public class TokenTests
{
    [Fact]
    public async Task A_token_route_with_no_token_throws_missing_token_and_sends_nothing()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, "{}");
        using var client = Make.Client(h, o => o.Token = "");

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.Equal(401, ex.Status);
        Assert.Equal("missing_token", ex.Code);
        Assert.Contains("GET /v1/balance needs a token", ex.Message);
        Assert.Contains("LOOOT_TOKEN", ex.Message);
        Assert.Empty(h.Requests);
        Assert.False(client.HasToken);
    }

    [Fact]
    public async Task Every_token_method_refuses_without_a_token()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, "{}");
        using var client = Make.Client(h, o => o.Token = "   ");

        var calls = new Func<Task>[]
        {
            () => client.SearchAsync(),
            () => client.InspectAsync("x"),
            () => client.RunAsync(new RunRequest { EndpointId = "x" }),
            () => client.RunJobAsync(new JobRunRequest { Job = "x" }),
            () => client.GetRunAsync("x"),
            () => client.ListRunsAsync(),
            () => client.CancelRunAsync("x"),
            () => client.RunAttemptsAsync("x"),
            () => client.BalanceAsync(),
            () => client.DiscoverAsync(),
            () => client.CatalogEndpointsAsync(),
            () => client.WhoamiAsync(),
            () => client.ListTopUpsAsync(),
            () => client.LedgerAsync(),
            () => client.CapabilityRequestAsync(new CapabilityRequestParams { Title = "t", Description = "d" }),
            () => client.DownloadMediaAsync("/v1/x"),
        };
        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<LoootException>(call);
            Assert.Equal("missing_token", ex.Code);
        }
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Free_routes_work_without_a_token()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, "{}");
        using var client = Make.Client(h, o => o.Token = "");

        await client.CatalogOverviewAsync();
        await client.PublicCatalogAsync();
        await client.PublicCatalogItemAsync("x");
        await client.ComparePublicCatalogAsync(new[] { "a", "b" });

        Assert.Equal(4, h.Requests.Count);
    }

    [Fact]
    public async Task The_token_never_shows_in_errors_or_ToString()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.Unauthorized, """{"error":{"code":"unauthorized","message":"Bad token."}}""");
        using var client = Make.Client(h);

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.DoesNotContain(Make.Token, ex.ToString());
        Assert.DoesNotContain(Make.Token, ex.Message);
        Assert.DoesNotContain(Make.Token, client.ToString());
        Assert.DoesNotContain(Make.Token, ex.BodyText);
    }

    [Fact]
    public async Task Token_comes_from_LOOOT_TOKEN_trimmed_when_no_option_is_given()
    {
        var before = Environment.GetEnvironmentVariable("LOOOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("LOOOT_TOKEN", "  env-token \n");
            var h = new FakeHandler().Reply(HttpStatusCode.OK, "{}");
            using var client = new LoootClient(new LoootClientOptions { HttpClient = new HttpClient(h), BaseUrl = "https://gateway.test" });
            await client.BalanceAsync();
            Assert.Equal("Bearer env-token", h.Requests[0].Header("Authorization"));

            using var explicitEmpty = new LoootClient(new LoootClientOptions { Token = "", HttpClient = new HttpClient(h) });
            Assert.False(explicitEmpty.HasToken);

            Environment.SetEnvironmentVariable("LOOOT_TOKEN", "");
            using var none = new LoootClient(new LoootClientOptions { HttpClient = new HttpClient(h) });
            Assert.False(none.HasToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOOOT_TOKEN", before);
        }
    }
}

public class ErrorTests
{
    internal static string Envelope(string code) => "{\"error\":{\"code\":\"" + code + "\",\"message\":\"m\"}}";

    [Fact]
    public async Task A_402_becomes_a_LoootException_with_code_request_id_and_top_up()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.PaymentRequired, """{"error":{"code":"insufficient_balance","message":"Not enough credit.","requestId":"req_1"},"topUp":{"checkoutUrl":"https://pay.test/c","dashboardUrl":"https://looot.ai/usage?top_up=1"}}""");
        using var client = Make.Client(h);

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.RunAsync(new RunRequest { EndpointId = "e" }));

        Assert.Equal(402, ex.Status);
        Assert.Equal("insufficient_balance", ex.Code);
        Assert.Equal("Not enough credit.", ex.Message);
        Assert.Equal("req_1", ex.RequestId);
        Assert.False(ex.Retryable);
        Assert.Equal("https://pay.test/c", ex.TopUp!.CheckoutUrl);
        Assert.Equal("https://pay.test/c", ex.TopUp.GetUrl());
        Assert.Equal("insufficient_balance", ex.Body!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task A_body_without_an_envelope_falls_back_to_http_code_and_header_request_id()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.BadGateway, "<html>bad gateway</html>", ("x-request-id", "req_hdr"));
        using var client = Make.Client(h, o => o.MaxRetries = 0);

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.Equal(502, ex.Status);
        Assert.Equal("http_502", ex.Code);
        Assert.Equal("GET /v1/balance failed with HTTP 502", ex.Message);
        Assert.Equal("req_hdr", ex.RequestId);
        Assert.Null(ex.Body);
        Assert.Equal("<html>bad gateway</html>", ex.BodyText);
    }

    [Fact]
    public async Task A_200_with_a_body_that_is_not_json_throws_invalid_response()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, "not json");
        using var client = Make.Client(h);

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.Equal("invalid_response", ex.Code);
        Assert.Equal(200, ex.Status);
    }

    [Theory]
    [InlineData(400, "validation_error", false)]
    [InlineData(401, "unauthorized", false)]
    [InlineData(402, "insufficient_balance", false)]
    [InlineData(403, "forbidden", false)]
    [InlineData(404, "not_found", false)]
    [InlineData(409, "idempotency_conflict", false)]
    [InlineData(429, "run_rate_limited", true)]
    [InlineData(500, "internal_error", true)]
    [InlineData(503, "storage_busy", true)]
    [InlineData(503, "billing_unavailable", false)]
    public async Task Retryable_follows_the_contract_table(int status, string code, bool retryable)
    {
        var h = new FakeHandler().Reply((HttpStatusCode)status, Envelope(code));
        using var client = Make.Client(h, o => o.MaxRetries = 0);

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.Equal(retryable, ex.Retryable);
    }

    [Fact]
    public async Task Body_retryable_wins_over_the_table_and_retry_after_is_read_from_body_or_header()
    {
        var h = new FakeHandler()
            .Reply(HttpStatusCode.BadRequest, """{"error":{"code":"validation_error","message":"m","retryable":true,"retryHint":"Try again."}}""")
            .Reply(HttpStatusCode.TooManyRequests, """{"error":{"code":"run_rate_limited","message":"m"},"retryAfterSeconds":7}""")
            .Reply(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"storage_busy","message":"m"}}""", ("Retry-After", "3"));
        using var client = Make.Client(h, o => o.MaxRetries = 0);

        var a = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());
        var b = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());
        var c = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.True(a.Retryable);
        Assert.Equal("Try again.", a.RetryHint);
        Assert.Equal(7, b.RetryAfterSeconds);
        Assert.Equal(3, c.RetryAfterSeconds);
    }
}

public class HardeningTests
{
    [Fact]
    public async Task Null_and_fractional_numbers_in_typed_fields_do_not_break_a_run()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"r","status":"completed","error":{"code":"x","retryable":null},"route":{"attempts":[{"n":null,"endpointId":"e","ms":380.5}]},"pricing":{"finalChargeMicros":12.5}}""");
        using var client = Make.Client(h);
        var run = await client.RunAsync(new RunRequest { EndpointId = "e" });
        Assert.Equal(380.5, run.Route!.Attempts![0].Ms);
    }

    [Fact]
    public async Task Input_keys_keep_their_case_and_non_ascii_keys_are_refused()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, """{"runId":"r","status":"completed"}""");
        using var client = Make.Client(h);
        await client.RunAsync(new RunRequest { EndpointId = "e", Input = new { Domain = "x" } });
        using var doc = JsonDocument.Parse(h.Requests[0].Body!);
        Assert.Equal("x", doc.RootElement.GetProperty("input").GetProperty("Domain").GetString());
        Assert.Throws<ArgumentException>(() => client.RunAsync(new RunRequest { EndpointId = "e", IdempotencyKey = "Jos\u00e9" }));
    }

    [Fact]
    public async Task Errors_carry_the_idempotency_key_and_nested_items_have_Raw()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.Created, """{"runs":[{"runId":"a","status":"completed","z":1}],"nextCursor":null}""")
            .Reply(HttpStatusCode.PaymentRequired, """{"error":{"code":"insufficient_balance","message":"m"}}""");
        using var client = Make.Client(h);
        var list = await client.ListRunsAsync();
        Assert.Equal(1, list.Runs[0].Raw.GetProperty("z").GetInt32());
        var ex = await Assert.ThrowsAsync<LoootException>(() => client.RunAsync(new RunRequest { EndpointId = "e" }));
        Assert.StartsWith("sdk-", ex.IdempotencyKey);
    }
}

public class RetryTests
{
    private static string Envelope(string code) => ErrorTests.Envelope(code);

    [Fact]
    public async Task A_429_is_retried_with_the_same_idempotency_key_then_succeeds()
    {
        var h = new FakeHandler()
            .Reply(HttpStatusCode.TooManyRequests, """{"error":{"code":"run_rate_limited","message":"slow down"},"retryAfterSeconds":0.001}""")
            .Reply(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"storage_busy","message":"busy"}}""")
            .Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"completed"}""");
        using var client = Make.Client(h);

        var run = await client.RunAsync(new RunRequest { EndpointId = "e" });

        Assert.Equal("run_1", run.RunId);
        Assert.Equal(3, h.Requests.Count);
        var keys = h.Requests.Select(r => r.Header("Idempotency-Key")).Distinct().ToList();
        Assert.Single(keys);
        Assert.StartsWith("sdk-", keys[0]);
        Assert.Equal(1, h.Requests.Select(r => r.Body).Distinct().Count());
    }

    [Fact]
    public async Task Retries_stop_after_two_and_the_last_error_is_thrown()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"storage_busy","message":"busy"}}""");
        using var client = Make.Client(h);

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.Equal("storage_busy", ex.Code);
        Assert.Equal(3, h.Requests.Count);
    }

    [Theory]
    [InlineData(400, "validation_error")]
    [InlineData(402, "insufficient_balance")]
    [InlineData(403, "forbidden")]
    [InlineData(409, "idempotency_conflict")]
    [InlineData(503, "billing_unavailable")]
    [InlineData(500, "internal_error")]
    public async Task Non_retryable_errors_are_sent_once(int status, string code)
    {
        var h = new FakeHandler().Reply((HttpStatusCode)status, Envelope(code));
        using var client = Make.Client(h);

        await Assert.ThrowsAsync<LoootException>(() => client.RunAsync(new RunRequest { EndpointId = "e" }));

        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task MaxRetries_zero_turns_retries_off()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.TooManyRequests, """{"error":{"code":"run_rate_limited","message":"m"}}""");
        using var client = Make.Client(h, o => o.MaxRetries = 0);

        await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task A_network_error_is_retried_then_raised_as_network_error_with_status_0()
    {
        var h = new FakeHandler()
            .Throw(new HttpRequestException("connection refused"))
            .Throw(new HttpRequestException("connection refused"))
            .Throw(new HttpRequestException("connection refused"));
        using var client = Make.Client(h);

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.RunAsync(new RunRequest { EndpointId = "e" }));

        Assert.Equal(0, ex.Status);
        Assert.Equal("network_error", ex.Code);
        Assert.True(ex.Retryable);
        Assert.Equal(3, h.Requests.Count);
        Assert.Single(h.Requests.Select(r => r.Header("Idempotency-Key")).Distinct());
    }

    [Fact]
    public async Task A_network_error_then_success_returns_the_run()
    {
        var h = new FakeHandler()
            .Throw(new HttpRequestException("reset"))
            .Reply(HttpStatusCode.OK, """{"runId":"run_1","status":"completed"}""");
        using var client = Make.Client(h);

        var run = await client.RunAsync(new RunRequest { EndpointId = "e" });

        Assert.Equal("run_1", run.RunId);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task A_slow_response_raises_timeout_with_status_0()
    {
        var h = new SlowHandler();
        using var client = new LoootClient(new LoootClientOptions
        {
            Token = Make.Token,
            HttpClient = new HttpClient(h),
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 0,
        });

        var ex = await Assert.ThrowsAsync<LoootException>(() => client.BalanceAsync());

        Assert.Equal(0, ex.Status);
        Assert.Equal("timeout", ex.Code);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task A_cancelled_token_raises_OperationCanceledException_not_LoootException()
    {
        var h = new FakeHandler().Reply(HttpStatusCode.OK, "{}");
        using var client = Make.Client(h);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.BalanceAsync(cancellationToken: cts.Token));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
