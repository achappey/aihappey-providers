# AIHappey Providers

Reusable provider implementations, native API clients, outbound transports, provider-specific C# metadata, and shared provider helpers. Consumers use the existing provider namespaces and protocol contracts without starting or referencing the gateway.

## Repository boundaries

- [C# implementation](dotnet/AIHappey.Providers/AIHappey.Providers.csproj) and [tests](dotnet/tests/AIHappey.Providers.Tests/AIHappey.Providers.Tests.csproj).
- [Language-neutral JSON metadata area](metadata/README.md), reserved for a separate migration. Chat metadata remains in the chat repository for now.
- Gateway authentication, inbound HTTP handling, storage, aggregate model resolution, refresh workers, and telemetry implementations remain host concerns.

Existing namespaces and wire contracts are preserved. The existing media contracts still require the ASP.NET shared framework; consuming this package does not require a web server. This first combined package retains vendor SDK dependencies rather than introducing a separate package for every vendor.

## Direct use and lifetimes

The [direct client](dotnet/AIHappey.Providers/Bootstrap/ProviderClient.cs:12) accepts a caller-owned HTTP client factory. Supply provider-keyed credentials and a callback using the unchanged [provider contract](dotnet/AIHappey.Providers/Contracts/IModelProvider.cs:12). Each execute call and each stream enumeration gets a fresh container/scope, provider instances, a credential snapshot, and a disabled cache. Streaming owns that scope until enumeration finishes or is disposed. The factory can reuse handlers; it must return separate clients rather than a shared mutable client instance.

See the [standalone executable](dotnet/tests/PackageConsumer/Program.cs) for compatible/native calls, media, cancellation, raw options, and streaming without a gateway. It uses deterministic fake outbound handlers, never real credentials or a live service.

Advanced hosts can use [provider registration](dotnet/AIHappey.Providers/Bootstrap/ServiceExtensions.cs:687), existing credential/identity contracts, and provider-specific options directly. Resolve providers lazily from an operation scope; do not cache providers at the root or share one provider/request DTO between concurrent operations. Some existing implementations mutate their outbound client defaults or request DTOs. The direct client deliberately isolates those objects instead of claiming that every concrete provider is a thread-safe singleton.

Provider registration does not register a web context, application storage, model refresh workers, telemetry persistence, or a memory cache. Provider-specific SDK clients may still require explicit settings or upstream resources. Delegated authentication remains supplied by the host through the existing resolver contracts.

### Explicit request inputs

[Provider request options](dotnet/AIHappey.Providers/Contracts/ProviderRequestOptions.cs) replace incoming web-request reads: Dust workspace ID, Yandex folder ID, and Suno callback URL. Gateway adapters retain the existing header/configuration precedence. Direct clients do not construct a fake HTTP context. Suno generation requires an explicit absolute HTTP(S) callback URL even though completion is polled.

### Optional caching

The [cache helper](dotnet/AIHappey.Providers/Common/AsyncCacheHelper.cs) is disabled without a caller-supplied memory cache. A DI host can explicitly register an enabled helper before provider registration. Cache keys include the catalog partition; provider discovery retains its provider/credential/workspace partitioning. Model results are copied on cache write/read so caller changes cannot mutate another operation's model list. Hosts are responsible for the lifetime and limits of any enabled cache.

The restricted [Cursor repository endpoint](dotnet/AIHappey.Providers/Providers/Cursor/CursorApiClient.cs) requires an explicit [caller-owned limiter](dotnet/AIHappey.Providers/Providers/Cursor/ICursorRepositoryRateLimiter.cs), enforcing at most one request per minute and thirty per hour per credential. It refuses to send without that policy; no process-global credential map is retained by the package.

## Model and pricing data are separate

Implement the small [read-only source contract](dotnet/AIHappey.Providers/Catalog/IProviderCatalogSource.cs) for caller-owned JSON data, using a stable partition that distinguishes its tenant/revision. Pass it to the direct client, or open an explicit [async-operation catalog scope](dotnet/AIHappey.Providers/Catalog/ProviderCatalogScope.cs) around a low-level call and its entire stream enumeration. This scope carries only operation-local read-only input, not a global configurable service or retained catalog cache. Nested scopes restore their parent; concurrent async operations do not share the selected source.

The direct client defaults to an [empty source](dotnet/AIHappey.Providers/Catalog/EmptyProviderCatalogSource.cs), with no filesystem dependency. Existing low-level helpers retain a [read-only compatibility reader](dotnet/AIHappey.Providers/Catalog/FileProviderCatalogSource.cs) for the host's existing output catalog directory when no explicit scope is selected. No datasets are packaged, downloaded, or migrated here. The reader also accepts an explicit catalog directory.

Some provider paths require model metadata for discovery, routing, or model-specific validation; supplying a model ID alone does not make every adapter catalog-independent. Missing catalog input must not broaden provider support. For example, Replicate still rejects unlisted models, but evaluates the caller's current data rather than a process-wide startup snapshot. Pricing-tier readers likewise use the current source rather than global lazy caches.

The [JSON metadata area](metadata/README.md) concerns provider identity/display metadata, not these model/pricing datasets. Chat remains its source of truth until the later migration.

## Build and verification

Use .NET 11 preview and check out the existing Responses, Chat Completions, Messages, Interactions, OpenAI/Decisions, Vercel, and Unified repositories as siblings. No AI/gateway checkout is needed for this repository's build or tests.

Run the [solution](AIHappey.Providers.slnx) tests in Release configuration. The [CI workflow](.github/workflows/ci.yml) lists the checkouts and commands.

Run the [package verification script](dotnet/tests/Verify-PackageConsumption.ps1) to pack the complete dependency closure, copy only the standalone consumer into a fresh temporary directory, restore with a private dependency cache, reject project/source and gateway implementation dependencies, and execute the direct-use checks. Third-party packages restore from NuGet; provider calls are fake and deterministic. Live-provider acceptance testing is separate and requires host-owned credentials.

The [boundary tests](dotnet/tests/AIHappey.Providers.Tests/ExtractionBoundaryTests.cs) compare all 676 model-provider IDs, 7 skill-provider IDs, registrations/lifetimes, and provider method signatures against the pre-extraction snapshot. Relocated transport/passthrough tests run here; catalog-dependent and gateway integration regressions also continue in the AI repository.
