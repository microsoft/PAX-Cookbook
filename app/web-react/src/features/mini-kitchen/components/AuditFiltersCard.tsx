import type {
  AgentFilterMode,
  LiteRecipeProcessing,
  PromptFilter,
} from '../types';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import { MiniKitchenField } from './MiniKitchenField';
import { DashboardReqTag, BOTH_DASHBOARD_SCOPES } from './DashboardRequirement';

interface AuditFiltersCardProps {
  value: LiteRecipeProcessing;
  disabled?: boolean;
  onChange: (next: LiteRecipeProcessing) => void;
}

function linesToList(text: string): readonly string[] {
  return text
    .split(/[\r\n,;]+/)
    .map(s => s.trim())
    .filter(s => s.length > 0);
}

function listToLines(list: readonly string[] | undefined): string {
  return (list ?? []).join('\n');
}

const AGENT_MODES: ReadonlyArray<{ id: AgentFilterMode; title: string; desc: string }> = [
  { id: 'none', title: 'No agent filter', desc: 'Include everything PAX returns.' },
  {
    id: 'ids-only',
    title: 'Specific IDs',
    desc: 'Only include events whose AgentId / app id matches the list below.',
  },
  {
    id: 'agents-only',
    title: 'Agents only',
    desc: 'Only include events with an Agent record type.',
  },
  {
    id: 'exclude-agents',
    title: 'Exclude agents',
    desc: 'Drop agent events from the result.',
  },
];

const PROMPT_FILTER_OPTIONS: ReadonlyArray<{
  id: PromptFilter | '__none__';
  title: string;
}> = [
  { id: '__none__', title: '(no filter)' },
  { id: 'Prompt', title: 'Prompt' },
  { id: 'Response', title: 'Response' },
  { id: 'Both', title: 'Both' },
  { id: 'Null', title: 'Null' },
];

/**
 * Phase 5: Audit filters card. Edits user / group / agent / prompt filter
 * fields of the processing slice. No Agent365, no RecordTypes, no
 * ServiceTypes — those are intentionally out of scope per the master plan.
 */
export function AuditFiltersCard({
  value,
  disabled = false,
  onChange,
}: AuditFiltersCardProps) {
  const agentMode: AgentFilterMode = value.agentFilter?.mode ?? 'none';
  return (
    <MiniKitchenSectionCard
      id="mk-audit-filters"
      title="User, group, and agent filters"
      subtitle="Optional narrowing of the audit-query result set."
      helpText="Leave blank to skip a filter. PAX Cookbook does not validate IDs or names against your tenant."
      disabled={disabled}
    >
      {disabled ? (
        <p className="mk-field__note" role="note">
          These filters are turned off because you chose User info only — there is
          no audit activity to filter.
        </p>
      ) : (
        <p className="mk-field__note" role="note">
          These are <strong>inclusion</strong> filters. When you enter a value, only
          data matching it is included and everything else is left out. Leave a
          filter empty to include <strong>all</strong> data for that category —
          nothing is excluded.
        </p>
      )}
      <MiniKitchenField
        label="User IDs"
        htmlFor="mk-audit-userids"
        hint="Separate with new lines, commas, or semicolons. Maps to -UserIds."
        optional
        disabled={disabled}
      >
        <textarea
          id="mk-audit-userids"
          className="mk-input mk-input--textarea mk-input--code"
          rows={3}
          value={listToLines(value.userIds)}
          placeholder={'alice@contoso.com\nbob@contoso.com'}
          spellCheck={false}
          disabled={disabled}
          onChange={e => {
            const ids = linesToList(e.target.value);
            onChange({ ...value, userIds: ids.length > 0 ? ids : undefined });
          }}
        />
      </MiniKitchenField>
      <MiniKitchenField
        label="Group display names"
        htmlFor="mk-audit-groups"
        hint="Separate with new lines, commas, or semicolons. Maps to -GroupNames. Adds GroupMember.Read.All to required permissions."
        optional
        disabled={disabled}
      >
        <textarea
          id="mk-audit-groups"
          className="mk-input mk-input--textarea mk-input--code"
          rows={3}
          value={listToLines(value.groupNames)}
          placeholder={'Copilot Pilots\nCustomer Success'}
          spellCheck={false}
          disabled={disabled}
          onChange={e => {
            const names = linesToList(e.target.value);
            onChange({ ...value, groupNames: names.length > 0 ? names : undefined });
          }}
        />
      </MiniKitchenField>
      <MiniKitchenField
        label="Agent filter"
        htmlFor="mk-audit-agentmode"
        disabled={disabled}
      >
        <div
          className="mk-radio-cards mk-radio-cards--compact"
          role="radiogroup"
          aria-label="Agent filter mode"
          id="mk-audit-agentmode"
        >
          {AGENT_MODES.map(m => {
            const inputId = `mk-audit-agentmode-${m.id}`;
            const selected = agentMode === m.id;
            return (
              <label
                key={m.id}
                htmlFor={inputId}
                className={'mk-radio-card' + (selected ? ' mk-radio-card--selected' : '')}
              >
                <input
                  type="radio"
                  id={inputId}
                  name="mk-audit-agentmode"
                  value={m.id}
                  className="mk-radio-card__input"
                  checked={selected}
                  disabled={disabled}
                  onChange={() =>
                    onChange({
                      ...value,
                      agentFilter:
                        m.id === 'none'
                          ? undefined
                          : { mode: m.id, ids: value.agentFilter?.ids },
                    })
                  }
                />
                <span className="mk-radio-card__title">
                  {m.title}
                  {m.id === 'none' ? (
                    <DashboardReqTag scopes={BOTH_DASHBOARD_SCOPES} />
                  ) : null}
                </span>
                <span className="mk-radio-card__desc">{m.desc}</span>
              </label>
            );
          })}
        </div>
      </MiniKitchenField>
      {agentMode === 'ids-only' ? (
        <MiniKitchenField
          label="Agent IDs"
          htmlFor="mk-audit-agentids"
          hint="Separate with new lines, commas, or semicolons. Required for the IDs-only mode."
          disabled={disabled}
        >
          <textarea
            id="mk-audit-agentids"
            className="mk-input mk-input--textarea mk-input--code"
            rows={3}
            value={listToLines(value.agentFilter?.ids)}
            placeholder={'guid-here\nguid-here'}
            spellCheck={false}
            disabled={disabled}
            onChange={e => {
              const ids = linesToList(e.target.value);
              onChange({
                ...value,
                agentFilter: {
                  mode: 'ids-only',
                  ids: ids.length > 0 ? ids : undefined,
                },
              });
            }}
          />
        </MiniKitchenField>
      ) : null}
      <MiniKitchenField
        label="Prompt filter"
        htmlFor="mk-audit-promptfilter"
        hint="Maps to -PromptFilter. Choose (no filter) to omit the switch, which keeps both prompts and responses."
        disabled={disabled}
      >
        <div
          className="mk-pill-group"
          role="radiogroup"
          aria-label="Prompt filter"
          id="mk-audit-promptfilter"
        >
          {PROMPT_FILTER_OPTIONS.map(opt => {
            const inputId = `mk-audit-promptfilter-${opt.id}`;
            const selected =
              (opt.id === '__none__' && !value.promptFilter) || value.promptFilter === opt.id;
            return (
              <label
                key={opt.id}
                htmlFor={inputId}
                className={'mk-pill' + (selected ? ' mk-pill--selected' : '')}
              >
                <input
                  type="radio"
                  id={inputId}
                  name="mk-audit-promptfilter"
                  value={opt.id}
                  className="mk-pill__input"
                  checked={selected}
                  disabled={disabled}
                  onChange={() =>
                    onChange({
                      ...value,
                      promptFilter:
                        opt.id === '__none__' ? undefined : (opt.id as PromptFilter),
                    })
                  }
                />
                <span className="mk-pill__label">{opt.title}</span>
              </label>
            );
          })}
        </div>
      </MiniKitchenField>
    </MiniKitchenSectionCard>
  );
}
