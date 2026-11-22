import assert from 'node:assert/strict';
import { test } from 'node:test';
import { compareVersions, isBreakingBump, isPrerelease, parseVersion } from './semver.mjs';

test('parses the core version and the prerelease identifiers', () => {
  assert.deepEqual(parseVersion('1.2.3'), { major: 1, minor: 2, patch: 3, prerelease: [] });
  assert.deepEqual(parseVersion('0.1.0-alpha.4'), { major: 0, minor: 1, patch: 0, prerelease: ['alpha', '4'] });
  assert.deepEqual(parseVersion('1.0.0-x-y.1'), { major: 1, minor: 0, patch: 0, prerelease: ['x-y', '1'] });
});

test('orders versions by Semantic Versioning precedence', () => {
  const ascending = [
    '0.1.0-alpha',
    '0.1.0-alpha.1',
    '0.1.0-alpha.2',
    '0.1.0-alpha.10',
    '0.1.0-alpha.beta',
    '0.1.0-beta',
    '0.1.0-beta.2',
    '0.1.0-beta.11',
    '0.1.0-rc.1',
    '0.1.0',
    '0.1.1',
    '0.2.0-alpha.1',
    '0.2.0',
    '0.10.0',
    '1.0.0',
  ];
  for (let low = 0; low < ascending.length; low += 1) {
    assert.equal(compareVersions(ascending[low], ascending[low]), 0, ascending[low]);
    for (let high = low + 1; high < ascending.length; high += 1) {
      assert.equal(compareVersions(ascending[low], ascending[high]), -1, `${ascending[low]} < ${ascending[high]}`);
      assert.equal(compareVersions(ascending[high], ascending[low]), 1, `${ascending[high]} > ${ascending[low]}`);
    }
  }
});

test('tells a prerelease from a stable version', () => {
  assert.equal(isPrerelease('0.1.0-alpha.4'), true);
  assert.equal(isPrerelease('0.1.0'), false);
});

test('a break needs a new minor version while the major version is 0', () => {
  assert.equal(isBreakingBump('0.1.0', '0.2.0'), true);
  assert.equal(isBreakingBump('0.1.0', '0.2.0-alpha.1'), true);
  assert.equal(isBreakingBump('0.1.0', '1.0.0'), true);
  assert.equal(isBreakingBump('0.1.0', '0.1.1'), false);
  assert.equal(isBreakingBump('0.1.0', '0.1.1-alpha.1'), false);
});

test('a break needs a new major version from 1.0.0 on', () => {
  assert.equal(isBreakingBump('1.2.0', '2.0.0'), true);
  assert.equal(isBreakingBump('1.2.0', '2.0.0-rc.1'), true);
  assert.equal(isBreakingBump('1.2.0', '1.3.0'), false);
  assert.equal(isBreakingBump('1.2.0', '1.2.1'), false);
});
