import { useState } from 'react';
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

// The starting points grouped into collapsible categories. One category is
// open at a time; the selected preset's category opens on mount.
const PRESET_CATEGORIES: ReadonlyArray<{
  id: string;
  title: string;
  presetIds: PresetId[];
}> = [
  {
    id: 'dashboards',
    title: 'Power BI Dashboard Templates',
    presetIds: ['aiInOneDashboard', 'aiBusinessValueDashboard', 'm365UsageAnalyticsDashboard'],
  },
  {
    id: 'exports',
    title: 'Specialized Exports',
    presetIds: ['userInfoOnly', 'customAuditExport'],
  },
  {
    id: 'import',
    title: 'Import or Resume',
    presetIds: ['importPaxRecipeJson', 'importLiteRecipeJson'],
  },
];

function categoryForPreset(id: PresetId): string | null {
  for (const c of PRESET_CATEGORIES) {
    if (c.presetIds.includes(id)) {
      return c.id;
    }
  }
  return null;
}

export function PresetPicker({ selected, onSelect, onImportClick }: PresetPickerProps) {
  // One category open at a time. Opens the selected preset's category on mount
  // (so arriving via a template shows its card pre-selected); collapsed when the
  // selection is not in any category.
  const [openCat, setOpenCat] = useState<string | null>(() => categoryForPreset(selected));

  function renderCard(presetId: PresetId) {
    const preset = DASHBOARD_PRESETS.find(p => p.id === presetId);
    if (!preset) {
      return null;
    }
    const isImport =
      preset.id === 'importLiteRecipeJson' || preset.id === 'importPaxRecipeJson';
    const isSelected = selected === preset.id;
    const inputId = `mk-preset-${preset.id}`;
    const scope = PRESET_SCOPE[preset.id];
    const activate = () => {
      if (isImport) {
        onImportClick();
      } else {
        onSelect(preset.id);
      }
    };
    return (
      <label
        key={preset.id}
        htmlFor={inputId}
        className={'mk-preset-option' + (isSelected ? ' mk-preset-option--selected' : '')}
      >
        <input
          type="radio"
          id={inputId}
          name="mk-preset"
          value={preset.id}
          className="mk-preset-option__input"
          checked={isSelected}
          onChange={activate}
          onClick={activate}
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
  }

  return (
    <MiniKitchenSectionCard
      id="mk-preset"
      title="Start with a recipe"
      subtitle="Pick a starting point by category, then refine the steps below."
      helpText="Switching recipes replaces the form with that recipe's defaults. PAX Cookbook does not save or compare recipes."
    >
      <div className="mk-preset-cats">
        {PRESET_CATEGORIES.map(cat => {
          const isOpen = openCat === cat.id;
          return (
            <div
              key={cat.id}
              className={'mk-preset-cat' + (isOpen ? ' mk-preset-cat--open' : '')}
            >
              <button
                type="button"
                className="mk-preset-cat__head"
                aria-expanded={isOpen}
                onClick={() => setOpenCat(isOpen ? null : cat.id)}
              >
                <span className="mk-preset-cat__title">{cat.title}</span>
                <span className="mk-preset-cat__chevron" aria-hidden="true" />
              </button>
              {isOpen ? (
                <div className="mk-preset-grid" role="radiogroup" aria-label={cat.title}>
                  {cat.presetIds.map(pid => renderCard(pid))}
                </div>
              ) : null}
            </div>
          );
        })}
      </div>
    </MiniKitchenSectionCard>
  );
}

