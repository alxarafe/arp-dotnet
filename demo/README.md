# ARP-DEMO-002 — .NET dictionary HTTP host

Private demonstration application, separate from `src/Alxarafe.Arp.Dictionary`. It adds no dependency to the library
and no required NuGet package: ASP.NET Core comes from the .NET 10 shared framework. **This HTTP API is demo-only,
not a normative ARP transport contract or a runtime-wide capability protocol.**

## Inspection and architecture

The library already provides `CatalogBuilder.BuildCatalog`, `Catalog.ElementIds`, `Catalog.Resolve`, descriptor
`ToJson()` and `Catalog.Validate(...).ToProjection()`. The host only composes these APIs. The library remains
unchanged and its existing solution/test project stays independent of the demo. Inspection baseline:
`arp-dotnet@d9a0acf42b36c8473b0618c80f60bc8415b8ce6a` and the ARP-DEMO-001 client inventory in arp-contract.

At startup the host reads these original, unchanged files from a read-only contract mount:

- `fixtures/dictionary/v1/catalog.json`: the 17 scalar types.
- `fixtures/dictionary/v1/sources/portable-equivalent.json`: one TEXT element with exact metadata `1e127`.

A failed catalog build fails startup; there is no partial catalog, implicit source discovery or client-selected
source file. The info response identifies source paths and SHA-256 hashes. `libraryContractRef` identifies the
candidate pinned by the library (`contract.ref`), not a claim that every feature in a newer contract is implemented.

## Demo API

| Method | Path | Behavior |
| --- | --- | --- |
| GET | `/demo/info` | `demoProtocol: arp-demo-dotnet/1`, runtime, library pin, source hashes and three capabilities |
| GET | `/demo/dictionary/elements` | `elements`: ordered `{id, type}` summaries; no numeric metadata |
| GET | `/demo/dictionary/elements/{id}` | Original descriptor JSON from the library, without lossy conversion |
| POST | `/demo/dictionary/elements/{id}/validate` | One JSON property `value`; actual library validation |

Capabilities are exactly `dictionary.list`, `dictionary.describe` and `dictionary.validate`.
An example body is `{"value":"9223372036854775807"}`. Numbers are strings in the scalar codec; booleans retain
their JSON kind and `null` remains null. Numeric/object/array values are passed to the library as incompatible
kinds and receive its real type rejection, never automatic coercion. An absent `value` is a malformed request.

Validation returns the library projection: `{valid, issues, normalizedValue}` on success and `{valid, issues}`
on failure. Normalization preserves exact strings and booleans. A valid or invalid value returns HTTP 200; missing
elements return 404 with `DICTIONARY_ELEMENT_NOT_FOUND`. Descriptor metadata numbers remain JSON Number with their
original exact value; browser consumers must keep raw text or use an exact decoder, never JavaScript `Number`.

Errors are JSON `{valid:false, issues:[{code:...}]}`: 400 for malformed JSON/shape (including duplicate `value` or
unknown properties), 413 over the 16 KiB body limit, 415 for non-JSON content, 403 for disallowed request origins,
404 for unknown routes/elements and 500 for execution failures. Internal traces remain in host logs, not responses.
Application error codes use `DEMO_*` and do not change dictionary diagnostic codes.

Malformed JSON, invalid UTF-8 and isolated UTF-16 surrogate escapes return **400 `DEMO_INVALID_JSON`**.
JSON parsing defers some Unicode decoding: the host explicitly decodes strings and property names before passing
inputs to the library. Only input parsing/decoding exceptions are classified this way; internal/library exceptions
remain execution failures (500). Valid surrogate pairs, literal escaped backslashes and valid UTF-8 remain valid.
Shape violations retain `DEMO_INPUT_SHAPE`; library value rejection still returns its original result with HTTP 200.

CORS allows exactly `ARP_DEMO_ORIGIN` (default `http://127.0.0.1:4173`), GET/POST and Content-Type, without
credentials. Preflights for other origins have no allow-origin header; ordinary requests with another Origin are
rejected. Requests without Origin remain possible: **CORS is not authentication**. This unauthenticated local demo
must not be treated as a production authorization layer. There is no simulated identity, persistence, CRUD,
projection engine, service dispatch or automatic capability inference from a Git repository.

## Docker execution

Prerequisites: Bash, Docker Engine, a checkout of arp-contract and Python 3 for HTTP checks. No host .NET SDK is
required. The SDK image is fixed by digest in `demo/run`. Builds run in an ephemeral copy, use locked restore, and
write only ignored `artifacts/demo/`. The source and contract are mounted read-only. No image/package is published.

From this repository, with the contract path adjusted to the actual checkout:

```sh
export ARP_CONTRACT_PATH=/absolute/path/to/arp-contract
./demo/run build
./demo/run start
# Open the Angular client at http://127.0.0.1:4173 and configure .NET URL http://127.0.0.1:5100
./demo/run stop
```

Default publication is exclusively `127.0.0.1:5100` → container port 8080. `ARP_DEMO_PORT` changes the host port.
`ARP_DEMO_ORIGIN` sets one exact http(s) Angular origin without a trailing slash; localhost and 127.0.0.1 are distinct.
`ARP_DEMO_CONTAINER` changes the local container name. Start refuses name collisions. Stop resolves an exact
container ID and requires both `arp.demo=dotnet-dictionary`
and `arp.demo.owner=<absolute checkout path>` labels. It does not remove unrelated containers, images or volumes.
Start first creates a container and records its exact ID before attempting startup. Failure/interrupt cleanup is
registered beforehand and only targets that created ID, including when port publication fails. A pre-existing
same-name container is never adopted by a failed start, even if it has matching demo labels. A successful standalone
start leaves the host running until explicit stop; check always cleans up its own host. Labels identify cooperative
local ownership, not a security boundary against a user with Docker daemon access.
If a contract file changes, restart the host to build a new snapshot and reconnect the client.

## Checks

The existing conformance tests require a **separate, clean checkout** at `contract.ref`, including the annotated
candidate tag. This restriction is unchanged; do not point those tests at an arbitrary newer contract checkout.

```sh
export ARP_CONTRACT_PATH=/absolute/path/to/current/arp-contract
export ARP_CONFORMANCE_PATH=/absolute/path/to/pinned/arp-contract
./demo/run check
```

This builds the host, runs the original .NET solution/tests against the pinned checkout, starts the actual host,
waits for readiness, tests its API using original type vectors and then stops/removes the test container, including
on failure. `demo/run test-library` runs just the existing tests. HTTP checks include all 17 original type vectors,
18-element listing, exact INT64/DECIMAL, huge numeric metadata, boolean/null/empty distinctions, structured errors,
request limits and restricted CORS. Unicode/JSON regressions assert stable response codes and envelopes, including
invalid UTF-8, isolated surrogate escapes, invalid property names/nested strings, malformed JSON, valid pairs and
literal escaped text. No mocks or fixture modifications are involved.

After building, run the Docker lifecycle regression suite with the same conformance path:

```sh
python3 demo/scripts/check-docker.py
```

It runs real containers with unique names, reserves its own loopback port, checks failed standalone and check-mode
starts on that occupied port, name collisions, each ownership label, configured non-default port, normal and repeated
stop. It snapshots existing running containers' status/start time/restart count and asserts they are unchanged.
Its finalizer removes only exact container IDs created by the test. It requires the pinned conformance checkout
because the check-mode failure case also exercises build/library tests.

For Chromium end-to-end tests, run `demo/client/angular/bin/integration-dotnet` in arp-contract with
`ARP_DOTNET_PATH` pointing to this checkout. It builds both sides, starts this real host and tests actual cross-origin
browser requests in desktop/mobile and all five themes in ES/EN. Its current Docker host-network setup targets
Linux; other Docker platforms need equivalent loopback connectivity, not an invented API or a mock backend.

## Limits and next steps

Only dictionary capabilities are executable. Source registration/authentication for arbitrary applications,
entities/composites, CDS projections, CRUD, persistence and domain services remain separate future work. There is
no liveness heartbeat or automatic retry: the client labels the last successful request and detects later failures
on the next operation. This demo does not approve a public HTTP protocol or certify unimplemented runtime features.