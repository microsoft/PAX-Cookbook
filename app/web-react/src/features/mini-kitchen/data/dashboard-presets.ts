/**
 * Built-in dashboard presets.
 *
 * Each preset narrows a subset of fields on top of the base default recipe
 * created by `lib/defaultRecipe.ts`. Names listed here are reserved; users
 * cannot save a custom recipe under a built-in preset name.
 *
 * Preset behavior is documented in master plan §8.
 */

import type { DashboardPresetDefinition, PresetId } from '../types';

export const DASHBOARD_PRESETS: readonly DashboardPresetDefinition[] = [
  {
    id: 'aiInOneDashboard',
    name: 'AI-in-One Dashboard',
    description:
      'Copilot interactions plus Entra user info, rolled up for the AI-in-One dashboard. Does not pull the broader M365 audit usage bundle.',
    notes: [
      'Does NOT use -IncludeM365Usage.',
      'Does NOT pull Exchange / OneDrive / SharePoint / Teams audit branches.',
      'Leaves ActivityTypes blank so PAX uses its default (CopilotInteraction).',
      'Rollup mode is on by default.',
    ],
    overrides: {
      queryMode: 'audit-query',
      identityName: 'AI-in-One Dashboard',
      identityDescription:
        'Copilot interaction rollup with Entra user info for the AI-in-One dashboard.',
      includeM365Usage: false,
      includeUserInfo: true,
      rollup: 'rollup',
      dashboard: 'aio',
    },
  },
  {
    id: 'aiBusinessValueDashboard',
    name: 'AI Business Value Dashboard',
    description:
      'Copilot interactions plus Entra user info, rolled up for the AI Business Value dashboard. Does not pull the broader M365 audit usage bundle.',
    notes: [
      'Does NOT use -IncludeM365Usage.',
      'Does NOT pull Exchange / OneDrive / SharePoint / Teams audit branches.',
      'Leaves ActivityTypes blank so PAX uses its default (CopilotInteraction).',
      'Rollup mode is on by default.',
      'Targets the AI Business Value 50-column superset dashboard.',
    ],
    overrides: {
      queryMode: 'audit-query',
      identityName: 'AI Business Value Dashboard',
      identityDescription:
        'Copilot interaction rollup with Entra user info for the AI Business Value dashboard.',
      includeM365Usage: false,
      includeUserInfo: true,
      rollup: 'rollup',
      dashboard: 'aibv',
    },
  },
  {
    id: 'm365UsageAnalyticsDashboard',
    name: 'M365 Usage Dashboard',
    description:
      'Broader M365 audit usage bundle (Exchange / OneDrive / SharePoint / Teams) plus Copilot interactions, rolled up for the M365 Usage dashboard.',
    notes: [
      'Uses -IncludeM365Usage to pull the broader M365 audit bundle.',
      'Adds Exchange / OneDrive / SharePoint / Teams audit branches alongside CopilotInteraction.',
      'Entra user info is ON by default so the dashboard has name / department fields.',
      'Rollup mode is on by default.',
    ],
    overrides: {
      queryMode: 'audit-query',
      identityName: 'M365 Usage Dashboard',
      identityDescription:
        'Broader M365 usage bundle plus Copilot interactions, rolled up for the M365 Usage dashboard.',
      includeM365Usage: true,
      includeUserInfo: true,
      rollup: 'rollup',
    },
  },
  {
    id: 'userInfoOnly',
    name: 'Entra user info only',
    description:
      'Skip the audit query and pull Entra user info only. Maps to -OnlyUserInfo at the command level.',
    notes: [
      'Forces -OnlyUserInfo. Audit-only fields (date range, activity types, rollup, audit output) are hidden.',
      'IncludeUserInfo is implied.',
    ],
    overrides: {
      queryMode: 'user-info-only',
      identityName: 'Entra user info only',
      identityDescription: 'Entra user info pull. No audit query.',
      includeUserInfo: true,
      onlyUserInfo: true,
      userInfoOutputMode: 'write-new',
    },
  },
  {
    id: 'customAuditExport',
    name: 'Custom audit export',
    description:
      'Empty audit-query starting point. Use when none of the dashboard presets apply.',
    notes: [
      'No activity types, user filters, or rollup applied by default.',
      'Pick a date range and filters yourself.',
    ],
    overrides: {
      queryMode: 'audit-query',
      identityName: 'Custom audit export',
      identityDescription: 'Hand-rolled audit query.',
      rollup: 'none',
    },
  },
  {
    id: 'importPaxRecipeJson',
    name: 'Import PAX Cookbook .pax Recipe',
    description:
      'Load a full PAX Cookbook .pax recipe export from disk. Triggers the importer instead of seeding from a preset.',
    notes: [
      'Importer fills the recipe from the full .pax export.',
      'Permissions and command preview re-derive from the imported state.',
    ],
    overrides: null,
  },
  {
    id: 'importLiteRecipeJson',
    name: 'Import Mini-Kitchen .paxlite Recipe',
    description:
      'Load a Mini-Kitchen .paxlite recipe file from disk. Triggers the importer instead of seeding from a preset.',
    notes: [
      'Importer fills the recipe from the .paxlite file.',
      'Permissions and command preview re-derive from the imported state.',
    ],
    overrides: null,
  },
] as const;

const PRESETS_BY_ID = new Map<PresetId, DashboardPresetDefinition>(
  DASHBOARD_PRESETS.map(p => [p.id, p]),
);

/** Lookup a preset definition by id. Returns `undefined` for unknown ids. */
export function getPresetById(id: PresetId): DashboardPresetDefinition | undefined {
  return PRESETS_BY_ID.get(id);
}

/** Reserved preset names. UI must reject these as user recipe names. */
export const RESERVED_PRESET_NAMES: readonly string[] = DASHBOARD_PRESETS.map(p => p.name);
