using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PAXCookbook.App;

// Native Windows application window that hosts the PAX Cookbook SPA inside a
// WebView2 control. This is the Office-grade shell foundation: a real native
// window + embedded Chromium (WebView2), not an external browser app-mode
// process and not a PowerShell launcher. The window navigates to the
// in-process Kestrel loopback
// URL; the SPA renders entirely inside the WebView2 control.
//
// X2 scope: smallest compile-safe WebView2 shell foundation. Window chrome
// polish (custom title bar, single-instance activation, taskbar jump list,
// graceful close-confirmation wiring to the SPA's Close App control) is
// deferred to a later shell slice. The architecture is intentionally a GUI
// (WinExe) app — it is not a console application.
internal static class WebViewShell
{
    // Stable Windows shell identity for this application. It mirrors
    // PAXCookbook.Shared.ProductConstants.Aumid and the AppUserModelID the
    // installer stamps onto the Start-menu and taskbar shortcuts, so a
    // shortcut-launched install and a direct exe run share one taskbar
    // identity. Setting an explicit AppUserModelID BEFORE the first window is
    // created is what lets the Windows taskbar adopt the window's own icon
    // (the bundled multi-resolution app icon) instead of deriving a generic,
    // low-resolution icon from the bare process.
    private const string AppUserModelId = Program.AppUserModelId;

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    // LoadImage flags used to realize a real icon frame from the bundled
    // multi-resolution .ico. System.Drawing.Icon's size-based constructor
    // cannot decode PNG-compressed frames and silently substitutes a lower
    // frame, so LoadImage with LR_LOADFROMFILE is used to select an exact
    // pixel size. The window's large (taskbar) icon is realized at the system
    // large-icon metric so the shell uses the .ico's dedicated, hand-tuned
    // frame for that size rather than downscaling the 256px master, whose
    // thin strokes wash out when the taskbar shrinks it to button size.
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTCOLOR = 0x0000;

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

    // Force the top-level window visible regardless of the show state this
    // process inherited from its launcher. The app is started through a hidden
    // launcher so the signed dotnet host's console window never flashes; that
    // launcher starts the process with SW_HIDE, which the first form show would
    // otherwise honor and leave the main window hidden. A direct ShowWindow on
    // the realized handle overrides the inherited state for the GUI window.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_SHOWMAXIMIZED = 3;

    // Taskbar button icon for the running window. Under corporate WDAC the app
    // runs via the Microsoft-signed dotnet.exe host, launched from a shortcut
    // that targets wscript.exe (the hidden launcher that suppresses the console
    // window). Because the shortcut points at that intermediary rather than at
    // this process, Windows cannot relate the running window back to the
    // shortcut to adopt its icon, and the taskbar falls back to the dotnet
    // host's generic icon. Setting the per-window
    // System.AppUserModel.RelaunchIconResource property gives the taskbar an
    // explicit icon source for this window's button — the documented mechanism
    // for host/intermediary launch scenarios.
    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid g, uint p) { fmtid = g; pid = p; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public int p2;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private static readonly Guid IID_IPropertyStore =
        new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    // PKEY_AppUserModel_RelaunchIconResource = {9F4C2855-...}, 3
    private static PropertyKey PKEY_AppUserModel_RelaunchIconResource =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);

    private const ushort VT_LPWSTR = 31;

    // Point the running window's taskbar button at an explicit icon resource
    // ("path,index" — an exe/.ico the shell can rasterize). Safe to call more
    // than once and before or after the taskbar button is realized.
    private static void SetWindowRelaunchIcon(IntPtr hwnd, string iconResource)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(iconResource)) { return; }
        IPropertyStore? store = null;
        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store is null)
            {
                return;
            }
            var pv = new PropVariant { vt = VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(iconResource) };
            try
            {
                store.SetValue(ref PKEY_AppUserModel_RelaunchIconResource, ref pv);
                store.Commit();
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        catch
        {
            // Non-fatal: the taskbar falls back to its default icon resolution.
        }
        finally
        {
            if (store is not null) { Marshal.ReleaseComObject(store); }
        }
    }


    // Window-mode test-only close seam. When selfCloseAfterMs is greater than
    // zero the shell schedules a single full-close on the UI thread after the
    // given delay, driving the EXACT same teardown path as the operator
    // choosing Close app from the close modal (WebView2 dispose -> message loop
    // exit). It is a command-line-driven test affordance only: there is no HTTP
    // shutdown route and the desktop launcher never passes it. The smoke harness
    // uses it to exercise the full-shutdown path without a human at the keyboard.
    //
    // restoreSignal, when supplied, is a process-wide auto-reset event the
    // single-instance guard in Program signals when a second launch is
    // suppressed; the shell waits on it on a background thread and restores the
    // window to the foreground so a second launch activates the running app
    // instead of starting a second server.
    //
    // aumidOverride, when supplied, replaces the product AppUserModelID for this
    // process only. It is a diagnostic seam (driven by the --test-seam-aumid
    // CLI flag, never by the launcher) used to separate a stale per-AUMID shell
    // icon cache from a defective icon asset: launching under a never-before-
    // seen AUMID forces Windows to rasterize the taskbar icon fresh from the
    // executable, with no cached entry to mask the real asset.
    //
    // importHandoffDir, when supplied, is the local folder that holds staged
    // file-open import tickets. Once the WebView2 control is ready AND the
    // in-process broker lock reads Unlocked (i.e. the user has completed the
    // normal Windows Hello / lock ceremony on the legacy shell), the shell
    // navigates ONE time to the React Import Recipe state for the newest pending
    // ticket. It never bypasses the lock, never fabricates a success, and never
    // sends the file path: navigation carries only the opaque ticket id.
    //
    // showTray controls whether THIS window creates its own system-tray icon.
    // It is true for a window that owns the in-process broker (the standalone /
    // single-process case), so minimizing to the tray keeps the server alive.
    // It is false for a window that ATTACHED to an already-running background
    // broker daemon (the two-process case): the daemon owns the persistent tray
    // presence, so the window suppresses its own tray to avoid a confusing
    // second icon, and "minimize to tray" degrades to a normal taskbar minimize
    // (the window is never hidden with no affordance to restore it).
    public static void Run(string url, string title, string iconPath, string userDataFolder, int selfCloseAfterMs = 0, EventWaitHandle? restoreSignal = null, string? aumidOverride = null, string? importHandoffDir = null, bool showTray = true)
    {
        // Establish a stable taskbar identity before any window exists. Without
        // this, a direct (non-shortcut) launch leaves Windows to derive the
        // taskbar button icon from the process, which renders as a small,
        // upscaled image rather than the bundled high-resolution app icon.
        try
        {
            string aumid = string.IsNullOrWhiteSpace(aumidOverride) ? AppUserModelId : aumidOverride;
            SetCurrentProcessExplicitAppUserModelID(aumid);
        }
        catch
        {
            // Non-fatal: identity is a taskbar-quality nicety, not a launch
            // prerequisite.
        }

        // Resolve an explicit taskbar-button icon source for this window. Prefer
        // the bundled apphost EXE that ships next to this DLL — it embeds the
        // multi-resolution app icon and is exactly the source the Start-menu
        // shortcut points at — and fall back to the .ico the shell already
        // loads. This is applied to the window via RelaunchIconResource so the
        // taskbar shows the app icon even though the process is the generic
        // dotnet.exe host launched through the wscript intermediary.
        string appHostExe = Path.Combine(AppContext.BaseDirectory, "PAX Cookbook.exe");
        string relaunchIconResource =
            File.Exists(appHostExe) ? appHostExe + ",0"
            : File.Exists(iconPath) ? iconPath + ",0"
            : string.Empty;

        // Fail fast and clearly if the Evergreen WebView2 Runtime is absent.
        // We do not install or bootstrap the runtime in X2.
        try
        {
            string available = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(available))
            {
                throw new WebView2RuntimeMissingException("No WebView2 runtime version reported.");
            }
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new WebView2RuntimeMissingException(ex.Message, ex);
        }

        ApplicationConfiguration.Initialize();

        // Open on the monitor where the user launched the app (the screen under
        // the cursor), not always the primary monitor. A maximized window
        // maximizes on the monitor that contains its restore bounds, so those
        // bounds are positioned on the target screen BEFORE the first show.
        // Cursor.Position can throw in unusual session states, so fall back to
        // the primary screen's working area.
        Rectangle targetWorkingArea;
        try { targetWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea; }
        catch { targetWorkingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 860); }
        var restoreSize = new Size(
            Math.Min(1280, targetWorkingArea.Width),
            Math.Min(860, targetWorkingArea.Height));
        var restoreLocation = new Point(
            targetWorkingArea.X + Math.Max(0, (targetWorkingArea.Width - restoreSize.Width) / 2),
            targetWorkingArea.Y + Math.Max(0, (targetWorkingArea.Height - restoreSize.Height) / 2));

        using var form = new Form
        {
            Text = title,
            // Manual placement on the target screen so the maximize lands on the
            // monitor the user launched from (CenterScreen always uses primary).
            StartPosition = FormStartPosition.Manual,
            Location = restoreLocation,
            // Restore-state bounds (used when the user un-maximizes); the window
            // OPENS maximized via WindowState below, and the force-show timer
            // re-asserts the maximized state rather than restoring it.
            ClientSize = restoreSize,
            WindowState = FormWindowState.Maximized,
            MinimumSize = new Size(880, 600),
        };

        // Window icons. The taskbar button uses the window's large icon and
        // the title bar uses the small icon. We realize BOTH from the bundled
        // multi-resolution .ico via Win32 LoadImage at the system large- and
        // small-icon metrics, so the shell receives the .ico's dedicated,
        // hand-tuned frame for each size. The system-tray icon already renders
        // crisply because it loads those same dedicated small frames; realizing
        // the window's large icon at the large-icon metric (rather than the
        // 256px master) gives the taskbar button the matching dedicated frame
        // instead of a 256-to-button downscale whose thin strokes wash out.
        // The raw HICON handles are owned here (Icon.FromHandle does not take
        // ownership) and destroyed after the message loop exits.
        IntPtr hBigIcon = IntPtr.Zero;
        IntPtr hSmallIcon = IntPtr.Zero;
        Icon? bigIcon = null;
        Icon? smallIcon = null;
        Icon? formIcon = null;
        if (File.Exists(iconPath))
        {
            try
            {
                // The managed window icon is the full multi-resolution icon
                // loaded from the SAME .ico the system-tray icon uses. The live
                // signal that the tray renders correctly while the taskbar did
                // not proves this file is good, so the window icon is sourced
                // from the identical file rather than a single down-picked
                // frame; WinForms selects the best-fit frame for the title bar
                // and the taskbar button from this multi-frame icon.
                formIcon = new Icon(iconPath);
                form.Icon = formIcon;

                Size smallSize = SystemInformation.SmallIconSize;
                Size bigSize = SystemInformation.IconSize;
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
                // Non-fatal: fall back to the default window icon.
                bigIcon = null;
                smallIcon = null;
            }
        }

        // Push the high-resolution icon handles onto the live top-level window.
        // The taskbar button adopts the large icon; the title bar uses the small
        // one. This must be re-sent every time the window's taskbar button is
        // (re)created — at first handle creation, when the window is first shown,
        // and after a restore from the tray, because Hide()/Show() destroys and
        // recreates the taskbar button, which would otherwise fall back to the
        // executable's embedded icon resource instead of these explicit
        // high-resolution handles.
        void ApplyWindowIcons()
        {
            try
            {
                if (!form.IsHandleCreated) { return; }
                if (hBigIcon != IntPtr.Zero)
                {
                    SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_BIG, hBigIcon);
                }
                if (hSmallIcon != IntPtr.Zero)
                {
                    SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_SMALL, hSmallIcon);
                }
            }
            catch
            {
                // Non-fatal: WinForms' own icon assignment remains in effect.
            }
        }

        form.HandleCreated += (_, _) =>
        {
            ApplyWindowIcons();
            SetWindowRelaunchIcon(form.Handle, relaunchIconResource);
        };
        form.Shown += (_, _) => ApplyWindowIcons();

        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        // Whether the next FormClosing should proceed to teardown rather than be
        // intercepted and routed to the in-app close modal. It is set true only
        // by an explicit Close-app decision (the modal's Close app button, the
        // tray's Close menu item, or the test-only self-close seam). Every other
        // close request (title-bar X, taskbar Close, Alt+F4) is intercepted and
        // turned into a modal prompt instead of an immediate exit.
        bool fullClose = false;

        // System-tray presence. The window can be hidden to the tray (the close
        // modal's Minimize to tray choice) while the Cookbook server keeps
        // running so bakes continue. The tray icon stays visible the whole time
        // the process runs; double-click or the Open menu item restores the
        // window, and the Close menu item performs a full shutdown.
        NotifyIcon? tray = null;
        Icon? trayIcon = null;

        void RestoreFromTray()
        {
            try
            {
                if (!form.Visible) { form.Show(); }
                if (form.WindowState == FormWindowState.Minimized)
                {
                    form.WindowState = FormWindowState.Normal;
                }
                form.ShowInTaskbar = true;
                form.Activate();
                form.BringToFront();
                // The Hide()/Show() round-trip recreates the taskbar button, so
                // re-push the explicit high-resolution icon handles to keep the
                // taskbar icon correct after a restore from the tray.
                ApplyWindowIcons();
            }
            catch
            {
                // Non-fatal: restore is a best-effort convenience.
            }
        }

        void HideToTray()
        {
            try
            {
                if (!showTray)
                {
                    // Attach mode: no window-owned tray exists, so hiding the
                    // window would strand it with no way back. Degrade to a
                    // normal taskbar minimize; the background daemon's tray
                    // remains the persistent presence and its Open item launches
                    // the window when wanted.
                    form.WindowState = FormWindowState.Minimized;
                    return;
                }
                // Hide() removes the taskbar button as well; the separate tray
                // icon remains the only affordance while hidden. The Kestrel
                // host is untouched, so any in-progress bakes keep running.
                form.Hide();
            }
            catch
            {
                // Non-fatal: if hiding fails the window simply stays visible.
            }
        }

        void RequestFullClose()
        {
            fullClose = true;
            try { form.Close(); }
            catch { /* Non-fatal: the form may already be closing. */ }
        }

        // Intercept window-close requests. Unless a full-close has already been
        // decided, cancel the native close and ask the SPA to open the shared
        // close modal (title-bar X, taskbar Close, and Alt+F4 all land here).
        // When the web layer is not yet live we cannot show the modal, so we
        // allow the close and tear the embedded control down. The WebView2
        // control is disposed on the UI thread while the message pump is still
        // alive so its browser/IPC worker threads exit and the process can end,
        // releasing the exclusive write lock on the executable image.
        form.FormClosing += (_, e) =>
        {
            if (fullClose)
            {
                try { web.Dispose(); }
                catch { /* Non-fatal: best-effort teardown of the embedded control. */ }
                return;
            }

            if (web.CoreWebView2 is not null)
            {
                e.Cancel = true;
                try { web.CoreWebView2.PostWebMessageAsString("cookbook:host-close-request"); }
                catch { /* Non-fatal: if posting fails the window simply stays open. */ }
                return;
            }

            // Web layer not ready yet — allow the close and tear down.
            try { web.Dispose(); }
            catch { /* Non-fatal: best-effort teardown of the embedded control. */ }
        };

        // Route input focus into the embedded web content whenever the
        // window becomes active. Browser-owned Windows Hello / WebAuthn
        // prompts (used by both the unlock ceremony and the manual-cook
        // step-up) are parented to the active top-level window; keeping
        // the WebView2 focused when the form is activated keeps those
        // prompts owned by — and in front of — the app window rather
        // than appearing behind it.
        form.Activated += (_, _) =>
        {
            try
            {
                if (web.CoreWebView2 is not null)
                {
                    web.Focus();
                }
            }
            catch
            {
                // Non-fatal: focus routing is a best-effort safeguard.
            }
        };

        Directory.CreateDirectory(userDataFolder);

        form.Load += async (_, _) =>
        {
            try
            {
                CoreWebView2Environment env =
                    await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                await web.EnsureCoreWebView2Async(env);

                // Loopback-only appliance: no devtools, no default context menu,
                // no status bar. Keep the shell minimal and tamper-resistant.
                CoreWebView2Settings settings = web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                // Receive the close-modal decisions the SPA posts back over the
                // WebView2 message channel. "minimize-to-tray" hides the window
                // and keeps the server running; "close-app" performs a full
                // shutdown via the same teardown path as the title-bar X.
                web.CoreWebView2.WebMessageReceived += (_, args) =>
                {
                    string message;
                    try { message = args.TryGetWebMessageAsString(); }
                    catch { return; }

                    switch (message)
                    {
                        case "cookbook:minimize-to-tray":
                            HideToTray();
                            break;
                        case "cookbook:close-app":
                            RequestFullClose();
                            break;
                    }
                };

                // External links (a target="_blank" to a public http/https URL —
                // e.g. a Power BI dashboard template repo) open in the operator's
                // default browser. The appliance window never navigates to, nor
                // hosts an in-app popup for, external sites; only http/https is
                // handed off (any other scheme is ignored).
                web.CoreWebView2.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    string target = args.Uri ?? string.Empty;
                    if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                        }
                        catch
                        {
                            // Best-effort: a failed shell launch leaves the click a no-op.
                        }
                    }
                };

                web.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "PAX Cookbook could not start its application window.\n\n" + ex.Message,
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                form.Close();
            }
        };

        // File-open import handoff navigation. When the EXE was launched (or
        // re-activated) with a double-clicked .paxlite / .pax file, Program
        // staged it as a one-time local ticket. This timer routes the React app
        // to the Import Recipe state for the newest pending ticket — but only
        // after the WebView2 control is ready AND the in-process broker lock
        // reads Unlocked. That preserves the normal Windows Hello / lock flow:
        // until the user unlocks on the legacy shell, the timer simply waits.
        // Navigation carries only the opaque ticket id (never the file path),
        // and each ticket id is navigated at most once.
        if (!string.IsNullOrWhiteSpace(importHandoffDir))
        {
            string? lastNavigatedImportId = null;
            var importTimer = new System.Windows.Forms.Timer { Interval = 500 };
            importTimer.Tick += (_, _) =>
            {
                try
                {
                    if (form.IsDisposed || web.IsDisposed || web.CoreWebView2 is null)
                    {
                        return;
                    }
                    if (!string.Equals(BrokerLock.GetState(), "Unlocked", StringComparison.Ordinal))
                    {
                        return;
                    }
                    string? pendingId = ImportHandoffQueue.PeekLatestId(importHandoffDir);
                    if (string.IsNullOrEmpty(pendingId) ||
                        string.Equals(pendingId, lastNavigatedImportId, StringComparison.Ordinal))
                    {
                        return;
                    }
                    lastNavigatedImportId = pendingId;
                    // url ends with '/', e.g. http://localhost:PORT/ — reload the
                    // integrated shell root with the import query. The shell's
                    // left nav and chrome stay in place; integrated-shell.js
                    // reads the ticket, selects Recipes, and forwards the id into
                    // the content iframe, which consumes it. The file path is
                    // never placed on the URL — only the opaque ticket id.
                    string importUrl = url + "?import=" + Uri.EscapeDataString(pendingId);
                    web.CoreWebView2.Navigate(importUrl);
                }
                catch
                {
                    // Best-effort: a transient navigation/poll failure must not
                    // crash the shell. The next tick retries.
                }
            };
            form.Shown += (_, _) => importTimer.Start();
        }

        // Test-only: drive the full Close-app teardown path after a delay so the
        // automated smoke can exercise shutdown without a human at the keyboard.
        // It sets the full-close flag first so the FormClosing interceptor lets
        // the close proceed instead of re-opening the modal.
        if (selfCloseAfterMs > 0)
        {
            var closeTimer = new System.Windows.Forms.Timer { Interval = selfCloseAfterMs };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                closeTimer.Dispose();
                RequestFullClose();
            };
            form.Shown += (_, _) => closeTimer.Start();
        }

        // System-tray icon. Realized from the same bundled .ico as the window
        // icon, at the system small-icon metric so it reads crisply in the tray.
        // It stays visible the entire time the process runs (including while the
        // window is hidden to the tray). Double-click and the Open menu item
        // restore the window; the Close menu item performs a full shutdown.
        // Suppressed in attach mode (showTray=false): the background daemon owns
        // the persistent tray, so this window does not add a second icon.
        ContextMenuStrip? trayMenu = null;
        if (showTray)
        {
            try
            {
                if (File.Exists(iconPath))
                {
                    Size traySize = SystemInformation.SmallIconSize;
                    trayIcon = new Icon(iconPath, traySize.Width, traySize.Height);
                }
            }
            catch
            {
                trayIcon = null;
            }

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open PAX Cookbook", null, (_, _) => RestoreFromTray());
            trayMenu.Items.Add("Close PAX Cookbook", null, (_, _) => RequestFullClose());

            tray = new NotifyIcon
            {
                Text = "PAX Cookbook",
                Icon = trayIcon ?? form.Icon,
                Visible = true,
                ContextMenuStrip = trayMenu,
            };
            tray.DoubleClick += (_, _) => RestoreFromTray();
        }

        // Single-instance restore listener. When Program suppresses a second
        // launch it signals restoreSignal; wake on a background thread and bring
        // the running window back to the foreground on the UI thread.
        if (restoreSignal is not null)
        {
            var restoreThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        restoreSignal.WaitOne();
                    }
                    catch
                    {
                        return;
                    }
                    if (form.IsDisposed) { return; }
                    try { form.BeginInvoke((Action)RestoreFromTray); }
                    catch { return; }
                }
            })
            {
                IsBackground = true,
                Name = "PAXCookbook.RestoreListener",
            };
            restoreThread.Start();
        }

        // Defeat an inherited hidden show state. The app is launched through a
        // hidden launcher so the signed dotnet host never flashes a console
        // window; that launcher starts this process with SW_HIDE, which the
        // first form show honors and would otherwise leave the main window
        // hidden with no affordance to restore it. A one-shot timer fires once
        // the message loop is pumping (the window handle is realized by then)
        // and forces the window visible and foreground. This is a no-op for a
        // process started normally visible.
        var forceShowTimer = new System.Windows.Forms.Timer { Interval = 1 };
        forceShowTimer.Tick += (_, _) =>
        {
            forceShowTimer.Stop();
            forceShowTimer.Dispose();
            try
            {
                if (form.IsDisposed) { return; }
                // The inherited hidden show state only suppresses the FIRST
                // ShowWindow call: Win32 honors the launcher's STARTUPINFO show
                // state on the first show and ignores the requested nCmdShow.
                // WinForms' own first show already consumed that, so this
                // subsequent ShowWindow is honored and brings the window visible
                // WITHOUT a Hide/Show toggle. A toggle would destroy and recreate
                // the taskbar button (a hidden window has no button), and the
                // fresh button would adopt the dotnet host process's generic icon
                // instead of the bundled high-resolution app icon. SW_SHOWMAXIMIZED
                // (not SW_SHOWNORMAL) is used so force-showing keeps the window
                // maximized — SW_SHOWNORMAL would RESTORE it, which made the window
                // flash maximized and then shrink to its restore size.
                ShowWindow(form.Handle, SW_SHOWMAXIMIZED);
                if (form.WindowState == FormWindowState.Minimized)
                {
                    form.WindowState = FormWindowState.Normal;
                }
                form.Activate();
                form.BringToFront();
                // Re-assert the explicit high-resolution icon handles on the now
                // visible window so the freshly realized taskbar button shows the
                // bundled app icon rather than the host process's generic icon.
                ApplyWindowIcons();
                SetWindowRelaunchIcon(form.Handle, relaunchIconResource);
            }
            catch
            {
                // Non-fatal: force-show is a best-effort correction.
            }
        };
        forceShowTimer.Start();

        Application.Run(form);

        // Tear down the tray before the icon handles are destroyed so the shell
        // leaves no orphaned tray entry behind. In attach mode there is no
        // window-owned tray, so these are null and the guards no-op.
        try
        {
            if (tray is not null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            trayMenu?.Dispose();
        }
        catch
        {
            // Non-fatal: process is exiting anyway.
        }
        trayIcon?.Dispose();

        // Keep the native icon handles alive until the message loop has fully
        // exited, then dispose the managed wrappers and release the underlying
        // HICONs (Icon.FromHandle does not own them, so they must be destroyed
        // explicitly to avoid leaking GDI handles).
        GC.KeepAlive(bigIcon);
        GC.KeepAlive(smallIcon);
        GC.KeepAlive(formIcon);
        bigIcon?.Dispose();
        smallIcon?.Dispose();
        formIcon?.Dispose();
        if (hBigIcon != IntPtr.Zero) { DestroyIcon(hBigIcon); }
        if (hSmallIcon != IntPtr.Zero) { DestroyIcon(hSmallIcon); }
    }
}

// Raised when the Evergreen WebView2 Runtime is not installed. The launcher
// surfaces a clear prerequisite message; X2 does not install the runtime.
internal sealed class WebView2RuntimeMissingException : Exception
{
    public WebView2RuntimeMissingException(string message)
        : base(message)
    {
    }

    public WebView2RuntimeMissingException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
