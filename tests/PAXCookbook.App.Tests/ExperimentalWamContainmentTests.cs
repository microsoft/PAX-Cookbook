using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S2A — containment invariants: no token-bearing member on the neutral/WAM
// DTOs, no hardcoded registration identifier in the experimental WAM product
// source, and no experimental provider exposure when the gate is disabled.
public sealed class ExperimentalWamContainmentTests
{
    private static readonly string[] ForbiddenMemberFragments =
    {
        "token", "secret", "password", "assertion", "bearer", "credential",
    };

    private static readonly Type[] NeutralAndWamDtos =
    {
        typeof(SessionUnlockOutcome),
        typeof(WamInteractiveResult),
        typeof(WamAuthRequest),
        typeof(NeutralWamResult),
        typeof(WamNativeDescriptor),
    };

    [Fact]
    public void NeutralAndWamDtos_HaveNoTokenBearingMembers()
    {
        foreach (Type t in NeutralAndWamDtos)
        {
            var members = t
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m is PropertyInfo or FieldInfo)
                .Select(m => m.Name.ToLowerInvariant());

            foreach (string name in members)
            {
                foreach (string forbidden in ForbiddenMemberFragments)
                {
                    Assert.False(name.Contains(forbidden),
                        $"{t.Name} exposes a forbidden token-bearing member '{name}'.");
                }
            }
        }
    }

    [Fact]
    public void ExperimentalWamProductSource_ContainsNoHardcodedGuid()
    {
        string srcDir = LocateAppSourceDir();
        string[] wamFiles =
        {
            "ExperimentalWamConfig.cs",
            "AuthProviderSelection.cs",
            "ExperimentalWamContracts.cs",
            "ExperimentalWamScopeIdentity.cs",
            "ExperimentalWamChallenge.cs",
            "WamServiceFailureClassifier.cs",
            "EntraWamAuthProviders.cs",
            "ExperimentalWamReleaseGate.cs",
            "MsalEntraWamAuthenticator.cs",
            "WorkAccountIssuedIdentity.cs",
            "NeutralWamResult.cs",
            "ExperimentalWamWindowBridge.cs",
            "ExperimentalWamDaemonEndpoint.cs",
            "ExperimentalWamHost.cs",
            "ExperimentalWamRoutes.cs",
            "ExperimentalWamWindowFactory.cs",
            "ExperimentalWamWindowMessage.cs",
            "ExperimentalWamPipe.cs",
            "ExperimentalWamNativeDescriptor.cs",
            "WorkAccountProfilePhotoFetcher.cs",
            "WorkAccountProfilePresentation.cs",
            "WorkAccountProfileWindowChannel.cs",
            "WorkAccountProfileControlMessage.cs",
            "WorkAccountDifferentAccountMessage.cs",
            "WorkAccountPreferredAccountStore.cs",
            "WebMessageOrigin.cs",
        };

        var guid = new Regex(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        foreach (string file in wamFiles)
        {
            string path = Path.Combine(srcDir, file);
            Assert.True(File.Exists(path), $"expected WAM source file missing: {file}");
            string text = File.ReadAllText(path);
            Assert.False(guid.IsMatch(text),
                $"experimental WAM source '{file}' contains a hardcoded GUID literal (registration id must be runtime-injected).");
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task DisabledGate_ExposesNoUsableExperimentalProvider()
    {
        // Options disabled by default.
        Assert.False(ExperimentalWamOptions.Disabled.Enabled);
        Assert.False(ExperimentalWamOptions.Disabled.IsFullyConfigured);

        // Selection of the experimental provider fails closed (no Hello fallback).
        AuthProviderSelection s = AuthProviderSelector.Select("entra-wam", ExperimentalWamOptions.Disabled);
        Assert.True(s.IsFailClosed);

        // The default authenticator can never authenticate.
        var disabled = DisabledExperimentalWamAuthenticator.Instance;
        WamInteractiveResult r = await disabled
            .AuthenticateAsync(
                new WamAuthRequest(WamAuthPurpose.SessionUnlock, "t", "c", "User.Read"),
                IntPtr.Zero,
                CancellationToken.None);
        Assert.Equal(WamAcquireStatus.Disabled, r.Status);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void ReleaseGate_BlocksStableUntilAllGovernanceGatesMet()
    {
        Assert.True(ExperimentalWamReleaseGate.StableReleaseBlocked);
        Assert.All(ExperimentalWamReleaseGate.Gates, g => Assert.False(g.Satisfied));
        Assert.Contains("POL-1", ExperimentalWamReleaseGate.Pol1ThreatModel);
    }

    [Fact]
    public void ProfileControlHelper_RemainsExperimentalOnly()
    {
        string source = File.ReadAllText(
            Path.Combine(LocateAppSourceDir(), "WorkAccountProfileControlMessage.cs"));
        Assert.StartsWith("#if EXPERIMENTAL_WAM", source.TrimStart());
        Assert.EndsWith("#endif", source.TrimEnd());
    }

    // The bounded token / photo doctrine (Cycle 2) SUPERSEDES the former blanket
    // "the product never calls Graph / the token is never read / the product is
    // token-free" language. Because the bounded profile-photo path now exists,
    // NO active App source file may still assert any of those superseded blanket
    // claims. (The provider native-test mode's own, correctly mode-scoped "never
    // reads a token, and never calls Microsoft Graph" statement is TRUE — that
    // path injects no photo fetcher — and is deliberately NOT one of the
    // forbidden fragments below.)
    [Fact]
    public void ActiveAppSource_NoSupersededTokenFreeGraphClaim_WhilePhotoPathExists()
    {
        string srcDir = LocateAppSourceDir();

        // Guard: the bounded profile-photo path must actually exist in source, so
        // this doctrine invariant is only enforced while the photo call is real.
        string photoFetcher = File.ReadAllText(
            Path.Combine(srcDir, "WorkAccountProfilePhotoFetcher.cs"));
        Assert.Contains("/v1.0/me/photo/$value", photoFetcher);

        string[] forbiddenClaims =
        {
            "product never calls Graph",
            "product is token-free",
            "token is never read",
            "never queries Graph with the sign-in token",
        };

        foreach (string path in Directory.GetFiles(srcDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            string text = File.ReadAllText(path);
            foreach (string claim in forbiddenClaims)
            {
                Assert.False(
                    text.Contains(claim, StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(path)} still asserts the superseded claim '{claim}' " +
                    "even though the bounded profile-photo Graph path exists.");
            }
        }
    }

    // The bounded doctrine STRENGTHENS token-containment: the access token never
    // leaves the WAM-owning native process, and only the reduced neutral result
    // crosses to the daemon. Those true statements must remain present.
    [Fact]
    public void ActiveAppSource_RetainsTokenContainmentDoctrine()
    {
        string srcDir = LocateAppSourceDir();

        string authenticator = File.ReadAllText(
            Path.Combine(srcDir, "MsalEntraWamAuthenticator.cs"));
        Assert.Contains("NEVER leaves this process", authenticator);
        Assert.Contains("never used for audit", authenticator);

        string neutral = File.ReadAllText(Path.Combine(srcDir, "NeutralWamResult.cs"));
        Assert.Contains("never leave MSAL inside the window process", neutral);

        string bridge = File.ReadAllText(
            Path.Combine(srcDir, "ExperimentalWamWindowBridge.cs"));
        Assert.Contains("never leave this process", bridge);
    }

    private static string LocateAppSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
            {
                return Path.Combine(dir.FullName, "src", "PAXCookbook.App");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("could not locate repo root (PAXCookbook.sln) from test base directory.");
    }
}
