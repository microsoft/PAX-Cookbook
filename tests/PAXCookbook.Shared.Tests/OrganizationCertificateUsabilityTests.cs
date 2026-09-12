using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// Cycle-12 focused matrix for the PURE, BOUNDED certificate USABILITY evaluator.
//
// Every fixture here is SYNTHETIC FACTS ONLY, produced by fake key-probe
// adapters. Nothing in this file opens, enumerates, reads, imports, writes out,
// installs, or deletes a certificate, touches a key pair, signs, verifies with a
// key, enciphers, deciphers, reads ProgramData or the registry, contacts a
// tenant, Graph, a service, WAM/Hello, PAX, or a Bake.
public sealed class OrganizationCertificateUsabilityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private const string ClientAuthOid =
        OrganizationCertificateUsabilityContract.ClientAuthenticationPurposeOid;

    // Server Authentication and Code Signing: real OIDs that are NOT client auth.
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    private static ICertificateUsabilityClock Clock(DateTimeOffset? at = null)
        => new FixedCertificateUsabilityClock(at ?? Now);

    // ---- fake key-probe adapters (never a real key) --------------------------

    // Each adapter models one bounded outcome of the AVAILABILITY probe without a
    // key, a handle, or a certificate ever existing.
    private static CertificateKeyAvailability ProbeDeclaresNoKey()
        => CertificateKeyAvailability.Unavailable;

    private static CertificateKeyAvailability ProbeDeclaresKeyButAccessorYieldsNothing()
        => CertificateKeyAvailability.Unavailable;

    private static CertificateKeyAvailability ProbeAccessorThrew()
        => CertificateKeyAvailability.Unavailable;

    private static CertificateKeyAvailability ProbeSupportedKeyAvailable()
        => CertificateKeyAvailability.Available;

    private static CertificateKeyAvailability ProbeUndetermined()
        => CertificateKeyAvailability.Unknown;

    // ---- fact builders -------------------------------------------------------

    // A fact record whose every check passes, so a single overridden argument
    // isolates exactly one refusal.
    private static CertificateUsabilityFacts Facts(
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        CertificateKeyAlgorithmClass algorithm = CertificateKeyAlgorithmClass.Rsa,
        CertificateKeyAvailability? availability = null,
        bool purposePresent = true,
        string[]? purposeOids = null,
        bool purposeMalformed = false,
        bool keyUsagePresent = true,
        bool digitalSignature = true,
        bool keyUsageMalformed = false)
        => CertificateUsabilityFacts.Create(
            notValidBeforeUtc: notBefore ?? Now.AddDays(-30),
            notValidAfterUtc: notAfter ?? Now.AddDays(30),
            keyAlgorithm: algorithm,
            keyAvailability: availability ?? ProbeSupportedKeyAvailable(),
            purposeExtensionPresent: purposePresent,
            purposeOids: purposeOids ?? new[] { ClientAuthOid },
            purposeExtensionMalformed: purposeMalformed,
            keyUsageExtensionPresent: keyUsagePresent,
            digitalSignatureAllowed: digitalSignature,
            keyUsageExtensionMalformed: keyUsageMalformed);

    private static CertificateUsabilityState State(
        CertificateUsabilityFacts? facts,
        CertificateResolutionState resolution = CertificateResolutionState.ResolvedMetadataOnly,
        DateTimeOffset? at = null,
        ICertificateUsabilityClock? clock = null)
        => CertificateUsabilityEvaluator.Evaluate(resolution, facts, clock ?? Clock(at)).State;

    // ---- T18 (TEST-FIRST) ----------------------------------------------------

    [Fact] // matrix 20/21/22 (pure side) — a uniquely resolved certificate WITHOUT
           // an accessible supported private key is NOT usable, even though every
           // other bounded check passes.
    public void T18_UniquelyResolved_WithoutAccessiblePrivateKey_IsNotUsable()
    {
        CertificateUsabilityFacts facts = CertificateUsabilityFacts.Create(
            notValidBeforeUtc: Now.AddDays(-1),
            notValidAfterUtc: Now.AddDays(1),
            keyAlgorithm: CertificateKeyAlgorithmClass.Rsa,
            keyAvailability: CertificateKeyAvailability.Unavailable,
            purposeExtensionPresent: true,
            purposeOids: new[] { OrganizationCertificateUsabilityContract.ClientAuthenticationPurposeOid },
            keyUsageExtensionPresent: true,
            digitalSignatureAllowed: true);

        CertificateUsability usability = CertificateUsabilityEvaluator.Evaluate(
            CertificateResolutionState.ResolvedMetadataOnly, facts, Clock());

        Assert.Equal(CertificateUsabilityState.PrivateKeyUnavailable, usability.State);
        Assert.False(usability.Usable);
    }

    // ==== A. Skip states (usability is never evaluated) =======================

    [Fact] // matrix 1, 3, 4, 5, 6 — every non-unique resolution skips usability.
    public void U01_NonUniqueResolutionStates_SkipUsability()
    {
        CertificateResolutionState[] skipped =
        {
            CertificateResolutionState.ReferenceMissing,   // matrix 6 (schema-v1)
            CertificateResolutionState.NotFound,           // matrix 3
            CertificateResolutionState.Ambiguous,          // matrix 4
            CertificateResolutionState.EntryDisabled,      // matrix 5
            CertificateResolutionState.InventoryNotAuthorized,
            CertificateResolutionState.InventoryNotProvisioned,
            CertificateResolutionState.Invalid,
            CertificateResolutionState.Unknown,            // matrix 1
        };

        foreach (CertificateResolutionState resolution in skipped)
        {
            // Even PERFECT facts cannot promote a non-unique resolution.
            Assert.Equal(
                CertificateUsabilityState.NotResolved,
                State(Facts(), resolution));
            Assert.Equal(
                CertificateUsabilityState.NotResolved,
                State(null, resolution));
        }
    }

    [Fact] // matrix 2 — an unavailable catalog skips usability with its own state.
    public void U02_CatalogUnavailable_SkipsUsability()
    {
        Assert.Equal(
            CertificateUsabilityState.CatalogUnavailable,
            State(Facts(), CertificateResolutionState.CatalogUnavailable));
        Assert.Equal(
            CertificateUsabilityState.CatalogUnavailable,
            State(null, CertificateResolutionState.CatalogUnavailable));
    }

    [Fact] // Missing facts, a missing clock, and unreadable facts all fail closed.
    public void U03_MissingFactsOrClock_FailClosed()
    {
        Assert.Equal(
            CertificateUsabilityState.Unknown,
            State(null));
        Assert.Equal(
            CertificateUsabilityState.Unknown,
            CertificateUsabilityEvaluator.Evaluate(
                CertificateResolutionState.ResolvedMetadataOnly, Facts(), null).State);
        Assert.Equal(
            CertificateUsabilityState.Invalid,
            State(CertificateUsabilityFacts.ExtractionUnavailable()));
    }

    // ==== B. Validity window ==================================================

    [Fact] // matrix 7 — before the window opens.
    public void U07_NotYetValid()
    {
        Assert.Equal(
            CertificateUsabilityState.NotYetValid,
            State(Facts(notBefore: Now.AddMinutes(1), notAfter: Now.AddDays(30))));
    }

    [Fact] // matrix 8 — at or after the window closes.
    public void U08_Expired()
    {
        Assert.Equal(
            CertificateUsabilityState.Expired,
            State(Facts(notBefore: Now.AddDays(-30), notAfter: Now.AddMinutes(-1))));
    }

    [Fact] // matrix 9 — EXACTLY at NotBefore the certificate is valid.
    public void U09_ValidAtNotBefore()
    {
        Assert.Equal(
            CertificateUsabilityState.Usable,
            State(Facts(notBefore: Now, notAfter: Now.AddDays(30))));
    }

    [Fact] // matrix 10 — EXACTLY at NotAfter the certificate is expired.
    public void U10_InvalidAtNotAfter()
    {
        Assert.Equal(
            CertificateUsabilityState.Expired,
            State(Facts(notBefore: Now.AddDays(-30), notAfter: Now)));

        // An inverted or empty window is malformed, not merely expired.
        Assert.Equal(
            CertificateUsabilityState.Invalid,
            State(Facts(notBefore: Now.AddDays(1), notAfter: Now.AddDays(-1))));
    }

    [Fact] // The instant comes from the INJECTED clock, never the system clock.
    public void U11_ValidityUsesInjectedClockOnly()
    {
        CertificateUsabilityFacts facts =
            Facts(notBefore: Now.AddDays(-1), notAfter: Now.AddDays(1));

        Assert.Equal(CertificateUsabilityState.Usable, State(facts, at: Now));
        Assert.Equal(CertificateUsabilityState.NotYetValid, State(facts, at: Now.AddDays(-5)));
        Assert.Equal(CertificateUsabilityState.Expired, State(facts, at: Now.AddDays(5)));

        // A non-UTC instant is normalized rather than mis-compared.
        Assert.Equal(
            CertificateUsabilityState.Usable,
            State(facts, clock: new FixedCertificateUsabilityClock(
                new DateTimeOffset(2026, 7, 1, 7, 0, 0, TimeSpan.FromHours(-5)))));
    }

    // ==== C. Purpose (Extended Key Usage) =====================================

    [Fact] // matrix 11 — the client-auth purpose is accepted, alone or alongside others.
    public void U12_ClientAuthPurposePresent_Accepted()
    {
        Assert.Equal(
            CertificateUsabilityState.Usable,
            State(Facts(purposeOids: new[] { ClientAuthOid })));
        Assert.Equal(
            CertificateUsabilityState.Usable,
            State(Facts(purposeOids: new[] { ServerAuthOid, ClientAuthOid, CodeSigningOid })));
    }

    [Fact] // matrix 12 — an ABSENT purpose extension REFUSES (deliberately unlike RFC 5280).
    public void U13_PurposeExtensionAbsent_FailsClosed()
    {
        Assert.Equal(
            CertificateUsabilityState.ClientAuthNotAllowed,
            State(Facts(purposePresent: false, purposeOids: Array.Empty<string>())));

        // Present-but-empty is the same refusal.
        Assert.Equal(
            CertificateUsabilityState.ClientAuthNotAllowed,
            State(Facts(purposePresent: true, purposeOids: Array.Empty<string>())));
    }

    [Fact] // matrix 13 — unrelated purposes only.
    public void U14_UnrelatedPurposesOnly_FailsClosed()
    {
        Assert.Equal(
            CertificateUsabilityState.ClientAuthNotAllowed,
            State(Facts(purposeOids: new[] { ServerAuthOid, CodeSigningOid })));

        // A near-miss OID is not a match.
        Assert.Equal(
            CertificateUsabilityState.ClientAuthNotAllowed,
            State(Facts(purposeOids: new[] { ClientAuthOid + ".1" })));
    }

    [Fact] // matrix 14 — malformed or DUPLICATED purpose extension is Invalid.
    public void U15_MalformedOrDuplicatePurposeExtension_Invalid()
    {
        Assert.Equal(
            CertificateUsabilityState.Invalid,
            State(Facts(purposeMalformed: true)));

        // Above the bound the list is refused rather than truncated.
        string[] tooMany = Enumerable
            .Range(0, OrganizationCertificateUsabilityContract.MaxPurposeOids + 1)
            .Select(i => ClientAuthOid)
            .ToArray();
        Assert.Equal(
            CertificateUsabilityState.Invalid,
            State(Facts(purposeOids: tooMany)));
    }

    // ==== D. Key usage ========================================================

    [Fact] // matrix 15 — Digital Signature present is accepted.
    public void U16_DigitalSignaturePresent_Accepted()
    {
        Assert.Equal(
            CertificateUsabilityState.Usable,
            State(Facts(keyUsagePresent: true, digitalSignature: true)));
    }

    [Fact] // matrix 16 — Key Usage present but Digital Signature prohibited.
    public void U17_DigitalSignatureProhibited_FailsClosed()
    {
        Assert.Equal(
            CertificateUsabilityState.DigitalSignatureNotAllowed,
            State(Facts(keyUsagePresent: true, digitalSignature: false)));
    }

    [Fact] // matrix 17 — an ABSENT Key Usage extension REFUSES (deliberately unlike RFC 5280).
    public void U18_KeyUsageExtensionAbsent_FailsClosed()
    {
        Assert.Equal(
            CertificateUsabilityState.DigitalSignatureNotAllowed,
            State(Facts(keyUsagePresent: false, digitalSignature: false)));

        // A malformed or duplicated Key Usage extension is Invalid, not merely
        // not-allowed.
        Assert.Equal(
            CertificateUsabilityState.Invalid,
            State(Facts(keyUsageMalformed: true)));
    }

    // ==== E. Algorithm and private-key availability ===========================

    [Fact] // matrix 18/19 — an available RSA or ECDSA key completes the checks.
    public void U19_SupportedAlgorithmWithAvailableKey_IsUsable()
    {
        foreach (CertificateKeyAlgorithmClass algorithm in new[]
                 {
                     CertificateKeyAlgorithmClass.Rsa,
                     CertificateKeyAlgorithmClass.Ecdsa,
                 })
        {
            Assert.Equal(
                CertificateUsabilityState.Usable,
                State(Facts(algorithm: algorithm, availability: ProbeSupportedKeyAvailable())));
        }
    }

    [Fact] // matrix 20/21/22 — every unavailable-key adapter refuses identically.
    public void U20_EveryUnavailableKeyOutcome_FailsClosed()
    {
        foreach (CertificateKeyAvailability availability in new[]
                 {
                     ProbeDeclaresNoKey(),                          // matrix 20
                     ProbeDeclaresKeyButAccessorYieldsNothing(),     // matrix 21
                     ProbeAccessorThrew(),                           // matrix 22
                     ProbeUndetermined(),
                 })
        {
            Assert.Equal(
                CertificateUsabilityState.PrivateKeyUnavailable,
                State(Facts(availability: availability)));
        }
    }

    [Fact] // matrix 23 — an unsupported algorithm is reported as such, NOT as a
           // missing key, even when no key is available either.
    public void U21_UnsupportedAlgorithm_ReportedBeforeKeyProbe()
    {
        foreach (CertificateKeyAlgorithmClass algorithm in new[]
                 {
                     CertificateKeyAlgorithmClass.Unsupported,
                     CertificateKeyAlgorithmClass.Unknown,
                 })
        {
            Assert.Equal(
                CertificateUsabilityState.UnsupportedKeyAlgorithm,
                State(Facts(algorithm: algorithm, availability: ProbeDeclaresNoKey())));
        }
    }

    // ==== F. Order, single usable result, and capabilities ====================

    [Fact] // matrix 24 — exactly one bounded state, and the fixed first-match order.
    public void U22_EvaluationOrder_IsFixedAndFirstMatchWins()
    {
        // One fully-passing record yields exactly one Usable result.
        CertificateUsability usable = CertificateUsabilityEvaluator.Evaluate(
            CertificateResolutionState.ResolvedMetadataOnly, Facts(), Clock());
        Assert.Equal(CertificateUsabilityState.Usable, usable.State);
        Assert.Equal("usable", usable.WireState);
        Assert.True(usable.Usable);

        // Validity outranks purpose, purpose outranks key usage, key usage
        // outranks algorithm, algorithm outranks the key probe.
        Assert.Equal(
            CertificateUsabilityState.Expired,
            State(Facts(notAfter: Now.AddMinutes(-1), purposePresent: false,
                        keyUsagePresent: false, algorithm: CertificateKeyAlgorithmClass.Unsupported,
                        availability: ProbeDeclaresNoKey())));
        Assert.Equal(
            CertificateUsabilityState.ClientAuthNotAllowed,
            State(Facts(purposePresent: false, keyUsagePresent: false,
                        algorithm: CertificateKeyAlgorithmClass.Unsupported,
                        availability: ProbeDeclaresNoKey())));
        Assert.Equal(
            CertificateUsabilityState.DigitalSignatureNotAllowed,
            State(Facts(keyUsagePresent: false, algorithm: CertificateKeyAlgorithmClass.Unsupported,
                        availability: ProbeDeclaresNoKey())));
        Assert.Equal(
            CertificateUsabilityState.UnsupportedKeyAlgorithm,
            State(Facts(algorithm: CertificateKeyAlgorithmClass.Unsupported,
                        availability: ProbeDeclaresNoKey())));
    }

    [Fact] // matrix 29, 30, 31 — a USABLE result still grants nothing downstream.
    public void U23_UsableGrantsNoDownstreamCapability()
    {
        CertificateUsability usable = CertificateUsabilityEvaluator.Evaluate(
            CertificateResolutionState.ResolvedMetadataOnly, Facts(), Clock());

        Assert.True(usable.Usable);
        Assert.False(usable.RecipeBound);      // matrix 29
        Assert.False(usable.BakeAuthorized);   // matrix 30
        Assert.False(usable.ServiceReady);     // matrix 31
        Assert.True(usable.ReadOnly);
        Assert.True(usable.KeyNeverUsed);

        // The capability markers are constant across EVERY bounded state.
        foreach (CertificateUsabilityState state in Enum.GetValues<CertificateUsabilityState>())
        {
            CertificateUsability result = Build(state);
            Assert.False(result.RecipeBound);
            Assert.False(result.BakeAuthorized);
            Assert.False(result.ServiceReady);
            Assert.True(result.ReadOnly);
            Assert.True(result.KeyNeverUsed);
            Assert.Equal(state == CertificateUsabilityState.Usable, result.Usable);
        }
    }

    [Fact] // The state set is CLOSED at eleven, each with a distinct wire token,
           // and ToString is content-free.
    public void U24_StateSet_IsClosedAndContentFree()
    {
        CertificateUsabilityState[] states = Enum.GetValues<CertificateUsabilityState>();
        Assert.Equal(11, states.Length);

        var tokens = new List<string>();
        foreach (CertificateUsabilityState state in states)
        {
            CertificateUsability result = Build(state);
            Assert.False(string.IsNullOrWhiteSpace(result.WireState));
            Assert.Equal(result.WireState, result.WireState.ToLowerInvariant());
            Assert.DoesNotContain(" ", result.WireState);
            tokens.Add(result.WireState);
            Assert.Equal($"CertificateUsability[state={result.WireState}]", result.ToString());
        }
        Assert.Equal(tokens.Count, tokens.Distinct().Count());

        // Facts never render their content either.
        Assert.Equal("CertificateUsabilityFacts[bounded]", Facts().ToString());
    }

    // ==== G. Boundary containment ============================================

    [Fact] // matrix 27 — no key, handle, byte array, or certificate type can cross
           // the evaluator boundary, in either direction.
    public void U25_NoKeyObject_CrossesTheEvaluatorBoundary()
    {
        foreach (Type type in new[] { typeof(CertificateUsabilityFacts), typeof(CertificateUsability) })
        {
            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.NotEqual(typeof(byte[]), property.PropertyType);
                Assert.NotEqual(typeof(IntPtr), property.PropertyType);
                foreach (string banned in new[] { "X509", "RSA", "ECDsa", "Asymmetric", "SafeHandle" })
                {
                    Assert.DoesNotContain(banned, property.PropertyType.Name, StringComparison.Ordinal);
                }
            }
        }

        // The evaluator itself accepts only bounded facts, a resolution state, and
        // a clock.
        MethodInfo evaluate = typeof(CertificateUsabilityEvaluator)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == "Evaluate");
        Assert.Equal(
            new[]
            {
                typeof(CertificateResolutionState),
                typeof(CertificateUsabilityFacts),
                typeof(ICertificateUsabilityClock),
            },
            evaluate.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(CertificateUsability), evaluate.ReturnType);
    }

    [Fact] // matrix 28 — the Shared contract references no key-using, mutating, or
           // platform API at all, and imports no certificate/Win32/network/
           // filesystem namespace.
    public void U26_SharedContract_UsesNoKeyOrPlatformApi()
    {
        string code = StripCommentsAndStrings(ReadSource(
            "src/PAXCookbook.Shared/Contracts/OrganizationCertificateUsabilityContract.cs"));

        foreach (string banned in new[]
                 {
                     "SignData", "SignHash", "VerifyData", "VerifyHash",
                     "Encrypt", "Decrypt", "Export", "Import",
                     "GetRSAPrivateKey", "GetECDsaPrivateKey", "GetDSAPrivateKey",
                     "CopyWithPrivateKey", "HasPrivateKey", "CspParameters",
                     "KeyContainer", "X509", "X509Chain", "X509Store",
                     "Thumbprint", "Subject", "Issuer", "SerialNumber", "FriendlyName",
                     "Registry", "HttpClient", "HttpContext", "File.", "Directory.",
                     "GraphServiceClient", "AcquireToken", "PublicClientApplication",
                     "Microsoft.Identity", "WebAuthn", "ServiceController", "Process.Start",
                     "DateTime.Now", "DateTimeOffset.Now",
                 })
        {
            Assert.DoesNotContain(banned, code, StringComparison.Ordinal);
        }

        // The ONLY imports are System and System.Collections.Generic.
        string[] usings = Regex
            .Matches(code, @"using\s+([A-Za-z0-9_.]+)\s*;")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.Equal(new[] { "System", "System.Collections.Generic" }, usings);

        // The production clock is the ONLY place a real instant is read, and it
        // reads UTC.
        Assert.Contains("DateTimeOffset.UtcNow", code, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------

    // Builds one result per bounded state by driving the evaluator, so no test
    // fabricates a state the evaluator cannot actually produce.
    private static CertificateUsability Build(CertificateUsabilityState state) => state switch
    {
        CertificateUsabilityState.NotResolved => CertificateUsabilityEvaluator.Evaluate(
            CertificateResolutionState.NotFound, Facts(), Clock()),
        CertificateUsabilityState.CatalogUnavailable => CertificateUsabilityEvaluator.Evaluate(
            CertificateResolutionState.CatalogUnavailable, Facts(), Clock()),
        CertificateUsabilityState.NotYetValid => Evaluate(Facts(notBefore: Now.AddDays(1))),
        CertificateUsabilityState.Expired => Evaluate(Facts(notAfter: Now.AddDays(-1))),
        CertificateUsabilityState.ClientAuthNotAllowed => Evaluate(Facts(purposePresent: false)),
        CertificateUsabilityState.DigitalSignatureNotAllowed => Evaluate(Facts(keyUsagePresent: false)),
        CertificateUsabilityState.PrivateKeyUnavailable => Evaluate(
            Facts(availability: ProbeDeclaresNoKey())),
        CertificateUsabilityState.UnsupportedKeyAlgorithm => Evaluate(
            Facts(algorithm: CertificateKeyAlgorithmClass.Unsupported)),
        CertificateUsabilityState.Usable => Evaluate(Facts()),
        CertificateUsabilityState.Invalid => Evaluate(CertificateUsabilityFacts.ExtractionUnavailable()),
        _ => CertificateUsabilityEvaluator.Evaluate(
            CertificateResolutionState.ResolvedMetadataOnly, null, Clock()),
    };

    private static CertificateUsability Evaluate(CertificateUsabilityFacts facts)
        => CertificateUsabilityEvaluator.Evaluate(
            CertificateResolutionState.ResolvedMetadataOnly, facts, Clock());

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Strip block comments, line comments, AND string/char literals so descriptive
    // doctrine comments cannot false-positive a CODE-pattern scan.
    private static string StripCommentsAndStrings(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//[^\r\n]*", " ");
        src = Regex.Replace(src, "@\"(?:[^\"]|\"\")*\"", " ");
        src = Regex.Replace(src, "\"(?:\\\\.|[^\"\\\\])*\"", " ");
        src = Regex.Replace(src, "'(?:\\\\.|[^'\\\\])*'", " ");
        return src;
    }
}

