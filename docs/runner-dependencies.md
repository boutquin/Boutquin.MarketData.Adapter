# Building and release verification

Use the SDK selected by `global.json` (10.0.401 or a later 10.0 feature band). All dependencies restore from NuGet.org; no private repository, deploy key, or sibling checkout is required.

`dependency-source.json` records signed MarketData RC and Storage 2.0.0 archive hashes, signed-content hashes and public source revisions. The content hash excludes the signature; the archive hash includes it. The checker rejects substituted same-version archives and unexpected package sources.

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

CI repeats cold restore, provenance, build, tests, formatting and documentation checks on Linux, macOS and Windows. Package inspection checks all twelve Adapter packages and the upstream kernel, XML docs, dependency versions, matching portable PDBs, public SourceLink, and actual assembly/file versions `2.0.0.0`.

Release archives are built once from the exact public source commit, qualified, and frozen in a SHA-256 manifest. The publication workflow accepts an explicit RC tag and manifest hash, downloads that prerelease's original assets, verifies source and package identities, and uploads those same bytes to NuGet.org. It never repacks a qualified identity. NuGet's repository signature is subsequently checked along with equality of every original package payload entry. Existing RC archives remain immutable.
