import type { WizardStepStatus } from '../components/WizardRail';
import type { DateRangeMode } from '../types';

/**
 * Inputs the wizard rail needs to render a real per-step verdict. All values
 * are read straight from the builder's existing derived state; nothing here
 * changes save, bake, or schedule behavior — it only describes what each step
 * currently looks like to the operator.
 */
export interface WizardStepStatusInputs {
  /**
   * The steps that own an unmet save requirement — the error set. This is the
   * same set the Review overview uses (derived from the save requirements via
   * STEP_FOR_REQUIREMENT), so the rail and the Review overview always agree. No
   * save requirement maps to the optional Schedule step, so it is never present
   * here.
   */
  stepsNeedingAttention: ReadonlySet<number>;
  /** The recipe name as typed (trimmed internally). */
  recipeName: string;
  /** True for a user-info-only pull, where the audit date range is not used. */
  isUserInfoOnly: boolean;
  /**
   * Audit date-range mode. `'previous-day'` deliberately omits both dates and is
   * a complete, valid configuration; `undefined`/`'custom'` use the date inputs.
   */
  dateMode?: DateRangeMode;
  /** Trimmed start / end dates ('' when unset). */
  startDate: string;
  endDate: string;
  /** The resolved output folder, or the literal 'Not set' when none is chosen. */
  outputPath: string;
  /** True when a schedule is registered on the server or enabled in the draft. */
  scheduleConfigured: boolean;
  /** True when the recipe is complete enough for the broker to save it. */
  candidateSaveable: boolean;
}

/**
 * Pure per-step rail verdict for the recipe builder wizard.
 *
 *  - 'error'      the step owns an unmet save requirement, or (Date Range only)
 *                 a start/end pair the broker would reject (end on or before
 *                 start). The broker rejects end == start, so this mirrors that
 *                 rule rather than inventing a client-only one.
 *  - 'valid'      no issue and the step's key detail is set.
 *  - 'incomplete' no issue but nothing is set yet (a neutral, non-alarming ring).
 *
 * Step 6 (Schedule) is optional and is NEVER 'error' for being unconfigured —
 * no save requirement maps to it, so `stepsNeedingAttention` never contains 6,
 * and an empty schedule simply reads 'incomplete'. Step 7 (Review) is 'valid'
 * once the recipe is saveable.
 */
export function computeWizardStepStatus(
  step: number,
  inputs: WizardStepStatusInputs,
): WizardStepStatus {
  const {
    stepsNeedingAttention,
    recipeName,
    isUserInfoOnly,
    dateMode,
    startDate,
    endDate,
    outputPath,
    scheduleConfigured,
    candidateSaveable,
  } = inputs;

  // A step that owns an outstanding save requirement is always an error — the
  // same verdict the Review overview shows. Schedule (6) never appears here.
  if (stepsNeedingAttention.has(step)) {
    return 'error';
  }

  switch (step) {
    case 1: // Basics — keyed on the recipe name.
      return recipeName.trim().length > 0 ? 'valid' : 'incomplete';
    case 2: // Authentication — a sign-in method is always chosen.
      return 'valid';
    case 3: // Date Range.
      if (isUserInfoOnly) {
        return 'valid';
      }
      // Previous-day mode deliberately omits both dates and is a complete,
      // ready configuration (PAX queries the previous full UTC day).
      if (dateMode === 'previous-day') {
        return 'valid';
      }
      // The end date must be after the start date; end == start is rejected by
      // the broker. That is not a save requirement, so it is flagged here so the
      // step never reads 'valid' for a range the broker would reject.
      if (startDate && endDate && endDate <= startDate) {
        return 'error';
      }
      return startDate && endDate ? 'valid' : 'incomplete';
    case 4: // Audit Operations — a query mode is always selected.
      return 'valid';
    case 5: // Output — keyed on the destination folder.
      return outputPath !== 'Not set' ? 'valid' : 'incomplete';
    case 6: // Schedule (optional) — never an error for being unconfigured.
      return scheduleConfigured ? 'valid' : 'incomplete';
    case 7: // Review + Save.
      return candidateSaveable ? 'valid' : 'incomplete';
    default:
      return 'incomplete';
  }
}
