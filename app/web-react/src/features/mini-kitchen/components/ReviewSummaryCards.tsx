import type { ReactNode } from 'react';
import type { RenderedCommand } from '../lib/commandRenderer';
import type { PermissionsReport } from '../types';

interface ReviewSummaryCardsProps {
  command: RenderedCommand;
  permissions: PermissionsReport;
  onOpenWarnings: () => void;
  onOpenAssumptions: () => void;
  onOpenPermissions: () => void;
}

/**
 * Three compact summary cards under the generated command in the review
 * section: Warnings, Assumptions, Permissions needed.
 *
 * Each card is a full-width button that opens the matching disclosure
 * panel below and scrolls it to the top of the viewport. Title + icon +
 * count only — every count is read from the renderer or resolver output.
 */
export function ReviewSummaryCards({
  command,
  permissions,
  onOpenWarnings,
  onOpenAssumptions,
  onOpenPermissions,
}: ReviewSummaryCardsProps) {
  const warningsCount = countReviewWarnings(command, permissions);
  const assumptionsCount = countReviewAssumptions(command, permissions);
  const permsCount = countReviewPermissions(permissions);

  return (
    <div className="mk-review-cards">
      <SummaryCard
        variant="warnings"
        icon={<WarningIcon />}
        title="Warnings"
        count={warningsCount}
        onClick={onOpenWarnings}
      />
      <SummaryCard
        variant="assumptions"
        icon={<InfoIcon />}
        title="Assumptions"
        count={assumptionsCount}
        onClick={onOpenAssumptions}
      />
      <SummaryCard
        variant="permissions"
        icon={<ShieldIcon />}
        title="Permissions needed"
        count={permsCount}
        onClick={onOpenPermissions}
      />
    </div>
  );
}

type SummaryVariant = 'warnings' | 'assumptions' | 'permissions';

interface SummaryCardProps {
  variant: SummaryVariant;
  icon: ReactNode;
  title: string;
  count: number;
  onClick: () => void;
}

function SummaryCard({ variant, icon, title, count, onClick }: SummaryCardProps) {
  return (
    <button
      type="button"
      className={`mk-summary-card mk-summary-card--${variant}`}
      onClick={onClick}
    >
      <span className={`mk-summary-card__icon mk-summary-card__icon--${variant}`} aria-hidden="true">
        {icon}
      </span>
      <div className="mk-summary-card__body">
        <header className="mk-summary-card__head">
          <h3 className="mk-summary-card__title">{title}</h3>
          <span
            className="mk-summary-card__count"
            aria-label={`${count} item${count === 1 ? '' : 's'}`}
          >
            {count}
          </span>
        </header>
      </div>
    </button>
  );
}

/**
 * Count helpers shared by the summary cards and the collapsible review
 * disclosures, so a card's count and its matching section badge always agree.
 */
export function countReviewWarnings(
  command: RenderedCommand,
  permissions: PermissionsReport,
): number {
  const seen = new Set<string>();
  for (const w of command.warnings) seen.add(w.message);
  for (const m of permissions.warnings) seen.add(m);
  return seen.size + command.blocked.length;
}

export function countReviewAssumptions(
  command: RenderedCommand,
  permissions: PermissionsReport,
): number {
  const seen = new Set<string>();
  for (const a of command.assumptions) seen.add(a);
  for (const a of permissions.assumptions) seen.add(a);
  return seen.size;
}

export function countReviewPermissions(permissions: PermissionsReport): number {
  return permissions.required.filter(entry => entry.group === 'graph').length;
}

// -----------------------------------------------------------------------------
// Icons — inline SVG so we don't add asset dependencies. aria-hidden on parent.
// -----------------------------------------------------------------------------

function WarningIcon() {
  return (
    <svg
      width="20"
      height="20"
      viewBox="0 0 20 20"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      focusable="false"
    >
      <path
        d="M10 2.5L18.5 17.5H1.5L10 2.5Z"
        fill="currentColor"
        stroke="currentColor"
        strokeWidth="0.5"
        strokeLinejoin="round"
      />
      <path
        d="M10 8V12"
        stroke="#ffffff"
        strokeWidth="1.6"
        strokeLinecap="round"
      />
      <circle cx="10" cy="14.5" r="0.9" fill="#ffffff" />
    </svg>
  );
}

function InfoIcon() {
  return (
    <svg
      width="20"
      height="20"
      viewBox="0 0 20 20"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      focusable="false"
    >
      <circle cx="10" cy="10" r="8.25" fill="currentColor" />
      <path
        d="M10 8.75V14"
        stroke="#ffffff"
        strokeWidth="1.6"
        strokeLinecap="round"
      />
      <circle cx="10" cy="6.4" r="1" fill="#ffffff" />
    </svg>
  );
}

function ShieldIcon() {
  return (
    <svg
      width="20"
      height="20"
      viewBox="0 0 20 20"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      focusable="false"
    >
      <path
        d="M10 2.25L16.5 4.5V10C16.5 13.6 13.7 16.7 10 17.75C6.3 16.7 3.5 13.6 3.5 10V4.5L10 2.25Z"
        fill="currentColor"
      />
      <path
        d="M7.25 10.25L9.25 12L13 8.25"
        stroke="#ffffff"
        strokeWidth="1.6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}