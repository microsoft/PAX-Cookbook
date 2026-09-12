import { describe, expect, it } from 'vitest';
import {
  computeBakeBlockReason,
  isReadinessBakeConfirmable,
  type BakeGateReadiness,
  type BakeGateInputs,
} from './bakeGate';

// The bake gate decides whether the confirmation modal may open and whether
// Confirm Bake is enabled. A manual bake now proceeds on the Unlocked broker
// session plus explicit confirmation (there is NO per-operation identity
// ceremony), so ONLY a FRESH readiness result that reports ready with an
// acquired engine and a ready sign-in is confirmable. These tests exercise the
// invalid-recipe states directly (no screenshots, no source-text assertions).

function ready(overrides: Partial<BakeGateReadiness> = {}): BakeGateReadiness {
  return {
    status: 'ready',
    engine: { isAcquired: true },
    auth: { ready: true },
    ...overrides,
  };
}

describe('isReadinessBakeConfirmable', () => {
  it('null readiness (never fetched / stale badge) is NOT confirmable', () => {
    expect(isReadinessBakeConfirmable(null)).toBe(false);
  });

  it('ready + engine acquired + auth ready is confirmable', () => {
    expect(isReadinessBakeConfirmable(ready())).toBe(true);
  });

  it('a non-ready status is NOT confirmable', () => {
    expect(isReadinessBakeConfirmable(ready({ status: 'blocked' }))).toBe(false);
    expect(isReadinessBakeConfirmable(ready({ status: 'notReady' }))).toBe(false);
    expect(isReadinessBakeConfirmable(ready({ status: '' }))).toBe(false);
  });

  it('an unacquired PAX engine is NOT confirmable', () => {
    expect(isReadinessBakeConfirmable(ready({ engine: { isAcquired: false } }))).toBe(false);
  });

  it('a not-ready sign-in (e.g. keyless App-registration recipe) is NOT confirmable', () => {
    expect(isReadinessBakeConfirmable(ready({ auth: { ready: false } }))).toBe(false);
  });

  it('ready with no engine/auth sections applicable is confirmable', () => {
    expect(isReadinessBakeConfirmable(ready({ engine: null, auth: null }))).toBe(true);
  });
});

describe('computeBakeBlockReason (readiness-aware bake gate)', () => {
  const base: BakeGateInputs = {
    busy: false,
    bakeSubmitting: false,
    savedRecipeId: 'r1',
    isDirty: false,
    candidateSaveable: true,
    saveBlockReason: 'Finish the required details and save before you bake.',
    readinessPhase: 'loaded',
    readiness: ready(),
  };

  it('allows a saved, clean, ready recipe', () => {
    expect(computeBakeBlockReason(base)).toBeNull();
  });

  it('blocks an unsaved recipe', () => {
    expect(computeBakeBlockReason({ ...base, savedRecipeId: null })).not.toBeNull();
  });

  it('blocks a dirty recipe (baking runs the saved recipe)', () => {
    expect(computeBakeBlockReason({ ...base, isDirty: true })).not.toBeNull();
  });

  it('blocks while a start is in flight', () => {
    expect(computeBakeBlockReason({ ...base, bakeSubmitting: true })).not.toBeNull();
  });

  it('blocks when a loaded readiness reports not ready', () => {
    expect(
      computeBakeBlockReason({ ...base, readiness: ready({ status: 'blocked' }) }),
    ).not.toBeNull();
  });

  it('blocks when the PAX engine is not acquired', () => {
    expect(
      computeBakeBlockReason({ ...base, readiness: ready({ engine: { isAcquired: false } }) }),
    ).not.toBeNull();
  });

  it('blocks when the recipe sign-in is not ready', () => {
    expect(
      computeBakeBlockReason({ ...base, readiness: ready({ auth: { ready: false } }) }),
    ).not.toBeNull();
  });
});
