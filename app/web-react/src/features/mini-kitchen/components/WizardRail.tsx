import type { ReactNode } from 'react';

/**
 * Left-rail wizard navigation for the recipe builder.
 *
 * Presentation-only: it lists the ordered steps as clickable buttons and
 * highlights the active one. Navigation is non-linear — clicking any step jumps
 * straight to it. The component owns no recipe state; the parent passes the step
 * list, the active step, and the click handler.
 *
 * The per-step status indicator slot is structurally present here so later
 * polish can light it up with a real verdict (incomplete circle / valid check /
 * error warning). For now the parent passes a neutral placeholder via
 * `statusFor`; the active step is highlighted independently of its status.
 */
export type WizardStepStatus = 'incomplete' | 'valid' | 'error';

export interface WizardRailStep {
  n: number;
  title: string;
}

export interface WizardRailProps {
  /** The ordered steps to render in the rail. */
  steps: ReadonlyArray<WizardRailStep>;
  /** The 1-based active step. */
  activeStep: number;
  /** Navigate to a step. Non-linear: any step is reachable at any time. */
  onSelect: (n: number) => void;
  /**
   * Per-step status for the indicator slot. UX14.6 wires the real per-step
   * validity; until then the parent passes a neutral 'incomplete' placeholder so
   * the three visual states stay structurally present without claiming a verdict.
   */
  statusFor?: (n: number) => WizardStepStatus;
  /**
   * Optional short tag rendered beside a step's title (for example "Optional" on
   * the Schedule step when no schedule is configured). Return null for no tag.
   */
  badgeFor?: (n: number) => string | null;
  /** Optional control rendered in the rail header (for example a help button). */
  headerAccessory?: ReactNode;
  /** Rail heading label. */
  title?: string;
}

const STATUS_GLYPH: Record<WizardStepStatus, string> = {
  incomplete: '',
  valid: '\u2713',
  error: '!',
};

export function WizardRail({
  steps,
  activeStep,
  onSelect,
  statusFor,
  badgeFor,
  headerAccessory,
  title = 'Recipe steps',
}: WizardRailProps) {
  return (
    <nav className="mk-wizard__rail" aria-label="Recipe builder steps">
      <div className="mk-wizard__rail-head">
        <span className="mk-wizard__rail-title">{title}</span>
        {headerAccessory ?? null}
      </div>
      <ol className="mk-wizard__steps">
        {steps.map(step => {
          const isActive = activeStep === step.n;
          const status: WizardStepStatus = statusFor
            ? statusFor(step.n)
            : 'incomplete';
          const badge = badgeFor ? badgeFor(step.n) : null;
          return (
            <li key={step.n}>
              <button
                type="button"
                className={
                  isActive
                    ? 'mk-wizard__step mk-wizard__step--active'
                    : 'mk-wizard__step'
                }
                aria-current={isActive ? 'step' : undefined}
                onClick={() => onSelect(step.n)}
              >
                <span className="mk-wizard__step-index">{step.n}</span>
                <span className="mk-wizard__step-body">
                  <span className="mk-wizard__step-titlerow">
                    <span className="mk-wizard__step-title">{step.title}</span>
                    {badge ? (
                      <span className="mk-wizard__badge">{badge}</span>
                    ) : null}
                  </span>
                </span>
                <span
                  className={`mk-wizard__status mk-wizard__status--${status}`}
                  aria-hidden="true"
                >
                  {STATUS_GLYPH[status]}
                </span>
              </button>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
