param(
    [string]$ZhinuCheckout = (Join-Path $PSScriptRoot '../../../aaaw-m1/zhinu'),
    [string]$QingniaoCheckout = (Join-Path $PSScriptRoot '../../../aaaw-m1/qingniao'),
    [switch]$ReuseQualifiedPackages
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$m4Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$m4QualificationStartedAt = Get-Date
$m4SavedProcessEnvironment = @{}
foreach ($m4Name in @('MSBUILDDISABLENODEREUSE', 'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER', 'VSTEST_CONNECTION_TIMEOUT')) {
    $m4SavedProcessEnvironment[$m4Name] = [Environment]::GetEnvironmentVariable($m4Name, 'Process')
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Push-Location -LiteralPath $m4Root
try {
    [Environment]::SetEnvironmentVariable('MSBUILDDISABLENODEREUSE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER', '1', 'Process')
    [Environment]::SetEnvironmentVariable('VSTEST_CONNECTION_TIMEOUT', '180', 'Process')
    $m4Uncommitted = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $m4Uncommitted.Count -ne 0) { throw 'Record the M4 source in a clean local commit before qualification.' }
    $m4QualifiedSource = (& git rev-parse HEAD).Trim()
    if (-not $ReuseQualifiedPackages) {
        & (Join-Path $PSScriptRoot 'qualify-aaaw-m1.ps1') -PackagesOnly -ZhinuCheckout $ZhinuCheckout -QingniaoCheckout $QingniaoCheckout
    }
    $m4PackageLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'aaaw-m2-packages.json') -Raw | ConvertFrom-Json
    foreach ($m4Package in $m4PackageLock) {
        $m4PackagePath = Join-Path $m4Root "artifacts/aaaw-m1/packages/$($m4Package.id).$($m4Package.version).nupkg"
        if (-not (Test-Path -LiteralPath $m4PackagePath)) { throw "Missing pinned package $($m4Package.id)." }
        if ($ReuseQualifiedPackages -and (Get-FileHash -LiteralPath $m4PackagePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $m4Package.sha256) {
            throw "Reused package does not match the original M1 qualification hash: $($m4Package.id)."
        }
        $m4PackageZip = [System.IO.Compression.ZipFile]::OpenRead($m4PackagePath)
        try {
            $m4Nuspec = $m4PackageZip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') } | Select-Object -First 1
            $m4PackageReader = [System.IO.StreamReader]::new($m4Nuspec.Open())
            try { [xml]$m4PackageMetadata = $m4PackageReader.ReadToEnd() } finally { $m4PackageReader.Dispose() }
            if ($m4PackageMetadata.package.metadata.repository.commit -ne $m4Package.commit -or
                $m4PackageMetadata.package.metadata.id -ne $m4Package.id -or $m4PackageMetadata.package.metadata.version -ne $m4Package.version) {
                throw "Pinned package producer metadata mismatch: $($m4Package.id)."
            }
        } finally { $m4PackageZip.Dispose() }
    }
    & dotnet restore Marang.slnx --configfile NuGet.AAAWM1.Config
    if ($LASTEXITCODE -ne 0) { throw 'Pinned package restore failed.' }
    & dotnet build Marang.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'M4 solution build failed.' }
    & dotnet format Marang.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Formatting qualification failed.' }
    & dotnet test tests/Marang.Tests/Marang.Tests.csproj -c Release --no-build --no-restore --logger trx --results-directory artifacts/aaaw-m4/final-consumer
    if ($LASTEXITCODE -ne 0) { throw 'M1/M2/M3/M4 package-consumer and process qualification failed.' }
    if ((& git rev-parse HEAD).Trim() -ne $m4QualifiedSource -or @(& git status --porcelain).Count -ne 0) {
        throw 'Source changed during M4 qualification; evidence cannot be attributed to the starting commit.'
    }
    & (Join-Path $PSScriptRoot 'capture-aaaw-m4-evidence.ps1') -QualificationStartedAt $m4QualificationStartedAt
    Write-Host 'M4 causal repository effects qualified. No package was published.'
} finally {
    Pop-Location
    foreach ($m4Name in $m4SavedProcessEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($m4Name, $m4SavedProcessEnvironment[$m4Name], 'Process')
    }
}
