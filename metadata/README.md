# Shared provider metadata

This directory is the single editable source for language-neutral provider display metadata, model catalogs, and pricing. It is deliberately separate from the [C# implementation](../dotnet/AIHappey.Providers/AIHappey.Providers.csproj). No .NET runtime, gateway, npm package, or running chat application is needed to read these files.

## Contracts

- [Provider index](providers/index.json): the existing ordered array of stable IDs and relative filenames. Read each referenced file relative to that index. Do not derive IDs from filenames: the ID for [302ai](providers/302ai.json) is **ai302**. The unchanged [display schema](provider.schema.json) defines the metadata fields. Optional/absent fields, empty arrays, icon order, descriptions, booleans, and URLs are not normalized. Icon fallback and executable handlers belong to clients, not JSON.
- [Models](models/providers): preserves the original model documents and nested provider directories. Most documents are model-list objects; vendor-specific shapes are preserved, not converted by this migration.
- [Pricing](pricing/providers): preserves base pricing documents and nested context/tier variants. Numeric strings, units, and provider-specific fields remain unchanged. The presence of a tier file does not mean every client implements its resolver.
- [Dataset manifest](index.json): version 1, with a deterministically ordered list of relative JSON filenames and SHA-256 hashes. It inventories the display index, schema, provider files, models, and pricing (including nested files). Hashes are over UTF-8 text with CRLF converted to LF, to support different Git checkout line endings. No JSON values or other whitespace are normalized.

Fetch/copy the manifest and all consumed files from the **same commit or tag** of this repository. For example, raw GitHub paths use the repository, pinned revision, and the path under this directory. Verify the manifest hashes before publishing a snapshot. Never combine an index from one revision with files from another. The hashes detect drift, not authenticity; trust/pin the repository revision separately.

## Editing and verification

Edit JSON here, not downstream snapshots. When adding/removing/reordering providers, update the ordered provider index. Regenerate the dataset manifest after every JSON change, then commit the JSON and manifest together:

```sh
node metadata/tools/catalog.cjs
node metadata/tools/catalog.cjs --check
node --test metadata/tools/catalog.test.cjs
```

The manifest tooling is dependency-free. Chat's generator additionally validates provider metadata against the unchanged draft-07 schema and rejects duplicate JSON properties, invalid metadata, missing/unindexed providers, and duplicate IDs/files.

## Consumer integration and rollout

- **.NET:** the providers project and package own build/publish copies in the existing runtime catalog layout. The package carries all model/pricing documents. Existing file readers, custom source inputs, and empty-source semantics are unchanged. See the [package guide](../README.md).
- **Chat:** build/watch startup synchronizes its display/schema snapshots, currently consumed static model files, and base pricing files from a sibling providers checkout. Generated static imports preserve browser/bundler behavior without runtime fetches. Snapshots include a hash manifest; an absent checkout permits validated offline development, but an existing incomplete/mixed checkout is an error. Package creation requires a current source checkout. Chat now uses the relocated AI datasets, intentionally superseding its previously divergent model/pricing copies. Pricing algorithms, direct routing, handlers, and request configuration stay in chat.
- **Windows:** synchronizes display files directly from providers, not via chat. Embedded resources, offline development fallback, and strict publish/build-fingerprint checks remain in place. This does not remove unrelated chat dependencies used by parity/assets tests.
- **Other clients:** read the provider index and/or dataset manifest, then copy only the raw JSON needed. Runtime downloading is not required or introduced by this refactor.

Roll out the providers change first, then update AI, chat snapshots, and Windows against that providers revision. Pin the resulting providers commit in release automation once it has been committed; do not invent a commit for an uncommitted working tree. Consumers must regenerate/check snapshots before release.
