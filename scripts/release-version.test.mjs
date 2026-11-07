import assert from 'node:assert/strict';
import { test } from 'node:test';
import { main, versionFromTag } from './release-version.mjs';

test('strips the v from a stable release tag', () => {
  assert.equal(versionFromTag('v1.2.3'), '1.2.3');
  assert.equal(versionFromTag('v0.10.0'), '0.10.0');
});

test('keeps the prerelease suffix', () => {
  assert.equal(versionFromTag('v0.1.0-alpha.1'), '0.1.0-alpha.1');
  assert.equal(versionFromTag('v2.0.0-rc.10'), '2.0.0-rc.10');
  assert.equal(versionFromTag('v1.0.0-beta'), '1.0.0-beta');
});

test('refuses tags that are not v<major>.<minor>.<patch>[-pre]', () => {
  for (const tag of ['1.2.3', 'v1.2', 'v1.2.3.4', 'v01.2.3', 'v1.2.3-', 'v1.2.3-alpha..1', 'v1.2.3-alpha.01', 'v1.2.3+build.1', 'release', '', undefined]) {
    assert.throws(() => versionFromTag(tag), /Not a release tag/, String(tag));
  }
});

test('main writes the version to GITHUB_OUTPUT and stdout', () => {
  const appended = [];
  const printed = [];
  const code = main(['v0.1.0-alpha.1'], {
    out: (text) => printed.push(text),
    err: () => assert.fail('no error expected'),
    env: { GITHUB_OUTPUT: '/tmp/output' },
    append: (path, text) => appended.push([path, text]),
  });
  assert.equal(code, 0);
  assert.deepEqual(appended, [['/tmp/output', 'version=0.1.0-alpha.1\n']]);
  assert.deepEqual(printed, ['0.1.0-alpha.1']);
});

test('main fails with usage on a bad tag and writes nothing', () => {
  const errors = [];
  const code = main(['main'], {
    out: () => assert.fail('no output expected'),
    err: (text) => errors.push(text),
    env: { GITHUB_OUTPUT: '/tmp/output' },
    append: () => assert.fail('nothing should be appended'),
  });
  assert.equal(code, 2);
  assert.match(errors[0], /Not a release tag: main/);
});

test('main needs exactly one argument', () => {
  const errors = [];
  const code = main([], { out: () => {}, err: (text) => errors.push(text), env: {}, append: () => {} });
  assert.equal(code, 2);
  assert.match(errors[0], /^Usage:/);
});
