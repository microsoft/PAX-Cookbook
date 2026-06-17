Option Explicit
' PAX Cookbook hidden launcher.
'
' Corporate WDAC blocks unsigned executables, so the framework-dependent app
' runs via the Microsoft-signed dotnet.exe host (dotnet "PAX Cookbook.dll").
' dotnet.exe is a CONSOLE application, so launching it from a shortcut, the Run
' key, a protocol handler, a file association, or a scheduled task would flash a
' blank terminal window. This script launches it with WScript.Shell.Run window
' style 0 (hidden), so no console window ever appears. wscript.exe and this .vbs
' text file are WDAC-allowed; the unsigned apphost EXE is never executed.
'
' The app DLL ships in the same folder as this script, so paths are resolved
' relative to the script and the launcher is fully relocatable. Every argument
' passed to this script is forwarded verbatim to the app after the DLL
' (e.g. --headless, --workspace <ws>, --approot <app>, protocol <uri>, <file>).
'
' If the FIRST argument is the sentinel "--pax-wait", the launcher WAITS for the
' app to exit and returns its exit code (used by scheduled bakes so the task's
' run result reflects the cook and overlap policies work). Otherwise it is
' fire-and-forget so the shell call returns immediately.
Dim shell, fso, scriptDir, dll, dotnet, cmd, i, startIdx, waitForExit
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
dll = scriptDir & "\PAX Cookbook.dll"
dotnet = ResolveDotNet()

startIdx = 0
waitForExit = False
If WScript.Arguments.Count > 0 Then
    If WScript.Arguments(0) = "--pax-wait" Then
        waitForExit = True
        startIdx = 1
    End If
End If

cmd = """" & dotnet & """ """ & dll & """"
For i = startIdx To WScript.Arguments.Count - 1
    cmd = cmd & " """ & WScript.Arguments(i) & """"
Next

If waitForExit Then
    WScript.Quit shell.Run(cmd, 0, True)
Else
    shell.Run cmd, 0, False
End If

' Resolve the Microsoft-signed dotnet.exe host. Prefer the standard per-machine
' install locations; fall back to the bare name (PATH / App Paths) when absent.
Function ResolveDotNet()
    Dim names, n, base, p
    names = Array("%ProgramFiles%", "%ProgramW6432%", "%ProgramFiles(x86)%")
    For Each n In names
        base = shell.ExpandEnvironmentStrings(n)
        If Len(base) > 0 And InStr(base, "%") = 0 Then
            p = base & "\dotnet\dotnet.exe"
            If fso.FileExists(p) Then
                ResolveDotNet = p
                Exit Function
            End If
        End If
    Next
    ResolveDotNet = "dotnet.exe"
End Function
