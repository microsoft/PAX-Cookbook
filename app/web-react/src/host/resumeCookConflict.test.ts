/**
 * Cycle 31 — a resume refused because that checkpoint is ALREADY being resumed
 * must not be reported as "the PAX engine needs setup".
 *
 * The broker returns 409 for two unrelated reasons: the engine has not been
 * acquired (`acquisitionRequired`), and the checkpoint already has a running
 * resume (`resume_already_running`). The host layer previously collapsed EVERY
 * 409 into `engineSetupRequired`, which sent the operator to set up an engine
 * that is already working and hid the real reason the run did not start.
 *
 * These tests stub global `fetch` and assert the parsed outcome, matching the
 * existing host-contract test convention. Nothing here starts a run: the browser
 * never spawns PAX and the broker owns every gate.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { resumeCook } from './brokerBridge';
import { describeResumeCookFailure } from '../features/mini-kitchen/lib/resumeCookMessages';

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function stubResponse(status: number, body: unknown): void {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(status, body)));
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('resumeCook host contract: 409 conflict classification', () => {
  it('maps resume_already_running to a typed conflict carrying the running cook id', async () => {
    stubResponse(409, {
      error: 'resume_already_running',
      cookId: 'cook-1234',
      checkpoint: { path: 'C:\\PAX\\out' },
    });

    const res = await resumeCook({ checkpointPath: 'C:\\PAX\\out', force: false, chefKeyId: null });

    expect(res.status).toBe(409);
    expect(res.outcome.kind).toBe('alreadyRunning');
    expect(res.outcome).toEqual({ kind: 'alreadyRunning', cookId: 'cook-1234' });
  });

  it('still maps an engine acquisition 409 to engineSetupRequired', async () => {
    // Positive control: the 409 branch is not simply returning the new kind for
    // everything. The pre-existing acquisition mapping must be untouched.
    stubResponse(409, { error: 'acquisitionRequired', acquisition: { state: 'not_acquired' } });

    const res = await resumeCook({ checkpointPath: 'C:\\PAX\\out', force: false, chefKeyId: null });

    expect(res.status).toBe(409);
    expect(res.outcome.kind).toBe('engineSetupRequired');
  });

  it('maps an unrecognised 409 to engineSetupRequired exactly as before', async () => {
    stubResponse(409, { error: 'something_else_entirely' });

    const res = await resumeCook({ checkpointPath: 'C:\\PAX\\out', force: false, chefKeyId: null });

    expect(res.outcome.kind).toBe('engineSetupRequired');
  });

  it('does not let resume_already_running leak into any other status', async () => {
    // The conflict code is only meaningful on 409. On a 400 it must fall through
    // to the existing bounded error mapping rather than claiming a run is going.
    stubResponse(400, { error: 'resume_already_running' });

    const res = await resumeCook({ checkpointPath: 'C:\\PAX\\out', force: false, chefKeyId: null });

    expect(res.outcome.kind).not.toBe('alreadyRunning');
    expect(res.outcome.kind).toBe('error');
  });

  it('still maps invalid_checkpoint_path on 400 with the additive reason field present', async () => {
    // The broker now returns an additive `reason` alongside the unchanged error
    // code. The host must keep mapping on `error` only.
    stubResponse(400, {
      error: 'invalid_checkpoint_path',
      reason: 'checkpoint_path_not_found',
      message: 'checkpointPath does not exist on this PC.',
    });

    const res = await resumeCook({ checkpointPath: 'C:\\nope', force: false, chefKeyId: null });

    expect(res.outcome.kind).toBe('invalidCheckpointPath');
  });
});

describe('resume failure copy: already-running conflict', () => {
  it('says plainly that the run did not start and points at the run in flight', () => {
    const sentence = describeResumeCookFailure({ kind: 'alreadyRunning', cookId: 'cook-1234' });

    expect(sentence).toContain('already being resumed');
    expect(sentence.endsWith('The run did not start.')).toBe(true);
    // It must NOT tell the operator to set up an engine that is fine.
    expect(sentence).not.toContain('set up');
    // It must not leak an internal identifier into operator-facing copy.
    expect(sentence).not.toContain('cook-1234');
  });

  it('is distinct from the engine-setup sentence', () => {
    // Positive control: the two 409 causes now read differently, which is the
    // whole point of the new outcome.
    const conflict = describeResumeCookFailure({ kind: 'alreadyRunning', cookId: null });
    const setup = describeResumeCookFailure({ kind: 'engineSetupRequired' });

    expect(conflict).not.toBe(setup);
    expect(setup).toContain('PAX engine');
  });
});
