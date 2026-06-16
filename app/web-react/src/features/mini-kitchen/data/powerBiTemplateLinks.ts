/**
 * Power BI dashboard template repositories on GitHub. Keyed by BOTH the builder
 * preset id and the bundled template id so the Recipes template cards and the
 * Step-1 preset cards resolve the same repo from either side.
 */
const POWER_BI_TEMPLATE_URLS: Readonly<Record<string, string>> = {
  aiInOneDashboard: 'https://github.com/microsoft/AI-in-One-Dashboard',
  'ai-in-one-rollup': 'https://github.com/microsoft/AI-in-One-Dashboard',
  aiBusinessValueDashboard: 'https://github.com/Keithland89/AI-Business-Value-Dashboard',
  'ai-business-value': 'https://github.com/Keithland89/AI-Business-Value-Dashboard',
  m365UsageAnalyticsDashboard: 'https://github.com/microsoft/M365UsageAnalytics',
  'm365-usage-analytics': 'https://github.com/microsoft/M365UsageAnalytics',
};

export function powerBiTemplateUrl(key: string | null | undefined): string | null {
  if (!key) return null;
  return POWER_BI_TEMPLATE_URLS[key] ?? null;
}
