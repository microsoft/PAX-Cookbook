namespace PAXCookbook.Shared.ExitCodes;

// App exit codes from exit-codes.md (PAXCookbook.exe).
public static class AppExitCodes
{
    public const int Ok = 0;
    public const int GenericError = 1;
    public const int UsageError = 2;
    public const int InternalError = 3;

    public const int AnotherInstanceHandled = 10;
    public const int IpcConnectFailed = 11;
    public const int IpcMalformedResponse = 12;
    public const int IpcTimeout = 13;
    public const int IpcRejected = 14;
    public const int IpcUnknown = 15;

    public const int WebView2RuntimeMissing = 20;
    public const int WebView2HostFailure = 30;
    public const int ProtocolRejected = 40;
}
