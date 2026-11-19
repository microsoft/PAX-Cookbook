import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MiniKitchenBuilderPreview } from '../features/mini-kitchen/MiniKitchenBuilderPreview';
import { createDefaultMiniKitchenRecipe } from '../features/mini-kitchen/lib/defaultRecipe';
import { buildFullPaxRecipeExport } from '../features/mini-kitchen/lib/fullPaxRecipeExporter';
import { setPendingImport } from '../host/importHandoff';
import { RecipesWorkspace } from './RecipesWorkspace';
import { consultNavigationGuard } from './navigationGuard';

const recipeId = '01ARZ3NDEKTSV4RRFFQ69G5FAV';
const recipesPath = '/api/v1/recipes';
const recipePath = `${recipesPath}/${recipeId}`;
const schedulePath = `${recipePath}/scheduled-task`;
const name = 'Inert mutation fixture';
const schedule = { enabled: true, scheduledTaskId: recipeId, recurrence: { kind: 'daily', hour: 3, minute: 0 } };
const diagnostic = 'inert raw scheduler diagnostic';

const scenarios = [
  {
    kind: 'commit warning',
    save: 'Recipe saved. Its skip-next marker could not be cleared.',
    delete: 'Recipe deleted. Its skip-next marker could not be cleared.',
  },
  {
    kind: 'refusal',
    save: 'PAX Cookbook could not remove the Windows task. The Recipe was not saved.',
    delete: 'PAX Cookbook could not remove the Windows task. The Recipe was not deleted.',
  },
  {
    kind: 'persistence failure',
    save: 'The Windows task is no longer registered, but PAX Cookbook could not save the Recipe. Check the Recipe and its schedule before making more changes.',
    delete: 'The Windows task is no longer registered, but PAX Cookbook could not delete the Recipe. Check the Recipe and its schedule before making more changes.',
  },
] as const;

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function holdScheduleResponse() {
  let release!: (response: Response) => void;
  const response = new Promise<Response>(resolve => { release = resolve; });
  return { response, release: (status: number, body: unknown) => release(jsonResponse(status, body)) };
}

function mockBroker(operation: 'PUT' | 'DELETE', kind: (typeof scenarios)[number]['kind'] | 'success' | 'registration failure', options: {
  failedRead?: boolean;
  drift?: boolean;
  scheduleRead?: () => Promise<Response>;
  beforePreserveReply?: () => Promise<void>;
  beforeRegistrationReply?: () => Promise<void>;
} = {}) {
  const state = createDefaultMiniKitchenRecipe();
  state.identity.name = name;
  state.destinations.fact.path = 'C:\\Fixtures\\audit.csv';
  const full = buildFullPaxRecipeExport({ state });
  if (!full.ok || !full.recipe) throw new Error('Invalid synthetic Recipe fixture.');
  const savedRecipe = { ...full.recipe.recipe, auth: { mode: 'AppRegistrationCertificate', chefKeyId: recipeId }, schedule };

  let committed = false;
  let savedName = name;
  let savedSchedule: typeof schedule | null = schedule;
  let marker: string | null = 'inert-marker';
  const reads: string[] = [];
  const writes: string[] = [];
  const putBodies: Record<string, unknown>[] = [];
  const mutations: RecordedMutation[] = [];
  const registrarCalls: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    if (method === 'GET') {
      reads.push(path);
      if (path === recipesPath) return jsonResponse(200, { recipes: committed && operation === 'DELETE' ? [] : [{ recipeId, name }] });
      if (path === recipePath) return jsonResponse(200, { recipeId, recipe: savedRecipe });
      if (path === schedulePath && options.scheduleRead) return options.scheduleRead();
      if (path === schedulePath) return options.failedRead ? jsonResponse(500, { error: 'inert_read_failure' })
        : jsonResponse(200, { recipeId, schedule: savedSchedule, drift: options.drift });
      if (path === '/api/v1/cooks') return jsonResponse(200, { cooks: [] });
      return jsonResponse(404, { error: 'inert_route_not_found' });
    }
    writes.push(`${method} ${path}`);
    const requestBody = init?.body === undefined ? {} : JSON.parse(String(init.body)) as Record<string, unknown>;
    mutations.push({ method, url: path, body: requestBody });
    if (path === schedulePath && method === 'PUT' && operation === 'PUT') {
      const { recurrence } = requestBody as { recurrence: typeof schedule.recurrence };
      registrarCalls.push(`register:${schedule.scheduledTaskId}`);
      await options.beforeRegistrationReply?.();
      if (kind === 'registration failure') return jsonResponse(502, { error: 'schedule_registration_failed' });
      savedSchedule = { ...schedule, recurrence };
      return jsonResponse(200, { recipeId, schedule: savedSchedule });
    }
    if (method !== operation || path !== recipePath) return jsonResponse(400, { error: 'unexpected_inert_write' });
    if (method === 'PUT') {
      const body = requestBody as Record<string, unknown> & { identity: { name: string } };
      putBodies.push(body);
      if (Object.hasOwn(body, 'schedule')) return jsonResponse(400, { error: 'invalid_schedule_intent' });
      if (body.scheduleIntent === 'preserve') {
        savedName = body.identity.name;
        const { scheduleIntent: _, ...content } = body;
        await options.beforePreserveReply?.();
        return jsonResponse(200, { recipeId, recipe: { ...content, schedule: savedSchedule } });
      }
      if (savedSchedule) registrarCalls.push(`unregister:${savedSchedule.scheduledTaskId}`);
      if (kind === 'success' || kind === 'commit warning') {
        savedName = body.identity.name;
        savedSchedule = null;
      }
      if (kind === 'success') {
        marker = null;
        return jsonResponse(200, { recipeId, recipe: { identity: { name: savedName } } });
      }
    }
    if (kind === 'commit warning') {
      committed = true;
      return jsonResponse(500, {
        error: 'schedule_skip_marker_clear_failed',
        [operation === 'PUT' ? 'recipeSaved' : 'recipeDeleted']: true,
        scheduleUnregistered: true,
        message: diagnostic,
      });
    }
    if (kind === 'refusal') {
      return jsonResponse(502, { error: 'schedule_unregistration_failed', message: diagnostic });
    }
    return jsonResponse(500, {
      error: operation === 'PUT' ? 'persist_failed' : 'delete_failed', scheduleUnregistered: true, message: diagnostic,
    });
  }));
  return { reads, writes, putBodies, mutations, registrarCalls, savedState: () => ({ name: savedName, schedule: savedSchedule, marker }) };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function makeDraft(draftName: string) {
  const draft = createDefaultMiniKitchenRecipe();
  draft.identity.name = draftName;
  draft.destinations.fact.path = 'C:\\Fixtures\\draft.csv';
  return draft;
}

function importDraft(draftName: string, bindSyntheticKey = false) {
  const full = buildFullPaxRecipeExport({ state: makeDraft(draftName) });
  if (!full.ok || !full.recipe) throw new Error('Invalid synthetic draft.');
  if (bindSyntheticKey) full.recipe.recipe.auth = { mode: 'AppRegistrationCertificate', chefKeyId: recipeId };
  act(() => setPendingImport({ kind: 'pax', text: JSON.stringify(full.recipe), fileName: 'inert.pax' }));
}

type RecordedMutation = { method: string; url: string; body: Record<string, unknown> };

it('creates the replacement draft instead of updating the earlier draft after a late create', async () => {
  const held = holdScheduleResponse();
  const replacementId = '01ARZ3NDEKTSV4RRFFQ69G5FAW';
  const mutations: RecordedMutation[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    if (method === 'GET') return jsonResponse(404, { error: 'inert_route_not_found' });
    const body = JSON.parse(String(init?.body)) as Record<string, unknown>;
    mutations.push({ method, url, body });
    if (mutations.length === 1) return held.response;
    return jsonResponse(200, { recipeId: replacementId, recipe: body });
  }));
  const draft = makeDraft('Inert draft A');
  render(createElement<{ initialDraftState: typeof draft }>(MiniKitchenBuilderPreview, { initialDraftState: draft }));
  fireEvent.change(await screen.findByDisplayValue('Inert draft A'), { target: { value: 'Save draft A' } });
  const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' });
  fireEvent.click(save);
  await waitFor(() => expect(mutations).toHaveLength(1));

  importDraft('Inert draft B');
  const replacement = await screen.findByDisplayValue('Inert draft B');
  await act(async () => held.release(201, { recipeId, recipe: mutations[0].body }));
  const replacementClean = save.disabled;
  fireEvent.change(replacement, { target: { value: 'Save draft B' } });
  fireEvent.click(save);
  await waitFor(() => expect(mutations).toHaveLength(2));
  console.info(JSON.stringify({ control: 'late-create', timing: ['post-a-held', 'import-b', 'reply-a-201', 'save-b'], returnedId: recipeId, replacementClean, mutations }));
  expect(mutations).toMatchObject([
    { method: 'POST', url: recipesPath, body: { identity: { name: 'Save draft A' } } },
    { method: 'POST', url: recipesPath, body: { identity: { name: 'Save draft B' } } },
  ]);
  expect(replacementClean).toBe(true);
  expect(mutations[1].body).not.toHaveProperty('scheduleIntent');
});

it('binds a current create and updates that same Recipe on the next save', async () => {
  const held = holdScheduleResponse();
  const mutations: RecordedMutation[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    if (method === 'GET') return jsonResponse(404, { error: 'inert_route_not_found' });
    const body = JSON.parse(String(init?.body)) as Record<string, unknown>;
    mutations.push({ method, url, body });
    if (mutations.length === 1) return held.response;
    return jsonResponse(200, { recipeId, recipe: body });
  }));
  const draft = makeDraft('Inert current draft');
  render(createElement<{ initialDraftState: typeof draft }>(MiniKitchenBuilderPreview, { initialDraftState: draft }));
  const input = await screen.findByDisplayValue('Inert current draft');
  const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' });
  fireEvent.change(input, { target: { value: 'Save current draft' } });
  fireEvent.click(save);
  await waitFor(() => expect(mutations).toHaveLength(1));
  await act(async () => held.release(201, { recipeId, recipe: mutations[0].body }));
  const originalClean = save.disabled;
  fireEvent.change(input, { target: { value: 'Edit same Recipe' } });
  fireEvent.click(save);
  await waitFor(() => expect(mutations).toHaveLength(2));
  console.info(JSON.stringify({ control: 'current-create', timing: ['post-held', 'reply-201', 'edit-and-save'], returnedId: recipeId, originalClean, mutations }));
  expect(originalClean).toBe(true);
  expect(mutations).toMatchObject([
    { method: 'POST', url: recipesPath, body: { identity: { name: 'Save current draft' } } },
    { method: 'PUT', url: recipePath, body: { identity: { name: 'Edit same Recipe' }, scheduleIntent: 'preserve' } },
  ]);
});

it.each(['before follow-up', 'registration success', 'registration failure'] as const)(
  'isolates a replacement from the old Save and Leave (%s)', async timing => {
    let release!: () => void;
    const held = new Promise<void>(resolve => { release = resolve; });
    const alreadySent = timing !== 'before follow-up';
    const { mutations, registrarCalls } = mockBroker('PUT', timing === 'registration failure' ? timing : 'success',
      alreadySent ? { beforeRegistrationReply: () => held } : { beforePreserveReply: () => held });
    const proceed = vi.fn();
    const cancel = vi.fn();
    render(createElement<{ initialOpenRecipeId: string }>(MiniKitchenBuilderPreview, { initialOpenRecipeId: recipeId }));
    await screen.findByDisplayValue(name);
    fireEvent.click(screen.getByRole('button', { name: /^6 Schedule/ }));
    const hour = await screen.findByRole('spinbutton', { name: 'Hour (0-23)' });
    fireEvent.change(hour, { target: { value: '4' } });
    act(() => expect(consultNavigationGuard({ section: 'settings', proceed, cancel })).toBe(true));
    const dialog = screen.getByRole('dialog', { name: 'You have unsaved changes' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save and leave' }));
    const acceptedCount = alreadySent ? 2 : 1;
    await waitFor(() => expect(mutations).toHaveLength(acceptedCount));

    importDraft('Inert replacement during update', true);
    const toggle = () => screen.getByRole<HTMLInputElement>('checkbox', { name: 'Run on a schedule' });
    expect(toggle().checked).toBe(false);
    await act(async () => release());
    const after = {
      enabled: toggle().checked,
      clean: screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' }).disabled,
      oldWarning: Boolean(screen.queryByText(/^Recipe saved\./)),
      oldPrompt: Boolean(screen.queryByRole('dialog', { name: 'You have unsaved changes' })),
      proceeded: proceed.mock.calls.length,
      canceled: cancel.mock.calls.length,
    };
    fireEvent.click(screen.getByRole('button', { name: /^1 Basics/ }));
    expect(screen.getByDisplayValue('Inert replacement during update')).toBeTruthy();
    console.info(JSON.stringify({ control: 'late-update', timing, acceptedCount, after, mutations, registrarCalls }));
    expect(after).toEqual({ enabled: false, clean: true, oldWarning: false, oldPrompt: false, proceeded: 0, canceled: 0 });
    expect(mutations).toHaveLength(acceptedCount);
    expect(mutations[0]).toMatchObject({ method: 'PUT', url: recipePath, body: { identity: { name }, scheduleIntent: 'preserve' } });
    expect(mutations[0].body).not.toHaveProperty('schedule');
    expect(registrarCalls).toEqual(alreadySent ? [`register:${schedule.scheduledTaskId}`] : []);
    if (alreadySent) {
      expect(mutations[1]).toEqual({ method: 'PUT', url: schedulePath, body: { recurrence: { kind: 'daily', hour: 4, minute: 0 } } });
    }
  },
);

it.each([200, 500])('ignores a delayed enabled schedule after importing an unscheduled draft (HTTP %s)', async status => {
  const held = holdScheduleResponse();
  const { reads, writes, putBodies, registrarCalls } = mockBroker('PUT', 'success', { scheduleRead: () => held.response });
  render(createElement<{ initialOpenRecipeId: string }>(MiniKitchenBuilderPreview, { initialOpenRecipeId: recipeId }));
  await screen.findByDisplayValue(name);
  await waitFor(() => expect(reads.filter(path => path === schedulePath)).toHaveLength(1));

  const state = createDefaultMiniKitchenRecipe();
  state.identity.name = 'Inert imported draft';
  state.destinations.fact.path = 'C:\\Fixtures\\draft.csv';
  const full = buildFullPaxRecipeExport({ state });
  if (!full.ok || !full.recipe) throw new Error('Invalid synthetic draft.');
  full.recipe.recipe.auth = { mode: 'AppRegistrationCertificate', chefKeyId: recipeId };
  act(() => setPendingImport({ kind: 'pax', text: JSON.stringify(full.recipe), fileName: 'inert.pax' }));
  await screen.findByDisplayValue(state.identity.name);
  fireEvent.click(screen.getByRole('button', { name: /^6 Schedule/ }));
  const card = () => ({
    enabled: screen.getByRole<HTMLInputElement>('checkbox', { name: 'Run on a schedule' }).checked,
    drift: Boolean(screen.queryByText(/Windows task does not match/)),
    unavailable: Boolean(screen.queryByText(/Schedule unavailable|Could not read this recipe/)),
    repair: Boolean(screen.queryByRole('button', { name: 'Repair schedule' })),
    clean: screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' }).disabled,
  });
  const before = card();
  const released = status === 200 ? { recipeId, schedule, drift: true } : { error: 'inert_read_failure' };
  await act(async () => held.release(status, released));
  const after = card();
  console.info(JSON.stringify({ control: 'late-import', status, reads, released, before, after, writes, putBodies, registrarCalls }));
  expect(before).toEqual({ enabled: false, drift: false, unavailable: false, repair: false, clean: true });
  expect(after).toEqual(before);
  fireEvent.click(screen.getByRole('checkbox', { name: 'Run on a schedule' }));
  expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' }).disabled).toBe(false);
  expect(writes).toEqual([]);
  expect(registrarCalls).toEqual([]);
});

it.each([200, 500])('keeps the newer resolved schedule when a superseded read finishes (HTTP %s)', async staleStatus => {
  const older = holdScheduleResponse();
  const newer = holdScheduleResponse();
  const pending = [older, newer];
  const { reads, writes, putBodies, registrarCalls, savedState } = mockBroker('PUT', 'success', {
    scheduleRead: () => pending.shift()!.response,
  });
  render(createElement<{ initialOpenRecipeId: string }>(MiniKitchenBuilderPreview, { initialOpenRecipeId: recipeId }));
  await screen.findByDisplayValue(name);
  fireEvent.click(screen.getByRole('button', { name: /^6 Schedule/ }));
  await screen.findByText('Loading schedule...');
  expect(screen.queryByRole('checkbox', { name: 'Run on a schedule' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Reload schedule' }));
  expect(reads.filter(path => path === schedulePath)).toHaveLength(2);
  await act(async () => newer.release(200, { recipeId, schedule, drift: true }));

  const card = () => ({
    enabled: screen.getByRole<HTMLInputElement>('checkbox', { name: 'Run on a schedule' }).checked,
    hour: screen.getByRole<HTMLInputElement>('spinbutton', { name: 'Hour (0-23)' }).value,
    drift: Boolean(screen.queryByText(/Windows task does not match/)),
    repairEnabled: !screen.getByRole<HTMLButtonElement>('button', { name: 'Repair schedule' }).disabled,
    clean: screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' }).disabled,
  });
  const before = card();
  const staleReply = staleStatus === 200
    ? { recipeId, schedule: { ...schedule, recurrence: { ...schedule.recurrence, hour: 10 } }, drift: false }
    : { error: 'inert_read_failure' };
  await act(async () => older.release(staleStatus, staleReply));
  const after = card();
  expect(before).toEqual({ enabled: true, hour: '3', drift: true, repairEnabled: true, clean: true });
  expect(after).toEqual(before);
  expect(writes).toEqual([]);

  fireEvent.click(screen.getByRole('checkbox', { name: 'Run on a schedule' }));
  const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' });
  expect(save.disabled).toBe(false);
  fireEvent.click(save);
  await waitFor(() => expect(savedState().schedule).toBeNull());
  await waitFor(() => expect(save.disabled).toBe(true));
  console.info(JSON.stringify({ control: 'newer-read', staleStatus, staleReply, reads, before, after, writes, putBodies, registrarCalls }));
  expect(putBodies).toHaveLength(1);
  expect(putBodies[0]).toMatchObject({ scheduleIntent: 'disable' });
  expect(putBodies[0]).not.toHaveProperty('schedule');
  expect(writes).toEqual([`PUT ${recipePath}`]);
  expect(registrarCalls).toEqual([`unregister:${schedule.scheduledTaskId}`]);
});

it('preserves on a pending schedule read even if the read resolves before the content PUT returns', async () => {
  const held = holdScheduleResponse();
  let releasePut!: () => void;
  const putReply = new Promise<void>(resolve => { releasePut = resolve; });
  const { reads, writes, putBodies, registrarCalls, savedState } = mockBroker('PUT', 'success', {
    scheduleRead: () => held.response,
    beforePreserveReply: () => putReply,
  });
  const before = savedState();
  render(createElement<{ initialOpenRecipeId: string }>(MiniKitchenBuilderPreview, { initialOpenRecipeId: recipeId }));
  fireEvent.change(await screen.findByDisplayValue(name), { target: { value: `${name} edited` } });
  fireEvent.click(screen.getByRole('button', { name: /^6 Schedule/ }));
  await screen.findByText('Loading schedule...');
  expect(screen.queryByRole('checkbox', { name: 'Run on a schedule' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Repair schedule' })).toBeNull();
  const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' });
  fireEvent.click(save);
  await waitFor(() => expect(putBodies).toHaveLength(1));
  await act(async () => held.release(200, { recipeId, schedule }));
  const current = screen.getByRole<HTMLInputElement>('checkbox', { name: 'Run on a schedule' });
  expect(current.checked).toBe(true);
  await act(async () => releasePut());
  const after = savedState();
  const clean = save.disabled;
  console.info(JSON.stringify({ control: 'save-during-read', reads, before, after, clean, writes, putBodies, registrarCalls }));
  expect(clean).toBe(true);
  expect(putBodies[0]).toMatchObject({ identity: { name: `${name} edited` }, scheduleIntent: 'preserve' });
  expect(putBodies[0]).not.toHaveProperty('schedule');
  expect(writes).toEqual([`PUT ${recipePath}`]);
  expect(registrarCalls).toEqual([]);
  expect(after).toEqual({ ...before, name: `${name} edited` });
});

it.each([false, true])('preserves the authoritative schedule on a name-only save (failed read: %s)', async failedRead => {
  const { writes, putBodies, registrarCalls, savedState } = mockBroker('PUT', 'success', { failedRead });
  const before = savedState();
  render(createElement<{ initialOpenRecipeId: string }>(MiniKitchenBuilderPreview, { initialOpenRecipeId: recipeId }));
  const input = await screen.findByDisplayValue(name);
  fireEvent.change(input, { target: { value: `${name} edited` } });
  if (failedRead) {
    fireEvent.click(screen.getByRole('button', { name: /^6 Schedule/ }));
    await screen.findByText('Schedule unavailable');
    expect(screen.queryByRole('checkbox', { name: 'Run on a schedule' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Repair schedule' })).toBeNull();
  }
  const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' });
  await waitFor(() => expect(save.disabled).toBe(false));
  fireEvent.click(save);
  await waitFor(() => expect(save.disabled).toBe(true));
  const after = savedState();
  console.info(JSON.stringify({ control: 'preserve', failedRead, putBodies, writes, registrarCalls, before, after }));
  expect(putBodies).toHaveLength(1);
  expect(putBodies[0]).toMatchObject({ identity: { name: `${name} edited` }, scheduleIntent: 'preserve' });
  expect(putBodies[0]).not.toHaveProperty('schedule');
  expect(registrarCalls).toEqual([]);
  expect(writes).toEqual([`PUT ${recipePath}`]);
  expect(after).toEqual({ ...before, name: `${name} edited` });
});

it.each([false, true])('disables once or changes recurrence without removal (disable: %s)', async disable => {
  const { writes, putBodies, registrarCalls, savedState } = mockBroker('PUT', disable ? 'success' : 'registration failure');
  const before = savedState();
  render(createElement<{ initialOpenRecipeId: string }>(MiniKitchenBuilderPreview, { initialOpenRecipeId: recipeId }));
  await screen.findByDisplayValue(name);
  fireEvent.click(screen.getByRole('button', { name: /^6 Schedule/ }));
  const toggle = await screen.findByRole<HTMLInputElement>('checkbox', { name: 'Run on a schedule' });
  await waitFor(() => expect(toggle.checked).toBe(true));
  if (disable) fireEvent.click(toggle);
  else fireEvent.change(screen.getByRole('spinbutton', { name: 'Hour (0-23)' }), { target: { value: '4' } });
  const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' });
  fireEvent.click(save);
  if (disable) await waitFor(() => expect(save.disabled).toBe(true));
  else await screen.findByText(/^Recipe saved\. The scheduled run could not be registered/);
  const after = savedState();
  console.info(JSON.stringify({ control: disable ? 'disable' : 'failed-registration', putBodies, writes, registrarCalls, before, after }));
  expect(putBodies).toHaveLength(1);
  expect(putBodies[0]).toMatchObject({ scheduleIntent: disable ? 'disable' : 'preserve' });
  expect(putBodies[0]).not.toHaveProperty('schedule');
  expect(writes).toEqual(disable ? [`PUT ${recipePath}`] : [`PUT ${recipePath}`, `PUT ${schedulePath}`]);
  expect(registrarCalls).toEqual([`${disable ? 'unregister' : 'register'}:${schedule.scheduledTaskId}`]);
  expect(after).toEqual(disable ? { ...before, schedule: null, marker: null } : before);
});

it('repairs drift only on an explicit action through the dedicated schedule route', async () => {
  const { writes, putBodies, registrarCalls, savedState } = mockBroker('PUT', 'success', { drift: true });
  const before = savedState();
  render(createElement<{ initialOpenRecipeId: string }>(MiniKitchenBuilderPreview, { initialOpenRecipeId: recipeId }));
  await screen.findByDisplayValue(name);
  fireEvent.click(screen.getByRole('button', { name: /^6 Schedule/ }));
  const repair = await screen.findByRole('button', { name: 'Repair schedule' });
  expect(writes).toEqual([]);
  fireEvent.click(repair);
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Repair schedule' })).toBeNull());
  console.info(JSON.stringify({ control: 'repair', putBodies, writes, registrarCalls, before, after: savedState() }));
  expect(writes).toEqual([`PUT ${schedulePath}`]);
  expect(registrarCalls).toEqual([`register:${schedule.scheduledTaskId}`]);
  expect(savedState()).toEqual(before);
});

describe.each(scenarios)('$kind', scenario => {
  const committed = scenario.kind === 'commit warning';

  it('shows the ordinary save outcome and only refreshes on a confirmed commit', async () => {
    const { reads, writes, putBodies, registrarCalls, savedState } = mockBroker('PUT', scenario.kind);
    const before = savedState();
    render(createElement<{ initialOpenRecipeId: string }>(MiniKitchenBuilderPreview, { initialOpenRecipeId: recipeId }));
    const input = await screen.findByDisplayValue(name);
    await waitFor(() => expect(reads.filter(path => path === schedulePath)).toHaveLength(1));
    fireEvent.change(input, { target: { value: `${name} edited` } });
    fireEvent.click(screen.getByRole('button', { name: /^6 Schedule/ }));
    const toggle = await screen.findByRole<HTMLInputElement>('checkbox', { name: 'Run on a schedule' });
    await waitFor(() => expect(toggle.checked).toBe(true));
    fireEvent.click(toggle);
    const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save recipe' });
    await waitFor(() => expect(save.disabled).toBe(false));
    fireEvent.click(save);

    expect((await screen.findByText(scenario.save)).getAttribute('role')).toBe('status');
    expect(screen.queryByText(diagnostic)).toBeNull();
    expect(save.disabled).toBe(committed);
    expect(reads.filter(path => path === schedulePath)).toHaveLength(committed ? 2 : 1);
    expect(writes).toEqual([`PUT ${recipePath}`]);
    expect(putBodies).toHaveLength(1);
    expect(putBodies[0]).toMatchObject({ scheduleIntent: 'disable' });
    expect(putBodies[0]).not.toHaveProperty('schedule');
    expect(registrarCalls).toEqual([`unregister:${schedule.scheduledTaskId}`]);
    const after = savedState();
    expect(after).toEqual(committed ? { ...before, name: `${name} edited`, schedule: null } : before);
    console.info(JSON.stringify({ control: scenario.kind, putBodies, writes, registrarCalls, before, after }));
  });

  it('shows the ordinary delete outcome and only refreshes on a confirmed commit', async () => {
    const { reads, writes } = mockBroker('DELETE', scenario.kind);
    render(createElement(RecipesWorkspace));
    fireEvent.click(await screen.findByRole('button', { name: 'Delete recipe' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete this recipe?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete recipe' }));

    expect((await screen.findByText(scenario.delete)).getAttribute('role')).toBe(committed ? 'status' : 'alert');
    expect(screen.queryByText(diagnostic)).toBeNull();
    if (committed) {
      await waitFor(() => expect(screen.queryByRole('button', { name: 'Delete recipe' })).toBeNull());
    }
    expect(screen.queryByRole('dialog', { name: 'Delete this recipe?' }) === null).toBe(committed);
    expect(reads.filter(path => path === recipesPath)).toHaveLength(committed ? 2 : 1);
    expect(writes).toEqual([`DELETE ${recipePath}`]);
  });
});