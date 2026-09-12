using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PAXCookbook.Service;

/// <summary>
/// Composition root. This is the ONLY code in the product that reads
/// <see cref="Environment.SpecialFolder.CommonApplicationData"/>, and the
/// resolver is private, so the test assembly cannot call it even though
/// internals are visible to it.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static void Main(string[] args)
    {
        var builder = new HostBuilder();

        // The service name and display name are applied at SCM registration time
        // from the fixed values in ServiceContract; the lifetime itself needs no
        // caller-supplied option here.
        builder.UseWindowsService();

        // UseWindowsService registers an EventLog logger. Creating a missing event
        // source requires administrator rights and can fail at Session 0 startup
        // under a virtual account, so every provider is removed. This also keeps
        // all output bounded to the contract documents.
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureServices(services =>
            services.AddHostedService(provider =>
                new StartupProbeWorker(
                    ServicePaths.ForMetadataRoot(ResolveProductionRoot()),
                    HeartbeatInterval,
                    provider.GetRequiredService<IHostApplicationLifetime>())));

        using var host = builder.Build();
        host.Run();
    }

    // Resolves the machine METADATA root. The name is retained because it is the
    // pinned composition-root resolver; the runtime root is derived from it by
    // ServicePaths and is never resolved here.
    private static string ResolveProductionRoot() =>
        ServicePaths.ComposeMetadataRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
}
