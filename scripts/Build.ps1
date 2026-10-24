param([switch]$Publish,[switch]$TestHarness)
. (Join-Path $PSScriptRoot 'Environment.ps1')
$project = Join-Path $projectRoot 'src\SidebarPrototype\SidebarPrototype.csproj'
if ($Publish -and $TestHarness) { throw 'Publish ships the Release build; the test harness is never published.' }
$configuration = if ($TestHarness) { 'TestHarness' } else { 'Release' }
& $projectSdk build $project -c $configuration -r win-x64 --nologo -p:SelfContained=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Publish) {
    & $projectSdk publish $project -c Release -r win-x64 --self-contained false --no-build --no-restore --nologo -o (Join-Path $projectRoot 'build\publish')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Host 'Candidate published to build\publish. The installed app folder was not replaced.'
}
