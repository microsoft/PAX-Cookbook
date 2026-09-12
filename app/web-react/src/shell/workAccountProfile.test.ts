/**
 * Batch 3 — TOP-LEVEL Work-account profile control tests.
 *
 * These exercise the REAL shipped shell asset in
 * app/web/assets/work-account-profile.js (no reimplementation). The module is
 * loaded as a raw string via Vite's `?raw` loader and evaluated in jsdom, which
 * populates `window.cookbookWorkAccountProfile`. The tests prove:
 *   1. The envelope receiver revalidates EXACTLY — correct message type, allowed
 *      state, closed field set, image/jpeg content type, encoded-size ceiling —
 *      and rejects any unknown field, wrong type, oversized payload, or malformed
 *      initials.
 *   2. The controller mounts a visible surface ONLY when the gate is open (work
 *      account selected AND Unlocked) and clears on lock, provider change, a
 *      'none' envelope, teardown, and a malformed/oversized envelope.
 *   3. The in-memory image object URL is revoked on every clear/replace, and the
 *      photo bytes are NEVER written to localStorage / sessionStorage.
 *   4. Menu actions invoke the injected authority callbacks.
 */
/// <reference types="vite/client" />
// @ts-expect-error Vitest runs in Node; the production app intentionally excludes Node types.
import { readFileSync } from 'node:fs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import moduleSrcRaw from '../../../web/assets/work-account-profile.js?raw';

const SRC: string = moduleSrcRaw;
const STYLES_SRC = readFileSync('../web/assets/styles.css', 'utf8');
const TOKENS_SRC = readFileSync('../web/assets/tokens.css', 'utf8');

interface ProfileApi {
  MESSAGE_TYPE: string;
  LABEL_TEXT: string;
  ACCEPTED_CONTENT_TYPE: string;
  MAX_ENCODED_CHARS: number;
  validateEnvelope: (data: unknown) => { ok: boolean; kind?: string; initials?: string; imageBase64?: string };
  create: (opts: Record<string, unknown>) => {
    applyNativeMessage: (data: unknown) => void;
    setProvider: (p: string | null) => void;
    setLockState: (s: string | null) => void;
    clear: () => void;
    showMessage: (t: string) => void;
    dismissMenu: () => void;
    teardown: () => void;
    _els: () => { root: HTMLElement | null; avatar: HTMLElement | null; button: HTMLElement | null; menu: HTMLElement | null };
    _state: () => { objectUrl: string | null; presentation: { kind: string } | null };
  };
}

function loadApi(): ProfileApi {
  (0, eval)(SRC); // eslint-disable-line no-eval
  return (window as unknown as { cookbookWorkAccountProfile: ProfileApi }).cookbookWorkAccountProfile;
}

let api: ProfileApi;
let mountedControllers: Array<{ teardown: () => void }> = [];

function photoEnvelope(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    type: api.MESSAGE_TYPE,
    state: 'photo',
    contentType: 'image/jpeg',
    imageBase64: 'QUJD', // "ABC"
    label: 'Work account',
    ...overrides,
  };
}

beforeEach(() => {
  api = loadApi();
});

afterEach(() => {
  mountedControllers.forEach((controller) => controller.teardown());
  mountedControllers = [];
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

describe('validateEnvelope — closed-shape revalidation', () => {
  it('accepts a well-formed photo envelope', () => {
    const r = api.validateEnvelope(photoEnvelope());
    expect(r.ok).toBe(true);
    expect(r.kind).toBe('photo');
    expect(r.imageBase64).toBe('QUJD');
  });

  it('rejects a wrong message type', () => {
    expect(api.validateEnvelope(photoEnvelope({ type: 'cookbook:something-else' })).ok).toBe(false);
  });

  it('rejects an unknown extra field on a photo envelope', () => {
    expect(api.validateEnvelope(photoEnvelope({ url: 'https://x' })).ok).toBe(false);
    expect(api.validateEnvelope(photoEnvelope({ upn: 'a@b.com' })).ok).toBe(false);
    expect(api.validateEnvelope(photoEnvelope({ token: 'x' })).ok).toBe(false);
  });

  it('rejects a non-jpeg content type (no broad image/*)', () => {
    expect(api.validateEnvelope(photoEnvelope({ contentType: 'image/png' })).ok).toBe(false);
    expect(api.validateEnvelope(photoEnvelope({ contentType: 'image/*' })).ok).toBe(false);
  });

  it('rejects an over-ceiling base64 image', () => {
    const oversized = 'A'.repeat(api.MAX_ENCODED_CHARS + 4);
    expect(api.validateEnvelope(photoEnvelope({ imageBase64: oversized })).ok).toBe(false);
  });

  it('rejects a non-base64 image string', () => {
    expect(api.validateEnvelope(photoEnvelope({ imageBase64: 'not base64 %%%' })).ok).toBe(false);
  });

  it('accepts a valid initials envelope and rejects malformed initials', () => {
    expect(api.validateEnvelope({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' }).ok).toBe(true);
    expect(api.validateEnvelope({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'ABC', label: 'Work account' }).ok).toBe(false);
    expect(api.validateEnvelope({ type: api.MESSAGE_TYPE, state: 'initials', initials: '', label: 'Work account' }).ok).toBe(false);
    expect(api.validateEnvelope({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account', imageBase64: 'QUJD' }).ok).toBe(false);
  });

  it('accepts a none (clear) envelope and rejects a bad state', () => {
    expect(api.validateEnvelope({ type: api.MESSAGE_TYPE, state: 'none', label: 'Work account' }).ok).toBe(true);
    expect(api.validateEnvelope({ type: api.MESSAGE_TYPE, state: 'bogus', label: 'Work account' }).ok).toBe(false);
  });

  it('rejects a missing/renamed label', () => {
    expect(api.validateEnvelope(photoEnvelope({ label: 'Not the label' })).ok).toBe(false);
    const noLabel: Record<string, unknown> = { type: api.MESSAGE_TYPE, state: 'none' };
    expect(api.validateEnvelope(noLabel).ok).toBe(false);
  });

  it('rejects non-object / array payloads', () => {
    expect(api.validateEnvelope(null).ok).toBe(false);
    expect(api.validateEnvelope('x').ok).toBe(false);
    expect(api.validateEnvelope([]).ok).toBe(false);
  });
});

describe('initials avatar CSS contract', () => {
  it('uses dedicated light/dark contrast tokens, fixed dimensions, and a keyboard focus ring', () => {
    const avatarRule = STYLES_SRC.match(/\.wa-profile__avatar\s*\{([^}]*)\}/)?.[1] ?? '';
    const focusRule = STYLES_SRC.match(/\.wa-profile__button:focus-visible\s*\{([^}]*)\}/)?.[1] ?? '';

    expect(TOKENS_SRC.split('--c-profile-avatar-fg:')).toHaveLength(3);
    expect(TOKENS_SRC.split('--c-profile-avatar-bg:')).toHaveLength(3);
    expect(TOKENS_SRC.split('--c-profile-avatar-border:')).toHaveLength(3);
    expect(TOKENS_SRC.split('--c-profile-avatar-border-hover:')).toHaveLength(3);
    expect(avatarRule).toContain('width: 28px;');
    expect(avatarRule).toContain('height: 28px;');
    expect(avatarRule).toContain('color: var(--c-profile-avatar-fg');
    expect(avatarRule).toContain('background: var(--c-profile-avatar-bg');
    expect(avatarRule).toContain('border: 1px solid var(--c-profile-avatar-border');
    expect(avatarRule).not.toContain('opacity');
    expect(focusRule).toContain('box-shadow: var(--focus-ring);');
  });
});

describe('controller — gate, rendering, clearing, and object-URL revocation', () => {
  function mount(decode?: (b64: string, type: string) => Promise<string | null>) {
    const target = document.createElement('div');
    target.className = 'topbar-actions';
    document.body.appendChild(target);
    const onLock = vi.fn();
    const onDifferentAccount = vi.fn();
    const onSignInMethod = vi.fn();
    const controller = api.create({
      doc: document,
      mountTarget: target,
      decodeImage: decode ?? (() => Promise.resolve('blob:fake-1')),
      onLock,
      onDifferentAccount,
      onSignInMethod,
    });
    mountedControllers.push(controller);
    return { controller, target, onLock, onDifferentAccount, onSignInMethod };
  }

  it('stays hidden until the gate is open AND a valid presentation arrives', async () => {
    const { controller } = mount();
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });
    // Gate not yet open (no provider/lock state): hidden.
    expect(controller._els().root?.hidden).toBe(true);

    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    expect(controller._els().root?.hidden).toBe(false);
    expect(controller._els().avatar?.textContent).toBe('AB');
  });

  it('clears the surface when locked', () => {
    const { controller } = mount();
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });
    expect(controller._els().root?.hidden).toBe(false);

    controller.setLockState('Locked');
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._state().presentation).toBeNull();
  });

  it('clears the surface when the provider is not the work account', () => {
    const { controller } = mount();
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });
    controller.setProvider('windows_hello');
    expect(controller._els().root?.hidden).toBe(true);
  });

  it('clears on a none envelope', () => {
    const { controller } = mount();
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'none', label: 'Work account' });
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._state().presentation).toBeNull();
  });

  it('clears on a malformed / oversized envelope', () => {
    const { controller } = mount();
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });
    // Oversized photo → invalid → clears.
    controller.applyNativeMessage(photoEnvelope({ imageBase64: 'A'.repeat(api.MAX_ENCODED_CHARS + 4) }));
    expect(controller._els().root?.hidden).toBe(true);
  });

  it('renders a decoded photo and revokes the object URL on the next clear', async () => {
    const revoke = vi.fn();
    (window as unknown as { URL: { revokeObjectURL: (u: string) => void; createObjectURL?: (b: unknown) => string } }).URL.revokeObjectURL = revoke;

    const { controller } = mount(() => Promise.resolve('blob:fake-photo'));
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage(photoEnvelope());
    await Promise.resolve();
    await Promise.resolve();

    expect(controller._state().objectUrl).toBe('blob:fake-photo');
    expect(controller._els().avatar?.style.backgroundImage).toContain('blob:fake-photo');

    controller.clear();
    expect(revoke).toHaveBeenCalledWith('blob:fake-photo');
    expect(controller._state().objectUrl).toBeNull();
  });

  it('falls back to nothing when the image will not decode', async () => {
    const { controller } = mount(() => Promise.resolve(null));
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage(photoEnvelope());
    await Promise.resolve();
    await Promise.resolve();
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._state().presentation).toBeNull();
  });

  it('never writes photo bytes to localStorage or sessionStorage', async () => {
    const localSet = vi.spyOn(Storage.prototype, 'setItem');
    const { controller } = mount(() => Promise.resolve('blob:fake-photo'));
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage(photoEnvelope());
    await Promise.resolve();
    await Promise.resolve();
    expect(localSet).not.toHaveBeenCalled();
  });

  it('teardown removes the control from the DOM and revokes the URL', async () => {
    const revoke = vi.fn();
    (window as unknown as { URL: { revokeObjectURL: (u: string) => void } }).URL.revokeObjectURL = revoke;
    const { controller, target } = mount(() => Promise.resolve('blob:fake-photo'));
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage(photoEnvelope());
    await Promise.resolve();
    await Promise.resolve();
    controller.teardown();
    expect(target.querySelector('#work-account-profile')).toBeNull();
    expect(revoke).toHaveBeenCalledWith('blob:fake-photo');
  });

  it('invokes the injected authority callbacks from the menu items', () => {
    const { controller, onLock, onDifferentAccount, onSignInMethod } = mount();
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });

    const menu = controller._els().menu!;
    const items = menu.querySelectorAll('button[data-action]');
    (items[0] as HTMLButtonElement).click(); // lock
    (items[1] as HTMLButtonElement).click(); // different-account
    (items[2] as HTMLButtonElement).click(); // sign-in-method

    expect(onLock).toHaveBeenCalledTimes(1);
    expect(onDifferentAccount).toHaveBeenCalledTimes(1);
    expect(onSignInMethod).toHaveBeenCalledTimes(1);
  });

  it('closes on an outside click, resets aria-expanded, and does not steal focus', () => {
    const { controller } = mount();
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });

    const button = controller._els().button as HTMLButtonElement;
    const outside = document.createElement('button');
    document.body.appendChild(outside);
    button.click();
    button.focus();
    expect(button.getAttribute('aria-expanded')).toBe('true');

    outside.click();
    expect(controller._els().menu?.hidden).toBe(true);
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(button);
  });

  it('does not outside-dismiss a click inside the profile root', () => {
    const { controller } = mount();
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });

    const button = controller._els().button as HTMLButtonElement;
    const menu = controller._els().menu as HTMLElement;
    button.click();
    menu.click();

    expect(menu.hidden).toBe(false);
    expect(button.getAttribute('aria-expanded')).toBe('true');
  });

  it('closes on Escape and returns focus to the profile button', () => {
    const { controller } = mount();
    controller.setProvider('work_account');
    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });

    const button = controller._els().button as HTMLButtonElement;
    const menu = controller._els().menu as HTMLElement;
    const menuItem = menu.querySelector('button[data-action]') as HTMLButtonElement;
    button.click();
    menuItem.focus();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(menu.hidden).toBe(true);
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(button);
  });

  it('dismissMenu is safe while unmounted, hidden, or closed and closes an open menu', () => {
    const { controller } = mount();
    expect(() => controller.dismissMenu()).not.toThrow();

    controller.setProvider('work_account');
    controller.setLockState('Locked');
    expect(() => controller.dismissMenu()).not.toThrow();

    controller.setLockState('Unlocked');
    controller.applyNativeMessage({ type: api.MESSAGE_TYPE, state: 'initials', initials: 'AB', label: 'Work account' });
    const button = controller._els().button as HTMLButtonElement;
    button.click();
    expect(button.getAttribute('aria-expanded')).toBe('true');

    controller.dismissMenu();
    expect(controller._els().menu?.hidden).toBe(true);
    expect(button.getAttribute('aria-expanded')).toBe('false');
  });

  it('removes document dismissal listeners during teardown', () => {
    const addListener = vi.spyOn(document, 'addEventListener');
    const removeListener = vi.spyOn(document, 'removeEventListener');
    const { controller } = mount();
    controller.setProvider('work_account');
    const clickListener = addListener.mock.calls.find(([type]) => type === 'click')?.[1];
    const keyListener = addListener.mock.calls.find(([type]) => type === 'keydown')?.[1];

    expect(clickListener).toBeDefined();
    expect(keyListener).toBeDefined();
    controller.teardown();

    expect(removeListener).toHaveBeenCalledWith('click', clickListener);
    expect(removeListener).toHaveBeenCalledWith('keydown', keyListener);
    expect(() => document.dispatchEvent(new MouseEvent('click', { bubbles: true }))).not.toThrow();
    expect(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))).not.toThrow();
  });
});
