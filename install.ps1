# Cabinet installer: downloads the latest release and runs it (Cabinet installs itself to
# %LOCALAPPDATA%\Cabinet, adds itself to startup, and keeps itself updated).
#   irm https://raw.githubusercontent.com/MRoiban/cabinet/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072
$url = 'https://github.com/MRoiban/cabinet/releases/latest/download/Cabinet.exe'
$exe = Join-Path $env:TEMP 'Cabinet-setup.exe'
Write-Host 'Downloading Cabinet...'
Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $exe
Unblock-File $exe
Start-Process $exe
Write-Host 'Cabinet is installed and running in the tray (look behind the ^ arrow).'
