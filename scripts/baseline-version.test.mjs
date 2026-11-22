import assert from 'node:assert/strict';
import { test } from 'node:test';
import { findBaseline, main, releaseVersions } from './baseline-version.mjs';

const nuget = (feed) => async (id) => feed[id] ?? [];

const run = async (argv, { tags = [], packageIds = [], feed = {}, env = { GITHUB_ENV: '/tmp/env' }, publishedVersions } = {}) => {
  const printed = [];
  const errors = [];
  const appended = [];
  const code = await main(argv, {
    out: (text) => printed.push(text),
    err: (text) => errors.push(text),
    env,
    append: (path, text) => appended.push([path, text]),
    tags: () => tags,
    packageIds: () => packageIds,
    publishedVersions: publishedVersions ?? nuget(feed),
  });
  return { code, printed, errors, appended };
};

test('orders release tags from highest to lowest and drops other tags', () => {
  assert.deepEqual(
    releaseVersions(['v0.1.0-alpha.2', 'v0.1.0-alpha.10', 'vnext', 'v0.1.0', 'v0.1.0-alpha.9', 'v1.2', 'v0.0.9']),
    ['0.1.0', '0.1.0-alpha.10', '0.1.0-alpha.9', '0.1.0-alpha.2', '0.0.9'],
  );
});

test('the baseline is the highest release by precedence, not by tag name order', async () => {
  const baseline = await findBaseline({
    tags: ['v0.1.0-alpha.9', 'v0.1.0-alpha.10', 'v0.1.0-alpha.2'],
    packageIds: ['A'],
    publishedVersions: nuget({ A: ['0.1.0-alpha.2', '0.1.0-alpha.9', '0.1.0-alpha.10'] }),
  });
  assert.deepEqual(baseline, { version: '0.1.0-alpha.10', packages: ['A'] });
});

test('only releases lower than the version being released count', async () => {
  const baseline = await findBaseline({
    tags: ['v0.1.0-alpha.4', 'v0.1.0-alpha.5', 'v0.2.0'],
    before: '0.1.0-alpha.5',
    packageIds: ['A'],
    publishedVersions: nuget({ A: ['0.1.0-alpha.4', '0.1.0-alpha.5', '0.2.0'] }),
  });
  assert.deepEqual(baseline, { version: '0.1.0-alpha.4', packages: ['A'] });
});

test('a package the last release did not publish has no baseline', async () => {
  const baseline = await findBaseline({
    tags: ['v0.1.0', 'v0.2.0'],
    packageIds: ['A', 'B', 'C'],
    publishedVersions: nuget({ A: ['0.1.0', '0.2.0'], B: ['0.1.0'] }),
  });
  assert.deepEqual(baseline, { version: '0.2.0', packages: ['A'] });
});

test('a tag that never reached nuget.org is passed over', async () => {
  const baseline = await findBaseline({
    tags: ['v0.1.0', 'v0.2.0'],
    packageIds: ['A', 'B'],
    publishedVersions: nuget({ A: ['0.1.0'], B: ['0.1.0'] }),
  });
  assert.deepEqual(baseline, { version: '0.1.0', packages: ['A', 'B'] });
});

test('nuget.org version casing does not matter', async () => {
  const baseline = await findBaseline({
    tags: ['v0.1.0-RC.1'],
    packageIds: ['A'],
    publishedVersions: nuget({ A: ['0.1.0-rc.1'] }),
  });
  assert.deepEqual(baseline, { version: '0.1.0-RC.1', packages: ['A'] });
});

test('there is no baseline before the first release, and nuget.org is not asked', async () => {
  const unasked = async () => assert.fail('nuget.org should not be asked');
  assert.equal(await findBaseline({ tags: [], packageIds: ['A'], publishedVersions: unasked }), null);
  assert.equal(await findBaseline({ tags: ['v0.1.0'], before: '0.1.0', packageIds: ['A'], publishedVersions: unasked }), null);
});

test('main writes the baseline to GITHUB_ENV and stdout', async () => {
  const { code, printed, errors, appended } = await run([], {
    tags: ['v0.1.0-alpha.3', 'v0.1.0-alpha.4'],
    packageIds: ['Claudalisk.Domain', 'Claudalisk.Jobs', 'Claudalisk.New'],
    feed: { 'Claudalisk.Domain': ['0.1.0-alpha.4'], 'Claudalisk.Jobs': ['0.1.0-alpha.4'] },
  });
  const lines = ['CLAUDALISK_BASELINE_VERSION=0.1.0-alpha.4', 'CLAUDALISK_BASELINE_PACKAGES=Claudalisk.Domain;Claudalisk.Jobs'];
  assert.equal(code, 0);
  assert.deepEqual(errors, []);
  assert.deepEqual(printed, lines);
  assert.deepEqual(appended, [['/tmp/env', `${lines.join('\n')}\n`]]);
});

test('main takes the tag being released with --before', async () => {
  const { code, printed, appended } = await run(['--before', 'v0.1.0-alpha.4'], {
    tags: ['v0.1.0-alpha.3', 'v0.1.0-alpha.4'],
    packageIds: ['A'],
    feed: { A: ['0.1.0-alpha.3', '0.1.0-alpha.4'] },
    env: {},
  });
  assert.equal(code, 0);
  assert.deepEqual(appended, []);
  assert.deepEqual(printed, ['CLAUDALISK_BASELINE_VERSION=0.1.0-alpha.3', 'CLAUDALISK_BASELINE_PACKAGES=A']);
});

test('main succeeds and writes nothing when there is no baseline', async () => {
  const { code, printed, appended } = await run([], { tags: [], packageIds: ['A'] });
  assert.equal(code, 0);
  assert.deepEqual(appended, []);
  assert.match(printed[0], /without a baseline/);
});

test('main fails when nuget.org cannot be asked', async () => {
  const { code, errors, appended } = await run([], {
    tags: ['v0.1.0'],
    packageIds: ['A'],
    publishedVersions: async () => {
      throw new Error('answered 503');
    },
  });
  assert.equal(code, 1);
  assert.deepEqual(appended, []);
  assert.match(errors[0], /Cannot find the last release: answered 503/);
});

test('main refuses unknown arguments and a bad tag', async () => {
  for (const argv of [['v0.1.0'], ['--before'], ['--after', 'v0.1.0'], ['--before', 'v0.1.0', 'x']]) {
    const { code, errors } = await run(argv);
    assert.equal(code, 2);
    assert.match(errors[0], /^Usage:/);
  }
  const { code, errors } = await run(['--before', 'main']);
  assert.equal(code, 2);
  assert.match(errors[0], /Not a release tag: main/);
});
