# Contract review observations from .NET

Authority: ARP-DICT-001 at `0172ab64742218b9f64a6ae96c43ebdfe690f4c6`.
No shared expectation was changed or skipped. No contract, PHP or Java file was
modified. The observations below are proposals for joint review, not new common
rules or evidence that PHP/Java outputs define .NET behavior.

## Actual parser differences outside the current shared fixtures

Eight independent probes were run with PHP and Java mounted read-only and the
.NET implementation under review. Complete inputs are in
[review-probes/inputs.json](review-probes/inputs.json); actual validity/error codes
and exact PHP/Java refs are in [review-probes/observed.json](review-probes/observed.json).
The probes are diagnostic observations without authored expected outcomes and
are not counted among the 298 shared conformance assertions.

| Concrete input | PHP | Java | .NET |
| --- | --- | --- | --- |
| YAML `description:` using literal block text | DOCUMENT_INVALID | Accepted | Accepted |
| YAML unquoted label `2026-11-14` | DOCUMENT_INVALID | Accepted as text | Accepted as text |
| YAML multiline quoted text with continuation at column zero | DOCUMENT_INVALID | Accepted | DOCUMENT_INVALID |
| Numeric length metadata `1.00` | Accepted | Accepted | Accepted |
| Numeric length metadata `1e10000` | LIMIT_EXCEEDED | Accepted | Accepted |
| Numeric length metadata `1e2147483648` | LIMIT_EXCEEDED | Accepted | Accepted |
| Numeric length metadata `1e2147483649` | LIMIT_EXCEEDED | DOCUMENT_INVALID | Accepted |
| Numeric length metadata `1e9999999999999999999999999999` | LIMIT_EXCEEDED | DOCUMENT_INVALID | Accepted |

The numerical examples are JSON numbers in `maximumLength` of a TEXT descriptor,
not DECIMAL scalar inputs. Scalar DECIMAL exponent notation remains invalid in
all contractual validation: source metadata and portable value grammar are distinct.
.NET retains raw JSON number tokens and compares coefficient/exponent magnitudes
without expansion. These examples did not cause floating-point conversion or
large powers of ten to be materialized in the .NET metadata path.

## Proposals for the common contract

1. **Minimum YAML subset:** select a common minimum resolution schema and scalar
   styles. Add positive and negative vectors for block text, dates as plain strings,
   continuation indentation and quoted symbols resembling tags/directives.
   YamlDotNet inserts implicit default `!` / `!!` handles in DocumentStart; those
   are not source-declared tags. The adapter rejects actual tag syntax and raw
   directives while preserving literal text. The unindented quoted continuation
   limitation is a disclosed YamlDotNet intake restriction, not a .NET scalar rule.
2. **Exact metadata and equivalent numeric spellings:** add `1`, `1.00`, `0.1e1`,
   huge integers, very large positive exponents, fractional negative exponents and
   zero with a huge exponent. Decide the supported common metadata domain explicitly;
   do not inherit float precision, PHP expansion limits or Java BigDecimal scale
   bounds accidentally. Preserve JSON Number kind on descriptor export.
3. **Processing budgets:** define depth and exponent/digit processing budgets and
   their error families beyond the existing 1 MiB/2048-element baseline. .NET exposes
   a configurable 128-level depth budget and returns LIMIT_EXCEEDED before recursive
   node conversion. It adds no arbitrary exponent ceiling to otherwise exact metadata.
4. **Source security and Unicode:** add duplicate-null/escaped mapping keys, unknown
   directives, malformed UTF-8, unpaired UTF-16 escapes and literal U+FFFD as distinct
   cases. Some native APIs normalize or reject invalid surrogate access at different
   stages; a raw-token check avoids silent replacement. Raw source intake errors are
   DOCUMENT_INVALID; invalid native portable string values are VALUE_TYPE.
5. **Temporal edges:** add positive and negative UTC boundaries at years 0001/9999,
   seven-digit tick extrema, ±14:00 crossings, invalid offsets and host-culture
   changes. Exact tick arithmetic must remain independent of system timezones.
6. **Numeric domain wording:** clarify numeric bounds relative to optional DECIMAL
   geometry. The `ordered-constraints` vector already establishes that the minimum
   bound outside the chosen geometry is not automatically a definition error.
   No stack should add an unrequested general satisfiability solver.

No amendment was made automatically. Passing the supplied fixtures proves this
candidate's tested outcomes; it does not settle these untested common boundaries
or imply a published contract release exists.
