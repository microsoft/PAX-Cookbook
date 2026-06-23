import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import { MiniKitchenField } from './MiniKitchenField';
import type { AuthMode } from '../types';
import {
  type ScheduleDraft,
  applyScheduleEnabled,
  applyScheduleKind,
  applyScheduleHour,
  applyScheduleMinute,
  applyScheduleDayToggle,
  isScheduleValid,
  describeSchedule,
} from '../lib/builderScheduleTransforms';

const WEEKDAYS: ReadonlyArray<{ id: number; short: string; long: string }> = [
  { id: 0, short: 'Sun', long: 'Sunday' },
  { id: 1, short: 'Mon', long: 'Monday' },
  { id: 2, short: 'Tue', long: 'Tuesday' },
  { id: 3, short: 'Wed', long: 'Wednesday' },
  { id: 4, short: 'Thu', long: 'Thursday' },
  { id: 5, short: 'Fri', long: 'Friday' },
  { id: 6, short: 'Sat', long: 'Saturday' },
];

const FREQUENCIES: ReadonlyArray<{ id: 'daily' | 'weekly'; title: string; desc: string }> = [
  { id: 'daily', title: 'Daily', desc: 'Runs once every day at the chosen time.' },
  { id: 'weekly', title: 'Weekly', desc: 'Runs on the chosen days each week.' },
];

interface ScheduleCardProps {
  value: ScheduleDraft;
  onChange: (next: ScheduleDraft) => void;
  /** True when a Chef's Key is bound; required to enable scheduling. */
  hasBoundChefKey: boolean;
  /** This recipe's current sign-in mode, used to tailor the not-schedulable copy. */
  authMode: AuthMode;
  /** Navigate to the Authentication step so the user can assign a Chef's Key. */
  onGoToAuthStep?: () => void;
  /** Optional note from the server (e.g. a schedule-drift advisory). */
  statusNote?: string | null;
}

/**
 * The recipe builder's Schedule section. Configures a recurring scheduled run
 * (daily or weekly at a chosen time). It only CONFIGURES the recurrence — it
 * never registers anything: the schedule is registered or removed by the save
 * flow through the broker's scheduled-task routes after the recipe is saved.
 *
 * Scheduling requires a bound Chef's Key (the run-time identity for an
 * unattended run): without one the enable toggle is disabled and the card shows
 * the not-schedulable gate. The broker independently enforces the same rule.
 */
export function ScheduleCard({
  value,
  onChange,
  hasBoundChefKey,
  authMode,
  onGoToAuthStep,
  statusNote,
}: ScheduleCardProps) {
  const validity = isScheduleValid(value);
  const showConfig = value.enabled;
  const isInteractiveAuth = authMode === 'WebLogin' || authMode === 'DeviceCode';

  return (
    <MiniKitchenSectionCard
      id="mk-schedule"
      title="Schedule"
      subtitle="Run this saved recipe automatically on a recurring schedule."
      helpText="Scheduling sets up a Windows task that runs this recipe automatically on the cadence you choose. The schedule is created when you save the recipe and signs in with the bound Chef's Key, so an unattended run needs an App registration key (secret or certificate). For a scheduled bake to actually run, keep Start PAX Cookbook at login turned on in Settings so PAX Cookbook is running in the background when the scheduled time arrives. Manual bakes are unaffected."
    >
      <p className="mk-field__note">
        Scheduling automates this recipe so it bakes on its own at set times — daily or
        weekly. PAX Cookbook sets up and manages a Windows scheduled task for you behind
        the scenes; you never need to open Task Scheduler, and every schedule change is
        made here in Cookbook.
      </p>

      {!hasBoundChefKey ? (
        <div className="mk-callout mk-callout--warning">
          <p>
            {isInteractiveAuth
              ? 'This recipe is set to sign in interactively (Web login or Device code), which needs a person at the keyboard \u2014 so it can\u2019t run on an unattended schedule. To schedule it, switch to an App registration sign-in and bind a Chef\u2019s Key on the Authentication step.'
              : 'This recipe doesn\u2019t have a Chef\u2019s Key bound yet. A scheduled bake runs unattended, so it needs a Chef\u2019s Key for sign-in. Bind one on the Authentication step to turn scheduling on.'}
          </p>
          {onGoToAuthStep ? (
            <button
              type="button"
              className="mk-preview-boundary__btn mk-preview-boundary__btn--sm"
              onClick={onGoToAuthStep}
            >
              Go to Authentication (Step 2)
            </button>
          ) : null}
        </div>
      ) : (
        <>
          <MiniKitchenField label="Run on a schedule" htmlFor="mk-schedule-enabled">
            <span className="mk-schedule__toggle">
              <input
                type="checkbox"
                id="mk-schedule-enabled"
                className="mk-schedule__toggle-input"
                checked={value.enabled}
                onChange={e => onChange(applyScheduleEnabled(value, e.target.checked))}
              />
              <span className="mk-schedule__toggle-text">
                Enable a recurring scheduled run for this recipe.
              </span>
            </span>
          </MiniKitchenField>

      {showConfig ? (
        <>
          <MiniKitchenField label="Frequency" htmlFor="mk-schedule-kind">
            <div
              className="mk-radio-cards"
              role="radiogroup"
              aria-label="Schedule frequency"
              id="mk-schedule-kind"
            >
              {FREQUENCIES.map(f => {
                const inputId = `mk-schedule-kind-${f.id}`;
                const selected = value.kind === f.id;
                return (
                  <label
                    key={f.id}
                    htmlFor={inputId}
                    className={
                      'mk-radio-card' + (selected ? ' mk-radio-card--selected' : '')
                    }
                  >
                    <input
                      type="radio"
                      id={inputId}
                      name="mk-schedule-kind"
                      value={f.id}
                      className="mk-radio-card__input"
                      checked={selected}
                      onChange={() => onChange(applyScheduleKind(value, f.id))}
                    />
                    <span className="mk-radio-card__title">{f.title}</span>
                    <span className="mk-radio-card__desc">{f.desc}</span>
                  </label>
                );
              })}
            </div>
          </MiniKitchenField>

          <MiniKitchenField
            label="Time of day"
            htmlFor="mk-schedule-hour"
            hint="24-hour clock (HH:MM), in this computer's local time."
          >
            <div className="mk-schedule__time">
              <input
                id="mk-schedule-hour"
                type="number"
                className="mk-input mk-schedule__time-input"
                min={0}
                max={23}
                value={value.hour}
                aria-label="Hour (0-23)"
                onChange={e =>
                  onChange(applyScheduleHour(value, Number.parseInt(e.target.value, 10)))
                }
              />
              <span className="mk-schedule__time-sep" aria-hidden="true">
                :
              </span>
              <input
                id="mk-schedule-minute"
                type="number"
                className="mk-input mk-schedule__time-input"
                min={0}
                max={59}
                value={value.minute}
                aria-label="Minute (0-59)"
                onChange={e =>
                  onChange(applyScheduleMinute(value, Number.parseInt(e.target.value, 10)))
                }
              />
            </div>
          </MiniKitchenField>

          {value.kind === 'weekly' ? (
            <MiniKitchenField
              label="Days of week"
              htmlFor="mk-schedule-days"
              hint="Pick the weekdays this recipe should run."
            >
              <div
                className="mk-schedule__days"
                role="group"
                aria-label="Days of week"
                id="mk-schedule-days"
              >
                {WEEKDAYS.map(day => {
                  const checked = value.daysOfWeek.includes(day.id);
                  return (
                    <label
                      key={day.id}
                      className={
                        'mk-schedule__day' + (checked ? ' mk-schedule__day--on' : '')
                      }
                    >
                      <input
                        type="checkbox"
                        className="mk-schedule__day-input"
                        checked={checked}
                        aria-label={day.long}
                        onChange={() => onChange(applyScheduleDayToggle(value, day.id))}
                      />
                      <span className="mk-schedule__day-text">{day.short}</span>
                    </label>
                  );
                })}
              </div>
              {value.daysOfWeek.length === 0 ? (
                <p className="settings-note">Pick at least one day for a weekly schedule.</p>
              ) : null}
            </MiniKitchenField>
          ) : null}

          {validity.ok ? (
            <p className="mk-callout mk-callout--info">
              Scheduled &mdash; {describeSchedule(value)}
            </p>
          ) : (
            <p className="mk-callout mk-callout--warning">{validity.reason}</p>
          )}
        </>
      ) : null}
        </>
      )}

      {statusNote ? <p className="settings-note">{statusNote}</p> : null}
    </MiniKitchenSectionCard>
  );
}
