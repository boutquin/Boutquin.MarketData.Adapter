---
name: Feature Request
about: Suggest a new adapter or enhancement to an existing one
title: ''
labels: enhancement
assignees: ''
---

## Problem

A clear description of the problem or limitation you're experiencing.

## Proposed Solution

Describe the feature or change you'd like to see.

**For a new data source adapter**, please include:

- The provider name and public API documentation URL.
- The canonical record type(s) it would produce (`Bar`, `FxRate`, `ScalarObservation`, `YieldCurveQuote`, `FuturesSettlement`, `FactorObservation`, `InstrumentMetadata`, or a new type that belongs in the kernel).
- Authentication model: API key, OAuth, anonymous, or file-based.
- License terms: free, delayed (how many minutes), commercial, research-only, redistribution-permitted.
- Rate limits (requests per minute / day, if known).
- Any known data-quality quirks: split adjustment, missing weekends, publication lag, field renaming across API versions.

**For an enhancement to an existing adapter**, include:

- The adapter package name.
- The new record type or field being added and the provider API field it maps from.
- A worked example of the expected raw API response → canonical record transformation.

## Alternatives Considered

Any alternative adapters, existing kernel capabilities, or external packages you've considered and why they don't fit.

## Additional Context

Add any other context, sample payloads, provider sandbox credentials (never real credentials), or benchmark data.
