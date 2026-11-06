# looot for .NET

[![NuGet](https://img.shields.io/nuget/v/Looot)](https://www.nuget.org/packages/Looot) [![License](https://img.shields.io/github/license/loootai/looot-dotnet)](LICENSE) [![Docs](https://img.shields.io/badge/docs-docs.looot.ai-12A06A)](https://docs.looot.ai)

The .NET client for the [looot](https://looot.ai) API. looot is one gateway to data providers
(email find and verify, company enrichment, SERP data, social profiles, web scraping) with one
token and one prepaid balance. 300+ providers.

No dependencies beyond `System.Text.Json` and `HttpClient`. Every call is async and takes a
`CancellationToken`. Targets .NET 8.

## Install

```bash
dotnet add package Looot --version 0.1.0
```

Create an API key at [looot.ai](https://looot.ai) (Settings, API keys) and export it:

```bash
export LOOOT_TOKEN=...
```

## Quickstart

Search the catalog, inspect the endpoint you want, then run a job. `search` and `inspect` are
free. The run is paid, so check the balance first.

```csharp
using Looot;

using var client = new LoootClient(); // reads LOOOT_TOKEN

// 1. Search (free): describe the task in plain words.
var found = await client.SearchAsync(new SearchParams { Q = "find a work email", Prefer = Prefer.Cheapest });
foreach (var row in found.Endpoints)
    Console.WriteLine($"{row.EndpointId} {row.Capability} ${row.EstimatedPrice}");

// 2. Inspect (free): inputs, price and the most a run can cost.
var op = await client.InspectAsync(found.Endpoints[0].EndpointId);
Console.WriteLine(op.InputSchema);

// 3. Check the balance, then run a job (paid).
var balance = await client.BalanceAsync();
if (balance.Available < 0.05)
{
    Console.WriteLine("Add credit: " + balance.TopUpLink?.GetUrl());
    return;
}

var run = await client.RunJobAsync(new JobRunRequest
{
    Job = "people.email.find",
    Input = new { first_name = "Jane", last_name = "Doe", domain = "example.com" },
    Fallback = FallbackOptions.On, // try the next provider on a miss
    Wait = 30,
});
Console.WriteLine($"{run.Status} {run.Outcome} {run.Result}");
```

Browsing the catalog needs no token: `CatalogOverviewAsync`, `PublicCatalogAsync`,
`PublicCatalogItemAsync` and `ComparePublicCatalogAsync` never send one.

## Read the run, not the HTTP status

A run that failed is not an exception. `RunAsync` and `RunJobAsync` return it with
`Status == "failed"` and `Error` set, even when the gateway answered HTTP 201. A `completed`
run can still be a `miss`.

```csharp
if (run.Error is { } err)
    Console.WriteLine($"{err.Code}: {err.RetryHint}"); // show RetryHint to the user as is
else if (run.IsSuccess())
    Console.WriteLine(run.Outcome); // hit, weak or miss
```

## Errors

Every non-2xx answer is a `LoootException`. So are a missing token (`Status` 401, code
`missing_token`, nothing is sent), a network failure (`network_error`) and a timeout
(`timeout`), both with `Status` 0. A cancelled `CancellationToken` throws
`OperationCanceledException`, as usual in .NET.

```csharp
try
{
    await client.RunJobAsync(request);
}
catch (LoootException e) when (e.Code == "insufficient_balance")
{
    Console.WriteLine("Add credit: " + e.TopUp?.GetUrl()); // then run again with a new key
}
catch (LoootException e) when (e.Retryable)
{
    Console.WriteLine($"Try later, same key: {e.RetryAfterSeconds}");
}
catch (LoootException e)
{
    Console.WriteLine($"{e.Status} {e.Code} {e.Message} {e.RequestId}");
}
```

Properties: `Status`, `Code`, `Message`, `RequestId`, `Retryable`, `RetryHint`,
`RetryAfterSeconds`, `TopUp`, `Body` (the decoded JSON) and `BodyText`. The token never appears
in an exception or in `client.ToString()`.

## Idempotency and retries

Every run sends an idempotency key. Pass your own, built from your record
(`lead-4821-email`), so a crashed worker replays the first run instead of paying twice. With no
key the SDK sends `sdk-<uuid v4>`.

The client retries 429, the retryable 503 codes, network errors and timeouts up to 2 times with
the same key. It waits `Retry-After` when the gateway sends it (capped at 30 s), else a short
backoff. It never retries other 4xx answers. `MaxRetries = 0` turns retries off.

If you pass no key, the SDK generates one and puts it on `LoootException.IdempotencyKey`, so you can retry the same run. For paid runs, build your own key anyway.

After a failed run, or a 402, run again with a new key. The same key only replays the first
answer.

## Options

```csharp
using var client = new LoootClient(new LoootClientOptions
{
    Token = Environment.GetEnvironmentVariable("MY_LOOOT_TOKEN"), // default: LOOOT_TOKEN
    BaseUrl = "http://localhost:8787",                            // default: https://api.looot.ai
    Timeout = TimeSpan.FromSeconds(90),                           // per request, default 75 s
    MaxRetries = 0,                                               // default 2
    Agent = "my-app",                                             // sent as X-Looot-Agent, analytics only
    HttpClient = myHttpClient,                                    // for tests or proxies
});
```

`Token = ""` means no token, even when `LOOOT_TOKEN` is set. In ASP.NET Core, register one
`LoootClient` as a singleton. It is thread safe.

## Methods

| Method | Route | Token scope | Cost |
| --- | --- | --- | --- |
| `CatalogOverviewAsync(CatalogOverviewParams?)` | `GET /v1/catalog/overview` | none | free |
| `PublicCatalogAsync(PublicCatalogParams?)` | `GET /v1/public-catalog` | none | free |
| `PublicCatalogItemAsync(id)` | `GET /v1/public-catalog/{id}` | none | free |
| `ComparePublicCatalogAsync(ids)` | `POST /v1/public-catalog/compare` | none | free |
| `SearchAsync(SearchParams?)` | `GET /v1/catalog/search` | `catalog.read` | free |
| `DiscoverAsync(DiscoverParams?)` | `GET /v1/discover` | `catalog.read` | free |
| `CatalogEndpointsAsync(CatalogEndpointsParams?)` | `GET /v1/catalog/endpoints` | `catalog.read` | free |
| `InspectAsync(endpointId)` | `GET /v1/operations/{id}` | `catalog.read` | free |
| `RunAsync(RunRequest)` | `POST /v1/runs` | `runs.execute` | paid |
| `RunJobAsync(JobRunRequest)` | `POST /v1/runs` with `job:<id>` | `runs.execute` | paid |
| `GetRunAsync(runId, GetRunParams?)` | `GET /v1/runs/{id}` | `runs.read` | free |
| `ListRunsAsync(ListRunsParams?)` | `GET /v1/runs` | `runs.read` | free |
| `AllRunsAsync(ListRunsParams?)` | `GET /v1/runs`, every page | `runs.read` | free |
| `WaitForRunAsync(runId, WaitOptions?)` | `GET /v1/runs/{id}` until terminal | `runs.read` | free |
| `CancelRunAsync(runId)` | `POST /v1/runs/{id}/cancel` | `runs.execute` | free |
| `RunAttemptsAsync(runId)` | `GET /v1/runs/{id}/attempts` | `runs.read` | free |
| `BalanceAsync()` | `GET /v1/balance` | `usage.read` | free |
| `WhoamiAsync()` | `GET /v1/whoami` | `organizations.read` | free |
| `ListTopUpsAsync()` | `GET /v1/top-ups` | `usage.read` | free |
| `LedgerAsync(LedgerParams?)` | `GET /v1/ledger` | `usage.read` | free |
| `CapabilityRequestAsync(CapabilityRequestParams)` | `POST /v1/capability-requests` | any | free |
| `DownloadMediaAsync(downloadPath)` | `GET <result.media.downloadPath>` | any | free |

Params objects may be `null`. Unset properties are not sent. Responses are typed records. Keys
the SDK does not type land in `Extra`, and `Raw` on each top-level response holds the body as
received, so a field the gateway adds later is still readable. Status and outcome are strings
(`RunStatuses`, `RunOutcomes` hold the known values), so a new value never breaks a client.

`RunJobAsync` puts `Prefer` into `fallback.prefer`, which turns fallback on. With neither
`Prefer` nor `Fallback`, a job run does not fall back. Take job ids from
`CatalogOverviewAsync` or a search row's `Capability`, never guess one.

To top up, show the user `(await client.BalanceAsync()).TopUpLink?.GetUrl()`. An agent token
cannot open a payment.

## Develop

```bash
dotnet test tests/Looot.Tests
bash scripts/leak-scan.sh .
git config core.hooksPath .githooks   # runs the leak scan before each commit
```

Tests mock HTTP with a fake `HttpMessageHandler`. No test calls the live API.

## Links

- Docs: https://docs.looot.ai
- API reference: https://docs.looot.ai/reference
- Run errors: https://docs.looot.ai/errors/run-errors
- Agent guide: https://looot.ai/skill.md
- Other SDKs: [looot-js](https://github.com/loootai/looot-js), [looot-python](https://github.com/loootai/looot-python), [looot-go](https://github.com/loootai/looot-go)

## License

MIT. See [LICENSE](LICENSE).
