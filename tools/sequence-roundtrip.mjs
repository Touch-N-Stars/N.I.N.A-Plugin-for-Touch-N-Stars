// Contract test for the sequence editor API against a running NINA with this plugin.
//
//   node tools/sequence-roundtrip.mjs [--base http://localhost:5000/api/] [--out report.json]
//
// For every item, trigger and condition type NINA offers, the script adds the type to an empty
// sequence, asks /sequence/fields which properties are editable and writes each of them twice:
// once with its current value (must succeed) and once with a changed value (must either succeed
// and be read back, or be rejected with Success=false - never a silent no-op).
//
// The currently loaded sequence is saved to a backup file in NINA's sequence folder first and
// loaded again at the end.
// Do not run it while a sequence is running; the script refuses to.
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const arg = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const BASE = arg('--base', 'http://localhost:5000/api/');
const REPORT = path.resolve(arg('--out', 'sequence-roundtrip-report.json'));
// The plugin only reads and writes sequence files inside NINA's sequence folder; a relative
// name is resolved against that folder.
const BACKUP = `tns-sequence-roundtrip-backup-${Date.now()}.json`;

async function call(method, route, params = {}) {
  const url = new URL(route, BASE);
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
  }
  const res = await fetch(url, { method, signal: AbortSignal.timeout(30000) });
  const text = await res.text();
  try {
    return { status: res.status, body: JSON.parse(text) };
  } catch {
    return { status: res.status, body: text };
  }
}

function walk(nodes, fn) {
  for (const n of nodes ?? []) {
    if (!n || typeof n !== 'object') continue;
    fn(n);
    walk(n.Items, fn);
    walk(n.Triggers, fn);
    walk(n.Conditions, fn);
    walk(n.GlobalTriggers, fn);
  }
}
async function current() {
  const r = await call('GET', 'sequence/current');
  if (!Array.isArray(r.body)) throw new Error(`sequence/current failed: HTTP ${r.status}`);
  return r.body;
}
function findById(tree, id) {
  let hit = null;
  walk(tree, (n) => {
    if (n.Id === id) hit = n;
  });
  return hit;
}
function allIds(tree) {
  const ids = new Set();
  walk(tree, (n) => n.Id && ids.add(n.Id));
  return ids;
}

function changedValue(field, value) {
  switch (field.Type) {
    case 'boolean':
      return !value;
    case 'integer':
      return value + 1;
    case 'number':
      return Math.round((value + 0.5) * 100) / 100;
    case 'choice': {
      const other = (field.Options ?? []).find((o) => o !== String(value));
      return other ?? value;
    }
    default:
      return `${value}_rt`;
  }
}
function same(a, b) {
  if (typeof a === 'number' && typeof b === 'number') return Math.abs(a - b) < 1e-6;
  return String(a) === String(b);
}

const status = await call('GET', 'sequence/status');
if (status.status === 404) {
  console.error('This plugin build has no /sequence/status - wrong plugin version?');
  process.exit(1);
}
if (status.body?.Running) {
  console.error('A sequence is running - aborting.');
  process.exit(1);
}
const save = await call('POST', 'sequence/save', { filePath: BACKUP });
if (!save.body?.Success) {
  console.error('Could not back up the loaded sequence - aborting.', save.body);
  process.exit(1);
}
console.log(`Loaded sequence saved as ${BACKUP} in NINA's sequence folder`);

const report = [];
try {
  const clear = await call('POST', 'sequence/clear');
  if (!clear.body?.Success) throw new Error(`sequence/clear failed: ${JSON.stringify(clear.body)}`);

  let tree = await current();
  const area = tree.find((c) => c.FullTypeName?.endsWith('TargetAreaContainer'));
  if (!area) throw new Error('no TargetAreaContainer in an empty sequence');

  // Host container for triggers and conditions
  const before = allIds(tree);
  await call('POST', 'sequence/add', {
    targetId: area.Id,
    type: 'NINA.Sequencer.Container.SequentialContainer',
  });
  tree = await current();
  const hostId = [...allIds(tree)].find((id) => !before.has(id));
  if (!hostId) throw new Error('could not create the host container');

  const kinds = [
    ['item', 'sequence/items', area.Id, 'Items'],
    ['trigger', 'sequence/triggers', hostId, 'Triggers'],
    ['condition', 'sequence/conditions', hostId, 'Conditions'],
  ];

  for (const [kind, listRoute, targetId, poolKey] of kinds) {
    const types = (await call('GET', listRoute)).body?.Items ?? [];
    for (const type of types) {
      const entry = { kind, type: type.FullTypeName, fields: [] };
      report.push(entry);
      process.stdout.write(`[${kind}] ${type.FullTypeName}\n`);
      try {
        const idsBefore = allIds(await current());
        const add = await call('POST', 'sequence/add', { targetId, type: type.FullTypeName });
        if (!add.body?.Success) {
          entry.addError = add.body?.Error ?? `HTTP ${add.status}`;
          continue;
        }
        const target = findById(await current(), targetId);
        const node = (target?.[poolKey] ?? []).find((n) => !idsBefore.has(n.Id));
        if (!node) {
          entry.addError = 'added node not found';
          continue;
        }

        const meta = await call('GET', 'sequence/fields', { id: node.Id });
        if (!Array.isArray(meta.body?.Fields)) {
          entry.fieldsError = meta.body?.Error ?? `HTTP ${meta.status}`;
          continue;
        }

        for (const field of meta.body.Fields) {
          const original = node[field.Name];
          const result = { name: field.Name, type: field.Type, readOnly: field.ReadOnly, original };
          entry.fields.push(result);
          if (field.ReadOnly || original === undefined || original === null) continue;
          if (!['integer', 'number', 'boolean', 'string', 'choice'].includes(field.Type)) continue;

          const r1 = await call('POST', 'sequence/set', { id: node.Id, propertyName: field.Name, value: original });
          result.sameOk = r1.body?.Success === true;
          if (!result.sameOk) result.sameError = r1.body?.Error ?? `HTTP ${r1.status}`;

          const next = changedValue(field, original);
          const r2 = await call('POST', 'sequence/set', { id: node.Id, propertyName: field.Name, value: next });
          result.changeAccepted = r2.body?.Success === true;
          if (!result.changeAccepted) result.changeError = r2.body?.Error ?? `HTTP ${r2.status}`;
          const readBack = findById(await current(), node.Id)?.[field.Name];
          result.readBack = readBack;
          // Silent failure: the API reported success but the value did not change
          result.silentFailure = result.changeAccepted && !same(readBack, next);

          if (result.changeAccepted) {
            await call('POST', 'sequence/set', { id: node.Id, propertyName: field.Name, value: original });
          }
        }
        await call('POST', 'sequence/remove', { id: node.Id });
      } catch (e) {
        entry.error = String(e?.message ?? e);
        try {
          await call('GET', 'sequence/status');
        } catch {
          console.error('NINA does not answer any more - stopping.');
          throw e;
        }
      }
    }
  }
} finally {
  fs.writeFileSync(REPORT, JSON.stringify(report, null, 2));
  const load = await call('GET', 'sequence/load', { filePath: BACKUP }).catch((e) => ({ body: e.message }));
  console.log(
    load.body?.Success
      ? 'Previous sequence restored.'
      : `RESTORE FAILED - load ${BACKUP} manually in NINA (${JSON.stringify(load.body)})`
  );
}

// --- summary ---
const tested = report.flatMap((e) => e.fields.map((f) => ({ ...f, owner: e.type })));
const writable = tested.filter((f) => f.sameOk !== undefined);
const sameFailures = writable.filter((f) => !f.sameOk);
const silent = writable.filter((f) => f.silentFailure);
const rejected = writable.filter((f) => f.changeAccepted === false);
console.log(`\nTypes: ${report.length}, add errors: ${report.filter((e) => e.addError).length}, other errors: ${report.filter((e) => e.error || e.fieldsError).length}`);
console.log(`Writable fields tested: ${writable.length}, read-only: ${tested.filter((f) => f.readOnly).length}`);
console.log(`Current value not accepted: ${sameFailures.length}`);
console.log(`Change rejected with an error (fine): ${rejected.length}`);
console.log(`Silent failures (bug): ${silent.length}`);
for (const f of [...sameFailures, ...silent]) {
  console.log(`  ${f.owner} ${f.name} (${f.type}): ${f.sameError ?? `read back ${JSON.stringify(f.readBack)}`}`);
}
console.log(`\nReport: ${REPORT}`);
process.exitCode = sameFailures.length || silent.length ? 1 : 0;
