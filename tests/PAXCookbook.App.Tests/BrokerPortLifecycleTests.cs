using System;
using System.IO;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle-29 broker.port lifecycle proof.
//
// Cycle 28 observed that lad\PAXCookbook\broker.port was REMOVED between the
// attended baseline and the post-run inventory, and deliberately left that
// UNCLASSIFIED because a removal is a more suspicious signal than an addition.
// These tests decide it on evidence rather than on plausibility: they exercise
// the real acquire/release/stale-cleanup paths against an isolated temp root.
public sealed class BrokerPortLifecycleTests : IDisposable
{
    private readonly string _root;

    public BrokerPortLifecycleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pax_c29_port_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BrokerDetection.SetLocalAppDataBaseOverride(_root);
    }

    public void Dispose()
    {
        BrokerDetection.SetLocalAppDataBaseOverride(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void OwnerAcquiresThePortFile_UnderTheIsolatedRoot()
    {
        string path = BrokerDetection.PortFilePath();
        Assert.StartsWith(_root, path, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));

        FileStream? held = BrokerDetection.AcquirePortFile(51234);
        try
        {
            Assert.NotNull(held);
            Assert.True(File.Exists(path));
        }
        finally
        {
            BrokerDetection.ReleasePortFile(held);
        }
    }

    [Fact]
    public void CleanOwnershipRelease_ClosesTheHandleAndRemovesTheOwnedFile()
    {
        string path = BrokerDetection.PortFilePath();
        FileStream? held = BrokerDetection.AcquirePortFile(51235);
        Assert.NotNull(held);
        Assert.True(File.Exists(path));

        BrokerDetection.ReleasePortFile(held);

        // THE CYCLE-28 FINDING C BEHAVIOUR: a clean release deliberately removes
        // the anchor so a dead port is never left advertised.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ANonOwnerCannotDeleteALiveOwnersPortFile()
    {
        string path = BrokerDetection.PortFilePath();
        FileStream? held = BrokerDetection.AcquirePortFile(51236);
        try
        {
            Assert.NotNull(held);

            // The live owner holds an exclusive write lock, so a stale-cleanup
            // attempt by anyone else must fail closed and leave the file intact.
            bool deleted = BrokerDetection.TryDeleteStalePortFile();

            Assert.False(deleted);
            Assert.True(File.Exists(path));
        }
        finally
        {
            BrokerDetection.ReleasePortFile(held);
        }
    }

    [Fact]
    public void StaleCleanupRemainsSeparatelyBounded_AndRemovesOnlyAnUnownedFile()
    {
        string path = BrokerDetection.PortFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A stale file with no live owner: no handle is held.
        File.WriteAllText(path, "51237");
        Assert.True(File.Exists(path));

        Assert.True(BrokerDetection.TryDeleteStalePortFile());
        Assert.False(File.Exists(path));

        // Bounded: with nothing to clean it reports false rather than throwing.
        Assert.False(BrokerDetection.TryDeleteStalePortFile());
    }
}
