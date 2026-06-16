/**
 * Advanced details fly-in panel for the Recipes workspace.
 *
 * Opened from the recipe detail pane's action row (the "Advanced" button next
 * to Bake (run), Open Editor, and Duplicate), this panel slides in from the
 * right edge as a real layout column — it PUSHES the three-column workspace to
 * the left rather than overlapping it. The open/close width is a
 * normal-flow flex transition in workspace.css (never position:fixed or
 * absolute), so the columns simply shrink to make room and return to full width
 * when it closes.
 *
 * It is a read-only surface. The command preview, required Graph permissions,
 * and support diagnostics are all projections the workspace already holds.
 * Nothing here runs PAX, mutates a recipe, or reveals a secret — the copy
 * button copies the very command already shown on screen.
 */
import { useEffect, useRef, useState } from 'react';
import type { RecipeReadinessBody } from '../host/brokerBridge';
import type { PermissionEntry } from '../features/mini-kitchen/types';
import { formatModified } from './shellNav';
import type {
  ReadinessPhase,
  RecipeDiagnostics,
  DiagnosticsLastBake,
} from './ReadinessInspector';
import {
  IconX,
  IconCopy,
  IconCode,
  IconShieldCheck,
  IconInfoCircle,
} from './CookbookIllustrations';

interface AdvancedPanelProps {
  open: boolean;
  onClose: () => void;
  result: RecipeReadinessBody | null;
  phase: ReadinessPhase;
  diagnostics: RecipeDiagnostics | null;
  graphPermissions: readonly PermissionEntry[];
}

export function AdvancedPanel({
  open,
  onClose,
  result,
  phase,
  diagnostics,
  graphPermissions,
}: AdvancedPanelProps) {
  // Transient "Copied" confirmation for the command copy button. The timer id is
  // held in a ref so it can be cleared on unmount (and on a rapid re-copy),
  // avoiding a setState-after-unmount warning.
  const [copied, setCopied] = useState(false);
  const copyTimerRef = useRef<number | null>(null);
  useEffect(
    () => () => {
      if (copyTimerRef.current !== null) {
        window.clearTimeout(copyTimerRef.current);
      }
    },
    [],
  );

  const command = result?.command ?? null;

  function handleCopy() {
    if (!command) {
      return;
    }
    // Fire-and-forget: the clipboard may be unavailable or denied; the command
    // stays on screen to copy by hand. Guarded so it never throws.
    void navigator.clipboard?.writeText(command).catch(() => undefined);
    setCopied(true);
    if (copyTimerRef.current !== null) {
      window.clearTimeout(copyTimerRef.current);
    }
    copyTimerRef.current = window.setTimeout(() => {
      setCopied(false);
      copyTimerRef.current = null;
    }, 2000);
  }

  // When closed the panel collapses to zero width but stays mounted for the push
  // transition; its controls are pulled out of the tab order and hidden from
  // assistive tech while clipped.
  const controlTabIndex = open ? undefined : -1;

  return (
    <aside
      className={'dvw-advanced-panel' + (open ? ' dvw-advanced-panel--open' : '')}
      aria-label="Advanced details"
      aria-hidden={open ? undefined : true}
      onKeyDown={ev => {
        if (open && ev.key === 'Escape') {
          onClose();
        }
      }}
    >
      <div className="dvw-advanced-panel__inner">
        <header className="dvw-advanced-panel__head">
          <h2 className="dvw-advanced-panel__title">Advanced Details</h2>
          <button
            type="button"
            className="dvw-advanced-panel__close"
            onClick={onClose}
            aria-label="Close advanced panel"
            tabIndex={controlTabIndex}
          >
            <IconX />
          </button>
        </header>

        {/* Section 1 — Command Preview (with copy to clipboard). */}
        <section className="dvw-advanced-panel__section">
          <h3 className="dvw-advanced-panel__section-title">
            <span className="dvw-advanced-panel__section-icon" aria-hidden="true">
              <IconCode />
            </span>
            <span>Command Preview</span>
          </h3>
          <div className="dvw-advanced-panel__section-body">
            {command ? (
              <code className="dvw-advanced__code">{command}</code>
            ) : (
              <div className="dvw-advanced__callout">
                <span className="dvw-advanced__callout-icon" aria-hidden="true">
                  <IconInfoCircle />
                </span>
                <p className="dvw-advanced__callout-text">
                  Run a readiness check to project the command the app would build.
                  This is a preview — the app rebuilds the command before any run.
                </p>
              </div>
            )}
            {command ? (
              <button
                type="button"
                className="dvw-btn dvw-advanced-panel__copy"
                onClick={handleCopy}
                tabIndex={controlTabIndex}
              >
                <IconCopy className="dvw-btn__icon" />
                <span>{copied ? 'Copied' : 'Copy to clipboard'}</span>
              </button>
            ) : null}
          </div>
        </section>

        {/* Section 2 — Permissions (collapsible, populated after a readiness check). */}
        <details className="dvw-advanced__item dvw-advanced-panel__perms" open>
          <summary className="dvw-advanced__summary" tabIndex={controlTabIndex}>
            <span className="dvw-advanced__summary-icon" aria-hidden="true">
              <IconShieldCheck />
            </span>
            <span className="dvw-advanced__summary-text">
              <span className="dvw-advanced__summary-title">Permissions</span>
              <span className="dvw-advanced__summary-sub">Graph API permissions</span>
            </span>
          </summary>
          <div className="dvw-advanced__body">
            {phase !== 'loaded' ? (
              <div className="dvw-advanced__callout">
                <span className="dvw-advanced__callout-icon" aria-hidden="true">
                  <IconInfoCircle />
                </span>
                <p className="dvw-advanced__callout-text">
                  Run Check Readiness to see required permissions.
                </p>
              </div>
            ) : graphPermissions.length > 0 ? (
              <ul className="dvw-advanced__perms">
                {graphPermissions.map(perm => (
                  <li key={perm.id} className="dvw-advanced__perm">
                    <span className="dvw-advanced__perm-name">{perm.name}</span>
                    <span className="dvw-advanced__perm-meta">
                      {perm.requiredBecause}
                    </span>
                    <span className="dvw-advanced__perm-meta">
                      Applies to: {perm.appliesToLabel ?? perm.appliesTo}
                    </span>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="dvw-card__muted">
                No additional Graph permissions are required for this recipe.
              </p>
            )}
          </div>
        </details>

        {/* Section 3 — Recipe Info (bottom). */}
        <section className="dvw-advanced-panel__section">
          <h3 className="dvw-advanced-panel__section-title">
            <span className="dvw-advanced-panel__section-icon" aria-hidden="true">
              <IconInfoCircle />
            </span>
            <span>Recipe Info</span>
          </h3>
          <div className="dvw-advanced-panel__section-body">
            {diagnostics ? (
              <dl className="dvw-advanced__diag">
                <div className="dvw-advanced__diag-row">
                  <dt className="dvw-advanced__diag-label">Recipe ID</dt>
                  <dd className="dvw-advanced__diag-value">{diagnostics.recipeId}</dd>
                </div>
                {diagnostics.lastBake ? (
                  <div className="dvw-advanced__diag-row">
                    <dt className="dvw-advanced__diag-label">Last bake</dt>
                    <dd className="dvw-advanced__diag-value">
                      {formatLastBake(diagnostics.lastBake)}
                    </dd>
                  </div>
                ) : null}
                {diagnostics.engineVersion ? (
                  <div className="dvw-advanced__diag-row">
                    <dt className="dvw-advanced__diag-label">Engine version</dt>
                    <dd className="dvw-advanced__diag-value">
                      {diagnostics.engineVersion}
                    </dd>
                  </div>
                ) : null}
                {diagnostics.appVersion ? (
                  <div className="dvw-advanced__diag-row">
                    <dt className="dvw-advanced__diag-label">App version</dt>
                    <dd className="dvw-advanced__diag-value">
                      {diagnostics.appVersion}
                    </dd>
                  </div>
                ) : null}
              </dl>
            ) : null}
            {result?.errors && result.errors.length > 0 ? (
              <ul className="dvw-advanced__notes">
                {result.errors.map((note, i) => (
                  <li key={'e' + i}>{note}</li>
                ))}
              </ul>
            ) : null}
            {result?.warnings && result.warnings.length > 0 ? (
              <ul className="dvw-advanced__notes">
                {result.warnings.map((note, i) => (
                  <li key={'w' + i}>{note}</li>
                ))}
              </ul>
            ) : null}
            {!diagnostics && !result ? (
              <p className="dvw-card__muted">
                Recipe info appears here after you select a recipe.
              </p>
            ) : null}
          </div>
        </section>
      </div>
    </aside>
  );
}

/**
 * Compact one-line description of the last recorded bake: status, when, and the
 * process exit code when present. Read-only display of cook history.
 */
function formatLastBake(bake: DiagnosticsLastBake): string {
  const parts: string[] = [bake.status];
  const when = formatModified(bake.when);
  if (when) {
    parts.push(when);
  }
  if (bake.exitCode !== null) {
    parts.push(`exit ${bake.exitCode}`);
  }
  return parts.join(' \u00b7 ');
}
