/**
 * Bounded Work-account presentation replay handshake tests. These execute the
 * REAL shipped integrated-shell.js asset in jsdom and simulate only the native
 * window-memory presentation channel; no WAM, Graph, identity, or daemon
 * authorization is involved.
 */
/// <reference types="vite/client" />
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import shellSrcRaw from '../../../web/assets/integrated-shell.js?raw';

const SHELL_SRC: string = shellSrcRaw;
const READY = 'cookbook:work-account-profile-ready';
const CLEAR = 'cookbook:work-account-profile-clear';
const PROFILE = 'cookbook:work-account-profile';

type ProfileEnvelope = Record<string, unknown>;

interface ControllerStub {
  presentations: ProfileEnvelope[];
  clearCount: number;
  dismissCount: number;
  provider: string | null;
  lockState: string | null;
  applyNativeMessage: (data: ProfileEnvelope) => void;
  setProvider: (provider: string | null) => void;
  setLockState: (lockState: string | null) => void;
  clear: () => void;
  showMessage: () => void;
  dismissMenu: () => void;
  teardown: () => void;
}

interface Harness {
  controller: ControllerStub;
  hostPosts: ProfileEnvelope[];
  apiPosts: string[];
  frameWindow: { postMessage: ReturnType<typeof vi.fn> };
  setProvider: (provider: string) => void;
  setLockState: (lockState: string) => void;
  setProviderFailure: (fails: boolean) => void;
  setLockFailure: (fails: boolean) => void;
  setRetained: (presentation: ProfileEnvelope | null) => void;
}

let harness: Harness;

async function flush(): Promise<void> {
  for (let i = 0; i < 8; i += 1) {
    // eslint-disable-next-line no-await-in-loop
    await Promise.resolve();
  }
}

function boot(provider: string, lockState: string, retained: ProfileEnvelope | null): Harness {
  document.body.innerHTML = '<div class="topbar-actions"></div><iframe id="mk-content-frame"></iframe>';
  const frame = document.getElementById('mk-content-frame') as HTMLIFrameElement;
  const frameWindow = { postMessage: vi.fn() };
  Object.defineProperty(frame, 'contentWindow', {
    value: frameWindow,
    configurable: true,
  });

  let currentProvider = provider;
  let currentLockState = lockState;
  let providerFails = false;
  let lockFails = false;
  let retainedPresentation = retained;
  let nativeListener: ((event: { data: ProfileEnvelope }) => void) | null = null;
  const hostPosts: ProfileEnvelope[] = [];
  const apiPosts: string[] = [];
  const controller: ControllerStub = {
    presentations: [],
    clearCount: 0,
    dismissCount: 0,
    provider: null,
    lockState: null,
    applyNativeMessage(data) {
      this.presentations.push(data);
    },
    setProvider(value) {
      this.provider = value;
    },
    setLockState(value) {
      this.lockState = value;
    },
    clear() {
      this.clearCount += 1;
      this.presentations = [];
    },
    showMessage() {},
    dismissMenu() {
      this.dismissCount += 1;
    },
    teardown() {},
  };

  (window as unknown as { cookbookWorkAccountProfile: unknown }).cookbookWorkAccountProfile = {
    create: () => controller,
  };
  (window as unknown as { cookbookApi: unknown }).cookbookApi = {
    get: (path: string) => {
      if (path === '/api/v1/broker/session-provider') {
        if (providerFails) return Promise.reject(new Error('synthetic provider failure'));
        return Promise.resolve({ ok: true, body: { selectedProvider: currentProvider } });
      }
      if (path === '/api/v1/broker/lock-state') {
        if (lockFails) return Promise.reject(new Error('synthetic lock failure'));
        return Promise.resolve({ ok: true, body: { state: currentLockState } });
      }
      return Promise.resolve({ ok: false, body: null });
    },
    post: (path: string) => {
      apiPosts.push(path);
      return Promise.resolve({ ok: true, body: {} });
    },
  };
  (window as unknown as { chrome: unknown }).chrome = {
    webview: {
      addEventListener: (type: string, listener: (event: { data: ProfileEnvelope }) => void) => {
        if (type === 'message') nativeListener = listener;
      },
      postMessage: (message: ProfileEnvelope) => {
        hostPosts.push(message);
        if (message.type === READY && retainedPresentation && nativeListener) {
          nativeListener({ data: retainedPresentation });
        }
        if (message.type === CLEAR) {
          retainedPresentation = null;
          if (nativeListener) {
            nativeListener({ data: { type: PROFILE, state: 'none', label: 'Work account' } });
          }
        }
      },
    },
  };

  (0, eval)(SHELL_SRC); // eslint-disable-line no-eval

  return {
    controller,
    hostPosts,
    apiPosts,
    frameWindow,
    setProvider: (value: string) => { currentProvider = value; },
    setLockState: (value: string) => { currentLockState = value; },
    setProviderFailure: (value: boolean) => { providerFails = value; },
    setLockFailure: (value: boolean) => { lockFails = value; },
    setRetained: (value: ProfileEnvelope | null) => { retainedPresentation = value; },
  };
}

function dispatchFrameMessage(
  data: ProfileEnvelope,
  origin = window.location.origin,
  source: object = harness.frameWindow,
): void {
  const event = new MessageEvent('message', { data, origin });
  Object.defineProperty(event, 'source', { value: source });
  window.dispatchEvent(event);
}

function controlTypes(): unknown[] {
  return harness.hostPosts
    .filter((message) => message.type === READY || message.type === CLEAR)
    .map((message) => message.type);
}

describe('integrated shell Work-account presentation replay handshake', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    window.dispatchEvent(new Event('pagehide'));
    vi.clearAllTimers();
    vi.useRealTimers();
    document.body.innerHTML = '';
    delete (window as unknown as { cookbookWorkAccountProfile?: unknown }).cookbookWorkAccountProfile;
    delete (window as unknown as { cookbookApi?: unknown }).cookbookApi;
    delete (window as unknown as { chrome?: unknown }).chrome;
    vi.restoreAllMocks();
  });

  it('initial work_account + Unlocked sends ready exactly once and renders replay', async () => {
    harness = boot('work_account', 'Unlocked', {
      type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account',
    });
    await flush();
    expect(controlTypes()).toEqual([READY]);
    expect(harness.controller.presentations).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(4500);
    await flush();
    expect(controlTypes()).toEqual([READY]);
  });

  it('initial Locked state sends neither clear nor ready', async () => {
    harness = boot('work_account', 'Locked', {
      type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account',
    });
    await flush();
    expect(controlTypes()).toEqual([]);
  });

  it('retains presentation through Locked polls, then Unlocked sends ready and renders it', async () => {
    const retained = { type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account' };
    harness = boot('work_account', 'Locked', retained);
    await flush();
    await vi.advanceTimersByTimeAsync(3000);
    await flush();
    expect(controlTypes()).toEqual([]);

    harness.setLockState('Unlocked');
    await vi.advanceTimersByTimeAsync(1500);
    await flush();
    expect(controlTypes()).toEqual([READY]);
    expect(harness.controller.presentations).toEqual([retained]);
  });

  it('open to Locked sends one clear and clears locally without poll spam', async () => {
    harness = boot('work_account', 'Unlocked', {
      type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account',
    });
    await flush();
    harness.setLockState('Locked');
    await vi.advanceTimersByTimeAsync(1500);
    await flush();
    await vi.advanceTimersByTimeAsync(3000);
    await flush();
    expect(controlTypes()).toEqual([READY, CLEAR]);
    expect(harness.controller.clearCount).toBeGreaterThan(0);
  });

  it('open to provider replacement sends one clear', async () => {
    harness = boot('work_account', 'Unlocked', {
      type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account',
    });
    await flush();
    harness.setProvider('windows_hello');
    await vi.advanceTimersByTimeAsync(1500);
    await flush();
    await vi.advanceTimersByTimeAsync(3000);
    await flush();
    expect(controlTypes()).toEqual([READY, CLEAR]);
  });

  it('broker uncertainty clears locally without native clear, then recovery replays', async () => {
    const retained = { type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account' };
    harness = boot('work_account', 'Unlocked', retained);
    await flush();
    expect(controlTypes()).toEqual([READY]);

    harness.setLockFailure(true);
    await vi.advanceTimersByTimeAsync(1500);
    await flush();
    expect(controlTypes()).toEqual([READY]);
    expect(harness.controller.clearCount).toBeGreaterThan(0);

    harness.setLockFailure(false);
    await vi.advanceTimersByTimeAsync(1500);
    await flush();
    expect(controlTypes()).toEqual([READY, READY]);
    expect(harness.controller.presentations).toEqual([retained]);
  });

  it('a new top-level controller on reload sends ready from initial Unlocked state', async () => {
    const retained = { type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account' };
    harness = boot('work_account', 'Unlocked', retained);
    await flush();
    expect(controlTypes()).toEqual([READY]);
    window.dispatchEvent(new Event('pagehide'));
    document.body.innerHTML = '';

    harness = boot('work_account', 'Unlocked', retained);
    await flush();
    expect(controlTypes()).toEqual([READY]);
    expect(harness.controller.presentations).toEqual([retained]);
  });

  it('never forwards replayed photo bytes to React or browser storage', async () => {
    const parentPost = vi.fn();
    const storageSet = vi.spyOn(Storage.prototype, 'setItem');
    (window as unknown as { parent: { postMessage: (message: unknown) => void } }).parent = {
      postMessage: parentPost,
    };
    harness = boot('work_account', 'Unlocked', {
      type: PROFILE,
      state: 'photo',
      contentType: 'image/jpeg',
      imageBase64: 'QUJD',
      label: 'Work account',
    });
    await flush();

    expect(harness.controller.presentations[0].imageBase64).toBe('QUJD');
    expect(parentPost).not.toHaveBeenCalled();
    expect(storageSet).not.toHaveBeenCalled();
    expect(JSON.stringify(harness.hostPosts)).not.toContain('QUJD');
  });

  it('accepts exact iframe provenance and shape, calling dismiss only', async () => {
    harness = boot('work_account', 'Unlocked', {
      type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account',
    });
    await flush();
    const hostPostCount = harness.hostPosts.length;
    const clearCount = harness.controller.clearCount;

    dispatchFrameMessage({ type: 'cookbook:work-account-profile-content-interaction' });

    expect(harness.controller.dismissCount).toBe(1);
    expect(harness.controller.clearCount).toBe(clearCount);
    expect(harness.hostPosts).toHaveLength(hostPostCount);
    expect(harness.apiPosts).toEqual([]);
  });

  it.each([
    ['wrong origin', { type: 'cookbook:work-account-profile-content-interaction' }, 'https://wrong.test', null],
    ['wrong source', { type: 'cookbook:work-account-profile-content-interaction' }, window.location.origin, {}],
    ['extra field', { type: 'cookbook:work-account-profile-content-interaction', target: 'menu' }, window.location.origin, null],
    ['wrong type', { type: 'cookbook:other' }, window.location.origin, null],
  ])('ignores %s', async (_name, data, origin, source) => {
    harness = boot('work_account', 'Unlocked', {
      type: PROFILE, state: 'initials', initials: 'ST', label: 'Work account',
    });
    await flush();

    dispatchFrameMessage(data, origin, source ?? harness.frameWindow);

    expect(harness.controller.dismissCount).toBe(0);
    expect(harness.apiPosts).toEqual([]);
  });
});