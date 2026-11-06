# Changelog

## 0.1.0 (2026-11-06)

First release.

- One `LoootClient` for the looot REST API. `HttpClient` and `System.Text.Json` only, async
  methods with a `CancellationToken` on each. Targets net8.0.
- Core methods: `CatalogOverviewAsync`, `PublicCatalogAsync`, `PublicCatalogItemAsync`,
  `SearchAsync`, `InspectAsync`, `RunAsync`, `RunJobAsync`, `GetRunAsync`, `ListRunsAsync`,
  `CancelRunAsync`, `RunAttemptsAsync`, `BalanceAsync`.
- Extended methods: `DiscoverAsync`, `CatalogEndpointsAsync`, `ComparePublicCatalogAsync`,
  `WhoamiAsync`, `ListTopUpsAsync`, `LedgerAsync`, `CapabilityRequestAsync`, `DownloadMediaAsync`.
- Helpers: `WaitForRunAsync` (polls 1 s to 5 s) and `AllRunsAsync` (an `IAsyncEnumerable`).
- `LoootException` for every non-2xx answer, missing token, network error and timeout, with
  `Code`, `Retryable`, `RetryHint`, `RetryAfterSeconds` and the 402 `TopUp` link.
- Retries 429, retryable 503s, network errors and timeouts twice by default with the same
  idempotency key. `MaxRetries = 0` turns them off.
- Typed response records keep unknown keys in `Extra` and the whole body in `Raw`.
- Sends `User-Agent: looot-dotnet/0.1.0 dotnet/<version>` and, with `Agent`, `X-Looot-Agent`.
