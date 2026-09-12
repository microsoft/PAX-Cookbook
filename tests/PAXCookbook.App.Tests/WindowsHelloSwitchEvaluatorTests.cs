using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Batch 1b — Work-account -> Windows Hello switch four-state model. Deterministic;
// no live WAM/tenant/MSAL/Hello dependency. Proves the TWO SEPARATE predicates
// (platform availability vs. local PAX registration integrity) drive exactly the
// four bounded decisions, that malformed local state fails closed toward repair
// (never silently re-enrolled or coerced to "not registered"), and that platform
// availability is evaluated first (no enrollment on an unproven platform).
public sealed class WindowsHelloSwitchEvaluatorTests
{
    // ---- pure decision: the four states -------------------------------------

    [Fact]
    public void Available_Valid_IsSwitchReady()
    {
        Assert.Equal(
            WindowsHelloSwitchDecision.SwitchReady,
            WindowsHelloSwitchEvaluator.Evaluate(true, HelloLocalRegistration.Valid));
    }

    [Fact]
    public void Available_NotRegistered_IsEnrollmentRequired()
    {
        Assert.Equal(
            WindowsHelloSwitchDecision.EnrollmentRequired,
            WindowsHelloSwitchEvaluator.Evaluate(true, HelloLocalRegistration.NotRegistered));
    }

    [Fact]
    public void Unavailable_AnyRegistration_IsPlatformUnavailable_NoEnrollment()
    {
        // Platform availability is evaluated FIRST: even a valid or malformed
        // local record cannot enroll or switch on an unavailable platform.
        Assert.Equal(
            WindowsHelloSwitchDecision.PlatformUnavailable,
            WindowsHelloSwitchEvaluator.Evaluate(false, HelloLocalRegistration.Valid));
        Assert.Equal(
            WindowsHelloSwitchDecision.PlatformUnavailable,
            WindowsHelloSwitchEvaluator.Evaluate(false, HelloLocalRegistration.NotRegistered));
        Assert.Equal(
            WindowsHelloSwitchDecision.PlatformUnavailable,
            WindowsHelloSwitchEvaluator.Evaluate(false, HelloLocalRegistration.Malformed));
    }

    [Fact]
    public void Available_Malformed_IsRegistrationRepairRequired()
    {
        Assert.Equal(
            WindowsHelloSwitchDecision.RegistrationRepairRequired,
            WindowsHelloSwitchEvaluator.Evaluate(true, HelloLocalRegistration.Malformed));
    }

    // ---- bounded, distinct reason identifiers (replacing windows_hello_unavailable)

    [Fact]
    public void ReasonIdentifiers_AreExplicitAndDistinct()
    {
        Assert.Equal("windows_hello_enrollment_required",
            WindowsHelloSwitchEvaluator.ReasonFor(WindowsHelloSwitchDecision.EnrollmentRequired));
        Assert.Equal("windows_hello_platform_unavailable",
            WindowsHelloSwitchEvaluator.ReasonFor(WindowsHelloSwitchDecision.PlatformUnavailable));
        Assert.Equal("windows_hello_registration_repair_required",
            WindowsHelloSwitchEvaluator.ReasonFor(WindowsHelloSwitchDecision.RegistrationRepairRequired));
    }

    [Fact]
    public void ReasonIdentifiers_DoNotReuseStaleWindowsHelloUnavailable()
    {
        Assert.NotEqual("windows_hello_unavailable",
            WindowsHelloSwitchEvaluator.ReasonFor(WindowsHelloSwitchDecision.PlatformUnavailable));
        Assert.NotEqual("windows_hello_unavailable",
            WindowsHelloSwitchEvaluator.ReasonFor(WindowsHelloSwitchDecision.EnrollmentRequired));
        Assert.NotEqual("windows_hello_unavailable",
            WindowsHelloSwitchEvaluator.ReasonFor(WindowsHelloSwitchDecision.RegistrationRepairRequired));
    }

    // ---- local registration integrity (WebAuthnService.EvaluateLocalRegistration)

    private static string NewWorkspace()
    {
        string path = Path.Combine(Path.GetTempPath(), "paxhello_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CredentialsFile(string workspace)
        => Path.Combine(workspace, "Auth", "webauthn-credentials.json");

    private static string ValidSpkiBase64()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static void WriteCredentials(string workspace, string json)
    {
        string file = CredentialsFile(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
    }

    [Fact]
    public void EvaluateLocalRegistration_NoFile_IsNotRegistered()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            Assert.Equal(HelloLocalRegistration.NotRegistered, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void EvaluateLocalRegistration_EmptyCredentialList_IsNotRegistered()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            WriteCredentials(ws, "{ \"schemaVersion\": 1, \"credentials\": [] }");
            Assert.Equal(HelloLocalRegistration.NotRegistered, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void EvaluateLocalRegistration_EmptyFile_IsNotRegistered()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            WriteCredentials(ws, "   ");
            Assert.Equal(HelloLocalRegistration.NotRegistered, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void EvaluateLocalRegistration_WellFormedCredential_IsValid()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            string spki = ValidSpkiBase64();
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                credentials = new[]
                {
                    new { credentialId = "cred-1", publicKeySpkiBase64 = spki, alg = -7, createdUtc = "2026-07-21T00:00:00Z", lastUsedUtc = "2026-07-21T00:00:00Z", signCount = 0 },
                },
            });
            WriteCredentials(ws, json);
            Assert.Equal(HelloLocalRegistration.Valid, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void EvaluateLocalRegistration_CorruptJson_IsMalformed_NotNotRegistered()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            WriteCredentials(ws, "{ not valid json");
            // Must NOT be silently coerced to NotRegistered (which would silently
            // re-enroll over corruption) — it must fail closed to Malformed.
            Assert.Equal(HelloLocalRegistration.Malformed, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void EvaluateLocalRegistration_MissingPublicKey_IsMalformed()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                credentials = new[]
                {
                    new { credentialId = "cred-1", publicKeySpkiBase64 = "", alg = -7 },
                },
            });
            WriteCredentials(ws, json);
            Assert.Equal(HelloLocalRegistration.Malformed, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void EvaluateLocalRegistration_UnsupportedAlg_IsMalformed()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            string spki = ValidSpkiBase64();
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                credentials = new[]
                {
                    new { credentialId = "cred-1", publicKeySpkiBase64 = spki, alg = -257 },
                },
            });
            WriteCredentials(ws, json);
            Assert.Equal(HelloLocalRegistration.Malformed, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void EvaluateLocalRegistration_WrongSchema_IsMalformed()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            string spki = ValidSpkiBase64();
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 99,
                credentials = new[]
                {
                    new { credentialId = "cred-1", publicKeySpkiBase64 = spki, alg = -7 },
                },
            });
            WriteCredentials(ws, json);
            Assert.Equal(HelloLocalRegistration.Malformed, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void EvaluateLocalRegistration_NonImportableSpki_IsMalformed()
    {
        string ws = NewWorkspace();
        try
        {
            var svc = new WebAuthnService(ws, 51234);
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                credentials = new[]
                {
                    new { credentialId = "cred-1", publicKeySpkiBase64 = "not-base64-!!!", alg = -7 },
                },
            });
            WriteCredentials(ws, json);
            Assert.Equal(HelloLocalRegistration.Malformed, svc.EvaluateLocalRegistration());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }
}
