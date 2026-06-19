// Maps Save-blocking requirement ids (from `deriveSaveRequirements`) to the
// plain-English field name and the recipe-builder step the field lives on, so
// the "needs a few more details" dialog can name each gap and offer a jump
// straight to the step that fixes it.
//
// The ids mirror `deriveSaveRequirements` in `recipeSaveRequirements.ts`. Step
// numbers mirror `EDITOR_STEPS` in `MiniKitchenBuilderPreview.tsx` (1 Basics,
// 2 Authentication, 3 Date Range, 4 Audit Operations, 5 Output, 6 Schedule,
// 7 Review + Save). Any unmapped id falls back to the Review + Save step, where
// the full requirements checklist is always shown.

import type { SaveRequirement } from './recipeSaveRequirements';

export interface SaveRequirementStep {
  /** Plain-English field name shown to the user. */
  label: string;
  /** 1-based builder step number the field lives on. */
  step: number;
  /** Step title, matching the wizard rail. */
  stepName: string;
}

const REQUIREMENT_STEPS: Record<string, SaveRequirementStep> = {
  name: { label: 'Recipe name', step: 1, stepName: 'Basics' },
  tenantId: { label: 'Tenant ID', step: 2, stepName: 'Authentication' },
  dateRange: { label: 'Date range', step: 3, stepName: 'Date Range' },
  startDate: { label: 'Start date', step: 3, stepName: 'Date Range' },
  endDate: { label: 'End date', step: 3, stepName: 'Date Range' },
  agentIds: { label: 'Audit operations', step: 4, stepName: 'Audit Operations' },
  factOutput: { label: 'Output folder', step: 5, stepName: 'Output' },
  userInfoOutput: { label: 'User info output folder', step: 5, stepName: 'Output' },
};

const REVIEW_STEP = { step: 7, stepName: 'Review + Save' } as const;

/**
 * Resolve a single requirement to its plain-English label and the step that
 * fixes it. Unknown ids keep their built-in label and route to Review + Save.
 */
export function describeRequirementStep(req: SaveRequirement): SaveRequirementStep {
  const mapped = REQUIREMENT_STEPS[req.id];
  if (mapped) {
    return mapped;
  }
  return { label: req.label, step: REVIEW_STEP.step, stepName: REVIEW_STEP.stepName };
}
