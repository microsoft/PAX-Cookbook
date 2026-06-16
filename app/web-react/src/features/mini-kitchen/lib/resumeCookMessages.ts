/**
 * Operator-facing failure messaging for a non-started resume (resumeCook outcome).
 *
 * The single source of truth for the sentence shown when a resume does not
 * start, mirroring startCookMessages so the resume flow speaks with the same
 * voice as a bake. Every sentence states plainly that the run did not start, so
 * a refused request can never read as a silent success.
 *
 * Pure: no fetch, no PAX, no state. The broker owns the real gate decisions and
 * the single sanctioned cook core; this only renders the typed outcome the
 * broker already returned.
 */
import type { ResumeCookOutcome } from '../../../host/brokerBridge';

/**
 * Map a non-started resumeCook outcome to a bounded, operator-facing sentence.
 * Every sentence states plainly that the run did not start so a refused request
 * can never read as a silent success.
 */
export function describeResumeCookFailure(outcome: ResumeCookOutcome): string {
  switch (outcome.kind) {
    case 'reauthRequired':
      return 'Windows Hello confirmation is required to resume. Try again to confirm. The run did not start.';
    case 'unauthorized':
      return 'PAX Cookbook needs you to sign in again before it can resume. Reopen PAX Cookbook and try again. The run did not start.';
    case 'forbidden':
      return 'PAX Cookbook refused the resume request. Reload PAX Cookbook and try again. The run did not start.';
    case 'locked':
      return 'PAX Cookbook is locked right now. Unlock it, then resume. The run did not start.';
    case 'engineSetupRequired':
      return 'The PAX engine still needs to be set up on this PC before a run can resume. Set it up, then try again. The run did not start.';
    case 'invalidCheckpointPath':
      return 'PAX Cookbook could not use that checkpoint location. Point it at the output folder — or the checkpoint .json — left by an interrupted run, then try again. The run did not start.';
    case 'pathTooLong':
      return 'That checkpoint path is too long for PAX Cookbook to use. Move the run output closer to the drive root, then try again. The run did not start.';
    case 'chefKeyProblem':
      return 'The chosen Chef\u2019s Key cannot sign in for this resume. Pick a different sign-in or leave the checkpoint\u2019s saved sign-in, then try again. The run did not start.';
    case 'integrityFailed':
      return 'PAX Cookbook could not verify the PAX engine on this PC, so it would not start the run. Set up the engine again, then try. The run did not start.';
    case 'diskSpace':
      return 'There is not enough disk space to resume this run. Free some space, then try again. The run did not start.';
    case 'network':
      return 'Could not reach PAX Cookbook to resume the run. Make sure it is running, then open the Bakes page to check before retrying.';
    case 'started':
      return '';
    default:
      return 'PAX Cookbook could not resume the run. Open the Bakes page to verify before retrying. The run did not start.';
  }
}
