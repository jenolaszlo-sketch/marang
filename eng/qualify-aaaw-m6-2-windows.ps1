$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'artifacts/aaaw-m6-2/windows-qualification'
$testResults = Join-Path $root 'artifacts/aaaw-m6-1/windows-qualification/trx'
$failures = [System.Collections.Generic.List[string]]::new()
$sourceCommit = ''
$startedEpoch = 0.0
New-Item -ItemType Directory -Force -Path $output | Out-Null
Push-Location -LiteralPath $root
try {
    $env:UseSharedCompilation = 'false'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
    $env:VSTEST_CONNECTION_TIMEOUT = '180'
    $env:PYTHONDONTWRITEBYTECODE = '1'

    $status = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) {
        $failures.Add('M6.2 qualification requires an exact clean committed source tree.')
        $sourceCommit = (& git rev-parse HEAD).Trim()
    } else {
        $sourceCommit = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $sourceCommit) { $failures.Add('Could not record the starting source commit.') }
    }

    if ($failures.Count -eq 0) {
        Write-Host '=== bootstrap-exact-m6.1-old-writer ==='
        & python eng/prepare-aaaw-m6-2-oldwriter.py 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { $failures.Add('Exact M6.1 old-writer bootstrap failed.') }
    }
    $startedEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0

    if ($failures.Count -eq 0) {
        try {
            Write-Host '=== unchanged-m6.1-windows-full-qualification ==='
            & (Join-Path $root 'eng/qualify-aaaw-m6-1-windows.ps1')
            if ($LASTEXITCODE -ne 0) { $failures.Add('M6.1 Windows full qualification failed.') }
        } catch {
            $failures.Add("M6.1 Windows runner exception: $($_.Exception.GetType().FullName): $($_.Exception.Message)")
        }
    }
} catch {
    $failures.Add("Runner exception: $($_.Exception.GetType().FullName): $($_.Exception.Message)")
} finally {
    $arguments = @('eng/qualify-aaaw-m6-2.py', '--validate-windows', '--test-results-dir', $testResults,
        '--started-epoch', $startedEpoch.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--source-commit', $sourceCommit)
    foreach ($failure in $failures) { $arguments += @('--runner-failure', $failure) }
    & python @arguments
    $validationExitCode = $LASTEXITCODE
    if ($validationExitCode -ne 0) { $failures.Add('M6.2 evidence validation did not pass; inspect qualification.json.') }
    Pop-Location
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}
Write-Host 'M6.2 profile compatibility and unchanged M5/M6.1 controls passed on Windows.'
