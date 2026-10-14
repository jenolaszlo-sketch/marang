param(
    [string]$ZhinuCheckout = (Join-Path $PSScriptRoot '../../../aaaw-m1/zhinu'),
    [string]$QingniaoCheckout = (Join-Path $PSScriptRoot '../../../aaaw-m1/qingniao'),
    [switch]$ReuseQualifiedPackages
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$m2Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
Push-Location -LiteralPath $m2Root
try {
    if (-not $ReuseQualifiedPackages) {
        & (Join-Path $PSScriptRoot 'qualify-aaaw-m1.ps1') -PackagesOnly -ZhinuCheckout $ZhinuCheckout -QingniaoCheckout $QingniaoCheckout
    }
    $m2PackageLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'aaaw-m2-packages.json') -Raw | ConvertFrom-Json
    foreach ($m2Package in $m2PackageLock) {
        $m2PackagePath = Join-Path $m2Root "artifacts/aaaw-m1/packages/$($m2Package.id).$($m2Package.version).nupkg"
        if (-not (Test-Path -LiteralPath $m2PackagePath)) { throw "Missing pinned package $($m2Package.id)." }
        if ($ReuseQualifiedPackages -and (Get-FileHash -LiteralPath $m2PackagePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $m2Package.sha256) {
            throw "Reused package does not match the original M1 qualification hash: $($m2Package.id)."
        }
        $m2PackageZip = [System.IO.Compression.ZipFile]::OpenRead($m2PackagePath)
        try {
            $m2Nuspec = $m2PackageZip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') } | Select-Object -First 1
            $m2PackageReader = [System.IO.StreamReader]::new($m2Nuspec.Open())
            try { [xml]$m2PackageMetadata = $m2PackageReader.ReadToEnd() } finally { $m2PackageReader.Dispose() }
            if ($m2PackageMetadata.package.metadata.repository.commit -ne $m2Package.commit -or
                $m2PackageMetadata.package.metadata.id -ne $m2Package.id -or $m2PackageMetadata.package.metadata.version -ne $m2Package.version) {
                throw "Pinned package producer metadata mismatch: $($m2Package.id)."
            }
        } finally { $m2PackageZip.Dispose() }
    }
    & dotnet restore Marang.slnx --configfile NuGet.AAAWM1.Config
    if ($LASTEXITCODE -ne 0) { throw 'Pinned package restore failed.' }
    & dotnet build Marang.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'M2 solution build failed.' }
    & dotnet format Marang.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Formatting qualification failed.' }
    & dotnet test tests/Marang.Tests/Marang.Tests.csproj -c Release --no-build --no-restore --logger trx --results-directory artifacts/aaaw-m2/final-consumer
    if ($LASTEXITCODE -ne 0) { throw 'M1/M2 package-consumer and process qualification failed.' }
    Write-Host 'M2 continuity qualified. No package was published.'
} finally { Pop-Location }
