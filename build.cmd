@echo off
rem Builds every *.cs in this folder into Cabinet.exe (tray app) and Cabinet-cli.exe (debug: --sync <appid>, --dump <file>)
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
set ICON=/win32icon:icon\cabinet.ico /resource:icon\cabinet.ico,Cabinet.Icon
rem Generate the version constant and Explorer assembly stamps; reject unsafe version text.
powershell -NoProfile -Command "$v=$env:CABINET_VERSION; if ([string]::IsNullOrEmpty($v)) { $v='0.0.0-dev' }; if ($v -notmatch '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:[-+][0-9A-Za-z.-]+)?$') { Write-Error 'Invalid CABINET_VERSION'; exit 1 }; $n=@([int]$Matches[1],[int]$Matches[2],[int]$Matches[3],0); if ($Matches[4]) { $n[3]=[int]$Matches[4] }; foreach ($part in $n) { if ($part -gt 65534) { Write-Error 'Assembly version component exceeds 65534'; exit 1 } }; $numeric=$n -join '.'; $q=[char]34; @('using System.Reflection;',('[assembly: AssemblyVersion('+$q+$numeric+$q+')]'),('[assembly: AssemblyFileVersion('+$q+$numeric+$q+')]'),('static class BuildInfo { public const string Version = '+$q+$v+$q+'; }')) | Set-Content -LiteralPath BuildInfo.cs -Encoding UTF8" || exit /b 1
"%CSC%" /nologo /langversion:5 /optimize /target:winexe /out:Cabinet.exe %REFS% %ICON% *.cs || exit /b 1
"%CSC%" /nologo /langversion:5 /optimize /target:exe /out:Cabinet-cli.exe %REFS% %ICON% *.cs || exit /b 1
echo Built.
