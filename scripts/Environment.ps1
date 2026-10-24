$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectSdk = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (!$projectSdk) { throw 'The .NET 10 SDK is missing. Install it with: winget install Microsoft.DotNet.SDK.10' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
# Tests run the TestHarness build, which adds the Testing folder and its hidden switches.
$projectAssembly = Join-Path $projectRoot 'src\SidebarPrototype\bin\TestHarness\net10.0-windows\win-x64\SidebarPrototype.dll'
