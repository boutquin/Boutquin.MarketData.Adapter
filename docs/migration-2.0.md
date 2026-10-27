# Migrating to 2.0 RC

Install Adapter `2.0.0-rc.20261027.2` with MarketData `2.0.0-rc.20261027.3` on .NET 10. Published Storage 2.0.0 is resolved transitively where needed. All dependencies are available from NuGet.org.

Credential-bearing Tiingo, TwelveData and FRED base URLs must use absolute HTTPS URLs. Cancellation propagates to callers rather than becoming a partial-data result. Price adapters and Frankfurter accept one symbol/pair per direct request; use the kernel to partition batches. Shared utilities are an implementation dependency; provider adapters remain independent of one another.

Read the [changelog](../CHANGELOG.md) for breaking renames and the [provider reference](adapters.md) for authentication, calendars, limits and completeness. CORRA uses `AVG.INTWO`; TwelveData close data is unadjusted; Tiingo earliest-price dates are not instrument inception dates. Default calendars are bounded to 2020–2030 and warn outside that range.

Qualification uses deterministic fixtures, independent behavioral controls, malformed-response and cancellation regressions, and cold package consumers. It does not certify live provider uptime, credentials, rate plans or licensed entitlements.
