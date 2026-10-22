@echo off
REM Compiles NfcTool.cs using the C# compiler bundled with .NET Framework -- no Visual Studio or
REM separate install needed. Run this once (or again after editing NfcTool.cs), then run
REM NfcTool.exe. /target:winexe suppresses the console window a plain "exe" target would pop up
REM alongside the actual GUI.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:NfcTool.exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll NfcTool.cs
if %ERRORLEVEL% EQU 0 (
    echo Build succeeded: NfcTool.exe
) else (
    echo Build failed -- see errors above.
)
pause
