# Builds the ZEIGARN installer (out\ZEIGARN-Setup-<version>.exe):
# a self-contained ZeiLand (no .NET install needed), BatteryHub next to it, plus PawnIO's official setup.
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'ZeiLand\ZeiLand.csproj'
$version = (Select-Xml -Path $project -XPath '//Version').Node.InnerText

$app = Join-Path $PSScriptRoot 'out\app'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $app -nologo
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# Downloaded once into vendor\ and checked against a known hash, every time.
function Get-Pinned([string]$name, [string]$url, [string]$hash) {
    $file = Join-Path $PSScriptRoot "vendor\$name"
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $hash) {
        New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest $url -OutFile $file -UseBasicParsing
        if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $hash) {
            Remove-Item $file
            throw "$name does not match the expected hash"
        }
    }
    $file
}

# PawnIO's signed setup, unmodified (GPL-2.0, https://github.com/namazso/PawnIO).
Get-Pinned 'PawnIO_setup.exe' 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe' `
    '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032' | Out-Null

# BatteryHub's official portable release, unmodified (GPL-3.0, https://github.com/krisgfx9/Wireless-Device-Battery-Monitor):
# a separate program ZeiLand runs with --json for the wireless devices it doesn't speak to itself. It goes next to
# the app with its license and its own notices (it brings HeadsetControl, GPL-3.0).
$batteryHub = Get-Pinned 'BatteryHub-1.1.0-portable.zip' `
    'https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/releases/download/v1.1.0/BatteryHub-1.1.0-portable.zip' `
    '7A97A1FF0AEFA895386077093B23A714DF549A08FAD3B1B9C37CCF2100A27026'
Expand-Archive $batteryHub -DestinationPath (Join-Path $app 'BatteryHub') -Force

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }

& $iscc /Qp "/DAppVersion=$version" (Join-Path $PSScriptRoot 'ZeiLand.iss')
if ($LASTEXITCODE) { throw 'Inno Setup failed' }
Get-Item (Join-Path $PSScriptRoot "out\ZEIGARN-Setup-$version.exe")
