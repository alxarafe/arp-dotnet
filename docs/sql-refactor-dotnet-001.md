# ARP-SQL-REFACTOR-DOTNET-001

Branch: `feature/arp-sql-refactor-dotnet-001`, from merged origin/main
`4f90b6b2f98eb9e231f45873f5ab04cd98599c01`. Initial tree was clean.
This delivery contains no commit, push or PR. No other engine is implemented.

## Projects and final class tree

```text
Alxarafe.Arp.Dictionary                         [pure core, no SQL/provider project reference]
  CdsReadResolver / CdsReadCodec / CdsReadPublication
  Cds/Physical/
    PhysicalCatalog : IReadCatalog
    IPhysicalObservationProvider / IInvalidatablePhysicalProvider / PhysicalObservationException
  Cds/Reads/
    ListGetHost                                [sealed, moved here from provider]
    IReadCatalog                               [existing CDS snapshot and publication boundary]
    IRecordReader                              [Prepare + Open]
    IReadAttempt                               [immutable Rows + Complete + Dispose]
    PreparedRead / ReadPlan / ReadColumn / ReadRow / ReadFault

Alxarafe.Arp.Cds.Sql -> Alxarafe.Arp.Dictionary   [optional ADO.NET infrastructure]
  abstract SqlRecordReader : IRecordReader
    private sealed SqlPreparedRead             [owner-bound SQL resource binding]
    private sealed Attempt : IReadAttempt      [resources held through final publication]

Alxarafe.Arp.Cds.PostgreSql -> Alxarafe.Arp.Cds.Sql
  PostgreSqlRecordReader : SqlRecordReader
  PostgreSqlObservationProvider
  existing PhysicalYamlStore / persisted-provider mechanisms

PostgreSql.Integration -> PostgreSql provider + Npgsql
  existing native scenarios + completion/inheritance regressions
Alxarafe.Arp.Dictionary.Tests -> pure core only
  existing tests + ReadHostTests
```

The core host retains its public namespace. Consumers must rebuild because the host now lives in the core
assembly and its constructor takes the two neutral ports. Existing source composition using PhysicalCatalog
and PostgreSqlRecordReader remains valid. No forwarding wrapper or duplicate host is retained.

## Responsibility and extension boundaries

The core owns portable request parsing, active registration, independent operation/field/row authorization,
projection exposure, typed/public key validation, AES-GCM references/cursors, logical paging and final output.
It contains no PostgreSQL references, nativeType decisions, SQL syntax or ADO.NET types.
Token payload, AEAD bindings, nonce/key generation and the 128-character boundary are preserved.
The seven-component constraint stays with the host's token capacity, not the generic SQL layer.

Prepare inspects resolved metadata only, before parsing key/cursor inputs, preserving rejection precedence.
The generic reader validates shape, codecs, nullability, integer PK facts and authorized integer restrictions.
Engine-specific capacity checks belong to protected hooks. PreparedRead exposes immutable neutral fields,
key descriptors and canonical integer equalities; the SQL subclass privately binds it to the original immutable
physical snapshot and owning reader. A plan prepared by another reader instance is rejected.

The generic SQL class owns the single SELECT builder, WHERE equality conjunction, complete PK predicate,
ORDER BY physical numeric columns, prepared parameters, bounded result loop, exact codec checks, null/identity
checks, transaction/resource lifecycle and safe failure conversion. Immutable rows never contain public tokens.
LIST fetches at most effective limit + one; GET at most two. The common loop additionally rejects an engine
specialization returning more than the bound instead of buffering/publishing an unbounded result.

Identifiers originate exclusively in trusted metadata. Quote doubles the configured closing delimiter separately
for each identifier. Values and take limits are parameters. No QuoteString utility or data-literal interpolation
is introduced. The local timeout literal remains a validated internal positive integer.

| Member | Base implementation | PostgreSQL specialization |
| --- | --- | --- |
| IdentifierStart / IdentifierEnd | protected init properties, double quotes | inherited unchanged |
| FromModifier | protected init property, empty | constructor initializes ONLY |
| Prepare / Open | C# non-virtual entry points | inherited, not overridden |
| BuildSelect / Decode | private, single implementation | unavailable for replacement |
| Quote / Add | common non-virtual escaping/parameter binding | inherited |
| LimitClause | virtual standard FETCH candidate | LIMIT @take, current tested syntax |
| KeyComparison | virtual complete tuple = or > | inherited unchanged |
| ValueExpression | virtual raw trusted column | BOOLEAN native, other values ::text |
| PortableValue | virtual exact strings/bools/integers/decimal/DateOnly, no float | inherited |
| SupportsColumn | virtual demonstrated scalar mapping | further rejects fixed CHAR padding |
| BeginTransaction | virtual ReadCommitted transaction | inherited |
| InitializeSession | virtual READ ONLY setup | existing READ ONLY, ISO DateStyle and lock timeout |
| AcquireReadProtection | abstract; no optimistic portable DDL default | ACCESS SHARE + ordinary/no-inheritance check |
| ObservePhysical | abstract, original snapshot revalidation | existing PostgreSqlObservationProvider |
| CommitTransaction | private, mandatory transaction.Commit | cannot be overridden |
| Attempt.Complete / Dispose | private sealed attempt class | cannot be replaced by a derived reader |

The class is abstract because no generic engine-independent DDL guard can be claimed.
The base FETCH default does not claim support for an untested engine: PostgreSQL continues to override LIMIT.
The protected conversion/syntax hooks are trusted implementation extension points, not a sandbox for hostile
provider code. They cannot replace the public pipeline or its finalization, result bounding and core publication.

No speculative MySQL/MariaDB/SQL Server/Oracle classes or intermediate hierarchies are added. A test-only descendant
of PostgreSqlRecordReader with no overrides proves that an otherwise sufficient inherited implementation works.
Intermediate classes can later inherit the same hooks without reproducing the read pipeline.

## Protected execution and publication

1. Core validates envelope and initial access, resolves the existing catalog and projection.
2. Reader Prepare validates storage/read capability without row I/O. Core binds generation atomically through
   the same catalog fence, validates inputs and resolves protected evidence.
3. Open (not overridable) opens a fresh connection/transaction, acquires DDL protection, reobserves and compares
   the physical snapshot, builds parameterized SQL, checks current access and fully decodes a bounded batch.
4. Open closes the data reader/command, then acquires a second independent protection transaction while the data
   transaction still holds its original lock. The returned attempt owns both connections/transactions.
   No business output has escaped. Failure to acquire this overlapping guard aborts the attempt.
5. Core reauthorizes operation, fields, row restrictions, policy, active registration and epoch, under its gate;
   PhysicalCatalog.PublishRead checks exact snapshot identity under the original catalog gate.
6. Core materializes the complete success envelope privately and calls Attempt.Complete while both fences hold.
   Complete calls the mandatory DATA transaction commit once. The independent guard remains locked.
   A failure produces READ_FAILED without output.
7. Core rechecks authorization/epoch/registration and snapshot after completion. This also catches reentrant
   driver/application callbacks that change policy or invalidate during completion despite reentrant monitors.
8. Only a successful final fence accepts the result for publication. Dispose attempts data-resource cleanup,
   explicitly rolls back the read-only guard, and disposes its transaction/connection. Every mandatory cleanup failure
   prevents successful return. The guard therefore outlives data commit and the final publication decision.

Unknown-reference GET still uses the final publication fence without opening a row read.
Lock order remains host gate -> catalog gate. SQL locking/introspection and decoding run outside those gates.
Incompatible external DDL can proceed only after the independent guard releases its lock, after the publication
decision. The guarantee is linearizable acceptance under the final fences, not preventing changes after acceptance
or keeping a schema/data snapshot alive until caller processing or network delivery.

## Verification

Final runner:

```sh
python3 providers/integration-docker.py \
    --contract "$HOME/Desarrollo/tmp/arp-contract-php-config-001-validation" \
    --listget-contract "$HOME/Desarrollo/tmp/arp-java-listget-001-contract" \
    --output "$HOME/Desarrollo/tmp/arp-sql-refactor-dotnet-001-final-review"
```

Results:

- 463 xUnit tests: the original 458 plus five neutral-core cases (assembly boundary and four completion outcomes).
- 25 PostgreSQL LIST/GET scenarios: original 21, completion/close failures, inheritance, post-commit DDL and
  queued-DDL guard handoff.
- 14 existing native PostgreSQL scenarios and 16 physical-storage seed scenarios.
- Recovery in a replacement container, same physical volume, zero observations.
- 212 recorded public responses validated against the pinned LISTGET result schema.
- All original 182 recorded responses equal the previous baseline after normalizing only random reference/cursor
  strings; JSON object member order is nonsemantic. No fields, values, statuses or diagnostics are ignored.
- The entire 88-vector coverage JSON is unchanged: N=24, E=58, U=6.
- dotnet format whitespace checked in each affected project; git diff --check passes.

Both simple and composite layouts still prove SQL restrictions before limiting, complete numeric keyset order,
exact INT64/DECIMAL beyond CLR decimal precision, nulls, Unicode, dates, historical rating 11, exact projections,
independent grants, token binding and invalidation, and fail-closed metadata/provider/codec errors.

The concurrent SELECT/DDL tests still observe SQLSTATE 55P03 for incompatible DDL while ACCESS SHARE is held,
invalidate on another thread without stale publication, and rebuild after successful later DDL.
New transaction tests inject commit failures on real LIST, existing GET and empty SQL pages: no success envelope,
and all three uncommitted transactions are disposed/rolled back. Successful retry proves resource recovery.
They also inject connection-disposal failure after a real successful commit and reentrant invalidation/permission
revocation during commit completion. The review additionally injects failure of mandatory guard rollback.
Every failed path has no output.
The exception injections wrap only owned test connections; no fake successful row database substitutes for PostgreSQL.

The initial validation run caught a test-reflection issue: implicit interface implementation methods may be
virtual final in CLR metadata despite being non-overridable in C#. The assertion now accepts nonvirtual or final.
No native read behavior was changed to make that assertion pass.

## Coverage and remaining limits

The original [88-vector matrix](listget-001-review.md) remains historical and unchanged. The new final
vector-coverage.json is identical; added completion tests do not promote any pending vector.

The six obligations remain: externally supplied stale catalog input, impossible NULL injection, duplicate-provider
identity injection, invalid server registration limits outside the neutral envelope, and two general module
dependency validations. Faulty-provider NULL/duplicate injection remains unexecuted; module validation remains
an application integration responsibility. No full 88-vector conformance is claimed.

Only PostgreSQL 18.6 is demonstrated. Base defaults and typed driver conversions are not claims of support for
other engines/drivers. Future overrides must prove lossless values, parameter binding, key ordering and DDL protection.
Current limits on ordinary storage, integer predicates, fixed CHAR and supported codecs are preserved.
There is no new result/token cache, generation protocol, HTTP host, ORM, permission system or DDL watcher.

Potential integration risk: moving the host and public neutral records to the core assembly requires rebuilding
binary consumers. Optional SQL/engine projects now have one-way references; lockfile changes contain project
edges only and preserve all NuGet package versions/hashes.

## Evidence and diff

The original refactor evidence remains at ~/Desarrollo/tmp/arp-sql-refactor-dotnet-001-final-review/.
The superseding concurrency-review evidence is ~/Desarrollo/tmp/arp-sql-refactor-dotnet-001-concurrency-review/:

- dotnet.log, dotnet-isolation.json, source-sha256.json.
- listget-responses.json, listget-summary.json, vector-coverage.json.
- schema-validation.json, response-equivalence.json, contract-integrity.json.
- final.diff and diff-stat.txt (complete branch changes); review-only.diff (changes since this review began).

The runner uses PostgreSQL 18.6 pinned by digest, nonce-guarded owned resources, read-only contract mounts and
build copies under ~/Desarrollo/tmp. Preexisting container/volume state and original logical YAML are preserved.
Both contract pins and historical fixture/test sources remain unchanged. No fixtures or Docker results enter the repo.
All auxiliary formatting/build copies live outside Alxarafe. No worktrees, commits, push, PR, merge or releases.


## Final architecture/concurrency review

### Real race, reproduced and corrected

Before this review, SqlRecordReader.Attempt.Complete committed the only transaction retaining ACCESS SHARE.
An external ALTER TABLE could complete after this commit and before ListGetHost.Publish performed its final checks.
PhysicalCatalog.Generation only tracks explicit application invalidation; external DDL does not increment it.
Consequently those local checks alone did not detect that race. Passing the previous tests did not establish safety.

PublicationDdl reproduces the interval deterministically: a test driver callback runs after the server has committed
the DATA transaction, opens an independent owned connection, and attempts ALTER TABLE ADD COLUMN with a 150ms
lock timeout. It does not invoke Invalidate. Before the fix, the regression fails because DDL crosses the intended
guard. Evidence: ~/Desarrollo/tmp/arp-sql-refactor-dotnet-001-ddl-red/dotnet.log.
After the fix, six attempts (simple/composite PK x LIST/GET/empty page) receive SQLSTATE 55P03. The same DDL succeeds
after each invocation completes and disposes its guard; invalidation/rebuild then succeeds.

The correction is in SqlRecordReader.Open and its existing sealed Attempt, without a new public interface or
engine hook: acquire an overlapping protection transaction before the data transaction can release its lock.
Complete commits only data. Dispose explicitly rolls back the guard after the host's publication decision.
Rollback is mandatory and checked, rather than relying solely on a driver Dispose that might suppress close errors.
Both guard and data resources are attempted even when another cleanup fails. Core ports document this lifetime.

### Exact lock/authorization/publication order

1. Host gate: validate target/operation and initial complete field grant, capture access epoch; release host gate.
2. Catalog Resolve takes resolveGate, briefly catalog gate/current publication checks, performs provider I/O without
   host/catalog gates, publishes the immutable CDS result under its existing publication gate, releases resolveGate.
3. Prepare is metadata-only. PublishRead briefly takes catalog gate and the publication getter gate, binds the
   exact snapshot to Generation, then releases them. No additional generation counter was introduced.
4. Open calls current: host gate -> catalog gate -> short publication getter; all released before SQL I/O.
5. Data connection starts ReadCommitted/READ ONLY, sets ISO DateStyle/timeouts and obtains ACCESS SHARE.
   Ordinary-table/no-inheritance facts are checked. ObservePhysical uses the existing observer on another fresh
   connection; its own transaction/lock ends on observation commit, while the DATA lock remains held.
6. Compare observation, build prepared SQL, call current again briefly, execute SELECT and decode the bounded batch.
   No host/catalog monitors are retained during database waits, query execution or decoding.
7. While DATA still holds ACCESS SHARE, acquire a separate read-only GUARD transaction with the same engine
   protection. If DDL is already queued ahead of it, timeout/error aborts safely; no output is published.
8. Host gate: final registration, operation/field/row-policy/context/epoch checks. Catalog gate: exact snapshot
   check through PublishRead. The publication object's own getter monitor is held only briefly, not across I/O.
9. Materialize the whole response privately. Commit DATA under host+catalog fences. Its SQL lock ends, but GUARD
   still holds ACCESS SHARE. AfterCommit callbacks therefore cannot perform incompatible DDL before publication.
10. Recheck authorization/epoch/registration and exact snapshot while GUARD is alive. This catches reentrant local
    invalidation or policy mutation during commit; another thread's explicit invalidation must wait for catalog gate.
    The successful final fence is the publication/acceptance linearization point.
11. Release catalog then host gates. The using scope disposes data resources, explicitly rolls back GUARD, and
    disposes both guard resources. Only after mandatory cleanup succeeds can Invoke return the success envelope.
    DDL may legitimately run after guard release: acceptance already occurred. No network-delivery atomicity is claimed.

If explicit invalidation wins before step 8, publication rejects the snapshot. If it runs after step 10, it is later
than this read's linearization point. Reentrant mutation inside commit is detected at step 10 rather than hidden
by the reentrant Monitor. Unknown-reference GET still performs the local final fence without SQL rows or a guard.

### Scope of DDL protection and remaining concurrency costs

ACCESS SHARE conflicts with ACCESS EXCLUSIVE; it does not freeze every PostgreSQL catalog mutation. This review
demonstrates incompatible table-layout/identity DDL such as the ALTER TABLE in the regression. Index maintenance,
statistics and other nonconflicting catalog changes are not a newly promised global metadata freeze. A later
reobservation can detect changed physical descriptors; application invalidation/rebuild remains required.
A whole-database DDL/permission watcher or stronger global locking is outside this profile.

The guard costs one additional connection acquisition/transaction per executed read. The overlapping phase needs
two fresh connections; the previous observer path already needed two concurrently. Pending DDL can make the handoff
wait behind an exclusive-lock request even though ACCESS SHARE locks are mutually compatible. QueuedDdl waits until
pg_stat_activity confirms the owned DDL is queued, then proves the one-second read guard timeout returns READ_FAILED,
releases the original locks, lets DDL finish and permits rebuild/retry. This is a bounded availability cost, not
an undetected structural change or an unbounded lock-order deadlock.

The guarantee assumes the normal demonstrated PostgreSQL lock/transaction behavior. It is not distributed
consensus, cross-request snapshot isolation, protection against all administrative actions, or lifetime guarantees
after response acceptance.

### Observation integration: common versus engine-specific

SqlRecordReader.Open invokes AcquireReadProtection, then ObservePhysical, and performs the physical equality
comparison itself before building/executing rows. The same protection hook establishes the post-commit guard.
Thus observation participates in the actual common pipeline; it is not just an unused extension point.

Common code is snapshot selection, provider registration and generation publication in PhysicalCatalog; and
protected observation orchestration, snapshot comparison, error handling and owned-resource cleanup in the SQL base.
PostgreSqlRecordReader.ObservePhysical constructs the existing PostgreSqlObservationProvider with the trusted source.
Its version check, pg_catalog query/resource, native facts and standalone observation transaction are PostgreSQL code.

A new engine needs one concrete IPhysicalObservationProvider and its read-reader overrides. It is not required to
inherit a PostgreSQL observer or create another complete observer hierarchy. The catalog accepts the existing
interface and the reader hook returns the existing neutral JsonElement. Extracting a second generic observer base
to deduplicate standalone session setup is an optional later improvement, not a prerequisite or current defect.

### Generic PhysicalCatalog relocation

PhysicalCatalog uses only the existing dictionary, CDS resolver/publication, immutable definitions and registered
IPhysicalObservationProvider instances. Its only optional invalidation dependency is IInvalidatablePhysicalProvider;
it contains no PostgreSQL SQL, connection type or engine-specific conditions.

It and its existing interfaces/PhysicalObservationException now live in the core Cds/Physical directory, preserving
their namespace, public contracts, lock bodies, resolver behavior and failure semantics. The invalidation interface
was moved out of PhysicalYamlStore; no replacement interface/class/project was added. The core assembly dependency
test checks that the catalog and both provider interfaces belong to the same neutral assembly as ListGetHost.
PhysicalYamlStore and its optional persistence implementation remain in the provider library; moving that packaging
later is optional and not needed for an independent engine's catalog/observer integration.

### Conceptual extension simulation (no new engines implemented)

| Point | MariaDbRecordReader | SqlServerRecordReader |
| --- | --- | --- |
| Identifier properties | backtick start/end | bracket start/end or demonstrated quoted identifiers |
| FromModifier | inherited empty, never PostgreSQL ONLY | inherited empty |
| LimitClause | parameterized LIMIT | OFFSET 0 ROWS FETCH NEXT parameter ROWS ONLY; keyset still supplies position |
| KeyComparison | tuple comparison candidate, needs native driver tests | expand complete PK into equality conjunction / lexicographic OR-of-AND |
| ValueExpression / PortableValue | exact DECIMAL/Int64/date and explicit logical BOOLEAN mapping | exact DECIMAL beyond CLR decimal, native bit/date conversion |
| SupportsColumn | actual native facts and read-capability checks | actual native facts and read-capability checks |
| InitializeSession / BeginTransaction | demonstrated read-only/session timeout behavior | override unsupported READ ONLY SQL; verify driver/session semantics |
| AcquireReadProtection | MDL retained by both data/guard transactions | a demonstrated lock held through guard transaction, not only statement Sch-S |
| ObservePhysical | concrete MariaDB provider through existing interface | concrete sys-catalog provider through existing interface |

The SQL Server OFFSET 0 would only satisfy T-SQL syntax: it does not implement positional pagination or accept
client offsets; the WHERE keyset remains authoritative. A strict policy banning even this token would require a
small SELECT-prefix/TOP extension, because the current limit hook is suffix-only. That is an optional future
syntax extension, not a reason to replace the whole SELECT pipeline now.
The base's current @pN/DbType binding must be demonstrated with each chosen .NET driver; it is not universal ADO.NET.
No new engine can claim support until its type conversions, metadata visibility and persistent DDL guard are proven.
There is no identified need to replace authorization, projection, row bounding, commit or host publication.

Primary references supporting these boundaries:
[PostgreSQL locks](https://www.postgresql.org/docs/18/explicit-locking.html),
[MariaDB MDL](https://mariadb.com/docs/server/reference/sql-statements/transactions/metadata-locking),
[SQL Server OFFSET/FETCH](https://learn.microsoft.com/en-us/sql/t-sql/queries/select-order-by-clause-transact-sql),
[SQL Server schema locking](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide).

### Review outcome

Real fixes: post-data-commit DDL gap and catalog placement that otherwise required a PostgreSQL assembly reference.
Preserved behavior: initial/final authorization precedence, exact codecs/tokens, ordinary PostgreSQL source profile,
the 88-vector classification, physical persistence/recovery and mandatory transaction failure without output.
Optional improvements: a standalone shared observer base, generic persistence packaging, alternative engine syntax
hooks when a real engine needs them, and broader administrative/catalog coherence.

The original working tree was backed up at ~/Desarrollo/tmp/arp-sql-refactor-dotnet-001-review-before/ before edits.
No pending work was reset or discarded. No branch switch, commit, push, PR, merge, tag or release in this review.
