import { describe, it, expect, vi, beforeEach } from 'vitest';

// Batch 4 — the update-check "unavailable" copy must read as PRE-RELEASE, never
// "experimental". These are the only three unavailable-detail strings on the
// experimental discovery path; each is driven here through its exact branch and
// asserted to (a) equal the approved pre-release wording and (b) contain no
// "experimental". The internal channel token that selects this path is
// unchanged (the runtime still reports releaseChannel === 'experimental').
vi.mock('./systemInfo', () => ({
  getRuntimeVersion: vi.fn(),
  getPaxEngineState: vi.fn(),
}));
vi.mock('./brokerBridge', () => ({
  getExperimentalUpdateManifest: vi.fn(),
}));

import { checkForUpdates } from './updateCheck';
import { getRuntimeVersion, getPaxEngineState } from './systemInfo';
import { getExperimentalUpdateManifest } from './brokerBridge';

const NO_EXPERIMENTAL = /experimental/i;

describe('updateCheck pre-release customer copy', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // Runtime reports the raw internal channel token unchanged; that selects
    // the experimental discovery branch under test.
    (getRuntimeVersion as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: true,
      data: { releaseChannel: 'experimental' },
    });
    (getPaxEngineState as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: false,
    });
  });

  it('reports "No pre-release builds are currently published." when none exist', async () => {
    (
      getExperimentalUpdateManifest as unknown as ReturnType<typeof vi.fn>
    ).mockResolvedValue({ state: 'no_prerelease' });

    const result = await checkForUpdates();

    expect(result.status).toBe('unavailable');
    expect(result.detail).toBe('No pre-release builds are currently published.');
    expect(result.detail ?? '').not.toMatch(NO_EXPERIMENTAL);
  });

  it('reports pre-release "unreadable" copy when the manifest body is not JSON', async () => {
    (
      getExperimentalUpdateManifest as unknown as ReturnType<typeof vi.fn>
    ).mockResolvedValue({ state: 'ok', manifestJson: '{ not json' });

    const result = await checkForUpdates();

    expect(result.status).toBe('unavailable');
    expect(result.detail).toBe(
      'The pre-release update information was unreadable.',
    );
    expect(result.detail ?? '').not.toMatch(NO_EXPERIMENTAL);
  });

  it('falls back to pre-release "couldn\u2019t check" copy on a generic failure', async () => {
    (
      getExperimentalUpdateManifest as unknown as ReturnType<typeof vi.fn>
    ).mockResolvedValue({ state: 'error' });

    const result = await checkForUpdates();

    expect(result.status).toBe('unavailable');
    expect(result.detail).toBe(
      'Couldn\u2019t check for pre-release updates just now.',
    );
    expect(result.detail ?? '').not.toMatch(NO_EXPERIMENTAL);
  });
});
