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

// Native "Save As" support. window.showSaveFilePicker is the File System Access
// API; its type comes from the global declaration in PantryWorkspace.tsx, so
// this module calls window.showSaveFilePicker DIRECTLY — exactly like the Pantry
// "Save As" flow that works — rather than through a cast. It is present in
// WebView2 / Chromium; the blob + anchor fallback covers anywhere it is missing.

// Return the file extension (with leading dot, lowercased) or null.
function fileExtWithDot(name: string): string | null {
  const dot = name.lastIndexOf('.');
  if (dot <= 0 || dot === name.length - 1) {
    return null;
  }
  return name.slice(dot).toLowerCase();
}

// Show the native Save As dialog and return the chosen file handle. Returns
// 'cancelled' when the user dismisses the dialog, or null when the picker is
// unavailable or fails for another reason (the caller then blob-downloads).
//
// The picker MUST be invoked while the click's user gesture is still active, so
// callers open it BEFORE any slow work (e.g. fetching the log) — mirroring the
// Pantry Save As flow, which opens the picker before downloading any bytes.
async function openSaveDialog(suggestedName: string) {
  const picker = typeof window !== 'undefined' ? window.showSaveFilePicker : undefined;
  if (typeof picker !== 'function') {
    return null;
  }
  const options: {
    suggestedName: string;
    types?: Array<{ description?: string; accept: Record<string, string[]> }>;
  } = { suggestedName };
  const ext = fileExtWithDot(suggestedName);
  if (ext) {
    options.types = [
      {
        description: ext.slice(1).toUpperCase() + ' file',
        accept: { 'application/octet-stream': [ext] },
      },
    ];
  }
  try {
    return await picker(options);
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') {
      return 'cancelled' as const;
    }
    // Surface the real reason in DevTools, then fall back to a blob download.
    console.error('[logDownload] showSaveFilePicker failed; falling back to download', e);
    return null;
  }
}

// Write text to a previously chosen file handle. Returns false (and logs) when
// the write fails, so the caller can fall back to a blob download.
async function writeTextToHandle(
  handle: { createWritable(): Promise<{ write(data: Blob): Promise<void>; close(): Promise<void> }> },
  text: string,
): Promise<boolean> {
  try {
    const writable = await handle.createWritable();
    await writable.write(new Blob([text], { type: 'text/plain;charset=utf-8' }));
    await writable.close();
    return true;
  } catch (e) {
    console.error('[logDownload] failed writing the chosen file; falling back to download', e);
    return false;
  }
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
  const filename = buildBakeLogFilename(recipeName, whenIso);
  const handle = await openSaveDialog(filename);
  if (handle === 'cancelled') {
    return;
  }
  if (handle && (await writeTextToHandle(handle, logText))) {
    return;
  }
  blobDownloadFallback(logText, filename);
}

export type LogDownloadResult = { ok: true } | { ok: false; reason: 'empty' | 'locked' | 'error' };

// Fetch a bake's managed cook.log by cookId and save it via the Save As dialog.
// Used where the log text is not already loaded (Home "Last Bake" and "Recent
// Outputs"). The Save As dialog opens FIRST — while the click gesture is still
// active — and only then is the log fetched and written, mirroring the Pantry
// Save As flow. Read-only: getCookLog never starts, mutates, or deletes anything.
export async function downloadBakeLogByCookId(
  cookId: string,
  recipeName: string | null | undefined,
  whenIso: string | null | undefined,
): Promise<LogDownloadResult> {
  const filename = buildBakeLogFilename(recipeName, whenIso);
  const handle = await openSaveDialog(filename);
  if (handle === 'cancelled') {
    return { ok: true };
  }
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
  if (handle && (await writeTextToHandle(handle, res.text))) {
    return { ok: true };
  }
  blobDownloadFallback(res.text, filename);
  return { ok: true };
}
