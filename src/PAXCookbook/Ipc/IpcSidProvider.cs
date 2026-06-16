using System.Security.Principal;
using PAXCookbook.Shared;

namespace PAXCookbook.Ipc;

// Provides the per-user pipe endpoint name (PAXCookbook.<sid>) per
// paxcookbook-ipc-contract.md §4. Production reads the current user's
// SID; tests inject a fixed value to keep pipe names unique.
public interface IIpcEndpointNameProvider
{
    string GetEndpointName();
}

public sealed class WindowsIdentitySidEndpointProvider : IIpcEndpointNameProvider
{
    public string GetEndpointName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new InvalidOperationException("could not resolve current user SID");
        return ProductConstants.PipeNamePrefix + sid;
    }
}

public sealed class FixedEndpointProvider : IIpcEndpointNameProvider
{
    private readonly string _name;
    public FixedEndpointProvider(string name) => _name = name;
    public string GetEndpointName() => _name;
}
