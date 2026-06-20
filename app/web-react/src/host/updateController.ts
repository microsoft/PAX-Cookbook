/**
 * Update-check controller.
 *
 * A tiny pub/sub that lets the section pages (Settings, Updates) trigger an
 * update check whose modal is owned by the shell (App.tsx) — the same split the
 * navigation guard uses, since the trigger and the modal live in different
 * component trees. `runUpdateCheck` performs the check, notifies the shell
 * listener (which decides whether to show the "Updates available" modal), and
 * also returns the result so the caller can show its own inline feedback
 * ("PAX Cookbook is up to date." / a brief "couldn't check" note).
 */
import { checkForUpdates, type UpdateCheckResult } from './updateCheck';

type UpdateResultListener = (result: UpdateCheckResult) => void;

let listener: UpdateResultListener | null = null;

/** The shell registers (or clears) the listener that shows the modal. */
export function setUpdateResultListener(next: UpdateResultListener | null): void {
  listener = next;
}

/**
 * Run an update check, notify the shell listener, and return the result. Never
 * throws — `checkForUpdates` resolves failures to `status: 'unavailable'`.
 */
export async function runUpdateCheck(): Promise<UpdateCheckResult> {
  const result = await checkForUpdates();
  if (listener) {
    try {
      listener(result);
    } catch {
      /* a throwing listener must never break the caller's own handling */
    }
  }
  return result;
}
