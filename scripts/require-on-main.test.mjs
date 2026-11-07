import assert from 'node:assert/strict';
import { test } from 'node:test';
import { main } from './require-on-main.mjs';

const run = (argv, status) => {
  const calls = [];
  const errors = [];
  const code = main(argv, {
    run: (command, args) => {
      calls.push([command, args]);
      return { status };
    },
    err: (text) => errors.push(text),
  });
  return { code, calls, errors };
};

test('asks git whether the commit is an ancestor of origin/main', () => {
  const { code, calls, errors } = run(['abc123'], 0);
  assert.equal(code, 0);
  assert.deepEqual(calls, [['git', ['merge-base', '--is-ancestor', 'abc123', 'origin/main']]]);
  assert.deepEqual(errors, []);
});

test('fails when the commit is not on main', () => {
  const { code, errors } = run(['abc123'], 1);
  assert.equal(code, 1);
  assert.match(errors[0], /abc123 is not on origin\/main/);
});

test('fails when git cannot answer', () => {
  const { code, errors } = run(['abc123'], 128);
  assert.equal(code, 1);
  assert.match(errors[0], /exited with 128/);
});

test('fails when git cannot be started', () => {
  const { code, errors } = run(['abc123'], null);
  assert.equal(code, 1);
  assert.match(errors[0], /exited with null/);
});

test('needs exactly one commit and runs nothing otherwise', () => {
  for (const argv of [[], [''], ['a', 'b']]) {
    const { code, calls, errors } = run(argv, 0);
    assert.equal(code, 2);
    assert.deepEqual(calls, []);
    assert.match(errors[0], /^Usage:/);
  }
});
