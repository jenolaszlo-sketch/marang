param(
    [string]$ZhinuCheckout = (Join-Path $PSScriptRoot '../../../aaaw-m1/zhinu'),
    [string]$QingniaoCheckout = (Join-Path $PSScriptRoot '../../../aaaw-m1/qingniao'),
    [switch]$ReuseQualifiedPackages
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$m3Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$m3QualificationStartedAt = Get-Date
Add-Type -AssemblyName System.IO.Compression.FileSystem
Push-Location -LiteralPath $m3Root
try {
    if (-not $ReuseQualifiedPackages) {
        & (Join-Path $PSScriptRoot 'qualify-aaaw-m1.ps1') -PackagesOnly -ZhinuCheckout $ZhinuCheckout -QingniaoCheckout $QingniaoCheckout
    }
    $m3PackageLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'aaaw-m2-packages.json') -Raw | ConvertFrom-Json
    foreach ($m3Package in $m3PackageLock) {
        $m3PackagePath = Join-Path $m3Root "artifacts/aaaw-m1/packages/$($m3Package.id).$($m3Package.version).nupkg"
        if (-not (Test-Path -LiteralPath $m3PackagePath)) { throw "Missing pinned package $($m3Package.id)." }
        if ($ReuseQualifiedPackages -and (Get-FileHash -LiteralPath $m3PackagePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $m3Package.sha256) {
            throw "Reused package does not match the original M1 qualification hash: $($m3Package.id)."
        }
        $m3PackageZip = [System.IO.Compression.ZipFile]::OpenRead($m3PackagePath)
        try {
            $m3Nuspec = $m3PackageZip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') } | Select-Object -First 1
            $m3PackageReader = [System.IO.StreamReader]::new($m3Nuspec.Open())
            try { [xml]$m3PackageMetadata = $m3PackageReader.ReadToEnd() } finally { $m3PackageReader.Dispose() }
            if ($m3PackageMetadata.package.metadata.repository.commit -ne $m3Package.commit -or
                $m3PackageMetadata.package.metadata.id -ne $m3Package.id -or $m3PackageMetadata.package.metadata.version -ne $m3Package.version) {
                throw "Pinned package producer metadata mismatch: $($m3Package.id)."
            }
        } finally { $m3PackageZip.Dispose() }
    }
    & dotnet restore Marang.slnx --configfile NuGet.AAAWM1.Config
    if ($LASTEXITCODE -ne 0) { throw 'Pinned package restore failed.' }
    & dotnet build Marang.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'M3 solution build failed.' }
    & dotnet format Marang.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Formatting qualification failed.' }
    & dotnet test tests/Marang.Tests/Marang.Tests.csproj -c Release --no-build --no-restore --logger trx --results-directory artifacts/aaaw-m3/final-consumer
    if ($LASTEXITCODE -ne 0) { throw 'M1/M2/M3 package-consumer and process qualification failed.' }
    & (Join-Path $PSScriptRoot 'capture-aaaw-m3-evidence.ps1') -QualificationStartedAt $m3QualificationStartedAt
    Write-Host 'M3 repository effects qualified. No package was published.'
} finally { Pop-Location }
