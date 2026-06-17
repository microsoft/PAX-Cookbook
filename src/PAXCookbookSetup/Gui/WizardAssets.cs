using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace PAXCookbookSetup.Gui;

// Loads the wizard branding assets that are embedded in the Setup EXE
// (declared in PAXCookbookSetup.csproj) and builds the clickable logo
// controls shown on every wizard / uninstall screen. Every loader degrades
// gracefully to null if the resource is missing or unreadable, so a branding
// hiccup never blocks an install — the wizard simply renders without the image.
internal static class WizardAssets
{
    private const string LogoResource      = "PAXCookbookSetup.Assets.logo-horizontal.png";
    private const string AppIconResource   = "PAXCookbookSetup.Assets.app-icon.png";
    private const string MicrosoftResource = "PAXCookbookSetup.Assets.microsoft-open-source-logo.png";

    // Clickable-logo destinations.
    public const string PaxUrl       = "https://PAXcookbook.com";
    public const string MicrosoftUrl = "https://opensource.microsoft.com";

    public static Image? LoadLogo() => LoadImage(LogoResource);

    public static Image? LoadMicrosoftLogo() => LoadImage(MicrosoftResource);

    // Builds a window/taskbar icon from the embedded square app-icon PNG.
    // GetHicon() allocates an unmanaged icon handle; for a short-lived
    // installer process this is acceptable and the handle is released when
    // the process exits.
    public static Icon? LoadAppIcon()
    {
        try
        {
            using var bmp = LoadImage(AppIconResource) as Bitmap;
            if (bmp is null) return null;
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch { return null; }
    }

    // The PAX Cookbook brand logo as a clickable PictureBox (opens PAXcookbook.com).
    public static PictureBox CreatePaxLogo(Point location, Size size)
        => CreateClickableLogo(LoadLogo(), location, size, PaxUrl, "PAX Cookbook");

    // The Microsoft Open Source logo as a clickable PictureBox (opens opensource.microsoft.com).
    public static PictureBox CreateMicrosoftLogo(Point location, Size size)
        => CreateClickableLogo(LoadMicrosoftLogo(), location, size, MicrosoftUrl, "Microsoft Open Source");

    private static PictureBox CreateClickableLogo(Image? image, Point location, Size size,
                                                  string url, string accessibleName)
    {
        var pb = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = location,
            Size = size,
            Image = image,
            BackColor = Color.Transparent,
            AccessibleName = accessibleName
        };
        // Only behave as a link when the image actually loaded.
        if (image is not null)
        {
            pb.Cursor = Cursors.Hand;
            pb.Click += (_, _) => OpenUrl(url);
        }
        return pb;
    }

    // Opens a URL in the user's default browser. Best-effort; never throws.
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* best effort — a browser hiccup must not affect the installer */ }
    }

    private static Image? LoadImage(string resourceName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            // Copy to a private MemoryStream the Image owns for its lifetime
            // (GDI+ keeps the backing stream open for some operations).
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return Image.FromStream(buffer);
        }
        catch { return null; }
    }
}
