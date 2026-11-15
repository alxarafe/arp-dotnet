#!/usr/bin/env bash
set -euo pipefail
library_root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
contract_root="${1:-${ARP_CONTRACT_PATH:-$library_root/../arp-contract}}"
expected_ref="$(cat "$library_root/contract.ref")"
actual_ref="$(git -C "$contract_root" rev-parse HEAD)"
if [[ "$actual_ref" != "$expected_ref" ]]; then
    echo "Contract SHA mismatch: expected $expected_ref, found $actual_ref" >&2
    exit 2
fi
[[ "$(git -C "$contract_root" cat-file -t refs/tags/v0.1.0-rc.1)" == tag ]]
[[ "$(git -C "$contract_root" rev-parse 'refs/tags/v0.1.0-rc.1^{}')" == "$expected_ref" ]]
git -C "$contract_root" diff --quiet HEAD -- fixtures schemas docs/features/arp-dict-001.md
cd -- "$contract_root"
export PYTHONDONTWRITEBYTECODE=1
python3 tools/check_contract.py
python3 -m unittest discover -s tests -v
