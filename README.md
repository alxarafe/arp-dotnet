# ARP .NET dictionary and CDS read metadata

Independent **.NET 10 / C#** class library implementing the approved ARP-DICT-001
candidate, format `arp.dictionary/v1`. All 17 scalar types are supported.
No ASP.NET, Entity Framework, database, HTTP, UI or ERP framework is required.

The semantic authority is `alxarafe/arp-contract`, pinned in `contract.ref` to
`7a62bba0e8f84dbc198e837baf63fc7e0d8722e1` for scalar dictionary and CDS read metadata.
The original scalar RC tag remains `v0.1.0-rc.1` at `4c0f4bd0337c01c15a036910b6462e6f0e674927`.
No final normative release is implied. Other runtimes are never test expectations.

## Public API

```csharp
using Alxarafe.Arp.Dictionary;

var source = SourceSnapshot.FromUtf8("application", "dictionary:finance", SourceFormat.Json, """
    {"apiVersion":"arp.dictionary/v1","elements":[
      {"id":"finance.amount","type":"DECIMAL","constraints":{"precision":18,"scale":2}}
    ]}
    """);

BuildResult build = new CatalogBuilder().BuildCatalog([source]);
if (!build.IsValid)
{
    // build.Issues includes typed errors and trusted diagnostic provenance.
    // build.Catalog is null; no partial snapshot is exposed.
    return;
}
Catalog catalog = build.Catalog!;
ResolutionResult found = catalog.Resolve("finance.amount");
ElementDescriptor descriptor = found.Descriptor!;
ValidationResult result = catalog.Validate("finance.amount", "1.2300");
// result.IsValid == true; result.NormalizedValue is "1.23"
```

The namespace is `Alxarafe.Arp.Dictionary`:

- `SourceSnapshot` owns a defensive copy of source bytes and explicit diagnostic
  provider/source identities. `SourceFormat` selects JSON or YAML. `FromUtf8`
  encodes valid native text strictly; the byte constructor accepts malformed
  snapshots so the builder can return contractual intake errors.
- `CatalogBuilder.BuildCatalog(IEnumerable<SourceSnapshot>)` returns an immutable
  `BuildResult`. Duplicate IDs and invalid sources reject the entire new build.
- `Catalog.Resolve(string)` returns a typed `ResolutionResult`, including a
  descriptor or `ElementNotFound` issue. IDs are case-sensitive and exact.
- `Catalog.Validate(string, object?)` uses the neutral codec: native Boolean for
  BOOLEAN; native String for every other type, including numbers. There are no
  truthiness, numeric-kind, whitespace or locale coercions.
- `ValidationResult.ToProjection()` emits `valid`, ordered `issues`, and
  `normalizedValue` **only on success**. Normalized values are Boolean or String.
- `ElementDescriptor` contains immutable constraints, ordered ENUM literals and
  optional label/description. `ToProjection()` / `ToJson()` expose the complete
  contractual descriptor, with absent optional properties remaining absent.
- `Catalog.ElementIds` is an immutable ordinal-sorted list;
  `GetProvenance(id)` exposes diagnostics separately. `DictionaryIssue` supplies a
  typed error enum and the exact `ContractCode`; `ToProjection()` excludes provenance.

Constraints retain cloned, read-only `JsonElement` values. Numeric metadata stays
JSON Number and preserves its exact source lexeme, including huge integers,
integral decimal spellings and exponent notation. Export never passes through
`double`, `float` or native `decimal`.

Catalogs share no mutable registry or cache. The application owns contexts and
publishes a replacement only after a successful build, for example with
`Interlocked.Exchange(ref activeCatalog, build.Catalog)`. Readers retain their
selected immutable Catalog for a whole operation. Failure cannot mutate an older
snapshot or expose the new build's partial definitions.

## Exactness, parsing and safety

`ExactNumber` uses a BigInteger coefficient and BigInteger decimal exponent.
Source numbers are checked before conversion: at most 256 token characters,
absolute explicit exponent at most 128, and absolute magnitude below 10^128.
Magnitude comparison never expands exponents. Portable scalar numeric lexemes have the
contractual 4096-digit ceiling and are normalized without rounding. Divisibility
uses exact BigInteger arithmetic. Host-sized numeric types are only used for
bounded lengths, counters and calendar/tick operations.

JSON uses System.Text.Json with strict UTF-8 decoding, a token pass rejecting
all duplicate keys and invalid escaped surrogate sequences, and explicit depth
checks before DOM processing. Invalid source Unicode is not replaced by U+FFFD.

The only direct runtime NuGet dependency is **YamlDotNet 16.3.0**. Its events are
read without using object deserialization or custom constructors. Native scalar
resolution implements a disclosed YAML 1.2 JSON-compatible subset: only plain
`null`, `true`, `false` and JSON numeric lexemes get non-string kinds. Quoted
scalars remain strings; `yes/no/on/off`, dates, literal and folded text remain
text. Block and flow collections are supported. Tags, anchors, aliases, merge
keys, directives (including unknown directives), multiple documents and duplicate
mapping keys are rejected. Implicit built-in tag handles inserted by YamlDotNet
are distinguished from source-declared tags. All multiline plain and quoted
continuations are rejected; use literal or folded blocks with optional chomping.
Double-quoted escapes follow JSON; numeric block indentation indicators are rejected.

A dedicated portable-pattern parser and bounded position matcher enforce the ARP
grammar without exposing .NET regex extensions. Native scalar lexical recognizers
use fixed, non-backtracking expressions. Unicode lengths count scalar values;
invalid UTF-16 is rejected. Temporal normalization uses strict Gregorian dates,
explicit offset/tick arithmetic and seven fractional digits without consulting
local timezones. Base64 must decode and re-encode identically.

Default `CatalogLimits`: **1 MiB/source**, **2048 elements/source**, **128 collection
levels/source**. All are configurable. The contract's numeric/pattern ceilings
are fixed independently. Only known input rejections become dictionary issues;
resource failures and programming exceptions are not disguised as invalid values.

## Build and verification

Use a .NET 10 SDK. `global.json` selects stable 10.0 SDK feature bands. Dependency
versions and NuGet content hashes are recorded in both `packages.lock.json` files.
No NuGet package is published or produced as part of the verification commands.

```sh
dotnet restore Arp.slnx --locked-mode
dotnet build Arp.slnx --no-restore -c Release
ARP_CONTRACT_PATH=/path/to/pinned/arp-contract \
  dotnet test Arp.slnx --no-build --no-restore -c Release \
  --logger 'trx;LogFileName=acceptance.trx' --results-directory artifacts
python3 scripts/summarize-results.py
bash scripts/check-contract.sh /path/to/pinned/arp-contract
```

The test project is independent of the library and uses xUnit. It reads the
original manifest and fixtures directly, verifies the exact commit and clean
tracked inputs, and preserves the deliberately invalid-surrogate fixture at the
native validation boundary. Cases of the catalog scenarios reconstruct their
prefix independently, so test ordering cannot alter the outcome. Unknown sets or
operations fail explicitly. No shared expectations are copied or regenerated.

`artifacts/conformance-summary.json` records each shared group's actual outcomes;
`acceptance.trx` records all individual xUnit results.
`summarize-results.py` cross-checks both sources, includes the runtime Fact inventory,
and fails for errors, omitted cases or inconsistent counters. Structural checks
run separately using the pinned contract's Python tools and requirements.

Where the host lacks dotnet, the existing local SDK image can be used:

```sh
docker run --rm --user "$(id -u):$(id -g)" \
  -e DOTNET_CLI_HOME=/home/build -e NUGET_PACKAGES=/nuget \
  -v "$HOME/Desarrollo/tmp/arp-dotnet-home:/home/build" \
  -v "$HOME/Desarrollo/tmp/arp-dotnet-nuget:/nuget" \
  -v "$PWD:/work" -w /work alxarafe-dotnet-app:latest \
  dotnet restore Arp.slnx --locked-mode
```

For builds and tests, add `--network none`, a read-only contract mount at
`/contract`, and `-e ARP_CONTRACT_PATH=/contract`; use `--no-restore` after the
locked restore. No application or database services are started.

See [architecture](docs/architecture.md), [RC1 results](docs/rc1-conformance.md),
and [concrete contract review observations](docs/contract-review.md).


## CDS read metadata

The pure resolver, generation publication API, exact shared scenarios and limits are documented in
[ARP-CDS-READ-002](docs/cds-read-002.md). This adds no HTTP, SQL, ORM or persistence dependency.

## Optional physical metadata provider

[ARP-CDS-READ-003](docs/cds-read-003.md) documents the independent real-database adapter, lazy generation,
explicit invalidation, optional physical YAML persistence/recovery, isolated Docker tests and the minimal
LIST/GET decisions still pending.
The pure dictionary/CDS resolver remains usable without SQL, HTTP, ORM or the adapter driver.

## Optional SQL engine integrations

`Arp.slnx` contains production libraries and ordinary unit/contract tests only.
Neither `dotnet build Arp.slnx` nor `dotnet test Arp.slnx` starts Docker, downloads
engines or opens database connections. With restored NuGet dependencies and the
local pinned contract checkout, both run with networking disabled.

Database regressions remain versioned separately under
`providers/tests/PostgreSql.Integration` and `providers/tests/SqlEngines.Integration`.
They require an explicit invocation; no integration project is included in the solution.
The new runner accepts `postgresql`, `mysql`, `mariadb`, `sqlserver` or `all`:

```sh
python3 providers/integration-sql-engines.py --engine mysql \
  --listget-contract "$HOME/Desarrollo/tmp/arp-java-listget-001-contract" \
  --output "$HOME/Desarrollo/tmp/arp-sql-mysql-review"

python3 providers/integration-sql-engines.py --engine all \
  --listget-contract "$HOME/Desarrollo/tmp/arp-java-listget-001-contract" \
  --output "$HOME/Desarrollo/tmp/arp-sql-all-review"
```

Use a new output directory for each invocation. Only this explicit integration
runner may pull its pinned images if absent. It creates an isolated network and
fresh database containers, publishes no ports and removes only its own labelled
containers and their temporary volumes. It never removes preexisting volumes.
SQL Server uses the Developer edition and requires acceptance of its container EULA
through the explicitly invoked runner. No database image or binary is versioned.

The original PostgreSQL observation/storage recovery regressions remain available:

```sh
python3 providers/integration-docker.py \
  --contract "$HOME/Desarrollo/tmp/arp-contract-php-config-001-validation" \
  --listget-contract "$HOME/Desarrollo/tmp/arp-java-listget-001-contract" \
  --output "$HOME/Desarrollo/tmp/arp-sql-postgresql-recovery-review"
```

Run the runners sequentially: their resource-preservation checks deliberately treat
all resources that predate an invocation as foreign. Results, tested combinations,
provider methods and limitations are recorded in
[the provider review](docs/sql-providers-dotnet-001.md).