import type { LiteRecipeQuery } from '../types';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import {
  DashboardReqBadge,
  DashboardReqTag,
  BOTH_DASHBOARD_SCOPES,
  type DashboardScope,
} from './DashboardRequirement';

interface DataCollectionCardProps {
  value: LiteRecipeQuery;
  /** True when the parent query mode is `user-info-only`. */
  disabled?: boolean;
  onChange: (next: LiteRecipeQuery) => void;
  activityTypes: readonly string[] | undefined;
  onActivityTypesChange: (next: readonly string[] | undefined) => void;
}

type ToggleKey =
  | 'includeM365Usage'
  | 'excludeCopilotInteraction'
  | 'includeUserInfo'
  | 'includeAgent365Info';

interface ToggleSpec {
  key: ToggleKey;
  title: string;
  desc: string;
  switchHint: string;
  /** Dashboard-requirement pills rendered next to the toggle title. */
  dashboardScopes?: readonly DashboardScope[];
}

const TOGGLES: readonly ToggleSpec[] = [
  {
    key: 'includeM365Usage',
    title: 'Include M365 usage signals',
    desc: 'Also pull Exchange / OneDrive / SharePoint / Teams audit branches to enrich Copilot activity.',
    switchHint: 'Adds -IncludeM365Usage to the rendered command.',
    dashboardScopes: ['m365-usage'],
  },
  {
    key: 'excludeCopilotInteraction',
    title: 'Exclude CopilotInteraction',
    desc: 'Drop CopilotInteraction events from the audit query. Independent of the M365 usage toggle.',
    switchHint: 'Adds -ExcludeCopilotInteraction to the rendered command.',
  },
  {
    key: 'includeUserInfo',
    title: 'Include Entra user info',
    desc: 'Co-export user / organization info alongside the audit results. Mirrors the Step 3 data-scope selection.',
    switchHint: 'Adds -IncludeUserInfo. Triggers extra Graph scopes.',
    dashboardScopes: BOTH_DASHBOARD_SCOPES,
  },
  {
    key: 'includeAgent365Info',
    title: 'Include Microsoft Agent 365 catalog',
    desc: 'Also export the Microsoft Agent 365 catalog alongside the audit run. Works with interactive sign-in or with app-registration / managed-identity auth.',
    switchHint: 'Adds -IncludeAgent365Info. Needs the Agent 365 catalog permissions + a license.',
  },
];

function linesToList(text: string): readonly string[] {
  return text
    .split(/[\r\n,;]+/)
    .map(s => s.trim())
    .filter(s => s.length > 0);
}

function listToLines(list: readonly string[] | undefined): string {
  return (list ?? []).join('\n');
}

export function DataCollectionCard({
  value,
  disabled = false,
  onChange,
  activityTypes,
  onActivityTypesChange,
}: DataCollectionCardProps) {
  return (
    <MiniKitchenSectionCard
      id="mk-data-collection"
      title="What to collect"
      subtitle="Independent switches. Toggle any combination."
      helpText="PAX Cookbook does not check whether your tenant exposes these signals — it only composes the command."
      disabled={disabled}
      titleBadge={<DashboardReqBadge scopes={['all-runs', ...BOTH_DASHBOARD_SCOPES]} />}
    >
      {disabled ? (
        <p className="mk-field__note" role="note">
          These options are turned off because you chose User info only, which
          collects user details instead of audit activity.
        </p>
      ) : null}
      <div className="mk-toggle-grid">
        {TOGGLES.map(t => {
          const inputId = `mk-data-${t.key}`;
          const checked = Boolean(value[t.key]);
          return (
            <label
              key={t.key}
              htmlFor={inputId}
              className={
                'mk-toggle' +
                (checked ? ' mk-toggle--on' : '') +
                (disabled ? ' mk-toggle--disabled' : '')
              }
            >
              <input
                type="checkbox"
                id={inputId}
                className="mk-toggle__input"
                checked={checked}
                disabled={disabled}
                onChange={e => onChange({ ...value, [t.key]: e.target.checked })}
              />
              <span className="mk-toggle__title">{t.title}</span>
              <span className="mk-toggle__pillrow">
                {t.dashboardScopes ? (
                  <DashboardReqTag scopes={t.dashboardScopes} />
                ) : null}
              </span>
              <span className="mk-toggle__desc">{t.desc}</span>
              <span className="mk-toggle__switch-hint">{t.switchHint}</span>
            </label>
          );
        })}
      </div>
      <div className="mk-byod-field">
        <label className="mk-byod-field__label" htmlFor="mk-data-userinfofile">
          Bring your own directory <span className="mk-field__optional">optional</span>
        </label>
        <p className="mk-field__hint" id="mk-data-userinfofile-hint">
          Supply the user and organization directory from a CSV file instead of
          pulling it live from Microsoft Entra. Accepts a local path, a SharePoint
          document, or a Fabric / OneLake file. Providing it counts as your user
          info, so you don&rsquo;t also need the Include Entra user info toggle, and
          it can&rsquo;t be combined with group filtering. Maps to -UserInfoFile.
        </p>
        <input
          type="text"
          id="mk-data-userinfofile"
          className="mk-input"
          value={value.userInfoFile ?? ''}
          placeholder={'C:\\PAX\\directory.csv'}
          spellCheck={false}
          disabled={disabled}
          aria-describedby="mk-data-userinfofile-hint"
          onChange={e => {
            const v = e.target.value;
            onChange({ ...value, userInfoFile: v.trim().length > 0 ? v : undefined });
          }}
        />
        <details className="mk-subcollapse">
          <summary className="mk-subcollapse__summary">
            <span className="mk-subcollapse__title">Directory file columns</span>
            <span className="mk-field__optional">reference</span>
            <span className="mk-card__chevron" aria-hidden="true" />
          </summary>
          <div className="mk-subcollapse__body">
            <p className="mk-field__hint">
              Header names are case-insensitive and alias-aware. Only{' '}
              <code>UserPrincipalName</code> is required. <code>DisplayName</code>,{' '}
              <code>Department</code>, <code>JobTitle</code>, and <code>ManagerUpn</code>{' '}
              are recommended for full org-hierarchy fidelity; <code>HasLicense</code>{' '}
              is optional (leave it blank to resolve each user online). Any extra
              columns you include are preserved as-is.
            </p>
          </div>
        </details>
      </div>
      <details className="mk-subcollapse">
        <summary className="mk-subcollapse__summary">
          <span className="mk-subcollapse__title">Custom activity types</span>
          <span className="mk-field__optional">optional</span>
          <span className="mk-card__chevron" aria-hidden="true" />
        </summary>
        <div className="mk-subcollapse__body">
          <p className="mk-field__hint" id="mk-data-activitytypes-hint">
            Most people leave this blank. It is for advanced users who need to
            target specific audit record types not covered by the options above.
            Separate with new lines, commas, or semicolons. Maps to -ActivityTypes;
            blank uses the PAX default (CopilotInteraction).
          </p>
          <textarea
            id="mk-data-activitytypes"
            className="mk-input mk-input--textarea mk-input--code"
            rows={3}
            value={listToLines(activityTypes)}
            placeholder={'CopilotInteraction'}
            spellCheck={false}
            disabled={disabled}
            aria-describedby="mk-data-activitytypes-hint"
            onChange={e => {
              const list = linesToList(e.target.value);
              onActivityTypesChange(list.length > 0 ? list : undefined);
            }}
          />
          <p className="mk-field__note">
            This is an advanced, rarely needed field — most people leave it blank.
            Activity types are the specific actions recorded in your tenant&rsquo;s
            audit log (for example, <code>CopilotInteraction</code>). For the full list
            of names you can enter here, see{' '}
            <a
              href="https://learn.microsoft.com/purview/audit-log-activities"
              target="_blank"
              rel="noopener noreferrer"
            >
              Audited activities in Microsoft Purview
            </a>
            .
          </p>
        </div>
      </details>
    </MiniKitchenSectionCard>
  );
}
