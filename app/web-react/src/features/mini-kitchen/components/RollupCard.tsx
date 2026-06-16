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
    desc: 'Run the PAX rollup post-processor in place of the raw rows. PAX auto-installs Python 3.10+ and orjson on first use.',
    dashboardOneOf: true,
  },
  {
    id: 'rollup-plus-raw',
    title: 'Rollup + raw',
    desc: 'Emit both the rollup output and the raw audit rows. PAX auto-installs Python 3.10+ and orjson on first use.',
    dashboardOneOf: true,
  },
];

/**
 * Phase 5: Rollup card. Three-way radio for the rollup mode. Both
 * non-`none` choices surface a runtime callout: PAX auto-installs its
 * Python helper on first use and Mini-Kitchen cannot inspect the runtime
 * from the browser.
 */
export function RollupCard({ value, disabled = false, onChange }: RollupCardProps) {
  const showRuntimeCallout = value !== 'none';
  return (
    <MiniKitchenSectionCard
      id="mk-rollup"
      title="Rollup"
      subtitle="Post-process the audit-query rows into a higher-level rollup."
      helpText="PAX Cookbook does not inspect the runtime. PAX bootstraps the Python rollup helper on first use."
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
      {showRuntimeCallout ? (
        <p className="mk-callout mk-callout--info">
          PAX auto-installs Python 3.10+ and the optional orjson package on first use —
          PAX Cookbook cannot verify the runtime.
        </p>
      ) : null}
    </MiniKitchenSectionCard>
  );
}
