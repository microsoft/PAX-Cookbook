using System;
using System.IO;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// The broker.port coordination anchor must honor the explicit test-only
// local-state-root override so an isolated run can never read, attach to, or
// overwrite the real installation's broker.port. Production (no override) keeps
// the real per-user %LOCALAPPDATA%\PAXCookbook anchor.
[Collection("BrokerDetectionIsolationSerial")]
public sealed class BrokerDetectionIsolationTests : IDisposable
{
    public void Dispose() => BrokerDetection.SetLocalAppDataBaseOverride(null);

    [Fact]
    public void PortFilePath_DefaultsToRealLocalAppData()
    {
        BrokerDetection.SetLocalAppDataBaseOverride(null);
        string real = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(real, "PAXCookbook", "broker.port"), BrokerDetection.PortFilePath());
    }

    [Fact]
    public void PortFilePath_HonorsIsolatedOverride()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "paxbp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            BrokerDetection.SetLocalAppDataBaseOverride(baseDir);
            Assert.Equal(Path.Combine(baseDir, "PAXCookbook", "broker.port"), BrokerDetection.PortFilePath());
        }
        finally
        {
            BrokerDetection.SetLocalAppDataBaseOverride(null);
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void PortFilePath_IgnoresNonAbsoluteOrMissingOverride()
    {
        string real = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string expected = Path.Combine(real, "PAXCookbook", "broker.port");

        BrokerDetection.SetLocalAppDataBaseOverride("relative\\path");
        Assert.Equal(expected, BrokerDetection.PortFilePath());

        BrokerDetection.SetLocalAppDataBaseOverride(Path.Combine(Path.GetTempPath(), "paxbp_missing_" + Guid.NewGuid().ToString("N")));
        Assert.Equal(expected, BrokerDetection.PortFilePath());
    }

    [Fact]
    public void SetOverride_Null_RestoresRealAnchor()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "paxbp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            BrokerDetection.SetLocalAppDataBaseOverride(baseDir);
            Assert.StartsWith(baseDir, BrokerDetection.PortFilePath());
            BrokerDetection.SetLocalAppDataBaseOverride(null);
            string real = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Equal(Path.Combine(real, "PAXCookbook", "broker.port"), BrokerDetection.PortFilePath());
        }
        finally
        {
            BrokerDetection.SetLocalAppDataBaseOverride(null);
            Directory.Delete(baseDir, recursive: true);
        }
    }
}
