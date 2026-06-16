import type { ReactNode } from 'react';

interface BuilderStepProps {
  stepNumber: number;
  title: string;
  description?: string;
  summary?: ReactNode;
  open: boolean;
  onToggle: () => void;
  children: ReactNode;
  id?: string;
}

/**
 * Collapsible numbered step used by the single-flow Mini-Kitchen page.
 * Header is a button (aria-expanded + aria-controls); body is a region
 * (role=region + aria-labelledby). When closed, the optional `summary`
 * slot surfaces the user's current selections so they can see what each
 * step holds without expanding it. D20 / R23: warnings, assumptions,
 * permissions, and blocked-items panels live OUTSIDE this wrapper so
 * they are never default-collapsed.
 */
export function BuilderStep(props: BuilderStepProps) {
  const { stepNumber, title, description, summary, open, onToggle, children, id } = props;

  const slug = id ?? `mk-step-${stepNumber}`;
  const headId = `${slug}-head`;
  const bodyId = `${slug}-body`;

  return (
    <section
      className={`mk-step${open ? ' mk-step--open' : ''}`}
      aria-labelledby={headId}
    >
      <button
        type="button"
        id={headId}
        className="mk-step__head"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={onToggle}
      >
        <span className="mk-step__number" aria-hidden="true">
          {stepNumber}
        </span>
        <span className="mk-step__title-block">
          <span className="mk-step__title">{title}</span>
          {description ? (
            <span className="mk-step__description">{description}</span>
          ) : null}
        </span>
        {summary ? (
          <span
            className={`mk-step__summary${open ? ' mk-step__summary--open' : ''}`}
          >
            {summary}
          </span>
        ) : null}
        <span className="mk-step__chevron" aria-hidden="true">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none">
            <path
              d="M6 9l6 6 6-6"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        </span>
      </button>
      <div
        id={bodyId}
        className="mk-step__body"
        role="region"
        aria-labelledby={headId}
        hidden={!open}
      >
        <div className="mk-step__body-inner">{children}</div>
      </div>
    </section>
  );
}
