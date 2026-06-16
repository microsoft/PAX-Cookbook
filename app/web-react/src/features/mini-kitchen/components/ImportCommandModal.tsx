/**
 * Import-command review modal.
 *
 * Two phases over a single dialog:
 *  1. Edit — a monospace textarea + "Preview import". The pasted text is parsed
 *     by `parsePaxCommand`; a rejection (empty, secret detected, or no
 *     recognizable PAX switches) shows a friendly message and nothing is saved
 *     or loaded.
 *  2. Review — Applied / Not applied / Notes sections, a recipe-name field, and
 *     four actions: Cancel, Load without saving, Save, Save & load (plus an
 *     "Edit command" step back to phase 1).
 *
 * Boundaries: nothing here runs the pasted command, spawns PowerShell, or
 * touches the PAX engine. Save persists through the broker's authenticated
 * recipe route (the parent owns the call); Load without saving pushes the
 * parsed state into the editor as an unsaved draft.
 */
import {
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type FormEvent,
  type MouseEvent,
} from 'react';
import type { MiniKitchenRecipeState } from '../types';
import { parsePaxCommand, type PaxCommandParseResult } from '../lib/paxCommandImporter';
import {
  deriveSaveRequirements,
  describeSaveRequirements,
} from '../lib/recipeSaveRequirements';
import type { RecipeSummary } from '../../../host/brokerBridge';
import { ContextualHelpButton } from '../../../components/ContextualHelpButton';

/** Result of a persist attempt the parent reports back to the modal. */
export interface CommandImportSaveOutcome {
  ok: boolean;
  message: string;
}

interface ImportCommandModalProps {
  /** Existing recipes, used to keep the chosen name unique. */
  existingRecipes: readonly RecipeSummary[];
  /** Cancel / Escape / backdrop click — nothing is saved or loaded. */
  onClose: () => void;
  /** Persist without loading into the editor. Resolves to a SaveOutcome. */
  onSave: (name: string, state: MiniKitchenRecipeState) => Promise<CommandImportSaveOutcome>;
  /** Persist and load into the editor. Resolves to a SaveOutcome. */
  onSaveAndLoad: (
    name: string,
    state: MiniKitchenRecipeState,
  ) => Promise<CommandImportSaveOutcome>;
  /** Load into the editor without saving. */
  onLoadOnly: (name: string, state: MiniKitchenRecipeState) => void;
}

const DEFAULT_IMPORT_NAME = 'Imported PAX command';

function validateName(
  name: string,
  existing: readonly RecipeSummary[],
): string | null {
  const trimmed = name.trim();
  if (!trimmed) {
    return 'Enter a name for this recipe.';
  }
  const clash = existing.some(
    r => (r.name ?? '').trim().toLowerCase() === trimmed.toLowerCase(),
  );
  if (clash) {
    return 'A recipe with this name already exists. Choose a different name.';
  }
  return null;
}

export function ImportCommandModal({
  existingRecipes,
  onClose,
  onSave,
  onSaveAndLoad,
  onLoadOnly,
}: ImportCommandModalProps) {
  const [commandText, setCommandText] = useState('');
  const [result, setResult] = useState<PaxCommandParseResult | null>(null);
  const [name, setName] = useState(DEFAULT_IMPORT_NAME);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const headingId = useId();
  const helpId = useId();
  const nameInputId = useId();
  const nameErrorId = useId();
  const commandInputId = useId();

  const inReview = result?.ok === true;

  useEffect(() => {
    if (!inReview) {
      textareaRef.current?.focus();
    }
  }, [inReview]);

  useEffect(() => {
    function onKey(ev: KeyboardEvent) {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        onClose();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const nameError = useMemo(
    () => validateName(name, existingRecipes),
    [name, existingRecipes],
  );

  // The state that would be persisted, with the chosen name applied. Used to
  // both compute the outstanding save requirements and to drive the actions.
  const stateForSave = useMemo<MiniKitchenRecipeState | null>(() => {
    if (!result?.state) {
      return null;
    }
    return {
      ...result.state,
      identity: { ...result.state.identity, name: name.trim() },
    };
  }, [result, name]);

  const saveRequirements = useMemo(
    () => (stateForSave ? deriveSaveRequirements(stateForSave) : []),
    [stateForSave],
  );
  const saveRequirementsText = describeSaveRequirements(saveRequirements);

  const canSave =
    inReview && nameError === null && saveRequirements.length === 0 && !saving;
  const canLoad = inReview && !saving;

  function handleBackdropClick(ev: MouseEvent<HTMLDivElement>) {
    if (ev.target === ev.currentTarget) {
      onClose();
    }
  }

  function handlePreview(ev: FormEvent<HTMLFormElement>) {
    ev.preventDefault();
    const parsed = parsePaxCommand(commandText);
    setResult(parsed);
    setSaveError(null);
    if (parsed.ok) {
      setName(DEFAULT_IMPORT_NAME);
    }
  }

  function handleBackToEdit() {
    setResult(null);
    setSaveError(null);
  }

  async function handleSaveOnly() {
    if (!stateForSave || !canSave) {
      return;
    }
    setSaving(true);
    setSaveError(null);
    try {
      const outcome = await onSave(name.trim(), stateForSave);
      if (!outcome.ok) {
        setSaveError(outcome.message);
      }
    } finally {
      setSaving(false);
    }
  }

  async function handleSaveAndLoad() {
    if (!stateForSave || !canSave) {
      return;
    }
    setSaving(true);
    setSaveError(null);
    try {
      const outcome = await onSaveAndLoad(name.trim(), stateForSave);
      if (!outcome.ok) {
        setSaveError(outcome.message);
      }
    } finally {
      setSaving(false);
    }
  }

  function handleLoadOnly() {
    if (!result?.state || !canLoad) {
      return;
    }
    onLoadOnly(name.trim(), result.state);
  }

  return (
    <div className="mk-modal__backdrop" role="presentation" onClick={handleBackdropClick}>
      <div
        className="mk-modal mk-modal--wide"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={helpId}
      >
        <header className="mk-modal__head">
          <h2 id={headingId} className="mk-modal__title">
            Import a PAX command
            <ContextualHelpButton topic="cookbookImportCommand" size="sm" />
          </h2>
          <p id={helpId} className="mk-modal__subtitle">
            Paste a full PAX command line — including its switches — and PAX Cookbook
            turns it into a recipe draft. Review what was picked up before you save or
            load it. Pasting a command never runs it.
          </p>
        </header>

        {!inReview ? (
          <form className="mk-modal__form" onSubmit={handlePreview} noValidate>
            <label className="mk-modal__label" htmlFor={commandInputId}>
              PAX command
            </label>
            <textarea
              ref={textareaRef}
              id={commandInputId}
              className="mk-input mk-modal__textarea"
              value={commandText}
              onChange={ev => setCommandText(ev.target.value)}
              rows={6}
              spellCheck={false}
              autoComplete="off"
              placeholder={
                'pwsh -NoProfile -ExecutionPolicy Bypass -File "C:\\PAX\\PAX.ps1" ' +
                '-StartDate 2026-01-01 -EndDate 2026-01-31 -ActivityTypes CopilotInteraction ...'
              }
            />
            {result && !result.ok ? (
              <p className="mk-modal__error" role="alert">
                {result.rejectedReason}
              </p>
            ) : (
              <p className="mk-modal__hint">
                PAX Cookbook never stores secrets. Leave out any client secret or
                password.
              </p>
            )}
            <div className="mk-modal__actions">
              <button type="button" className="mk-modal__button" onClick={onClose}>
                Cancel
              </button>
              <button
                type="submit"
                className="mk-modal__button mk-modal__button--primary"
                disabled={commandText.trim().length === 0}
              >
                Preview import
              </button>
            </div>
          </form>
        ) : (
          <div className="mk-modal__form">
            <div className="mk-cmd-import">
              {result!.applied.length > 0 && (
                <section className="mk-cmd-import__section mk-cmd-import__section--applied">
                  <h3 className="mk-cmd-import__section-title">
                    <span aria-hidden="true">✓</span> Applied to this recipe
                    <span className="mk-cmd-import__count">{result!.applied.length}</span>
                  </h3>
                  <ul className="mk-cmd-import__list">
                    {result!.applied.map((item, i) => (
                      <li key={`a-${i}`} className="mk-cmd-import__row">
                        <code className="mk-cmd-import__switch">-{item.switchName}</code>
                        <span className="mk-cmd-import__arrow" aria-hidden="true">→</span>
                        <span className="mk-cmd-import__field">{item.fieldLabel}</span>
                        <span className="mk-cmd-import__detail">{item.detail}</span>
                      </li>
                    ))}
                  </ul>
                </section>
              )}

              {result!.notApplied.length > 0 && (
                <section className="mk-cmd-import__section mk-cmd-import__section--dropped">
                  <h3 className="mk-cmd-import__section-title">
                    <span aria-hidden="true">⚠</span> Not applied
                    <span className="mk-cmd-import__count">{result!.notApplied.length}</span>
                  </h3>
                  <ul className="mk-cmd-import__list">
                    {result!.notApplied.map((item, i) => (
                      <li key={`n-${i}`} className="mk-cmd-import__row">
                        <code className="mk-cmd-import__switch">-{item.label}</code>
                      </li>
                    ))}
                  </ul>
                  <p className="mk-cmd-import__note">
                    A switch can be left out because it was retired over time and PAX
                    Cookbook tracks the latest PAX script version, or because it is not a
                    PAX switch. If you still use it in a customized PAX script, you can
                    re-add it under Advanced arguments in Step 5.
                  </p>
                </section>
              )}

              {result!.notes.length > 0 && (
                <section className="mk-cmd-import__section mk-cmd-import__section--notes">
                  <ul className="mk-cmd-import__list">
                    {result!.notes.map((n, i) => (
                      <li key={`note-${i}`} className="mk-cmd-import__note-item">
                        {n}
                      </li>
                    ))}
                  </ul>
                </section>
              )}
            </div>

            <label className="mk-modal__label" htmlFor={nameInputId}>
              Recipe name
            </label>
            <input
              id={nameInputId}
              type="text"
              className={
                nameError
                  ? 'mk-input mk-modal__input mk-input--invalid'
                  : 'mk-input mk-modal__input'
              }
              value={name}
              onChange={ev => setName(ev.target.value)}
              aria-invalid={nameError ? 'true' : 'false'}
              aria-describedby={nameError ? nameErrorId : undefined}
              autoComplete="off"
              spellCheck={false}
              maxLength={200}
            />
            {nameError ? (
              <p id={nameErrorId} className="mk-modal__error" role="alert">
                {nameError}
              </p>
            ) : saveRequirements.length > 0 ? (
              <p className="mk-modal__hint">
                {saveRequirementsText} Load without saving to finish it in the editor.
              </p>
            ) : (
              <p className="mk-modal__hint">
                Save keeps this recipe in PAX Cookbook on this PC. Load without saving
                opens it in the editor and saves nothing.
              </p>
            )}

            {saveError && (
              <p className="mk-modal__error" role="alert">
                {saveError}
              </p>
            )}

            <div className="mk-modal__actions mk-modal__actions--wrap">
              <button
                type="button"
                className="mk-modal__button"
                onClick={onClose}
                disabled={saving}
              >
                Cancel
              </button>
              <button
                type="button"
                className="mk-modal__button"
                onClick={handleBackToEdit}
                disabled={saving}
              >
                Edit command
              </button>
              <button
                type="button"
                className="mk-modal__button"
                onClick={handleLoadOnly}
                disabled={!canLoad}
              >
                Load without saving
              </button>
              <button
                type="button"
                className="mk-modal__button"
                onClick={handleSaveOnly}
                disabled={!canSave}
                title={nameError ?? (saveRequirementsText || undefined)}
              >
                Save
              </button>
              <button
                type="button"
                className="mk-modal__button mk-modal__button--primary"
                onClick={handleSaveAndLoad}
                disabled={!canSave}
                title={nameError ?? (saveRequirementsText || undefined)}
              >
                Save &amp; load
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
