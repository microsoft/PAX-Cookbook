using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// Validator tests for the authoritative TestIsolationContext contract. This is
// the keystone of the real-installation safety fix: an isolated run may only be
// activated by a descriptor that passes EVERY rule here. The negative cases are
// the destructive-safety guarantees — each MUST fail closed (no context, errors
// returned) without the caller ever being handed a real-root-resolving context.
public sealed class TestIsolationContextTests
{
    private static string NewIsolatedRoot()
    {
        // Under %TEMP% (i.e. LocalAppData\Temp) which is disjoint from the real
        // LocalAppData\PAXCookbook install root and from profile shell folders.
        string path = Path.Combine(Path.GetTempPath(), "paxiso_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Dictionary<string, object?> ValidDescriptor(string isolated)
    {
        string Sub(string name) => Path.Combine(isolated, name);
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["isolatedRoot"] = isolated,
            ["installRoot"] = Sub("install"),
            ["localStateRoot"] = Sub("state"),
            ["workspace"] = Sub("Workspace"),
            ["webView2Data"] = Sub("WebView2Data"),
            ["engineState"] = Sub("Engine"),
            ["payloadCache"] = Sub("PayloadCache"),
            ["logs"] = Sub("Logs"),
            ["coordinationState"] = Sub("coord"),
            ["setupTempRoot"] = Sub("setuptmp"),
            ["shellIntegrationEnabled"] = false,
            ["updateChecksEnabled"] = false,
            ["networkPayloadDownloadEnabled"] = false,
            ["updateApplyEnabled"] = false,
            ["preserveIsolationOnRelaunch"] = true,
            ["installedProductDiscoveryEnabled"] = false,
        };
    }

    private static string Json(Dictionary<string, object?> d) => JsonSerializer.Serialize(d);

    [Fact]
    public void ValidDescriptor_Parses()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var r = TestIsolationParser.Parse(Json(ValidDescriptor(iso)));
            Assert.True(r.Ok, string.Join("; ", r.Errors));
            Assert.NotNull(r.Context);
            Assert.False(r.Context!.ShellIntegrationEnabled);
            Assert.False(r.Context.UpdateChecksEnabled);
            Assert.False(r.Context.NetworkPayloadDownloadEnabled);
            Assert.False(r.Context.UpdateApplyEnabled);
            Assert.True(r.Context.PreserveIsolationOnRelaunch);
            Assert.True(r.Context.ContainsPath(Path.Combine(iso, "install", "App", "bin")));
            Assert.False(r.Context.ContainsPath(TestIsolationPaths.RealInstallRoot()));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void MissingContext_Empty_FailsClosed()
    {
        var r = TestIsolationParser.Parse("");
        Assert.False(r.Ok);
        Assert.Null(r.Context);
    }

    [Fact]
    public void MalformedJson_FailsClosed()
    {
        var r = TestIsolationParser.Parse("{ not json ");
        Assert.False(r.Ok);
        Assert.Null(r.Context);
    }

    [Fact]
    public void PartialContext_MissingField_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d.Remove("workspace");
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("workspace"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void UnknownField_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d["registrationId"] = "should-never-be-here";
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("unknown field"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void DuplicateField_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            // Hand-craft JSON with a duplicate key (serializer can't emit one).
            string valid = Json(ValidDescriptor(iso));
            string dup = valid.Insert(1, "\"schemaVersion\":1,");
            var r = TestIsolationParser.Parse(dup);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("duplicate field"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void RealInstallRoot_AsIsolatedRoot_FailsClosed()
    {
        var d = ValidDescriptor(TestIsolationPaths.RealInstallRoot());
        var r = TestIsolationParser.Parse(Json(d));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("overlaps the real install root"));
    }

    [Fact]
    public void IsolatedRoot_NestedUnderRealRoot_FailsClosed()
    {
        string nested = Path.Combine(TestIsolationPaths.RealInstallRoot(), "iso_sandbox");
        var r = TestIsolationParser.Parse(Json(ValidDescriptor(nested)));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("overlaps the real install root"));
    }

    [Fact]
    public void RealRoot_NestedUnderIsolatedRoot_FailsClosed()
    {
        // Isolated root = LocalAppData (which CONTAINS the real PAXCookbook root).
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var r = TestIsolationParser.Parse(Json(ValidDescriptor(localAppData)));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("overlaps the real install root"));
    }

    [Fact]
    public void NonAbsoluteSubPath_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d["installRoot"] = "relative\\install";
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("installRoot") && e.Contains("absolute"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void SubPathOutsideIsolatedRoot_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d["installRoot"] = Path.Combine(Path.GetTempPath(), "paxiso_elsewhere_install");
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("installRoot") && e.Contains("isolated root"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void WrongSchemaVersion_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d["schemaVersion"] = 999;
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("schemaVersion"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void UpdateApplyEnabled_WithoutPayload_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d["updateApplyEnabled"] = true;
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("testLocalPayloadPath"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void UpdateApplyDisabled_WithPayload_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d["testLocalPayloadPath"] = Path.Combine(iso, "payload.zip");
            d["testLocalPayloadSha256"] = new string('a', 64);
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("must be null when updateApplyEnabled is false"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void UpdateApplyEnabled_WithValidPayload_Parses()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d["updateApplyEnabled"] = true;
            d["testLocalPayloadPath"] = Path.Combine(iso, "payload.zip");
            d["testLocalPayloadSha256"] = new string('a', 64);
            var r = TestIsolationParser.Parse(Json(d));
            Assert.True(r.Ok, string.Join("; ", r.Errors));
            Assert.True(r.Context!.UpdateApplyEnabled);
            Assert.Equal(new string('a', 64), r.Context.TestLocalPayloadSha256);
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void BooleanFieldWrongType_FailsClosed()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var d = ValidDescriptor(iso);
            d["shellIntegrationEnabled"] = "false"; // string, not bool
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("shellIntegrationEnabled") && e.Contains("boolean"));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void SymlinkEscape_SubPathThroughReparsePoint_FailsClosed()
    {
        // Build an isolated root containing a junction that redirects a sub-path
        // out to the real install root, then declare installRoot under it.
        string iso = NewIsolatedRoot();
        string outside = Path.Combine(Path.GetTempPath(), "paxiso_outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string junction = Path.Combine(iso, "link");
        bool madeLink = TryCreateJunction(junction, outside);
        try
        {
            if (!madeLink) return; // environment can't create junctions; skip

            var d = ValidDescriptor(iso);
            d["installRoot"] = Path.Combine(junction, "install");
            var r = TestIsolationParser.Parse(Json(d));
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("reparse"));
        }
        finally
        {
            try { Directory.Delete(iso, recursive: true); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }
}
