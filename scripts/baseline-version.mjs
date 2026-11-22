#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { appendFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { versionFromTag } from './release-version.mjs';
import { compareVersions } from './semver.mjs';

const USAGE = 'Usage: node scripts/baseline-version.mjs [--before <tag>]';
const FLAT_CONTAINER = 'https://api.nuget.org/v3-flatcontainer';

export function releaseVersions(tags) {
  const versions = [];
  for (const tag of tags) {
    try {
      versions.push(versionFromTag(tag));
    } catch {
      continue;
    }
  }
  return versions.sort(compareVersions).reverse();
}

export async function findBaseline({ tags, before, packageIds, publishedVersions }) {
  const candidates = releaseVersions(tags).filter((version) => !before || compareVersions(version, before) < 0);
  if (!candidates.length) return null;
  const published = new Map();
  for (const id of packageIds) {
    published.set(id, new Set((await publishedVersions(id)).map((version) => version.toLowerCase())));
  }
  for (const version of candidates) {
    const packages = packageIds.filter((id) => published.get(id).has(version.toLowerCase()));
    if (packages.length) return { version, packages };
  }
  return null;
}

export async function main(argv, { out, err, env, append, tags, packageIds, publishedVersions }) {
  let before;
  try {
    if (argv.length !== 0 && !(argv.length === 2 && argv[0] === '--before')) throw new Error(USAGE);
    if (argv.length === 2) before = versionFromTag(argv[1]);
  } catch (error) {
    err(error.message);
    return 2;
  }
  let baseline;
  try {
    baseline = await findBaseline({ tags: tags(), before, packageIds: packageIds(), publishedVersions });
  } catch (error) {
    err(`Cannot find the last release: ${error.message}`);
    return 1;
  }
  if (!baseline) {
    out('No earlier release is on nuget.org. Packing without a baseline.');
    return 0;
  }
  const lines = [`CLAUDALISK_BASELINE_VERSION=${baseline.version}`, `CLAUDALISK_BASELINE_PACKAGES=${baseline.packages.join(';')}`];
  if (env.GITHUB_ENV) append(env.GITHUB_ENV, `${lines.join('\n')}\n`);
  for (const line of lines) out(line);
  return 0;
}

function gitTags() {
  const { status, stdout, stderr } = spawnSync('git', ['tag', '--list', 'v*'], { encoding: 'utf8' });
  if (status !== 0) throw new Error(`git tag exited with ${status}: ${stderr}`);
  return stdout.split('\n').map((line) => line.trim()).filter(Boolean);
}

function sourcePackageIds() {
  return readdirSync('src', { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
    .sort();
}

async function nugetVersions(id) {
  const url = `${FLAT_CONTAINER}/${id.toLowerCase()}/index.json`;
  const response = await fetch(url);
  if (response.status === 404) return [];
  if (!response.ok) throw new Error(`${url} answered ${response.status}`);
  return (await response.json()).versions;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = await main(process.argv.slice(2), {
    out: (text) => console.log(text),
    err: (text) => console.error(text),
    env: process.env,
    append: (path, text) => appendFileSync(path, text),
    tags: gitTags,
    packageIds: sourcePackageIds,
    publishedVersions: nugetVersions,
  });
}
