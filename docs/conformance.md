# ARP-DICT-001-RUNTIME-002 acceptance evidence

Date: 2026-11-15. Branch: `feature/arp-dict-001-portability-002`.
Authoritative contract: `2dde1a09780e29c7385606c5e018fa75a0808b68`.
Main was clean and matched origin/main (`2061545`) after `git pull --ff-only`.
The requested branch already existed at `03bf12c`; it was preserved as
`backup/arp-dict-001-portability-002-03bf12c` before creating the requested branch
from updated main. The two starting commits had identical trees (the main commit
was the squash merge of the earlier implementation). The required runtime changes
were therefore already present; this run reviewed them and added focused regression
coverage instead of duplicating production changes.

## Actual results

| Group | Passed | Failed | Not executed |
| --- | ---: | ---: | ---: |
| Base catalog | 1 | 0 | 0 |
| Values | 136 | 0 | 0 |
| Definitions | 36 | 0 | 0 |
| Sources | 40 | 0 | 0 |
| Source descriptor projections | 4 | 0 | 0 |
| Patterns | 45 | 0 | 0 |
| Pattern samples | 21 | 0 | 0 |
| Catalog steps | 32 | 0 | 0 |
| Catalog descriptor projections | 12 | 0 | 0 |
| **Shared** | **327** | **0** | **0** |
| Independent .NET | 35 | 0 | 0 |
| **Total** | **362** | **0** | **0** |

The runner consumes original manifest/fixtures directly, checks the pinned Git
revision, and compares complete JSON projections with mathematical exact numeric
equality and distinct booleans. Expectations are neither copied nor regenerated.
[Machine-readable report](acceptance-summary.json) cross-checks the generated TRX,
shared counters and independent Fact inventory.

## Changes and regression coverage

Public API and snapshot architecture are preserved. Source parsing now checks
numeric token length (256), absolute explicit exponent (128), and absolute
magnitude (<10^128) before coefficient/exponent conversion. Zero coefficients,
padded exponents and unknown fields are checked too. Admitted metadata retain
JSON Number kind and exact authored value; scalar numeric strings keep their
existing rules and 4096-digit arithmetic coverage.

YamlDotNet remains an event parser, with explicit rejection of multiline plain or
quoted scalars, non-JSON double-quoted escapes, numeric block indentation headers
and unsupported physical line separators. Literal/folded blocks use the parser's
indentation and clip/strip/keep processing. Existing tag/anchor/alias/directive,
duplicate-key, UTF-8/UTF-16, depth and byte/element guards remain active.

The previous enormous-exponent acceptance and indented quoted-continuation tests
now explicitly assert the consolidated contract rejection. All previous runtime
coverage is retained, including concurrency, isolation, atomicity, defensive
copies, immutable snapshots, exact arithmetic, Unicode, dates and absence of
validation side effects. The consolidation Facts cover YAML chomping/escapes and numeric security limits
in YAML unknown fields. This verification adds a Fact for a compact sequence whose
first mapping field is a block scalar, followed by a plain date and exact numeric
metadata. It verifies decoded block content, retained date text, Number kinds and
mathematical projection equality. The existing event-based parser passes unchanged.

## Toolchain and reproduction

.NET SDK 10.0.403, runtime 10.0.14, MSBuild 18.9.15; existing image
`alxarafe-dotnet-app:latest`. YamlDotNet 16.3.0 and the locked test dependencies
are unchanged. Build treats warnings as errors. Final tests ran in Docker with
network disabled, read-only contract mount and a private local NuGet cache.

```sh
dotnet restore Arp.slnx --locked-mode
ARP_CONTRACT_PATH=/path/to/pinned/arp-contract dotnet test Arp.slnx \
  --no-restore -c Release --logger 'trx;LogFileName=acceptance.trx' \
  --results-directory artifacts
python3 scripts/summarize-results.py
```

Structural contract controls were separately rerun with `bash scripts/check-contract.sh ../arp-contract`: `check_contract.py` PASS, inventory 327; 27/27 Python
structural regression tests PASS. They are not counted as runtime conformity.
No shared or independent test remains unexecuted.

The historical [contract review](contract-review.md) and probe observations remain
unaltered evidence of the earlier contract; the consolidated profile resolves
their YAML/numeric/resource portability questions. No further contractual
contradiction was found in these checks. No other repository was modified, and
no push, PR, merge, tag or package publication was performed.

## Current rerun environment issues and resolution

The host has no `dotnet` executable. The existing SDK image was used. Its previous
NuGet cache was absent, so an initial offline restore failed with NU1301. A locked
restore with network access populated `/tmp/arp-dotnet-runtime002-nuget`; final
build and tests then ran with `--network none` and a read-only `/contract` mount.
The first test launch used the container's root user and Git rejected the differently
owned contract. The final run sets `GIT_CONFIG_COUNT=1`,
`GIT_CONFIG_KEY_0=safe.directory`, `GIT_CONFIG_VALUE_0=/contract` only in that
container process. It does not change global Git configuration or contract files.

The final commands, after the locked restore, were:

```sh
docker run --rm --network none \
  -v "$PWD:/work" -v "$PWD/../arp-contract:/contract:ro" \
  -v /tmp/arp-dotnet-runtime002-nuget:/nuget \
  -e NUGET_PACKAGES=/nuget -e ARP_CONTRACT_PATH=/contract \
  -e GIT_CONFIG_COUNT=1 -e GIT_CONFIG_KEY_0=safe.directory \
  -e GIT_CONFIG_VALUE_0=/contract -w /work alxarafe-dotnet-app:latest \
  dotnet test Arp.slnx --no-build --no-restore -c Release \
  --logger 'trx;LogFileName=acceptance.trx' --results-directory artifacts
python3 scripts/summarize-results.py
PYTHONDONTWRITEBYTECODE=1 bash scripts/check-contract.sh ../arp-contract
```

The preceding `dotnet test` invocation compiled Release successfully with warnings
as errors; the final invocation reused those same binaries. Final outcome:
327/327 shared, 35/35 native, zero skipped; schema/integrity PASS and 27/27
structural regressions PASS. No production API or dependency changed in this rerun.
