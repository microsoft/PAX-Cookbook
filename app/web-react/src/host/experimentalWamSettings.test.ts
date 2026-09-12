import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  getWamConfigState,
  getWamAdminDetails,
  importWamSetupResult,
  verifyWamSetup,
  setWamEnabled,
  removeWamLocalConfig,
  prepareWamDeprovision,
  executeWamDeprovision,
  getSessionProviderStatus,
  selectWorkAccountProvider,
  selectWindowsHelloProvider,
  requestBrokerLock,
} from './experimentalWam';

// T1-S3 Settings workflow host functions. Deterministic, mocked fetch. Prove
// each fails closed to unavailable_in_this_build and targets the exact route.

interface FakeResponse {
  ok: boolean;
  status: number;
  text: () => Promise<string>;
}

function jsonResponse(status: number, body: unknown): FakeResponse {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => (body === undefined ? '' : JSON.stringify(body)),
  };
}

function fetchMock() {
  return vi.fn() as unknown as ReturnType<typeof vi.fn> & typeof fetch;
}

function lastUrl(mock: ReturnType<typeof vi.fn>): string {
  const calls = mock.mock.calls;
  return String(calls[calls.length - 1][0]);
}

function lastInit(mock: ReturnType<typeof vi.fn>): RequestInit {
  const calls = mock.mock.calls;
  return calls[calls.length - 1][1] as RequestInit;
}

beforeEach(() => {
  window.sessionStorage.setItem('cookbook.sessionToken', 'test-token');
});

afterEach(() => {
  vi.restoreAllMocks();
  window.sessionStorage.clear();
});

describe('config-state', () => {
  it('normalizes the live state', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, {
      state: 'ready', providerId: 'entra-wam', disabled: false, configured: true,
      capabilityAvailable: true, verifiedUtc: '2026-07-20T00:00:00Z',
    }));
    vi.stubGlobal('fetch', m);
    const s = await getWamConfigState();
    expect(s.state).toBe('ready');
    expect(s.capabilityAvailable).toBe(true);
    expect(s.configured).toBe(true);
    expect(lastUrl(m)).toContain('/experimental/wam/config-state');
  });

  it('fails closed to unavailable on non-200 and error', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(500, { error: 'x' })).mockRejectedValueOnce(new Error('boom'));
    vi.stubGlobal('fetch', m);
    expect((await getWamConfigState()).state).toBe('unavailable_in_this_build');
    expect((await getWamConfigState()).state).toBe('unavailable_in_this_build');
  });
});

describe('import', () => {
  it('posts the raw setup-result JSON body and returns the new state', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { ok: true, reason: null, state: 'configured_unverified', capabilityAvailable: false }));
    vi.stubGlobal('fetch', m);
    const r = await importWamSetupResult('{"kind":"x"}');
    expect(r.ok).toBe(true);
    expect(r.state.state).toBe('configured_unverified');
    expect(lastUrl(m)).toContain('/experimental/wam/import');
    expect(lastInit(m).body).toBe('{"kind":"x"}');
  });

  it('returns a bounded failure on rejection and never becomes ready', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(400, { ok: false, reason: 'invalid_json', state: 'not_configured' }));
    vi.stubGlobal('fetch', m);
    const r = await importWamSetupResult('{bad');
    expect(r.ok).toBe(false);
    expect(r.reason).toBe('invalid_json');
    expect(r.state.state).not.toBe('ready');
  });
});

describe('verify / enable / disable / remove', () => {
  it('verify posts to the verify route', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { ok: true, reason: null, state: 'ready', capabilityAvailable: true }));
    vi.stubGlobal('fetch', m);
    const r = await verifyWamSetup();
    expect(r.state.state).toBe('ready');
    expect(lastUrl(m)).toContain('/experimental/wam/verify');
  });

  it('enable and disable target the correct routes', async () => {
    const m = fetchMock();
    m.mockResolvedValue(jsonResponse(200, { ok: true, reason: null, state: 'configured_unverified' }));
    vi.stubGlobal('fetch', m);
    await setWamEnabled(true);
    expect(lastUrl(m)).toContain('/experimental/wam/enable');
    await setWamEnabled(false);
    expect(lastUrl(m)).toContain('/experimental/wam/disable');
  });

  it('remove targets the remove route', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { ok: true, reason: null, state: 'not_configured' }));
    vi.stubGlobal('fetch', m);
    const r = await removeWamLocalConfig();
    expect(r.state.state).toBe('not_configured');
    expect(lastUrl(m)).toContain('/experimental/wam/remove');
  });

  it('remove surfaces a bounded failure reason (no Hello requirement)', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(400, { ok: false, reason: 'not_configured' }));
    vi.stubGlobal('fetch', m);
    const r = await removeWamLocalConfig();
    expect(r.ok).toBe(false);
    expect(r.reason).toBe('not_configured');
  });
});

describe('admin details', () => {
  it('returns identifiers only for an explicit available:true', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { available: true, tenantId: 't', clientId: 'c' }));
    vi.stubGlobal('fetch', m);
    expect(await getWamAdminDetails()).toEqual({ available: true, tenantId: 't', clientId: 'c' });
  });

  it('fails closed on non-200 / missing fields', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(404, { available: false }))
      .mockResolvedValueOnce(jsonResponse(200, { available: true }));
    vi.stubGlobal('fetch', m);
    expect(await getWamAdminDetails()).toEqual({ available: false });
    expect(await getWamAdminDetails()).toEqual({ available: false });
  });
});

describe('deprovision plan/execute', () => {
  it('prepare returns a plan id; execute posts it', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { ok: true, planId: 'plan-1', categories: { registration: true } }))
      .mockResolvedValueOnce(jsonResponse(200, { ok: true, reason: null, state: 'not_configured' }));
    vi.stubGlobal('fetch', m);
    const plan = await prepareWamDeprovision();
    expect(plan.ok).toBe(true);
    expect(plan.planId).toBe('plan-1');
    expect(lastUrl(m)).toContain('/experimental/wam/prepare-deprovision');
    const exec = await executeWamDeprovision('plan-1');
    expect(exec.state.state).toBe('not_configured');
    expect(lastUrl(m)).toContain('/experimental/wam/execute-deprovision');
    expect(JSON.parse(String(lastInit(m).body))).toEqual({ planId: 'plan-1' });
  });

  it('prepare fails closed on error', async () => {
    const m = fetchMock();
    m.mockRejectedValueOnce(new Error('boom'));
    vi.stubGlobal('fetch', m);
    const plan = await prepareWamDeprovision();
    expect(plan.ok).toBe(false);
    expect(plan.planId).toBeNull();
  });
});

describe('selected session provider (mutually-exclusive)', () => {
  it('reads the bounded provider status from the lock-bypass route', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, {
      selectedProvider: 'work_account', recoveryRequired: false, usable: true, healthCode: 'ready',
    }));
    vi.stubGlobal('fetch', m);
    const st = await getSessionProviderStatus();
    expect(st.selectedProvider).toBe('work_account');
    expect(st.usable).toBe(true);
    expect(st.healthCode).toBe('ready');
    expect(lastUrl(m)).toContain('/broker/session-provider');
  });

  it('fails closed to recovery on error or unknown provider', async () => {
    const m = fetchMock();
    m.mockRejectedValueOnce(new Error('boom'))
      .mockResolvedValueOnce(jsonResponse(200, { selectedProvider: 'nonsense' }));
    vi.stubGlobal('fetch', m);
    const a = await getSessionProviderStatus();
    expect(a.selectedProvider).toBe('recovery_required');
    expect(a.recoveryRequired).toBe(true);
    const b = await getSessionProviderStatus();
    expect(b.selectedProvider).toBe('recovery_required');
  });

  it('select-work-account targets its route and surfaces the auth-test guard', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(409, { ok: false, reason: 'work_account_auth_test_required' }));
    vi.stubGlobal('fetch', m);
    const r = await selectWorkAccountProvider();
    expect(r.ok).toBe(false);
    expect(r.reason).toBe('work_account_auth_test_required');
    expect(lastUrl(m)).toContain('/broker/session-provider/select-work-account');
    expect(lastInit(m).method).toBe('POST');
  });

  it('select-windows-hello sends the platformAvailable predicate and succeeds when ready', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { ok: true, selectedProvider: 'windows_hello' }));
    vi.stubGlobal('fetch', m);
    const r = await selectWindowsHelloProvider(true);
    expect(r.ok).toBe(true);
    expect(r.selectedProvider).toBe('windows_hello');
    expect(lastUrl(m)).toContain('/broker/session-provider/select-windows-hello');
    // The bounded platform-availability predicate is sent to the daemon.
    expect(JSON.parse(lastInit(m).body as string)).toEqual({ platformAvailable: true });
  });

  it('select-windows-hello forwards platformAvailable=false when the platform is unavailable', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(409, { ok: false, reason: 'windows_hello_platform_unavailable' }));
    vi.stubGlobal('fetch', m);
    const r = await selectWindowsHelloProvider(false);
    expect(r.ok).toBe(false);
    expect(r.reason).toBe('windows_hello_platform_unavailable');
    expect(JSON.parse(lastInit(m).body as string)).toEqual({ platformAvailable: false });
  });

  it('select-windows-hello surfaces the enrollment-required guard (register before switch)', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(409, { ok: false, reason: 'windows_hello_enrollment_required' }));
    vi.stubGlobal('fetch', m);
    const r = await selectWindowsHelloProvider(true);
    expect(r.ok).toBe(false);
    expect(r.reason).toBe('windows_hello_enrollment_required');
  });

  it('select-windows-hello surfaces the registration-repair guard, never the stale unavailable reason', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(409, { ok: false, reason: 'windows_hello_registration_repair_required' }));
    vi.stubGlobal('fetch', m);
    const r = await selectWindowsHelloProvider(true);
    expect(r.ok).toBe(false);
    expect(r.reason).toBe('windows_hello_registration_repair_required');
    expect(r.reason).not.toBe('windows_hello_unavailable');
  });

  it('requestBrokerLock posts the lock route and returns ok', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { state: 'Locked' }));
    vi.stubGlobal('fetch', m);
    expect(await requestBrokerLock()).toBe(true);
    expect(lastUrl(m)).toContain('/broker/lock');
    expect(lastInit(m).method).toBe('POST');
  });
});
