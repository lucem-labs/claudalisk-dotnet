#!/usr/bin/env node
import { readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const USAGE = 'Usage: node scripts/require-packages.mjs <folder>';

export const packagesIn = (names) => names.filter((name) => name.endsWith('.nupkg')).sort();

export function main(argv, { list, out, err }) {
  if (argv.length !== 1) {
    err(USAGE);
    return 2;
  }
  let names;
  try {
    names = list(argv[0]);
  } catch (error) {
    err(`Cannot read ${argv[0]}: ${error.message}`);
    return 1;
  }
  const packages = packagesIn(names);
  if (packages.length === 0) {
    err(`No packages in ${argv[0]}. Make a package packable once it has content, then tag again.`);
    return 1;
  }
  for (const name of packages) out(name);
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2), {
    list: (folder) => readdirSync(folder),
    out: (text) => console.log(text),
    err: (text) => console.error(text),
  });
}
