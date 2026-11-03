const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { test } = require('node:test');
const { hash, buildManifest } = require('./catalog.cjs');

test('manifest inventories every display, model, and pricing document, including nested variants', () => {
  const expected = JSON.parse(fs.readFileSync(path.join(__dirname, '../index.json'), 'utf8'));
  assert.deepEqual(buildManifest(), expected);
  assert.ok(expected.files.some(({ file }) => /^pricing\/providers\/[^/]+\/[^/]+\.json$/.test(file)));
  assert.ok(expected.files.some(({ file }) => /^models\/providers\/[^/]+\/[^/]+\.json$/.test(file)));
  assert.equal(hash(Buffer.from('{\r\n}\r\n')), hash(Buffer.from('{\n}\n')));
});

test('invalid indexes and malformed documents fail rather than publishing partial data', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'metadata-catalog-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  for (const kind of ['providers', 'models', 'pricing']) fs.mkdirSync(path.join(root, kind));
  fs.writeFileSync(path.join(root, 'provider.schema.json'), '{}');
  fs.writeFileSync(path.join(root, 'providers/example.json'), '{"name":"Example"}');
  const entry = { id: 'example', file: 'example.json' };
  const writeIndex = (entries) => fs.writeFileSync(path.join(root, 'providers/index.json'), JSON.stringify(entries));
  writeIndex([entry]);
  assert.equal(buildManifest(root).files.length, 3);
  writeIndex([entry, entry]);
  assert.throws(() => buildManifest(root), /Duplicate/);
  writeIndex([{ ...entry, file: '../example.json' }]);
  assert.throws(() => buildManifest(root));
  writeIndex([entry]);
  fs.writeFileSync(path.join(root, 'models/broken.json'), '{');
  assert.throws(() => buildManifest(root), SyntaxError);
});
