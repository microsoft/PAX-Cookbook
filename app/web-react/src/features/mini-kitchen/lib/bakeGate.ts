// Client-side decision for why the Bake (run) action is unavailable, or null
// when the recipe may bake. Extracted from the builder so the bake gate can be
// reasoned about and validated in isolation. This module is pure: it has no
// imports, holds no state, and never runs PAX.
//
// UXR2 (Issues 5 + 6): Check readiness is an OPTIONAL pre-bake preflight, not a
// required step. A saved, clean, valid recipe can bake without first running
// readiness. The confirmation modal and the broker's own pre-spawn checks
// (engine SHA, Unlocked session, same-recipe-busy, integrity) remain the
// enforcing safety gates and reject an unready bake at start time. If readiness
// HAS been run and reports a problem, baking stays blocked here so a user who
// saw "not ready" cannot bake past it.

export interface BakeGateReadiness {
  status: string;
  engine: { isAcquired: boolean } | null;
  auth: { ready: boolean } | null;
}

export interface BakeGateInputs {
  /** A blocking action is already in flight. */
  busy: boolean;
  /** The single startCook call (and its at-most-once Hello retry) is running. */
  bakeSubmitting: boolean;
  /** The saved recipe id, or null for an unsaved draft. */
  savedRecipeId: string | null;
  /** The in-memory recipe differs from the last saved baseline. */
  isDirty: boolean;
  /** The recipe candidate is complete enough to save. */
  candidateSaveable: boolean;
  /** Fully composed sentence shown when the recipe is not yet saveable. */
  saveBlockReason: string;
  /** Readiness request lifecycle. */
  readinessPhase: 'idle' | 'loading' | 'loaded' | 'error';
  /** The readiness envelope the broker returned (any status), or null. */
  readiness: BakeGateReadiness | null;
}

export function computeBakeBlockReason(inputs: BakeGateInputs): string | null {
  if (inputs.busy) {
    return 'Finish the current action before you bake.';
  }
  if (inputs.bakeSubmitting) {
    return 'Starting this bake…';
  }
  if (inputs.savedRecipeId === null) {
    return 'Save this recipe before you can bake it.';
  }
  if (inputs.isDirty) {
    return 'Save your changes before you bake — baking runs the recipe saved on disk.';
  }
  if (!inputs.candidateSaveable) {
    return inputs.saveBlockReason;
  }

  // Readiness is optional: not having run it does not block a bake. Only a
  // readiness result that is present AND reports a problem blocks here. When
  // readiness was never run, the broker enforces engine/sign-in/Unlocked-session/
  // integrity at bake start, after the confirmation modal.
  const readiness = inputs.readiness;
  if (inputs.readinessPhase === 'loaded' && readiness !== null) {
    if (readiness.status !== 'ready') {
      return 'Readiness check found blockers — resolve them before you bake.';
    }
    if (readiness.engine && !readiness.engine.isAcquired) {
      return 'The PAX engine isn’t ready on this PC yet. Set it up, then bake.';
    }
    if (readiness.auth && !readiness.auth.ready) {
      return 'Finish signing in for this recipe before you bake.';
    }
  }

  return null;
}

// Pure decision: is a readiness result a CONFIRMABLE bake? A manual bake now
// proceeds on the Unlocked session plus explicit confirmation (no per-operation
// identity ceremony), so the confirmation modal must open — and Confirm Bake must
// be enabled — ONLY for a recipe whose FRESH readiness reports ready with an
// acquired engine and a ready sign-in. Any other result, including a null /
// not-yet-fetched readiness, is NOT confirmable. Callers fetch fresh readiness
// immediately before opening the modal and re-check this on confirm, so a stale
// "ready" badge or a keyless App-registration recipe can never reach a
// confirmable Bake state.
export function isReadinessBakeConfirmable(readiness: BakeGateReadiness | null): boolean {
  if (readiness === null) {
    return false;
  }
  if (readiness.status !== 'ready') {
    return false;
  }
  if (readiness.engine !== null && !readiness.engine.isAcquired) {
    return false;
  }
  if (readiness.auth !== null && !readiness.auth.ready) {
    return false;
  }
  return true;
}
