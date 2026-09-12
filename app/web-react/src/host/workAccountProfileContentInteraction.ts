export const WORK_ACCOUNT_PROFILE_CONTENT_INTERACTION_TYPE =
  'cookbook:work-account-profile-content-interaction';

interface ParentMessageTarget {
  postMessage(message: unknown, targetOrigin: string): void;
}

interface ContentInteractionWindow {
  parent?: ParentMessageTarget | ContentInteractionWindow;
  location?: { origin?: string };
  addEventListener(
    type: 'pointerdown',
    listener: EventListener,
    options: boolean,
  ): void;
}

const installedWindows = new WeakSet<object>();

export function installWorkAccountProfileContentInteraction(
  hostWindow: ContentInteractionWindow | null =
    typeof window === 'undefined' ? null : window,
): boolean {
  if (!hostWindow || typeof hostWindow.addEventListener !== 'function') {
    return false;
  }
  if (installedWindows.has(hostWindow)) {
    return false;
  }
  installedWindows.add(hostWindow);

  hostWindow.addEventListener('pointerdown', () => {
    try {
      const parent = hostWindow.parent;
      const origin = hostWindow.location?.origin;
      if (
        !parent ||
        parent === hostWindow ||
        !('postMessage' in parent) ||
        typeof parent.postMessage !== 'function' ||
        !origin ||
        origin === 'null'
      ) {
        return;
      }
      parent.postMessage(
        { type: WORK_ACCOUNT_PROFILE_CONTENT_INTERACTION_TYPE },
        origin,
      );
    } catch {
      // Standalone/dev and a disappearing parent are harmless.
    }
  }, true);

  return true;
}