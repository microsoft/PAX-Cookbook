// Browser file-save helpers for a bake's managed cook.log. A single place so
// the Bakes log viewer, the Home "Last Bake" panel, and the Home "Recent
// Outputs" rows all download the same way and produce identically-named files.

import { getCookLog } from '../host/brokerBridge';

// Sanitize a recipe name into a filename-safe token (letters, digits, dot,
// underscore, hyphen). Returns '' when there is nothing usable.
function safeToken(value: string | null | undefined): string {
  if (!value) {
    return '';
  }
  return value
    .trim()
    .replace(/[^A-Za-z0-9._-]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 60);
}

// Build a bake-log filename, e.g. `PAX_Cookbook_Bake_My_Recipe_2026-06-17_111115.log`
// (the recipe segment is omitted when no name is available). `whenIso` is the
// bake's start/created time; falls back to now when missing or unparseable.
export function buildBakeLogFilename(
  recipeName: string | null | undefined,
  whenIso: string | null | undefined,
): string {
  let when = whenIso ? new Date(whenIso) : new Date();
  if (Number.isNaN(when.getTime())) {
    when = new Date();
  }
  const pad = (n: number) => String(n).padStart(2, '0');
  const date = `${when.getFullYear()}-${pad(when.getMonth() + 1)}-${pad(when.getDate())}`;
  const time = `${pad(when.getHours())}${pad(when.getMinutes())}${pad(when.getSeconds())}`;
  const recipe = safeToken(recipeName);
  const middle = recipe ? `_${recipe}` : '';
  return `PAX_Cookbook_Bake${middle}_${date}_${time}.log`;
}

// Trigger a browser download of `text` as a file named `filename`.
export function triggerTextFileDownload(text: string, filename: string): void {
  const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.style.display = 'none';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  // Revoke after a tick so the download has started.
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// Download already-loaded log text (used by the Bakes log viewer where the log
// is already on screen).
export function downloadBakeLogText(
  logText: string,
  recipeName: string | null | undefined,
  whenIso: string | null | undefined,
): void {
  if (!logText) {
    return;
  }
  triggerTextFileDownload(logText, buildBakeLogFilename(recipeName, whenIso));
}

export type LogDownloadResult = { ok: true } | { ok: false; reason: 'empty' | 'locked' | 'error' };

// Fetch a bake's managed cook.log by cookId and download it. Used where the log
// text is not already loaded (Home "Last Bake" and "Recent Outputs"). Read-only:
// getCookLog never starts, mutates, or deletes anything.
export async function downloadBakeLogByCookId(
  cookId: string,
  recipeName: string | null | undefined,
  whenIso: string | null | undefined,
): Promise<LogDownloadResult> {
  const res = await getCookLog(cookId);
  if (res.locked) {
    return { ok: false, reason: 'locked' };
  }
  if (res.networkError || !res.ok) {
    return { ok: false, reason: 'error' };
  }
  if (!res.available || !res.text) {
    return { ok: false, reason: 'empty' };
  }
  triggerTextFileDownload(res.text, buildBakeLogFilename(recipeName, whenIso));
  return { ok: true };
}
