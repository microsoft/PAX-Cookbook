using System;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Source-tree engine-immutability tripwire.
//
// Replaces the 14 removed NativeBrokerHostStage3* PaxScriptBaselineHash pins
// (formerly in tests/PAXCookbook.Tests, deleted along with the superseded
// native broker library) as the `dotnet test`-time check that the bundled PAX
// engine script has not been mutated in the source tree. The ship path is
// independently guarded by Build-Setup.ps1's $expectedEngineSha, which
// re-hashes the STAGED engine at build time and refuses to package a mismatch;
// this test restores the earlier, faster tripwire that fires during a bare
// `dotnet test`, before any build, so an accidental engine edit is caught early.
//
// A deliberate engine ingestion must update ExpectedEngineSha256 here in the
// same change that updates Build-Setup.ps1 ($expectedEngineSha), versions.json
// (current.engine.sha256), app/VERSION.json, and app/resources/manifest.json.
public sealed class EngineSourceTreeImmutabilityTests
{
    // Mirrors Build-Setup.ps1 $expectedEngineSha and versions.json
    // current.engine.sha256 (PAX engine v1.11.14). Compared case-insensitively.
    private const string ExpectedEngineSha256 =
        "99AB97232C76022771197B84AF880ED48D9E4B83F678F05262C38A223D3814C9";

    // Repo-relative path to the canonical bundled engine script.
    private const string EngineRelativePath =
        "app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1";

    // Test-only override so the tripwire can be exercised against a mutated
    // scratch copy without ever touching the tracked engine file. Unset in
    // normal CI / dev runs, where the real source-tree engine is hashed.
    private const string EnginePathOverrideEnvVar = "PAX_ENGINE_PATH_OVERRIDE";

    [Fact]
    public void EngineSourceTreeImmutabilityTest()
    {
        string enginePath = ResolveEnginePath();
        Assert.True(File.Exists(enginePath), $"PAX engine script not found at: {enginePath}");

        string actual = ComputeSha256(enginePath);
        Assert.Equal(ExpectedEngineSha256, actual, ignoreCase: true);
    }

    private static string ResolveEnginePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(EnginePathOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return Path.Combine(
            Phase5Fixture.RepoRoot(),
            EngineRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
