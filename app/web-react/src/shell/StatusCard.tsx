/**
 * Status tile for the Home dashboard and the small status badges shared by the
 * Home and Recipes surfaces. Presentational only — the caller supplies the tone
 * and copy; nothing here calls the broker or runs anything.
 */
import type { ReactNode } from 'react';
import {
  IconCheckCircle,
  IconAlertCircle,
  IconClock,
  IconFolder,
  IconUser,
} from './CookbookIllustrations';

export type StatusTone = 'ready' | 'attention' | 'neutral' | 'unknown';

const TONE_ICON: Record<StatusTone, (p: { className?: string }) => ReactNode> = {
  ready: IconCheckCircle,
  attention: IconAlertCircle,
  neutral: IconClock,
  unknown: IconClock,
};

interface StatusCardProps {
  /** The thing being reported on, e.g. "PAX Engine". */
  title: string;
  /** Short status word, e.g. "Ready" / "Needs attention". */
  state: string;
  /** One-line plain-English detail. */
  detail: string;
  tone: StatusTone;
  /** Optional override icon (e.g. folder for Workspace, person for Sign-in). */
  icon?: 'check' | 'folder' | 'person' | 'clock' | 'alert';
}

function resolveIcon(
  tone: StatusTone,
  icon: StatusCardProps['icon'],
): (p: { className?: string }) => ReactNode {
  switch (icon) {
    case 'folder':
      return IconFolder;
    case 'person':
      return IconUser;
    case 'clock':
      return IconClock;
    case 'alert':
      return IconAlertCircle;
    case 'check':
      return IconCheckCircle;
    default:
      return TONE_ICON[tone];
  }
}

export function StatusCard({ title, state, detail, tone, icon }: StatusCardProps) {
  const Icon = resolveIcon(tone, icon);
  return (
    <div className={`dvw-status-card dvw-status-card--${tone}`}>
      <span className="dvw-status-card__icon" aria-hidden="true">
        <Icon />
      </span>
      <span className="dvw-status-card__title">{title}</span>
      <span className="dvw-status-card__state">{state}</span>
      <span className="dvw-status-card__detail">{detail}</span>
    </div>
  );
}

/* ---------------------------------------------------------------- */
/* Small inline status badge (recipe rows, detail header)           */
/* ---------------------------------------------------------------- */

export type RecipeBadgeTone = 'ready' | 'attention' | 'draft';

const BADGE_LABEL: Record<RecipeBadgeTone, string> = {
  ready: 'Ready',
  attention: 'Needs attention',
  draft: 'Draft',
};

/** Map a free-form recipe status string onto one of the three badge tones. */
export function toneForRecipeStatus(status: unknown): RecipeBadgeTone {
  const value = typeof status === 'string' ? status.trim().toLowerCase() : '';
  if (value === 'ready' || value === 'complete' || value === 'valid') {
    return 'ready';
  }
  if (
    value === 'needsprep' ||
    value === 'needs_prep' ||
    value === 'needs attention' ||
    value === 'attention' ||
    value === 'incomplete' ||
    value === 'invalid'
  ) {
    return 'attention';
  }
  return 'draft';
}

export function RecipeStatusBadge({
  tone,
  label,
}: {
  tone: RecipeBadgeTone;
  label?: string;
}) {
  return (
    <span className={`dvw-badge dvw-badge--${tone}`}>{label ?? BADGE_LABEL[tone]}</span>
  );
}
