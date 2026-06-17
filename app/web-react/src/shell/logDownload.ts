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
// The File System Access API (showSaveFilePicker) is not in the standard TS DOM
// lib; declare the minimal surface this module uses and access it via a cast so
// it never conflicts with another module's own declaration. It is present in
// WebView2 / Chromium; the blob + anchor fallback covers anywhere it is missing.
interface SaveFileWritable {
  write(data: Blob | BufferSource | string): Promise<void>;
  close(): Promise<void>;
}
interface SaveFileHandle {
  createWritable(): Promise<SaveFileWritable>;
}
interface SaveFilePickerOptions {
  suggestedName?: string;
  types?: Array<{ description?: string; accept: Record<string, string[]> }>;
}
type ShowSaveFilePicker = (options?: SaveFilePickerOptions) => Promise<SaveFileHandle>;

function getShowSaveFilePicker(): ShowSaveFilePicker | undefined {
  if (typeof window === 'undefined') {
    return undefined;
  }
  const fn = (window as unknown as { showSaveFilePicker?: ShowSaveFilePicker })
    .showSaveFilePicker;
  return typeof fn === 'function' ? fn : undefined;
}

// Save text to disk. Prefers the native Windows "Save As" dialog
// (showSaveFilePicker) so the user chooses the folder and filename; a cancelled
// dialog (AbortError) is silently ignored. Falls back to a blob + anchor
// download to the default folder when the File System Access API is unavailable
// or fails for a non-cancel reason.
async function saveTextToFile(text: string, suggestedName: string): Promise<void> {
  const picker = getShowSaveFilePicker();
  if (picker) {
    try {
      const handle = await picker({
        suggestedName,
        types: [
          { description: 'Log files', accept: { 'text/plain': ['.log', '.txt'] } },
        ],
      });
      const writable = await handle.createWritable();
      await writable.write(text);
      await writable.close();
      return;
    } catch (err) {
      // The user cancelled the Save As dialog: do nothing (no download).
      const name = (err as { name?: unknown } | null)?.name;
      if (name === 'AbortError') {
        return;
      }
      // Any other failure falls through to the blob download below.
    }
  }
  blobDownloadFallback(text, suggestedName);
}

// Legacy auto-download (blob + anchor) to the browser's default folder. Used
// only as a fallback when the native Save As picker is unavailable or errors.
function blobDownloadFallback(text: string, filename: string): void {
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

// Save already-loaded log text via the Save As dialog (used by the Bakes log
// viewer where the log is already on screen).
export async function downloadBakeLogText(
  logText: string,
  recipeName: string | null | undefined,
  whenIso: string | null | undefined,
): Promise<void> {
  if (!logText) {
    return;
  }
  await saveTextToFile(logText, buildBakeLogFilename(recipeName, whenIso));
}

export type LogDownloadResult = { ok: true } | { ok: false; reason: 'empty' | 'locked' | 'error' };

// Fetch a bake's managed cook.log by cookId and save it via the Save As dialog.
// Used where the log text is not already loaded (Home "Last Bake" and "Recent
// Outputs"). Read-only: getCookLog never starts, mutates, or deletes anything.
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
  await saveTextToFile(res.text, buildBakeLogFilename(recipeName, whenIso));
  return { ok: true };
}
