using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Service.Tests;

// Cycle 33 - SERVICE-SIDE boundary coverage for the linked cook preparation
// phase order.
//
// SCOPE, stated plainly: compile-linking the portable CookPreparationSequence
// proves ONE source file and one phase order. It does NOT prove equivalent host
// capabilities and does NOT make service cook execution ready. This file exists to
// keep that honest: the service adapter fails closed on the FIRST requested phase,
// has no production caller, and the service assembly still structurally excludes
// every desktop-only capability.
//
// Nothing here installs, registers, starts, or contacts a service, touches machine
// state, spawns a process, runs PAX, or starts a Bake.
public class ServiceCookPreparationBoundaryTests
{
    // ---- the DISABLED adapter fails closed on its first requested phase ------

    [Theory]
    [InlineData(CookTriggerKind.Manual)]
    [InlineData(CookTriggerKind.Scheduled)]
    [InlineData(CookTriggerKind.Resume)]
    public void The_service_adapter_stops_at_the_first_phase_the_sequence_requests(CookTriggerKind trigger)
    {
        DisabledServiceCookPreparationAdapter adapter = new();

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(trigger, adapter);

        Assert.Equal(CookPreparationDisposition.RefusedAtGates, outcome.Disposition);
        Assert.Equal(CookPreparationPhase.Gates, outcome.FurthestPhaseInvoked);
        Assert.False(outcome.Prepared);

        // Exactly ONE phase was ever requested: authorization and preparation were
        // never reached.
        Assert.Equal(1, adapter.PhasesRequested);
        Assert.Equal(ServiceCookPreparationRefusal.GatesNotEnabledInService, adapter.Refusal);
    }

    [Fact]
    public void The_service_adapter_covers_every_declared_trigger()
    {
        // EXHAUSTIVE: the fail-closed run above is asserted for EVERY declared
        // trigger, not just the ones a theory happened to name, so adding a trigger
        // without covering the service host fails here.
        foreach (CookTriggerKind trigger in Enum.GetValues<CookTriggerKind>())
        {
            DisabledServiceCookPreparationAdapter adapter = new();

            CookPreparationOutcome outcome =
                CookPreparationSequence.Execute(trigger, adapter);

            Assert.Equal(CookPreparationDisposition.RefusedAtGates, outcome.Disposition);
            Assert.Equal(1, adapter.PhasesRequested);
            Assert.Equal(ServiceCookPreparationRefusal.GatesNotEnabledInService, adapter.Refusal);
        }
    }

    [Fact]
    public void Every_service_adapter_phase_refuses_with_a_bounded_non_secret_disposition()
    {
        // POSITIVE CONTROL for the counter above: the counter is live and each phase
        // individually refuses, so "exactly 1" is a real finding.
        //
        // The service serves NO trigger, so ALL THREE authorization phases are
        // cross-trigger dead ends and every one of them must return false.
        DisabledServiceCookPreparationAdapter adapter = new();

        Assert.False(adapter.EvaluateGatesPhase());
        Assert.False(adapter.AuthorizeManualPhase());
        Assert.False(adapter.AuthorizeScheduledPhase());
        Assert.False(adapter.AuthorizeResumePhase());
        Assert.False(adapter.PreparePhase());

        Assert.Equal(5, adapter.PhasesRequested);
        Assert.True(Enum.IsDefined(adapter.Refusal));

        // Each authorization phase records the SAME bounded, non-secret reason.
        foreach (Func<DisabledServiceCookPreparationAdapter, bool> phase in new Func<DisabledServiceCookPreparationAdapter, bool>[]
                 {
                     a => a.AuthorizeManualPhase(),
                     a => a.AuthorizeScheduledPhase(),
                     a => a.AuthorizeResumePhase(),
                 })
        {
            DisabledServiceCookPreparationAdapter each = new();
            Assert.False(phase(each));
            Assert.Equal(ServiceCookPreparationRefusal.AuthorizationNotEnabledInService, each.Refusal);
        }

        // The zero value means "never asked", so it can never read as authorized,
        // and a fresh adapter moves off it the moment a phase is requested.
        DisabledServiceCookPreparationAdapter fresh = new();
        Assert.Equal(ServiceCookPreparationRefusal.NotRequested, fresh.Refusal);
        Assert.Equal(0, fresh.PhasesRequested);
        Assert.False(fresh.PreparePhase());
        Assert.Equal(ServiceCookPreparationRefusal.PreparationNotEnabledInService, fresh.Refusal);
    }

    [Fact]
    public void The_disabled_adapter_has_no_production_caller()
    {
        var offenders = new List<string>();
        foreach (string file in ServiceSourceFiles())
        {
            if (Path.GetFileName(file) == "DisabledCookPreparationAdapter.cs")
            {
                continue;
            }
            if (StripComments(File.ReadAllText(file))
                .Contains("DisabledServiceCookPreparationAdapter", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(offenders);

        // POSITIVE CONTROL: the declaring file DOES contain the name, so the scan
        // above is capable of finding it.
        string declaring = ServiceSourceFiles()
            .Single(f => Path.GetFileName(f) == "DisabledCookPreparationAdapter.cs");
        Assert.Contains(
            "DisabledServiceCookPreparationAdapter",
            StripComments(File.ReadAllText(declaring)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_program_nor_the_startup_probe_worker_references_the_sequence_or_the_adapter()
    {
        foreach (string name in new[] { "Program.cs", "StartupProbeWorker.cs" })
        {
            string body = StripComments(File.ReadAllText(Path.Combine(ServiceSourceRoot(), name)));
            Assert.DoesNotContain("DisabledServiceCookPreparationAdapter", body, StringComparison.Ordinal);
            Assert.DoesNotContain("CookPreparationSequence", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ICookPreparationAdapter", body, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the same reader DOES see real content in those files.
        Assert.Contains(
            "StartupProbeWorker",
            StripComments(File.ReadAllText(Path.Combine(ServiceSourceRoot(), "StartupProbeWorker.cs"))),
            StringComparison.Ordinal);
    }

    // ---- token-precise process scan ------------------------------------------

    // Rejects the three tokens that can start an executable: the argument object,
    // the static launcher, and the instance form the desktop supervisor actually
    // uses. Deliberately does NOT reject Process.GetCurrentProcess, which the
    // startup probe legitimately uses to read its own Session ID.
    private static readonly Regex ProcessStartInfoToken = new(@"\bProcessStartInfo\b", RegexOptions.Compiled);
    private static readonly Regex ProcessStartToken = new(@"\bProcess\s*\.\s*Start\s*\(", RegexOptions.Compiled);
    private static readonly Regex NewProcessToken = new(@"\bnew\s+Process\s*[({]", RegexOptions.Compiled);

    private static bool FiresOn(string source)
    {
        string stripped = StripComments(source);
        return ProcessStartInfoToken.IsMatch(stripped)
            || ProcessStartToken.IsMatch(stripped)
            || NewProcessToken.IsMatch(stripped);
    }

    [Fact]
    public void No_service_source_file_can_start_an_executable()
    {
        var offenders = ServiceSourceFiles()
            .Where(f => FiresOn(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_process_scan_fires_on_the_desktop_supervisor()
    {
        // POSITIVE CONTROL. The desktop cook supervisor is the one file in the
        // product that legitimately spawns the sanctioned child, so the scanner must
        // fire on it. Without this, "0 offenders" above would be unfalsifiable.
        string supervisor = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "PAXCookbook.App", "RecipeReadModel.CookSupervisor.cs"));

        Assert.True(FiresOn(supervisor));
        Assert.Matches(ProcessStartInfoToken, StripComments(supervisor));
        Assert.Matches(NewProcessToken, StripComments(supervisor));

        // Disclosed honestly: the desktop supervisor spawns through the INSTANCE
        // form (new Process { StartInfo = psi } then proc.Start()), so the static
        // Process.Start( token does not appear in the product at all. Its own
        // positive control is therefore synthetic - see
        // The_comment_stripper_removes_both_comment_forms.
        Assert.DoesNotMatch(ProcessStartToken, StripComments(supervisor));
    }

    [Fact]
    public void The_process_scan_does_not_misclassify_the_startup_probe_session_id_read()
    {
        // NEGATIVE CONTROL with teeth. StartupProbeWorker imports System.Diagnostics
        // and calls Process.GetCurrentProcess() to read its own Session ID. That is
        // a read, not an execution, and the scanner must not fire on it.
        string probe = File.ReadAllText(Path.Combine(ServiceSourceRoot(), "StartupProbeWorker.cs"));
        string stripped = StripComments(probe);

        // The construct really is present - otherwise "does not fire" would be
        // vacuous.
        Assert.Contains("Process.GetCurrentProcess", stripped, StringComparison.Ordinal);
        Assert.Contains("using System.Diagnostics;", stripped, StringComparison.Ordinal);

        Assert.False(FiresOn(probe));
    }

    // ---- structural exclusion of desktop-only capabilities -------------------

    public static TheoryData<string> ForbiddenTokens() => new()
    {
        "Sqlite", "SqliteConnection", "System.Windows.Forms", "WinForms", "WebView2",
        "Microsoft.Identity.Client", "PublicClientApplication", "WithBroker",
        "ChefKeyModel", "CookConsoleWindow", "TelegramNotifier", "WindowsCredentialStore",
        "X509Store", "Microsoft.Win32.Registry", "HttpClient",
    };

    [Theory]
    [MemberData(nameof(ForbiddenTokens))]
    public void The_service_sources_exclude_every_desktop_only_capability(string token)
    {
        var offenders = ServiceSourceFiles()
            .Where(f => StripComments(File.ReadAllText(f)).Contains(token, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_forbidden_token_scan_is_live()
    {
        // POSITIVE CONTROL for the theory above. Each of these tokens IS present in
        // a real App file, so a zero count on the service side is meaningful rather
        // than a scanner that never matches anything.
        (string Token, string RelativePath)[] controls =
        {
            ("SqliteConnection", @"src\PAXCookbook.App\RecipeReadModel.CookStart.cs"),
            ("WebView2", @"src\PAXCookbook.App\WebViewShell.cs"),
            ("ChefKeyModel", @"src\PAXCookbook.App\ChefKeyModel.cs"),
            ("CookConsoleWindow", @"src\PAXCookbook.App\CookConsoleWindow.cs"),
            ("TelegramNotifier", @"src\PAXCookbook.App\TelegramNotifier.cs"),
            ("WindowsCredentialStore", @"src\PAXCookbook.App\WindowsCredentialStore.cs"),
        };

        foreach ((string token, string relative) in controls)
        {
            string body = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), relative)));
            Assert.Contains(token, body, StringComparison.Ordinal);
        }
    }

    // ---- assembly boundary ---------------------------------------------------

    [Fact]
    public void The_service_assembly_references_neither_the_app_nor_shared()
    {
        string[] referenced = typeof(DisabledServiceCookPreparationAdapter).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("PAX Cookbook", referenced);
        Assert.DoesNotContain("PAXCookbook.App", referenced);
        Assert.DoesNotContain("PAXCookbook.Shared", referenced);

        // The portable contract nonetheless compiles INTO the service assembly, so
        // the phase order is genuinely shared source rather than a copy.
        Assert.Equal(
            typeof(DisabledServiceCookPreparationAdapter).Assembly,
            typeof(CookPreparationSequence).Assembly);
    }

    [Fact]
    public void The_service_project_file_links_the_contract_and_adds_no_project_reference()
    {
        string csproj = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "PAXCookbook.Service", "PAXCookbook.Service.csproj"));

        Assert.Contains("CookPreparationSequence.cs", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    internal static string ServiceSourceRoot() =>
        Path.Combine(RepoRoot(), "src", "PAXCookbook.Service");

    // Authored service sources only: build output (bin/obj), generated files, and
    // dependency documentation are excluded.
    internal static string[] ServiceSourceFiles() =>
        Directory.GetFiles(ServiceSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    internal static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return string.Join(
            "\n",
            withoutBlocks.Split('\n').Select(line =>
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            }));
    }

    [Fact]
    public void The_comment_stripper_removes_both_comment_forms()
    {
        // POSITIVE CONTROL for every "comment-stripped" claim above.
        const string sample = "int a = 1; // ProcessStartInfo\n/* Process.Start( */ int b = 2;";
        string stripped = StripComments(sample);

        Assert.DoesNotContain("ProcessStartInfo", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start(", stripped, StringComparison.Ordinal);
        Assert.Contains("int b = 2;", stripped, StringComparison.Ordinal);
        Assert.False(FiresOn(sample));

        // ... and it does NOT remove real code.
        Assert.True(FiresOn("var psi = new ProcessStartInfo();"));
        Assert.True(FiresOn("Process.Start(psi);"));
        Assert.True(FiresOn("var p = new Process { StartInfo = psi };"));

        // The permitted read is still permitted.
        Assert.False(FiresOn("using var current = Process.GetCurrentProcess();"));
    }
}
