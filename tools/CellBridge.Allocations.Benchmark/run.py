#!/usr/bin/env python3
"""Run isolated offline allocation stages; retain reports only under artifacts."""
import argparse
import os
import hashlib
import json
import shutil
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--label', required=True)
parser.add_argument('--repeats', type=int, default=3)
parser.add_argument('--iterations', type=int, default=30)
options = parser.parse_args()
if options.repeats < 1 or options.iterations < 1:
    parser.error('repeats and iterations must be positive')
if not options.label or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_' for c in options.label):
    parser.error('label must contain only letters, digits, hyphens or underscores')
repo = Path(__file__).resolve().parents[2]
exe = repo / 'tools/CellBridge.Allocations.Benchmark/bin/Release/net10.0/CellBridge.Allocations.Benchmark.dll'
source = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
production_diff = subprocess.check_output(['git', 'diff', 'HEAD', '--', 'src'], cwd=repo)
source += '; production-diff-sha256=' + hashlib.sha256(production_diff).hexdigest()
reports = repo / 'artifacts/allocations' / options.label
reports.mkdir(parents=True, exist_ok=False)
# Every stage launches an immutable snapshot, even if another build happens later.
shutil.copytree(exe.parent, reports / 'executable')
exe = reports / 'executable' / exe.name
assembly_hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                   for p in sorted(exe.parent.glob('*.dll'))}
benchmark_hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(Path(__file__).parent.iterdir()) if p.is_file()}
manifest = {'source': source, 'assemblySha256': assembly_hashes,
            'benchmarkSourceSha256': benchmark_hashes,
            'sdk': subprocess.check_output(['dotnet', '--version'], text=True).strip(),
            'environmentOverrides': {'DOTNET_TieredCompilation': '0'}}
(reports / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
source += '; manifest-sha256=' + hashlib.sha256((reports / 'manifest.json').read_bytes()).hexdigest()
env = os.environ.copy()
env['DOTNET_TieredCompilation'] = '0'
# Sequential execution avoids contention between this runner's stage processes.
for repeat in range(options.repeats):
    for workload, size in [('synthetic', 1024), ('synthetic', 1048576), ('synthetic', 8388608),
                           ('save-first', 0), ('save-second', 0)]:
        for stage in ['inbound', 'binary', 'restore', 'nested', 'file-build']:
            for backend in (['memory', 'filesystem'] if stage == 'restore' else ['memory']):
                name = f'{workload}-{size}-{stage}-{backend}-{repeat}.json'
                subprocess.run(['dotnet', str(exe), '--repo', str(repo), '--source', source,
                                '--workload', workload, '--bytes', str(size), '--stage', stage,
                                '--content', backend, '--iterations', str(options.iterations),
                                '--output', str(reports / name)], cwd=repo, env=env, check=True)
