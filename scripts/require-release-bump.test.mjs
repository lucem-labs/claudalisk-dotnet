import assert from 'node:assert/strict';
import { test } from 'node:test';
import { baselineSuppressions, main, refusal } from './require-release-bump.mjs';

const suppression = ({ id = 'CP0001', target, baseline = true }) =>
  [
    '  <Suppression>',
    `    <DiagnosticId>${id}</DiagnosticId>`,
    `    <Target>${target}</Target>`,
    '    <Left>lib/net10.0/A.dll</Left>',
    '    <Right>lib/net10.0/A.dll</Right>',
    ...(baseline ? ['    <IsBaselineSuppression>true</IsBaselineSuppression>'] : []),
    '  </Suppression>',
  ].join('\n');

const file = (...suppressions) => `<?xml version="1.0" encoding="utf-8"?>\n<Suppressions>\n${suppressions.join('\n')}\n</Suppressions>\n`;

const repository = ({ tags, trees }) => (args) => {
  if (args[0] === 'tag') return `${tags.join('\n')}\n`;
  const ref = args[0] === 'ls-tree' ? args[3] : args[1].slice(0, args[1].indexOf(':'));
  const tree = trees[ref];
  if (!tree) throw new Error(`unknown ref ${ref}`);
  if (args[0] === 'ls-tree') return `${Object.keys(tree).join('\n')}\n`;
  return tree[args[1].slice(ref.length + 1)];
};

const run = (argv, git) => {
  const printed = [];
  const errors = [];
  const code = main(argv, { git, out: (text) => printed.push(text), err: (text) => errors.push(text) });
  return { code, printed, errors };
};

const PATH = 'src/A/CompatibilitySuppressions.xml';
const REMOVED_TYPE = suppression({ target: 'T:A.Removed' });
const OLD_BREAK = suppression({ target: 'T:A.Older' });

test('reads only the suppressions against the baseline', () => {
  const xml = file(REMOVED_TYPE, suppression({ id: 'CP0002', target: 'M:A.B.C', baseline: false }));
  assert.deepEqual(baselineSuppressions(xml), ['DiagnosticId=CP0001 Left=lib/net10.0/A.dll Right=lib/net10.0/A.dll Target=T:A.Removed']);
  assert.deepEqual(baselineSuppressions(file()), []);
});

test('refuses a version that is not higher than every earlier release', () => {
  for (const [version, earlier] of [
    ['0.1.0-alpha.4', ['0.1.0-alpha.5', '0.1.0-alpha.3']],
    ['0.1.0-alpha.5', ['0.1.0', '0.1.0-alpha.4']],
    ['0.1.0', ['0.2.0-alpha.1', '0.1.0-alpha.4']],
  ]) {
    assert.match(refusal({ version, earlier, added: [] }), new RegExp(`v${version} is not higher than the earlier release v${earlier[0]}`));
  }
});

test('accepts any higher version when no suppression was added', () => {
  assert.equal(refusal({ version: '0.1.1', earlier: ['0.1.0'], baseline: '0.1.0', added: [] }), null);
  assert.equal(refusal({ version: '0.1.0-alpha.5', earlier: ['0.1.0-alpha.4'], baseline: '0.1.0-alpha.4', added: [] }), null);
});

test('a break before the first stable release needs no bump', () => {
  assert.equal(refusal({ version: '0.1.0-alpha.5', earlier: ['0.1.0-alpha.4'], baseline: '0.1.0-alpha.4', added: ['x'] }), null);
  assert.equal(refusal({ version: '0.1.0', earlier: ['0.1.0-rc.1'], baseline: '0.1.0-rc.1', added: ['x'] }), null);
});

test('a break after a stable release needs a new minor version while the major version is 0', () => {
  assert.match(refusal({ version: '0.1.1', earlier: ['0.1.0'], baseline: '0.1.0', added: ['x'] }), /too small a step from the last stable release v0\.1\.0/);
  assert.match(refusal({ version: '0.1.1-alpha.1', earlier: ['0.1.0'], baseline: '0.1.0', added: ['x'] }), /too small a step/);
  assert.equal(refusal({ version: '0.2.0', earlier: ['0.1.0'], baseline: '0.1.0', added: ['x'] }), null);
  assert.equal(refusal({ version: '0.2.0-alpha.1', earlier: ['0.1.0'], baseline: '0.1.0', added: ['x'] }), null);
  assert.equal(refusal({ version: '1.0.0', earlier: ['0.1.0'], baseline: '0.1.0', added: ['x'] }), null);
});

test('a prerelease may break the one before it when its version is a breaking step from the last stable release', () => {
  const earlier = ['0.2.0-alpha.1', '0.1.0', '0.1.0-rc.1'];
  assert.equal(refusal({ version: '0.2.0-alpha.2', earlier, baseline: '0.2.0-alpha.1', added: ['x'] }), null);
  assert.equal(refusal({ version: '0.2.0', earlier, baseline: '0.2.0-alpha.1', added: ['x'] }), null);
  assert.match(
    refusal({ version: '0.1.1-alpha.2', earlier: ['0.1.1-alpha.1', '0.1.0'], baseline: '0.1.1-alpha.1', added: ['x'] }),
    /v0\.1\.1-alpha\.2 breaks v0\.1\.1-alpha\.1 but is too small a step from the last stable release v0\.1\.0/,
  );
});

test('a break after 1.0.0 needs a new major version', () => {
  assert.match(refusal({ version: '1.3.0', earlier: ['1.2.0'], baseline: '1.2.0', added: ['x'] }), /too small a step/);
  assert.equal(refusal({ version: '2.0.0-alpha.1', earlier: ['1.2.0'], baseline: '1.2.0', added: ['x'] }), null);
});

test('main compares the suppressions at the release with those at the baseline tag', () => {
  const git = repository({
    tags: ['v0.1.0', 'v0.1.0-rc.1', 'v0.1.1', 'vnext'],
    trees: {
      'refs/tags/v0.1.0': { 'README.md': '', [PATH]: file(OLD_BREAK) },
      HEAD: { 'README.md': '', [PATH]: file(OLD_BREAK, REMOVED_TYPE), 'src/B/Other.xml': file(REMOVED_TYPE) },
    },
  });
  const { code, errors } = run(['v0.1.1', '0.1.0'], git);
  assert.equal(code, 1);
  assert.match(errors[0], /v0\.1\.1 breaks v0\.1\.0/);
  assert.match(errors[0], /src\/A\/CompatibilitySuppressions\.xml: DiagnosticId=CP0001 .*Target=T:A\.Removed/);
  assert.doesNotMatch(errors[0], /T:A\.Older/);
  assert.doesNotMatch(errors[0], /Other\.xml/);
});

test('main accepts a breaking step, a kept suppression and a removed one', () => {
  const trees = {
    'refs/tags/v0.1.0': { [PATH]: file(OLD_BREAK) },
    HEAD: { [PATH]: file(OLD_BREAK, REMOVED_TYPE) },
  };
  const breaking = run(['v0.2.0', '0.1.0'], repository({ tags: ['v0.1.0', 'v0.2.0'], trees }));
  assert.equal(breaking.code, 0);
  assert.deepEqual(breaking.printed, ['v0.2.0 is a valid step from v0.1.0.']);

  const kept = run(['v0.1.1', '0.1.0'], repository({ tags: ['v0.1.0'], trees: { ...trees, HEAD: trees['refs/tags/v0.1.0'] } }));
  assert.equal(kept.code, 0);

  const removed = run(['v0.1.1', '0.1.0'], repository({ tags: ['v0.1.0'], trees: { ...trees, HEAD: { 'README.md': '' } } }));
  assert.equal(removed.code, 0);
});

test('main ignores suppressions between target frameworks', () => {
  const git = repository({
    tags: ['v0.1.0'],
    trees: {
      'refs/tags/v0.1.0': {},
      HEAD: { [PATH]: file(suppression({ target: 'T:A.OnlyOnNet11', baseline: false })) },
    },
  });
  assert.equal(run(['v0.1.1', '0.1.0'], git).code, 0);
});

test('main checks only the order when nothing is on nuget.org yet', () => {
  const git = repository({ tags: ['v0.1.0-alpha.1'], trees: {} });
  for (const baseline of [[], ['']]) {
    const { code, printed } = run(['v0.1.0-alpha.1', ...baseline], git);
    assert.equal(code, 0);
    assert.deepEqual(printed, ['v0.1.0-alpha.1 is the first release on nuget.org.']);
  }
});

test('main refuses a tag below an earlier tag', () => {
  const { code, errors } = run(['v0.1.0-alpha.4'], repository({ tags: ['v0.1.0-alpha.4', 'v0.1.0-alpha.10'], trees: {} }));
  assert.equal(code, 1);
  assert.match(errors[0], /v0\.1\.0-alpha\.4 is not higher than the earlier release v0\.1\.0-alpha\.10/);
});

test('main fails when git cannot answer', () => {
  const { code, errors } = run(['v0.1.1', '0.1.0'], repository({ tags: ['v0.1.0'], trees: {} }));
  assert.equal(code, 1);
  assert.match(errors[0], /Cannot check the step to v0\.1\.1: unknown ref refs\/tags\/v0\.1\.0/);
});

test('main refuses bad arguments', () => {
  const git = () => assert.fail('git should not run');
  for (const argv of [[], ['v0.1.0', '0.0.9', 'x']]) {
    const { code, errors } = run(argv, git);
    assert.equal(code, 2);
    assert.match(errors[0], /^Usage:/);
  }
  for (const argv of [['main'], ['v0.1.0', 'latest']]) {
    const { code, errors } = run(argv, git);
    assert.equal(code, 2);
    assert.match(errors[0], /Not a release tag/);
  }
});
