using System.Diagnostics;

namespace PAXCookbookSetup;

// Injectable launcher. Production uses Process.Start; tests capture
// invocations without spawning real processes.
public interface IProcessLauncher
{
    // Returns the started Process (null in test impls that record only).
    Process? Start(string fileName, IList<string> arguments);

    // Last invocation seen (test helper; production may return null).
    LaunchRecord? Last { get; }
}

public sealed record LaunchRecord(string FileName, IReadOnlyList<string> Arguments);

public sealed class RealProcessLauncher : IProcessLauncher
{
    public LaunchRecord? Last { get; private set; }
    public Process? Start(string fileName, IList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            // The self-handoff relaunches the framework-dependent Setup via the
            // console host dotnet.exe. CreateNoWindow=true suppresses its blank
            // console window so uninstall/repair/upgrade run silently; a WinForms
            // uninstall dialog (shown by the child) is unaffected.
            CreateNoWindow = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        Last = new LaunchRecord(fileName, arguments.ToList());
        return Process.Start(psi);
    }
}

public sealed class RecordingProcessLauncher : IProcessLauncher
{
    public LaunchRecord? Last { get; private set; }
    public Process? Start(string fileName, IList<string> arguments)
    {
        Last = new LaunchRecord(fileName, arguments.ToList());
        return null;
    }
}
