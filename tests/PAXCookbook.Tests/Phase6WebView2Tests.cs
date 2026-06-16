using System.Text.Json;
using PAXCookbook.Broker;
using PAXCookbook.Commands;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.WebView2;
using Xunit;
using RuntimeStatus = PAXCookbook.WebView2.WebView2RuntimeStatus;

namespace PAXCookbook.Tests;

// Phase 6 focused tests. Cover:
//   - WebView2 runtime detector (HKLM64/HKLM32/HKCU; missing; parse-fail)
//   - WebView2 user-data folder is app-owned (LocalAppData + override)
//   - NavigationAllowlist (allow selected port; block external https,
//     file/javascript/data, wrong port, 127.0.0.1)
//   - OpenCommand integrates detector + UI host (broker-before-UI;
//     missing-runtime fails clean; UI hand-off receives correct request)
//   - StatusCommand emits Phase 6 webView2/ui surface
//   - ProtocolCommand dispatches through OpenCommand to UI host
//
// All tests run against the existing TestEnv from Phase4Tests.cs to
// avoid duplicating fakes and to keep the temp-install-root discipline.

internal sealed class FakeRegistryProbe : IRegistryProbe
{
    public Dictionary<(string view, string sub, string val), string?> Values { get; } = new();
    public string? ReadString(string view, string subkey, string valueName)
        => Values.TryGetValue((view, subkey, valueName), out var v) ? v : null;
}

internal sealed class RecordingUiHost : IUiHost
{
    public UiHostLaunchRequest? Last { get; private set; }
    public int Calls { get; private set; }
    public UiHostResult Next { get; set; } = new(UiHostOutcome.Launched, null);
    public UiHostResult Launch(UiHostLaunchRequest request)
    {
        Calls++;
        Last = request;
        return Next;
    }
}

internal sealed class FixedDetector : IWebView2RuntimeDetector
{
    public WebView2DetectionResult Result { get; set; } =
        new(RuntimeStatus.Present, "1.2.3.4", new[] { "HKLM64" }, false);
    public WebView2DetectionResult Detect() => Result;
}

public class WebView2RuntimeDetectorTests
{
    private static FakeRegistryProbe MakeProbe()
    {
        // Pre-seed all three known keys to null (= missing).
        var p = new FakeRegistryProbe();
        p.Values[("HKLM64", WebView2RuntimeDetector.KeyHklm64, "pv")] = null;
        p.Values[("HKLM32", WebView2RuntimeDetector.KeyHklm32, "pv")] = null;
        p.Values[("HKCU",   WebView2RuntimeDetector.KeyHkcu,  "pv")]  = null;
        return p;
    }

    [Fact]
    public void Detect_AllMissing_ReturnsMissing()
    {
        var d = new WebView2RuntimeDetector(MakeProbe()).Detect();
        Assert.Equal(RuntimeStatus.Missing, d.Status);
        Assert.Null(d.Pv);
        Assert.Empty(d.Sources);
    }

    [Fact]
    public void Detect_Hklm64Populated_ReturnsPresent()
    {
        var p = MakeProbe();
        p.Values[("HKLM64", WebView2RuntimeDetector.KeyHklm64, "pv")] = "120.0.0.0";
        var d = new WebView2RuntimeDetector(p).Detect();
        Assert.Equal(RuntimeStatus.Present, d.Status);
        Assert.Equal("120.0.0.0", d.Pv);
        Assert.Contains(d.Sources, s => s.StartsWith("HKLM64\\"));
    }

    [Fact]
    public void Detect_Hklm32OnlyPopulated_ReturnsPresent()
    {
        var p = MakeProbe();
        p.Values[("HKLM32", WebView2RuntimeDetector.KeyHklm32, "pv")] = "121.0.2210.144";
        var d = new WebView2RuntimeDetector(p).Detect();
        Assert.Equal(RuntimeStatus.Present, d.Status);
        Assert.Contains(d.Sources, s => s.StartsWith("HKLM32\\"));
    }

    [Fact]
    public void Detect_HkcuOnlyPopulated_ReturnsPresent()
    {
        var p = MakeProbe();
        p.Values[("HKCU", WebView2RuntimeDetector.KeyHkcu, "pv")] = "122.0.0.0";
        var d = new WebView2RuntimeDetector(p).Detect();
        Assert.Equal(RuntimeStatus.Present, d.Status);
        Assert.Contains(d.Sources, s => s.StartsWith("HKCU\\"));
    }

    [Fact]
    public void Detect_AllThreePopulated_ListsAllSources()
    {
        var p = MakeProbe();
        p.Values[("HKLM64", WebView2RuntimeDetector.KeyHklm64, "pv")] = "120.0.0.0";
        p.Values[("HKLM32", WebView2RuntimeDetector.KeyHklm32, "pv")] = "120.0.0.0";
        p.Values[("HKCU",   WebView2RuntimeDetector.KeyHkcu,  "pv")]  = "120.0.0.0";
        var d = new WebView2RuntimeDetector(p).Detect();
        Assert.Equal(3, d.Sources.Count);
    }

    [Fact]
    public void Detect_EmptyPv_TreatedAsMissing()
    {
        var p = MakeProbe();
        p.Values[("HKCU", WebView2RuntimeDetector.KeyHkcu, "pv")] = "   ";
        var d = new WebView2RuntimeDetector(p).Detect();
        Assert.Equal(RuntimeStatus.Missing, d.Status);
    }

    [Fact]
    public void Detect_UnparseablePv_PresentButFlagged()
    {
        var p = MakeProbe();
        p.Values[("HKLM64", WebView2RuntimeDetector.KeyHklm64, "pv")] = "abc";
        var d = new WebView2RuntimeDetector(p).Detect();
        Assert.Equal(RuntimeStatus.Present, d.Status);
        Assert.True(d.PvParseFailed);
    }
}

public class WebView2DataPathsTests
{
    [Fact]
    public void FromLocalAppData_UsesAppOwnedFolder()
    {
        var p = WebView2DataPaths.FromLocalAppData();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "PAXCookbook", "WebView2Data"), p.UserDataFolder);
        // Explicit check: never uses Microsoft\Edge\User Data path.
        Assert.DoesNotContain("\\Microsoft\\Edge\\", p.UserDataFolder);
    }

    [Fact]
    public void FromInstallRoot_OverridesForTests()
    {
        var p = WebView2DataPaths.FromInstallRoot(@"C:\temp\xyz");
        Assert.Equal(@"C:\temp\xyz\WebView2Data", p.UserDataFolder);
    }
}

public class NavigationAllowlistTests
{
    [Fact]
    public void Allow_LocalhostSelectedPort()
    {
        var a = new NavigationAllowlist(17654);
        Assert.Equal(NavigationDecision.Allow, a.Evaluate("http://localhost:17654/").Decision);
        Assert.Equal(NavigationDecision.Allow, a.Evaluate("http://localhost:17654/recipes/123").Decision);
        Assert.Equal(NavigationDecision.Allow, a.Evaluate("http://LOCALHOST:17654/").Decision);
    }

    [Fact]
    public void Block_ExternalHttps()
    {
        var a = new NavigationAllowlist(17654);
        Assert.Equal(NavigationDecision.Block, a.Evaluate("https://example.com/").Decision);
        Assert.Equal(NavigationDecision.Block, a.Evaluate("https://localhost:17654/").Decision);
    }

    [Fact]
    public void Block_FileJavascriptData()
    {
        var a = new NavigationAllowlist(17654);
        Assert.Equal(NavigationDecision.Block, a.Evaluate("file:///C:/Windows/System32/calc.exe").Decision);
        Assert.Equal(NavigationDecision.Block, a.Evaluate("javascript:alert(1)").Decision);
        Assert.Equal(NavigationDecision.Block, a.Evaluate("data:text/html,<script>alert(1)</script>").Decision);
    }

    [Fact]
    public void Block_WrongPort()
    {
        var a = new NavigationAllowlist(17654);
        var d = a.Evaluate("http://localhost:17655/");
        Assert.Equal(NavigationDecision.Block, d.Decision);
        Assert.StartsWith("port-", d.Reason);
    }

    [Fact]
    public void Block_127001()
    {
        // 127.0.0.1 is rejected because the SPA's WebAuthn origin
        // assumptions rely on the "localhost" host literal.
        var a = new NavigationAllowlist(17654);
        Assert.Equal(NavigationDecision.Block, a.Evaluate("http://127.0.0.1:17654/").Decision);
    }
}

public class OpenCommand_Phase6Tests
{
    private static (TestEnv env, FakeBrokerController broker, RecordingUiHost ui) Setup(
        bool brokerAlreadyRunning = false,
        IWebView2RuntimeDetector? detector = null)
    {
        var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var brokerDir = Path.Combine(env.InstallRoot, "App", "broker");
        Directory.CreateDirectory(brokerDir);
        File.WriteAllText(Path.Combine(brokerDir, "Start-Broker.ps1"), "# stub");

        var broker = new FakeBrokerController();
        if (brokerAlreadyRunning)
        {
            broker.NextProbe = new BrokerStatus(true, 999, 17654, "http://localhost:17654", env.WorkspacePath, "workspace-lock");
        }
        var ui = new RecordingUiHost();
        return (env, broker, ui);
    }

    private static CommandContext Ctx(TestEnv env, FakeBrokerController broker, RecordingUiHost ui, IWebView2RuntimeDetector? det = null)
        => new(env.Resolver, env.Bootstrap, env.Locks, env.Sidecars, broker, env.Log, env.Stdout, env.Stderr,
               installRootOverride: null,
               brokerReadyTimeout: TimeSpan.FromSeconds(2),
               brokerStopTimeout: TimeSpan.FromSeconds(2),
               webView2Detector: det ?? new FixedDetector(),
               uiHost: ui,
               webView2Data: WebView2DataPaths.FromInstallRoot(env.InstallRoot));

    [Fact]
    public void Open_MissingRuntime_FailsBeforeBrokerOrUi()
    {
        var (env, broker, ui) = Setup();
        var det = new FixedDetector
        {
            Result = new WebView2DetectionResult(RuntimeStatus.Missing, null, Array.Empty<string>(), false)
        };
        var rc = OpenCommand.Run(Ctx(env, broker, ui, det));
        Assert.Equal(AppExitCodes.WebView2RuntimeMissing, rc);
        Assert.Equal(0, broker.ProbeCalls);
        Assert.Equal(0, broker.StartCalls);
        Assert.Equal(0, ui.Calls);
        Assert.Contains("WebView2", env.Stderr.ToString());
        env.Dispose();
    }

    [Fact]
    public void Open_StartsBrokerThenHandsOffToUiHost()
    {
        var (env, broker, ui) = Setup();
        var rc = OpenCommand.Run(Ctx(env, broker, ui));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, broker.StartCalls);
        Assert.Equal(1, ui.Calls);
        Assert.NotNull(ui.Last);
        Assert.Equal("PAX Cookbook", ui.Last!.WindowTitle);
        Assert.Equal(17654, ui.Last.BrokerPort);
        Assert.Equal("http://localhost:17654", ui.Last.BrokerUrl);
        Assert.EndsWith("WebView2Data", ui.Last.UserDataFolder);
        env.Dispose();
    }

    [Fact]
    public void Open_BrokerAlreadyRunning_ReusesAndHandsOffUi()
    {
        var (env, broker, ui) = Setup(brokerAlreadyRunning: true);
        var rc = OpenCommand.Run(Ctx(env, broker, ui));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(0, broker.StartCalls);
        Assert.Equal(1, ui.Calls);
        env.Dispose();
    }

    [Fact]
    public void Open_DoesNotReferenceBrowserStrings()
    {
        // Structural ban: the OpenCommand source must not embed any
        // string literal naming a browser binary or Edge --app= form.
        var src = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "PAXCookbook", "Commands", "OpenCommand.cs"));
        foreach (var banned in new[] { "msedge", "chrome", "iexplore", "--app=", "start microsoft-edge", "shell:AppsFolder" })
        {
            Assert.DoesNotContain(banned, src, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Reopen_DelegatesToOpenAndLaunchesUi()
    {
        var (env, broker, ui) = Setup();
        var rc = ReopenCommand.Run(Ctx(env, broker, ui));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, ui.Calls);
        env.Dispose();
    }

    internal static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PAXCookbook.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}

public class StatusCommand_Phase6Tests
{
    [Fact]
    public void Status_ReportsWebView2ImplementedAndSurface()
    {
        using var env = new TestEnv();
        env.WriteInstallState(new InstallState
        {
            AppVersion = "0.6.0",
            InstallRoot = env.InstallRoot,
            WorkspaceFolderPath = env.WorkspacePath
        });
        var ctx = new CommandContext(env.Resolver, env.Bootstrap, env.Locks, env.Sidecars,
            new FakeBrokerController(), env.Log, env.Stdout, env.Stderr, null,
            webView2Detector: new FixedDetector
            {
                Result = new WebView2DetectionResult(RuntimeStatus.Present, "120.0.0.0", new[] { "HKLM64" }, false)
            },
            webView2Data: WebView2DataPaths.FromInstallRoot(env.InstallRoot));
        var rc = StatusCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        using var doc = JsonDocument.Parse(env.Stdout.ToString());
        var ui = doc.RootElement.GetProperty("ui");
        Assert.True(ui.GetProperty("implemented").GetBoolean());
        Assert.Equal("webview2", ui.GetProperty("surface").GetString());
        var wv = doc.RootElement.GetProperty("webView2");
        Assert.True(wv.GetProperty("implemented").GetBoolean());
        Assert.Equal("present", wv.GetProperty("runtimeStatus").GetString());
        Assert.Equal("120.0.0.0", wv.GetProperty("pv").GetString());
        Assert.EndsWith("WebView2Data", wv.GetProperty("userDataFolder").GetString());
    }

    [Fact]
    public void Status_MissingRuntime_RuntimeStatusMissing()
    {
        using var env = new TestEnv();
        var ctx = new CommandContext(env.Resolver, env.Bootstrap, env.Locks, env.Sidecars,
            new FakeBrokerController(), env.Log, env.Stdout, env.Stderr, null,
            webView2Detector: new FixedDetector
            {
                Result = new WebView2DetectionResult(RuntimeStatus.Missing, null, Array.Empty<string>(), false)
            });
        StatusCommand.Run(ctx);
        using var doc = JsonDocument.Parse(env.Stdout.ToString());
        Assert.Equal("missing", doc.RootElement.GetProperty("webView2").GetProperty("runtimeStatus").GetString());
    }
}

public class ProtocolCommand_Phase6Tests
{
    [Fact]
    public void Protocol_AcceptedUri_DispatchesThroughOpenToUiHost()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var brokerDir = Path.Combine(env.InstallRoot, "App", "broker");
        Directory.CreateDirectory(brokerDir);
        File.WriteAllText(Path.Combine(brokerDir, "Start-Broker.ps1"), "# stub");
        var broker = new FakeBrokerController();
        var ui = new RecordingUiHost();
        var ctx = new CommandContext(env.Resolver, env.Bootstrap, env.Locks, env.Sidecars,
            broker, env.Log, env.Stdout, env.Stderr, null,
            webView2Detector: new FixedDetector(),
            uiHost: ui,
            webView2Data: WebView2DataPaths.FromInstallRoot(env.InstallRoot));
        var rc = ProtocolCommand.Run(ctx, "paxcookbook://open");
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, ui.Calls);
        // Belt-and-braces: ensure we did not leak the raw URI into stdout.
        Assert.DoesNotContain("paxcookbook://open", env.Stdout.ToString());
    }
}

public class ProjectReferenceShapeTests
{
    private static string ReadCsproj(string rel)
    {
        var root = OpenCommand_Phase6Tests.FindRepoRoot();
        return File.ReadAllText(Path.Combine(root, rel));
    }

    [Fact]
    public void AppCsproj_ReferencesWebView2()
    {
        var s = ReadCsproj("src\\PAXCookbook\\PAXCookbook.csproj");
        Assert.Contains("Microsoft.Web.WebView2", s);
    }

    [Fact]
    public void SetupCsproj_DoesNotReferenceWebView2()
    {
        var s = ReadCsproj("src\\PAXCookbookSetup\\PAXCookbookSetup.csproj");
        Assert.DoesNotContain("Microsoft.Web.WebView2", s);
    }
}
