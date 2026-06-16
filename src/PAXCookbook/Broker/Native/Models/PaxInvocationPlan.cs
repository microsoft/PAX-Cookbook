namespace PAXCookbook.Broker.Native.Models;

// Stage 3e -- mirror of the hashtable returned by
// Get-PaxInvocationPlan (app/broker/Pax/Adapter.psm1). The native
// broker invokes a hidden one-shot pwsh sidecar that imports
// Adapter.psm1 and emits this object as JSON; PaxInvocationPlanProvider
// deserializes the JSON into this record. The field names match the
// adapter's hashtable keys exactly so the JsonPropertyName attributes
// are unnecessary -- System.Text.Json's case-insensitive matching is
// sufficient given the upstream contract is fixed.
//
// Verbatim PS contract (Adapter.psm1 ~line 811):
//   @{
//     paxArgv        = [string[]]
//     extraArguments = string
//     paxCommand     = string
//     spawnArgv      = [string[]]  # ('-NoProfile','-NoLogo','-Command', commandExpr)
//     spawnCommand   = string      # human-readable, never spawned
//     paxScriptPath  = string      # absolute path the adapter was told
//   }
//
// Stage 3e treats spawnArgv as the canonical argv for the child
// pwsh.exe -- the runner does NOT re-quote spawnArgv[3]; PowerShell's
// own argument quoting handles that automatically when the ArgumentList
// collection is populated element-by-element.
public sealed record PaxInvocationPlan(
    string[] PaxArgv,
    string   ExtraArguments,
    string   PaxCommand,
    string[] SpawnArgv,
    string   SpawnCommand,
    string   PaxScriptPath);
