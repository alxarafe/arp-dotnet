# ARP-SQL-PROVIDERS-DOTNET-001

## Scope and architecture

Branch `feature/arp-sql-providers-dotnet-001` starts at updated main
`4bc9842c3241ff39f34be9f35ac823fe1ea7c3f7`, incorporating merged PR #10.
The previous clean refactor branch had the same tree as the squash merge and was
removed locally; its remote branch was already absent. No unrelated work was discarded.

The ARP core, `ListGetHost`, `PhysicalCatalog`, `ReadPorts`, the existing PostgreSQL
provider and `SqlRecordReader.cs` are unchanged. There is one LIST/GET execution
algorithm, and no new dialect composition framework, host, cache or permission system.

```text
Alxarafe.Arp.Dictionary                    neutral core and existing ports
        ^
Alxarafe.Arp.Cds.Sql                       SqlRecordReader + SqlMetadata plumbing
        ^
        +-- Alxarafe.Arp.Cds.PostgreSql    PostgreSqlRecordReader (unchanged)
        +-- Alxarafe.Arp.Cds.MySql         MySqlRecordReaderBase
        |                                    +-- MySqlRecordReader
        |                                    +-- MariaDbRecordReader
        +-- Alxarafe.Arp.Cds.SqlServer     SqlServerRecordReader
```

All production providers use `DbConnection`/`DbCommand`, without a driver package
reference. Applications supply fresh closed connections and register the matching
observation provider with the existing physical catalog. MySqlConnector, SqlClient
and Npgsql are dependencies of the optional integration executables only.

## Methods and differences

| Class | Configuration / overrides |
| --- | --- |
| `SqlRecordReader` | Unchanged nonvirtual Prepare/Open, SELECT/WHERE/ORDER, bound parameters, bounded decode, protected two-transaction attempt, mandatory completion and disposal. |
| `MySqlRecordReaderBase` | Backtick delimiters; BeginTransaction; InitializeSession; LimitClause; ValueExpression; PortableValue; SupportsColumn; AcquireReadProtection; ObservePhysical. |
| `MySqlRecordReader` | Inherits all family behavior, no overrides. |
| `MariaDbRecordReader` | Constructor sets the family property `IsMariaDb`; inherits the same algorithms. |
| `SqlServerRecordReader` | Bracket delimiters; InitializeSession; LimitClause; KeyComparison; ValueExpression; SupportsColumn; AcquireReadProtection; ObservePhysical. |
| `SqlMetadata` | Bounded parameterized metadata query/materialization, physical descriptor serialization and exact integer bounds. No row-query algorithm or dialect interface. |

`MySqlObservationProviderBase` implements one family metadata algorithm; its concrete
MySQL and MariaDB descendants select and verify the family. `SqlServerObservationProvider`
queries the actual SQL Server catalogs. Each reader uses its matching observation
provider through the existing ObservePhysical hook. Engine-specific protection/session
helpers are reused by observation and execution; they do not implement another read pipeline.

### MySQL / MariaDB

Both tested engines use ordinary, nonpartitioned InnoDB tables, LIMIT, parameterized
integer equalities, full-key tuple comparisons and transaction-held metadata locks.
A read-only transaction is requested before BeginTransaction. Session lock timeouts
are bounded. MySQL uses `max_execution_time` in milliseconds; MariaDB uses
`max_statement_time` in seconds. The actual VERSION() distinguishes the two families.

Values are read as UTF-8 character conversions, preserving full INT64 and DECIMAL
without a floating intermediate. `TINYINT(1)` is observed as INT8, never inferred as
BOOLEAN. Only an explicit logical BOOLEAN binding admits it, and only the exact
stored values 0/1 decode. The regression stores 2 and requires failure with no partial page.

A real metadata-authority regression creates a column invisible to SELECT * and
accounts with all visible column grants versus a full table SELECT grant. MySQL
8.4 permits the first SELECT *; MariaDB 12.3 denies it with error 1142. Both observation
providers reject incomplete authority and accept the complete table grant.
Whole-table authority is demonstrated from direct global/schema/table SELECT grants;
role-only authority is not inferred or claimed supported. This restriction prevents
publishing a silently incomplete descriptor, including invisible columns.

### SQL Server

OFFSET 0 / FETCH NEXT supplies the bounded limit; pagination still uses keyset WHERE,
not a client offset. Composite positions use a lexicographic OR-of-AND predicate;
GET requires equality on every PK component. All values remain parameters.

Statement-lifetime Sch-S alone is insufficient. The provider takes a transaction-held
table S lock with TABLOCK,HOLDLOCK, which also conflicts with Sch-M. This is intentionally
stronger than PostgreSQL ACCESS SHARE and can block writers. The independent publication
guard takes the same protection before data commit. Session LOCK_TIMEOUT and command
timeouts bound waits; XACT_ABORT is enabled. Tests use ReadCommitted on the actual server.

INT64, BIT, DATE and Unicode text use lossless conversions (DATE style 23). Declared
character-length metadata uses the engine's actual byte limit, avoiding an incorrect
Unicode character-count proof. Alias/unknown types do not receive invented scalar facts.

SQL Server DECIMAL has at most 38 digits. The shared fixture combines a 24-integer-digit
value with an 18-fractional-digit value, which cannot share a single DECIMAL(38,s).
The complete fixture therefore uses sql_variant containing actual per-value DECIMAL/
NUMERIC values. The physical type remains sql_variant with no invented physical DECIMAL
proof; only an explicit logical DECIMAL binding and a decimal/numeric runtime subtype
are accepted. Text and floating subtypes fail even if their conversion looks numeric.
A separate regression verifies ordinary DECIMAL(38,18) LIST/GET. No fixture or logical
contract is rewritten, and no float or CLR decimal intermediate limits precision.

## Safety and supported profile

The unchanged fixed pipeline acquires a data protection transaction, reobserves physical
facts while protected, executes/decodes a bounded batch, and acquires the independent
publication guard before releasing the first protection. The host performs final
access/generation checks, mandatory data commit, final checks and publication while
its catalog fence and the second physical guard remain held. Guard rollback/disposal
is mandatory; commit, close and guard-rollback failures produce an output-free failure.
Derived readers cannot replace Prepare/Open/Complete or authorize/publish results.

Real tests attempt external DDL while rows are selected and after DATA commit before
publication, for LIST, GET and empty results. The same DDL succeeds after invocation.
Other tests change physical structure without explicit invalidation and require
GENERATION_CHANGED, then explicitly rebuild. Concurrent catalog invalidation and access
revocation never publish stale or partial data. The original PostgreSQL queued-DDL
handoff and storage recovery regressions remain separately versioned and executed.

The demonstrated profile uses registered ordinary tables; integer simple/two-column
composite PKs; SQL equality row restrictions; deterministic bounded paging; explicit
public projections; exact demonstrated codecs; and application-validated compositions.
Views, partitioned MySQL-family tables, non-InnoDB tables, SQL Server memory-optimized/
temporal/partitioned/RLS tables, opaque types and unsupported conversions fail closed.
SQL Server requires whole-object SELECT and database VIEW DEFINITION authority, so
security policies cannot be hidden by metadata visibility. A real RLS policy is rejected
for both the administrator and the permitted reader. CHECK constraints
and user-defined types do not produce unjustified scalar-domain proofs. Fixed CHAR/NCHAR
padding is not claimed portable and is rejected by the readers.

Capabilities not demonstrated: other engine/driver/OS versions, pooled-session reuse,
role-only MySQL/MariaDB authority, SQL Server RCSI/SNAPSHOT configurations, advanced
storage forms, cross-page snapshots under changing data, and distributed invalidation.
No external DDL watcher is added; each protected read reobserves facts. Storage YAML
persistence/recovery is rechecked for the existing PostgreSQL implementation; no new
engine-specific persistence implementation is introduced.

## Test policy and execution

`Arp.slnx` includes the new production libraries, not integration executables.
Ordinary tests require the local historical contract checkout and restored packages;
they do not invoke Docker, download engines or use database connections. An offline
SDK process with --network none, no Docker socket and no database environment passes
build and all 463 ordinary tests. The SDK container is a verification environment,
not a requirement of the ordinary dotnet commands.

`providers/tests/SqlEngines.Integration` is an explicit executable, with one shared
set of regression assertions and engine-specific fixture provisioning. The assertions
reuse the established PostgreSQL regression scenarios; shared contract fixture files
are read directly from the independently pinned checkout, never copied into this repo.
`providers/integration-sql-engines.py --engine ENGINE|all` runs it optionally, sequentially.
The original PostgreSQL suite and runner remain unchanged and optional.

The new runner pins engine image digests, creates its own random labelled network and
fresh ephemeral containers/volumes, publishes no host ports, checks a random database
ownership nonce before fixture DDL and removes only its own resources. SQL Server uses
Developer Edition. No image/binary, database state, credential or generated result is
versioned. Evidence/source copies/locks caches stay under ~/Desarrollo/tmp/. Integration
driver packages are pinned in the optional project's lock; production has no dependency
on those drivers. The test image and its dependencies may be fetched only on explicit
integration execution, never as a side effect of ordinary tests.

The two integration runners must not overlap: each records every preexisting volume
as foreign. An early overlapping diagnostic run completed its tests but correctly
flagged removal of a volume owned by the other run in this same task. Final acceptance
uses sequential runs with a clean preservation check. No resources from another
project were deleted.

## Results and evidence

Final acceptance: .NET SDK 10.0.404 / .NET 10, Linux AMD64. Drivers are
Npgsql 10.0.0, MySqlConnector 3.0.0 (MySQL and MariaDB), Microsoft.Data.SqlClient 7.1.2.

| Engine actually run | Shared scenarios passed | Schema-valid response envelopes | Matrix N / E / U |
| --- | ---: | ---: | --- |
| 18.6 (Debian 18.6-1.pgdg13+2) | 23 | 198 | 24 / 58 / 6 |
| 8.4.13 | 24 | 200 | 24 / 58 / 6 |
| 12.3.3-MariaDB-ubu2404 | 24 | 200 | 24 / 58 / 6 |
| Microsoft SQL Server 2022 (RTM-CU28) (KB5125472) - 16.0.4325.4 (X64)  | 25 | 204 | 24 / 58 / 6 |

These are grouped integration scenarios, not xUnit test counts. All four engine processes
exit 0; 802 response envelopes validate against the pinned LIST/GET result schema.
The PostgreSQL metadata-authority regression runs in the original suite rather than
being counted as an unexecuted no-op in the shared suite.

The separately rerun original PostgreSQL suite passes 463 ordinary xUnit tests,
14 observation scenarios (5 contractual and 9 additional), 25 LIST/GET scenarios,
16 storage scenarios and actual recovery after container replacement with zero
introspection. Its final isolation report has completed=true, preserved preexisting
containers/volumes, unchanged logical YAML and removal of its own storage volume only.
The final four-engine runner also reports foreignResourcesPreserved=true.

Concurrency/security results are backed by each engine log: integer row restrictions
before limits, operation/field denials, scope/policy/token binding, explicit concurrent
invalidation, post-SELECT revocation, actual external DDL lock timeouts before/after
data commit, external uninvalidated DDL detection, commit/close/guard-rollback failure
and whole-page decode rejection. SQL Server additionally demonstrates rejection of
an actual RLS policy for both administrator and the metadata reader, DECIMAL native
LIST/GET, exact per-value variant DECIMAL and rejection of text/float variants.
MySQL/MariaDB additionally reject stored BOOLEAN representation 2 and partial metadata
authority even when MySQL SELECT * itself succeeds.

The final offline ordinary build/tests pass with zero build warnings/errors and
463 passed / 0 failed / 0 skipped, without network, Docker socket or database services.
All new C# projects/helper pass dotnet format whitespace --verify-no-changes;
new C# and runner lines respect 120 columns and configured final-newline policy.
The runner's final line wrapping is Python-AST identical to the executed version;
all C# source/project/lock/solution hashes match the four-engine tested snapshot exactly.
No behavioral source changes follow acceptance. Documentation is completed afterwards.

Evidence root: `~/Desarrollo/tmp/arp-sql-providers-dotnet-001/`:

- `final-engines-verified/`: source-sha256.json, source copy, build.log, one engine.log,
  coverage JSON, responses JSON, validation.json and isolation summary.json.
- `final-postgresql-legacy/`: dotnet.log, source-sha256.json, vector-coverage.json,
  listget-summary.json, listget-responses.json, dotnet-isolation.json.
- `ordinary-offline-final.log`, `format-final.log`, `format/offline-results/ordinary-offline.trx`.
- `runner-format-equivalence.json`, `final-source-sha256.json`, `final-checks.json`, `final.diff`.

Diagnostic earlier runs are retained separately. They are not acceptance evidence.
No commit, push, PR, merge, tag or release is made.

## Coverage of all 88 vectors

The original matrix remains unchanged: **24 direct negative fixture request/response
replays (N), 58 behavioral equivalents using real rows (E), 6 pending (U)** on each
engine. N cases assert zero row SELECT. E cases use issued cryptographic tokens and
actual engine execution, rather than copying the finite fixture oracle's token values.
Every equivalence names a scenario that actually ran on that engine. No full
conformance claim is made and no pending obligation was promoted without new evidence.

The six pending items remain individually distinguished:

- `stale-catalog-generation`: external stale snapshot input is not a supported host
  input. Concurrent invalidation and stale publication are tested through real catalog fences.
- `unexpected-null-nonnullable`: physical NOT NULL prevents that contradiction;
  a faulty-provider injection is a separate integration still pending.
- `duplicate-record-identity`: physical PK prevents duplicates; faulty-provider
  duplicate injection remains pending despite the defensive rejection in the reader.
- `invalid-server-limit-registration`: trusted registration rejects invalid limits
  at ReadTarget construction, rather than returning the fixture's invocation envelope.
  The runtime rejects them; exact envelope integration remains unclaimed.
- `modules-reports-without-catalog`: dependency validation belongs to the application
  supplying an already valid active composition, outside this supported integration profile.
- `modules-undeclared-projection-dependency`: module manifest/source dependency
  validation requires that same application integration, not another SQL provider.

These are outstanding coverage/integration obligations, not evidence of a failure
of the demonstrated profile. Role-only MySQL metadata authority, SQL Server advanced
isolation/storage configurations and other versions remain explicit additional limits.

| Vector | All four engines | Executed evidence / pending reason |
| --- | --- | --- |
| get-authorized-hidden-key | E | composite: exact values and pages |
| get-null-preserved | E | composite: exact values and pages |
| get-historical-rating | E | composite: exact values and pages |
| get-exact-decimal | E | composite: exact values and pages |
| get-unicode-and-empty-values | E | composite: exact values and pages |
| get-unknown-reference | E | composite: references and cursors |
| get-existing-but-row-denied | E | composite: exact values and pages |
| get-scope-binding-denied | E | composite: references and cursors |
| get-principal-binding-denied | E | composite: references and cursors |
| get-policy-binding-denied | E | composite: references and cursors |
| get-projection-binding-denied | E | composite: references and cursors |
| get-simple-typed-key | E | simple: exact values and pages |
| get-composite-typed-key | E | composite: exact values and pages |
| get-missing-typed-key | E | composite: exact values and pages |
| get-raw-hidden-key-rejected | N | shared negative request vectors |
| key-null | N | shared negative request vectors |
| key-incomplete | N | shared negative request vectors |
| key-reordered | N | shared negative request vectors |
| key-extra | N | shared negative request vectors |
| key-number-not-string | N | shared negative request vectors |
| key-wrong-type | N | shared negative request vectors |
| key-out-of-range | N | shared negative request vectors |
| list-first-page | E | composite: exact values and pages |
| list-second-page | E | composite: exact values and pages |
| list-last-page | E | composite: exact values and pages |
| list-server-clamps-limit | E | composite: exact values and pages |
| list-empty-authorized-set | E | composite: SQL restrictions and derived projection |
| list-numeric-order-simple-keys | E | simple: exact values and pages |
| list-derived-projection | E | composite: SQL restrictions and derived projection |
| list-limit-zero | N | shared negative request vectors |
| list-limit-negative | N | shared negative request vectors |
| list-limit-bool | N | shared negative request vectors |
| list-limit-fraction | N | shared negative request vectors |
| list-limit-too-large | N | shared negative request vectors |
| list-limit-null | N | shared negative request vectors |
| list-arbitrary-filter | N | shared negative request vectors |
| list-arbitrary-sort | N | shared negative request vectors |
| list-fields-injection | N | shared negative request vectors |
| list-offset-rejected | N | shared negative request vectors |
| cursor-unknown | N | shared negative request vectors |
| cursor-null | N | shared negative request vectors |
| cursor-limit-changed | E | composite: references and cursors |
| cursor-context-changed | E | composite: references and cursors |
| operation-not-authorized | E | composite: authorization |
| target-not-registered | E | composite: authorization |
| module-disabled | E | composite: authorization |
| operation-not-enabled | E | composite: authorization |
| derived-operation-not-exposed | E | composite: authorization |
| selected-field-not-readable | E | composite: authorization |
| client-context-injection | N | shared negative request vectors |
| client-context-cannot-authorize | N | shared negative request vectors |
| input-null | N | shared negative request vectors |
| read-capability-not-proven | E | composite: authorization |
| table-without-key | E | key capacity and incompatible storage |
| invalid-definition | E | composite: metadata and provider errors |
| unauthorized-definition-error-concealed | E | authorization before definitions |
| physical-incompatibility | E | key capacity and incompatible storage |
| stale-catalog-generation | U | The host resolves its own catalog; externally supplied stale snapshots are not an input. |
| stale-record-reference | E | composite: references and cursors |
| stale-continuation | E | composite: references and cursors |
| invalidation-during-read | E | composite: invalidation and external DDL |
| authorization-lost-before-publication | E | composite: revocation |
| provider-failure | E | composite: metadata and provider errors |
| last-row-decode-failure-no-partial-page | E | composite: decoding failure discards page |
| unexpected-null-nonnullable | U | Physical NOT NULL prevents this contradiction; faulty-provider null injection not executed. |
| duplicate-record-identity | U | Physical PK prevents duplicate identities; faulty-provider duplicate injection not executed. |
| new-column-not-published-after-rebuild | E | composite: metadata and provider errors |
| removed-published-column-rebuild-rejected | E | composite: metadata and provider errors |
| public-application-without-principal | E | composite: exact values and pages |
| empty-get-still-checks-publication-generation | E | empty GET final fence |
| context-changed-before-publication | E | composite: revocation |
| list-limit-one | E | composite: SQL restrictions and derived projection |
| list-after-last-empty | E | composite: SQL restrictions and derived projection |
| invalid-server-limit-registration | U | Invalid limits rejected at ReadTarget construction (tested), outside the neutral envelope. |
| modules-catalog-list | E | composite: exact values and pages |
| modules-catalog-get-denied-independently | E | composite: authorization |
| modules-catalog-get-authorized | E | composite: exact values and pages |
| modules-reports-derived-list | E | composite: SQL restrictions and derived projection |
| modules-reports-get-unavailable | E | composite: authorization |
| modules-reports-inactive | E | composite: authorization |
| modules-all-inactive | E | composite: authorization |
| modules-reports-without-catalog | U | Application validates module dependencies before registration; no general module engine. |
| modules-undeclared-projection-dependency | U | General module source/dependency manifest validation requires application integration. |
| modules-deactivation-during-read | E | composite: revocation |
| modules-row-restriction-still-authoritative | E | composite: SQL restrictions and derived projection |
| modules-client-cannot-activate | N | shared negative request vectors |
| modules-field-access-still-authoritative | E | composite: authorization |
| modules-permission-revoked-before-publication | E | composite: revocation |

Historical contract.ref remains 7a62bba0e8f84dbc198e837baf63fc7e0d8722e1.
Independent listget-contract.ref remains eb38cb96e6ccd218734df2a181908de46067e0c7.

## Primary technical references

- [MySQL metadata locking](https://dev.mysql.com/doc/refman/8.4/en/metadata-locking.html).
- [MySQL table privileges](https://dev.mysql.com/doc/refman/8.4/en/information-schema-table-privileges-table.html).
- [SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17).
- [MySqlConnector 3.0.0](https://www.nuget.org/packages/MySqlConnector/3.0.0).
- [Microsoft.Data.SqlClient 7.1.2](https://www.nuget.org/packages/Microsoft.Data.SqlClient/7.1.2).