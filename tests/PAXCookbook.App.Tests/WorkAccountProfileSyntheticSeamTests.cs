// NON-LIVE synthetic session-profile acceptance — NATIVE units (TEST ONLY).
//
// Compiles under the EXPERIMENTAL_WAM constant (the Work-account build) and runs
// in the existing /p:ExperimentalWam=true test invocation. It adds NO product
// source: the synthetic driver below lives entirely in the test assembly, so
// every product binary (stable, normal-WAM, isolated) is byte-unchanged.
//
// These units drive the REAL bounded presentation pipeline
// (WorkAccountProfileWindowChannel + WorkAccountProfilePresentationFactory +
// WorkAccountPreferredAccountStore) with SYNTHETIC inputs only: a locally
// embedded valid JPEG and runtime-fabricated, non-identifying account references.
// No network, no MSAL/WAM/Graph, no real identity is involved.

#if EXPERIMENTAL_WAM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

public sealed class WorkAccountProfileSyntheticSeamTests
{
    // ---- Test-only synthetic driver (no product surface) ---------------------

    // A self-contained minimal valid JPEG: SOI (0xFFD8) + JFIF APP0 header +
    // EOI (0xFFD9). Synthetic image data, not a real profile photo. Built as a
    // byte literal so it is trivially valid (no base64 parsing).
    private static byte[] SyntheticJpeg() => new byte[]
    {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46,
        0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0xFF, 0xD9,
    };

    // A synthetic, non-identifying display source used ONLY to exercise the
    // initials deriver — a fabricated two-token label, not a real name/UPN.
    private const string SyntheticInitialsSource = "Synthetic Tester";

    // Fabricates a HomeAccountId-shaped "<object>.<tenant>" reference at runtime
    // from fresh GUIDs — not a configured client id or tenant id, no source literal.
    private static string SyntheticAccountReference()
        => Guid.NewGuid().ToString("D") + "." + Guid.NewGuid().ToString("D");

    private static WorkAccountProfilePresentation BuildPhotoPresentation()
        => WorkAccountProfilePresentationFactory.Build(
            WorkAccountPhotoFetchResult.Available(SyntheticJpeg(), WorkAccountProfileWindowChannel.AcceptedContentType),
            SyntheticInitialsSource);

    private static WorkAccountProfilePresentation BuildFailurePresentation(WorkAccountPhotoOutcome outcome)
        => WorkAccountProfilePresentationFactory.Build(
            WorkAccountPhotoFetchResult.Failure(outcome), SyntheticInitialsSource);

    // ---- Capturing channel harness -------------------------------------------

    private static (WorkAccountProfileWindowChannel channel, List<string> captured) NewCapturingChannel()
    {
        var channel = new WorkAccountProfileWindowChannel();
        var captured = new List<string>();
        channel.SetPoster(json => captured.Add(json), action => action());
        return (channel, captured);
    }

    private static JsonElement ParseOnly(List<string> captured)
    {
        Assert.Single(captured);
        using var doc = JsonDocument.Parse(captured[0]);
        return doc.RootElement.Clone();
    }

    private static readonly string[] IdentityLeakKeys =
    {
        "token", "accessToken", "idToken", "upn", "username", "email",
        "tenantId", "objectId", "clientId", "accountId", "homeAccountId",
        "claims", "scope", "url", "authority",
    };

    private static void AssertNoIdentityKeys(JsonElement envelope)
    {
        foreach (JsonProperty p in envelope.EnumerateObject())
        {
            foreach (string bad in IdentityLeakKeys)
            {
                Assert.False(
                    string.Equals(p.Name, bad, StringComparison.OrdinalIgnoreCase),
                    $"envelope carried a forbidden identity key '{p.Name}'.");
            }
        }
    }

    // --- Scenario 1: synthetic photo -> photo envelope (closed shape) ----------
    [Fact]
    public void Scenario1_SyntheticPhoto_PublishesBoundedPhotoEnvelope()
    {
        var (channel, captured) = NewCapturingChannel();

        channel.Publish(BuildPhotoPresentation());

        JsonElement env = ParseOnly(captured);
        Assert.Equal(WorkAccountProfileWindowChannel.MessageType, env.GetProperty("type").GetString());
        Assert.Equal(WorkAccountProfileWindowChannel.StatePhoto, env.GetProperty("state").GetString());
        Assert.Equal(WorkAccountProfileWindowChannel.AcceptedContentType, env.GetProperty("contentType").GetString());
        Assert.Equal(WorkAccountProfileWindowChannel.LabelText, env.GetProperty("label").GetString());

        // The envelope's ONLY keys are the bounded photo contract.
        var keys = env.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "contentType", "imageBase64", "label", "state", "type" }, keys);
        AssertNoIdentityKeys(env);

        // The image bytes are the synthetic JPEG: SOI (FF D8) ... EOI (FF D9).
        byte[] bytes = Convert.FromBase64String(env.GetProperty("imageBase64").GetString()!);
        Assert.True(bytes.Length > 2);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[^2]);
        Assert.Equal(0xD9, bytes[^1]);
    }

    // --- Scenario 2: every photo failure -> initials fallback (never blank) ----
    // The outcome is passed as its underlying int so the public [Theory] method
    // does not expose the internal WorkAccountPhotoOutcome enum in its signature.
    public static IEnumerable<object[]> FailureOutcomes()
    {
        yield return new object[] { (int)WorkAccountPhotoOutcome.NotFound };
        yield return new object[] { (int)WorkAccountPhotoOutcome.Forbidden };
        yield return new object[] { (int)WorkAccountPhotoOutcome.Timeout };
        yield return new object[] { (int)WorkAccountPhotoOutcome.Redirected };
        yield return new object[] { (int)WorkAccountPhotoOutcome.WrongContentType };
        yield return new object[] { (int)WorkAccountPhotoOutcome.TooLarge };
        yield return new object[] { (int)WorkAccountPhotoOutcome.TransportFailure };
        yield return new object[] { (int)WorkAccountPhotoOutcome.MalformedResponse };
    }

    [Theory]
    [MemberData(nameof(FailureOutcomes))]
    public void Scenario2_PhotoFailure_PublishesInitialsEnvelope(int outcomeValue)
    {
        var outcome = (WorkAccountPhotoOutcome)outcomeValue;
        var (channel, captured) = NewCapturingChannel();

        channel.Publish(BuildFailurePresentation(outcome));

        JsonElement env = ParseOnly(captured);
        Assert.Equal(WorkAccountProfileWindowChannel.StateInitials, env.GetProperty("state").GetString());
        Assert.Equal(WorkAccountProfileWindowChannel.LabelText, env.GetProperty("label").GetString());

        var keys = env.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "initials", "label", "state", "type" }, keys);
        AssertNoIdentityKeys(env);

        string initials = env.GetProperty("initials").GetString()!;
        // Never blank; at most two characters; uppercase; or the "WA" fallback.
        Assert.False(string.IsNullOrEmpty(initials));
        Assert.InRange(initials.Length, 1, 2);
        Assert.Equal(initials.ToUpperInvariant(), initials);
        // The synthetic "Synthetic Tester" source derives to "ST".
        Assert.Equal("ST", initials);
    }

    [Fact]
    public void Scenario2_InitialsDeriver_FallsBackToWa_ForNonLetterSource()
    {
        Assert.Equal(WorkAccountInitialsDeriver.Fallback, WorkAccountInitialsDeriver.Derive("1234@contoso.com"));
        Assert.Equal("WA", WorkAccountInitialsDeriver.Derive(null));
    }

    // --- Scenario 3: oversized photo bytes -> receiver-safe initials degrade ---
    [Fact]
    public void Scenario3_OversizedPhoto_DegradesToInitials_NoPhotoEnvelope()
    {
        // A photo whose base64 encoding exceeds the encoded ceiling must never be
        // posted as a photo; the channel degrades it to initials before crossing.
        var oversized = new byte[WorkAccountProfilePhotoFetcher.MaxPhotoBytes + 3];
        oversized[0] = 0xFF;
        oversized[1] = 0xD8;
        var presentation = WorkAccountProfilePresentationFactory.Build(
            WorkAccountPhotoFetchResult.Available(oversized, WorkAccountProfileWindowChannel.AcceptedContentType),
            SyntheticInitialsSource);

        string envelope = WorkAccountProfileWindowChannel.BuildEnvelope(presentation);
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal(
            WorkAccountProfileWindowChannel.StateInitials,
            doc.RootElement.GetProperty("state").GetString());
        Assert.False(doc.RootElement.TryGetProperty("imageBase64", out _));
    }

    // --- Scenario 6: none/teardown -> clear envelope ---------------------------
    [Fact]
    public void Scenario6_Clear_PublishesNoneEnvelope()
    {
        var (channel, captured) = NewCapturingChannel();

        channel.Clear();

        JsonElement env = ParseOnly(captured);
        Assert.Equal(WorkAccountProfileWindowChannel.StateNone, env.GetProperty("state").GetString());
        var keys = env.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "label", "state", "type" }, keys);
        AssertNoIdentityKeys(env);
    }

    // --- Scenario 7: different-account synthetic -> DPAPI blob removed ----------
    [Fact]
    public void Scenario7_SyntheticPreferredAccount_ClearRemovesProtectedBlob()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pax-seam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkAccountPreferredAccountStore(baseDir);
            string reference = SyntheticAccountReference();

            // The synthetic reference is HomeAccountId-shaped and non-identifying.
            string[] parts = reference.Split('.');
            Assert.Equal(2, parts.Length);
            Assert.True(Guid.TryParse(parts[0], out _));
            Assert.True(Guid.TryParse(parts[1], out _));

            string path = WorkAccountPreferredAccountStore.ResolvePreferredPath(baseDir);

            Assert.True(store.TrySave(reference));
            Assert.True(File.Exists(path));

            // The on-disk artifact is opaque ciphertext, not the plaintext ref.
            byte[] onDisk = File.ReadAllBytes(path);
            string onDiskText = System.Text.Encoding.UTF8.GetString(onDisk);
            Assert.DoesNotContain(reference, onDiskText, StringComparison.Ordinal);

            // Round-trips in-process only.
            Assert.Equal(reference, store.TryLoad());

            // The native different-account clear removes the protected blob.
            Assert.True(store.Clear());
            Assert.False(File.Exists(path));
            Assert.Null(store.TryLoad());
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SyntheticAccountReference_IsRuntimeGenerated_AndDistinctPerCall()
    {
        string a = SyntheticAccountReference();
        string b = SyntheticAccountReference();
        Assert.NotEqual(a, b);
    }

    // --- Scenario 8: relaunch/re-init -> no persistence (fresh store is empty) --
    [Fact]
    public void Scenario8_FreshStore_HasNoPreference_NoInitialsOrPhotoPersistence()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pax-seam-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A brand-new install root (simulating relaunch/re-init) starts empty:
            // no preferred account, no persisted photo/initials anywhere.
            var store = new WorkAccountPreferredAccountStore(baseDir);
            Assert.Null(store.TryLoad());
            Assert.False(File.Exists(WorkAccountPreferredAccountStore.ResolvePreferredPath(baseDir)));
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // --- Synthetic photo primitive: valid JPEG SOI/EOI -------------------------
    [Fact]
    public void SyntheticJpeg_IsValidJpeg_SoiEoi()
    {
        byte[] bytes = SyntheticJpeg();
        Assert.True(bytes.Length > 2);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[^2]);
        Assert.Equal(0xD9, bytes[^1]);
    }
}
#endif
