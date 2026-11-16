# ARP-DEMO-002-HARDENING verification

Executed on 2026-11-16 on `feature/arp-demo-002-hardening`, based on main
`c931572b928453da2d5653279a3478306c419d55`. Changes are restricted to the demo host, runner, regression
checks and documentation. Library implementation, public APIs, dependencies, contract pin and fixtures are unchanged.

## Reproduction and correction

The original host returned 500 `DEMO_EXECUTION_FAILURE` for raw JSON bodies `{"value":"\ud800"}` and
`{"value":"\udc00"}`. Logs identify `InvalidOperationException` from `JsonElement.GetString()`:
`Cannot read invalid UTF-16 JSON text as string`. `JsonDocument` accepts the escaped token and defers decoding,
so the failure happened after parsing and reached the generic internal-error handler. A valid pair
`{"value":"\ud83d\ude00"}` returned 200 before and after the correction.

The host now decodes input strings and property names, including nested values, before library validation.
Malformed JSON is caught only around input parsing; invalid Unicode is caught only around that decoding pass.
Both return 400 `{"valid":false,"issues":[{"code":"DEMO_INVALID_JSON"}]}`. Internal/library exceptions are not
broadly reclassified as input errors. Shape errors and actual library value validation retain their previous
responses. This is demo transport hardening, not a change to the ARP scalar validation contract.

## Checks repeated for this change

- `demo/run check`, with port 5198, origin `http://127.0.0.1:4198` and a unique test container: passed.
- Locked host restore, Release publish and configured `dotnet format whitespace --verify-no-changes`: passed.
- Library Release build: zero warnings/errors; **362 tests passed, zero failures/skips**, including all 327 shared
  assertions, against clean contract `4c0f4bd0337c01c15a036910b6462e6f0e674927` and its annotated RC tag.
- Real host HTTP checks: passed. Original 18 elements, 17 type vectors, exact numeric values and metadata,
  library rejection, unknown elements/routes/methods, request size/media type, CORS and preflight remain covered.
- Added raw-body regressions: 11 malformed JSON/Unicode cases, three shape violations and three valid Unicode/text
  cases. They assert HTTP status and the complete response projection. Coverage includes isolated high/low
  surrogates, invalid pairs, property names/nested strings, invalid UTF-8, valid pairs, valid literal UTF-8 and
  literal backslash text that resembles an escape. All passed against the real library-backed host.
- `python3 demo/scripts/check-docker.py`: passed against real Docker. Both standalone start and check mode were
  exercised with an occupied loopback port. Created containers were removed after failure; the port owner was
  unchanged. Name collisions, missing ownership labels, configured non-default loopback publication,
  successful start/stop and repeated stop passed.
- Existing running containers were compared by exact ID, status, start timestamp and restart count; unchanged.
  The pre-existing services on 5100 and 4173 were not stopped. Only test-created container IDs were removed.
- `bash scripts/check-contract.sh /tmp/arp-demo-002-contract-rc1`: passed; pinned schema/fixture checker and
  **27 Python tooling tests** passed. This is distinct from the current main's broader candidate suites.
- Bash syntax and Python AST parsing of changed scripts: passed. ShellCheck is not installed.
- `git diff --check`: passed. New files and ignored artifacts were reviewed; no generated output, installed
  dependencies or temporary data are included in the change.

## Isolation and limits

The runner records the exact ID returned by `docker create` before startup. Cleanup is installed before creation
and requires both demo-kind and checkout-owner labels on that ID; it never falls back to deleting a same-name
container. Explicit stop also resolves an ID before checking both labels. No global prune, image/volume removal,
port-based process termination or foreign-container stop is used. Builds and native tests still use ephemeral
`--rm` containers and read-only source/contract mounts; output remains ignored under `artifacts/demo/`.

Labels provide local ownership checks, not protection against an adversary controlling the Docker daemon.
Cleanup requires an available daemon and cannot be guaranteed after SIGKILL or daemon failure. INT/TERM handlers
are installed, but signal interruption was not separately exercised by the regression suite.

Chromium and Angular tests were not rerun for this host-only correction. Their previous ARP-DEMO-002 results are
preserved below as historical evidence, not reported as new runs. No contracts, CDS, CRUD, services, packages,
releases or tags were added. These checks do not establish normative transport or full-runtime conformity.

---

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