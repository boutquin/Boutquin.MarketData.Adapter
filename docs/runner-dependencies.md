# Building and release verification

Use the SDK selected by `global.json` (10.0.403 or a later 10.0 feature band). Published builds restore from NuGet.org. During prepublication qualification, supply the sealed MarketData archive directory explicitly as described below.

`dependency-source.json` records the prepared MarketData and Storage archive hashes, per-entry payload hashes, content hashes and release-source revision, alongside signed published Domain identities. The content hash excludes the repository signature; the archive hash includes it. Private qualification requires the exact original MarketData and Storage archives. After publication, the checker requires a valid NuGet signature and equality of every original payload entry, as well as the pinned content hash. Same-version substitutes and unexpected package sources fail verification.

For private qualification, set `BOUTQUIN_PREPARED_FEED` to the directory containing the twelve sealed MarketData packages and three sealed Storage packages. Run `python3 scripts/prepared-dependencies.py --config artifacts/prepared.nuget.config` and use that configuration in place of `nuget.ci.config` below. The generated mappings restrict each prepared package identity to that feed; Domain and other dependencies still restore from NuGet.org. The directory must already exist, and the generated configuration must be a fresh path. Clear the variable for public-only verification after MarketData and Storage publication.

```bash
python3 scripts/check-runner-dependencies.py
export NUGET_PACKAGES="$(mktemp -d)"
export NUGET_HTTP_CACHE_PATH="$(mktemp -d)"
dotnet restore Boutquin.MarketData.Adapter.slnx --configfile nuget.ci.config --locked-mode --no-http-cache
python3 scripts/check-runner-dependencies.py --packages "$NUGET_PACKAGES"
dotnet build Boutquin.MarketData.Adapter.slnx -c Release --no-restore
dotnet test Boutquin.MarketData.Adapter.slnx -c Release --no-build --no-restore
dotnet format Boutquin.MarketData.Adapter.slnx --verify-no-changes --no-restore
```

CI repeats cold restore, provenance, build, tests, formatting and documentation checks on Linux, macOS and Windows. Package inspection checks all twelve Adapter packages and the upstream kernel, XML docs, dependency versions, matching portable PDBs, public SourceLink, and actual assembly version `2.0.0.0`, file version matching the package core (`2.0.1.0` for 2.0.1), and informational version containing the exact source commit.

Release archives are built once from the exact public source commit, qualified, and frozen in a SHA-256 manifest. The publication workflow accepts an explicit `v2.0.0`/`v2.0.1` or matching numeric RC tag and manifest hash, downloads that release's original assets, verifies source and package identities, and uploads those same bytes to NuGet.org. It never repacks a qualified identity. NuGet's repository signature is subsequently checked along with equality of every original package payload entry. Existing RC archives remain immutable.

The publisher authenticates to NuGet.org with trusted publishing: the job's GitHub OIDC token, issued for the `release` environment, is exchanged for a one-hour API key, so no long-lived key is stored. Each release follows the same order. Date the `CHANGELOG.md` section and align the README version and tag links before the source push; `python3 scripts/check-release-docs.py <version>` enforces this, and the publication workflow runs it against the tagged source for stable tags. Run the verification workflow on the public source, create the tag and release assets, then dispatch the publisher. Publish MarketData before Adapter, repin the Adapter to the published kernel bytes, verify signed public payload equality and cold public consumption from an empty package folder (a populated global NuGet cache can silently serve a different build of the same version), then release the Adapter. Never rebuild sealed packages.
