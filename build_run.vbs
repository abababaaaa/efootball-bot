' NOTE: keep this file pure ASCII (no hardcoded Chinese path) - WSH reads .vbs as
' ANSI/GBK, so a UTF-8 Chinese path literal becomes mojibake ("file not found").
' Derive the project root from this script's own location instead.
Set fso = CreateObject("Scripting.FileSystemObject")
Set WshShell = CreateObject("WScript.Shell")
WshShell.CurrentDirectory = fso.GetParentFolderName(WScript.ScriptFullName)
exitCode = WshShell.Run("cmd /c dotnet build src\EfootballBot\EfootballBot.csproj -c Debug --nologo -v q > build_result.txt 2>&1", 0, True)
Set f = fso.OpenTextFile("build_result.txt", 8, True)
f.WriteLine "Exit code: " & exitCode
f.Close
