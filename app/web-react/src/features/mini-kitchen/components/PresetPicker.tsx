import { DASHBOARD_PRESETS } from '../data/dashboard-presets';
import { powerBiTemplateUrl } from '../data/powerBiTemplateLinks';
import type { PresetId } from '../types';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import { DashboardReqTag, type DashboardScope } from './DashboardRequirement';
import { DashboardRepoPills } from './DashboardRepoPills';

interface PresetPickerProps {
  selected: PresetId;
  onSelect: (id: PresetId) => void;
  /** Invoked when the Import card is activated; opens the file picker. */
  onImportClick: () => void;
}

/**
 * Dashboard-scope pill shown under a preset title. The dashboard presets carry
 * one; every other card reserves the same row height so all descriptions stay
 * vertically aligned across the grid.
 */
const PRESET_SCOPE: Partial<Record<PresetId, DashboardScope>> = {
  aiInOneDashboard: 'ai-in-one',
  aiBusinessValueDashboard: 'ai-business-value',
  m365UsageAnalyticsDashboard: 'm365-usage',
  userInfoOnly: 'entra-user-info',
};

export function PresetPicker({ selected, onSelect, onImportClick }: PresetPickerProps) {
  return (
    <MiniKitchenSectionCard
      id="mk-preset"
      title="Start with a recipe"
      subtitle="Seven starting points. Pick one, then refine the steps below."
      helpText="Switching recipes replaces the form with that recipe's defaults. PAX Cookbook does not save or compare recipes."
    >
      <div className="mk-preset-grid" role="radiogroup" aria-label="Dashboard preset">
        {DASHBOARD_PRESETS.map(preset => {
          const isImport =
            preset.id === 'importLiteRecipeJson' ||
            preset.id === 'importPaxRecipeJson';
          const isSelected = selected === preset.id;
          const inputId = `mk-preset-${preset.id}`;
          const scope = PRESET_SCOPE[preset.id];
          return (
            <label
              key={preset.id}
              htmlFor={inputId}
              className={
                'mk-preset-option' +
                (isSelected ? ' mk-preset-option--selected' : '')
              }
            >
              <input
                type="radio"
                id={inputId}
                name="mk-preset"
                value={preset.id}
                className="mk-preset-option__input"
                checked={isSelected}
                onChange={() => {
                  if (isImport) {
                    onImportClick();
                  } else {
                    onSelect(preset.id);
                  }
                }}
                onClick={() => {
                  if (isImport) {
                    onImportClick();
                  } else {
                    onSelect(preset.id);
                  }
                }}
              />
              <span className="mk-preset-option__title">{preset.name}</span>
              <span className="mk-preset-option__pillrow">
                {scope ? <DashboardReqTag scopes={[scope]} /> : null}
                {isImport ? (
                  <span className="mk-preset-option__filetype">
                    {preset.id === 'importPaxRecipeJson' ? '.pax' : '.paxlite'}
                  </span>
                ) : null}
              </span>
              <span className="mk-preset-option__desc">{preset.description}</span>
              {preset.notes.length > 0 ? (
                <ul className="mk-preset-option__notes">
                  {preset.notes.map((n, i) => (
                    <li key={i}>{n}</li>
                  ))}
                </ul>
              ) : null}
              {powerBiTemplateUrl(preset.id) ? (
                <DashboardRepoPills url={powerBiTemplateUrl(preset.id)!} />
              ) : null}
            </label>
          );
        })}
      </div>
    </MiniKitchenSectionCard>
  );
}

