import assert from 'node:assert/strict';
import { test } from 'node:test';
import { main, packagesIn } from './require-packages.mjs';

const run = (argv, list) => {
  const out = [];
  const err = [];
  const code = main(argv, { list, out: (text) => out.push(text), err: (text) => err.push(text) });
  return { code, out, err };
};

test('lists the .nupkg files and leaves out symbol packages', () => {
  assert.deepEqual(
    packagesIn(['Claudalisk.Jobs.1.0.0.nupkg', 'Claudalisk.Domain.1.0.0.snupkg', 'Claudalisk.Domain.1.0.0.nupkg', 'notes.txt']),
    ['Claudalisk.Domain.1.0.0.nupkg', 'Claudalisk.Jobs.1.0.0.nupkg'],
  );
});

test('passes and prints the packages when there is at least one', () => {
  const listed = [];
  const { code, out, err } = run(['artifacts'], (folder) => {
    listed.push(folder);
    return ['Claudalisk.Domain.0.1.0-alpha.1.nupkg', 'Claudalisk.Domain.0.1.0-alpha.1.snupkg'];
  });
  assert.equal(code, 0);
  assert.deepEqual(listed, ['artifacts']);
  assert.deepEqual(out, ['Claudalisk.Domain.0.1.0-alpha.1.nupkg']);
  assert.deepEqual(err, []);
});

test('fails when the folder holds no package', () => {
  const { code, out, err } = run(['artifacts'], () => []);
  assert.equal(code, 1);
  assert.deepEqual(out, []);
  assert.match(err[0], /No packages in artifacts/);
});

test('fails when the folder cannot be read', () => {
  const { code, err } = run(['missing'], () => {
    throw new Error('ENOENT');
  });
  assert.equal(code, 1);
  assert.match(err[0], /Cannot read missing: ENOENT/);
});

test('needs exactly one argument', () => {
  const { code, err } = run([], () => []);
  assert.equal(code, 2);
  assert.match(err[0], /^Usage:/);
});
