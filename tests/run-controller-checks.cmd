@echo off
cd /d "%~dp0.."
call "%~dp0..\build.cmd" || exit /b 1
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /langversion:5 /target:exe /main:ControllerLayoutChecks /out:tests\ControllerLayoutChecks.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll *.cs tests\ControllerLayoutChecks.cs || exit /b 1
"%~dp0ControllerLayoutChecks.exe"
exit /b %errorlevel%
