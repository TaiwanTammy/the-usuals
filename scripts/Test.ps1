param([ValidateSet('Quick','Ui','Unload','Native','All')][string]$Mode='Quick',[switch]$NoBuild)
. (Join-Path $PSScriptRoot 'Environment.ps1')
if (!$NoBuild) { & (Join-Path $PSScriptRoot 'Build.ps1'); if (!$?) { throw 'Build failed.' } }
if (!(Test-Path -LiteralPath $projectAssembly)) { throw 'Build the project first.' }
$resultsDirectory = Join-Path $projectRoot 'build\test-results'
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
if ($Mode -in @('Quick','All')) {
    & $projectSdk $projectAssembly --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
    $policy = Join-Path $projectRoot 'tests\PolicyProbe\PolicyProbe.csproj'
    & $projectSdk restore $policy --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Policy test restore failed.' }
    $policyJson = & $projectSdk run --project $policy -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Policy tests failed.' }
    $policyJson | Set-Content -LiteralPath (Join-Path $resultsDirectory 'policy.json') -Encoding utf8
    $policyResult = ($policyJson -join [Environment]::NewLine) | ConvertFrom-Json
    if ($policyResult.failed -ne 0) { throw 'Policy checks failed.' }
    Write-Host "Policy checks: $($policyResult.passed) passed."
}
$cases = @{
    Ui = @{ Args=@('--ui-test'); File='ui-evidence.json' }
    Unload = @{ Args=@('--unload-only-test','--browser-test'); File='unload-evidence.json' }
    Native = @{ Args=@('--transition-native-toolwindow-test'); File='transition-evidence.json' }
}
$selected = if ($Mode -eq 'All') { @('Ui','Unload','Native') } elseif ($Mode -eq 'Quick') { @() } else { @($Mode) }
foreach ($caseName in $selected) {
    $data = Join-Path $projectRoot ('build\test-runs\'+$caseName+'-'+[Guid]::NewGuid().ToString('N'))
    $case = $cases[$caseName]
    & $projectSdk $projectAssembly @($case.Args) --data-dir $data
    if ($LASTEXITCODE -ne 0) { throw "$caseName process failed." }
    $evidenceFile = Join-Path $data $case.File
    $evidence = Get-Content -Raw -LiteralPath $evidenceFile | ConvertFrom-Json
    if ($evidence.status -ne 'completed') { throw "$caseName failed; inspect $evidenceFile" }
    Copy-Item -LiteralPath $evidenceFile -Destination (Join-Path $resultsDirectory ($caseName.ToLowerInvariant()+'.json')) -Force
    Write-Host "$caseName passed. Diagnostic files: $data"
}
