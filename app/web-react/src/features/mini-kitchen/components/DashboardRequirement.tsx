/**
 * Dashboard-requirement indicators.
 *
 * Settings that the PAX dashboards (and every PAX run) depend on are flagged
 * with small colored pills so it is obvious on the builder cards which choices
 * matter. There are exactly five pills:
 *   - "All Runs" — required for every PAX run, including all dashboards.
 *   - "AI-in-One Dashboard" — required to feed the AI-in-One dashboard.
 *   - "AI Business Value Dashboard" — required to feed the AI Business Value dashboard.
 *   - "M365 Usage Dashboard" — required to feed the M365 Usage dashboard.
 *   - "Entra User Info Only" — required whenever Entra user info is collected.
 *
 * Each location renders the subset of the pills that applies. Two render
 * helpers share the same vocabulary:
 *   - `DashboardReqBadge` — pills shown next to a card title.
 *   - `DashboardReqTag` — pills shown inline next to a specific option / field.
 */

/** The five canonical dashboard / run requirement pills. */
export type DashboardScope =
  | 'all-runs'
  | 'ai-in-one'
  | 'ai-business-value'
  | 'm365-usage'
  | 'entra-user-info';

const SCOPE_COPY: Record<DashboardScope, { label: string; title: string }> = {
  'all-runs': {
    label: 'All Runs',
    title: 'Required for every PAX run, including all dashboards.',
  },
  'ai-in-one': {
    label: 'AI-in-One Dashboard',
    title: 'Required for the AI-in-One dashboard.',
  },
  'ai-business-value': {
    label: 'AI Business Value Dashboard',
    title: 'Required for the AI Business Value dashboard.',
  },
  'm365-usage': {
    label: 'M365 Usage Dashboard',
    title: 'Required for the M365 Usage dashboard.',
  },
  'entra-user-info': {
    label: 'Entra User Info Only',
    title: 'Required whenever you collect Entra user info.',
  },
};

/** Every-run settings: needed for all runs and therefore all dashboards. */
export const ALL_DASHBOARD_SCOPES: readonly DashboardScope[] = [
  'all-runs',
  'ai-in-one',
  'ai-business-value',
  'm365-usage',
];

/** Settings required specifically to feed the dashboards. */
export const BOTH_DASHBOARD_SCOPES: readonly DashboardScope[] = [
  'ai-in-one',
  'ai-business-value',
  'm365-usage',
];

/**
 * Every-run settings that also feed the Entra user-info pull — used on shared
 * outputs / headers where all pills apply, with the user-info pill last.
 */
export const USER_INFO_RUN_SCOPES: readonly DashboardScope[] = [
  'all-runs',
  'ai-in-one',
  'ai-business-value',
  'm365-usage',
  'entra-user-info',
];

interface DashboardReqProps {
  scopes: readonly DashboardScope[];
}

/** Pills rendered next to a card title. */
export function DashboardReqBadge({ scopes }: DashboardReqProps) {
  return (
    <span className="mk-dash-req-badges">
      {scopes.map(scope => (
        <span
          key={scope}
          className={`mk-dash-req-badge mk-dash-req-badge--${scope}`}
          title={SCOPE_COPY[scope].title}
        >
          {SCOPE_COPY[scope].label}
        </span>
      ))}
    </span>
  );
}

/** Pills rendered inline next to a specific option / toggle / field. */
export function DashboardReqTag({ scopes }: DashboardReqProps) {
  return (
    <span className="mk-dash-req-tags">
      {scopes.map(scope => (
        <span
          key={scope}
          className={`mk-dash-req-tag mk-dash-req-tag--${scope}`}
          title={SCOPE_COPY[scope].title}
        >
          {SCOPE_COPY[scope].label}
        </span>
      ))}
    </span>
  );
}
