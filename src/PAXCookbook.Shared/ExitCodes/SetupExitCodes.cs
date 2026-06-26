namespace PAXCookbook.Shared.ExitCodes;

// Setup exit codes from exit-codes.md (PAXCookbookSetup.exe).
public static class SetupExitCodes
{
    public const int Ok = 0;
    public const int GenericError = 1;
    public const int UsageError = 2;
    public const int InternalError = 3;

    public const int InstallFailed = 50;
    public const int UpdateFailed = 51;
    public const int RepairFailed = 52;
    public const int UninstallFailed = 53;
    public const int DowngradeBlocked = 54;

    public const int RollbackPerformed = 60;
    public const int RollbackFailed = 61;
    public const int IntegrityCheckFailed = 62;

    public const int UninstallPartialFailure = 70;
    public const int UninstallAppExeLocked = 71;

    public const int WebView2RuntimeMissing = 80;
    public const int WebView2DetectionAmbiguous = 81;

    // Early startup guard: the host OS is older than Windows 10.
    public const int UnsupportedWindowsVersion = 82;

    public const int HandoffRequired = 90;
    public const int HandoffFailed = 91;

    // Phase 2 scaffold sentinel (not in contract; reserved range).
    public const int NotImplementedInPhase2 = 200;
}
