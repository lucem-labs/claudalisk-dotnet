#!/usr/bin/env node
import { appendFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const RELEASE_TAG = /^v((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?)$/;
const USAGE = 'Usage: node scripts/release-version.mjs <tag>';

export function versionFromTag(tag) {
  const match = RELEASE_TAG.exec(tag ?? '');
  if (!match) throw new Error(`Not a release tag: ${tag}. Expected v<major>.<minor>.<patch> with an optional prerelease suffix, such as v0.1.0-alpha.1.\n${USAGE}`);
  return match[1];
}

export function main(argv, { out, err, env, append }) {
  let version;
  try {
    if (argv.length !== 1) throw new Error(USAGE);
    version = versionFromTag(argv[0]);
  } catch (error) {
    err(error.message);
    return 2;
  }
  if (env.GITHUB_OUTPUT) append(env.GITHUB_OUTPUT, `version=${version}\n`);
  out(version);
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2), {
    out: (text) => console.log(text),
    err: (text) => console.error(text),
    env: process.env,
    append: (path, text) => appendFileSync(path, text),
  });
}
