param([Parameter(Mandatory)][datetime]$QualificationStartedAt)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$m4EvidenceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$m4Output = Join-Path $m4EvidenceRoot 'artifacts/aaaw-m4'
function RelativeEvidencePath([string]$path) { [IO.Path]::GetRelativePath($m4EvidenceRoot, $path).Replace('\', '/') }
function EvidenceFile([string]$path) {
    [ordered]@{ path = RelativeEvidencePath $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$m4Runs = foreach ($m4Framework in @('net8.0', 'net10.0')) {
    $m4Trx = Get-ChildItem -LiteralPath (Join-Path $m4Output 'final-consumer') -Filter "*_$m4Framework.trx" |
        Where-Object LastWriteTime -ge $QualificationStartedAt | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $m4Trx) { throw "No final $m4Framework test result from this qualification." }
    [xml]$m4TestDocument = Get-Content -LiteralPath $m4Trx.FullName -Raw
    $m4Counters = $m4TestDocument.TestRun.ResultSummary.Counters
    if ([int]$m4Counters.failed -ne 0 -or [int]$m4Counters.passed -ne [int]$m4Counters.total) { throw "Incomplete final $m4Framework qualification." }
    [ordered]@{ framework = $m4Framework; total = [int]$m4Counters.total; passed = [int]$m4Counters.passed;
        failed = [int]$m4Counters.failed; trx = EvidenceFile $m4Trx.FullName }
}
$m4ScenarioFile = Get-ChildItem -LiteralPath (Join-Path $m4Output 'process-probes') -Filter 'multi-effect-result.json' -Recurse |
    Where-Object LastWriteTime -ge $QualificationStartedAt | Sort-Object LastWriteTime -Descending | Where-Object {
        $m4Candidate = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        $m4Candidate.recoveryBarriers.Count -eq 11 -and $m4Candidate.finalBranchCompleted -and
            $m4Candidate.workflowValidationPassed -and $m4Candidate.noDuplicateMutation -and
            $m4Candidate.inventoryInvocationCount -eq 1 -and $m4Candidate.aWasExplicitlyAdopted -and
            $m4Candidate.oldBAndCRemainAppliedEvidence -and -not $m4Candidate.oldGraphValidationPassed
    } | Select-Object -First 1
if ($null -eq $m4ScenarioFile) { throw 'No successful eleven-kill continuing workflow from this qualification.' }
$m4Scenario = Get-Content -LiteralPath $m4ScenarioFile.FullName -Raw | ConvertFrom-Json
foreach ($m4Name in @('A', 'B', 'C', 'B2', 'C2')) {
    if ($m4Scenario.effectApplyCounts.$m4Name -ne 1) { throw "Incorrect physical application count for $m4Name." }
}
if (($m4Scenario.acceptedLogicalEffects -join ',') -ne 'A,B2,C2') { throw 'Incorrect corrective accepted chain.' }
if (($m4Scenario.activeAcceptedEffects -join ',') -ne 'A,B2,C2' -or -not $m4Scenario.exactStateLineage) { throw 'Current accepted effects or exact physical lineage were not proven.' }
if ($m4Scenario.catchUp.mutationBlocked) { throw 'Final active graph is still blocked.' }
$m4PartialFile = Get-ChildItem -LiteralPath (Join-Path $m4Output 'process-probes') -Filter 'multi-effect-K4-result.json' -Recurse |
    Where-Object LastWriteTime -ge $QualificationStartedAt | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $m4PartialFile) { throw 'Missing K4 partial-state qualification.' }
$m4Partial = Get-Content -LiteralPath $m4PartialFile.FullName -Raw | ConvertFrom-Json
if ($m4Partial.prerequisiteClassification -ne 'PartiallyApplied' -or -not $m4Partial.dependentEffectBlocked -or
    $m4Partial.physicalApplyCount -ne 1 -or $m4Partial.dependentPhysicalApplyCount -ne 0 -or
    $m4Partial.dependentStage -ne 'Prepared' -or $m4Partial.dependentBlockedByEffectIds.Count -eq 0) {
    throw 'Partial B did not block its named downstream effect without replay.'
}
$m4Barriers = foreach ($m4Barrier in @('K1','K2','K3','K4','K5','K6','K7','K8','K9','K10','K11','K12')) {
    $m4Fixture = if ($m4Barrier -eq 'K4') { $m4PartialFile.DirectoryName } else { $m4ScenarioFile.DirectoryName }
    $m4Receipt = Join-Path $m4Fixture "barrier-context-$m4Barrier.json"
    if (-not (Test-Path -LiteralPath (Join-Path $m4Fixture "barrier-used-$m4Barrier"))) { throw "Missing killed-process receipt $m4Barrier." }
    [ordered]@{ barrier = $m4Barrier; killedHostProcessId = [int](Get-Content -LiteralPath (Join-Path $m4Fixture "process-id-$m4Barrier.txt"));
        context = EvidenceFile $m4Receipt; observation = Get-Content -LiteralPath $m4Receipt -Raw | ConvertFrom-Json }
}
$m4ValidationContext = Join-Path $m4ScenarioFile.DirectoryName 'validation-context-K12.json'
$m4K12 = Get-Content -LiteralPath $m4ValidationContext -Raw | ConvertFrom-Json
if (-not $m4K12.validationProcessRunning) { throw 'K12 did not interrupt a running final validator.' }
$m4Effects = foreach ($m4Effect in $m4Scenario.effects) {
    $m4Causal = @($m4Scenario.graphs | ForEach-Object { $_.effects } | Where-Object { $_.effect.intent.effectId -eq $m4Effect.intent.effectId })[0].effect
    [ordered]@{ effectId = $m4Effect.intent.effectId; logicalId = $m4Effect.intent.logicalStepId;
        workflowId = $m4Effect.intent.workflowId; revisionId = $m4Effect.intent.revisionId;
        executionId = $m4Effect.intent.executionId; attemptId = $m4Effect.intent.attemptId; repositoryId = $m4Effect.intent.repositoryId;
        baseState = $m4Effect.intent.baseState.stateDigest; patchIdentity = $m4Effect.intent.patchIdentity; intentDigest = $m4Effect.intent.intentDigest;
        causalDigest = $m4Causal.causalDigest; prerequisites = @($m4Causal.prerequisites);
        requirementEvidence = $m4Causal.requirementEvidence; expectedPostState = $m4Effect.intent.expectedPostState.stateDigest;
        observedState = $m4Effect.observedState.stateDigest; authorityEvidence = $m4Effect.authorityEvidence;
        stage = $m4Effect.stage; classification = $m4Effect.classification; localValidation = $m4Effect.validation }
}
$m4Evidence = [ordered]@{
    schema = 'aaaw-m4-qualification/v1'; qualifiedAt = [DateTimeOffset]::UtcNow.ToString('O');
    hostRepository = 'Marang'; hostBranch = (& git -C $m4EvidenceRoot branch --show-current).Trim(); hostSourceCommit = (& git -C $m4EvidenceRoot rev-parse HEAD).Trim();
    fixtureSourceCommit = $m4Scenario.sourceCommit; fixtureBranch = 'm4-multi-effect-qualification';
    operatingSystem = [Environment]::OSVersion.ToString(); sdk = (& dotnet --version).Trim();
    packages = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'aaaw-m2-packages.json') -Raw | ConvertFrom-Json;
    testRuns = @($m4Runs); scenario = EvidenceFile $m4ScenarioFile.FullName; partialScenario = EvidenceFile $m4PartialFile.FullName;
    barriers = @($m4Barriers); interruptedFinalValidation = EvidenceFile $m4ValidationContext;
    effects = @($m4Effects); graphs = @($m4Scenario.graphs); prerequisiteAuthority = @($m4Scenario.prerequisiteAuthority);
    inventoryInvocations = $m4Scenario.inventoryInvocationCount; effectApplyCounts = $m4Scenario.effectApplyCounts;
    acceptedLogicalEffects = $m4Scenario.acceptedLogicalEffects; historicalPhysicalEffects = $m4Scenario.historicalPhysicalEffects;
    activeAcceptedEffects = $m4Scenario.activeAcceptedEffects; exactStateLineage = $m4Scenario.exactStateLineage;
    invalidatedPriorEffects = $m4Scenario.invalidatedR2Effects;
    finalRevision = $m4Scenario.finalRevision; finalJoinCompleted = $m4Scenario.finalBranchCompleted;
    originalWorkflowValidationPassed = $m4Scenario.oldGraphValidationPassed; correctiveWorkflowValidationPassed = $m4Scenario.workflowValidationPassed;
    finalState = $m4Scenario.physicalState.stateDigest;
    supportedClaim = 'Within the bounded Windows repository profile, Penghou coordinates dependent durable effects across restart, preserves compatible upstream effects, invalidates changed downstream assumptions, and executes an approved corrective graph against actual physical state without duplicate mutation.';
    limits = @('Windows process-kill qualification; no power-loss guarantee', 'One exclusive controlled Git checkout and owner journal',
        'One to eight selected effects; serial exact physical lineage; independent read scheduling retained',
        'One to four regular tracked replacements per effect; 1 MiB total patch content per effect',
        'No automatic partial/unresolved recovery, merge, rollback or compensation', 'No general exactly-once external-effect claim',
        'Stable foundations and Hufu/Gagamba implementation unchanged', 'No package publication or push')
}
$m4Evidence | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $m4Output 'qualification.json')
Write-Host 'M4 machine evidence captured from this qualification only.'
