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
  children,
}: MiniKitchenSectionCardProps) {
  const headingId = id ? `${id}-title` : undefined;
  return (
    <section
      className={`mk-card${disabled ? ' mk-card--disabled' : ''}`}
      aria-labelledby={headingId}
      aria-disabled={disabled || undefined}
    >
      <header className="mk-card__head">
        <div className="mk-card__title-row">
          <h2 id={headingId} className="mk-card__title">
            {title}
          </h2>
          {badge ? <span className="mk-card__badge">{badge}</span> : null}
          {titleBadge ?? null}
        </div>
        {subtitle ? <p className="mk-card__subtitle">{subtitle}</p> : null}
        {helpText ? <p className="mk-card__help">{helpText}</p> : null}
      </header>
      <div className="mk-card__body">{children}</div>
    </section>
  );
}
