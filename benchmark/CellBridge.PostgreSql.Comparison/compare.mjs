#!/usr/bin/env node
// Analyze complete SQL/EF runs. This script never starts a workload.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import crypto from 'node:crypto';

const [rootArg, outArg, expectedHead] = process.argv.slice(2);
if (!rootArg || !outArg || !/^[a-f0-9]{40}$/.test(expectedHead ?? ''))
  throw Error('Usage: node compare.mjs RUN_DIRECTORY OUTPUT_DIRECTORY SOURCE_SHA');
const runRoot = path.resolve(rootArg), out = path.resolve(outArg);
const projectRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(projectRoot, '../..');
assert(out.startsWith(path.join(repoRoot, 'artifacts') + path.sep), 'Output must be under this checkout artifacts/.');
assert(out !== runRoot && !out.startsWith(runRoot + path.sep), 'Keep analysis separate from raw results.');
const read = file => JSON.parse(fs.readFileSync(file, 'utf8'));
const text = file => fs.readFileSync(file, 'utf8');
const check = (condition, message) => assert(condition, message);
const same = (actual, expected, message) => assert.deepEqual(actual, expected, message);
const only = (values, message) => {
  const unique = [...new Map(values.map(value => [JSON.stringify(value), value])).values()];
  assert.equal(unique.length, 1, message);
  return unique[0];
};
const finite = (value, message) => check(Number.isFinite(value), message);
const parseParameters = value => Object.fromEntries(new URLSearchParams(value));
const pct = ratio => (ratio - 1) * 100;
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
function initialValue(record) {
  check(record.rows[0]?.stage === 'setup-started', `${record.file} does not start with setup-started`);
  return record.rows[0].value;
}

function databaseValue(record) {
  return record.rows.find(row => row.stage === 'database')?.value
    ?? record.rows.find(row => row.stage === 'setup-complete')?.value?.database;
}

function assertIterations(rows, stage, file) {
  const selected = rows.filter(row => row.stage === stage).map(row => row.value);
  same(selected.map(row => row.iteration), selected.map((_, index) => index), `${file} ${stage} iteration sequence`);
  return selected;
}

function validateOutcome(record, identities) {
  const initial = initialValue(record);
  const setup = record.rows.find(row => row.stage === 'setup-complete')?.value;
  const validations = record.rows.filter(row => row.stage === 'validated' || row.stage === 'iteration-validated');
  check(validations.length > 0, `${record.file} has no validation marker`);
  const shaShape = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
  const outcome = { validationMarkers: validations.length, meaningfulHashes: {}, inputIdentity: {},
    actualOutcome: {}, intentionallyUncompared: [] };

  if (initial.benchmark === 'ContentReadBenchmarks') {
    assert.equal(validations.length, 1, `${record.file} validation count`);
    const validated = validations[0].value;
    same(setup.handle, validated.handle, `${record.file} setup/readback handle`);
    assert.equal(validated.handle.Length, initial.conditions.bytes, `${record.file} content length`);
    assert.equal(validated.handle.Sha256, initial.conditions.payloadSha256, `${record.file} content SHA-256`);
    assert.equal(validated.handle.Key, initial.conditions.payloadSha256, `${record.file} content key`);
    same(validated.counts, { cellbridge_documents: 0, cellbridge_states: 0, cellbridge_objects: 1, cellbridge_chunks: 16 },
      `${record.file} content counts`);
    outcome.inputIdentity = { bytes: initial.conditions.bytes, seed: initial.conditions.seed,
      payloadSha256: initial.conditions.payloadSha256 };
    outcome.meaningfulHashes = { contentSha256: validated.handle.Sha256 };
    outcome.actualOutcome = { handle: validated.handle, counts: validated.counts };
  } else if (initial.benchmark === 'ContentWriteBenchmarks') {
    const prepared = assertIterations(record.rows, 'iteration-prepared', record.file);
    const checked = assertIterations(record.rows, 'iteration-validated', record.file);
    assert.equal(prepared.length, checked.length, `${record.file} preparation/validation marker count`);
    assert.equal(initial.conditions.payloadSha256.length, 8, `${record.file} content input count`);
    check(initial.conditions.payloadSha256.every(shaShape), `${record.file} content input SHA-256 shape`);
    for (const value of prepared)
      same(value.counts, { cellbridge_documents: 0, cellbridge_states: 0, cellbridge_objects: 0, cellbridge_chunks: 0 },
        `${record.file} pre-write counts`);
    for (const value of checked)
      same(value.counts, { cellbridge_documents: 0, cellbridge_states: 0, cellbridge_objects: 8, cellbridge_chunks: 128 },
        `${record.file} post-write counts`);
    const handlesSha256 = only(checked.map(value => value.handlesSha256), `${record.file} stable handles hash`);
    check(shaShape(handlesSha256), `${record.file} handles SHA-256 shape`);
    outcome.inputIdentity = { bytesPerObject: initial.conditions.bytesPerObject,
      payloadSha256: initial.conditions.payloadSha256 };
    outcome.meaningfulHashes = { handlesSha256 };
    outcome.actualOutcome = { objects: 8, chunks: 128 };
  } else if (initial.benchmark === 'PagingBenchmarks') {
    assert.equal(validations.length, 1, `${record.file} validation count`);
    const validated = validations[0].value;
    assert.equal(setup.payloadSha256, identities.lists.sha256, `${record.file} Lists payload identity`);
    assert.equal(setup.counts['cellbridge_lists.records'], 1000, `${record.file} prepared record count`);
    check(shaShape(validated.firstPageSha256) && shaShape(validated.secondPageSha256), `${record.file} page hashes`);
    const analyzed = initial.conditions.Analyzed;
    check((setup.statistics.columnStatistics > 0) === analyzed, `${record.file} setup statistics state`);
    check((validated.statistics.columnStatistics > 0) === analyzed, `${record.file} validation statistics state`);
    outcome.inputIdentity = { records: 1000, payloadSha256: setup.payloadSha256,
      analyzed, filter: initial.conditions.filter, pageSize: initial.conditions.pageSize };
    outcome.meaningfulHashes = { firstPageSha256: validated.firstPageSha256,
      secondPageSha256: validated.secondPageSha256 };
    outcome.actualOutcome = { setupStatistics: setup.statistics, validationStatistics: validated.statistics };
  } else if (initial.benchmark === 'ListMutationBenchmarks') {
    const prepared = assertIterations(record.rows, 'iteration-prepared', record.file);
    const checked = assertIterations(record.rows, 'iteration-validated', record.file);
    assert.equal(prepared.length, checked.length, `${record.file} preparation/validation marker count`);
    assert.equal(initial.conditions.payloadSha256, identities.lists.sha256, `${record.file} Lists payload identity`);
    const operation = initial.conditions.Operation;
    for (let i = 0; i < checked.length; i++) {
      const before = prepared[i].before, after = checked[i].after;
      same(checked[i].before, before, `${record.file} recorded before counts`);
      assert.equal(before['cellbridge_lists.records'], operation === 'create' ? 0 : 16, `${record.file} starting records`);
      assert.equal(after['cellbridge_lists.records'], 16, `${record.file} ending records`);
      assert.equal(after['cellbridge_lists.create_receipts'], 16, `${record.file} ending receipts`);
      assert.equal(after['cellbridge_lists.audit_events'] - before['cellbridge_lists.audit_events'], 16,
        `${record.file} audit growth`);
    }
    const warmPrepared = record.rows.filter(row => row.stage === 'engine-warmup-prepared');
    const warmValidated = record.rows.filter(row => row.stage === 'engine-warmup-validated');
    assert.equal(warmPrepared.length, 1, record.file + ' one explicit untimed engine warmup preparation');
    assert.equal(warmValidated.length, 1, record.file + ' one explicit untimed engine warmup validation');
    check(record.rows.indexOf(warmValidated[0]) < record.rows.findIndex(row => row.stage === 'iteration-prepared'), record.file + ' engine warmed before normal iterations');
    same(warmPrepared[0].value.before, prepared[0].before, record.file + ' warm and timed initial state');
    same(warmPrepared[0].value.requestsSha256, prepared[0].requestsSha256, record.file + ' warm and timed requests');
    same(warmValidated[0].value.before, checked[0].before, record.file + ' warm and timed before counts');
    same(warmValidated[0].value.after, checked[0].after, record.file + ' warm and timed after counts');
    same(warmValidated[0].value.normalizedResultsSha256, checked[0].normalizedResultsSha256, record.file + ' warm and timed result identity');
    const requestsSha256 = only(prepared.map(value => value.requestsSha256), `${record.file} stable request hash`);
    const normalizedResultsSha256 = only(checked.map(value => value.normalizedResultsSha256),
      `${record.file} stable normalized result hash`);
    check(shaShape(requestsSha256) && shaShape(normalizedResultsSha256), `${record.file} mutation hashes`);
    outcome.inputIdentity = { operation, payloadSha256: initial.conditions.payloadSha256, requestsSha256 };
    outcome.meaningfulHashes = { normalizedResultsSha256 };
    outcome.actualOutcome = { before: prepared[0].before, after: checked[0].after };
  } else if (initial.benchmark === 'ServiceSaveBenchmarks') {
    const prepared = assertIterations(record.rows, 'iteration-prepared', record.file);
    const checked = assertIterations(record.rows, 'iteration-validated', record.file);
    assert.equal(prepared.length, checked.length, `${record.file} preparation/validation marker count`);
    const expectedInputs = identities.storage.filter(item => item.sizeMiB === 1 && item.seed >= 42 && item.seed <= 54)
      .map(item => item.sha256);
    same(initial.conditions.payloadSha256, expectedInputs, `${record.file} frozen DOCX identities`);
    const mode = initial.conditions.Mode;
    for (const value of checked) {
      assert.equal(value.histories.length, 8, `${record.file} saved document count`);
      for (let doc = 0; doc < value.histories.length; doc++) {
        const history = value.histories[doc];
        const finalSha = mode === 'changed-full' ? expectedInputs[5 + doc] : expectedInputs[0];
        const revisionContents = mode === 'changed-full'
          ? [...expectedInputs.slice(0, 5), finalSha]
          : Array(6).fill(expectedInputs[0]);
        assert.equal(history.StateVersion, 5, `${record.file} state version`);
        assert.equal(history.ContentVersion, 6, `${record.file} content version`);
        assert.equal(history.revisions, 6, `${record.file} revision count`);
        assert.equal(history.receipts, 5, `${record.file} receipt count`);
        assert.equal(history.contentSha256, finalSha, `${record.file} final content identity`);
        same(history.revisionContents, revisionContents, `${record.file} revision content identities`);
      }
      assert.equal(hash(Buffer.from(JSON.stringify(value.histories))), value.normalizedResultsSha256,
        `${record.file} normalized save result recomputation`);
    }
    const normalizedResultsSha256 = only(checked.map(value => value.normalizedResultsSha256),
      `${record.file} stable normalized save result hash`);
    check(shaShape(normalizedResultsSha256), `${record.file} normalized save hash`);
    outcome.inputIdentity = { mode, payloadSha256: expectedInputs };
    outcome.meaningfulHashes = { normalizedResultsSha256 };
    outcome.actualOutcome = { preparedCounts: only(prepared.map(value => value.counts), `${record.file} prepared counts`),
      validatedCounts: only(checked.map(value => value.counts), `${record.file} validated counts`) };
    outcome.intentionallyUncompared = [
      'requestSha256 contains generated protocol identifiers',
      'stateSha256 and responseSha256 contain generated identifiers or other provider-specific representation'
    ];
  } else throw Error(`Unknown benchmark family ${initial.benchmark}`);

  return outcome;
}

const reportFiles = fs.readdirSync(path.join(runRoot, 'bdn/results')).filter(file => file.endsWith('-report-full-compressed.json')).sort();
assert.equal(reportFiles.length, 5, 'Expected five BenchmarkDotNet JSON reports');
const benchmarks = reportFiles.flatMap(file => read(path.join(runRoot, 'bdn/results', file)).Benchmarks
  .map(benchmark => ({ ...benchmark, report: file })));
assert.equal(benchmarks.length, 20, 'Expected 20 BenchmarkDotNet cases');

const runLog = text(path.join(runRoot, 'run.log'));
check(runLog.includes('Global total time:') && runLog.includes('executed benchmarks: 20'), 'Incomplete global BenchmarkDotNet run');
const exitMatches = [...runLog.matchAll(/\/\/ Benchmark Process (\d+) has exited with code (\d+)\./g)];
assert.equal(exitMatches.length, 20, 'Expected 20 benchmark process exits');
check(exitMatches.every(match => match[2] === '0'), 'A benchmark process had a nonzero exit code');


const logCases = [];
const blocks = runLog.split(/(?=^\/\/ Benchmark: )/m).slice(1);
for (const block of blocks) {
  const heading = block.match(/^\/\/ Benchmark: (\w+)\.(\w+): .* \[([^\]]+)\]/m);
  if (!heading) continue;
  const pid = block.match(/Starting DCP with arguments: start-apiserver --monitor (\d+)/)?.[1];
  const exit = block.match(/\/\/ Benchmark Process (\d+) has exited with code (\d+)\./);
  check(pid && exit && pid === exit[1] && exit[2] === '0', `Incomplete process mapping for ${heading[1]}.${heading[2]}`);
  const parameters = Object.fromEntries(heading[3].split(', ').map(item => {
    const index = item.indexOf('=');
    return [item.slice(0, index), item.slice(index + 1)];
  }));
  logCases.push({ type: heading[1], method: heading[2], parameters, pid: Number(pid) });
}
assert.equal(logCases.length, 20, 'Expected 20 PID-to-case mappings in the run log');

const evidenceFiles = fs.readdirSync(runRoot).filter(file => file.endsWith('.jsonl')).sort();
assert.equal(evidenceFiles.length, 20, 'Expected exactly 20 provenance files');
const evidence = evidenceFiles.map(file => {
  const match = file.match(/^(\w+)-(sql|ef)-(\d+)-([a-f0-9]{32})\.jsonl$/);
  check(match, `Malformed provenance filename ${file}`);
  const rows = text(path.join(runRoot, file)).trim().split(/\n/).map((line, index) => {
    try { return JSON.parse(line); } catch (error) { throw Error(`${file}:${index + 1}: ${error.message}`); }
  });
  return { file, family: match[1], variant: match[2], pid: Number(match[3]), rows };
});

const commonInitial = ['method', 'currentHead', 'status', 'diffSha256', 'sqlHead', 'runtime', 'os',
  'architecture', 'processors', 'serverGc', 'sourceHashes'];
for (const record of evidence) {
  const initial = initialValue(record);
  assert.equal(initial.benchmark, record.family, record.file + ' family');
  assert.equal(initial.variant, record.variant, record.file + ' variant');
  assert.equal(initial.method, 'optimized-matched-long-lived-engine-v1', record.file + ' method');
  assert.equal(initial.currentHead, expectedHead, record.file + ' source HEAD');
  assert.equal(initial.status, '', record.file + ' requires a clean checkout');
  assert.equal(initial.diffSha256, hash(''), record.file + ' source diff');
  assert.equal(initial.sqlHead, '9827c29211ded20543546fe7696c8c28521189cb', record.file + ' SQL HEAD');
}
for (const key of commonInitial)
  only(evidence.map(record => initialValue(record)[key]), 'All processes agree on ' + key);
const fingerprints = evidence.map(record => initialValue(record).assemblies.map(a => ({
  identity: a.identity, loadContext: a.loadContext, mvid: a.mvid, version: a.version, sha256: a.sha256
})).sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b))));
only(fingerprints, 'All processes agree on assembly fingerprints');
for (const assembly of initialValue(evidence[0]).assemblies) {
  if (!assembly.identity.startsWith('CellBridge.')) continue;
  const source = assembly.loadContext === 'immutable-original-sql-providers'
    ? '9827c29211ded20543546fe7696c8c28521189cb' : expectedHead;
  check(assembly.version?.includes(source), 'Assembly source identity: ' + assembly.identity);
}
const verifiedSources = [];
const unavailableSources = [];
for (const [file, expected] of Object.entries(initialValue(evidence[0]).sourceHashes)) {
  let bytes;
  try { bytes = execFileSync('git', ['show', expectedHead + ':' + file], {cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe']}); }
  catch { unavailableSources.push(file); continue; }
  assert.equal(hash(bytes), expected, 'Recorded source hash: ' + file);
  verifiedSources.push(file);
}
for (const record of evidence) {
  const logCase = logCases.find(item => item.pid === record.pid);
  check(logCase, `No run-log case for ${record.file}`);
  assert.equal(logCase.type, record.family, `${record.file} run-log family`);
  assert.equal(logCase.parameters.Variant, record.variant, `${record.file} run-log variant`);
  const condition = initialValue(record).conditions;
  if ('Analyzed' in logCase.parameters) assert.equal(String(condition.Analyzed).toLowerCase(), logCase.parameters.Analyzed.toLowerCase(), `${record.file} analyzed parameter`);
  if ('Operation' in logCase.parameters) assert.equal(condition.Operation, logCase.parameters.Operation, `${record.file} operation parameter`);
  if ('Mode' in logCase.parameters) assert.equal(condition.Mode, logCase.parameters.Mode, `${record.file} mode parameter`);
  record.logCase = logCase;
  record.benchmark = benchmarks.find(benchmark => benchmark.Type === logCase.type && benchmark.Method === logCase.method &&
    Object.entries(parseParameters(benchmark.Parameters)).every(([key, value]) => String(logCase.parameters[key]).toLowerCase() === String(value).toLowerCase()));
  check(record.benchmark, `No BDN result for ${record.file}`);
}
assert.equal(new Set(evidence.map(record => record.benchmark.FullName)).size, 20, 'Every BDN case has one provenance file');

const identities = read(path.join(projectRoot, 'expected-inputs.json'));
for (const record of evidence) record.outcome = validateOutcome(record, identities);
const cases = evidence.map(record => {
  const benchmark = record.benchmark;
  const workloadActual = benchmark.Measurements.filter(measurement =>
    measurement.IterationMode === 'Workload' && measurement.IterationStage === 'Actual');
  const workloadResult = benchmark.Measurements.filter(measurement =>
    measurement.IterationMode === 'Workload' && measurement.IterationStage === 'Result');
  check(workloadActual.length >= 8, `Fewer than eight target iterations: ${benchmark.FullName}`);
  check(benchmark.Statistics && benchmark.Statistics.N > 0, `No retained results: ${benchmark.FullName}`);
  assert.equal(workloadResult.length, benchmark.Statistics.N, `Retained result count: ${benchmark.FullName}`);
  check(benchmark.Statistics.N <= workloadActual.length, `Retained N exceeds actual target iterations: ${benchmark.FullName}`);
  for (const value of [benchmark.Statistics.Mean, benchmark.Statistics.Median, benchmark.Statistics.Min,
    benchmark.Statistics.Max, benchmark.Statistics.StandardDeviation, benchmark.Memory.BytesAllocatedPerOperation])
    finite(value, `Non-finite result: ${benchmark.FullName}`);
  const parameters = parseParameters(benchmark.Parameters);
  return {
    fullName: benchmark.FullName, type: benchmark.Type, method: benchmark.Method, parameters,
    evidenceFile: record.file, actualIterations: workloadActual.length, retainedN: benchmark.Statistics.N,
    removedMeasurements: workloadActual.length - benchmark.Statistics.N,
    detectedOutliersInStatistics: benchmark.Statistics.AllOutliers?.length ?? 0,
    meanMs: benchmark.Statistics.Mean / 1e6, medianMs: benchmark.Statistics.Median / 1e6,
    minMs: benchmark.Statistics.Min / 1e6, maxMs: benchmark.Statistics.Max / 1e6,
    standardDeviationMs: benchmark.Statistics.StandardDeviation / 1e6,
    coefficientOfVariation: benchmark.Statistics.StandardDeviation / benchmark.Statistics.Mean,
    confidenceIntervalMs: { lower: benchmark.Statistics.ConfidenceInterval.Lower / 1e6,
      upper: benchmark.Statistics.ConfidenceInterval.Upper / 1e6,
      level: benchmark.Statistics.ConfidenceInterval.Level },
    allocatedBytes: benchmark.Memory.BytesAllocatedPerOperation,
    inputIdentity: record.outcome.inputIdentity,
    validationMarkers: record.outcome.validationMarkers, meaningfulHashes: record.outcome.meaningfulHashes,
    actualOutcome: record.outcome.actualOutcome, intentionallyUncompared: record.outcome.intentionallyUncompared
  };
});

const pairGroups = new Map();
for (const result of cases) {
  const parameters = { ...result.parameters };
  const variant = parameters.Variant;
  delete parameters.Variant;
  const key = JSON.stringify([result.type, result.method, parameters]);
  if (!pairGroups.has(key)) pairGroups.set(key, { type: result.type, method: result.method, parameters });
  check(!pairGroups.get(key)[variant], `Duplicate ${variant} case ${key}`);
  pairGroups.get(key)[variant] = result;
}
assert.equal(pairGroups.size, 10, 'Expected ten logical SQL/EF pairs');

const comparisons = [...pairGroups.values()].map(pair => {
  check(pair.sql && pair.ef, `Unmatched SQL/EF pair ${pair.type}.${pair.method}`);
  const sqlEvidence = evidence.find(record => record.file === pair.sql.evidenceFile);
  const efEvidence = evidence.find(record => record.file === pair.ef.evidenceFile);
  same(initialValue(sqlEvidence).conditions, initialValue(efEvidence).conditions, `${pair.type}.${pair.method} conditions`);
  same(sqlEvidence.outcome.inputIdentity, efEvidence.outcome.inputIdentity, `${pair.type}.${pair.method} input identity`);
  same(sqlEvidence.outcome.meaningfulHashes, efEvidence.outcome.meaningfulHashes,
    `${pair.type}.${pair.method} normalized outcome hashes`);
  same(sqlEvidence.outcome.actualOutcome, efEvidence.outcome.actualOutcome,
    `${pair.type}.${pair.method} actual outcome`);
  const sqlDatabase = databaseValue(sqlEvidence), efDatabase = databaseValue(efEvidence);
  same(sqlDatabase.postgres, efDatabase.postgres, `${pair.type}.${pair.method} PostgreSQL version`);
  same(sqlDatabase.settings, efDatabase.settings, `${pair.type}.${pair.method} PostgreSQL settings`);
  same(sqlDatabase.pool, efDatabase.pool, `${pair.type}.${pair.method} pool settings`);
  same(sqlDatabase.runtimeRole, efDatabase.runtimeRole, `${pair.type}.${pair.method} runtime role`);
  const meanRatio = pair.ef.meanMs / pair.sql.meanMs;
  const medianRatio = pair.ef.medianMs / pair.sql.medianMs;
  const allocationRatio = pair.ef.allocatedBytes / pair.sql.allocatedBytes;
  const confidenceIntervalsOverlap = pair.ef.confidenceIntervalMs.lower <= pair.sql.confidenceIntervalMs.upper &&
    pair.sql.confidenceIntervalMs.lower <= pair.ef.confidenceIntervalMs.upper;
  return {
    type: pair.type, method: pair.method, parameters: pair.parameters, sql: pair.sql, ef: pair.ef,
    meanRatio, meanDifferencePercent: pct(meanRatio), medianRatio, medianDifferencePercent: pct(medianRatio),
    allocationRatio, allocationDifferencePercent: pct(allocationRatio), confidenceIntervalsOverlap,
    schema: { sql: sqlDatabase.schema, ef: efDatabase.schema }, settingsParity: true, normalizedOutcomeParity: true
  };
});

const result = {
  validationStatus: 'passed', sourceHead: expectedHead, verifiedSources, unavailableSources,
  assemblyFingerprints: fingerprints[0], cases, comparisons,
  limits: 'One process launch per case in fixed order. Allocation is not retained heap. This analysis does not collect or qualify host resource pressure.'
};
fs.mkdirSync(out, { recursive: true });
check(fs.readdirSync(out).length === 0, 'Use an empty analysis output directory.');
fs.writeFileSync(path.join(out, 'comparison.json'), JSON.stringify(result, null, 2) + '\n');
const lines = ['# Warm PostgreSQL comparison', '',
  '| Case | SQL mean ms | EF mean ms | EF time difference | EF allocation difference | CI overlap |',
  '| --- | ---: | ---: | ---: | ---: | --- |'];
const percent = value => (value >= 0 ? '+' : '') + value.toFixed(1) + '%';
for (const c of comparisons) lines.push('| ' + [c.type + '.' + c.method + ' ' + JSON.stringify(c.parameters),
  c.sql.meanMs.toFixed(3), c.ef.meanMs.toFixed(3), percent(c.meanDifferencePercent),
  percent(c.allocationDifferencePercent), c.confidenceIntervalsOverlap ? 'yes' : 'no'].join(' | ') + ' |');
lines.push('', result.limits, '', 'Overlapping confidence intervals do not establish an advantage or equivalence.',
  '', 'The JSON retains medians, confidence intervals, variation, actual and retained iteration counts, removed measurements, normalized outputs and assembly fingerprints.',
  '', 'Source files unavailable in git at the supplied commit: ' + (unavailableSources.join(', ') || 'none') + '.');
fs.writeFileSync(path.join(out, 'comparison.md'), lines.join('\n') + '\n');
console.log(JSON.stringify({validationStatus: result.validationStatus, cases: cases.length, pairs: comparisons.length,
  unavailableSources: unavailableSources.length}));
