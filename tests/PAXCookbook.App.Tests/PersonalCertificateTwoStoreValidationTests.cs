using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 37 — the desktop Chef's Key Test must truthfully resolve a personal
// certificate reference across BOTH fixed Personal stores and fail closed on
// ambiguity or incomplete enumeration.
//
// Design under test (ruling R1): a PURE resolver with ZERO I/O carries the whole
// decision table, and a FIXED production shim performs exactly one Current User >
// Personal query and exactly one Local Machine > Personal query with no
// caller-supplied location, store, delegate, enumerator or override. Every
// synthetic case below therefore runs WITHOUT touching a real certificate store,
// the Windows Credential Manager, the registry, the network, PAX or a Bake.
//
// Every negative source scan is paired with a positive control, so a scan that
// silently read the wrong text (or an empty string) cannot pass.
public sealed class PersonalCertificateTwoStoreValidationTests
{
    private const string UniqueCurrentUserCopy =
        "A certificate with this thumbprint was found in Current User > Personal.";

    private const string UniqueLocalMachineCopy =
        "A certificate with this thumbprint was found in Local Machine > Personal. " +
        "This confirms the certificate reference only; background service access has not been configured or verified.";

    private const string NotFoundCopy =
        "No certificate with this thumbprint was found in Current User > Personal or Local Machine > Personal.";

    private const string AmbiguousCopy =
        "More than one certificate with this thumbprint was found. Remove the duplicate certificate reference before using this Chef's Key.";

    private const string UnavailableCopy =
        "PAX Cookbook could not safely check both Personal certificate stores.";

    private const string InvalidCopy =
        "The certificate thumbprint is not a valid certificate reference.";

    private const string SampleThumbprint = "11112222333344445555666677778888AAAABBBB";

    private static readonly ChefKeyModel.PersonalCertificateStoreMatchCount Zero =
        ChefKeyModel.PersonalCertificateStoreMatchCount.Zero;

    private static readonly ChefKeyModel.PersonalCertificateStoreMatchCount One =
        ChefKeyModel.PersonalCertificateStoreMatchCount.One;

    private static readonly ChefKeyModel.PersonalCertificateStoreMatchCount Many =
        ChefKeyModel.PersonalCertificateStoreMatchCount.Many;

    private static readonly ChefKeyModel.PersonalCertificateResolution[] PassingStates =
    {
        ChefKeyModel.PersonalCertificateResolution.UniqueCurrentUser,
        ChefKeyModel.PersonalCertificateResolution.UniqueLocalMachine,
    };

    // ---- helpers -------------------------------------------------------------

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // tests/PAXCookbook.App.Tests/<file>  ->  repo root is two levels up.
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private const string ChefKeyModelRel = "src/PAXCookbook.App/ChefKeyModel.cs";

    private static string ChefKeyModelRaw => ReadSource(ChefKeyModelRel);

    // Strip block comments, line comments and string/char literals so descriptive
    // comments and customer copy cannot false-positive a CODE-pattern scan.
    private static string StripCommentsAndStrings(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//[^\r\n]*", " ");
        src = Regex.Replace(src, "@\"(?:[^\"]|\"\")*\"", " ");
        src = Regex.Replace(src, "\"(?:\\\\.|[^\"\\\\])*\"", " ");
        src = Regex.Replace(src, "'(?:\\\\.|[^'\\\\])*'", " ");
        return src;
    }

    private static string ChefKeyModelCode => StripCommentsAndStrings(ChefKeyModelRaw);

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // Extract a method by unique declaration anchor + brace balance.
    private static string MethodBody(string src, string anchor)
    {
        Assert.Equal(1, Occurrences(src, anchor));
        int start = src.IndexOf(anchor, StringComparison.Ordinal);
        int open = src.IndexOf('{', start);
        Assert.True(open > 0, "no body for anchor: " + anchor);
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') { depth++; }
            else if (src[i] == '}')
            {
                depth--;
                if (depth == 0) { return src.Substring(start, i - start + 1); }
            }
        }
        throw new InvalidOperationException("unbalanced body for anchor: " + anchor);
    }

    private static Dictionary<string, object?> CertificateMeta(string? thumbprint)
    {
        var meta = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["authType"] = ChefKeyModel.AuthAppRegCertificate,
            ["displayName"] = "Contoso App Registration",
            ["tenantId"] = "contoso-tenant",
            ["clientId"] = "contoso-client",
        };
        if (thumbprint is not null) { meta["certThumbprint"] = thumbprint; }
        return meta;
    }

    private static JsonElement Wire(
        string authType,
        Dictionary<string, object?> meta,
        bool hasSecret,
        ChefKeyModel.PersonalCertificateResolution resolution)
    {
        (int status, object body) = ChefKeyModel.ProjectTestResponse(authType, meta, hasSecret, resolution);
        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return doc.RootElement.Clone();
    }

    private static JsonElement CertificateWire(ChefKeyModel.PersonalCertificateResolution resolution)
        => Wire(ChefKeyModel.AuthAppRegCertificate, CertificateMeta(SampleThumbprint), hasSecret: false, resolution);

    private static string[] CheckNames(JsonElement root)
        => root.GetProperty("checks").EnumerateArray()
               .Select(c => c.GetProperty("name").GetString()!)
               .ToArray();

    private static (bool Ok, string Detail) CertInStore(JsonElement root)
    {
        JsonElement check = root.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "cert_in_store");
        return (check.GetProperty("ok").GetBoolean(), check.GetProperty("detail").GetString()!);
    }

    // =========================================================================
    // A. PURE RESOLVER DECISION TABLE (synthetic; no real certificate store).
    // =========================================================================

    [Fact] // A01 — an invalid reference resolves WITHOUT consulting either store.
    public void A01_InvalidReference_NeitherStoreIsConsulted()
    {
        // Both stores are declared unavailable AND stuffed with many matches. If
        // either store input were consulted, the answer could not still be invalid.
        Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Invalid,
            ChefKeyModel.ResolvePersonalCertificateState(
                referenceValid: false,
                currentUserAvailable: false, currentUserMatches: Many,
                localMachineAvailable: false, localMachineMatches: Many));

        Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Invalid,
            ChefKeyModel.ResolvePersonalCertificateState(
                referenceValid: false,
                currentUserAvailable: true, currentUserMatches: One,
                localMachineAvailable: true, localMachineMatches: Zero));
    }

    [Fact] // A02 — no matches anywhere.
    public void A02_ZeroMatchesInBothStores_IsNotFound()
        => Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.NotFound,
            ChefKeyModel.ResolvePersonalCertificateState(true, true, Zero, true, Zero));

    [Fact] // A03 — exactly one Current User match.
    public void A03_OneCurrentUserZeroLocalMachine_IsUniqueCurrentUser()
        => Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.UniqueCurrentUser,
            ChefKeyModel.ResolvePersonalCertificateState(true, true, One, true, Zero));

    [Fact] // A04 — exactly one Local Machine match (the cycle-36 gap).
    public void A04_ZeroCurrentUserOneLocalMachine_IsUniqueLocalMachine()
        => Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.UniqueLocalMachine,
            ChefKeyModel.ResolvePersonalCertificateState(true, true, Zero, true, One));

    [Fact] // A05 — duplicates inside Current User.
    public void A05_TwoCurrentUserMatches_IsAmbiguous()
        => Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Ambiguous,
            ChefKeyModel.ResolvePersonalCertificateState(true, true, Many, true, Zero));

    [Fact] // A06 — duplicates inside Local Machine.
    public void A06_TwoLocalMachineMatches_IsAmbiguous()
        => Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Ambiguous,
            ChefKeyModel.ResolvePersonalCertificateState(true, true, Zero, true, Many));

    [Fact] // A07 — one in each store is still ambiguous; neither store is preferred.
    public void A07_OneInEachStore_IsAmbiguous()
        => Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Ambiguous,
            ChefKeyModel.ResolvePersonalCertificateState(true, true, One, true, One));

    [Fact] // A08 — strict whole-check availability: Current User unreadable.
    public void A08_CurrentUserUnavailable_IsUnavailableEvenWithOneMachineMatch()
    {
        Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Unavailable,
            ChefKeyModel.ResolvePersonalCertificateState(true, false, Zero, true, One));

        // ... and never silently downgrades to notFound.
        Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Unavailable,
            ChefKeyModel.ResolvePersonalCertificateState(true, false, Zero, true, Zero));
    }

    [Fact] // A09 — strict whole-check availability: Local Machine unreadable.
    public void A09_LocalMachineUnavailable_IsUnavailableEvenWithOneUserMatch()
    {
        Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Unavailable,
            ChefKeyModel.ResolvePersonalCertificateState(true, true, One, false, Zero));

        Assert.Equal(
            ChefKeyModel.PersonalCertificateResolution.Unavailable,
            ChefKeyModel.ResolvePersonalCertificateState(true, false, Zero, false, Zero));
    }

    [Fact] // A10 — saturation never truncates `many` into `one`.
    public void A10_Saturation_ManyNeverCollapsesToOne()
    {
        Assert.Equal(Zero, ChefKeyModel.SaturateMatchCount(0));
        Assert.Equal(One, ChefKeyModel.SaturateMatchCount(1));

        foreach (int n in new[] { 2, 3, 4, 7, 16, 64, 1024, 65536, int.MaxValue })
        {
            Assert.Equal(Many, ChefKeyModel.SaturateMatchCount(n));

            // A saturated `many` must still resolve to ambiguous, never unique.
            Assert.Equal(
                ChefKeyModel.PersonalCertificateResolution.Ambiguous,
                ChefKeyModel.ResolvePersonalCertificateState(
                    true, true, ChefKeyModel.SaturateMatchCount(n), true, Zero));
            Assert.Equal(
                ChefKeyModel.PersonalCertificateResolution.Ambiguous,
                ChefKeyModel.ResolvePersonalCertificateState(
                    true, true, Zero, true, ChefKeyModel.SaturateMatchCount(n)));
        }

        // A defensive non-positive count is a zero, never a match.
        Assert.Equal(Zero, ChefKeyModel.SaturateMatchCount(-1));
        Assert.Equal(Zero, ChefKeyModel.SaturateMatchCount(int.MinValue));
    }

    [Fact] // A11 — an unknown / unmapped match-count value cannot produce success.
    public void A11_UnknownMatchCountEnum_FailsClosed()
    {
        var bogus = (ChefKeyModel.PersonalCertificateStoreMatchCount)99;

        foreach (ChefKeyModel.PersonalCertificateStoreMatchCount other in new[] { Zero, One, Many })
        {
            ChefKeyModel.PersonalCertificateResolution left =
                ChefKeyModel.ResolvePersonalCertificateState(true, true, bogus, true, other);
            ChefKeyModel.PersonalCertificateResolution right =
                ChefKeyModel.ResolvePersonalCertificateState(true, true, other, true, bogus);

            Assert.Equal(ChefKeyModel.PersonalCertificateResolution.Unavailable, left);
            Assert.Equal(ChefKeyModel.PersonalCertificateResolution.Unavailable, right);
            Assert.DoesNotContain(left, PassingStates);
            Assert.DoesNotContain(right, PassingStates);
        }
    }

    [Fact] // A12 — the resolver is total and closed, and only two inputs can pass.
    public void A12_ExhaustiveDecisionTable_IsClosedAndFailsClosed()
    {
        var counts = new[] { Zero, One, Many };
        var declared = Enum.GetValues<ChefKeyModel.PersonalCertificateResolution>().ToHashSet();
        int passing = 0, total = 0;

        foreach (bool valid in new[] { true, false })
        {
            foreach (bool cuAvailable in new[] { true, false })
            {
                foreach (bool lmAvailable in new[] { true, false })
                {
                    foreach (ChefKeyModel.PersonalCertificateStoreMatchCount cu in counts)
                    {
                        foreach (ChefKeyModel.PersonalCertificateStoreMatchCount lm in counts)
                        {
                            total++;
                            ChefKeyModel.PersonalCertificateResolution state =
                                ChefKeyModel.ResolvePersonalCertificateState(valid, cuAvailable, cu, lmAvailable, lm);

                            Assert.Contains(state, declared);

                            bool expectedPass = valid && cuAvailable && lmAvailable &&
                                ((cu == One && lm == Zero) || (cu == Zero && lm == One));
                            Assert.Equal(expectedPass, PassingStates.Contains(state));
                            if (expectedPass) { passing++; }
                        }
                    }
                }
            }
        }

        Assert.Equal(72, total);
        Assert.Equal(2, passing); // exactly the two unique-match inputs
    }

    // =========================================================================
    // B. cert_in_store PROJECTION AND FULL RESPONSE SHAPE.
    // =========================================================================

    [Fact] // B01 — exact customer copy for every closed state.
    public void B01_ProjectCertInStoreCheck_ExactCopyPerState()
    {
        Assert.Equal((true, UniqueCurrentUserCopy),
            ChefKeyModel.ProjectCertInStoreCheck(ChefKeyModel.PersonalCertificateResolution.UniqueCurrentUser));
        Assert.Equal((true, UniqueLocalMachineCopy),
            ChefKeyModel.ProjectCertInStoreCheck(ChefKeyModel.PersonalCertificateResolution.UniqueLocalMachine));
        Assert.Equal((false, NotFoundCopy),
            ChefKeyModel.ProjectCertInStoreCheck(ChefKeyModel.PersonalCertificateResolution.NotFound));
        Assert.Equal((false, AmbiguousCopy),
            ChefKeyModel.ProjectCertInStoreCheck(ChefKeyModel.PersonalCertificateResolution.Ambiguous));
        Assert.Equal((false, UnavailableCopy),
            ChefKeyModel.ProjectCertInStoreCheck(ChefKeyModel.PersonalCertificateResolution.Unavailable));
        Assert.Equal((false, InvalidCopy),
            ChefKeyModel.ProjectCertInStoreCheck(ChefKeyModel.PersonalCertificateResolution.Invalid));
    }

    [Fact] // B02 — only the two unique states pass, and no diagnostic text escapes.
    public void B02_OnlyUniqueStatesPass_AndDetailIsAlwaysAFixedLiteral()
    {
        string[] allowed =
        {
            UniqueCurrentUserCopy, UniqueLocalMachineCopy, NotFoundCopy,
            AmbiguousCopy, UnavailableCopy, InvalidCopy,
        };

        var states = Enum.GetValues<ChefKeyModel.PersonalCertificateResolution>().ToList();
        states.Add((ChefKeyModel.PersonalCertificateResolution)999); // unmapped

        foreach (ChefKeyModel.PersonalCertificateResolution state in states)
        {
            (bool ok, string detail) = ChefKeyModel.ProjectCertInStoreCheck(state);
            Assert.Contains(detail, allowed);
            Assert.Equal(PassingStates.Contains(state), ok);

            Assert.DoesNotContain("Exception", detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X509", detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Access is denied", detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HRESULT", detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stack", detail, StringComparison.OrdinalIgnoreCase);
        }

        // Unmapped values fail closed with the unavailable copy.
        Assert.Equal((false, UnavailableCopy),
            ChefKeyModel.ProjectCertInStoreCheck((ChefKeyModel.PersonalCertificateResolution)999));
    }

    [Fact] // B03 — full response shape for a UNIQUE LOCAL MACHINE match.
    public void B03_UniqueLocalMachine_FullResponseShape()
    {
        JsonElement root = CertificateWire(ChefKeyModel.PersonalCertificateResolution.UniqueLocalMachine);

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("pass", root.GetProperty("status").GetString());
        Assert.Equal(
            "All local checks passed. A live sign-in is verified when a recipe using this key is baked.",
            root.GetProperty("reason").GetString());
        Assert.Equal(ChefKeyModel.AuthAppRegCertificate, root.GetProperty("authType").GetString());
        Assert.False(root.GetProperty("graphConnectivityTested").GetBoolean());

        Assert.Equal(
            new[] { "tenant_id_present", "client_id_present", "cert_thumbprint_present", "cert_in_store" },
            CheckNames(root));

        (bool ok, string detail) = CertInStore(root);
        Assert.True(ok);
        Assert.Equal(UniqueLocalMachineCopy, detail);

        // The body carries exactly the six documented top-level fields, in order.
        Assert.Equal(
            new[] { "ok", "status", "reason", "authType", "graphConnectivityTested", "checks" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact] // B04 — full response shape for AMBIGUOUS.
    public void B04_Ambiguous_FullResponseShape()
    {
        JsonElement root = CertificateWire(ChefKeyModel.PersonalCertificateResolution.Ambiguous);

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("fail", root.GetProperty("status").GetString());
        Assert.Equal("One or more local checks failed. See the details below.",
            root.GetProperty("reason").GetString());
        Assert.False(root.GetProperty("graphConnectivityTested").GetBoolean());
        Assert.Equal(
            new[] { "tenant_id_present", "client_id_present", "cert_thumbprint_present", "cert_in_store" },
            CheckNames(root));
        Assert.Equal((false, AmbiguousCopy), CertInStore(root));
    }

    [Fact] // B05 — full response shape for UNAVAILABLE.
    public void B05_Unavailable_FullResponseShape()
    {
        JsonElement root = CertificateWire(ChefKeyModel.PersonalCertificateResolution.Unavailable);

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("fail", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("graphConnectivityTested").GetBoolean());
        Assert.Equal(
            new[] { "tenant_id_present", "client_id_present", "cert_thumbprint_present", "cert_in_store" },
            CheckNames(root));
        Assert.Equal((false, UnavailableCopy), CertInStore(root));
    }

    [Fact] // B06 — full response shape for NOT FOUND.
    public void B06_NotFound_FullResponseShape()
    {
        JsonElement root = CertificateWire(ChefKeyModel.PersonalCertificateResolution.NotFound);

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("fail", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("graphConnectivityTested").GetBoolean());
        Assert.Equal(
            new[] { "tenant_id_present", "client_id_present", "cert_thumbprint_present", "cert_in_store" },
            CheckNames(root));
        Assert.Equal((false, NotFoundCopy), CertInStore(root));

        // The old CurrentUser-only copy is gone from the wire.
        Assert.DoesNotContain("your personal certificate store",
            JsonSerializer.Serialize(root), StringComparison.Ordinal);
    }

    [Fact] // B07 — full response shape for INVALID.
    public void B07_Invalid_FullResponseShape()
    {
        JsonElement root = CertificateWire(ChefKeyModel.PersonalCertificateResolution.Invalid);
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("fail", root.GetProperty("status").GetString());
        Assert.Equal((false, InvalidCopy), CertInStore(root));
    }

    [Fact] // B08 — with no thumbprint the cert_in_store check is not emitted at all.
    public void B08_NoThumbprint_OmitsCertInStoreCheck()
    {
        JsonElement root = Wire(
            ChefKeyModel.AuthAppRegCertificate, CertificateMeta(null), hasSecret: false,
            ChefKeyModel.PersonalCertificateResolution.Invalid);

        Assert.Equal(
            new[] { "tenant_id_present", "client_id_present", "cert_thumbprint_present" },
            CheckNames(root));
        Assert.False(root.GetProperty("ok").GetBoolean());
    }

    [Fact] // B09 — every OTHER Chef's Key type is completely unchanged.
    public void B09_OtherChefKeyTypes_AreUnchanged()
    {
        foreach (ChefKeyModel.PersonalCertificateResolution noise in
                 Enum.GetValues<ChefKeyModel.PersonalCertificateResolution>())
        {
            var webLogin = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["authType"] = ChefKeyModel.AuthWebLogin,
                ["upn"] = "chef@contoso.com",
            };
            JsonElement web = Wire(ChefKeyModel.AuthWebLogin, webLogin, hasSecret: false, noise);
            Assert.Equal(new[] { "upn_present", "upn_format" }, CheckNames(web));
            Assert.True(web.GetProperty("ok").GetBoolean());

            var deviceCode = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["authType"] = ChefKeyModel.AuthDeviceCode,
                ["upn"] = "not-a-upn",
            };
            JsonElement device = Wire(ChefKeyModel.AuthDeviceCode, deviceCode, hasSecret: false, noise);
            Assert.Equal(new[] { "upn_present", "upn_format" }, CheckNames(device));
            Assert.False(device.GetProperty("ok").GetBoolean());

            var secret = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["authType"] = ChefKeyModel.AuthAppRegSecret,
                ["tenantId"] = "contoso-tenant",
                ["clientId"] = "contoso-client",
            };
            JsonElement withSecret = Wire(ChefKeyModel.AuthAppRegSecret, secret, hasSecret: true, noise);
            Assert.Equal(
                new[] { "tenant_id_present", "client_id_present", "client_secret_present" },
                CheckNames(withSecret));
            Assert.True(withSecret.GetProperty("ok").GetBoolean());

            JsonElement withoutSecret = Wire(ChefKeyModel.AuthAppRegSecret, secret, hasSecret: false, noise);
            Assert.False(withoutSecret.GetProperty("ok").GetBoolean());

            JsonElement unknown = Wire("Nonsense", new Dictionary<string, object?>(), hasSecret: false, noise);
            Assert.Equal(new[] { "auth_type_known" }, CheckNames(unknown));
            Assert.False(unknown.GetProperty("ok").GetBoolean());

            // No certificate copy leaks into a non-certificate response.
            foreach (JsonElement e in new[] { web, device, withSecret, withoutSecret, unknown })
            {
                string json = JsonSerializer.Serialize(e);
                Assert.DoesNotContain("Personal", json, StringComparison.Ordinal);
                Assert.DoesNotContain("cert_in_store", json, StringComparison.Ordinal);
            }
        }
    }

    // =========================================================================
    // C. STRUCTURAL ASSERTIONS (R4 and containment).
    // =========================================================================

    private const string CurrentUserAnchor =
        "private static (bool Available, PersonalCertificateStoreMatchCount Matches) CountCurrentUserPersonalMatches(";

    private const string LocalMachineAnchor =
        "private static (bool Available, PersonalCertificateStoreMatchCount Matches) CountLocalMachinePersonalMatches(";

    [Fact] // C01 — the RED expectation: BOTH stores are consulted, machine copy exists, old helper gone.
    public void C01_TestPath_ConsultsBothFixedPersonalStores_AndEmitsMachineStoreCopy()
    {
        string raw = ChefKeyModelRaw;

        Assert.Contains("StoreLocation.CurrentUser", raw);  // positive control
        Assert.Contains("StoreLocation.LocalMachine", raw);
        Assert.Contains("found in Local Machine > Personal", raw);
        Assert.DoesNotContain("CertificateExistsInCurrentUserMy", raw);
    }

    [Fact] // C02 — R4 open flags and find flags.
    public void C02_OpenFlagsAndFindFlags_AreExactlyAsRuled()
    {
        string code = ChefKeyModelCode;

        Assert.Contains("validOnly: false", code);           // positive control
        Assert.Equal(2, Occurrences(code, "validOnly: false"));
        Assert.Equal(2, Occurrences(code, "OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly"));

        Assert.DoesNotContain("OpenFlags.ReadWrite", code);
        Assert.DoesNotContain("OpenFlags.MaxAllowed", code);
        Assert.DoesNotContain("validOnly: true", code);
        // The bare ReadOnly-only open (the pre-cycle-37 form) is gone.
        Assert.Equal(0, Occurrences(code, "store.Open(OpenFlags.ReadOnly)"));
    }

    [Fact] // C03 — exactly one fixed construction and one open per store; no injection surface.
    public void C03_ExactlyOneFixedConstructionAndOpenPerStore()
    {
        string code = ChefKeyModelCode;

        Assert.Equal(2, Occurrences(code, "new X509Store("));
        Assert.Equal(1, Occurrences(code, "new X509Store(StoreName.My, StoreLocation.CurrentUser)"));
        Assert.Equal(1, Occurrences(code, "new X509Store(StoreName.My, StoreLocation.LocalMachine)"));
        Assert.Equal(2, Occurrences(code, "store.Open("));

        Assert.Contains("CountLocalMachinePersonalMatches(string thumbprint)", code); // positive control
        foreach (string banned in new[]
        {
            "StoreLocation location", "StoreLocation storeLocation", "StoreLocation loc",
            "X509Store store,", "X509Store store)", "Func<X509", "IEnumerable<X509", "ICertificateStore",
        })
        {
            Assert.DoesNotContain(banned, code);
        }
    }

    [Fact] // C04 — no loop reopens either store.
    public void C04_NoLoopReopensAStore()
    {
        string code = ChefKeyModelCode;

        foreach (string anchor in new[] { CurrentUserAnchor, LocalMachineAnchor })
        {
            string body = MethodBody(code, anchor);

            Assert.Equal(1, Occurrences(body, "new X509Store("));   // positive control
            Assert.Equal(1, Occurrences(body, "store.Open("));
            Assert.Equal(0, Occurrences(body, "for ("));
            Assert.Equal(0, Occurrences(body, "while ("));

            // The only loops are certificate-disposal loops; nothing from the first
            // loop onwards constructs, opens, or re-queries a store.
            int firstLoop = body.IndexOf("foreach (", StringComparison.Ordinal);
            Assert.True(firstLoop > 0, "expected a disposal loop");
            string afterLoops = body.Substring(firstLoop);
            Assert.Equal(0, Occurrences(afterLoops, "new X509Store("));
            Assert.Equal(0, Occurrences(afterLoops, ".Open("));
            Assert.Equal(0, Occurrences(afterLoops, ".Find("));
        }
    }

    [Fact] // C05 — no private-key, export, or mutation API anywhere in the model.
    public void C05_NoPrivateKeyExportOrMutationApi()
    {
        string code = ChefKeyModelCode;

        Assert.Contains("FindByThumbprint", code); // positive control

        foreach (string banned in new[]
        {
            "HasPrivateKey", "GetRSAPrivateKey", "GetECDsaPrivateKey", "GetDSAPrivateKey",
            "CopyWithPrivateKey", "X509KeyStorageFlags", "CngKey", "CspParameters", "CryptoKeySecurity",
            "GetRawCertData", "GetCertHash", "RawData", ".Export(", "SignData", "SignHash",
            "store.Add(", "store.Remove(", "store.AddRange(", "store.RemoveRange(",
            "FileSystemAccessRule", "SetAccessControl", "GetAccessControl",
        })
        {
            Assert.DoesNotContain(banned, code);
        }

        // The ONE pre-existing `PrivateKey` token is the ORGANIZATION inventory's
        // identifier-free count field, which is not a private-key API and is not
        // touched by this cycle. Nothing else in the file mentions a private key.
        Assert.Equal(1, Occurrences(code, "PrivateKey"));
        Assert.Contains("certificates.PrivateKeyUnavailableCount", code);

        // The two fixed store queries touch no key material at all.
        foreach (string anchor in new[] { CurrentUserAnchor, LocalMachineAnchor })
        {
            string body = MethodBody(code, anchor);
            Assert.Contains("FindByThumbprint", body); // positive control
            foreach (string banned in new[] { "PrivateKey", "Export", "RawData", "Add(", "Remove(" })
            {
                Assert.DoesNotContain(banned, body);
            }
        }
    }

    [Fact] // C06 — no store preference and no first-match selection.
    public void C06_NoStorePreferenceOrFirstMatchSelection()
    {
        string code = ChefKeyModelCode;

        Assert.Contains("SaturateMatchCount(matches.Count)", code); // positive control

        foreach (string banned in new[]
        {
            "matches[", "all[", ".First(", ".FirstOrDefault(", ".Single(", ".SingleOrDefault(",
            ".OrderBy(", ".OrderByDescending(", ".Distinct(", ".GroupBy(",
        })
        {
            Assert.DoesNotContain(banned, code);
        }

        // Both stores are counted independently; neither result is discarded.
        Assert.Equal(1, Occurrences(code, "CountCurrentUserPersonalMatches(normalized)"));
        Assert.Equal(1, Occurrences(code, "CountLocalMachinePersonalMatches(normalized)"));
    }

    [Fact] // C07 — no exception text can escape into any response.
    public void C07_NoExceptionTextEscapes()
    {
        string code = ChefKeyModelCode;

        Assert.Equal(2, Occurrences(code, "catch"));      // positive control: the two store catches
        Assert.Equal(0, Occurrences(code, "catch ("));    // both are bare, so nothing is bound

        foreach (string banned in new[]
        {
            "ex.Message", "e.Message", ".StackTrace", "Exception ex", "Exception e)",
            "Marshal.GetLastWin32Error", "GetHRForException",
        })
        {
            Assert.DoesNotContain(banned, code);
        }
    }

    [Fact] // C08 — exactly one production call site of the fixed shim, inside Test; the projection is pure.
    public void C08_ExactlyOneProductionCallSiteOfTheFixedShim()
    {
        string code = ChefKeyModelCode;

        // declaration + exactly one call
        Assert.Equal(2, Occurrences(code, "ResolvePersonalCertificateReference("));
        Assert.Equal(1, Occurrences(code, "certResolution = ResolvePersonalCertificateReference(thumb)"));

        string testBody = MethodBody(code, "public static (int Status, object Body) Test(string id)");
        Assert.Contains("ResolvePersonalCertificateReference(thumb)", testBody);
        Assert.Contains("ProjectTestResponse(authType, metaDict, rec.HasSecret, certResolution)", testBody);

        string projectBody = MethodBody(code, "internal static (int Status, object Body) ProjectTestResponse(");
        Assert.Contains("ProjectCertInStoreCheck(certResolution)", projectBody); // positive control
        Assert.DoesNotContain("X509", projectBody);
        Assert.DoesNotContain("WindowsCredentialStore", projectBody);
        Assert.DoesNotContain("ResolvePersonalCertificateReference", projectBody);

        string resolverBody = MethodBody(code,
            "internal static PersonalCertificateResolution ResolvePersonalCertificateState(");
        Assert.Contains("PersonalCertificateResolution.Ambiguous", resolverBody); // positive control
        foreach (string banned in new[]
        {
            "X509", "X509Store", "StoreName", "StoreLocation", "OpenFlags",
            "WindowsCredentialStore", "store.", "Find(",
        })
        {
            Assert.DoesNotContain(banned, resolverBody);
        }
    }

    [Fact] // C09 — no new caller in Cook, Resume, preview, readiness, or the service.
    public void C09_NoNewCallerInCookResumePreviewReadinessOrService()
    {
        string[] files =
        {
            "src/PAXCookbook.App/RecipeReadModel.CookStart.cs",
            "src/PAXCookbook.App/RecipeReadModel.CookSupervisor.cs",
            "src/PAXCookbook.App/RecipeReadModel.ResumeCook.cs",
            "src/PAXCookbook.App/RecipeReadModel.Preview.cs",
            "src/PAXCookbook.App/RecipePreviewModel.cs",
            "src/PAXCookbook.App/RecipeReadinessModel.cs",
            "src/PAXCookbook.App/CookCredentialInjection.cs",
            "src/PAXCookbook.Service/Program.cs",
            "src/PAXCookbook.Service/ServiceContract.cs",
            "src/PAXCookbook.Service/ServicePaths.cs",
            "src/PAXCookbook.Service/StartupProbeWorker.cs",
            "src/PAXCookbook.Service/DisabledCookPreparationAdapter.cs",
        };

        foreach (string rel in files)
        {
            string src = ReadSource(rel);

            // Positive control — the file really was read and really is source.
            Assert.Contains("namespace PAXCookbook", src);

            foreach (string banned in new[]
            {
                "ResolvePersonalCertificateReference", "PersonalCertificateResolution",
                "PersonalCertificateStoreMatchCount", "ProjectCertInStoreCheck",
                "CountCurrentUserPersonalMatches", "CountLocalMachinePersonalMatches",
                "ProjectTestResponse", "ChefKeyModel.Test(",
                "X509Store", "StoreName.My", "FindByThumbprint", "cert_in_store",
            })
            {
                Assert.DoesNotContain(banned, src);
            }
        }
    }

    [Fact] // C10 — no new caller anywhere in the React app.
    public void C10_NoNewCallerInReact()
    {
        string reactRoot = Path.Combine(RepoRoot(), "app", "web-react", "src");
        string[] files = Directory.GetFiles(reactRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(files.Length > 100, "expected the React source tree to be scanned");

        bool seenChefKeyToken = false;
        foreach (string file in files)
        {
            string src = File.ReadAllText(file);
            if (src.Contains("chefKey", StringComparison.Ordinal)) { seenChefKeyToken = true; }

            foreach (string banned in new[]
            {
                "PersonalCertificateResolution", "PersonalCertificateStoreMatchCount",
                "ResolvePersonalCertificateReference", "ProjectCertInStoreCheck",
                "uniqueLocalMachine", "uniqueCurrentUser", "X509Store",
            })
            {
                Assert.DoesNotContain(banned, src);
            }
        }

        // Positive control — the scanner really read the Chef's Keys React surface.
        Assert.True(seenChefKeyToken, "expected at least one React file to mention chefKey");
    }

    [Fact] // C11 — Recipe readiness stays metadata-only; it gains no certificate-store I/O.
    public void C11_RecipeReadinessRemainsMetadataOnly()
    {
        string src = ReadSource("src/PAXCookbook.App/RecipeReadinessModel.cs");

        Assert.Contains("namespace PAXCookbook.App", src); // positive control

        foreach (string banned in new[]
        {
            "X509", "StoreName", "StoreLocation", "OpenFlags", "ResolvePersonalCertificateReference",
        })
        {
            Assert.DoesNotContain(banned, src);
        }
    }

    [Fact] // C12 — no store-location field is added to Chef's Key metadata.
    public void C12_NoStoreLocationFieldInChefKeyMetadata()
    {
        string raw = ChefKeyModelRaw;

        // Positive control — the exhaustive request-key allow list is unchanged.
        Assert.Contains(
            "\"authType\", \"displayName\", \"tenantId\", \"clientId\", \"certThumbprint\", \"upn\", \"clientSecret\",",
            raw);

        string metadataBody = MethodBody(ChefKeyModelCode,
            "private static string BuildMetadataJson(ChefKeyFields fields)");
        Assert.Contains("fields.CertThumbprint", metadataBody); // positive control
        foreach (string banned in new[] { "StoreLocation", "storeLocation", "certStore", "StoreName" })
        {
            Assert.DoesNotContain(banned, metadataBody);
        }

        foreach (string banned in new[]
        {
            "certStoreLocation", "certificateStoreLocation", "storeLocation",
        })
        {
            Assert.DoesNotContain(banned, raw);
        }
    }

    [Fact] // C13 — the organizationKeys wire literals pinned by T67 survive this cycle.
    public void C13_OrganizationWireLiteralsSurvive()
    {
        string code = ChefKeyModelCode;
        Assert.Contains("chefKeys = items", code);
        Assert.Contains("readOnly = true", code);
        Assert.Contains("certificateOnly = true", code);
    }
}
