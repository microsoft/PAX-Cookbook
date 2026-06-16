import { useEffect, useState } from 'react';
import type { LiteRecipeIdentity } from '../types';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import { MiniKitchenField } from './MiniKitchenField';
import { DashboardReqTag, USER_INFO_RUN_SCOPES } from './DashboardRequirement';

interface RecipeBasicsProps {
  value: LiteRecipeIdentity;
  onChange: (next: LiteRecipeIdentity) => void;
  /** Inline error to show under the name field (empty / reserved / duplicate). */
  nameError?: string | null;
}

function parseTags(text: string): string[] {
  return text
    .split(',')
    .map(t => t.trim())
    .filter(t => t.length > 0);
}

export function RecipeBasics({ value, onChange, nameError }: RecipeBasicsProps) {
  // Keep the raw text the user is typing as the source of truth for the
  // input so commas and spaces survive each keystroke. The parsed array is
  // pushed up on change; we only re-sync the text when the identity's tags
  // change to something that does not match what the current text parses to
  // (for example when a saved recipe is loaded).
  const [tagsText, setTagsText] = useState(() => (value.tags ?? []).join(', '));
  useEffect(() => {
    const incoming = value.tags ?? [];
    const parsed = parseTags(tagsText);
    const matches =
      parsed.length === incoming.length &&
      parsed.every((t, i) => t === incoming[i]);
    if (!matches) {
      setTagsText(incoming.join(', '));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value.tags]);
  const nameWarnings = nameError ? [nameError] : undefined;
  return (
    <MiniKitchenSectionCard
      id="mk-basics"
      title="Name the recipe"
      subtitle="Give this draft a unique name so you can find it later on this device."
      helpText="Names are scoped to this device. PAX Cookbook blocks empty names, reserved preset names, and duplicates of recipes already saved here."
    >
      <MiniKitchenField
        label="Name"
        htmlFor="mk-basics-name"
        warnings={nameWarnings}
        required
        labelBadge={<DashboardReqTag scopes={USER_INFO_RUN_SCOPES} />}
      >
        <input
          id="mk-basics-name"
          type="text"
          className={'mk-input' + (nameError ? ' mk-input--invalid' : '')}
          value={value.name}
          placeholder="e.g. AI-in-One weekly export"
          autoComplete="off"
          spellCheck
          aria-required={true}
          aria-invalid={nameError ? true : undefined}
          onChange={e => onChange({ ...value, name: e.target.value })}
        />
      </MiniKitchenField>
      <MiniKitchenField
        label="Description"
        htmlFor="mk-basics-desc"
        hint="A short summary of what this recipe pulls and where it lands."
        optional
      >
        <textarea
          id="mk-basics-desc"
          className="mk-input mk-input--textarea"
          rows={2}
          value={value.description ?? ''}
          placeholder="Optional. One or two sentences."
          onChange={e => onChange({ ...value, description: e.target.value })}
        />
      </MiniKitchenField>
      <MiniKitchenField
        label="Tags"
        htmlFor="mk-basics-tags"
        hint="Comma-separated. Only used to label this recipe in your saved-recipes list."
        optional
      >
        <input
          id="mk-basics-tags"
          type="text"
          className="mk-input"
          value={tagsText}
          placeholder="copilot, dashboard, weekly"
          autoComplete="off"
          onChange={e => {
            setTagsText(e.target.value);
            onChange({ ...value, tags: parseTags(e.target.value) });
          }}
        />
      </MiniKitchenField>
      <MiniKitchenField
        label="Notes"
        htmlFor="mk-basics-notes"
        hint="Free-form context for yourself. Travels with the recipe on export."
        optional
      >
        <textarea
          id="mk-basics-notes"
          className="mk-input mk-input--textarea"
          rows={3}
          value={value.notes ?? ''}
          placeholder="Optional. Caveats, owner, schedule expectations, etc."
          onChange={e => onChange({ ...value, notes: e.target.value })}
        />
      </MiniKitchenField>
    </MiniKitchenSectionCard>
  );
}
