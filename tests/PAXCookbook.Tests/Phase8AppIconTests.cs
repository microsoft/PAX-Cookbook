using System.Reflection;
using Xunit;

namespace PAXCookbook.Tests;

// Phase 8 — embedded app icon foundation.
public class Phase8AppIconTests
{
    [Fact]
    public void EmbeddedAppIcon_IsPresentInAssembly()
    {
        var asm = typeof(PAXCookbook.Commands.OpenCommand).Assembly;
        using var s = asm.GetManifestResourceStream("PAXCookbook.Resources.PAXCookbook.ico");
        Assert.NotNull(s);
        Assert.True(s!.Length > 1000, "icon resource should not be empty");
    }

    [Fact]
    public void EmbeddedAppIcon_HasValidIcoHeader_AndAtLeastSevenSizes()
    {
        var asm = typeof(PAXCookbook.Commands.OpenCommand).Assembly;
        using var s = asm.GetManifestResourceStream("PAXCookbook.Resources.PAXCookbook.ico")!;
        var hdr = new byte[6];
        Assert.Equal(6, s.Read(hdr, 0, 6));
        // ICONDIR header: reserved=0, type=1 (icon), count.
        Assert.Equal(0, hdr[0]);
        Assert.Equal(0, hdr[1]);
        Assert.Equal(1, hdr[2]);
        Assert.Equal(0, hdr[3]);
        var count = hdr[4] | (hdr[5] << 8);
        Assert.True(count >= 7, $"icon should contain >=7 sizes (got {count})");
    }
}
