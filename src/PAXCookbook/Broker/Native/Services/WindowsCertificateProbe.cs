using System.Security.Cryptography.X509Certificates;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- production X.509 store probe used by the auth-
// profile structural-test surface. Opens the named store in
// ReadOnly mode, scans for an exact-match thumbprint, and reports
// presence. Never returns the certificate itself -- the caller does
// not need it because the test is purely structural.
public sealed class WindowsCertificateProbe : ICertificateProbe
{
    public bool Locate(string thumbprint, string store)
    {
        var (location, storeName) = ParseStoreSpec(store);
        using var x509 = new X509Store(storeName, location);
        x509.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        try
        {
            var found = x509.Certificates.Find(
                X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            return found.Count > 0;
        }
        finally
        {
            x509.Close();
        }
    }

    private static (StoreLocation Location, string Name) ParseStoreSpec(string spec)
    {
        if (string.IsNullOrEmpty(spec))
            throw new ArgumentException("certificate store specifier is required", nameof(spec));
        var ix = spec.IndexOf('\\');
        if (ix <= 0 || ix == spec.Length - 1)
            throw new ArgumentException(
                "certificate store specifier must be of the form 'LocalMachine\\<name>' or 'CurrentUser\\<name>'",
                nameof(spec));
        var loc  = spec.Substring(0, ix);
        var name = spec.Substring(ix + 1);
        var parsedLocation = loc switch
        {
            "LocalMachine" => StoreLocation.LocalMachine,
            "CurrentUser"  => StoreLocation.CurrentUser,
            _              => throw new ArgumentException(
                "certificate store location must be 'LocalMachine' or 'CurrentUser'",
                nameof(spec)),
        };
        return (parsedLocation, name);
    }
}
