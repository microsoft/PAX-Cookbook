/**
 * Version compatibility checks between a selected PAX version and a switch
 * usage list, against the static switch catalog.
 *
 * Phase 2 ships the compare + assessment functions; the UI in later phases
 * decides how to surface the issues.
 */

import type { PaxSwitchDefinition } from '../types';

export type VersionIssueSeverity = 'info' | 'warning' | 'error';

export interface VersionCompatibilityIssue {
  switchName: string;
  severity: VersionIssueSeverity;
  message: string;
}

/**
 * Compare two semver-lite strings (`major.minor.patch[...]`). Missing segments
 * are treated as 0. Non-numeric segments are coerced to 0; Mini-Kitchen does
 * not currently surface prerelease comparisons.
 *
 * Returns `-1` if `a < b`, `0` if equal, `1` if `a > b`.
 */
export function compareVersions(a: string, b: string): number {
  const segs = (v: string): number[] => v.split('.').map(p => Number.parseInt(p, 10) || 0);
  const aa = segs(a);
  const bb = segs(b);
  const len = Math.max(aa.length, bb.length);
  for (let i = 0; i < len; i++) {
    const av = aa[i] ?? 0;
    const bv = bb[i] ?? 0;
    if (av < bv) return -1;
    if (av > bv) return 1;
  }
  return 0;
}

/** Convenience: `a >= b`. */
export function versionAtLeast(a: string, b: string): boolean {
  return compareVersions(a, b) >= 0;
}

/** Convenience: `a < b`. */
export function versionLessThan(a: string, b: string): boolean {
  return compareVersions(a, b) < 0;
}

/**
 * Walk a list of switch names used in the recipe and flag any whose
 * `since` / `until` bounds disagree with the target PAX version.
 *
 * Unknown switch names are skipped (the advanced-args analyzer reports
 * those separately).
 */
export function assessVersionCompatibility(
  targetVersion: string,
  catalog: readonly PaxSwitchDefinition[],
  usedSwitches: readonly string[],
): readonly VersionCompatibilityIssue[] {
  const issues: VersionCompatibilityIssue[] = [];
  for (const name of usedSwitches) {
    const def = catalog.find(s => s.name === name);
    if (!def) {
      continue;
    }
    if (def.since && versionLessThan(targetVersion, def.since)) {
      issues.push({
        switchName: name,
        severity: 'error',
        message: `-${name} requires PAX ${def.since} or newer; selected version is ${targetVersion}.`,
      });
    }
    if (def.until && versionAtLeast(targetVersion, def.until)) {
      issues.push({
        switchName: name,
        severity: 'error',
        message: `-${name} was removed in PAX ${def.until}; selected version is ${targetVersion}.`,
      });
    }
  }
  return issues;
}
