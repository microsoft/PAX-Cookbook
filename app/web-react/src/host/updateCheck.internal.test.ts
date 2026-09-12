import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./systemInfo', () => ({
  getRuntimeVersion: vi.fn(),
  getPaxEngineState: vi.fn(),
}));
vi.mock('./brokerBridge', () => ({
  getExperimentalUpdateManifest: vi.fn(),
}));

import { checkForUpdates, isNewerVersion, sameVersion } from './updateCheck';
import { getExperimentalUpdateManifest } from './brokerBridge';
import { getPaxEngineState, getRuntimeVersion } from './systemInfo';

describe('internal update isolation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('fetch', vi.fn());
    (getRuntimeVersion as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: true,
      data: { releaseChannel: 'internal' },
    });
    (getPaxEngineState as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({ ok: false });
  });

  afterEach(() => vi.unstubAllGlobals());

  it('refuses without contacting stable or experimental discovery', async () => {
    const result = await checkForUpdates();

    expect(result).toEqual({
      status: 'unavailable',
      components: [],
      allComponents: [],
      checkedAtUtc: null,
      detail: 'Local validation builds do not check production updates.',
    });
    expect(fetch).not.toHaveBeenCalled();
    expect(getExperimentalUpdateManifest).not.toHaveBeenCalled();
  });
});

describe('release-contract version comparison', () => {
  it('orders channel-compatible prerelease ordinals', () => {
    expect(isNewerVersion('2.0.0-exp.5', '2.0.0-exp.4')).toBe(true);
    expect(isNewerVersion('2.0.0-internal.8', '2.0.0-internal.7')).toBe(true);
    expect(isNewerVersion('2.0.0-exp.4', '2.0.0-exp.4')).toBe(false);
  });

  it('compares equality without discarding prerelease identity', () => {
    expect(sameVersion('2.0.0-exp.4', '2.0.0-exp.4')).toBe(true);
    expect(sameVersion('2.0.0-exp.4', '2.0.0-exp.5')).toBe(false);
    expect(sameVersion('2.0.0', '2.0.0-exp.4')).toBe(false);
  });

  it('refuses malformed or cross-channel guesses', () => {
    expect(isNewerVersion('2.0.0-internal.8', '2.0.0-exp.7')).toBe(false);
    expect(isNewerVersion('2.0.0-preview.8', '2.0.0-preview.7')).toBe(false);
  });
});