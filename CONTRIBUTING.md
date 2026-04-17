# Contributing to Boutquin.MarketData.Adapter

Thank you for considering contributing to Boutquin.MarketData.Adapter! Whether it's reporting a bug, proposing a new adapter, or submitting a pull request, your input is welcome.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [How to Contribute](#how-to-contribute)
  - [Reporting Bugs](#reporting-bugs)
  - [Suggesting Enhancements](#suggesting-enhancements)
  - [Contributing Code](#contributing-code)
  - [Adding a New Adapter](#adding-a-new-adapter)
- [Style Guides](#style-guides)
  - [Git Commit Messages](#git-commit-messages)
  - [C# Style Guide](#c-style-guide)
  - [Documentation Style Guide](#documentation-style-guide)
  - [Data Correctness](#data-correctness)
- [Pull Request Process](#pull-request-process)
- [License](#license)
- [Community](#community)

## Code of Conduct

This project adheres to the Contributor Covenant [Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code. Report unacceptable behavior through [GitHub Issues](https://github.com/boutquin/Boutquin.MarketData.Adapter/issues).

## How to Contribute

### Reporting Bugs

Open an issue on the [Issues](https://github.com/boutquin/Boutquin.MarketData.Adapter/issues) page with:

- A clear and descriptive title.
- Steps to reproduce the issue (ideally a minimal failing code snippet).
- Expected and actual record output, including field values and any relevant `DataProvenance` or `DataIssue` content.
- Reference values (from provider API documentation, official data releases, or a reference implementation) when asserting data correctness.
- Environment: OS, .NET runtime version, adapter package version, date range, and symbol(s).

### Suggesting Enhancements

Open an issue describing:

- The new data source or enhancement to an existing adapter.
- A primary reference (provider API docs, official specification) with the canonical field mapping.
- The canonical record type(s) the adapter would produce (`Bar`, `FxRate`, `ScalarObservation`, `YieldCurveQuote`, `FuturesSettlement`, `FactorObservation`, `InstrumentMetadata`).
- Authentication model (API key, OAuth, anonymous, file-based), license terms, and known rate limits.
- Any data-quality quirks: split adjustment, publication lag, missing weekends, field changes across API versions.

### Contributing Code

1. **Fork the repository** and clone your fork locally.
   ```bash
   git clone https://github.com/your-username/Boutquin.MarketData.Adapter.git
   cd Boutquin.MarketData.Adapter
   ```

2. **Create a feature branch**:
   ```bash
   git checkout -b feature-or-bugfix-name
   ```

3. **Implement the change** following the style guides below.

4. **Add tests** covering the new behavior:
   - Unit tests under `tests/Boutquin.MarketData.Adapter.Tests.Unit/` with `FakeHttpMessageHandler` or equivalent test doubles and deterministic field assertions.
   - Integration tests under `tests/Boutquin.MarketData.Adapter.Tests.Integration/` when the full fetch-normalize path changes.
   - Architecture tests under `tests/Architecture/` when new assemblies or external dependencies are introduced.

5. **Record the public API surface** in `src/<Adapter>/PublicAPI.Unshipped.txt`. The `PublicAPI` analyzer enforces this at build time.

6. **Update `CHANGELOG.md`** under the `[Unreleased]` section.

7. **Update `docs/adapters.md`** when adding a new adapter or changing configuration options.

8. **Run the full gate** before opening a PR:
   ```bash
   dotnet build Boutquin.MarketData.Adapter.slnx --configuration Release
   dotnet test Boutquin.MarketData.Adapter.slnx --configuration Release
   dotnet format Boutquin.MarketData.Adapter.slnx --verify-no-changes
   ```

   > **Note:** The `pr-verify.yml` CI job is currently disabled because `Boutquin.MarketData.*` kernel packages are not yet published to nuget.org. Run the gate locally before opening a PR.

9. **Push and open a pull request**.

### Adding a New Adapter

New adapters follow this structure:

```
src/<ProviderName>/
  <ProviderName>Options.cs                  — configuration (BaseUrl + auth fields)
  <ProviderName><RecordType>Adapter.cs      — IDataSourceAdapter<TRequest, TRecord> implementation
  ServiceCollectionExtensions.cs            — AddMarketData<ProviderName>() extension
  <ProviderName>.csproj                     — package metadata, depends on kernel only
  PublicAPI.Shipped.txt                     — empty at first release
  PublicAPI.Unshipped.txt                   — new public API surface
```

Each adapter must:
- Implement `IDataSourceAdapter<TRequest, TRecord>` and, if applicable, `IPrioritizedAdapter`.
- Set `DataProvenance.DataDate` to the actual business date of the data served (not the wall-clock date of the fetch).
- Emit a `DataIssue` with code `IssueCode.StaleData` or appropriate code when the actual data date differs from the requested date.
- Self-register via `AddMarketData<ProviderName>()` on `IServiceCollection`.
- Not depend on any other adapter package — only kernel packages (`Abstractions`, `Transport`, `Storage`).

Add an entry to `docs/adapters.md` and the adapter table in `README.md`.

## Style Guides

### Git Commit Messages

- Use the present tense ("Add Tiingo instrument metadata adapter" not "Added …").
- Use the imperative mood ("Move rate normalizer to shared helper" not "Moves …").
- Limit the first line to 72 characters.
- Reference issues and pull requests where applicable.

### C# Style Guide

- Follow the conventions documented in `CLAUDE.md` and `.editorconfig` at the repository root.
- Public types are `sealed` unless they are interfaces or records (enforced by architecture tests).
- No adapter package may depend on another adapter package — each depends only on kernel packages.
- No adapter package may depend on `Boutquin.Analytics`, `Boutquin.Trading`, or `Boutquin.OptionPricing`.
- Litmus test for any new type: "Does this belong in the kernel (`Boutquin.MarketData`) or in this adapter?" Only provider-specific parsing, authentication, and URL construction belong here.

### Documentation Style Guide

All public API additions must satisfy the in-code documentation bar:

- `<summary>` on every public type, constructor, method, property, and enum member.
- `<param>`, `<returns>`, and `<remarks>` per the required-elements checklist.
- No banned boilerplate phrases ("Provides the ... functionality", "Executes the ... operation", "Gets or sets the ... for this instance", "Input value for ...", "/// Executes ...", "Operation result.", "/// Gets the ...").
- Provider API references: name the endpoint, the response field, and the provider documentation URL in the `<remarks>` of the adapter class.
- `Options` classes: every property must document its default value, valid range (if constrained), and what happens when the field is empty or invalid.

Validation commands (must return zero matches before opening a PR):

```bash
# Enforce banned-phrase policy
grep -rn \
  -e 'Provides the .* functionality and related domain behavior' \
  -e 'Executes the .* operation for this component' \
  -e 'The .* input value for the operation' \
  -e 'Gets or sets the .* for this instance' \
  --include='*.cs' src/

# Enforce low-signal phrase policy
grep -rn \
  -e 'Input value for <paramref name=' \
  -e '/// Executes ' \
  -e 'Operation result\.' \
  --include='*.cs' src/

# Enforce accessor-verb property doc policy
grep -rn '/// Gets \w' --include='*.cs' src/
```

### Data Correctness

- State the provider field mapping explicitly in docs and tests (e.g., "`adjClose` → `Bar.Close`"). Do not silently drop or transform fields without documentation.
- Document publication lag: if the provider publishes T+1, say so in `<remarks>` and emit `IssueCode.StaleData` when the actual date lags the requested date.
- Validate normalization rules with explicit assertions: `5.25` (percent) → `0.0525` (decimal) is not obvious to a reader unfamiliar with the provider.
- When asserting output field values in tests, use a fixed HTTP response fixture — never a live network call in unit or architecture tests.

## Pull Request Process

1. **Ensure the full gate passes**: build (warnings-as-errors), all tests (unit, integration, architecture), and `dotnet format --verify-no-changes`.
2. **Describe your changes** in the PR body: reference the issue, summarise the adapter or fix, cite the provider API docs, and note any `PublicAPI.Unshipped.txt` entries added.
3. **Review process**: maintainers will review for correctness, style, and architectural fit. You may be asked to tighten test coverage, add provider documentation citations, or adjust the `DataIssue` emission logic.
4. **Merge**: once approved, a maintainer merges the PR. Releases are cut separately via the dual-repo squash workflow on the public repository.

## License

By contributing to Boutquin.MarketData.Adapter, you agree that your contributions are licensed under the Apache 2.0 License.

## Community

Join the [GitHub Discussions](https://github.com/boutquin/Boutquin.MarketData.Adapter/discussions) to ask questions, propose new adapters, and share usage patterns.

---

Thank you for contributing!
