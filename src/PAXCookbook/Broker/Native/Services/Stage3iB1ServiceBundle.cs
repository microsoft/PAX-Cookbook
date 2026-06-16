using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B1 -- service bundle providing the injectable seams for
// the recipe mutation surface. Mirrors the pattern Stage3hServiceBundle
// + Stage3iAServiceBundle established for 3h / 3i-A:
//
//   * Clock         -> overridable wall clock (defaults to UtcNow).
//   * RecipeIdFactory -> overridable ULID generator (defaults to a
//                        Crockford-base32 New-RecipeId port).
//   * Provenance    -> server-stamped paxAdapterVersion +
//                      createdBy block. Pulled from VersionInfo at
//                      production wiring, hand-built in tests.
//
// All members are init-only; bundle is sealed + immutable.
public sealed record Stage3iB1ServiceBundle
{
    public Func<DateTimeOffset>?   Clock           { get; init; }
    public Func<string>?           RecipeIdFactory { get; init; }

    // PS-equivalent of Get-RecipeCreatedByBlock + paxAdapterVersion
    // pulled from $Script:PaxScriptVersion. Native broker reuses
    // VersionInfo (cookbook.version, paxScript.version, channel).
    public string?                 PaxAdapterVersion { get; init; }
    public RecipeCreatedBy?        CreatedByTemplate { get; init; }

    public static Stage3iB1ServiceBundle FromVersionInfo(Models.VersionInfo? versionInfo)
    {
        if (versionInfo is null
            || !versionInfo.IsAvailable
            || versionInfo.BundledPax is null
            || string.IsNullOrWhiteSpace(versionInfo.CookbookVersion)
            || string.IsNullOrWhiteSpace(versionInfo.ReleaseChannel))
        {
            return new Stage3iB1ServiceBundle();
        }
        return new Stage3iB1ServiceBundle
        {
            PaxAdapterVersion = versionInfo.BundledPax.Version,
            CreatedByTemplate = new RecipeCreatedBy(
                CookbookVersion:   versionInfo.CookbookVersion!,
                BundledPaxVersion: versionInfo.BundledPax.Version,
                ReleaseChannel:    versionInfo.ReleaseChannel!),
        };
    }

    // Default ULID factory. Equivalent to New-RecipeId in Recipes.ps1:
    //   * 48-bit ms-since-epoch timestamp (10 chars Crockford base32)
    //   * 80-bit random suffix (16 chars Crockford base32)
    public static string NewRecipeId()
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<char> chars = stackalloc char[26];

        // Timestamp (10 chars, big-endian 5-bit groups).
        for (int i = 9; i >= 0; i--)
        {
            chars[i] = CrockfordBase32.Alphabet[(int)(ms & 0x1F)];
            ms >>= 5;
        }

        // Random suffix (16 chars from 10 bytes, 5 bits each).
        Span<byte> rand = stackalloc byte[10];
        RandomNumberGenerator.Fill(rand);
        long bitBuf = 0;
        int bitCount = 0;
        int outIdx = 10;
        foreach (var b in rand)
        {
            bitBuf = (bitBuf << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                var val = (int)((bitBuf >> bitCount) & 0x1F);
                chars[outIdx++] = CrockfordBase32.Alphabet[val];
            }
        }
        return new string(chars);
    }
}
