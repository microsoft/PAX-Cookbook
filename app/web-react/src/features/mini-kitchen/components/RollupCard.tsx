import type { ReactNode } from 'react';
import type { RollupMode } from '../types';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import {
  DashboardReqBadge,
  DashboardReqTag,
  BOTH_DASHBOARD_SCOPES,
} from './DashboardRequirement';

interface RollupCardProps {
  value: RollupMode;
  disabled?: boolean;
  onChange: (next: RollupMode) => void;
  /** Optional nested content (the Dashboard target subsection). */
  children?: ReactNode;
}

const OPTIONS: ReadonlyArray<{
  id: RollupMode;
  title: string;
  desc: string;
  dashboardOneOf?: boolean;
}> = [
  {
    id: 'none',
    title: 'No rollup',
    desc: 'Emit raw audit rows only. No post-processing.',
  },
  {
    id: 'rollup',
    title: 'Rollup only',
    desc: 'Run the PAX rollup post-processor in place of the raw rows.',
    dashboardOneOf: true,
  },
  {
    id: 'rollup-plus-raw',
    title: 'Rollup + raw',
    desc: 'Emit both the rollup output and the raw audit rows.',
    dashboardOneOf: true,
  },
];

/**
 * Phase 5: Rollup card. Three-way radio for the rollup mode. Both
 * non-`none` choices surface a runtime callout: PAX auto-installs its
 * Python helper on first use and Mini-Kitchen cannot inspect the runtime
 * from the browser.
 */
export function RollupCard({ value, disabled = false, onChange, children }: RollupCardProps) {
  return (
    <MiniKitchenSectionCard
      id="mk-rollup"
      title="Rollup"
      subtitle="Post-process the audit-query rows into a higher-level rollup."
      helpText="Turn raw audit rows into the higher-level totals a dashboard expects. PAX Cookbook handles everything needed to produce the rollup automatically."
      disabled={disabled}
      titleBadge={<DashboardReqBadge scopes={BOTH_DASHBOARD_SCOPES} />}
    >
      {disabled ? (
        <p className="mk-field__note" role="note">
          Rollup is turned off because you chose User info only, which produces a
          user list, not rollup totals.
        </p>
      ) : null}
      <div
        className="mk-radio-cards"
        role="radiogroup"
        aria-label="Rollup mode"
      >
        {OPTIONS.map(opt => {
          const inputId = `mk-rollup-${opt.id}`;
          const selected = value === opt.id;
          return (
            <label
              key={opt.id}
              htmlFor={inputId}
              className={'mk-radio-card' + (selected ? ' mk-radio-card--selected' : '')}
            >
              <input
                type="radio"
                id={inputId}
                name="mk-rollup"
                value={opt.id}
                className="mk-radio-card__input"
                checked={selected}
                disabled={disabled}
                onChange={() => onChange(opt.id)}
              />
              <span className="mk-radio-card__title">
                {opt.title}
                {opt.dashboardOneOf ? (
                  <DashboardReqTag scopes={BOTH_DASHBOARD_SCOPES} />
                ) : null}
              </span>
              <span className="mk-radio-card__desc">{opt.desc}</span>
            </label>
          );
        })}
      </div>
      {children}
    </MiniKitchenSectionCard>
  );
}
