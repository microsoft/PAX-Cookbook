import type { ReactNode } from 'react';

interface MiniKitchenFieldProps {
  /** Label text. */
  label: string;
  /** Unique id of the control rendered inside `children`. */
  htmlFor: string;
  /** Optional hint shown below the label, above the control. */
  hint?: string;
  /** Optional warnings (e.g. path-shape warnings) shown below the control. */
  warnings?: readonly string[];
  /** Optional informational notes shown below warnings. */
  notes?: readonly string[];
  /** True when this whole field is in an inactive state. Greys out the label. */
  disabled?: boolean;
  /** True if the user must fill this in before the lite recipe is meaningful. Pure visual cue; no validation. */
  optional?: boolean;
  /**
   * True when this field must be filled before the recipe can be saved. Renders
   * a subtle "required" marker beside the label. Pure visual cue — the actual
   * save gating lives in the broker's rules and the builder's save requirements.
   */
  required?: boolean;
  /** Optional element rendered inline after the label text (e.g. a requirement pill). */
  labelBadge?: ReactNode;
  children: ReactNode;
}

/**
 * Labelled field wrapper. Binds `label.htmlFor` to the consumer's control
 * via the `htmlFor` prop, and links the field's `hint` to the control via
 * `aria-describedby` (the consumer must wire that on its input).
 */
export function MiniKitchenField({
  label,
  htmlFor,
  hint,
  warnings,
  notes,
  disabled = false,
  optional = false,
  required = false,
  labelBadge,
  children,
}: MiniKitchenFieldProps) {
  const hintId = hint ? `${htmlFor}-hint` : undefined;
  return (
    <div
      className={`mk-field${disabled ? ' mk-field--disabled' : ''}`}
      aria-disabled={disabled || undefined}
    >
      <label className="mk-field__label" htmlFor={htmlFor}>
        <span className="mk-field__label-text">{label}</span>
        {optional ? <span className="mk-field__optional">optional</span> : null}
        {required ? (
          <span className="mk-field__required" title="Required to save">
            required
          </span>
        ) : null}
        {labelBadge ?? null}
      </label>
      {hint ? (
        <p id={hintId} className="mk-field__hint">
          {hint}
        </p>
      ) : null}
      <div className="mk-field__control">{children}</div>
      {warnings && warnings.length > 0 ? (
        <ul className="mk-field__warnings" role="list">
          {warnings.map((w, i) => (
            <li key={i} className="mk-field__warning">
              {w}
            </li>
          ))}
        </ul>
      ) : null}
      {notes && notes.length > 0 ? (
        <ul className="mk-field__notes" role="list">
          {notes.map((n, i) => (
            <li key={i} className="mk-field__note">
              {n}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
