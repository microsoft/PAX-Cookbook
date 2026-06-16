/**
 * Pure transforms for the recipe builder's Schedule card.
 *
 * The card lets a user configure a recurring scheduled run for a saved recipe.
 * Its draft state is held separately from the recipe body: the recipe
 * create/update body never carries a schedule block, so saving a recipe can
 * never register (or claim) a schedule on its own. The schedule is registered
 * or removed only by the broker's PUT/DELETE `…/scheduled-task` routes, which
 * both write the recipe's schedule block and register/unregister the per-user
 * Windows task. This separation is the no-drift rule: a recipe is never marked
 * scheduled without a matching OS task.
 *
 * This module is pure. It never fetches, never touches storage, never reads a
 * credential store, and never constructs or runs a command. It only carries a
 * recurrence (frequency + time + weekdays) — never a secret.
 */

import type {
  ScheduledTaskRecurrence,
  ScheduledTaskSchedule,
} from '../../../host/brokerBridge';

/** Frequency of a scheduled run. */
export type ScheduleKind = 'daily' | 'weekly';

/**
 * The editable schedule the card owns. `daysOfWeek` (0 = Sun … 6 = Sat) is
 * only meaningful for a weekly recurrence; it is kept normalized (deduped,
 * sorted, in range) by the apply helpers below.
 */
export interface ScheduleDraft {
  enabled: boolean;
  kind: ScheduleKind;
  /** Hour of day, 0-23. */
  hour: number;
  /** Minute of hour, 0-59. */
  minute: number;
  /** Weekdays (0-6) for a weekly recurrence; ignored for daily. */
  daysOfWeek: number[];
}

/** A fresh, disabled schedule draft (9:00 AM daily by default). */
export function defaultScheduleDraft(): ScheduleDraft {
  return { enabled: false, kind: 'daily', hour: 9, minute: 0, daysOfWeek: [] };
}

/** What the save-flow should do with the schedule once the recipe is saved. */
export type ScheduleAction = 'put' | 'delete' | 'none';

// -----------------------------------------------------------------------------
// internals
// -----------------------------------------------------------------------------

function clampInt(value: number, lo: number, hi: number): number {
  if (!Number.isFinite(value)) {
    return lo;
  }
  const n = Math.trunc(value);
  if (n < lo) return lo;
  if (n > hi) return hi;
  return n;
}

/** Dedupe, range-filter (0-6), and sort a list of weekday indices. */
function normalizeDays(days: readonly number[]): number[] {
  const seen = new Set<number>();
  for (const raw of days) {
    if (!Number.isFinite(raw)) {
      continue;
    }
    const d = Math.trunc(raw);
    if (d >= 0 && d <= 6) {
      seen.add(d);
    }
  }
  return Array.from(seen).sort((a, b) => a - b);
}

// -----------------------------------------------------------------------------
// apply helpers — each returns a new ScheduleDraft (never mutates `prev`)
// -----------------------------------------------------------------------------

export function applyScheduleEnabled(prev: ScheduleDraft, enabled: boolean): ScheduleDraft {
  return { ...prev, enabled: Boolean(enabled) };
}

export function applyScheduleKind(prev: ScheduleDraft, kind: ScheduleKind): ScheduleDraft {
  return { ...prev, kind: kind === 'weekly' ? 'weekly' : 'daily' };
}

export function applyScheduleHour(prev: ScheduleDraft, hour: number): ScheduleDraft {
  return { ...prev, hour: clampInt(hour, 0, 23) };
}

export function applyScheduleMinute(prev: ScheduleDraft, minute: number): ScheduleDraft {
  return { ...prev, minute: clampInt(minute, 0, 59) };
}

/**
 * Toggle a single weekday on/off, keeping `daysOfWeek` normalized. A weekday
 * index outside 0-6 is not a real day, so toggling it is a no-op (the existing
 * selection is returned, normalized).
 */
export function applyScheduleDayToggle(prev: ScheduleDraft, day: number): ScheduleDraft {
  if (!Number.isFinite(day)) {
    return { ...prev, daysOfWeek: normalizeDays(prev.daysOfWeek) };
  }
  const d = Math.trunc(day);
  if (d < 0 || d > 6) {
    return { ...prev, daysOfWeek: normalizeDays(prev.daysOfWeek) };
  }
  const has = prev.daysOfWeek.includes(d);
  const next = has
    ? prev.daysOfWeek.filter(x => x !== d)
    : [...prev.daysOfWeek, d];
  return { ...prev, daysOfWeek: normalizeDays(next) };
}

// -----------------------------------------------------------------------------
// server <-> draft mapping
// -----------------------------------------------------------------------------

/**
 * Build the recurrence payload for PUT `…/scheduled-task`. A daily recurrence
 * omits `daysOfWeek`; a weekly recurrence carries the normalized weekday list.
 */
export function scheduleToRecurrence(draft: ScheduleDraft): ScheduledTaskRecurrence {
  const hour = clampInt(draft.hour, 0, 23);
  const minute = clampInt(draft.minute, 0, 59);
  if (draft.kind === 'weekly') {
    return { kind: 'weekly', hour, minute, daysOfWeek: normalizeDays(draft.daysOfWeek) };
  }
  return { kind: 'daily', hour, minute };
}

/**
 * Project a schedule block echoed by the scheduled-task routes back into a
 * draft. A `null`/absent schedule yields the disabled default.
 */
export function scheduleFromResponse(
  schedule: ScheduledTaskSchedule | null | undefined,
): ScheduleDraft {
  if (!schedule || !schedule.recurrence) {
    return defaultScheduleDraft();
  }
  const rec = schedule.recurrence;
  const kind: ScheduleKind = rec.kind === 'weekly' ? 'weekly' : 'daily';
  return {
    enabled: Boolean(schedule.enabled),
    kind,
    hour: clampInt(rec.hour, 0, 23),
    minute: clampInt(rec.minute, 0, 59),
    daysOfWeek: kind === 'weekly' ? normalizeDays(rec.daysOfWeek ?? []) : [],
  };
}

// -----------------------------------------------------------------------------
// validation + description
// -----------------------------------------------------------------------------

export interface ScheduleValidity {
  ok: boolean;
  /** A short reason when `ok` is false, else `null`. */
  reason: string | null;
}

/**
 * Whether a draft is a registrable recurrence: hour/minute in range and, for a
 * weekly recurrence, at least one weekday selected.
 */
export function isScheduleValid(draft: ScheduleDraft): ScheduleValidity {
  if (draft.hour < 0 || draft.hour > 23) {
    return { ok: false, reason: 'Hour must be between 0 and 23.' };
  }
  if (draft.minute < 0 || draft.minute > 59) {
    return { ok: false, reason: 'Minute must be between 0 and 59.' };
  }
  if (draft.kind === 'weekly' && normalizeDays(draft.daysOfWeek).length === 0) {
    return { ok: false, reason: 'Pick at least one day of the week for a weekly schedule.' };
  }
  return { ok: true, reason: null };
}

const DAY_SHORT_NAMES: ReadonlyArray<string> = [
  'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat',
];

function pad2(n: number): string {
  return n < 10 ? '0' + n : String(n);
}

/**
 * A human-readable summary of the recurrence, e.g. "Daily at 09:05" or
 * "Weekly Mon, Wed, Fri at 14:30". The minute is zero-padded and weekdays are
 * rendered in Sun-first order.
 */
export function describeSchedule(draft: ScheduleDraft): string {
  const time = pad2(clampInt(draft.hour, 0, 23)) + ':' + pad2(clampInt(draft.minute, 0, 59));
  if (draft.kind === 'weekly') {
    const days = normalizeDays(draft.daysOfWeek);
    const names = days.length > 0
      ? days.map(d => DAY_SHORT_NAMES[d]).join(', ')
      : 'no days';
    return `Weekly ${names} at ${time}`;
  }
  return `Daily at ${time}`;
}

// -----------------------------------------------------------------------------
// save-flow reconcile decision (pure)
// -----------------------------------------------------------------------------

/**
 * Decide what the save flow should do with the OS schedule once the recipe
 * itself has been saved, given whether the recipe currently has a registered
 * schedule (`prevHadSchedule`) and the user's draft:
 *
 *   - enabled + valid recurrence          → 'put'    (register/replace)
 *   - disabled, but a schedule existed     → 'delete' (turn it off)
 *   - disabled and none existed            → 'none'   (no-op)
 *   - enabled but an invalid recurrence    → 'none'   (cannot register)
 *
 * The bound-Chef's-Key requirement is enforced separately by the card (the
 * enable toggle is disabled without one) and by the broker (409). It is not
 * part of this decision: the recurrence shape alone determines the action.
 */
export function decideScheduleAction(
  prevHadSchedule: boolean,
  draft: ScheduleDraft,
): ScheduleAction {
  if (draft.enabled) {
    return isScheduleValid(draft).ok ? 'put' : 'none';
  }
  return prevHadSchedule ? 'delete' : 'none';
}
