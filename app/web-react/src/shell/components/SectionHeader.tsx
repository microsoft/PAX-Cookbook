/**
 * Shared section header.
 *
 * One consistent title + contextual-help treatment for every product surface
 * (Home, Recipes, Pantry, Bakes, Taste Tests, Chef's Keys, Settings, Updates).
 * The same markup and styling are reused everywhere so the heading scale, the
 * accent rule, and the inline "?" help button line up across sections.
 *
 * An optional `art` slot carries decorative illustration on the polished
 * surfaces (Home and Recipes) without changing the title treatment.
 */
import type { CSSProperties, ReactNode } from 'react';
import { ContextualHelpButton } from '../../components/ContextualHelpButton';
import type { ContextualHelpTopicId } from '../../data/contextual-help';

interface SectionHeaderProps {
  title: ReactNode;
  lede?: ReactNode;
  helpTopic?: ContextualHelpTopicId;
  /** Token reference (e.g. var(--c-blue)) used for the accent rule. */
  accent?: string;
  /** Optional decorative art rendered at the trailing edge of the header. */
  art?: ReactNode;
  /** Optional interactive actions (e.g. a button) rendered top-right of the title row. */
  actions?: ReactNode;
  /** Heading level. Home/Recipes use h1; nested placeholder views use h2. */
  headingLevel?: 'h1' | 'h2';
  /** Optional id applied to the heading for aria-labelledby wiring. */
  titleId?: string;
}

export function SectionHeader({
  title,
  lede,
  helpTopic,
  accent,
  art,
  actions,
  headingLevel = 'h2',
  titleId,
}: SectionHeaderProps) {
  const Heading = headingLevel;
  return (
    <header
      className="view-head"
      style={accent ? ({ ['--accent' as string]: accent } as CSSProperties) : undefined}
    >
      <div className="view-head__row">
        <div className="view-head__text">
          <Heading className="view-head__title" id={titleId}>
            {title}
            {helpTopic ? <ContextualHelpButton topic={helpTopic} /> : null}
          </Heading>
          {lede ? <p className="view-head__lede">{lede}</p> : null}
        </div>
        {actions ? <div className="view-head__actions">{actions}</div> : null}
        {art ? <div className="view-head__art">{art}</div> : null}
      </div>
    </header>
  );
}
