import type {
  LiteRecipeDestinations,
  LiteRecipeFactDestination,
  LiteRecipeUserInfoDestination,
  OutputCombineMode,
  OutputMode,
  StorageTier,
  UserInfoOutputMode,
} from '../types';
import { STORAGE_TARGETS } from '../data/storage-targets';
import { detectPathWarnings } from '../lib/pathWarnings';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import { MiniKitchenField } from './MiniKitchenField';
import {
  DashboardReqBadge,
  DashboardReqTag,
  ALL_DASHBOARD_SCOPES,
  USER_INFO_RUN_SCOPES,
} from './DashboardRequirement';
import { BrowsePathButton } from '../../../shell/BrowsePathButton';
import { ContextualHelpButton } from '../../../components/ContextualHelpButton';

interface OutputTargetCardProps {
  value: LiteRecipeDestinations;
  /** When true, this is a user-info-only recipe and the fact destination is suppressed. */
  userInfoOnly?: boolean;
  /** True when the recipe has a rollup mode set in Step 3. */
  rollupActive?: boolean;
  /**
   * Current audit activity output layout. `combined` -> `-CombineOutput`;
   * `separate` -> omit. Undefined is treated as `combined` (default).
   */
  combineMode?: OutputCombineMode;
  /**
   * True when the combine/separate picker is meaningful: more than one
   * audit activity type AND `-IncludeM365Usage` is off. When false, the
   * control is rendered disabled and visually pinned to Combined.
   */
  combineEligible?: boolean;
  /**
   * True when ineligibility is driven by `-IncludeM365Usage`. Drives the
   * disabled-state helper copy.
   */
  combineDisabledByM365?: boolean;
  /**
   * True when the recipe is going to produce Entra user-info output
   * (`-IncludeUserInfo` is set or the recipe is user-info-only). When false,
   * the user-info output-mode picker and path field are rendered disabled.
   */
  userInfoEligible?: boolean;
  /**
   * Current value of the engine-wide de-identify flag (PAX `-Deidentify`).
   * Optional; `undefined`/`false` both render the toggle off.
   */
  deidentify?: boolean;
  onChange: (next: LiteRecipeDestinations) => void;
  onCombineModeChange?: (next: OutputCombineMode) => void;
  /** Toggle the engine-wide de-identify flag. */
  onDeidentifyChange?: (next: boolean) => void;
}

const FACT_MODES: ReadonlyArray<{ id: OutputMode; title: string; desc: string }> = [
  {
    id: 'write-new',
    title: 'Write new file',
    desc: 'Maps to -OutputPath, which PAX treats as the output folder — PAX writes and names the CSV file inside it each run. A file path works too (PAX uses its parent folder).',
  },
  {
    id: 'append',
    title: 'Append to existing file',
    desc: 'Maps to -AppendFile. Accepts a bare filename (resolved against -OutputPath) or a full path. PAX expects the file to already exist with matching columns.',
  },
];

const COMBINE_MODES: ReadonlyArray<{ id: OutputCombineMode; title: string; desc: string }> = [
  {
    id: 'combined',
    title: 'Combined file',
    desc: 'Adds -CombineOutput. PAX writes one combined CSV covering every selected activity type.',
  },
  {
    id: 'separate',
    title: 'Separate files',
    desc: 'Omits -CombineOutput. PAX writes one CSV per activity type.',
  },
];

const USER_INFO_MODES: ReadonlyArray<{ id: UserInfoOutputMode; title: string; desc: string }> = [
  {
    id: 'default-colocate',
    title: 'Co-locate next to the audit output',
    desc: 'Emits -OutputPathUserInfo with the audit output\'s directory so PAX writes the user-info CSV alongside the fact file.',
  },
  {
    id: 'write-new',
    title: 'Write a separate user-info file',
    desc: 'Maps to -OutputPathUserInfo. Use when you want the user-info CSV in a distinct location.',
  },
  {
    id: 'append',
    title: 'Append to existing user-info file',
    desc: 'Maps to -AppendUserInfo. Accepts a bare filename (resolved against -OutputPath) or a full path. PAX expects the file to already exist with matching columns.',
  },
];

const TIER_DESC: Record<StorageTier, string> = {
  local: 'Default storage tier for new recipes. Works on Windows, macOS, and Linux runtimes.',
  sharepoint:
    'PAX auto-detects the SharePoint tier from the URL shape. Same OutputPath / AppendFile switches as local.',
  fabric:
    'OneLake Delta Lake destination. PAX auto-detects Fabric Lakehouse from the URL shape — no Fabric-specific switches.',
};

const SHAREPOINT_LEARN_URL =
  'https://learn.microsoft.com/en-us/graph/api/shares-get';
const FABRIC_LEARN_URL =
  'https://learn.microsoft.com/en-us/fabric/onelake/onelake-access-api';

function placeholderForTier(tier: StorageTier): string {
  if (tier === 'local') return 'C:\\PAX\\copilot-audit.csv';
  if (tier === 'sharepoint')
    return 'https://contoso.sharepoint.com/sites/copilot/Shared%20Documents/audit.csv';
  return 'https://onelake.dfs.fabric.microsoft.com/<workspace>/<lakehouse>.Lakehouse/Tables/copilot_audit';
}

// Append-target syntax guard. PAX accepts:
//   - a bare filename (resolved against -OutputPath), e.g. audit.csv
//   - a full Windows path, e.g. C:\PAX\audit.csv or \\server\share\audit.csv
//   - a SharePoint / OneLake URL, e.g. https://...sharepoint.com/... or
//     https://onelake.dfs.fabric.microsoft.com/...
// We only flag characters that Windows truly rejects inside a single path
// segment. Drive-letter `:` and URL scheme `://` are excluded by checking
// segments after the drive / scheme prefix.
const URL_LIKE = /^[a-z][a-z0-9+.-]*:\/\//i;
const WINDOWS_DRIVE_PREFIX = /^[A-Za-z]:[\\/]/;
const SEGMENT_ILLEGAL = /[<>"|?*\x00-\x1f]/;

function appendPathWarning(path: string | undefined): string | null {
  if (!path) return null;
  const trimmed = path.trim();
  if (trimmed.length === 0) return null;
  // URL-shaped destinations are not subject to Windows filename rules.
  // detectPathWarnings() handles tier-shape guidance for SharePoint / OneLake.
  if (URL_LIKE.test(trimmed)) return null;
  const body = WINDOWS_DRIVE_PREFIX.test(trimmed) ? trimmed.slice(2) : trimmed;
  const segments = body.split(/[\\/]+/);
  for (const segment of segments) {
    if (segment.length === 0) continue;
    if (SEGMENT_ILLEGAL.test(segment)) {
      return 'A path segment contains an illegal filename character (one of < > " | ? * or a control character).';
    }
  }
  return null;
}

export function OutputTargetCard({
  value,
  userInfoOnly = false,
  rollupActive = false,
  combineMode,
  combineEligible = false,
  combineDisabledByM365 = false,
  userInfoEligible = true,
  deidentify = false,
  onChange,
  onCombineModeChange,
  onDeidentifyChange,
}: OutputTargetCardProps) {
  const fact = value.fact;
  const userInfo = value.userInfo;

  function setFact(next: Partial<LiteRecipeFactDestination>) {
    onChange({ ...value, fact: { ...fact, ...next } });
  }
  function setUserInfo(next: Partial<LiteRecipeUserInfoDestination>) {
    onChange({ ...value, userInfo: { ...userInfo, ...next } });
  }

  const factPathTrimmed = (fact.path ?? '').trim();
  const factPathMissing = !userInfoOnly && factPathTrimmed.length === 0;
  const factAppendWarn = fact.mode === 'append' ? appendPathWarning(fact.path) : null;
  const factWarnings: string[] = [
    ...(factPathMissing
      ? [
          fact.mode === 'append'
            ? 'Append mode requires a filename or full path.'
            : 'Output path is required.',
        ]
      : []),
    ...(factAppendWarn ? [factAppendWarn] : []),
    ...(fact.path ? detectPathWarnings(fact.path).map(w => w.message) : []),
  ];

  const userInfoPathTrimmed = (userInfo.path ?? '').trim();
  const userInfoNeedsPath = userInfo.mode !== 'default-colocate';
  const userInfoPathMissing = userInfoNeedsPath && userInfoPathTrimmed.length === 0;
  const userInfoAppendWarn =
    userInfo.mode === 'append' ? appendPathWarning(userInfo.path) : null;
  const userInfoWarnings: string[] = [
    ...(userInfoPathMissing
      ? [
          userInfo.mode === 'append'
            ? 'Append mode requires a filename or full path.'
            : 'User-info path is required.',
        ]
      : []),
    ...(userInfoAppendWarn ? [userInfoAppendWarn] : []),
    ...(userInfo.path ? detectPathWarnings(userInfo.path).map(w => w.message) : []),
  ];

  return (
    <MiniKitchenSectionCard
      id="mk-output"
      title="Where output lands"
      subtitle="Storage tier, output mode, and path."
      helpText="PAX detects the tier from the path shape at runtime. PAX Cookbook does not validate paths, URLs, libraries, or workspaces."
      titleBadge={<DashboardReqBadge scopes={USER_INFO_RUN_SCOPES} />}
    >
      {!userInfoOnly ? (
        <>
          <MiniKitchenField label="Storage tier" htmlFor="mk-output-tier">
            <div
              className="mk-radio-cards"
              role="radiogroup"
              aria-label="Storage tier"
              id="mk-output-tier"
            >
              {STORAGE_TARGETS.map(t => {
                const inputId = `mk-output-tier-${t.id}`;
                const selected = fact.tier === t.id;
                return (
                  <label
                    key={t.id}
                    htmlFor={inputId}
                    className={'mk-radio-card' + (selected ? ' mk-radio-card--selected' : '')}
                  >
                    <input
                      type="radio"
                      id={inputId}
                      name="mk-output-tier"
                      value={t.id}
                      className="mk-radio-card__input"
                      checked={selected}
                      onChange={() => setFact({ tier: t.id, path: undefined })}
                    />
                    <span className="mk-radio-card__title">{t.name}</span>
                    <span className="mk-radio-card__desc">{TIER_DESC[t.id]}</span>
                  </label>
                );
              })}
            </div>
          </MiniKitchenField>
          <MiniKitchenField label="Audit data output mode" htmlFor="mk-output-fact-mode">
            <div
              className="mk-radio-cards mk-radio-cards--compact"
              role="radiogroup"
              aria-label="Audit data output mode"
              id="mk-output-fact-mode"
            >
              {FACT_MODES.map(m => {
                const inputId = `mk-output-fact-mode-${m.id}`;
                const selected = fact.mode === m.id;
                return (
                  <label
                    key={m.id}
                    htmlFor={inputId}
                    className={'mk-radio-card' + (selected ? ' mk-radio-card--selected' : '')}
                  >
                    <input
                      type="radio"
                      id={inputId}
                      name="mk-output-fact-mode"
                      value={m.id}
                      className="mk-radio-card__input"
                      checked={selected}
                      onChange={() => setFact({ mode: m.id })}
                    />
                    <span className="mk-radio-card__title">{m.title}</span>
                    <span className="mk-radio-card__desc">{m.desc}</span>
                  </label>
                );
              })}
            </div>
          </MiniKitchenField>
          {fact.mode === 'append' && rollupActive ? (
            <p className="mk-field__note mk-field__note--warn">
              Rollup is active — append targets the audit rollup file, not the raw audit output.
            </p>
          ) : null}
          <MiniKitchenField
            label="Audit destination path"
            htmlFor="mk-output-fact-path"
            hint={
              fact.tier === 'local'
                ? 'Absolute local path. PAX Cookbook does not test write access.'
                : 'Full HTTPS URL. PAX Cookbook does not contact the service.'
            }
            warnings={factWarnings}
          >
            <p className="mk-dash-req-line">
              <DashboardReqTag scopes={ALL_DASHBOARD_SCOPES} />
              <span className="mk-dash-req-line__text">
                A destination path is required for every run — and so the
                dashboards have output to load.
              </span>
            </p>
            <div className="mk-path-row">
              <input
                id="mk-output-fact-path"
                type="text"
                className={
                  'mk-input mk-input--code' +
                  (factPathMissing || factAppendWarn ? ' mk-input--invalid' : '')
                }
                value={fact.path ?? ''}
                spellCheck={false}
                autoComplete="off"
                placeholder={placeholderForTier(fact.tier)}
                aria-invalid={factPathMissing || Boolean(factAppendWarn) || undefined}
                onChange={e =>
                  setFact({ path: e.target.value === '' ? undefined : e.target.value })
                }
              />
              {fact.tier === 'local' ? (
                fact.mode === 'append' ? (
                  <BrowsePathButton
                    mode="file"
                    title="Select the output file"
                    filters={[{ name: 'CSV files', extensions: ['csv'] }]}
                    onSelect={path => setFact({ path })}
                  />
                ) : (
                  <BrowsePathButton
                    mode="folder"
                    title="Select the output folder"
                    onSelect={path => setFact({ path })}
                  />
                )
              ) : null}
            </div>
            {fact.tier === 'local' ? (
              <p className="mk-path-row__note">
                Enter the full path where PAX should save the output, starting
                with a drive letter (like C:\) or a network share (like
                \\server\). A relative path works too if you run PAX from that
                folder.
                {fact.mode === 'append'
                  ? ' Append mode needs the target file path, including the file name.'
                  : ' Write-new mode treats this as the output folder: PAX names the CSV file itself, so a folder is enough (a file path like C:\\PAX\\copilot-audit.csv also works — PAX writes into its parent folder).'}
              </p>
            ) : null}
          </MiniKitchenField>
          {fact.tier === 'sharepoint' ? (
            <div className="mk-tier-guidance mk-tier-guidance--sharepoint">
              <p className="mk-tier-guidance__title">
                How to get the right SharePoint URL
              </p>
              <p className="mk-tier-guidance__lede">
                The destination URL is{' '}
                <strong>not</strong> the URL in your browser address bar. PAX
                needs the canonical item URL, which SharePoint exposes in the
                Details pane.
              </p>
              <ol className="mk-tier-guidance__steps">
                <li>
                  In SharePoint, navigate to the destination folder (or the
                  parent folder of the destination file).
                </li>
                <li>
                  Select the folder or file, then open the Details pane (the
                  small &ldquo;i&rdquo; info icon in the top right of the
                  SharePoint command bar).
                </li>
                <li>
                  Scroll to the <strong>Path</strong> field near the bottom and
                  click the copy icon next to it.
                </li>
                <li>
                  Paste that URL into the Audit destination path field above.
                  For an append target, paste the full URL of the existing
                  CSV; for a new file, paste the folder URL and append{' '}
                  <code>/&lt;filename&gt;.csv</code>.
                </li>
              </ol>
              <p className="mk-tier-guidance__learn">
                Microsoft Learn:{' '}
                <a
                  href={SHAREPOINT_LEARN_URL}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  Accessing shared DriveItems via Microsoft Graph
                </a>
                .
              </p>
            </div>
          ) : null}
          {fact.tier === 'fabric' ? (
            <div className="mk-tier-guidance mk-tier-guidance--fabric">
              <p className="mk-tier-guidance__title">
                Valid Fabric / OneLake URL format
              </p>
              <p className="mk-tier-guidance__lede">
                PAX only accepts Fabric destinations on the OneLake
                DFS endpoint. Workspace portal URLs from{' '}
                <code>app.fabric.microsoft.com</code> point at the Fabric UI,
                not the underlying data, and are rejected.
              </p>
              <p className="mk-tier-guidance__pattern">
                <code>
                  https://[&lt;region&gt;-]onelake.dfs.fabric.microsoft.com/&lt;workspace&gt;/&lt;item&gt;.Lakehouse[/Tables[/&lt;schema&gt;]&nbsp;|&nbsp;/Files[/&lt;sub&gt;]]
                </code>
              </p>
              <ul className="mk-tier-guidance__steps">
                <li>
                  Host: <code>onelake.dfs.fabric.microsoft.com</code>, or its
                  regional variant (for example{' '}
                  <code>westus-onelake.dfs.fabric.microsoft.com</code>).
                </li>
                <li>
                  The item is addressed by <strong>name</strong> &mdash; the
                  segment ends with <code>.Lakehouse</code> &mdash; or by{' '}
                  <strong>GUID</strong> (the lakehouse item ID, no suffix), the
                  form the Fabric portal shows in the item URL. Both are accepted.
                </li>
                <li>
                  Inside the Lakehouse, use <code>/Tables</code> or{' '}
                  <code>/Tables/&lt;schema&gt;</code> for Delta-table output, or{' '}
                  <code>/Files/&lt;sub&gt;</code> for file-mode output. Pointing at
                  the Lakehouse <em>root</em> (no <code>/Tables</code> or{' '}
                  <code>/Files</code>) writes the tables as Delta tables under{' '}
                  <code>/Tables</code> automatically. PAX accepts one identifier
                  under <code>/Tables</code>; deeper paths are rejected.
                </li>
                <li>
                  In the Fabric portal you can copy this URL from a Lakehouse,
                  Tables, or Files node via the &ldquo;...&rdquo; menu &rarr;{' '}
                  <em>Copy URL</em> or <em>Properties</em>.
                </li>
              </ul>
              <p className="mk-tier-guidance__learn">
                Microsoft Learn:{' '}
                <a
                  href={FABRIC_LEARN_URL}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  Connecting to Microsoft OneLake (URI syntax)
                </a>
                .
              </p>
            </div>
          ) : null}
          {combineEligible ? (
          <MiniKitchenField
            label="Audit activity output"
            htmlFor="mk-output-combine-mode"
            hint={
              combineEligible
                ? 'Choose how PAX lays out the audit results across the selected activity types.'
                : combineDisabledByM365
                  ? 'M365 Usage bundle always produces a single combined output; PAX auto-enables -CombineOutput at runtime.'
                  : 'Only meaningful when more than one audit activity type is selected.'
            }
          >
            <div
              className="mk-radio-cards mk-radio-cards--compact"
              role="radiogroup"
              aria-label="Audit activity output"
              id="mk-output-combine-mode"
            >
              {COMBINE_MODES.map(m => {
                const inputId = `mk-output-combine-mode-${m.id}`;
                // When ineligible the picker is visually pinned to Combined
                // (PAX default behavior when the switch matters) without
                // mutating the stored value.
                const effectiveMode: OutputCombineMode = combineEligible
                  ? combineMode ?? 'combined'
                  : 'combined';
                const selected = effectiveMode === m.id;
                return (
                  <label
                    key={m.id}
                    htmlFor={inputId}
                    className={
                      'mk-radio-card' +
                      (selected ? ' mk-radio-card--selected' : '') +
                      (combineEligible ? '' : ' mk-radio-card--disabled')
                    }
                  >
                    <input
                      type="radio"
                      id={inputId}
                      name="mk-output-combine-mode"
                      value={m.id}
                      className="mk-radio-card__input"
                      checked={selected}
                      disabled={!combineEligible}
                      onChange={() => onCombineModeChange?.(m.id)}
                    />
                    <span className="mk-radio-card__title">{m.title}</span>
                    <span className="mk-radio-card__desc">{m.desc}</span>
                  </label>
                );
              })}
            </div>
          </MiniKitchenField>
          ) : null}
        </>
      ) : (
        <p className="mk-callout mk-callout--info">
          User-info-only recipes only emit a user-info destination. The audit destination
          controls are hidden because no audit data file is produced.
        </p>
      )}
      <MiniKitchenField
        label="User info output mode"
        htmlFor="mk-output-userinfo-mode"
        hint={
          userInfoEligible
            ? undefined
            : 'This recipe does not include Entra user info. Enable Include user info in Step 3 to choose a user-info output target.'
        }
      >
        <div
          className="mk-radio-cards mk-radio-cards--compact"
          role="radiogroup"
          aria-label="User info output mode"
          id="mk-output-userinfo-mode"
        >
          {USER_INFO_MODES.map(m => {
            const inputId = `mk-output-userinfo-mode-${m.id}`;
            const selected = userInfo.mode === m.id;
            return (
              <label
                key={m.id}
                htmlFor={inputId}
                className={
                  'mk-radio-card' +
                  (selected ? ' mk-radio-card--selected' : '') +
                  (userInfoEligible ? '' : ' mk-radio-card--disabled')
                }
              >
                <input
                  type="radio"
                  id={inputId}
                  name="mk-output-userinfo-mode"
                  value={m.id}
                  className="mk-radio-card__input"
                  checked={selected}
                  disabled={!userInfoEligible}
                  onChange={() =>
                    setUserInfo({
                      mode: m.id,
                      path: m.id === 'default-colocate' ? undefined : userInfo.path,
                    })
                  }
                />
                <span className="mk-radio-card__title">{m.title}</span>
                <span className="mk-radio-card__desc">{m.desc}</span>
              </label>
            );
          })}
        </div>
      </MiniKitchenField>
      {userInfoNeedsPath ? (
        <MiniKitchenField
          label="User-info destination path"
          htmlFor="mk-output-userinfo-path"
          hint="Same tier-detection rules apply as the audit destination."
          warnings={userInfoEligible ? userInfoWarnings : []}
        >
          <p className="mk-dash-req-line mk-dash-req-line--stack">
            <DashboardReqTag scopes={USER_INFO_RUN_SCOPES} />
            <span className="mk-dash-req-line__text">
              Required whenever you collect Entra user info — on any run, not
              just the dashboards.
            </span>
          </p>
          <div className="mk-path-row">
            <input
              id="mk-output-userinfo-path"
              type="text"
              className={
                'mk-input mk-input--code' +
                (userInfoEligible && (userInfoPathMissing || userInfoAppendWarn)
                  ? ' mk-input--invalid'
                  : '')
              }
              value={userInfo.path ?? ''}
              spellCheck={false}
              autoComplete="off"
              placeholder={placeholderForTier(fact.tier)}
              disabled={!userInfoEligible}
              aria-invalid={
                userInfoEligible && (userInfoPathMissing || Boolean(userInfoAppendWarn))
                  ? true
                  : undefined
              }
              onChange={e =>
                setUserInfo({ path: e.target.value === '' ? undefined : e.target.value })
              }
            />
          </div>
          {userInfoEligible && fact.tier === 'local' ? (
            <p className="mk-path-row__note">
              Enter the full path where PAX should save the Entra user info
              output, starting with a drive letter (like C:\) or a network share
              (like \\server\). A relative path works too if you run PAX from
              that folder.
              {userInfo.mode === 'append'
                ? ' Append mode needs the target file path, including the file name.'
                : ''}
            </p>
          ) : null}
        </MiniKitchenField>
      ) : null}
      {onDeidentifyChange ? (
        <div className="mk-subsection" id="mk-deidentify">
          <div className="mk-subsection__head">
            <h3 className="mk-subsection-title">Privacy</h3>
            <ContextualHelpButton topic="outputDeidentify" />
          </div>
          <label
            htmlFor="mk-deidentify-toggle"
            className={'mk-toggle' + (deidentify ? ' mk-toggle--on' : '')}
          >
            <input
              type="checkbox"
              id="mk-deidentify-toggle"
              className="mk-toggle__input"
              checked={deidentify}
              onChange={e => onDeidentifyChange(e.target.checked)}
            />
            <span className="mk-toggle__title">De-identify output</span>
            <span className="mk-toggle__desc">
              Anonymizes people in both the audit output and the Entra user-info
              output (and the rollup built from them). One-way: the original
              identities cannot be recovered from the de-identified files.
            </span>
            <span className="mk-toggle__switch-hint">-Deidentify</span>
          </label>
          {deidentify && (fact.mode === 'append' || userInfo.mode === 'append') ? (
            <p className="mk-field__note" role="note">
              Heads up: you are appending to an existing file. De-identified rows
              will be mixed in with whatever that file already contains. Append
              de-identified output only to a file that is itself de-identified.
            </p>
          ) : null}
        </div>
      ) : null}
    </MiniKitchenSectionCard>
  );
}
