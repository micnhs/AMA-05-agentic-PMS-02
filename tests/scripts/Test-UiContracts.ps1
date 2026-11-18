#requires -Version 7.6
[CmdletBinding()]
param([string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$helper = Join-Path $root 'scripts\Test-Ui.ps1'
. (Join-Path $root 'scripts\Backup-Local.ps1')
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $root ('.local\ui-contracts-' + [Guid]::NewGuid().ToString('N')) }
$proofRoot = Assert-RecoveryPath $ArtifactDirectory -New
$text = [IO.File]::ReadAllText($helper)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'The actual browser runner does not parse.' }
foreach ($name in @('HashFile','WriteDurableJson','ResourceStage','CleanupStep','Restore-BrowserEnvironment','RemoveRuntime','Get-BrowserSelection','Assert-BrowserSelection')) {
    $nodes = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $true))
    if ($nodes.Count -ne 1) { throw 'An actual runner function is ambiguous.' }
    . ([scriptblock]::Create($nodes[0].Extent.Text))
}
$main = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.TryStatementAst] -and
    $n.Body.Statements.Count -gt 0 -and $n.Body.Statements[0].Extent.Text -ceq '$master.Open()' }, $true))
if ($main.Count -ne 1) { throw 'The actual resource registration block is ambiguous.' }
$last = @($main[0].Body.Statements | Where-Object { $_.Extent.Text -ceq "ResourceStage 'DatabaseCreated'" })
if ($last.Count -ne 1) { throw 'Database-created checkpoint missing.' }
$begin = $main[0].Body.Statements[0].Extent.StartOffset
$registrationText = $text.Substring($begin, $last[0].Extent.EndOffset - $begin)
if ([regex]::Matches($registrationText, [regex]::Escape('HashFile $PSCommandPath')).Count -ne 1) {
    throw 'Expected the one execution-file metadata reference in the actual registration block.'
}
$registration = [scriptblock]::Create($registrationText.Replace('HashFile $PSCommandPath', "'" + (HashFile $helper) + "'"))
$tailBegin = $text.LastIndexOf('if ($cleanupErrors.Count) {', [StringComparison]::Ordinal)
$tailEnd = $text.IndexOf('if (-not $passed', $tailBegin, [StringComparison]::Ordinal)
if ($tailBegin -lt 0 -or $tailEnd -le $tailBegin) { throw 'The actual independent failure-preservation tail is missing.' }
$errorTail = [scriptblock]::Create($text.Substring($tailBegin, $tailEnd - $tailBegin))
$master = [pscustomobject]@{}
$master | Add-Member -MemberType ScriptMethod -Name Open -Value {}
function DatabaseNames { @('master', 'Cohors') }
function Invoke-RecoverySql($connection, [string]$sql) {
    if ($sql -cne "CREATE DATABASE [$database]") { throw 'Only the synthetic CREATE callback is permitted; no SQL is opened.' }
    $intent = Get-Content $intentPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $stages = @(Get-Content $journalPath | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -DateKind String })
    if (-not $intent.databaseAbsentBeforeCreate -or -not $intent.runtimeAbsentBeforeCreate -or
        $stages.Count -ne 1 -or $stages[0].phase -cne 'CreateRequested' -or
        $stages[0].intentSha256 -cne (HashFile $intentPath) -or $stages[0].apiPid) {
        throw 'Intent and flushed journal must be readable at the first SQL boundary, before any API.'
    }
    $script:sqlCallbacks++
}
New-Item -ItemType Directory -Path $proofRoot | Out-Null
$checks = [Collections.Generic.List[object]]::new()
foreach ($variant in @('RegisteredBeforeCreate','JournalWriteBlocked','RuntimeAlreadyExists','UnexpectedRuntimeObject')) {
    $artifacts = Join-Path $proofRoot $variant
    New-Item -ItemType Directory -Path $artifacts | Out-Null
    $runtime = Join-Path $artifacts 'runtime'; $private = Join-Path $runtime '.local'
    $settingsPath = Join-Path $private 'settings.json'; $accessPath = Join-Path $private 'demo-access.txt'
    $intentPath = Join-Path $artifacts 'owned-resources-intent.json'; $journalPath = Join-Path $artifacts 'owned-resources-journal.jsonl'
    $database = 'Cohors_Test_' + [Guid]::NewGuid().ToString('N')
    $application = Join-Path $root '.local\contract-placeholder-application'; $dotnet = 'not-executed'
    $Port = 5197; $apiPid = $null; $intentHash = $null; $registered = $false; $createRequested = $false; $runtimeCreated = $false
    $created = $false; $sqlCallbacks = 0; $resourceClock = [Diagnostics.Stopwatch]::StartNew(); $failure = $null; $lock = $null
    try {
        if ($variant -ceq 'JournalWriteBlocked') {
            $lock = [IO.FileStream]::new($journalPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        }
        if ($variant -ceq 'RuntimeAlreadyExists') { New-Item -ItemType Directory -Path $runtime | Out-Null }
        try { . $registration } catch { $failure = $_ }
    } finally { if ($lock) { $lock.Dispose() } }
    if ($variant -in @('RegisteredBeforeCreate','UnexpectedRuntimeObject')) {
        if ($failure -or $sqlCallbacks -ne 1 -or -not $registered -or -not $runtimeCreated -or -not $created) {
            throw 'The actual pre-create registration did not complete.'
        }
        [IO.File]::WriteAllText($settingsPath, '{}'); [IO.File]::WriteAllText($accessPath, 'synthetic-proof-only')
        $objects = Join-Path $private 'blobs'
        New-Item -ItemType Directory -Path $objects | Out-Null
        $bytes = [Text.Encoding]::UTF8.GetBytes('synthetic ownership proof')
        $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
        $objectPath = Join-Path $objects $hash
        [IO.File]::WriteAllBytes($objectPath, $bytes)
        ResourceStage 'SchemaReady'; ResourceStage 'Ready'; ResourceStage 'CleanupRequested'
        if ($variant -ceq 'UnexpectedRuntimeObject') {
            $unexpected = Join-Path $objects ('0' * 64)
            [IO.File]::WriteAllText($unexpected, 'not content-addressed')
            $refused = $false
            try { RemoveRuntime } catch {
                if ($_.Exception.Message -cne 'Unexpected or non-addressed owned object.') { throw }
                $refused = $true
            }
            if (-not $refused -or -not (Test-Path $settingsPath) -or -not (Test-Path $accessPath) -or
                -not (Test-Path $objectPath) -or -not (Test-Path $unexpected)) { throw 'Unknown content was not rejected before any deletion.' }
            [IO.File]::Delete($unexpected)
        }
        RemoveRuntime
        ResourceStage 'Disposed'
        $stages = @(Get-Content $journalPath | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -DateKind String })
        if (($stages.phase -join ',') -cne 'CreateRequested,DatabaseCreated,SchemaReady,Ready,CleanupRequested,Disposed' -or
            (Test-Path $runtime) -or @($stages | Where-Object apiPid).Count) { throw 'Complete journal or exact file cleanup failed.' }
    } else {
        if (-not $failure -or $sqlCallbacks -ne 0 -or $registered -or $runtimeCreated) { throw 'Rejected setup reached a resource-creation boundary.' }
        if ($variant -ceq 'RuntimeAlreadyExists') {
            if (Test-Path $intentPath) { throw 'Pre-existing runtime unexpectedly acquired ownership.' }
            [IO.Directory]::Delete($runtime)
        } elseif (Test-Path $runtime) { throw 'Blocked journal still created a runtime.' }
    }
    $checks.Add(@{ variant = $variant; success = $true; syntheticSqlCallbacks = $sqlCallbacks; runtimeAbsent = -not (Test-Path $runtime) })
}
$cleanupErrors = [Collections.Generic.List[Exception]]::new()
try { throw [InvalidOperationException]::new('synthetic-execution-failure') } catch { $originalError = $_ }
CleanupStep { throw [IO.IOException]::new('synthetic-journal-failure') }
$cleanupContinued = $false
CleanupStep { $script:cleanupContinued = $true }
$preserved = $false
try { . $errorTail } catch {
    if ($_.Exception -is [AggregateException] -and $_.Exception.InnerExceptions.Count -eq 2 -and
        $_.Exception.InnerExceptions[0].Message -ceq 'synthetic-execution-failure' -and
        $_.Exception.InnerExceptions[1].Message -ceq 'synthetic-journal-failure') { $preserved = $true }
    else { throw }
}
if (-not $cleanupContinued -or -not $preserved) { throw 'Original and cleanup failures were not preserved independently.' }
$checks.Add(@{ variant = 'IndependentCleanupErrors'; success = $true })
$artifacts = Join-Path $proofRoot 'UnstoppedApi'
$runtime = Join-Path $artifacts 'runtime'; $private = Join-Path $runtime '.local'
$settingsPath = Join-Path $private 'settings.json'; $accessPath = Join-Path $private 'demo-access.txt'
$journalPath = Join-Path $artifacts 'owned-resources-journal.jsonl'
New-Item -ItemType Directory -Path $private | Out-Null
[IO.File]::WriteAllText($settingsPath, 'owned-runtime-must-remain')
$previous = @{}; $registered = $false; $intentHash = $null; $apiPid = 1
$apiStopped = $false; $databaseRemoved = $false; $createRequested = $true; $runtimeCreated = $true; $passed = $false
$cleanupErrors = [Collections.Generic.List[Exception]]::new()
$stopBudget = $null; $killRequested = $false; $apiDisposed = $false; $sqlCleanupCalls = 0
$apiProcess = [pscustomobject]@{ HasExited = $false }
$apiProcess | Add-Member -MemberType ScriptMethod -Name Kill -Value { param($tree) $script:killRequested = $tree }
$apiProcess | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($milliseconds) $script:stopBudget = $milliseconds; return $false }
$apiProcess | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $script:apiDisposed = $true }
$master | Add-Member -MemberType ScriptMethod -Name Dispose -Value {}
function Invoke-BrowserCleanupSql { $script:sqlCleanupCalls++; throw 'SQL cleanup must not run while API stop is unproven.' }
$finallyBlock = [scriptblock]::Create(($main[0].Finally.Statements | ForEach-Object { $_.Extent.Text }) -join "`n")
. $finallyBlock
if ($apiStopped -or $databaseRemoved -or $sqlCleanupCalls -ne 0 -or -not $killRequested -or $stopBudget -ne 10000 -or
    -not $apiDisposed -or $cleanupErrors.Count -ne 2 -or
    [IO.File]::ReadAllText($settingsPath) -cne 'owned-runtime-must-remain') {
    throw 'Failed API stop must retain database/runtime and fail finitely without suppressing cleanup errors.'
}
RemoveRuntime
$checks.Add(@{ variant = 'UnstoppedApiPreservesResources'; success = $true; fakeProcessOnly = $true })
$apiStops = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and
    $n.GetCommandName() -ceq 'CleanupStep' -and $n.Extent.Text.Contains('$apiProcess.WaitForExit(10000)') }, $true))
if ($apiStops.Count -ne 1) { throw 'The actual finite API cleanup block is ambiguous.' }
$stopBody = $apiStops[0].CommandElements[1].ScriptBlock.EndBlock.Statements
$stopBlock = [scriptblock]::Create(($stopBody | ForEach-Object { $_.Extent.Text }) -join "`n")
$apiProcess = [pscustomobject]@{ HasExited = $true }
$apiProcess | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($milliseconds) return $true }
$pending = [Threading.Tasks.TaskCompletionSource[string]]::new()
$completed = [Threading.Tasks.TaskCompletionSource[string]]::new()
$completed.SetResult('')
$stdout = $pending.Task; $stderr = $completed.Task
$apiStopped = $false; $streamFailure = $false
try { . $stopBlock } catch {
    if ($_.Exception.Message -cne 'API streams retained after three seconds; independent cleanup is required.') { throw }
    $streamFailure = $true
}
$pending.SetResult('')
if (-not $streamFailure -or -not $apiStopped) { throw 'Unfinished API streams must fail without an unbounded drain.' }
$checks.Add(@{ variant = 'UnfinishedApiStreamsFailFinitely'; success = $true; fakeProcessOnly = $true })
$artifacts = Join-Path $proofRoot 'UnremovedDatabase'
$runtime = Join-Path $artifacts 'runtime'; $private = Join-Path $runtime '.local'
$settingsPath = Join-Path $private 'settings.json'; $accessPath = Join-Path $private 'demo-access.txt'
$journalPath = Join-Path $artifacts 'owned-resources-journal.jsonl'
New-Item -ItemType Directory -Path $private | Out-Null
[IO.File]::WriteAllText($settingsPath, 'database-not-removed')
$cleanupErrors = [Collections.Generic.List[Exception]]::new()
$apiStopped = $false; $databaseRemoved = $false; $sqlCleanupCalls = 0; $originalError = $null
$stdout = $completed.Task; $stderr = $completed.Task
$apiProcess | Add-Member -MemberType ScriptMethod -Name Dispose -Value {}
$master | Add-Member -MemberType NoteProperty -Name State -Value ([Data.ConnectionState]::Open)
function DatabaseNames { @('master', 'Cohors', $database) }
function Invoke-BrowserCleanupSql { $script:sqlCleanupCalls++; throw 'synthetic-database-cleanup-failure' }
. $finallyBlock
if (-not $apiStopped -or $databaseRemoved -or $sqlCleanupCalls -ne 1 -or $cleanupErrors.Count -ne 2 -or
    [IO.File]::ReadAllText($settingsPath) -cne 'database-not-removed') {
    throw 'Database cleanup failure must retain the complete runtime and remain an explicit failure.'
}
RemoveRuntime
$checks.Add(@{ variant = 'UnremovedDatabasePreservesRuntime'; success = $true; fakeProcessAndSqlOnly = $true })
$prefix = 'COHORS_UI_CONTRACT_' + [Guid]::NewGuid().ToString('N')
$environment = @{ "$($prefix)_ABSENT" = $null; "$($prefix)_EMPTY" = ''; "$($prefix)_VALUE" = 'synthetic-original' }
try {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, 'synthetic-changed') }
    Restore-BrowserEnvironment $environment
    foreach ($name in $environment.Keys) {
        if ([Environment]::GetEnvironmentVariable($name) -cne $environment[$name]) {
            throw 'Environment restoration must distinguish absent, empty and nonempty original values.'
        }
    }
} finally {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value) }
}
$checks.Add(@{ variant = 'ExactEnvironmentRestoration'; success = $true; processLocalOnly = $true })
foreach ($profile in @('EudamedDossiers','PrimaryBaseline')) {
    $selection = Get-BrowserSelection $profile
    if ($selection.arguments -isnot [string[]]) { throw 'Selection arguments must remain an array, even for one element.' }
    $listed = @{ suites = @(@{ suites = @(@{ specs = @($selection.titles | ForEach-Object {
        @{ title = $_; tests = @(@{ projectName = $selection.project; expectedStatus = 'passed' }) }
    }) }) }) }
    $null = Assert-BrowserSelection $listed $selection
    if ($profile -ceq 'PrimaryBaseline') {
        foreach ($title in $selection.titles) {
            if ("[preview] pilot.spec.ts > $title" -cnotmatch $selection.arguments[2]) { throw 'Selection must match full Playwright titles, not an incorrect leading anchor.' }
        }
    }
    foreach ($mutation in @('WrongTitle','Skipped','WrongProject','MissingCase','ExtraCase')) {
        $bad = $listed | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
        $spec = $bad.suites[0].suites[0].specs[0]
        switch ($mutation) {
            'WrongTitle' { $spec.title = 'Unrelated preview' }
            'Skipped' { $spec.tests[0].expectedStatus = 'skipped' }
            'WrongProject' { $spec.tests[0].projectName = 'unrelated-project' }
            'MissingCase' { $bad.suites[0].suites[0].specs = @($bad.suites[0].suites[0].specs | Select-Object -Skip 1) }
            'ExtraCase' { $bad.suites[0].suites[0].specs += $spec }
        }
        $rejected = $false
        try { $null = Assert-BrowserSelection $bad $selection } catch {
            if ($_.Exception.Message -cne 'Playwright must list exactly all declared unskipped cases in the selected project before resources.') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'A changed, skipped, missing or additional selected case was accepted.' }
    }
    $checks.Add(@{ variant = "$profile-selection"; success = $true; rejectedMutations = 5 })
}
$proof = [ordered]@{
    success = $true; executionHelperSha256 = HashFile $helper; testHelperSha256 = HashFile $PSCommandPath
    actualFunctionsAndRegistrationBlockUsed = $true; executionFileMetadataSuppliedFromBoundHelper = $true
    syntheticSqlCallbackOnly = $true; noSqlApiOrBrowserStarted = $true; checks = $checks.ToArray()
    completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$output = Join-Path $proofRoot 'result.json'
WriteDurableJson $output $proof
Write-Output "Public browser runner contracts: $($checks.Count) passed; no SQL, API or browser started. Evidence: $output"
