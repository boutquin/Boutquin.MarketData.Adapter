## Summary

Brief description of what this PR does.

## Changes

- ...

## Related Issues

Closes #

## Checklist

- [ ] Code compiles with zero warnings (`TreatWarningsAsErrors` enabled)
- [ ] All existing tests pass (unit, integration, architecture)
- [ ] New tests added for new functionality — unit tests under `tests/Boutquin.MarketData.Adapter.Tests.Unit/`; integration tests under `tests/Boutquin.MarketData.Adapter.Tests.Integration/` when the full fetch-normalize path changes; architecture tests under `tests/Architecture/` when new assemblies or external dependencies are introduced
- [ ] `PublicAPI.Unshipped.txt` updated for any new or changed public API in the affected adapter package(s)
- [ ] `dotnet format Boutquin.MarketData.Adapter.slnx --verify-no-changes` produces no changes
- [ ] XML doc comments are complete (no banned phrases; semantic description; `<remarks>` on `Options` classes and service-collection extensions — see [CONTRIBUTING.md](../CONTRIBUTING.md#documentation-style-guide))
- [ ] `CHANGELOG.md` updated under `[Unreleased]` (if user-visible change)
- [ ] No new provider-to-provider dependencies introduced (Adapter.Shared and kernel infrastructure are allowed)
- [ ] New adapter (if applicable): provider API docs URL cited in XML `<remarks>`, `Options` class has complete doc comments, `ServiceCollectionExtensions` method follows the `AddMarketData*` naming convention
