$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$m5Root=(Get-Location).Path
$m5Start=[DateTimeOffset]::UtcNow
$m5Head=(git rev-parse HEAD).Trim()
if (@(git status --porcelain).Count -ne 0) { throw 'Source must be clean before qualification.' }
$m5Output=Join-Path $m5Root 'artifacts/aaaw-m5/windows-final'
New-Item -ItemType Directory -Force -Path $m5Output | Out-Null
$env:MSBUILDDISABLENODEREUSE='1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1'
$env:VSTEST_CONNECTION_TIMEOUT='180'
$m5Evidence=[ordered]@{schema='aaaw-m5-windows-qualification/v1';startedAt=$m5Start.ToString('O');sourceCommit=$m5Head;branch=(git branch --show-current).Trim();sourceCleanAtStart=$true;os=[Environment]::OSVersion.ToString();architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString();sdk=(dotnet --version).Trim();runtimes=@(dotnet --list-runtimes);commands=@();testRuns=@();qualificationPassed=$false}
try {
 $m5Specs=@(
  @{name='bootstrap';argv=@('python','eng/bootstrap-aaaw-m5-feed.py')},
  @{name='restore';argv=@('dotnet','restore','Marang.slnx','--configfile','NuGet.AAAWM1.Config')},
  @{name='build';argv=@('dotnet','build','Marang.slnx','-c','Release','--no-restore','-m:1','-p:UseSharedCompilation=false')},
  @{name='format';argv=@('dotnet','format','Marang.slnx','--verify-no-changes','--no-restore')},
  @{name='test';argv=@('dotnet','test','tests/Marang.Tests/Marang.Tests.csproj','-c','Release','--no-build','--no-restore','--logger','trx','--results-directory',$m5Output)}
 )
 foreach ($m5Spec in $m5Specs) {
  $m5Executable=$m5Spec.argv[0]; $m5Arguments=@($m5Spec.argv | Select-Object -Skip 1)
  & $m5Executable @m5Arguments *>&1 | Tee-Object -FilePath (Join-Path $m5Output ($m5Spec.name+'.log'))
  $m5Exit=$LASTEXITCODE
  $m5Evidence.commands+=@{name=$m5Spec.name;argv=$m5Spec.argv;exitCode=$m5Exit}
  if ($m5Exit -ne 0) { throw "Qualification command failed: $($m5Spec.name)" }
 }
 foreach ($m5Framework in @('net8.0','net10.0')) {
  $m5Trx=Get-ChildItem -LiteralPath $m5Output -Filter "*_$m5Framework.trx" | Where-Object LastWriteTimeUtc -ge $m5Start.UtcDateTime | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  if ($null -eq $m5Trx) { throw "Missing current $m5Framework TRX" }
  [xml]$m5Doc=Get-Content -LiteralPath $m5Trx.FullName -Raw
  $m5Counters=$m5Doc.TestRun.ResultSummary.Counters
  $m5Skips=@($m5Doc.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'NotExecuted' | ForEach-Object testName)
  $m5ExpectedPass=if ($m5Framework -eq 'net8.0') {201} else {246}
  if ([int]$m5Counters.failed -ne 0 -or [int]$m5Counters.passed -ne $m5ExpectedPass -or $m5Skips.Count -ne 1 -or $m5Skips[0] -notlike '*Distinct_case_sensitive_git_roots_have_distinct_repository_identities*') { throw "Unexpected failures/counts/skips for $m5Framework" }
  $m5Evidence.testRuns+=@{framework=$m5Framework;total=[int]$m5Counters.total;passed=[int]$m5Counters.passed;failed=[int]$m5Counters.failed;skipped=$m5Skips.Count;skippedNames=$m5Skips;skipDisposition='Case-only sibling roots are not distinct on this Windows temporary filesystem; Linux exercised the test natively.';trx=$m5Trx.Name;sha256=(Get-FileHash -LiteralPath $m5Trx.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
 }
 $m5Evidence.qualificationPassed=$true
} catch { $m5Evidence.failure=$_.Exception.Message; throw }
finally {
 $m5Evidence.sourceCommitAtEnd=(git rev-parse HEAD).Trim()
 $m5Evidence.sourceCleanAtEnd=@(git status --porcelain).Count -eq 0
 if ($m5Evidence.sourceCommitAtEnd -ne $m5Head -or -not $m5Evidence.sourceCleanAtEnd) {$m5Evidence.qualificationPassed=$false}
 $m5Evidence.completedAt=[DateTimeOffset]::UtcNow.ToString('O')
 $m5Evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $m5Output 'qualification.json')
}
