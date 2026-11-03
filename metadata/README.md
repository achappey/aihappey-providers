# Provider JSON metadata (reserved)

This directory is deliberately separate from the [C# implementation](../dotnet/AIHappey.Providers/AIHappey.Providers.csproj).

The language-neutral provider catalog and schema will move here in a later task. Until then, the chat repository remains the only source of truth. No duplicate catalog is maintained here and the C# build does not require this directory.

The later migration must preserve stable provider IDs, the explicit ordered index, schema, relative file references, and publication of the index and its files from the same revision. Chat and native clients must be able to consume that data without installing .NET or running a gateway. Executable chat handlers are not JSON metadata. Model and pricing catalogs are a separate concern.
