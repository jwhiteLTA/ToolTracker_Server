@echo off
REM Compiles NfcBridge.cs using the C# compiler bundled with .NET Framework -- no Visual Studio
REM or separate install needed. Run this once (or again after editing NfcBridge.cs), then run
REM NfcBridge.exe and leave its window open.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:NfcBridge.exe /r:System.Web.Extensions.dll NfcBridge.cs
if %ERRORLEVEL% EQU 0 (
    echo Build succeeded: NfcBridge.exe
) else (
    echo Build failed -- see errors above.
)
pause
