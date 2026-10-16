$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'artifacts/aaaw-m6-1/windows-qualification'
$trxDirectory = Join-Path $output 'trx'
$startedEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0
$failures = [System.Collections.Generic.List[string]]::new()
$sourceCommit = ''
$transcript = Join-Path $output 'runner.log'

New-Item -ItemType Directory -Force -Path $trxDirectory | Out-Null
Push-Location -LiteralPath $root
try {
    Start-Transcript -LiteralPath $transcript -Force | Out-Null
    $env:UseSharedCompilation = 'false'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
    $env:VSTEST_CONNECTION_TIMEOUT = '180'
    $env:PYTHONDONTWRITEBYTECODE = '1'

    $sourceStatus = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $sourceStatus.Count -ne 0) {
        $failures.Add('Source must be clean and committed before M6.1 qualification.')
    } else {
        $sourceCommit = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $sourceCommit) { $failures.Add('Could not record the starting source commit.') }
    }

    function Invoke-QualificationCommand([string]$Name, [string]$Executable, [string[]]$Arguments) {
        Write-Host "=== $Name ==="
        & $Executable @Arguments 2>&1 | Out-Host
        $code = $LASTEXITCODE
        if ($code -ne 0) { $failures.Add("$Name exited with code $code.") }
        return $code -eq 0
    }

    # Prepare the pinned M1/M5 offline package feed before restore, matching
    # the native Linux/macOS qualification entrypoints.
    $feedReady = $false
    if ($failures.Count -eq 0) {
        $feedReady = Invoke-QualificationCommand 'bootstrap-pinned-feed' 'python' @('eng/bootstrap-aaaw-m5-feed.py')
    }
    $restored = $failures.Count -eq 0
    if ($restored -and $feedReady) {
        $restored = Invoke-QualificationCommand 'restore' 'dotnet' @('restore', 'Marang.slnx', '--configfile', 'NuGet.AAAWM1.Config', '--disable-parallel')
    } elseif ($restored) {
        $restored = $false
    }
    $built = $false
    if ($restored) {
        $built = Invoke-QualificationCommand 'release-build' 'dotnet' @('build', 'Marang.slnx', '-c', 'Release', '--no-restore', '-m:1', '-p:UseSharedCompilation=false')
    }
    if ($built) {
        [void](Invoke-QualificationCommand 'format-check' 'dotnet' @('format', 'Marang.slnx', '--verify-no-changes', '--no-restore'))
        foreach ($framework in @('net8.0', 'net10.0')) {
            $trx = Join-Path $trxDirectory "m6-$framework.trx"
            $testArgs = @('test', 'tests/Marang.Tests/Marang.Tests.csproj', '-c', 'Release', '-f', $framework,
                '--no-build', '--no-restore', '--logger', "trx;LogFileName=m6-$framework.trx",
                '--results-directory', $trxDirectory)
            [void](Invoke-QualificationCommand "tests-$framework" 'dotnet' $testArgs)
            if (-not (Test-Path -LiteralPath $trx)) { $failures.Add("Full-suite TRX is missing for $framework.") }
        }
    }
} catch {
    $failures.Add("Runner exception: $($_.Exception.GetType().FullName): $($_.Exception.Message)")
} finally {
    try { Stop-Transcript | Out-Null } catch { }
    $arguments = @('eng/qualify-aaaw-m6-1.py', '--validate-windows', '--test-results-dir', $trxDirectory,
        '--started-epoch', $startedEpoch.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--source-commit', $sourceCommit)
    foreach ($failure in $failures) { $arguments += @('--runner-failure', $failure) }
    & python @arguments
    $validationExitCode = $LASTEXITCODE
    if ($validationExitCode -ne 0) { $failures.Add('M6.1 evidence validation did not pass; inspect qualification.json.') }
    Pop-Location
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}
Write-Host 'M6.1 and the unchanged M5 qualification controls passed on Windows.'
