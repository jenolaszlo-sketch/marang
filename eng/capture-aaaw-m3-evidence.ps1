param([Parameter(Mandatory)][datetime]$QualificationStartedAt)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$m3EvidenceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$m3Output = Join-Path $m3EvidenceRoot 'artifacts/aaaw-m3'
function RelativeEvidencePath([string]$path) { [IO.Path]::GetRelativePath($m3EvidenceRoot, $path).Replace('\', '/') }
function EvidenceFile([string]$path) {
    [ordered]@{ path = RelativeEvidencePath $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$m3Runs = foreach ($m3Framework in @('net8.0', 'net10.0')) {
    $m3Trx = Get-ChildItem -LiteralPath (Join-Path $m3Output 'final-consumer') -Filter "*_$m3Framework.trx" |
        Where-Object LastWriteTime -ge $QualificationStartedAt | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $m3Trx) { throw "No final $m3Framework test result from this qualification." }
    [xml]$m3TestDocument = Get-Content -LiteralPath $m3Trx.FullName -Raw
    $m3Counters = $m3TestDocument.TestRun.ResultSummary.Counters
    if ([int]$m3Counters.failed -ne 0 -or [int]$m3Counters.passed -ne [int]$m3Counters.total) { throw "Incomplete final $m3Framework qualification." }
    [ordered]@{ framework = $m3Framework; total = [int]$m3Counters.total; passed = [int]$m3Counters.passed;
        failed = [int]$m3Counters.failed; trx = EvidenceFile $m3Trx.FullName }
}
$m3CompletedFiles = @(Get-ChildItem -LiteralPath (Join-Path $m3Output 'process-probes') -Filter 'repository-effect-result.json' -Recurse |
    Where-Object LastWriteTime -ge $QualificationStartedAt | Sort-Object LastWriteTime -Descending)
$m3ScenarioFile = $m3CompletedFiles | Where-Object {
    $m3Candidate = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    $m3Candidate.recoveryBarriers.Count -eq 9 -and $m3Candidate.finalJoinCompleted -and
        $m3Candidate.originalPhysicalApplyCount -eq 1 -and $m3Candidate.correctivePhysicalApplyCount -eq 1
} | Select-Object -First 1
if ($null -eq $m3ScenarioFile) { throw 'No successful nine-kill continuing workflow from this qualification.' }
$m3Scenario = Get-Content -LiteralPath $m3ScenarioFile.FullName -Raw | ConvertFrom-Json
$m3PartialFile = Get-ChildItem -LiteralPath (Join-Path $m3Output 'process-probes') -Filter 'repository-effect-partial.json' -Recurse |
    Where-Object LastWriteTime -ge $QualificationStartedAt | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $m3PartialFile) { throw 'Missing K3 partial-state qualification.' }
$m3Partial = Get-Content -LiteralPath $m3PartialFile.FullName -Raw | ConvertFrom-Json
if ($m3Partial.classification -ne 'PartiallyApplied' -or -not $m3Partial.automaticReplayBlocked -or $m3Partial.physicalApplyCount -ne 1) { throw 'Partial mutation was not safely parked.' }
$m3Barriers = foreach ($m3Barrier in @('K1','K2','K3','K4','K5','K6','K7','K8','K9','K10')) {
    $m3Fixture = if ($m3Barrier -eq 'K3') { $m3PartialFile.DirectoryName } else { $m3ScenarioFile.DirectoryName }
    $m3Receipt = Join-Path $m3Fixture "barrier-context-$m3Barrier.json"
    if (-not (Test-Path -LiteralPath (Join-Path $m3Fixture "barrier-used-$m3Barrier"))) { throw "Missing killed-process receipt $m3Barrier." }
    [ordered]@{ barrier = $m3Barrier; killedHostProcessId = [int](Get-Content -LiteralPath (Join-Path $m3Fixture "process-id-$m3Barrier.txt"));
        context = EvidenceFile $m3Receipt; observation = Get-Content -LiteralPath $m3Receipt -Raw | ConvertFrom-Json }
}
$m3Effects = foreach ($m3Effect in $m3Scenario.effects) {
    [ordered]@{ effectId = $m3Effect.intent.effectId; workflowId = $m3Effect.intent.workflowId; revisionId = $m3Effect.intent.revisionId;
        executionId = $m3Effect.intent.executionId; attemptId = $m3Effect.intent.attemptId; repositoryId = $m3Effect.intent.repositoryId;
        baseState = $m3Effect.intent.baseState.stateDigest; patchIdentity = $m3Effect.intent.patchIdentity; intentDigest = $m3Effect.intent.intentDigest;
        expectedPostState = $m3Effect.intent.expectedPostState.stateDigest; observedState = $m3Effect.observedState.stateDigest;
        authorityEvidence = $m3Effect.authorityEvidence; stage = $m3Effect.stage; classification = $m3Effect.classification; validation = $m3Effect.validation }
}
$m3Evidence = [ordered]@{
    schema = 'aaaw-m3-qualification/v1'; qualifiedAt = [DateTimeOffset]::UtcNow.ToString('O');
    hostRepository = 'Marang'; hostBranch = (& git -C $m3EvidenceRoot branch --show-current).Trim(); hostSourceCommit = (& git -C $m3EvidenceRoot rev-parse HEAD).Trim();
    fixtureSourceCommit = $m3Scenario.sourceCommit; fixtureBranch = $m3Scenario.branch;
    operatingSystem = [Environment]::OSVersion.ToString(); sdk = (& dotnet --version).Trim();
    packages = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'aaaw-m2-packages.json') -Raw | ConvertFrom-Json;
    testRuns = @($m3Runs); scenario = EvidenceFile $m3ScenarioFile.FullName; partialScenario = EvidenceFile $m3PartialFile.FullName;
    barriers = @($m3Barriers); effects = @($m3Effects);
    inventoryExecutionId = $m3Scenario.inventoryExecutionId; inventoryInvocations = $m3Scenario.inventoryInvocationCount;
    originalMutationRounds = $m3Scenario.originalPhysicalApplyCount; correctiveMutationRounds = $m3Scenario.correctivePhysicalApplyCount;
    finalJoinCompleted = $m3Scenario.finalJoinCompleted; catchUpExplainsLineage = $m3Scenario.catchUpExplainsEffectLineage;
    drillDownShowsAppliedHistory = $m3Scenario.drillDownShowsSupersededAppliedEffect;
    supportedClaim = 'Within the qualified controlled repository checkout, durable intent and physical reconciliation prevent unsafe replay and permit an independently approved, exactly based corrective revision.';
    limits = @('Windows process-kill qualification; no power-loss guarantee', 'One exclusive controlled Git checkout and owner journal',
        'One to four regular tracked file replacements; 1 MiB total patch content', 'No automatic partial/unresolved recovery, merge, rollback or compensation',
        'No general exactly-once external-effect claim', 'Stable foundations and Hufu/Gagamba implementation unchanged', 'No package publication or push')
}
$m3Evidence | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $m3Output 'qualification.json')
Write-Host 'M3 machine evidence captured from this qualification only.'
