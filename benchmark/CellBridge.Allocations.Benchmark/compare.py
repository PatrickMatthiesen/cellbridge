#!/usr/bin/env python3
"""Compare repeat medians and check workload/wire identity for stage reports."""
import argparse
import json
from pathlib import Path
import statistics

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('before', type=Path)
parser.add_argument('after', type=Path)
options = parser.parse_args()


def load(directory):
    groups = {}
    for path in sorted(directory.glob('*.json')):
        if path.name == 'manifest.json':
            continue
        report = json.loads(path.read_text())
        if len(report['measurements']) != 1:
            raise ValueError(f'{path}: expected one isolated stage')
        row = report['measurements'][0]
        key = (report['workload'], report['payloadBytes'], row['stage'], report['backend'])
        groups.setdefault(key, []).append((report, row))
    if not groups:
        raise ValueError(f'{directory}: no stage reports')
    return groups


before = load(options.before)
after = load(options.after)
if before.keys() != after.keys():
    raise ValueError('Before/after workload sets differ')
print('workload\tbytes\tstage\tbackend\tallocated-before\tallocated-after\tchange-percent\twall-ms-before\twall-ms-after')
for key in sorted(before):
    left, right = before[key], after[key]
    if len(left) != len(right):
        raise ValueError(f'{key}: repeat counts differ')
    for field in ['runtime', 'os', 'architecture', 'processorCount', 'serverGc', 'iterations', 'requestBytes',
                  'binaryBytes', 'graphElements', 'graphBytes', 'payloadSha256', 'nestedSha256',
                  'fileCurrentSha256', 'fileLegacySha256']:
        if any(report[field] != left[0][0][field] for report, _ in left + right):
            raise ValueError(f'{key}: {field} differs')
    for field in ['contentReadsPerOperation', 'logicalContentBytesPerOperation']:
        if any(row[field] != left[0][1][field] for _, row in left + right):
            raise ValueError(f'{key}: {field} differs')
    med = lambda rows, field: statistics.median(row[field] for _, row in rows)
    alloc_before = med(left, 'allocatedBytesPerOperation')
    alloc_after = med(right, 'allocatedBytesPerOperation')
    print('\t'.join(map(str, key)), f'{alloc_before:.1f}', f'{alloc_after:.1f}',
          f'{100 * (alloc_after / alloc_before - 1):.2f}',
          f'{med(left, "wallMsPerOperation"):.4f}', f'{med(right, "wallMsPerOperation"):.4f}', sep='\t')
