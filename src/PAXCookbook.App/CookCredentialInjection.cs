using System.Text;

namespace PAXCookbook.App;

// Bake-time Graph credential injection for the single sanctioned PAX child (CK-3).
//
// This is the ONE place that turns a recipe's bound Chef's Key into the GRAPH_*
// environment variables the managed PAX engine reads for App-registration auth.
// It is deliberately small and self-contained so the constraint-14 posture can be
// audited in a single file:
//
//   * The credentials are placed on the CHILD process environment ONLY (the
//     ProcessStartInfo.Environment dictionary, which UseShellExecute=false makes a
//     per-child copy). The broker's own process environment is NEVER touched --
//     there is no Environment.SetEnvironmentVariable anywhere in this file.
//   * The client secret is read from Windows Credential Manager as a byte[] and is
//     converted to the managed GRAPH_CLIENT_SECRET string ONLY at the moment of
//     assignment; the byte[] is zeroed (Array.Clear) immediately afterward.
//   * .NET string immutability means the managed GRAPH_CLIENT_SECRET string cannot
//     be truly zeroed. The realistic mitigation is lifetime minimization: the
//     caller removes the entry from the parent ProcessStartInfo.Environment the
//     moment the child has inherited the env block (see ScrubSecretEnv), so the
//     string is unreferenced and GC-eligible almost immediately after spawn.
//   * The GRAPH_* values are NEVER written to cook.log, the started/finished/
//     interrupted sentinels, command.txt / command-argv.json, the readiness
//     snapshot, the cook row, or any route response. Only the child's env block
//     ever carries them.
//
// WebLogin / DeviceCode (and any non-App mode) are interactive and receive NO
// GRAPH_* variables; PAX performs the sign-in itself.
internal static class CookCredentialInjection
{
    internal const string GraphTenantId = "GRAPH_TENANT_ID";
    internal const string GraphClientId = "GRAPH_CLIENT_ID";
    internal const string GraphClientSecret = "GRAPH_CLIENT_SECRET";
    internal const string GraphClientCertThumbprint = "GRAPH_CLIENT_CERT_THUMBPRINT";

    // The result of applying credential injection to a child environment.
    internal enum CredentialInjectionOutcome
    {
        // No injection performed: interactive / managed-identity / unbound recipe.
        None,

        // GRAPH_* variables were placed on the child environment (the client
        // secret too, for AppRegistrationSecret recipes).
        Injected,

        // The recipe is AppRegistrationSecret and the Chef's Key was expected to
        // carry a secret (gate-14 HasSecret was true), but the secret could not be
        // read from Windows Credential Manager at spawn time. The caller must fail
        // the spawn with a bounded, secret-free error rather than launch a child
        // that cannot authenticate.
        SecretMissingAtSpawn,
    }

    // Pure builder. Given a resolved (secret-free) Chef's Key and -- for the
    // secret mode -- the raw secret bytes read from WCM, returns the GRAPH_*
    // additions for the child process. The secretBytes buffer is ALWAYS zeroed
    // before this method returns (constraint 14). Returns an empty dictionary for
    // interactive / unbound recipes.
    //
    // secretBytes is the UTF-16LE blob exactly as stored by Chef's Keys (CK-1
    // writes Encoding.Unicode.GetBytes(clientSecret)); it is decoded with the same
    // encoding here.
    internal static Dictionary<string, string> BuildGraphCredentialEnv(
        ChefKeyModel.ChefKeyResolved? ck, byte[]? secretBytes)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (ck is null)
            {
                return env;
            }

            switch (ck.AuthType)
            {
                case ChefKeyModel.AuthAppRegSecret:
                    AddIfPresent(env, GraphTenantId, ck.TenantId);
                    AddIfPresent(env, GraphClientId, ck.ClientId);
                    // Decode the secret to a managed string ONLY here, at the point
                    // of assignment. The byte[] is zeroed in the finally below.
                    if (secretBytes is { Length: > 0 })
                    {
                        env[GraphClientSecret] = Encoding.Unicode.GetString(secretBytes);
                    }
                    break;

                case ChefKeyModel.AuthAppRegCertificate:
                    AddIfPresent(env, GraphTenantId, ck.TenantId);
                    AddIfPresent(env, GraphClientId, ck.ClientId);
                    AddIfPresent(env, GraphClientCertThumbprint, ck.CertThumbprint);
                    break;

                // WebLogin / DeviceCode / anything else: interactive sign-in, no
                // GRAPH_* credentials.
                default:
                    break;
            }

            return env;
        }
        finally
        {
            if (secretBytes is not null)
            {
                Array.Clear(secretBytes, 0, secretBytes.Length);
            }
        }
    }

    // Integration wrapper used by the supervisor (and the credential-env test
    // seam). Reads the client secret from WCM for AppRegistrationSecret recipes,
    // builds the GRAPH_* additions, and copies them onto the supplied CHILD
    // environment dictionary (ProcessStartInfo.Environment). The transient
    // builder dictionary is cleared after the copy so the only managed reference
    // to the secret string is the child-env entry, which the caller scrubs after
    // the child has started.
    //
    // Returns:
    //   None                -> no GRAPH_* set (interactive / unbound recipe).
    //   Injected            -> GRAPH_* set (secret included for the secret mode).
    //   SecretMissingAtSpawn-> AppRegistrationSecret with a previously-confirmed
    //                          secret that is no longer readable; caller must fail
    //                          the spawn (no doomed child).
    internal static CredentialInjectionOutcome ApplyChildCredentialEnv(
        IDictionary<string, string?> childEnv, ChefKeyModel.ChefKeyResolved? ck)
    {
        if (ck is null ||
            (ck.AuthType != ChefKeyModel.AuthAppRegSecret &&
             ck.AuthType != ChefKeyModel.AuthAppRegCertificate))
        {
            return CredentialInjectionOutcome.None;
        }

        // Read the secret as late as possible, only for the secret mode. The bytes
        // are handed to BuildGraphCredentialEnv, which zeroes them.
        byte[]? secretBytes = ck.AuthType == ChefKeyModel.AuthAppRegSecret
            ? ChefKeyModel.ReadRecipeSecretBytes(ck.ChefKeyId)
            : null;

        Dictionary<string, string> additions = BuildGraphCredentialEnv(ck, secretBytes);
        try
        {
            // Defense in depth: a secret-mode recipe that reaches spawn without a
            // readable secret must not launch a child that cannot authenticate.
            if (ck.AuthType == ChefKeyModel.AuthAppRegSecret &&
                !additions.ContainsKey(GraphClientSecret))
            {
                return CredentialInjectionOutcome.SecretMissingAtSpawn;
            }

            foreach (KeyValuePair<string, string> kv in additions)
            {
                childEnv[kv.Key] = kv.Value;
            }

            return CredentialInjectionOutcome.Injected;
        }
        finally
        {
            // Drop the only other managed reference to the secret string; the
            // child-env copy remains until the caller scrubs it post-spawn.
            additions.Clear();
        }
    }

    // Removes GRAPH_CLIENT_SECRET from the CHILD environment dictionary once the
    // child has inherited the env block, minimizing the secret string's lifetime
    // in the parent process. Safe to call when the key is absent (interactive
    // recipes, or a second defensive call on the spawn-failure path).
    internal static void ScrubSecretEnv(IDictionary<string, string?> childEnv)
    {
        childEnv.Remove(GraphClientSecret);
    }

    private static void AddIfPresent(IDictionary<string, string> env, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            env[key] = value;
        }
    }
}
