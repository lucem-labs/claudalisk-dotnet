const NUMERIC = /^\d+$/;

export function parseVersion(version) {
  const [core, ...rest] = version.split('-');
  const [major, minor, patch] = core.split('.').map(Number);
  const prerelease = rest.length ? rest.join('-').split('.') : [];
  return { major, minor, patch, prerelease };
}

function compareIdentifiers(left, right) {
  const leftNumeric = NUMERIC.test(left);
  const rightNumeric = NUMERIC.test(right);
  if (leftNumeric && rightNumeric) return Math.sign(Number(left) - Number(right));
  if (leftNumeric) return -1;
  if (rightNumeric) return 1;
  return left < right ? -1 : left > right ? 1 : 0;
}

export function compareVersions(left, right) {
  const a = parseVersion(left);
  const b = parseVersion(right);
  for (const part of ['major', 'minor', 'patch']) {
    if (a[part] !== b[part]) return Math.sign(a[part] - b[part]);
  }
  if (!a.prerelease.length || !b.prerelease.length) return Math.sign(b.prerelease.length - a.prerelease.length);
  for (let index = 0; index < Math.min(a.prerelease.length, b.prerelease.length); index += 1) {
    const order = compareIdentifiers(a.prerelease[index], b.prerelease[index]);
    if (order !== 0) return order;
  }
  return Math.sign(a.prerelease.length - b.prerelease.length);
}

export function isPrerelease(version) {
  return parseVersion(version).prerelease.length > 0;
}

export function isBreakingBump(from, to) {
  const a = parseVersion(from);
  const b = parseVersion(to);
  if (b.major !== a.major) return b.major > a.major;
  return a.major === 0 && b.minor > a.minor;
}
