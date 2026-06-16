/**
 * Operator-facing failure messaging for a non-started bake (startCook outcome).
 *
 * The single source of truth for the sentence shown when a bake does not start,
 * so the Recipes homepage Bake and the builder Bake speak with one voice and
 * the security-critical "the bake did not start" wording cannot drift between
 * them. Every sentence states plainly that the bake did not start, so a refused
 * request can never read as a silent success.
 *
 * Pure: no fetch, no PAX, no state. The broker owns the real gate decisions and
 * the single execution channel; this only renders the typed outcome the broker
 * already returned.
 */
import type { StartCookOutcome } from '../../../host/brokerBridge';

/**
 * Map a non-started startCook outcome to a bounded, operator-facing sentence.
 * Every sentence states plainly that the bake did not start so a refused
 * request can never read as a silent success.
 */
export function describeStartCookFailure(outcome: StartCookOutcome): string {
  switch (outcome.kind) {
    case 'reauthRequired':
      return 'Windows Hello confirmation is required to bake. Try again to confirm. The bake did not start.';
    case 'unauthorized':
      return 'PAX Cookbook needs you to sign in again before it can bake. Reopen the recipe and try again. The bake did not start.';
    case 'forbidden':
      return 'PAX Cookbook refused the bake request. Reload PAX Cookbook and try again. The bake did not start.';
    case 'locked':
      return 'PAX Cookbook is locked right now. Unlock it, then bake. The bake did not start.';
    case 'engineSetupRequired':
      return 'The PAX engine still needs to be set up on this PC before a bake can run. Set it up, then try again. The bake did not start.';
    case 'recipeBusy':
      return 'This recipe is already baking. Open the Bakes page to follow the run already in progress. A new bake was not started.';
    case 'validationFailed':
      return 'This recipe is not valid to bake yet. Re-check readiness, fix the highlighted items, save, and try again. The bake did not start.';
    case 'notFound':
      return 'That saved recipe no longer exists. Save it again, then bake. The bake did not start.';
    case 'invalidRecipeId':
      return 'PAX Cookbook could not identify this recipe. Reopen it from the list and try again. The bake did not start.';
    case 'appAuthUnsupported':
      return 'App-registration sign-in cannot bake in this build. Switch the recipe to an interactive sign-in, then bake. The bake did not start.';
    case 'integrityFailed':
      return 'PAX Cookbook could not verify the PAX engine on this PC, so it would not start the bake. Set up the engine again, then try. The bake did not start.';
    case 'diskSpace':
      return 'There is not enough disk space to start this bake. Free some space, then try again. The bake did not start.';
    case 'network':
      return 'Could not reach PAX Cookbook to start the bake. Make sure it is running, then open the Bakes page to check before retrying.';
    case 'started':
      return '';
    default:
      return 'PAX Cookbook could not start the bake. Open the Bakes page to verify before retrying. The bake did not start.';
  }
}
