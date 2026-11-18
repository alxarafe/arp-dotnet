# Final review: ARP-DOTNET-LISTGET-001

## Executed verification

- 458 existing xUnit runtime tests: zero failures/skips, including 327 DICT and 96 CDS vectors.
- 14 existing native PostgreSQL scenarios: five shared contract database scenarios plus nine additional checks.
- 16 existing physical-storage seed scenarios plus recovery in a replacement container, same volume, zero observations.
- 21 LIST/GET PostgreSQL scenarios, including 24 complete negative shared request/expectation replays.
- 182 recorded public responses validated against the independent pinned LISTGET result schema.
- dotnet format whitespace checked per affected project and git diff --check passed.

The scenario counts are separate from the xUnit test count; repeated assertions and schema checks are not added
as additional tests. No complete audit of Java was repeated. No complete 88-vector conformance is claimed.

## Security, concurrency and invalidation

Both simple and composite layouts execute real SQL: catalog LIST, derived reports LIST, public/hidden-key GET,
missing/inaccessible GET, stable multipage traversal, clamping, empty pages and a conjunction of SQL equalities.
Exact shared product values include INT64 beyond 2^53, high precision DECIMAL, combining Unicode/emoji, empty
strings, nulls, booleans, dates and historical rating 11. A further numeric test exceeds CLR decimal capacity.

References are tested at lengths 0, 1, 128 and 129, with corruption, unknown evidence, wrong purpose, independent
principal/scope/policy/projection bindings, policy-epoch revocation, host recreation and expired generations.
Rejected/unusable references perform zero row SELECT. Valid references preserve GET behavior.
Raw hidden keys, malformed public components, duplicate JSON members and invalid Unicode fail safely.
The authorizer precedes definition errors, grants operations independently and must allow the complete projection.

Two actual threads coordinate after a SELECT: external ALTER TABLE receives SQLSTATE 55P03 under a bounded
lock timeout; the other thread invalidates the catalog while the read is paused. No stale output escapes,
stale/null publication callbacks never run, and DDL/rebuild succeed after release.
Separate tests revoke operation permission, change authoritative policy revision and remove the active composition
after SQL but before publication. Unknown-reference GET still executes the final generation fence without row SQL.
Provider failures before connection and after a real SELECT return only a safe failure. NaN, infinite date and
an out-of-range stored integer discard the complete page. External drift, removed columns and narrowing reject
stale/incompatible schemas; a new column never expands projected values. Real inheritance and fixed CHAR padding
fail closed before row SELECT. Seven-component keys and 128-character cursors work; eight fail before row SELECT.

## Matrix interpretation

N: complete shared request/expected response replay with live PostgreSQL metadata, adapting only registered
target aliases. These 24 negative dispatch vectors assert zero row SELECT, not native row execution.
E: an identified executed PostgreSQL scenario proves equivalent behavior; it is not exact vector replay.
U: pending/not supported, neither passed nor equivalent. Totals: **N=24, E=58, U=6**.

Scenario aliases (each appears as LISTGET_PASS in dotnet.log):

- T1: `simple: values, paging and GET`.
- T2: `simple: bindings and reference input`.
- T3: `simple: authorization and registry`.
- T4: `simple: empty SQL and derived restrictions`.
- T5: `simple: metadata rebuild and provider failure`.
- T6: `simple: scalar failure discards page`.
- T7: `simple: in-flight revocation and composition`.
- T8: `simple: concurrent invalidation and DDL`.
- T9: `composite: values, paging and GET`.
- T10: `composite: bindings and reference input`.
- T11: `composite: authorization and registry`.
- T12: `composite: empty SQL and derived restrictions`.
- T13: `composite: metadata rebuild and provider failure`.
- T14: `composite: scalar failure discards page`.
- T15: `composite: in-flight revocation and composition`.
- T16: `composite: concurrent invalidation and DDL`.
- T17: `shared request vectors`.
- T18: `authorization before definition failure`.
- T19: `empty GET final fence`.
- T20: `storage capacity and incompatible domains`.
- T21: `unsupported storage forms`.

| Vector | Status | Evidence |
| --- | --- | --- |
| get-authorized-hidden-key | E | T9 |
| get-null-preserved | E | T9 |
| get-historical-rating | E | T9 |
| get-exact-decimal | E | T9 |
| get-unicode-and-empty-values | E | T9 |
| get-unknown-reference | E | T10 |
| get-existing-but-row-denied | E | T9 |
| get-scope-binding-denied | E | T10 |
| get-principal-binding-denied | E | T10 |
| get-policy-binding-denied | E | T10 |
| get-projection-binding-denied | E | T10 |
| get-simple-typed-key | E | T1 |
| get-composite-typed-key | E | T9 |
| get-missing-typed-key | E | T9 |
| get-raw-hidden-key-rejected | N | T17 |
| key-null | N | T17 |
| key-incomplete | N | T17 |
| key-reordered | N | T17 |
| key-extra | N | T17 |
| key-number-not-string | N | T17 |
| key-wrong-type | N | T17 |
| key-out-of-range | N | T17 |
| list-first-page | E | T9 |
| list-second-page | E | T9 |
| list-last-page | E | T9 |
| list-server-clamps-limit | E | T9 |
| list-empty-authorized-set | E | T12 |
| list-numeric-order-simple-keys | E | T1 |
| list-derived-projection | E | T12 |
| list-limit-zero | N | T17 |
| list-limit-negative | N | T17 |
| list-limit-bool | N | T17 |
| list-limit-fraction | N | T17 |
| list-limit-too-large | N | T17 |
| list-limit-null | N | T17 |
| list-arbitrary-filter | N | T17 |
| list-arbitrary-sort | N | T17 |
| list-fields-injection | N | T17 |
| list-offset-rejected | N | T17 |
| cursor-unknown | N | T17 |
| cursor-null | N | T17 |
| cursor-limit-changed | E | T10 |
| cursor-context-changed | E | T10 |
| operation-not-authorized | E | T11 |
| target-not-registered | E | T11 |
| module-disabled | E | T11 |
| operation-not-enabled | E | T11 |
| derived-operation-not-exposed | E | T11 |
| selected-field-not-readable | E | T11 |
| client-context-injection | N | T17 |
| client-context-cannot-authorize | N | T17 |
| input-null | N | T17 |
| read-capability-not-proven | E | T11 |
| table-without-key | E | T20 |
| invalid-definition | E | T13 |
| unauthorized-definition-error-concealed | E | T18 |
| physical-incompatibility | E | T20 |
| stale-catalog-generation | U | - |
| stale-record-reference | E | T10 |
| stale-continuation | E | T10 |
| invalidation-during-read | E | T16 |
| authorization-lost-before-publication | E | T15 |
| provider-failure | E | T13 |
| last-row-decode-failure-no-partial-page | E | T14 |
| unexpected-null-nonnullable | U | - |
| duplicate-record-identity | U | - |
| new-column-not-published-after-rebuild | E | T13 |
| removed-published-column-rebuild-rejected | E | T13 |
| public-application-without-principal | E | T9 |
| empty-get-still-checks-publication-generation | E | T19 |
| context-changed-before-publication | E | T15 |
| list-limit-one | E | T12 |
| list-after-last-empty | E | T12 |
| invalid-server-limit-registration | U | - |
| modules-catalog-list | E | T9 |
| modules-catalog-get-denied-independently | E | T11 |
| modules-catalog-get-authorized | E | T9 |
| modules-reports-derived-list | E | T12 |
| modules-reports-get-unavailable | E | T11 |
| modules-reports-inactive | E | T11 |
| modules-all-inactive | E | T11 |
| modules-reports-without-catalog | U | - |
| modules-undeclared-projection-dependency | U | - |
| modules-deactivation-during-read | E | T15 |
| modules-row-restriction-still-authoritative | E | T12 |
| modules-client-cannot-activate | N | T17 |
| modules-field-access-still-authoritative | E | T11 |
| modules-permission-revoked-before-publication | E | T15 |

## Equivalence differences

- Positive outputs use real protected evidence, replacing neither the contract's fixed tokens nor its oracle.
  Exact projected values are compared directly with shared products; PK simple/composite and numeric order are tested.
- Missing and row-denied GET use SQL key/restriction conjunctions over actual data, with indistinguishable output.
- Token binding tests use application-issued evidence and the contract's alternate GET-enabled reports definition.
- Clamping is tested on identified over the same physical model; limiting is projection-independent.
- Derived reports uses tenant/product equalities yielding one row; the finite fixture may use a different allowed set.
  Both equalities are confirmed in SQL before pagination. No arbitrary allowedRows list is claimed as a SQL policy.
- Unregistered/inactive/all-inactive use the application's controlled active registry, not a general module parser.
  Host GET denial is independent from LIST; derived reports GET remains unavailable unless explicitly exposed.
- Empty authorized sets and after-last pages are actual SQL queries; unknown-reference GET is a no-row-query path.
- No-PK, removed-column and narrowing cases use real PostgreSQL DDL, not mocked metadata.
- Public application context uses a host-defined public identity without requiring a principal.
- Final reauthorization checks the original immutable context against an authoritative policy revision.
- Metadata and permission changes are tested separately: generation/composition failure and authorization denial
  remain distinct. The application must route policy changes through ChangeAccess.

## Six individually pending obligations

- **stale-catalog-generation**: this host resolves its own current catalog; it does not accept external stale
  catalog input. It tests stale snapshot publication separately but does not claim exact invocation equivalence.
- **unexpected-null-nonnullable**: real NOT NULL prevents the physical contradiction. The defensive null failure
  exists; injecting a faulty provider is another integration and was not executed.
- **duplicate-record-identity**: the physical PK prevents duplicates. Defensive identity rejection exists,
  but a faulty-provider duplicate injection was not executed.
- **invalid-server-limit-registration**: invalid defaults/maxima fail at trusted ReadTarget construction (tested).
  This differs from the neutral fixture's INVALID_DEFINITION invocation envelope; it is not counted as equivalent.
- **modules-reports-without-catalog**: general dependency composition is outside this slice. The application must
  validate its composition before supplying the active registry; this requires application/module integration.
- **modules-undeclared-projection-dependency**: no general module source/manifest parser is supplied here.
  Dependency declaration validation remains with that integration.

These limits do not claim compliance for an unexecuted obligation. The supported profile requires ordinary tables,
demonstrated integer PKs, bounded deterministic pagination, exact demonstrated codecs, SQL integer equalities,
resolver-derived projections and dependency-valid application compositions. There is no general permission engine,
HTTP host, ORM, row cache, external DDL watcher, distributed coordination or cross-request data snapshot.

## Evidence and delivery

Final evidence: ~/Desarrollo/tmp/arp-dotnet-listget-001-final-review/

- dotnet.log, dotnet-isolation.json: complete executions, formatting, PostgreSQL identity and ownership/preservation.
- listget-responses.json, schema-validation.json: public responses and result-schema validation.
- vector-coverage.json, listget-summary.json: all 88 vector classifications and the 21 executed scenario names.
- source-sha256.json, contract-integrity.json: exact source fingerprints and independent historical/LISTGET pins.
- final.diff, diff-stat.txt: complete final tracked/untracked patch for review.

The original tree was clean; no preexisting work was stashed or overwritten. No fixtures were copied into the repo.
Only arp-dotnet is modified; auxiliary builds/evidence live outside Alxarafe. No worktrees or clones were created.
No commit, push, PR, merge, tag or release was performed.
