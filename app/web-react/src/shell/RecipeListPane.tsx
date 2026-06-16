/**
 * Saved-recipes list pane for the Recipes workspace (left column).
 *
 * Read-only presentation of the broker's recipe list with a search box.
 * Selecting a row asks the workspace to load that recipe's detail; it never
 * runs PAX, bakes, deletes, renames, or schedules.
 */
import type { RecipeSummary } from '../host/brokerBridge';
import type { PresetId } from '../features/mini-kitchen/types';
import { formatModified } from './shellNav';
import { RecipeStatusBadge, toneForRecipeStatus } from './StatusCard';
import {
  IconBook,
  IconPencil,
  IconSearch,
} from './CookbookIllustrations';
import { powerBiTemplateUrl } from '../features/mini-kitchen/data/powerBiTemplateLinks';
import { DashboardRepoPills } from '../features/mini-kitchen/components/DashboardRepoPills';

export type ListPhase = 'idle' | 'loading' | 'loaded' | 'error';

interface RecipeListPaneProps {
  phase: ListPhase;
  recipes: readonly RecipeSummary[];
  selectedId: string | null;
  search: string;
  onSearch: (value: string) => void;
  onSelect: (recipe: RecipeSummary) => void;
  onEditRecipe: (recipeId: string) => void;
  onPickPreset: (presetId: PresetId) => void;
  onResume: () => void;
  onImportCommand: () => void;
}

function recipeName(recipe: RecipeSummary): string {
  return typeof recipe.name === 'string' && recipe.name.trim().length > 0
    ? recipe.name.trim()
    : 'Untitled recipe';
}

// Fixed "Start from a template" starting points shown at the foot of the saved
// list. Preset cards seed a fresh, unsaved editor draft client-side (no PAX, no
// cook, no broker write); the Resume and Import Command cards reuse the
// workspace's existing modal handlers. Repo cards render the Power BI repo
// pills; the two import cards show a file-type tag.
type StartCard =
  | { kind: 'preset'; id: string; title: string; desc: string; presetId: PresetId; pills: 'repo' | 'filetype-pax' | 'filetype-paxlite' | 'none' }
  | { kind: 'resume'; id: string; title: string; desc: string }
  | { kind: 'import-command'; id: string; title: string; desc: string };

const START_CARDS: readonly StartCard[] = [
  { kind: 'preset', id: 'aiInOneDashboard', title: 'AI-in-One Dashboard', desc: 'Copilot adoption rollup for the AI-in-One Power BI dashboard.', presetId: 'aiInOneDashboard', pills: 'repo' },
  { kind: 'preset', id: 'aiBusinessValueDashboard', title: 'AI Business Value Dashboard', desc: 'Copilot ROI superset for the AI Business Value dashboard.', presetId: 'aiBusinessValueDashboard', pills: 'repo' },
  { kind: 'preset', id: 'm365UsageAnalyticsDashboard', title: 'M365 Usage Analytics Dashboard', desc: 'Broad M365 usage (Exchange, SharePoint, OneDrive, Teams) for the M365 Usage dashboard.', presetId: 'm365UsageAnalyticsDashboard', pills: 'repo' },
  { kind: 'preset', id: 'userInfoOnly', title: 'Entra Directory Export', desc: 'Export Entra user directory metadata — departments, managers, locations — without pulling audit logs.', presetId: 'userInfoOnly', pills: 'none' },
  { kind: 'preset', id: 'customAuditExport', title: 'Custom Audit Export', desc: 'Blank-slate audit export — choose your own scope and filters.', presetId: 'customAuditExport', pills: 'none' },
  { kind: 'preset', id: 'importPaxRecipeJson', title: 'Import PAX Cookbook .pax Recipe', desc: 'Open a full .pax recipe export from disk.', presetId: 'importPaxRecipeJson', pills: 'filetype-pax' },
  { kind: 'preset', id: 'importLiteRecipeJson', title: 'Import Mini-Kitchen .paxlite Recipe', desc: 'Open a Mini-Kitchen .paxlite recipe from disk.', presetId: 'importLiteRecipeJson', pills: 'filetype-paxlite' },
  { kind: 'resume', id: 'resume', title: 'Resume from Checkpoint', desc: 'Recover an interrupted run from its checkpoint.' },
  { kind: 'import-command', id: 'importCommand', title: 'Import Command', desc: 'Paste a PAX command and turn it into a recipe.' },
];

export function RecipeListPane({
  phase,
  recipes,
  selectedId,
  search,
  onSearch,
  onSelect,
  onEditRecipe,
  onPickPreset,
  onResume,
  onImportCommand,
}: RecipeListPaneProps) {
  const query = search.trim().toLowerCase();
  const visible =
    query.length === 0
      ? recipes
      : recipes.filter(r => recipeName(r).toLowerCase().includes(query));

  return (
    <section className="dvw-card dvw-list" aria-labelledby="dvw-list-h">
      <header className="dvw-card__head">
        <h2 id="dvw-list-h" className="dvw-card__title">
          Saved Recipes
        </h2>
      </header>

      <div className="dvw-list__search">
        <span className="dvw-list__search-icon" aria-hidden="true">
          <IconSearch />
        </span>
        <input
          type="search"
          className="dvw-list__search-input"
          placeholder="Search recipes"
          value={search}
          onChange={e => onSearch(e.target.value)}
          aria-label="Search recipes"
        />
      </div>

      {phase === 'loading' ? (
        <p className="dvw-card__muted" role="status">
          Loading recipes…
        </p>
      ) : null}

      {phase === 'error' ? (
        <p className="dvw-card__muted" role="status">
          PAX Cookbook could not load your recipes. Make sure it is running.
        </p>
      ) : null}

      {phase === 'loaded' && recipes.length === 0 ? (
        <p className="dvw-card__muted" role="status">
          No saved recipes yet. Create a recipe to see it here.
        </p>
      ) : null}

      {phase === 'loaded' && recipes.length > 0 && visible.length === 0 ? (
        <p className="dvw-card__muted" role="status">
          No recipes match "{search.trim()}".
        </p>
      ) : null}

      {visible.length > 0 ? (
        <ul className="dvw-list__rows" role="listbox" aria-label="Saved recipes">
          {visible.map(recipe => {
            const selected = recipe.recipeId === selectedId;
            const tone = toneForRecipeStatus(recipe['status']);
            const modified = formatModified(recipe['updatedAt']);
            return (
              <li key={recipe.recipeId} className="dvw-list__item">
                <button
                  type="button"
                  role="option"
                  aria-selected={selected}
                  className={
                    'dvw-list__row' + (selected ? ' dvw-list__row--selected' : '')
                  }
                  onClick={() => onSelect(recipe)}
                >
                  <span className="dvw-list__row-icon" aria-hidden="true">
                    <IconBook />
                  </span>
                  <span className="dvw-list__row-text">
                    <span className="dvw-list__row-name">{recipeName(recipe)}</span>
                    <span className="dvw-list__row-meta">
                      {modified ? `Modified ${modified}` : 'Saved recipe'}
                    </span>
                  </span>
                  <RecipeStatusBadge tone={tone} />
                </button>
                <button
                  type="button"
                  className="dvw-list__row-edit"
                  onClick={() => onEditRecipe(recipe.recipeId)}
                  aria-label={`Edit ${recipeName(recipe)}`}
                  title="Edit this recipe"
                >
                  <IconPencil />
                </button>
              </li>
            );
          })}
        </ul>
      ) : null}

      <section className="dvw-list__templates" aria-labelledby="dvw-list-templates-h">
        <h3 id="dvw-list-templates-h" className="dvw-list__templates-head">
          Start from a template
        </h3>
        <ul className="dvw-tpl-grid" aria-label="Recipe starting points">
          {START_CARDS.map(card => {
            const onClick =
              card.kind === 'preset' ? () => onPickPreset(card.presetId)
              : card.kind === 'resume' ? onResume
              : onImportCommand;
            const repoUrl =
              card.kind === 'preset' && card.pills === 'repo'
                ? powerBiTemplateUrl(card.presetId)
                : null;
            return (
              <li key={card.id} className="dvw-tpl-card">
                <button
                  type="button"
                  className="dvw-tpl-card__btn"
                  onClick={onClick}
                  title={card.desc}
                >
                  <span className="dvw-tpl-card__title">{card.title}</span>
                  <span className="dvw-tpl-card__desc">{card.desc}</span>
                </button>
                {repoUrl ? <DashboardRepoPills url={repoUrl} /> : null}
                {card.kind === 'preset' && card.pills === 'filetype-pax' ? (
                  <span className="dvw-tpl-card__filetype">.pax</span>
                ) : null}
                {card.kind === 'preset' && card.pills === 'filetype-paxlite' ? (
                  <span className="dvw-tpl-card__filetype">.paxlite</span>
                ) : null}
              </li>
            );
          })}
        </ul>
      </section>
    </section>
  );
}
