const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { test } = require('node:test');
const { hash, buildManifest } = require('./catalog.cjs');

test('Speechmatics catalog tags all five STT models for real-time tokens without changing TTS capabilities', () => {
  const data = JSON.parse(fs.readFileSync(path.join(__dirname, '../models/providers/speechmatics.json'), 'utf8')).data;
  const models = data.filter(model => model.type === 'transcription');
  assert.deepEqual(models.map(model => model.id), ['standard', 'enhanced', 'melia-1', 'oak-1', 'linden-1'].map(model => 'speechmatics/' + model));
  for (const model of models) { assert.ok(model.tags.includes('real-time')); assert.ok(model.tags.includes('realtime-token')); assert.ok(!model.tags.includes('realtime-session')); }
  for (const model of data.filter(model => model.type === 'speech')) assert.ok(!model.tags?.includes('real-time'));
});

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
