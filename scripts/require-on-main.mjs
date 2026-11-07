#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const USAGE = 'Usage: node scripts/require-on-main.mjs <commit>';
const MAIN = 'origin/main';

export function main(argv, { run, err }) {
  if (argv.length !== 1 || !argv[0]) {
    err(USAGE);
    return 2;
  }
  const commit = argv[0];
  const { status } = run('git', ['merge-base', '--is-ancestor', commit, MAIN]);
  if (status === 0) return 0;
  if (status === 1) {
    err(`${commit} is not on ${MAIN}. Tag a commit on main to release.`);
    return 1;
  }
  err(`Cannot check whether ${commit} is on ${MAIN}: git merge-base exited with ${status}.`);
  return 1;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2), {
    run: (command, args) => spawnSync(command, args, { stdio: ['ignore', 'inherit', 'inherit'] }),
    err: (text) => console.error(text),
  });
}
