#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { releaseVersions } from './baseline-version.mjs';
import { versionFromTag } from './release-version.mjs';
import { compareVersions, isBreakingBump, isPrerelease } from './semver.mjs';

const USAGE = 'Usage: node scripts/require-release-bump.mjs <tag> [<baseline version>]';
const SUPPRESSION_FILE = /(^|\/)CompatibilitySuppressions\.xml$/;
const RELEASED_TREE = 'HEAD';

export function baselineSuppressions(xml) {
  const found = [];
  for (const [, body] of xml.matchAll(/<Suppression>([\s\S]*?)<\/Suppression>/g)) {
    const fields = Object.fromEntries([...body.matchAll(/<(\w+)>([^<]*)<\/\1>/g)].map(([, name, value]) => [name, value.trim()]));
    if (fields.IsBaselineSuppression !== 'true') continue;
    found.push(
      Object.keys(fields)
        .filter((name) => name !== 'IsBaselineSuppression')
        .sort()
        .map((name) => `${name}=${fields[name]}`)
        .join(' '),
    );
  }
  return found;
}

function suppressionsAt(git, ref) {
  const found = new Set();
  const paths = git(['ls-tree', '-r', '--name-only', ref]).split('\n').map((line) => line.trim()).filter((path) => SUPPRESSION_FILE.test(path));
  for (const path of paths) {
    for (const suppression of baselineSuppressions(git(['show', `${ref}:${path}`]))) found.add(`${path}: ${suppression}`);
  }
  return found;
}

export function addedSuppressions(git, baseline) {
  const before = suppressionsAt(git, `refs/tags/v${baseline}`);
  return [...suppressionsAt(git, RELEASED_TREE)].filter((suppression) => !before.has(suppression)).sort();
}

export function refusal({ version, earlier, baseline, added }) {
  const notLower = earlier.filter((other) => compareVersions(other, version) >= 0);
  if (notLower.length) {
    return `v${version} is not higher than the earlier release v${notLower[0]}. Each release must be higher than every earlier one.`;
  }
  if (!added.length) return null;
  const lastStable = earlier.find((other) => !isPrerelease(other));
  if (!lastStable || isBreakingBump(lastStable, version)) return null;
  return [
    `v${version} breaks v${baseline} but is too small a step from the last stable release v${lastStable}.`,
    'While the major version is 0 a break needs a new minor version, and from 1.0.0 on a new major version. A prerelease of that version may carry the break.',
    'Suppressions added since the last release:',
    ...added.map((suppression) => `  ${suppression}`),
  ].join('\n');
}

export function main(argv, { git, out, err }) {
  let version;
  let baseline;
  try {
    if (argv.length < 1 || argv.length > 2) throw new Error(USAGE);
    version = versionFromTag(argv[0]);
    baseline = argv[1] ? versionFromTag(`v${argv[1]}`) : undefined;
  } catch (error) {
    err(error.message);
    return 2;
  }
  let message;
  try {
    const earlier = releaseVersions(git(['tag', '--list', 'v*']).split('\n').map((line) => line.trim())).filter((other) => other !== version);
    const added = baseline ? addedSuppressions(git, baseline) : [];
    message = refusal({ version, earlier, baseline, added });
  } catch (error) {
    err(`Cannot check the step to v${version}: ${error.message}`);
    return 1;
  }
  if (message) {
    err(message);
    return 1;
  }
  out(baseline ? `v${version} is a valid step from v${baseline}.` : `v${version} is the first release on nuget.org.`);
  return 0;
}

function runGit(args) {
  const { status, stdout, stderr } = spawnSync('git', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (status !== 0) throw new Error(`git ${args.join(' ')} exited with ${status}: ${stderr}`);
  return stdout;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2), {
    git: runGit,
    out: (text) => console.log(text),
    err: (text) => console.error(text),
  });
}
