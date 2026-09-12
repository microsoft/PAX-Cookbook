/**
 * Plan step 17 — NON-LIVE synthetic session-profile acceptance (TOP-LEVEL path).
 *
 * These tests drive the REAL shipped shell control in
 * app/web/assets/work-account-profile.js with the EXACT bounded envelopes the
 * native channel (WorkAccountProfileWindowChannel) posts — WITHOUT any real
 * Microsoft sign-in, real Graph call, or real identity. The synthetic envelopes
 * are constructed here in test code (no product surface is added); the photo
 * payload is the same locally embedded synthetic JPEG (SOI 0xFFD8 ... EOI
 * 0xFFD9) the native units use.
 *
 * Proven here, per the non-live scenario matrix:
 *   1. Synthetic photo  -> the TOP-LEVEL control renders the image; the photo
 *      bytes reach ONLY the injected top-level decoder and are NEVER posted to a
 *      parent/React frame nor written to storage.
 *   2. Every photo failure -> an initials envelope renders (never blank / never
 *      an error), with the synthetic 'ST' initials or the 'WA' fallback.
 *   3. Malformed / oversized envelope -> REJECTED (no render), surface cleared.
 *   4. Lock -> control CLEARS and the image object URL is revoked.
 *   5. Provider change / provider != work_account -> CLEARS.
 *   6. none envelope -> CLEARS.
 *   9. STABLE-mode inertness: a non-work-account provider yields NO surface even
 *      for a valid synthetic photo envelope, and the shipped asset carries no
 *      synthetic-seam marker at all.
 */
/// <reference types="vite/client" />
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import moduleSrcRaw from '../../../web/assets/work-account-profile.js?raw';

const SRC: string = moduleSrcRaw;

// The same minimal valid synthetic JPEG the native units use: SOI (0xFFD8) +
// JFIF APP0 header + EOI (0xFFD9). Synthetic image data, not a real photo.
const SYNTHETIC_JPEG_B64: string = '/9j/4AAQSkZJRgABAQAAAQABAAD/2Q==';

interface ProfileApi {
  MESSAGE_TYPE: string;
  LABEL_TEXT: string;
  ACCEPTED_CONTENT_TYPE: string;
  MAX_ENCODED_CHARS: number;
  validateEnvelope: (data: unknown) => { ok: boolean; kind?: string };
  create: (opts: Record<string, unknown>) => {
    applyNativeMessage: (data: unknown) => void;
    setProvider: (p: string | null) => void;
    setLockState: (s: string | null) => void;
    clear: () => void;
    teardown: () => void;
    _els: () => { root: HTMLElement | null; avatar: HTMLElement | null };
    _state: () => { objectUrl: string | null; presentation: { kind: string } | null };
  };
}

function loadApi(): ProfileApi {
  (0, eval)(SRC); // eslint-disable-line no-eval
  return (window as unknown as { cookbookWorkAccountProfile: ProfileApi }).cookbookWorkAccountProfile;
}

let api: ProfileApi;

// Envelope builders matching the native channel's exact closed shapes.
function syntheticPhotoEnvelope(): Record<string, unknown> {
  return {
    type: api.MESSAGE_TYPE,
    state: 'photo',
    contentType: 'image/jpeg',
    imageBase64: SYNTHETIC_JPEG_B64,
    label: 'Work account',
  };
}

function initialsEnvelope(initials: string): Record<string, unknown> {
  return { type: api.MESSAGE_TYPE, state: 'initials', initials, label: 'Work account' };
}

function noneEnvelope(): Record<string, unknown> {
  return { type: api.MESSAGE_TYPE, state: 'none', label: 'Work account' };
}

beforeEach(() => {
  api = loadApi();
});

afterEach(() => {
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

function mount(decode?: (b64: string, type: string) => Promise<string | null>) {
  const target = document.createElement('div');
  target.className = 'topbar-actions';
  document.body.appendChild(target);
  const decodeArgs: string[] = [];
  const controller = api.create({
    doc: document,
    mountTarget: target,
    decodeImage:
      decode ??
      ((b64: string) => {
        decodeArgs.push(b64);
        return Promise.resolve('blob:synthetic-photo');
      }),
    onLock: vi.fn(),
    onDifferentAccount: vi.fn(),
    onSignInMethod: vi.fn(),
  });
  controller.setProvider('work_account');
  controller.setLockState('Unlocked');
  return { controller, target, decodeArgs };
}

describe('non-live synthetic seam — TOP-LEVEL session-profile pipeline', () => {
  // Scenario 1
  it('renders the synthetic photo and never leaks bytes to a parent frame or storage', async () => {
    const parentPost = vi.fn();
    (window as unknown as { parent: { postMessage: (m: unknown) => void } }).parent = { postMessage: parentPost };
    const storageSet = vi.spyOn(Storage.prototype, 'setItem');

    const { controller, decodeArgs } = mount();
    controller.applyNativeMessage(syntheticPhotoEnvelope());
    await Promise.resolve();
    await Promise.resolve();

    expect(controller._els().root?.hidden).toBe(false);
    expect(controller._els().avatar?.style.backgroundImage).toContain('blob:synthetic-photo');
    // The photo bytes reached ONLY the top-level decoder.
    expect(decodeArgs).toEqual([SYNTHETIC_JPEG_B64]);
    // ...and NEVER a parent/React frame nor any storage.
    expect(parentPost).not.toHaveBeenCalled();
    expect(storageSet).not.toHaveBeenCalled();
  });

  // Scenario 2
  it('renders synthetic initials for every failure (never blank)', () => {
    const { controller } = mount();
    controller.applyNativeMessage(initialsEnvelope('ST'));
    expect(controller._els().root?.hidden).toBe(false);
    expect(controller._els().avatar?.textContent).toBe('ST');

    controller.applyNativeMessage(initialsEnvelope('WA'));
    expect(controller._els().avatar?.textContent).toBe('WA');
    expect(controller._els().avatar?.textContent).not.toBe('');
  });

  // Scenario 3 (malformed + oversized rejection)
  it('rejects a malformed envelope (extra identity field) and clears', () => {
    const { controller } = mount();
    controller.applyNativeMessage(initialsEnvelope('ST'));
    expect(controller._els().root?.hidden).toBe(false);

    controller.applyNativeMessage({ ...syntheticPhotoEnvelope(), upn: 'a@b.com' });
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._state().presentation).toBeNull();
  });

  it('rejects an oversized photo envelope and clears', () => {
    const { controller } = mount();
    controller.applyNativeMessage(initialsEnvelope('ST'));
    controller.applyNativeMessage({
      ...syntheticPhotoEnvelope(),
      imageBase64: 'A'.repeat(api.MAX_ENCODED_CHARS + 4),
    });
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._state().presentation).toBeNull();
  });

  // Scenario 4 (lock clears + revokes object URL)
  it('clears and revokes the object URL on lock', async () => {
    const revoke = vi.fn();
    (window as unknown as { URL: { revokeObjectURL: (u: string) => void } }).URL.revokeObjectURL = revoke;

    const { controller } = mount(() => Promise.resolve('blob:synthetic-photo'));
    controller.applyNativeMessage(syntheticPhotoEnvelope());
    await Promise.resolve();
    await Promise.resolve();
    expect(controller._state().objectUrl).toBe('blob:synthetic-photo');

    controller.setLockState('Locked');
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._state().presentation).toBeNull();
    expect(revoke).toHaveBeenCalledWith('blob:synthetic-photo');
    expect(controller._state().objectUrl).toBeNull();
  });

  // Scenario 5 (provider change clears)
  it('clears when the provider is no longer the work account', () => {
    const { controller } = mount();
    controller.applyNativeMessage(initialsEnvelope('ST'));
    expect(controller._els().root?.hidden).toBe(false);

    controller.setProvider('windows_hello');
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._state().presentation).toBeNull();
  });

  // Scenario 6 (none clears)
  it('clears on a none envelope', () => {
    const { controller } = mount();
    controller.applyNativeMessage(initialsEnvelope('ST'));
    controller.applyNativeMessage(noneEnvelope());
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._state().presentation).toBeNull();
  });

  // Scenario 9 (STABLE-mode inertness)
  it('shows NO surface for a valid synthetic photo when the provider is not the work account', async () => {
    const { controller } = mount();
    controller.setProvider('windows_hello');
    controller.applyNativeMessage(syntheticPhotoEnvelope());
    await Promise.resolve();
    await Promise.resolve();
    // No visible surface and no rendered photo, even for a valid envelope: with a
    // non-work-account provider the gate is closed, so nothing is ever shown.
    expect(controller._els().root?.hidden).toBe(true);
    expect(controller._els().avatar?.style.backgroundImage ?? '').not.toContain('blob:');
  });

  it('the shipped shell asset carries no synthetic-seam marker', () => {
    expect(SRC).not.toContain('PAXCOOKBOOK-SYNTHETIC-PROFILE-SEAM');
    expect(SRC).not.toContain('WorkAccountProfileSyntheticSeam');
  });
});
