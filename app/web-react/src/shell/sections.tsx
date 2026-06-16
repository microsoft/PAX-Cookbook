/**
 * PAX Cookbook product-shell section views.
 *
 * Every section here renders a live product surface. No section runs PAX, cooks
 * a recipe, or shows secrets.
 */
import type { ReactNode } from 'react';
import { DesktopHome } from './DesktopHome';
import { RecipesWorkspace } from './RecipesWorkspace';
import { PantryWorkspace } from './PantryWorkspace';
import { SettingsWorkspace } from './SettingsWorkspace';
import { ChefsKeysWorkspace } from './ChefsKeysWorkspace';
import { TasteTestsWorkspace } from './TasteTestsWorkspace';
import { BakesWorkspace } from './BakesWorkspace';
import { UpdatesWorkspace } from './UpdatesWorkspace';

export interface ShellSection {
  id: string;
  label: string;
  /** A token reference (e.g. var(--c-blue)) used as the section accent. */
  accent: string;
  body: ReactNode;
  /**
   * When true, the section is still reachable (active-body lookup and
   * requestShellSection still resolve it) but is not rendered as a top-level
   * left-nav button. Taste Tests uses this: its readiness/preflight entry now
   * lives in the recipe builder action bar, and past results stay reachable via
   * the Bakes "Open Taste Tests" link. Pantry uses it too: its template
   * browsing now lives in the Recipes homepage ("Start from a template"), and
   * the builder's Step 1 preset cards still mount it through the same lookup.
   */
  hideFromNav?: boolean;
}

function HomeView() {
  return <DesktopHome />;
}

function PantryView() {
  return <PantryWorkspace />;
}

function RecipesView() {
  return <RecipesWorkspace />;
}

function BakesView() {
  return <BakesWorkspace />;
}

function TasteTestsView() {
  return <TasteTestsWorkspace />;
}

function ChefsKeysView() {
  return <ChefsKeysWorkspace />;
}

function SettingsView() {
  return <SettingsWorkspace />;
}

function UpdatesView() {
  return <UpdatesWorkspace />;
}

export const SHELL_SECTIONS: ReadonlyArray<ShellSection> = [
  { id: 'home', label: 'Home', accent: 'var(--c-blue)', body: <HomeView /> },
  {
    id: 'pantry',
    label: 'Pantry',
    accent: 'var(--c-teal)',
    body: <PantryView />,
    hideFromNav: false,
  },
  { id: 'recipes', label: 'Recipes', accent: 'var(--c-indigo)', body: <RecipesView /> },
  { id: 'bakes', label: 'Bakes', accent: 'var(--c-amber)', body: <BakesView /> },
  {
    id: 'tastetests',
    label: 'Taste Tests',
    accent: 'var(--c-green)',
    body: <TasteTestsView />,
    hideFromNav: true,
  },
  { id: 'chefskeys', label: "Chef's Keys", accent: 'var(--c-purple)', body: <ChefsKeysView /> },
  { id: 'settings', label: 'Settings', accent: 'var(--c-slate)', body: <SettingsView /> },
  { id: 'updates', label: 'Updates', accent: 'var(--c-slate)', body: <UpdatesView /> },
];
