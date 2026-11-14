#!/usr/bin/env python3
"""Combine actual TRX outcomes and the directly executed contract vector counters."""
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
folder = Path(sys.argv[1]) if len(sys.argv) > 1 else root / 'artifacts'
shared = json.loads((folder / 'conformance-summary.json').read_text())
trx = ET.parse(folder / 'acceptance.trx').getroot()
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
definitions = {t.attrib['id']: t.find('t:TestMethod', ns).attrib['className']
               for t in trx.findall('t:TestDefinitions/t:UnitTest', ns)}
counts = {'shared': {'passed': 0, 'failed': 0, 'notExecuted': 0},
          'dotnet': {'passed': 0, 'failed': 0, 'notExecuted': 0}}
for result in trx.findall('t:Results/t:UnitTestResult', ns):
    name = definitions[result.attrib['testId']]
    group = 'shared' if '.ConformanceTests' in name else 'dotnet' if '.RuntimeTests' in name else None
    if group is None:
        raise RuntimeError(f'Unclassified test: {name}')
    outcome = result.attrib['outcome']
    key = 'passed' if outcome == 'Passed' else 'notExecuted' if outcome in {'NotExecuted', 'Skipped'} else 'failed'
    counts[group][key] += 1
planned_runtime = len(re.findall(r'\[Fact\]', (root / 'tests/Alxarafe.Arp.Dictionary.Tests/RuntimeTests.cs').read_text()))
observed_runtime = sum(counts['dotnet'].values())
if observed_runtime > planned_runtime:
    raise RuntimeError('More runtime cases than the declared Fact inventory')
counts['dotnet']['notExecuted'] += planned_runtime - observed_runtime
for key in ('passed', 'failed', 'notExecuted'):
    if counts['shared'][key] != shared[key]:
        raise RuntimeError(f'TRX/shared report mismatch for {key}')
report = {'contractSha': shared['contractSha'], 'shared': shared,
          'dotnet': counts['dotnet'],
          'total': {k: sum(v[k] for v in counts.values()) for k in ('passed', 'failed', 'notExecuted')},
          'sources': ['acceptance.trx', 'conformance-summary.json'],
          'runtimeFactInventory': planned_runtime}
(folder / 'acceptance-summary.json').write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
if report['total']['failed'] or report['total']['notExecuted']:
    raise SystemExit(1)
