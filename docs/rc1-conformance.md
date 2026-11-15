# ARP dictionary v0.1.0-rc.1 verification

Date: 2026-11-15. Branch: `release/arp-dict-001-v0.1.0-rc1`.
Implementation baseline: `786632f3b04cd615a21db89d66942727152c7c8f`, clean main
updated with `git pull --ff-only` before creating the release branch.
Production implementation and all 35 independent tests are unchanged from that
commit. This commit only changes contract pins, runner safeguards and evidence.

## Verified authority

Remote: `https://github.com/alxarafe/arp-contract.git`.
Annotated tag: `v0.1.0-rc.1`.
Tag object: `192dc1ce0ceab1a2b2f4545a4e327cde2e0fc5d5`.
Peeled commit: `4c0f4bd0337c01c15a036910b6462e6f0e674927`.

`git ls-remote` returned both the tag object and peeled commit. A separate clone
in `/tmp/arp-dotnet-contract-rc1` confirmed `git cat-file -t` is `tag` and HEAD
is the required commit. The existing `arp-contract` checkout was not modified.
Fixtures and schema match the prior consolidated authority byte for byte.
The runtime and structural runners require the exact HEAD/pin, an annotated
local RC tag and its exact peeled SHA. Original fixtures and expectations remain
external and unchanged.

## Actual results

| Check | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Shared assertions | 327 | 0 | 0 |
| Independent .NET tests | 35 | 0 | 0 |
| Total native tests | 362 | 0 | 0 |
| Structural regressions | 27 | 0 | 0 |

Schema and fixture integrity: PASS, inventory 327. The shared execution includes
136 values, 36 definitions, 40 sources, four source descriptor projections,
45 patterns, 21 samples, 32 catalog steps, 12 catalog descriptor projections and
one base catalog. [Acceptance summary](acceptance-summary.json) cross-checks the
actual TRX results and shared execution counters.

The retained independent tests include concurrency, isolated catalogs, atomic
publication, defensive copies, immutable snapshots, byte/element/depth limits,
exact 4096-digit arithmetic, Unicode, YAML security and portable scalar resolution.
No functionality, public API, dependency, test expectation or schema was changed.

## Environment and reproduction

Ubuntu 24.04 linux-x64 container, existing image `alxarafe-dotnet-app:latest`
(image ID `ec920d55f11747c8b35949ccee66b792667f1fa4baca6e181162be6a03d4303b`),
.NET SDK 10.0.403, runtime 10.0.14, MSBuild 18.9.15. YamlDotNet 16.3.0 and
locked NuGet dependencies are unchanged. Host Python 3.14.4 runs contract tools.
Build uses Release and warnings as errors. Final build/test ran without network,
using the previously restored locked cache. No package was published.

From the implementation repository:

```sh
git ls-remote https://github.com/alxarafe/arp-contract.git 'refs/tags/v0.1.0-rc.1*'
git clone --depth 1 --branch v0.1.0-rc.1 \
  https://github.com/alxarafe/arp-contract.git /tmp/arp-dotnet-contract-rc1
git -C /tmp/arp-dotnet-contract-rc1 cat-file -t refs/tags/v0.1.0-rc.1
git -C /tmp/arp-dotnet-contract-rc1 rev-parse HEAD

docker run --rm --network none \
  -v "$PWD:/work" -v /tmp/arp-dotnet-contract-rc1:/contract:ro \
  -v /tmp/arp-dotnet-runtime002-nuget:/nuget \
  -e NUGET_PACKAGES=/nuget -e ARP_CONTRACT_PATH=/contract \
  -e GIT_CONFIG_COUNT=1 -e GIT_CONFIG_KEY_0=safe.directory \
  -e GIT_CONFIG_VALUE_0=/contract -w /work alxarafe-dotnet-app:latest \
  dotnet test Arp.slnx --no-restore -c Release \
  --logger 'trx;LogFileName=acceptance.trx' --results-directory artifacts
python3 scripts/summarize-results.py
PYTHONDONTWRITEBYTECODE=1 bash scripts/check-contract.sh /tmp/arp-dotnet-contract-rc1
```

On a fresh machine, first run `dotnet restore Arp.slnx --locked-mode` with the
same cache mount and `NUGET_PACKAGES`, before disabling network. The Git ownership
exception above applies only to the container process and read-only mount; it
does not change global Git configuration. No conformity obstacle was found.
No push, PR, merge, tag creation or publication was performed.
