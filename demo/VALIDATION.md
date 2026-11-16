# ARP-DEMO-002 verification

Executed on 2026-11-16 from branch `feature/arp-demo-002-dotnet-host`. Sources are unchanged original fixtures
from arp-contract; the library implementation, `contract.ref`, native tests and solution are unchanged.

- New ASP.NET Core host: locked restore and Release build/publish passed.
- `dotnet format whitespace ... --verify-no-changes --include demo/DictionaryHost/Program.cs`: passed.
- Existing solution: build passed with zero warnings/errors; **362 tests passed, zero failures/skips** against
  clean contract commit `4c0f4bd0337c01c15a036910b6462e6f0e674927` with its original annotated candidate tag.
- Real HTTP checks: 18-element listing, source SHA-256 hashes, original descriptor `1e127`, all 17 original scalar
  type vectors, INT64 maximum and large DECIMAL normalization without rounding, boolean/null/empty distinctions.
- Negative HTTP checks: real library value rejection, unknown element, malformed/duplicate/extra request fields,
  oversized body, non-JSON media type, unknown route/method, restricted Origin and actual browser preflight.
- Angular + actual host in Chromium: **32 tests passed**, desktop/mobile, no backend mocks, all five packaged
  themes in ES/EN, exact values, invalid values, explicit disconnect, URL/runtime invalidation and connection errors.
- Independent Angular checks: 4 translation unit tests and 24 no-backend browser regressions passed.
- Contract-only checks: 82 Python tests and all five existing checkers passed independently of the frontend.
- `git diff --check`: passed in both repositories; generated outputs and installed dependencies remain ignored.

The SDK image is pinned in `demo/run`; the library's YamlDotNet dependency is unchanged and locked. ASP.NET comes
from the shared framework, not a new runtime library dependency. NuGet package publication, releases and tags
were not performed. CORS authorizes only the selected Angular origin. Test host ports publish only on loopback.
The integration frontend used 4174 because an unrelated process occupied 4173; that process was not stopped.

Readiness checks tolerate connection refusal/reset while Kestrel starts. Build/test/start/HTTP/stop were exercised
repeatedly; the test lifecycle cleans up only its own labelled container. The integration uses Linux Docker host
networking so Chromium reaches the actual loopback port. No remote production deployment was attempted.

These results establish this demo dictionary integration, not ARP transport approval, CRUD/service availability,
production authorization or conformity of unrelated runtime capabilities. See README.md for scope and limitations.