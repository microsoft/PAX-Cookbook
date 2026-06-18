Option Explicit
' PAX Cookbook hidden uninstaller launcher.
'
' The installed Setup is framework-dependent and runs via the Microsoft-signed
' dotnet.exe host (dotnet "PAXCookbookSetup.dll" uninstall). dotnet.exe is a
' CONSOLE application, so the Add/Remove Programs UninstallString launching it
' directly would flash a blank terminal window during uninstall. This script
' launches it with WScript.Shell.Run window style 0 (hidden), so no console
' window ever appears -- the only UI is the uninstall confirmation/progress
' dialog. wscript.exe and this .vbs text file are WDAC-allowed; the unsigned
' Setup apphost EXE is never executed.
'
' The Setup DLL ships in the same folder as this script, so paths resolve
' relative to the script and the launcher is fully relocatable. Every argument
' passed to this script is forwarded verbatim AFTER the "uninstall" verb
' (e.g. --force for the QuietUninstallString).
Dim shell, fso, scriptDir, dll, dotnet, cmd, i
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
dll = scriptDir & "\PAXCookbookSetup.dll"
dotnet = ResolveDotNet()

cmd = """" & dotnet & """ """ & dll & """ uninstall"
For i = 0 To WScript.Arguments.Count - 1
    cmd = cmd & " """ & WScript.Arguments(i) & """"
Next

' Window style 0 keeps the dotnet host hidden; wait for it and forward its exit
' code (Add/Remove Programs uses the UninstallString's exit status).
WScript.Quit shell.Run(cmd, 0, True)

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
