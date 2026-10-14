param(
    [string]$ZhinuCheckout = (Join-Path $PSScriptRoot '../../zhinu'),
    [string]$QingniaoCheckout = (Join-Path $PSScriptRoot '../../qingniao'),
    [switch]$FullSourceQualification,
    [switch]$PackagesOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'aaaw-m1-sources.json') -Raw | ConvertFrom-Json
$packageFeed = Join-Path $taskRoot 'artifacts/aaaw-m1/packages'
New-Item -ItemType Directory -Path $packageFeed -Force | Out-Null

function Invoke-TaskCommand([string]$Directory, [string]$Command, [string[]]$Arguments) {
    Push-Location -LiteralPath $Directory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
    } finally { Pop-Location }
}
function Assert-Source([string]$Checkout, [string]$ExpectedCommit) {
    $actual = (& git -C $Checkout rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $ExpectedCommit) { throw "Source checkout must be at $ExpectedCommit" }
    $changes = & git -C $Checkout status --porcelain
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Qualification source checkout must be clean.' }
}
Assert-Source $ZhinuCheckout $sourceLock.zhinu.commit
Assert-Source $QingniaoCheckout $sourceLock.qingniao.commit

foreach ($project in @('Penghou.Zhinu', 'Penghou.Zhinu.Sqlite')) {
    Invoke-TaskCommand $ZhinuCheckout 'dotnet' @('pack', "src/$project/$project.csproj", '-c', 'Release',
        "-p:Version=$($sourceLock.zhinu.packageVersion)", '-p:ContinuousIntegrationBuild=true', '-o', $packageFeed)
}
foreach ($project in @('Penghou.Qingniao.Abstractions', 'Penghou.Qingniao')) {
    Invoke-TaskCommand $QingniaoCheckout 'dotnet' @('pack', "src/$project/$project.csproj", '-c', 'Release',
        "-p:Version=$($sourceLock.qingniao.packageVersion)", '-p:ContinuousIntegrationBuild=true', '-o', $packageFeed)
}
$packageEvidence = Get-ChildItem -LiteralPath $packageFeed -Filter '*.nupkg' |
    Where-Object { $_.Name.Contains($sourceLock.zhinu.packageVersion) -or $_.Name.Contains($sourceLock.qingniao.packageVersion) } |
    ForEach-Object { @{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }
$packageEvidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts/aaaw-m1/package-evidence.json') -Encoding utf8
if ($PackagesOnly) { return }

$runtimeArgs = @('test', 'tests/Penghou.Zhinu.Sqlite.Tests/Penghou.Zhinu.Sqlite.Tests.csproj', '-c', 'Release',
    '--logger', 'trx', '--results-directory', 'artifacts/aaaw-m1/final-source')
if (-not $FullSourceQualification) { $runtimeArgs += @('--filter', 'FullyQualifiedName~AdaptiveRevisionTests') }
Invoke-TaskCommand $ZhinuCheckout 'dotnet' $runtimeArgs
$delegationArgs = @('test', 'tests/Penghou.Qingniao.Tests/Penghou.Qingniao.Tests.csproj', '-c', 'Release',
    '--logger', 'trx', '--results-directory', 'artifacts/aaaw-m1/final-source')
if (-not $FullSourceQualification) { $delegationArgs += @('--filter', 'FullyQualifiedName~DurableExternalOperationProviderTests') }
Invoke-TaskCommand $QingniaoCheckout 'dotnet' $delegationArgs
Invoke-TaskCommand $taskRoot 'dotnet' @('restore', 'tests/Marang.Tests/Marang.Tests.csproj', '--configfile', 'NuGet.AAAWM1.Config')
Invoke-TaskCommand $taskRoot 'dotnet' @('test', 'tests/Marang.Tests/Marang.Tests.csproj', '-c', 'Release', '--no-restore',
    '--logger', 'trx', '--results-directory', 'artifacts/aaaw-m1/final-consumer')

Write-Host 'M1 source/package consumer and fresh-process qualification complete. No package was published.'
