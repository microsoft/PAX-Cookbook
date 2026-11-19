import { afterEach, describe, expect, it, vi } from 'vitest';
import { deleteRecipe, updateRecipe } from './brokerBridge';

afterEach(() => vi.unstubAllGlobals());

const recipeId = '01ARZ3NDEKTSV4RRFFQ69G5FAV';
const markerError = 'schedule_skip_marker_clear_failed';

function reply(status: number, body: Record<string, unknown>) {
  const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  }));
  vi.stubGlobal('fetch', fetch);
  return fetch;
}

describe.each([
  { method: 'PUT', flag: 'recipeSaved', otherFlag: 'recipeDeleted', persistError: 'persist_failed', invoke: () => updateRecipe(recipeId, {}) },
  { method: 'DELETE', flag: 'recipeDeleted', otherFlag: 'recipeSaved', persistError: 'delete_failed', invoke: () => deleteRecipe(recipeId) },
])('$method ordinary Recipe mutation', ({ method, flag, otherFlag, persistError, invoke }) => {
  it('retains the normal authenticated mutation path for a 200 commit', async () => {
    const body = { recipeId };
    const fetch = reply(200, body);
    expect(await invoke()).toMatchObject({ ok: true, outcome: 'committed', data: body });
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch).toHaveBeenCalledWith(`/api/v1/recipes/${recipeId}`, expect.objectContaining({
      method,
      headers: expect.objectContaining({ 'X-Cookbook-Request': '1' }),
    }));
  });

  it('projects a committed marker warning without passing raw diagnostics to the caller', async () => {
    reply(500, { error: markerError, [flag]: true, scheduleUnregistered: true, message: 'synthetic diagnostic' });
    expect(await invoke()).toMatchObject({
      ok: false, outcome: 'committedWithMarkerWarning', message: null, rawText: '', data: null,
    });
  });

  it.each([500, 502])('keeps an unregister refusal distinct (HTTP %i)', async status => {
    reply(status, { error: 'schedule_unregistration_failed', registrarExitCode: 6, message: 'synthetic diagnostic' });
    expect(await invoke()).toMatchObject({ ok: false, outcome: 'unregisterRefused', message: null, rawText: '' });
  });

  it('keeps a failed local commit after unregister distinct from a commit', async () => {
    reply(500, { error: persistError, scheduleUnregistered: true });
    expect(await invoke()).toMatchObject({ ok: false, outcome: 'persistFailedAfterUnregister' });
  });

  it.each(['other operation', 'string flag', 'not unregistered', 'wrong status'])('does not infer a commit from %s', async mode => {
    reply(mode === 'wrong status' ? 502 : 500, {
      error: markerError,
      [mode === 'other operation' ? otherFlag : flag]: mode === 'string flag' ? 'true' : true,
      scheduleUnregistered: mode !== 'not unregistered',
    });
    expect(await invoke()).toMatchObject({ ok: false, outcome: 'failed' });
  });
});