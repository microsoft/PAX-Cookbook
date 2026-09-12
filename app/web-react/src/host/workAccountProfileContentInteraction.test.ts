import { describe, expect, it, vi } from 'vitest';
import {
  installWorkAccountProfileContentInteraction,
  WORK_ACCOUNT_PROFILE_CONTENT_INTERACTION_TYPE,
} from './workAccountProfileContentInteraction';

function makeHost(parent?: { postMessage: (message: unknown, origin: string) => void }) {
  const eventRoot = document.createElement('div');
  const host = {
    parent,
    location: { origin: 'https://cookbook.test' },
    addEventListener: eventRoot.addEventListener.bind(eventRoot),
  };
  return { eventRoot, host };
}

describe('Work-account profile content-interaction courier', () => {
  it('posts exactly the type-only message to the parent with the exact origin', () => {
    const postMessage = vi.fn();
    const { eventRoot, host } = makeHost({ postMessage });

    expect(installWorkAccountProfileContentInteraction(host)).toBe(true);
    eventRoot.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true }));

    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledWith(
      { type: WORK_ACCOUNT_PROFILE_CONTENT_INTERACTION_TYPE },
      'https://cookbook.test',
    );
    expect(Object.keys(postMessage.mock.calls[0][0])).toEqual(['type']);
  });

  it('does not prevent the underlying target click', () => {
    const postMessage = vi.fn();
    const { eventRoot, host } = makeHost({ postMessage });
    const target = document.createElement('button');
    const clickAction = vi.fn();
    target.addEventListener('click', clickAction);
    eventRoot.appendChild(target);
    installWorkAccountProfileContentInteraction(host);

    const pointerDown = new MouseEvent('pointerdown', {
      bubbles: true,
      cancelable: true,
    });
    target.dispatchEvent(pointerDown);
    target.click();

    expect(pointerDown.defaultPrevented).toBe(false);
    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(clickAction).toHaveBeenCalledTimes(1);
  });

  it('registers only once per window-like host', () => {
    const postMessage = vi.fn();
    const { eventRoot, host } = makeHost({ postMessage });

    expect(installWorkAccountProfileContentInteraction(host)).toBe(true);
    expect(installWorkAccountProfileContentInteraction(host)).toBe(false);
    eventRoot.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true }));

    expect(postMessage).toHaveBeenCalledTimes(1);
  });

  it('fails harmlessly when the environment or parent is unavailable', () => {
    expect(installWorkAccountProfileContentInteraction(null)).toBe(false);

    const { eventRoot, host } = makeHost();
    expect(() => installWorkAccountProfileContentInteraction(host)).not.toThrow();
    expect(() => eventRoot.dispatchEvent(new MouseEvent('pointerdown'))).not.toThrow();
  });
});