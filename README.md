# AIHappey Providers

Reusable provider implementations, native API clients, outbound transports, provider-specific C# metadata, and shared provider helpers. Consumers use the existing provider namespaces and protocol contracts without starting or referencing the gateway.

## Repository boundaries

- [C# implementation](dotnet/AIHappey.Providers/AIHappey.Providers.csproj) and [tests](dotnet/tests/AIHappey.Providers.Tests/AIHappey.Providers.Tests.csproj).
- [Language-neutral JSON metadata](metadata/README.md): the canonical provider display catalog, model catalogs, and pricing catalogs, shared by .NET, chat, Windows, and other clients.
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

## Model and pricing data

Canonical JSON is maintained under [models](metadata/models) and [pricing](metadata/pricing), independently of C# code. Project references and the NuGet package copy the same files to the existing runtime catalog directory on build and publish, including through transitive references. The [package targets](dotnet/AIHappey.Providers/buildTransitive/AIHappey.Providers.targets) require no source checkout, gateway, or runtime download.

Catalog-source semantics are unchanged. The direct client defaults to an [empty source](dotnet/AIHappey.Providers/Catalog/EmptyProviderCatalogSource.cs), with no filesystem reads. To opt into the shipped data, supply the [file reader](dotnet/AIHappey.Providers/Catalog/FileProviderCatalogSource.cs) for the runtime catalog directory as the direct client's catalog input. Existing low-level helpers use that directory automatically when no explicit scope is selected. The reader also accepts caller-owned catalog directories.

Implement the [read-only source contract](dotnet/AIHappey.Providers/Catalog/IProviderCatalogSource.cs) for other caller-owned JSON data, using a stable partition that distinguishes its tenant/revision. Pass it to the direct client, or open an explicit [async-operation catalog scope](dotnet/AIHappey.Providers/Catalog/ProviderCatalogScope.cs) around a low-level call and its entire stream enumeration. Nested scopes restore their parent; concurrent async operations do not share the selected source. An explicit empty/custom source never falls back to bundled data.

Some provider paths require model metadata for discovery, routing, or model-specific validation; supplying a model ID alone does not make every adapter catalog-independent. Missing catalog input must not broaden provider support. For example, Replicate still rejects unlisted models, but evaluates the caller's current data rather than a process-wide startup snapshot. Pricing-tier readers likewise use the current source rather than global lazy caches.

Non-.NET consumers use the same [metadata manifest and raw JSON](metadata/README.md). Chat and Windows generate local snapshots at build time and remain offline for metadata at runtime. Chat request-configuration types, defaults, UI, and executable cost/usage handlers remain in chat; they are not data files in this repository.

## Build and verification

Use .NET 11 preview and check out the existing Responses, Chat Completions, Messages, Interactions, OpenAI/Decisions, Vercel, and Unified repositories as siblings. No AI/gateway checkout is needed for this repository's build or tests.

Run the [solution](AIHappey.Providers.slnx) tests in Release configuration. The [CI workflow](.github/workflows/ci.yml) lists the checkouts and commands.

Run the [package verification script](dotnet/tests/Verify-PackageConsumption.ps1) to pack the complete dependency closure, copy only the standalone consumer into a fresh temporary directory, restore with a private dependency cache, reject project/source and gateway implementation dependencies, and execute the direct-use checks. Third-party packages restore from NuGet; provider calls are fake and deterministic. Live-provider acceptance testing is separate and requires host-owned credentials.

The [boundary tests](dotnet/tests/AIHappey.Providers.Tests/ExtractionBoundaryTests.cs) compare all 676 model-provider IDs, 7 skill-provider IDs, registrations/lifetimes, and provider method signatures against the pre-extraction snapshot. Relocated transport/passthrough tests run here; catalog-dependent and gateway integration regressions also continue in the AI repository.
