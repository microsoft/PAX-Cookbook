using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace PAXCookbook.App;

// Live taskbar-icon diagnostic. This is a command-line-only diagnostic seam
// (driven by --test-seam-icon-diagnostics, never by the desktop launcher) that
// answers a single question with evidence instead of speculation: what icon
// source does Windows actually consume for this application's top-level window?
//
// It opens a real native top-level window whose icon wiring is IDENTICAL to the
// product shell (WebViewShell.Run): the same AppUserModelID is set before the
// window exists, the same multi-resolution .ico is assigned to Form.Icon, and
// the same Win32 LoadImage handles are realized at the system large- and
// small-icon metrics and pushed via WM_SETICON. The only thing it omits is the
// embedded WebView2 child control, which has no bearing on the top-level
// window's icon handles, window class, or shell identity. It then reads back,
// from the live HWND, exactly what Windows holds: the WM_GETICON handles, the
// class-level icons, the window-level AppUserModelID, the executable's
// associated/embedded icons, the system-tray icon, and the on-disk shortcut /
// pin inventory. Every captured handle is rasterized to a PNG and hashed so the
// wrong taskbar image can be matched against a concrete source.
//
// The diagnostic never acquires the PAX engine, never performs a WebAuthn
// ceremony, never starts a cook, and never mutates any shortcut or pin it
// inspects. It writes all artifacts under the caller-supplied output folder and
// exits.
internal static class IconDiagnostics
{
    private const int WM_SETICON = 0x0080;
    private const int WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTCOLOR = 0x0000;

    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appID);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, nIndex) : new IntPtr(GetClassLong32(hWnd, nIndex));

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("propsys.dll", PreserveSig = false)]
    private static extern void PropVariantToStringAlloc(ref PropVariant pv, out IntPtr ppszOut);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr P;
        public IntPtr P2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    private static readonly PropertyKey PKEY_AppUserModel_ID = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5,
    };

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    // Entry point. iconPath is the bundled .ico the product shell uses; exePath
    // is the running executable (apphost) whose embedded icon the shell may use;
    // aumid is the AppUserModelID to establish (product or diagnostic override);
    // outDir is where all artifacts are written.
    public static void Run(string iconPath, string exePath, string aumid, string outDir)
    {
        Directory.CreateDirectory(outDir);

        // Mirror the product shell: establish taskbar identity BEFORE any window
        // is created so the captured window-level identity reflects production.
        try
        {
            SetCurrentProcessExplicitAppUserModelID(aumid);
        }
        catch
        {
            // Identity is a taskbar nicety, not a launch prerequisite.
        }

        ApplicationConfiguration.Initialize();

        IntPtr hBigIcon = IntPtr.Zero;
        IntPtr hSmallIcon = IntPtr.Zero;
        Icon? formIcon = null;
        Icon? bigIcon = null;
        Icon? smallIcon = null;
        Icon? trayIcon = null;
        NotifyIcon? tray = null;

        using var form = new Form
        {
            Text = "PAX Cookbook",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(1280, 860),
            MinimumSize = new Size(880, 600),
        };

        // Icon wiring identical to WebViewShell.Run: multi-resolution managed
        // Form.Icon plus Win32 LoadImage handles realized at the system large-
        // and small-icon metrics, pushed via WM_SETICON on handle-create and on
        // show. This is what makes the captured WM_GETICON values represent the
        // exact image the product window hands to the taskbar.
        Size smallSize = SystemInformation.SmallIconSize;
        Size bigSize = SystemInformation.IconSize;
        string formIconSource = "(none)";
        string trayIconSource = "(none)";
        if (File.Exists(iconPath))
        {
            try
            {
                formIcon = new Icon(iconPath);
                form.Icon = formIcon;
                formIconSource = iconPath;

                hBigIcon = LoadImage(
                    IntPtr.Zero, iconPath, IMAGE_ICON, bigSize.Width, bigSize.Height,
                    LR_LOADFROMFILE | LR_DEFAULTCOLOR);
                hSmallIcon = LoadImage(
                    IntPtr.Zero, iconPath, IMAGE_ICON, smallSize.Width, smallSize.Height,
                    LR_LOADFROMFILE | LR_DEFAULTCOLOR);
                if (hBigIcon != IntPtr.Zero)
                {
                    bigIcon = Icon.FromHandle(hBigIcon);
                }
                if (hSmallIcon != IntPtr.Zero)
                {
                    smallIcon = Icon.FromHandle(hSmallIcon);
                }
            }
            catch
            {
                // Fall through with whatever loaded; capture records the gaps.
            }
        }

        void ApplyWindowIcons()
        {
            if (!form.IsHandleCreated)
            {
                return;
            }
            if (hBigIcon != IntPtr.Zero)
            {
                SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_BIG, hBigIcon);
            }
            if (hSmallIcon != IntPtr.Zero)
            {
                SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_SMALL, hSmallIcon);
            }
        }

        form.HandleCreated += (_, _) => ApplyWindowIcons();
        form.Shown += (_, _) => ApplyWindowIcons();

        // System-tray icon, sourced exactly as the product shell sources it
        // (dedicated small frame from the same .ico), so the tray PNG in the
        // contact sheet is the known-correct reference image.
        if (File.Exists(iconPath))
        {
            try
            {
                trayIcon = new Icon(iconPath, smallSize.Width, smallSize.Height);
                trayIconSource = iconPath;
            }
            catch
            {
                trayIcon = null;
            }
        }
        tray = new NotifyIcon
        {
            Text = "PAX Cookbook",
            Icon = trayIcon ?? form.Icon,
            Visible = true,
        };

        // Capture once, after the window has had a moment to settle, then close.
        var captureTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        captureTimer.Tick += (_, _) =>
        {
            captureTimer.Stop();
            captureTimer.Dispose();
            try
            {
                Capture(
                    form, tray, iconPath, exePath, aumid,
                    formIconSource, trayIconSource,
                    hBigIcon, hSmallIcon, bigSize, smallSize, outDir);
            }
            finally
            {
                form.Close();
            }
        };
        form.Shown += (_, _) => captureTimer.Start();

        Application.Run(form);

        // Teardown. The window-owned WM_SETICON handles (hBigIcon/hSmallIcon)
        // are owned here because Icon.FromHandle does not take ownership.
        try { tray.Visible = false; tray.Dispose(); } catch { }
        trayIcon?.Dispose();
        bigIcon?.Dispose();
        smallIcon?.Dispose();
        formIcon?.Dispose();
        if (hBigIcon != IntPtr.Zero) { DestroyIcon(hBigIcon); }
        if (hSmallIcon != IntPtr.Zero) { DestroyIcon(hSmallIcon); }
    }

    private static void Capture(
        Form form, NotifyIcon tray, string iconPath, string exePath, string processAumid,
        string formIconSource, string trayIconSource,
        IntPtr hBigIcon, IntPtr hSmallIcon, Size bigSize, Size smallSize, string outDir)
    {
        IntPtr hwnd = form.Handle;

        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        var windowTitle = new StringBuilder(512);
        GetWindowText(hwnd, windowTitle, windowTitle.Capacity);
        GetWindowThreadProcessId(hwnd, out uint pid);

        var observations = new List<string>();
        var renderedCells = new List<(string Label, string? PngPath)>();

        // --- Window icons via WM_GETICON ---
        IconCapture wmSmall = CaptureSendIcon(hwnd, ICON_SMALL, outDir, "wm_geticon_small.png");
        IconCapture wmBig = CaptureSendIcon(hwnd, ICON_BIG, outDir, "wm_geticon_big.png");
        IconCapture wmSmall2 = CaptureSendIcon(hwnd, ICON_SMALL2, outDir, "wm_geticon_small2.png");

        // --- Class icons via GetClassLongPtr ---
        IconCapture classHicon = CaptureClassIcon(hwnd, GCLP_HICON, outDir, "class_hicon.png");
        IconCapture classHiconSm = CaptureClassIcon(hwnd, GCLP_HICONSM, outDir, "class_hiconsm.png");

        // --- Tray icon (known-correct reference) ---
        IconCapture trayCapture = CaptureManagedIcon(tray.Icon, outDir, "tray_icon.png", trayIconSource);

        // --- Executable associated icon (what the shell extracts) ---
        IconCapture exeAssociated = CaptureAssociatedIcon(exePath, outDir, "exe_associated_icon.png");

        // --- Executable embedded icons via ExtractIconEx (apphost RT_GROUP_ICON) ---
        IconCapture exeEmbeddedLarge = CaptureExtractedExeIcon(exePath, large: true, outDir, "exe_embedded_large.png");
        IconCapture exeEmbeddedSmall = CaptureExtractedExeIcon(exePath, large: false, outDir, "exe_embedded_small.png");

        // --- Selected .ico frames (16/32/48) realized exactly as the shell would ---
        IconCapture ico16 = CaptureLoadImageFrame(iconPath, 16, outDir, "ico_frame_16.png");
        IconCapture ico32 = CaptureLoadImageFrame(iconPath, 32, outDir, "ico_frame_32.png");
        IconCapture ico48 = CaptureLoadImageFrame(iconPath, 48, outDir, "ico_frame_48.png");

        // --- Window-level AppUserModelID via SHGetPropertyStoreForWindow ---
        string? windowAumid = null;
        string? windowAumidError = null;
        try
        {
            Guid iid = IID_IPropertyStore;
            int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore store);
            if (hr == 0 && store is not null)
            {
                windowAumid = ReadStringProperty(store, PKEY_AppUserModel_ID);
                Marshal.ReleaseComObject(store);
            }
            else
            {
                windowAumidError = $"hr=0x{hr:X8}";
            }
        }
        catch (Exception ex)
        {
            windowAumidError = ex.Message;
        }

        // --- Shortcut / pin inventory (read-only) ---
        List<ShortcutRecord> shortcuts = InventoryShortcuts();
        string shortcutInventoryPath = Path.Combine(outDir, "shortcut_inventory.json");
        File.WriteAllText(
            shortcutInventoryPath,
            JsonSerializer.Serialize(shortcuts, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        // --- Automated observations ---
        observations.Add(wmBig.HandleValue != "0x0"
            ? "WM_GETICON ICON_BIG returned a non-null handle (the window has a large icon)."
            : "WM_GETICON ICON_BIG returned NULL — the taskbar has no window large icon to use.");
        observations.Add(wmSmall.HandleValue != "0x0"
            ? "WM_GETICON ICON_SMALL returned a non-null handle."
            : "WM_GETICON ICON_SMALL returned NULL.");
        observations.Add(classHicon.HandleValue != "0x0"
            ? "Class GCLP_HICON is set — the taskbar may fall back to this class icon."
            : "Class GCLP_HICON is NULL — no class-level large icon fallback exists.");
        observations.Add(classHiconSm.HandleValue != "0x0"
            ? "Class GCLP_HICONSM is set."
            : "Class GCLP_HICONSM is NULL.");
        observations.Add(string.Equals(windowAumid, processAumid, StringComparison.OrdinalIgnoreCase)
            ? $"Window-level AppUserModelID matches the process AUMID ('{processAumid}')."
            : $"Window-level AppUserModelID ('{windowAumid ?? "null"}') does NOT match the process AUMID ('{processAumid}').");

        AddHashMatch(observations, "taskbar large (WM_GETICON ICON_BIG)", wmBig, "the dedicated .ico 32px frame", ico32);
        AddHashMatch(observations, "taskbar large (WM_GETICON ICON_BIG)", wmBig, "the executable embedded large icon", exeEmbeddedLarge);
        AddHashMatch(observations, "taskbar large (WM_GETICON ICON_BIG)", wmBig, "the executable associated icon", exeAssociated);
        AddHashMatch(observations, "tray", trayCapture, "WM_GETICON ICON_SMALL", wmSmall);

        // --- Contact sheet ---
        renderedCells.Add(("tray (reference)", trayCapture.PngPath));
        renderedCells.Add(("WM_GETICON small", wmSmall.PngPath));
        renderedCells.Add(("WM_GETICON big", wmBig.PngPath));
        renderedCells.Add(("WM_GETICON small2", wmSmall2.PngPath));
        renderedCells.Add(("class HICON", classHicon.PngPath));
        renderedCells.Add(("class HICONSM", classHiconSm.PngPath));
        renderedCells.Add(("exe associated", exeAssociated.PngPath));
        renderedCells.Add(("exe embedded large", exeEmbeddedLarge.PngPath));
        renderedCells.Add(("exe embedded small", exeEmbeddedSmall.PngPath));
        renderedCells.Add((".ico 16", ico16.PngPath));
        renderedCells.Add((".ico 32", ico32.PngPath));
        renderedCells.Add((".ico 48", ico48.PngPath));
        string contactSheetPath = Path.Combine(outDir, "icon-contact-sheet.png");
        BuildContactSheet(renderedCells, contactSheetPath);

        // --- JSON report ---
        var report = new
        {
            capturedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            identity = new
            {
                processPath = exePath,
                commandLine = Environment.CommandLine,
                processId = pid,
                hwnd = "0x" + hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                className = className.ToString(),
                windowTitle = windowTitle.ToString(),
                showInTaskbar = form.ShowInTaskbar,
                processAumid,
                windowAumid,
                windowAumidError,
                systemIconMetric = $"{bigSize.Width}x{bigSize.Height}",
                systemSmallIconMetric = $"{smallSize.Width}x{smallSize.Height}",
            },
            windowIcons = new
            {
                wmGetIconSmall = wmSmall,
                wmGetIconBig = wmBig,
                wmGetIconSmall2 = wmSmall2,
            },
            classIcons = new
            {
                gclpHicon = classHicon,
                gclpHiconSm = classHiconSm,
            },
            sources = new
            {
                formIconSource,
                trayIconSource,
                tray = trayCapture,
                exeAssociated,
                exeEmbeddedLarge,
                exeEmbeddedSmall,
                icoFrame16 = ico16,
                icoFrame32 = ico32,
                icoFrame48 = ico48,
            },
            shortcutInventoryPath,
            shortcutCount = shortcuts.Count,
            contactSheetPath,
            observations,
        };

        string jsonPath = Path.Combine(outDir, "icon-diagnostics.json");
        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        Console.WriteLine($"X16C_ICON_DIAGNOSTICS_JSON={jsonPath}");
        Console.WriteLine($"X16C_ICON_DIAGNOSTICS_CONTACT_SHEET={contactSheetPath}");
        Console.WriteLine($"X16C_ICON_DIAGNOSTICS_SHORTCUTS={shortcutInventoryPath}");
        Console.WriteLine($"X16C_ICON_DIAGNOSTICS_HWND={report.identity.hwnd}");
        Console.WriteLine($"X16C_ICON_DIAGNOSTICS_CLASS={className}");
        Console.WriteLine("X16C_ICON_DIAGNOSTICS_DONE=1");
    }

    private sealed class IconCapture
    {
        public string Source { get; set; } = string.Empty;
        public string HandleValue { get; set; } = "0x0";
        public bool Present { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? PngPath { get; set; }
        public string? Sha256 { get; set; }
        public string? Error { get; set; }
    }

    private sealed class ShortcutRecord
    {
        public string Location { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? TargetPath { get; set; }
        public string? Arguments { get; set; }
        public string? IconLocation { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? AppUserModelId { get; set; }
        public string? LastWriteTimeUtc { get; set; }
        public string? Error { get; set; }
    }

    private static IconCapture CaptureSendIcon(IntPtr hwnd, int iconType, string outDir, string fileName)
    {
        var cap = new IconCapture { Source = $"WM_GETICON({iconType})" };
        try
        {
            IntPtr h = SendMessage(hwnd, WM_GETICON, (IntPtr)iconType, IntPtr.Zero);
            cap.HandleValue = "0x" + h.ToInt64().ToString("X", CultureInfo.InvariantCulture);
            if (h != IntPtr.Zero)
            {
                SaveHandleIcon(h, ownsHandle: false, outDir, fileName, cap);
            }
        }
        catch (Exception ex)
        {
            cap.Error = ex.Message;
        }
        return cap;
    }

    private static IconCapture CaptureClassIcon(IntPtr hwnd, int index, string outDir, string fileName)
    {
        var cap = new IconCapture { Source = $"GetClassLongPtr({index})" };
        try
        {
            IntPtr h = GetClassLongPtr(hwnd, index);
            cap.HandleValue = "0x" + h.ToInt64().ToString("X", CultureInfo.InvariantCulture);
            if (h != IntPtr.Zero)
            {
                SaveHandleIcon(h, ownsHandle: false, outDir, fileName, cap);
            }
        }
        catch (Exception ex)
        {
            cap.Error = ex.Message;
        }
        return cap;
    }

    private static IconCapture CaptureManagedIcon(Icon? icon, string outDir, string fileName, string source)
    {
        var cap = new IconCapture { Source = source };
        if (icon is null)
        {
            cap.Error = "icon is null";
            return cap;
        }
        try
        {
            using Bitmap bmp = icon.ToBitmap();
            string path = Path.Combine(outDir, fileName);
            bmp.Save(path, ImageFormat.Png);
            cap.Present = true;
            cap.Width = bmp.Width;
            cap.Height = bmp.Height;
            cap.PngPath = path;
            cap.Sha256 = HashFile(path);
        }
        catch (Exception ex)
        {
            cap.Error = ex.Message;
        }
        return cap;
    }

    private static IconCapture CaptureAssociatedIcon(string exePath, string outDir, string fileName)
    {
        var cap = new IconCapture { Source = $"ExtractAssociatedIcon({exePath})" };
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null)
            {
                cap.Error = "ExtractAssociatedIcon returned null";
                return cap;
            }
            using Bitmap bmp = icon.ToBitmap();
            string path = Path.Combine(outDir, fileName);
            bmp.Save(path, ImageFormat.Png);
            cap.Present = true;
            cap.Width = bmp.Width;
            cap.Height = bmp.Height;
            cap.PngPath = path;
            cap.Sha256 = HashFile(path);
        }
        catch (Exception ex)
        {
            cap.Error = ex.Message;
        }
        return cap;
    }

    private static IconCapture CaptureExtractedExeIcon(string exePath, bool large, string outDir, string fileName)
    {
        var cap = new IconCapture { Source = $"ExtractIconEx({exePath}, {(large ? "large" : "small")})" };
        var handles = new IntPtr[1];
        try
        {
            uint n = large
                ? ExtractIconEx(exePath, 0, handles, null, 1)
                : ExtractIconEx(exePath, 0, null, handles, 1);
            IntPtr h = handles[0];
            cap.HandleValue = "0x" + h.ToInt64().ToString("X", CultureInfo.InvariantCulture);
            if (h != IntPtr.Zero)
            {
                SaveHandleIcon(h, ownsHandle: true, outDir, fileName, cap);
            }
            else
            {
                cap.Error = $"ExtractIconEx returned {n} icons, handle null";
            }
        }
        catch (Exception ex)
        {
            cap.Error = ex.Message;
        }
        return cap;
    }

    private static IconCapture CaptureLoadImageFrame(string iconPath, int size, string outDir, string fileName)
    {
        var cap = new IconCapture { Source = $"LoadImage({iconPath}, {size}px)" };
        if (!File.Exists(iconPath))
        {
            cap.Error = "icon file not found";
            return cap;
        }
        IntPtr h = IntPtr.Zero;
        try
        {
            h = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE | LR_DEFAULTCOLOR);
            cap.HandleValue = "0x" + h.ToInt64().ToString("X", CultureInfo.InvariantCulture);
            if (h != IntPtr.Zero)
            {
                SaveHandleIcon(h, ownsHandle: true, outDir, fileName, cap);
            }
            else
            {
                cap.Error = "LoadImage returned null";
            }
        }
        catch (Exception ex)
        {
            cap.Error = ex.Message;
        }
        return cap;
    }

    private static void SaveHandleIcon(IntPtr hIcon, bool ownsHandle, string outDir, string fileName, IconCapture cap)
    {
        try
        {
            using (Icon icon = Icon.FromHandle(hIcon))
            using (Bitmap bmp = icon.ToBitmap())
            {
                string path = Path.Combine(outDir, fileName);
                bmp.Save(path, ImageFormat.Png);
                cap.Present = true;
                cap.Width = bmp.Width;
                cap.Height = bmp.Height;
                cap.PngPath = path;
                cap.Sha256 = HashFile(path);
            }
        }
        catch (Exception ex)
        {
            cap.Error = ex.Message;
        }
        finally
        {
            if (ownsHandle && hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
            }
        }
    }

    private static string? ReadStringProperty(IPropertyStore store, PropertyKey key)
    {
        PropVariant pv = default;
        IntPtr psz = IntPtr.Zero;
        try
        {
            store.GetValue(ref key, out pv);
            if (pv.Vt == 0) // VT_EMPTY
            {
                return null;
            }
            PropVariantToStringAlloc(ref pv, out psz);
            return psz != IntPtr.Zero ? Marshal.PtrToStringUni(psz) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (psz != IntPtr.Zero)
            {
                CoTaskMemFree(psz);
            }
            try { PropVariantClear(ref pv); } catch { }
        }
    }

    private static List<ShortcutRecord> InventoryShortcuts()
    {
        var records = new List<ShortcutRecord>();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var roots = new (string Location, string Path, bool Recursive)[]
        {
            ("start-menu-user", Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
            ("start-menu-common", Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true),
            ("desktop-user", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            ("desktop-common", Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
            ("taskbar-pinned", Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false),
            ("implicit-app-shortcuts", Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\ImplicitAppShortcuts"), true),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root.Path) || !Directory.Exists(root.Path))
            {
                continue;
            }
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(
                    root.Path, "*.lnk",
                    root.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }
            foreach (string lnk in links)
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                if (name.IndexOf("PAX Cookbook", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("PAXCookbook", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Still include pins whose target is the app exe even if the
                    // pin name was renamed; resolve target below and re-check.
                }
                records.Add(ReadShortcut(root.Location, lnk));
            }
        }

        // Drop records that are neither named for PAX Cookbook nor target the
        // app exe, so the inventory stays focused on this product.
        return records
            .Where(r =>
                r.Name.IndexOf("PAX Cookbook", StringComparison.OrdinalIgnoreCase) >= 0 ||
                r.Name.IndexOf("PAXCookbook", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (r.TargetPath?.IndexOf("PAX Cookbook", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (r.TargetPath?.IndexOf("PAXCookbook", StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();
    }

    private static ShortcutRecord ReadShortcut(string location, string lnkPath)
    {
        var record = new ShortcutRecord
        {
            Location = location,
            Path = lnkPath,
            Name = Path.GetFileNameWithoutExtension(lnkPath),
        };
        try
        {
            record.LastWriteTimeUtc = File.GetLastWriteTimeUtc(lnkPath).ToString("o", CultureInfo.InvariantCulture);
        }
        catch
        {
            // best-effort
        }

        // Target / arguments / icon location via IShellLinkW (read-only; no
        // Windows Script Host — WDAC-safe).
        try
        {
            var link = ShellLinkReader.Read(lnkPath);
            if (link is not null)
            {
                record.TargetPath = link.Target;
                record.Arguments = link.Arguments;
                record.IconLocation = link.IconLocation;
                record.WorkingDirectory = link.WorkingDirectory;
            }
        }
        catch (Exception ex)
        {
            record.Error = ex.Message;
        }

        // Window/shortcut AppUserModelID via IPropertyStore (read-only).
        try
        {
            Guid iid = IID_IPropertyStore;
            int hr = SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0, ref iid, out IPropertyStore store);
            if (hr == 0 && store is not null)
            {
                record.AppUserModelId = ReadStringProperty(store, PKEY_AppUserModel_ID);
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            // best-effort; AppID may be absent
        }

        return record;
    }

    private static void AddHashMatch(List<string> observations, string aLabel, IconCapture a, string bLabel, IconCapture b)
    {
        if (a.Sha256 is null || b.Sha256 is null)
        {
            return;
        }
        if (string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            observations.Add($"MATCH: {aLabel} is byte-identical to {bLabel} (sha256 {a.Sha256[..12]}…).");
        }
    }

    private static void BuildContactSheet(List<(string Label, string? PngPath)> cells, string outPath)
    {
        const int cell = 96;
        const int pad = 14;
        const int labelH = 28;
        int cols = 6;
        int rows = (int)Math.Ceiling(cells.Count / (double)cols);
        // Two background swatches per cell (dark + light), stacked vertically.
        int cellH = (cell * 2) + (pad * 3) + labelH;
        int cellW = cell + (pad * 2);
        int width = cols * cellW;
        int height = rows * cellH;

        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(245, 245, 245));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            using var font = new Font("Segoe UI", 8f);
            using var labelBrush = new SolidBrush(Color.Black);

            for (int i = 0; i < cells.Count; i++)
            {
                int r = i / cols;
                int c = i % cols;
                int x = c * cellW;
                int y = r * cellH;

                var darkRect = new Rectangle(x + pad, y + pad, cell, cell);
                var lightRect = new Rectangle(x + pad, y + pad + cell + pad, cell, cell);
                using (var darkBg = new SolidBrush(Color.FromArgb(32, 32, 32)))
                using (var lightBg = new SolidBrush(Color.White))
                {
                    g.FillRectangle(darkBg, darkRect);
                    g.FillRectangle(lightBg, lightRect);
                }

                (string label, string? png) = cells[i];
                if (!string.IsNullOrEmpty(png) && File.Exists(png))
                {
                    try
                    {
                        using var img = Image.FromFile(png);
                        g.DrawImage(img, darkRect);
                        g.DrawImage(img, lightRect);
                    }
                    catch
                    {
                        // leave the swatches empty if the png can't be read
                    }
                }
                else
                {
                    g.DrawString("(none)", font, labelBrush, x + pad, y + pad + 4);
                }

                g.DrawString(label, font, labelBrush, x + pad, y + cellH - labelH + 2);
            }
        }
        sheet.Save(outPath, ImageFormat.Png);
    }

    private static string HashFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
