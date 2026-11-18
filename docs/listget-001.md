# ARP-DOTNET-LISTGET-001: native PostgreSQL reads

Branch: `feature/arp-dotnet-listget-001`, based on updated `origin/main`
`e7e73a8b8df70cb19240dc16cbf9c3cda9e43350`. Initial working tree was clean.
No commits or publications are part of this delivery.

## Integration and boundaries

`Alxarafe.Arp.Cds.Reads.ListGetHost.Invoke(ReadOnlyMemory<byte>, ReadContext)` is the application invocation
boundary. It uses the existing portable JSON parser (unique members, valid Unicode, exact numbers, 1 MiB,
depth 64), CDS resolver and scalar validator. It has no HTTP endpoint, ORM or general permission engine.

The host receives an explicitly registered immutable `ReadTarget` for each active projection and an authorizer
returning immutable `ReadAccess`. Operation and complete field authorization precede metadata resolution.
`Equalities = null` means an unsupported policy; an empty map explicitly permits all rows.
Only equality on physically demonstrated integer columns is executed. Other predicates must never be represented
as an empty map. Context identity must bind every principal, tenant and scope; policy revision is authoritative.
Every policy/composition mutation, including ABA, must pass through `ChangeAccess`.
The application validates module dependencies before publishing its registry. Removing catalog also removes reports.

`PostgreSqlRecordReader` uses fresh closed `DbConnection` objects and `DbCommand`; only the test host depends
on Npgsql. Registered/resolved identifiers are quoted and values, positions, complete keys and limits parameterized.
LIST orders by the **physical numeric columns**, not by their textual output expressions. It uses complete PK
tuple comparison and at most effective limit + one rows. GET uses the complete PK and at most two rows.
Restrictions precede ORDER BY/LIMIT. There is no OFFSET, row post-filtering, total count or result cache.

Only selected projection columns and the complete internal PK are read. PostgreSQL numeric values are converted
to exact text by the server, avoiding the precision bounds of CLR decimal and any floating-point intermediate.
The shared DICT codec canonicalizes integers/DECIMAL, preserves Unicode, null, BOOLEAN and DATE, and validates type
representability without current write constraints. Historical rating 11 remains readable. An invalid selected
value, hidden key, provider failure or duplicate identity discards the entire response, including lookahead rows.
The observer adds numeric/DATE built-in facts under its existing namespace/domain/CHECK guards. Older persisted
observations lacking these facts must be invalidated/rebuilt; absence of capability fails closed.
This does not claim verified DECIMAL editing-domain compatibility.

If any PK component is hidden, AES-256-GCM encrypts the entire key with a fresh 96-bit nonce and an instance-private
random 256-bit key. Associated data binds purpose, target, projection, full identity, policy revision and access
epoch. The encrypted payload binds catalog generation, effective limit and all signed 64-bit key components.
Reference/cursor capacity is calculated from nonce (12), tag (16), generation (8), limit (4) and PK components
(8 each): seven components produce 96 bytes / 128 base64url characters; eight cannot fit. The boundary is tested.
GET rejects empty or >128-character references as INVALID_KEY before decryption. Unknown, tampered or differently
bound references within that size return found:false without selecting rows. Host recreation revokes its tokens.
No token registry or parallel generation cache is added.

`PhysicalCatalog.PublishRead` fences the exact immutable metadata snapshot against invalidation.
`CdsReadPublication.Generation` exposes the existing generation without replacing its protocol.
The lock order is host gate, then catalog gate. Database work and metadata resolution happen outside the host gate.
The read transaction retains ACCESS SHARE across metadata reobservation, SELECT, decoding, final authorization,
response construction and commit. Commit and publication occur under the final gates; commit failure has no output.
External incompatible DDL is blocked while a read is in flight. Drift between requests fails closed and requires
application invalidation/rebuild. There is no external DDL watcher or distributed policy/generation coordinator.
No lasting data snapshot is promised between separate requests.

## Run the PostgreSQL 18.6 evidence

The historical `contract.ref` remains `7a62bba0e8f84dbc198e837baf63fc7e0d8722e1`.
Independent `listget-contract.ref` pins `eb38cb96e6ccd218734df2a181908de46067e0c7`.
Clean existing contract checkouts are mounted read-only; products/definitions/vectors are read directly from them.
The Java merge `03452cd75ce5ee79215881686b3601edcffbdc7a` was consulted as a functional reference,
including the GET length correction. The pinned shared contract is the conformance oracle.

```sh
python3 providers/integration-docker.py \
    --contract "$HOME/Desarrollo/tmp/arp-contract-php-config-001-validation" \
    --listget-contract "$HOME/Desarrollo/tmp/arp-java-listget-001-contract" \
    --output "$HOME/Desarrollo/tmp/arp-dotnet-listget-001-final-review"
```

The runner uses its pinned PostgreSQL 18.6 image, run-owned resources and pre-DDL nonce. Build copies/results
live under ~/Desarrollo/tmp. No fixtures, generated physical descriptors or Docker results enter the source tree.
It destroys its first runner container and recovers the same physical YAML in a replacement with zero observations.
Inventory preservation checks cover all preexisting containers/volumes. They are isolation evidence, not a checksum
or inspection of unrelated project data. Original logical YAML is hashed and unchanged.
The runner records source SHA-256 hashes and verifies whitespace in each affected C# project.

See [final review and all 88 vectors](listget-001-review.md). This is a bounded working implementation,
**not full conformance to all 88 draft vectors**. Unsupported codecs/policies, nonordinary storage, CHAR padding
and unrepresentable keys fail closed. General module composition remains an application integration obligation.
