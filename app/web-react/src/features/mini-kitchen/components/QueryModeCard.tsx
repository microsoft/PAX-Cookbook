import type { QueryMode } from '../types';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import {
  DashboardReqBadge,
  DashboardReqTag,
  BOTH_DASHBOARD_SCOPES,
  type DashboardScope,
} from './DashboardRequirement';

export interface QueryModeSelection {
  mode: QueryMode;
  includeUserInfo: boolean;
}

interface QueryModeCardProps {
  value: QueryModeSelection;
  onChange: (next: QueryModeSelection) => void;
}

type CardKey = 'combined' | 'audit-only' | 'entra-only';

const OPTIONS: ReadonlyArray<{
  key: CardKey;
  title: string;
  desc: string;
  dashboardScopes?: readonly DashboardScope[];
}> = [
  {
    key: 'combined',
    title: 'Audit activity + user info',
    desc: 'Pull Microsoft Graph audit activity for the date range and append Entra user info for everyone in the result. Adds -IncludeUserInfo.',
    dashboardScopes: BOTH_DASHBOARD_SCOPES,
  },
  {
    key: 'audit-only',
    title: 'Audit activity only',
    desc: 'Pull Microsoft Graph audit activity for the date range. No Entra user info is fetched.',
  },
  {
    key: 'entra-only',
    title: 'Entra user info only',
    desc: 'Skip the audit query and only export Entra user / org info. Adds -OnlyUserInfo and hides audit-only sections below.',
    dashboardScopes: ['entra-user-info'],
  },
];

function keyFor(value: QueryModeSelection): CardKey {
  if (value.mode === 'user-info-only') return 'entra-only';
  return value.includeUserInfo ? 'combined' : 'audit-only';
}

function selectionFor(key: CardKey): QueryModeSelection {
  if (key === 'entra-only') return { mode: 'user-info-only', includeUserInfo: false };
  if (key === 'combined') return { mode: 'audit-query', includeUserInfo: true };
  return { mode: 'audit-query', includeUserInfo: false };
}

export function QueryModeCard({ value, onChange }: QueryModeCardProps) {
  const selectedKey = keyFor(value);
  return (
    <MiniKitchenSectionCard
      id="mk-query-mode"
      title="Choose data scope"
      subtitle="Pick the high-level shape. Other cards in this step adapt to your choice."
      helpText="PAX Cookbook does not run the query. It only composes the command and the recipe draft."
      titleBadge={<DashboardReqBadge scopes={['all-runs', ...BOTH_DASHBOARD_SCOPES, 'entra-user-info']} />}
    >
      <div className="mk-radio-cards" role="radiogroup" aria-label="Data scope">
        {OPTIONS.map(opt => {
          const inputId = `mk-query-mode-${opt.key}`;
          const selected = selectedKey === opt.key;
          return (
            <label
              key={opt.key}
              htmlFor={inputId}
              className={'mk-radio-card' + (selected ? ' mk-radio-card--selected' : '')}
            >
              <input
                type="radio"
                id={inputId}
                name="mk-query-mode"
                value={opt.key}
                className="mk-radio-card__input"
                checked={selected}
                onChange={() => onChange(selectionFor(opt.key))}
              />
              <span className="mk-radio-card__title">{opt.title}</span>
              <span className="mk-radio-card__pillrow">
                {opt.dashboardScopes ? (
                  <DashboardReqTag scopes={opt.dashboardScopes} />
                ) : null}
              </span>
              <span className="mk-radio-card__desc">{opt.desc}</span>
            </label>
          );
        })}
      </div>
    </MiniKitchenSectionCard>
  );
}
