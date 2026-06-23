import { useMemo } from 'react';
import { analyzeAdvancedArgs } from '../lib/advancedArgs';
import { REMOVED_OR_UNSUPPORTED_SWITCHES } from '../data/pax-switch-catalog';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import { MiniKitchenField } from './MiniKitchenField';

interface AdvancedArgsCardProps {
  value: string;
  onChange: (next: string) => void;
}

/**
 * Phase 5: Advanced args card. Lets the user append raw PowerShell-style
 * `-Switch Value` tokens to the rendered command. Analysis is purely
 * informational — Mini-Kitchen never blocks typing.
 *
 * The renderer applies the same `analyzeAdvancedArgs` pipeline server-side,
 * so the warnings shown here mirror what will ultimately surface in the
 * generated command preview (Phase 6).
 */
export function AdvancedArgsCard({ value, onChange }: AdvancedArgsCardProps) {
  const analysis = useMemo(() => analyzeAdvancedArgs(value), [value]);
  const warnings: string[] = [];
  for (const dup of analysis.duplicates) {
    warnings.push(`-${dup} is specified more than once. The last value will win.`);
  }
  for (const removed of analysis.removedSwitches) {
    const def = REMOVED_OR_UNSUPPORTED_SWITCHES.find(s => s.name === removed);
    const label = def?.userFacingName ?? `-${removed}`;
    warnings.push(
      `${label} is a removed / unsupported switch and will be dropped from the rendered command.`,
    );
  }
  for (const unknown of analysis.unknownSwitches) {
    warnings.push(
      `-${unknown} is not in the builder's switch catalog. It will pass through unchanged.`,
    );
  }
  if (analysis.secretWarnings.length > 0) {
    warnings.push(
      'A token in this field looks like a client secret or password. The builder does not store this value, but recipe drafts you export will contain it as plain text.',
    );
  }
  const notes: string[] = [];
  if (analysis.tokens.length > 0) {
    notes.push(
      `Parsed ${analysis.tokens.length} token${analysis.tokens.length === 1 ? '' : 's'}; ${analysis.switchesPresent.length} switch${analysis.switchesPresent.length === 1 ? '' : 'es'} present.`,
    );
  }

  return (
    <MiniKitchenSectionCard
      id="mk-advanced"
      title="Advanced options (switches)"
      subtitle="Optional — most bakes don't need anything here. Only add raw PAX switches for edge cases the guided options above don't cover."
      helpText="Use carefully. The builder does not validate values — it only flags removed, duplicate, unknown, or secret-shaped tokens. Documentation for the available switches is linked below."
    >
      <MiniKitchenField
        label="Extra switches"
        htmlFor="mk-advanced-extra"
        hint="Space-separated, PowerShell-style tokens."
        optional
        warnings={warnings}
        notes={notes}
      >
        <textarea
          id="mk-advanced-extra"
          className="mk-input mk-input--textarea mk-input--code"
          rows={3}
          value={value}
          spellCheck={false}
          autoComplete="off"
          placeholder="-PartitionHours 6"
          onChange={e => onChange(e.target.value)}
        />
      </MiniKitchenField>
      <p className="mk-field__note">
        For detailed information on all supported PAX switches and options, see the{' '}
        <a
          href="https://github.com/microsoft/PAX/blob/release/release_documentation/Purview_Audit_Log_Processor/PAX_Purview_Audit_Log_Processor_Documentation_v1.11.x.md"
          target="_blank"
          rel="noopener noreferrer"
        >
          PAX Purview Audit Log Processor documentation
        </a>
        .
      </p>
    </MiniKitchenSectionCard>
  );
}
