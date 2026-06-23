import type { ReactNode } from 'react';

interface MiniKitchenSectionCardProps {
  title: string;
  subtitle?: string;
  helpText?: string;
  disabled?: boolean;
  /** Optional id used to anchor labels / aria-labelledby relationships. */
  id?: string;
  /** Optional badge shown to the right of the title (e.g. "PowerShell 7 only"). */
  badge?: string;
  /** Optional rich badge node shown to the right of the title (e.g. dashboard-required marker). */
  titleBadge?: ReactNode;
  /** When true, shows an "optional" tag next to the title. */
  optional?: boolean;
  /** When true, the whole card collapses/expands from its header. */
  collapsible?: boolean;
  /** Initial open state for a collapsible card. Defaults to open. */
  defaultOpen?: boolean;
  children: ReactNode;
}

/**
 * Wrapper card used by every Mini-Kitchen builder section. Provides
 * consistent heading, subtitle, help text, and disabled framing. Composition
 * only — owns no recipe state.
 */
export function MiniKitchenSectionCard({
  title,
  subtitle,
  helpText,
  disabled = false,
  id,
  badge,
  titleBadge,
  optional = false,
  collapsible = false,
  defaultOpen = true,
  children,
}: MiniKitchenSectionCardProps) {
  const headingId = id ? `${id}-title` : undefined;
  const titleRow = (
    <div className="mk-card__title-row">
      <h2 id={headingId} className="mk-card__title">
        {title}
      </h2>
      {badge ? <span className="mk-card__badge">{badge}</span> : null}
      {optional ? <span className="mk-field__optional">optional</span> : null}
      {titleBadge ?? null}
      {collapsible ? (
        <span className="mk-card__chevron" aria-hidden="true" />
      ) : null}
    </div>
  );

  if (collapsible) {
    return (
      <details
        className={`mk-card mk-card--collapsible${disabled ? ' mk-card--disabled' : ''}`}
        open={defaultOpen}
        aria-disabled={disabled || undefined}
      >
        <summary className="mk-card__head mk-card__head--summary">{titleRow}</summary>
        <div className="mk-card__body">
          {subtitle ? <p className="mk-card__subtitle">{subtitle}</p> : null}
          {helpText ? <p className="mk-card__help">{helpText}</p> : null}
          {children}
        </div>
      </details>
    );
  }

  return (
    <section
      className={`mk-card${disabled ? ' mk-card--disabled' : ''}`}
      aria-labelledby={headingId}
      aria-disabled={disabled || undefined}
    >
      <header className="mk-card__head">
        {titleRow}
        {subtitle ? <p className="mk-card__subtitle">{subtitle}</p> : null}
        {helpText ? <p className="mk-card__help">{helpText}</p> : null}
      </header>
      <div className="mk-card__body">{children}</div>
    </section>
  );
}
