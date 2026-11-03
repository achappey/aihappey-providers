const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
// Git checkouts can use different line endings. JSON values and all other bytes are significant.
const hash = (bytes) => crypto.createHash('sha256').update(bytes.toString('utf8').replace(/\r\n/g, '\n')).digest('hex');
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;

function files(directory, prefix = '') {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    assert.ok(!entry.isSymbolicLink(), `Metadata symlinks are not supported: ${entry.name}`);
    const relative = prefix + entry.name;
    return entry.isDirectory() ? files(path.join(directory, entry.name), relative + '/')
      : entry.name.endsWith('.json') ? [relative] : [];
  }).sort(compare);
}

function buildManifest(directory = root) {
  const names = ['provider.schema.json', ...['providers', 'models', 'pricing'].flatMap((kind) =>
    files(path.join(directory, kind), kind + '/'))].sort(compare);
  const index = JSON.parse(fs.readFileSync(path.join(directory, 'providers/index.json'), 'utf8'));
  assert.ok(Array.isArray(index) && index.length > 0, 'Provider index must be a nonempty array');
  const ids = new Set();
  const references = new Set(['providers/index.json']);
  for (const entry of index) {
    assert.deepEqual(Object.keys(entry).sort(), ['file', 'id']);
    assert.match(entry.id, /^[A-Za-z][A-Za-z0-9]*$/);
    assert.notEqual(entry.id, '__proto__');
    assert.match(entry.file, /^[a-z0-9][a-z0-9-]*\.json$/);
    assert.notEqual(entry.file, 'index.json');
    assert.ok(!ids.has(entry.id), `Duplicate provider ID: ${entry.id}`);
    assert.ok(!references.has('providers/' + entry.file), `Duplicate provider file: ${entry.file}`);
    ids.add(entry.id);
    references.add('providers/' + entry.file);
  }
  assert.deepEqual(names.filter((file) => file.startsWith('providers/')), [...references].sort(compare), 'Unindexed or missing provider files');
  return { version: 1, files: names.map((file) => {
    const bytes = fs.readFileSync(path.join(directory, file));
    const value = JSON.parse(bytes.toString('utf8'));
    assert.ok(value && typeof value === 'object', `Invalid JSON document: ${file}`);
    // Preserve vendor-specific documents (for example VoyageAI's top-level array).
    // This migration inventories data; it does not impose a new model/pricing schema.
    return { file, sha256: hash(bytes) };
  }) };
}

function main(args = process.argv.slice(2)) {
  const content = JSON.stringify(buildManifest(), null, 2) + '\n';
  const target = path.join(root, 'index.json');
  if (args.includes('--check')) assert.equal(fs.readFileSync(target, 'utf8').replace(/\r\n/g, '\n'), content, 'Stale metadata manifest; run node metadata/tools/catalog.cjs');
  else fs.writeFileSync(target, content);
  console.log(`Metadata manifest ${args.includes('--check') ? 'checked' : 'generated'}.`);
}

module.exports = { hash, buildManifest };
if (require.main === module) main();
