using System.Security.Cryptography;

namespace PAXCookbook.Shared.Hashing;

public static class Sha256Hash
{
    public static string OfFile(string path)
    {
        using var s = File.OpenRead(path);
        return OfStream(s);
    }

    public static string OfStream(Stream stream)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes); // upper-case hex
    }

    public static string OfBytes(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    public static bool Equal(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
