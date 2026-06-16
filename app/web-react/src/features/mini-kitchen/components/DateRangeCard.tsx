import type { LiteRecipeQuery } from '../types';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import { MiniKitchenField } from './MiniKitchenField';
import {
  DashboardReqBadge,
  ALL_DASHBOARD_SCOPES,
} from './DashboardRequirement';

interface DateRangeCardProps {
  value: LiteRecipeQuery;
  disabled?: boolean;
  onChange: (next: LiteRecipeQuery) => void;
}

function isoDateNDaysAgo(n: number): string {
  const d = new Date();
  d.setUTCHours(0, 0, 0, 0);
  d.setUTCDate(d.getUTCDate() - n);
  return d.toISOString().slice(0, 10);
}

function todayIso(): string {
  const d = new Date();
  d.setUTCHours(0, 0, 0, 0);
  return d.toISOString().slice(0, 10);
}

/**
 * First end date that counts as "in the future" for the informational note.
 * End dates are exclusive in Purview, so an end date of tomorrow still only
 * pulls data through today — it is not a future pull. Anything past tomorrow
 * reaches into days that have no audit data yet.
 */
function firstFutureEndIso(): string {
  const d = new Date();
  d.setUTCHours(0, 0, 0, 0);
  d.setUTCDate(d.getUTCDate() + 2);
  return d.toISOString().slice(0, 10);
}

function rangeInvalid(start: string | undefined, end: string | undefined): boolean {
  if (!start || !end) return false;
  return end <= start;
}

function endInFuture(end: string | undefined): boolean {
  if (!end) return false;
  return end >= firstFutureEndIso();
}

export function DateRangeCard({ value, disabled = false, onChange }: DateRangeCardProps) {
  const startId = 'mk-date-start';
  const endId = 'mk-date-end';
  const invalid = rangeInvalid(value.startDate, value.endDate);
  const endFuture = endInFuture(value.endDate);
  // `undefined` (recipes saved before this field existed) is treated as custom.
  const isPreviousDay = value.dateMode === 'previous-day';
  return (
    <MiniKitchenSectionCard
      id="mk-date-range"
      title="Date range"
      subtitle="Window passed to -StartDate and -EndDate. End date is exclusive."
      helpText="Audit-only window — Entra user info is point-in-time and ignores this range. PAX Cookbook does not validate against audit-log retention."
      disabled={disabled}
      titleBadge={<DashboardReqBadge scopes={ALL_DASHBOARD_SCOPES} />}
    >
      {disabled ? (
        <p className="mk-field__note" role="note">
          This step is turned off because you chose User info only, which doesn’t
          use an audit date range.
        </p>
      ) : null}
      {!disabled ? (
        <div className="mk-radio-cards" role="radiogroup" aria-label="Date range mode">
          <label
            htmlFor="mk-date-mode-custom"
            className={'mk-radio-card' + (!isPreviousDay ? ' mk-radio-card--selected' : '')}
          >
            <input
              type="radio"
              id="mk-date-mode-custom"
              name="mk-date-mode"
              value="custom"
              className="mk-radio-card__input"
              checked={!isPreviousDay}
              disabled={disabled}
              onChange={() => onChange({ ...value, dateMode: 'custom' })}
            />
            <span className="mk-radio-card__title">Custom range</span>
            <span className="mk-radio-card__desc">
              Pick an explicit start and end date. Emits -StartDate and -EndDate.
            </span>
          </label>
          <label
            htmlFor="mk-date-mode-previous-day"
            className={'mk-radio-card' + (isPreviousDay ? ' mk-radio-card--selected' : '')}
          >
            <input
              type="radio"
              id="mk-date-mode-previous-day"
              name="mk-date-mode"
              value="previous-day"
              className="mk-radio-card__input"
              checked={isPreviousDay}
              disabled={disabled}
              onChange={() =>
                onChange({ ...value, dateMode: 'previous-day', startDate: undefined, endDate: undefined })
              }
            />
            <span className="mk-radio-card__title">Previous day</span>
            <span className="mk-radio-card__desc">
              Omit both dates so PAX pulls the previous full UTC day. Ideal for
              scheduled daily or append runs.
            </span>
          </label>
        </div>
      ) : null}
      {isPreviousDay && !disabled ? (
        <p className="mk-field__note" role="note">
          PAX will query the previous full UTC day (yesterday 00:00 UTC through
          today 00:00 UTC) — it leaves out <strong>-StartDate</strong> and{' '}
          <strong>-EndDate</strong> entirely. Ideal for scheduled daily or append
          runs; no date is needed and nothing here blocks the run.
        </p>
      ) : null}
      {!isPreviousDay ? (
        <>
      <p className="mk-dash-req-line">
        <span className="mk-dash-req-line__text">
          Set both dates so the audit query returns data — required for every
          run, and the dashboards in particular. Or switch to Previous day above
          for an automatic one-day lookback (the last full UTC day).
        </span>
      </p>
      <div className="mk-date-row">
        <MiniKitchenField label="Start date" htmlFor={startId} disabled={disabled} required={!disabled}>
          <input
            id={startId}
            type="date"
            className={'mk-input mk-input--date' + (invalid ? ' mk-input--invalid' : '')}
            value={value.startDate ?? ''}
            disabled={disabled}
            aria-required={!disabled ? true : undefined}
            aria-invalid={invalid || undefined}
            onChange={e => onChange({ ...value, startDate: e.target.value || undefined })}
          />
        </MiniKitchenField>
        <MiniKitchenField label="End date" htmlFor={endId} disabled={disabled} required={!disabled}>
          <input
            id={endId}
            type="date"
            className={'mk-input mk-input--date' + (invalid ? ' mk-input--invalid' : '')}
            value={value.endDate ?? ''}
            disabled={disabled}
            aria-required={!disabled ? true : undefined}
            aria-invalid={invalid || undefined}
            onChange={e => onChange({ ...value, endDate: e.target.value || undefined })}
          />
        </MiniKitchenField>
      </div>
      {invalid ? (
        <p className="mk-field__warning" role="alert">
          End date must be after start date — they can't be the same day.
        </p>
      ) : null}
      {endFuture ? (
        <p className="mk-field__note mk-field__note--warn" role="status">
          That end date is in the future. Purview has no audit data for days that
          haven't happened yet, so a run today would stop at the current date — but if
          you're setting this recipe up to run later, that's perfectly fine.
        </p>
      ) : null}
      <p className="mk-field__note">
        The end date is exclusive: records are pulled up to{' '}
        <strong>but not including</strong> that day. For example, a Jan 1 – Jan 8 range
        returns Jan 1 through Jan 7. To include the 8th, set the end date to Jan 9. This is
        how Purview audit log queries work, not a PAX Cookbook or PAX behavior.
      </p>
      <div className="mk-date-helpers">
        <button
          type="button"
          className="mk-chip-button"
          disabled={disabled}
          onClick={() =>
            onChange({ ...value, startDate: isoDateNDaysAgo(7), endDate: todayIso() })
          }
        >
          Last 7 days
        </button>
        <button
          type="button"
          className="mk-chip-button"
          disabled={disabled}
          onClick={() =>
            onChange({ ...value, startDate: isoDateNDaysAgo(30), endDate: todayIso() })
          }
        >
          Last 30 days
        </button>
        <button
          type="button"
          className="mk-chip-button mk-chip-button--warn"
          disabled={disabled}
          onClick={() =>
            onChange({ ...value, startDate: isoDateNDaysAgo(90), endDate: todayIso() })
          }
        >
          Last 90 days
        </button>
        <button
          type="button"
          className="mk-chip-button mk-chip-button--warn"
          disabled={disabled}
          onClick={() =>
            onChange({ ...value, startDate: isoDateNDaysAgo(180), endDate: todayIso() })
          }
        >
          Last 180 days
        </button>
        <button
          type="button"
          className="mk-chip-button mk-chip-button--ghost"
          disabled={disabled}
          onClick={() => onChange({ ...value, startDate: undefined, endDate: undefined })}
        >
          Clear
        </button>
      </div>
      <p className="mk-field__note mk-field__note--warn">
        Tenants typically retain Purview audit data for 90 or 180 days. Longer windows may return
        partial results.
      </p>
        </>
      ) : null}
    </MiniKitchenSectionCard>
  );
}
