using System.Reflection;
using System.Text.Json;
using Xunit;

namespace PAXCookbook.Service.Tests;

public sealed class ServiceContractTests
{
    [Fact]
    public void FixedIdentityValuesAreClosed()
    {
        Assert.Equal(1, ServiceContract.SchemaVersion);
        Assert.Equal("PAXCookbookService", ServiceContract.ServiceName);
        Assert.Equal("PAXCookbook Machine Service", ServiceContract.ServiceDisplayName);
        Assert.Equal("1.0.0", ServiceContract.ServiceVersion);
        Assert.Equal("PAXCookbook", ServiceContract.MachineRootFolderName);
        Assert.Equal("Service", ServiceContract.MachineDataFolderName);
        Assert.Equal("startup-session0-probe", ServiceContract.StartupProbeId);
    }

    [Fact]
    public void FixedLeafNamesHaveNoSeparatorOrTraversal()
    {
        Assert.Equal(5, ServiceContract.FixedLeafFileNames.Count);
        Assert.Equal(
            ServiceContract.FixedLeafFileNames.Count,
            ServiceContract.FixedLeafFileNames.Distinct(StringComparer.Ordinal).Count());

        foreach (var leaf in ServiceContract.FixedLeafFileNames)
        {
            Assert.False(string.IsNullOrWhiteSpace(leaf));
            Assert.DoesNotContain("..", leaf, StringComparison.Ordinal);
            Assert.DoesNotContain("/", leaf, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", leaf, StringComparison.Ordinal);
            Assert.DoesNotContain(":", leaf, StringComparison.Ordinal);
            Assert.Equal(leaf, Path.GetFileName(leaf));
        }
    }

    [Fact]
    public void OwnershipLedgerNameIsReservedButNotUsedByTheWorker()
    {
        Assert.Equal("ownership-ledger.json", ServiceContract.OwnershipLedgerFileName);

        var workerSourceMembers = typeof(StartupProbeWorker)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(m => m.Name)
            .ToArray();

        Assert.DoesNotContain("WriteOwnershipLedger", workerSourceMembers);
    }

    [Fact]
    public void StateAndOutcomeSetsAreBounded()
    {
        Assert.Equal(4, Enum.GetValues<ServiceStateCode>().Length);
        Assert.Equal(2, Enum.GetValues<ProbeOutcomeCode>().Length);

        Assert.Equal("starting", ServiceContract.ToWireValue(ServiceStateCode.Starting));
        Assert.Equal("running", ServiceContract.ToWireValue(ServiceStateCode.Running));
        Assert.Equal("stopped", ServiceContract.ToWireValue(ServiceStateCode.Stopped));
        Assert.Equal("failed", ServiceContract.ToWireValue(ServiceStateCode.Failed));

        Assert.Equal("completed", ServiceContract.ToWireValue(ProbeOutcomeCode.Completed));
        Assert.Equal("failed", ServiceContract.ToWireValue(ProbeOutcomeCode.Failed));
    }

    [Fact]
    public void UnknownStateValuesDegradeToTheBoundedFailureValue()
    {
        Assert.Equal("failed", ServiceContract.ToWireValue((ServiceStateCode)9999));
        Assert.Equal("failed", ServiceContract.ToWireValue((ProbeOutcomeCode)9999));
    }

    [Fact]
    public void StatusDocumentEmitsOnlyPermittedProperties()
    {
        AssertPropertyNames(
            new ServiceStatusDocument(),
            "schemaVersion", "state", "timestampUtc", "sessionId", "userInteractive", "serviceVersion");
    }

    [Fact]
    public void HeartbeatDocumentEmitsOnlyPermittedProperties()
    {
        AssertPropertyNames(
            new ServiceHeartbeatDocument(),
            "schemaVersion", "timestampUtc", "sessionId", "userInteractive", "serviceVersion");
    }

    [Fact]
    public void ProbeResultDocumentEmitsOnlyPermittedProperties()
    {
        AssertPropertyNames(
            new StartupProbeResultDocument(),
            "schemaVersion", "probeId", "outcome", "observedUtc", "sessionId", "userInteractive", "serviceVersion");
    }

    [Fact]
    public void ComposeRootIsPureStringCompositionBeneathTheSuppliedBase()
    {
        var fakeBase = Path.Combine(Path.GetTempPath(), "paxcookbook-service-fake-base");
        var metadataRoot = ServicePaths.ComposeMetadataRoot(fakeBase);

        Assert.Equal(
            Path.Combine(fakeBase, ServiceContract.MachineRootFolderName, ServiceContract.MachineDataFolderName),
            metadataRoot);

        // Pure composition: nothing was created on disk.
        Assert.False(Directory.Exists(metadataRoot));
    }

    [Fact]
    public void ComposeRootRejectsAnEmptyBase()
    {
        Assert.Throws<ArgumentException>(() => ServicePaths.ComposeMetadataRoot(string.Empty));
        Assert.Throws<ArgumentException>(() => ServicePaths.ComposeMetadataRoot("   "));
    }

    // CYCLE 63R (authorized repair). The one-root model is gone: runtime documents
    // bind beneath the RUNTIME root and ownership records bind beneath the METADATA
    // root. Both roots are pinned explicitly so no later reader can confuse them.
    [Fact]
    public void ForRootBindsEveryFixedLeafBeneathTheSuppliedRoot()
    {
        var metadataRoot = Path.Combine(
            Path.GetTempPath(), "paxcookbook-service-bind-" + Guid.NewGuid().ToString("N"));
        var paths = ServicePaths.ForMetadataRoot(metadataRoot);

        Assert.Equal(Path.GetFullPath(metadataRoot), paths.MetadataRoot);
        Assert.Equal(
            Path.Combine(paths.MetadataRoot, ServiceContract.RuntimeDataFolderName),
            paths.Root);
        Assert.NotEqual(paths.MetadataRoot, paths.Root);

        // Runtime documents: beneath the RUNTIME root, never the metadata parent.
        Assert.Equal(Path.Combine(paths.Root, ServiceContract.StatusFileName), paths.StatusFile);
        Assert.Equal(Path.Combine(paths.Root, ServiceContract.HeartbeatFileName), paths.HeartbeatFile);
        Assert.Equal(Path.Combine(paths.Root, ServiceContract.ProbeRequestFileName), paths.ProbeRequestFile);
        Assert.Equal(Path.Combine(paths.Root, ServiceContract.ProbeResultFileName), paths.ProbeResultFile);

        // Ownership record: beneath the METADATA root, and provably NOT under runtime.
        Assert.Equal(
            Path.Combine(paths.MetadataRoot, ServiceContract.OwnershipLedgerFileName),
            paths.OwnershipLedgerFile);
        Assert.False(
            paths.OwnershipLedgerFile.StartsWith(
                paths.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        Assert.False(Directory.Exists(paths.MetadataRoot));
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public void ForRootRejectsAnEmptyRoot()
    {
        Assert.Throws<ArgumentException>(() => ServicePaths.ForMetadataRoot(string.Empty));
        Assert.Throws<ArgumentException>(() => ServicePaths.ForMetadataRoot("   "));
    }

    [Fact]
    public void ServicePathsNeverResolvesAnEnvironmentRootItself()
    {
        var members = typeof(ServicePaths)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(m => m.Name)
            .Concat(typeof(StartupProbeWorker)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(m => m.Name))
            .ToArray();

        Assert.DoesNotContain("ResolveProductionRoot", members);
        Assert.DoesNotContain("CreateProduction", members);
    }

    [Fact]
    public void TheProductionRootResolverIsPrivateToTheCompositionRoot()
    {
        var resolver = typeof(Program).GetMethod(
            "ResolveProductionRoot",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(resolver);

        // Private, so no test can call it in source even with InternalsVisibleTo.
        // It is deliberately never invoked here.
        Assert.True(resolver!.IsPrivate);
        Assert.Empty(resolver.GetParameters());
    }

    private static void AssertPropertyNames<T>(T document, params string[] expected)
    {
        var json = JsonSerializer.Serialize(document, ServiceContract.JsonOptions);
        using var parsed = JsonDocument.Parse(json);

        var actual = parsed.RootElement
            .EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            actual);
    }
}
