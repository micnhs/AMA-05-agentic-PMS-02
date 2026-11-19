#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [ValidateSet('Preview','PrimaryBaseline','AuditExport','SummaryReport','CaseDocuments','ReportPrefill','InformationLifecycle','EudamedConfiguration','EudamedDossiers','CockpitCalendar','SignalReview','PhysicalRetention')][string]$Profile = 'Preview',
    [int]$Port = 5197,
    [switch]$HostedCi,
    [Collections.Generic.List[string]]$CredentialInventory
)
$ErrorActionPreference = 'Stop'
function HashFile([string]$path) {
    $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::Read, 1048576, [IO.FileOptions]::SequentialScan)
    try { [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}
function WriteDurableJson([string]$path, $value, [switch]$Append) {
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $value -Depth 20 -Compress) + "`n")
    $mode = if ($Append) { [IO.FileMode]::Append } else { [IO.FileMode]::CreateNew }
    $stream = [IO.FileStream]::new($path, $mode, [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}
function ResourceStage([ValidateSet('CreateRequested','DatabaseCreated','SchemaReady','Ready','CleanupRequested','Disposed')][string]$phase) {
    WriteDurableJson $journalPath ([ordered]@{
        schema = 'cohors-browser-resources-1'; phase = $phase; intentSha256 = $intentHash
        database = $database; runtime = $runtime; apiPid = $apiPid
        elapsedTicks = $resourceClock.ElapsedTicks; frequency = [Diagnostics.Stopwatch]::Frequency
        utc = [DateTimeOffset]::UtcNow.ToString('O')
    }) -Append
}
function DatabaseNames {
    $command = $master.CreateCommand(); $command.CommandTimeout = 15
    $command.CommandText = 'SELECT name FROM sys.databases ORDER BY name'
    try {
        $reader = $command.ExecuteReader()
        $names = [Collections.Generic.List[string]]::new()
        try { while ($reader.Read()) { $names.Add($reader.GetString(0)) } } finally { $reader.Dispose() }
        return $names.ToArray()
    } finally { $command.Dispose() }
}
function Invoke-BrowserCleanupSql([string]$text, [switch]$Scalar) {
    $command = $master.CreateCommand(); $command.CommandTimeout = 15; $command.CommandText = $text
    try {
        if ($Scalar) { return $command.ExecuteScalar() }
        $command.ExecuteNonQuery() | Out-Null
    } finally { $command.Dispose() }
}
function CleanupStep([scriptblock]$action) {
    try { & $action } catch { $cleanupErrors.Add($_.Exception) }
}
function Restore-BrowserEnvironment([Collections.IDictionary]$values) {
    foreach ($name in $values.Keys) {
        if ($null -eq $values[$name]) {
            [Environment]::SetEnvironmentVariable($name, [NullString]::Value)
        } else {
            [Environment]::SetEnvironmentVariable($name, $values[$name])
        }
    }
}
function RemoveRuntime {
    if (-not (Test-Path -LiteralPath $runtime)) { return }
    $objects = Join-Path $private 'blobs'
    $allowedDirectories = @($runtime, $private, $objects, (Join-Path $objects 'raw-quarantine'), (Join-Path $objects '.deletion-receipts'))
    $directories = [Collections.Generic.List[string]]::new()
    $files = [Collections.Generic.List[object]]::new()
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($runtime)
    while ($queue.Count) {
        $directory = $queue.Dequeue()
        if ($directory -cnotin $allowedDirectories) { throw 'Unexpected directory inside the exact owned runtime.' }
        $null = Assert-RecoveryPath $directory
        $directories.Add($directory)
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName); continue }
            if ($item.LinkTarget -or $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Redirected owned file.' }
            $hash = HashFile $item.FullName
            if ($item.FullName -cnotin @($settingsPath, $accessPath)) {
                if ($item.DirectoryName -cin @($objects, (Join-Path $objects 'raw-quarantine'))) {
                    if ($item.Name -cnotmatch '^[0-9a-f]{64}$' -or $item.Name -cne $hash) { throw 'Unexpected or non-addressed owned object.' }
                } elseif ($item.DirectoryName -ceq (Join-Path $objects '.deletion-receipts')) {
                    if ($item.Name -cnotmatch '^[0-9a-f]{64}\.json$') { throw 'Unexpected deletion receipt name.' }
                } else { throw 'Unknown file in the exact owned runtime.' }
            }
            $files.Add(@{ path = $item.FullName; sha256 = $hash })
        }
    }
    foreach ($directory in $directories) { $null = Assert-RecoveryPath $directory }
    foreach ($file in $files) {
        $null = Assert-RecoveryPath $file.path
        if ((HashFile $file.path) -cne $file.sha256) { throw 'Owned bytes changed before exact removal.' }
        [IO.File]::Delete($file.path)
    }
    for ($index = $directories.Count - 1; $index -ge 0; $index--) { [IO.Directory]::Delete($directories[$index]) }
    if (Test-Path -LiteralPath $runtime) { throw 'The owned runtime parent remains.' }
}
function Get-BrowserSelection([ValidateSet('EudamedDossiers','PrimaryBaseline')][string]$SelectedProfile) {
    if ($SelectedProfile -ceq 'EudamedDossiers') {
        $project = 'eudamed-dossiers'
        [string[]]$titles = @(
            'E03: source-bound MIR dossier, separate approvals, business receipt and newly approved correspondence reply',
            'E04: byte-exact dossier PDF and audit originals, historical scope and cancelled download after identity switch',
            'E05: separate FSN publication reviews include shared-ledger feedback and preserve simulated recipient evidence'
        )
    } else {
        $project = 'preview'
        [string[]]$titles = @(
            ('SQL-backed complaint {0} grounded assessment {0} distinct approvals {0} unknown/reconcile {0} verified audit' -f [char]0x2192),
            'T16: grouped PSUR stays independently readable, editable and protected by every member scope'
        )
    }
    [string[]]$arguments = @("--project=$project")
    if ($SelectedProfile -ceq 'PrimaryBaseline') {
        $arguments += @('--grep', ('(?:' + (($titles | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$'))
    }
    return @{ project = $project; titles = $titles; arguments = $arguments }
}
function Assert-BrowserSelection($document, $selection) {
    $cases = [Collections.Generic.List[object]]::new()
    function VisitSuite($suite) {
        foreach ($spec in $suite['specs']) {
            foreach ($test in $spec['tests']) {
                $cases.Add(@{ title = $spec['title']; project = $test['projectName']; expectedStatus = $test['expectedStatus'] })
            }
        }
        foreach ($child in $suite['suites']) { VisitSuite $child }
    }
    foreach ($suite in $document['suites']) { VisitSuite $suite }
    if ($cases.Count -ne $selection.titles.Count -or
        @($cases | Where-Object { $_.project -cne $selection.project -or $_.expectedStatus -cne 'passed' }).Count -or
        @(Compare-Object @($selection.titles) @($cases | ForEach-Object { $_.title })).Count) {
        throw 'Playwright must list exactly all declared unskipped cases in the selected project before resources.'
    }
    return $cases.ToArray()
}
function Confirm-BrowserSelection($selection) {
    $jsonPath = Join-Path $artifacts 'selected-tests.json'
    $stub = Join-Path $artifacts 'selection-access.txt'
    $stream = [IO.FileStream]::new($stub, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $stream.Dispose()
    [string[]]$arguments = $selection.arguments
    try {
        $env:COHORS_URL = "http://localhost:$Port"
        $env:COHORS_TEST_ACCESS_FILE = $stub
        $env:COHORS_UI_ARTIFACT_DIR = $artifacts
        & node (Join-Path $root 'src\Cohors.Web\node_modules\@playwright\test\cli.js') test `
            --config (Join-Path $root 'src\Cohors.Web\playwright.config.ts') @arguments --list --reporter=json `
            1> $jsonPath 2> (Join-Path $artifacts 'selected-tests.stderr.log')
        if ($LASTEXITCODE -ne 0) { throw 'Read-only Playwright test listing failed before resources.' }
    } finally {
        Restore-BrowserEnvironment $previous
        [IO.File]::Delete($stub)
    }
    $document = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json -AsHashtable
    $cases = @(Assert-BrowserSelection $document $selection)
    WriteDurableJson (Join-Path $artifacts 'selected-tests-proof.json') @{
        profile = $Profile; project = $selection.project; cases = $cases; arguments = $arguments
        selectionOnly = $true; browserSqlAndApiStarted = $false; listSha256 = HashFile $jsonPath
        sourceFiles = $sourceFiles
    }
}
$root = Split-Path $PSScriptRoot -Parent
if ($HostedCi) {
    if ($env:GITHUB_ACTIONS -cne 'true' -or -not $IsLinux -or
        [string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE) -or
        [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($env:GITHUB_WORKSPACE)) -cne $root -or
        $null -eq $CredentialInventory -or $CredentialInventory.Count -eq 0) {
        throw 'Hosted browser acceptance requires the explicit Linux Actions checkout and its shared credential inventory.'
    }
} elseif ($env:GITHUB_ACTIONS -eq 'true' -or $null -ne $CredentialInventory) {
    throw 'GitHub Actions must opt in through the owned Hosted CI lifecycle; local runs do not accept a CI credential inventory.'
}
. (Join-Path $PSScriptRoot 'Backup-Local.ps1') -ApplicationDirectory $ApplicationDirectory
$application = Assert-RecoveryPath $ApplicationDirectory
$artifacts = Assert-RecoveryPath $ArtifactDirectory -New
if (-not (Test-Path -LiteralPath (Join-Path $application 'wwwroot\index.html'))) {
    throw 'Publish the current API with its built web assets before browser acceptance.'
}
if ($Port -lt 1024 -or $Port -gt 65535 -or $Port -eq 5187) { throw 'Use a separate unprivileged browser-test port, never the existing 5187 preview.' }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } finally { $listener.Stop() }
Initialize-RecoveryRuntime $application
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$config = Get-Content -LiteralPath (Join-Path $root '.local\settings.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 30
$sql = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($config.ConnectionStrings.Cohors)
if ($sql.DataSource -cnotmatch '^(127\.0\.0\.1|localhost),[1-9][0-9]{3,4}$') { throw 'Browser acceptance requires the explicit existing loopback SQL server.' }
$database = 'Cohors_Test_' + [Guid]::NewGuid().ToString('N')
$masterSql = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($sql.ConnectionString)
$masterSql.set_InitialCatalog('master')
$masterSql.set_Pooling($false)
$masterSql.set_ConnectTimeout(5)
$master = [Microsoft.Data.SqlClient.SqlConnection]::new($masterSql.ConnectionString)
$runtime = Join-Path $artifacts 'runtime'
$private = Join-Path $runtime '.local'
$settingsPath = Join-Path $private 'settings.json'
$accessPath = Join-Path $private 'demo-access.txt'
$created = $false
$apiProcess = $null
$apiPid = $null
$apiStopped = $false
$databaseRemoved = $false
$passed = $false
$selection = $null
$intentHash = $null
$registered = $false
$createRequested = $false
$runtimeCreated = $false
$baselineDatabases = @()
$stdout = $null
$stderr = $null
$originalError = $null
$cleanupErrors = [Collections.Generic.List[Exception]]::new()
$journalPath = Join-Path $artifacts 'owned-resources-journal.jsonl'
$intentPath = Join-Path $artifacts 'owned-resources-intent.json'
$resourceClock = [Diagnostics.Stopwatch]::StartNew()
$started = [DateTimeOffset]::UtcNow.ToString('O')
$assemblyNames = @('Cohors.Api.dll','Cohors.Application.dll','Cohors.Domain.dll','Cohors.Infrastructure.dll','Cohors.Agents.dll')
$applicationFiles = [ordered]@{}
$files = @($assemblyNames | ForEach-Object { Join-Path $application $_ }) +
    @(Get-ChildItem -LiteralPath (Join-Path $application 'wwwroot') -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName })
foreach ($file in $files) {
    $applicationFiles[[IO.Path]::GetRelativePath($application, $file)] = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
}
$sourceFiles = [ordered]@{}
foreach ($file in @('scripts\Test-Ui.ps1','src\Cohors.Web\e2e\pilot.spec.ts','src\Cohors.Web\playwright.config.ts')) {
    $sourceFiles[$file] = (Get-FileHash -LiteralPath (Join-Path $root $file)).Hash.ToLowerInvariant()
}
$previous = @{}
foreach ($name in @('COHORS_URL', 'COHORS_TEST_ACCESS_FILE', 'COHORS_UI_ARTIFACT_DIR')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
}
New-Item -ItemType Directory -Path $artifacts | Out-Null
if ($Profile -in @('EudamedDossiers', 'PrimaryBaseline')) {
    $selection = Get-BrowserSelection $Profile
    Confirm-BrowserSelection $selection
}
try {
    $master.Open()
    $baselineDatabases = @(DatabaseNames)
    if ($database -cin $baselineDatabases) { throw 'The new database name already exists; no ownership was acquired.' }
    if (Test-Path -LiteralPath $runtime) { throw 'The private runtime already exists; no ownership was acquired.' }
    $ownerProcess = [Diagnostics.Process]::GetCurrentProcess()
    try { $ownerStarted = $ownerProcess.StartTime.ToUniversalTime().ToString('O') } finally { $ownerProcess.Dispose() }
    WriteDurableJson $intentPath ([ordered]@{
        schema = 'cohors-browser-resources-1'; sourceCheckout = $root; application = $application
        artifacts = $artifacts; runtime = $runtime; database = $database; port = $Port
        executable = $dotnet; runnerPid = $PID; runnerStartedUtc = $ownerStarted
        databaseAbsentBeforeCreate = $true; baselineDatabases = $baselineDatabases
        runtimeAbsentBeforeCreate = $true
        helperSha256 = HashFile $PSCommandPath; createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    })
    $intentHash = HashFile $intentPath
    ResourceStage 'CreateRequested'
    $registered = $true
    $null = Assert-RecoveryPath $runtime -New
    New-Item -ItemType Directory -Path $runtime | Out-Null
    $runtimeCreated = $true
    New-Item -ItemType Directory -Path $private | Out-Null
    $createRequested = $true
    Invoke-RecoverySql $master "CREATE DATABASE [$database]"
    $created = $true
    ResourceStage 'DatabaseCreated'
    $sql.set_InitialCatalog($database)
    $config.ConnectionStrings.Cohors = $sql.ConnectionString
    $config.Cohors.AutoMigrate = $true
    $config.Cohors.Seed = $true
    $config.Cohors.LocalOperations = $true
    $config.Cohors.PublicSources = $false
    $config.Cohors.ObjectStore = 'Local'
    $config.Cohors.Search = 'Local'
    if ($Profile -in @('EudamedConfiguration', 'EudamedDossiers', 'PrimaryBaseline')) {
        $config.Domibus = @{ Endpoints = @(); Secrets = @(); ModuleKeys = @(); OnboardingEvidence = @() }
    }
    $physical = $Profile -eq 'PhysicalRetention'
    $config.Retention = @{ PhysicalMode = if ($physical) { 'SyntheticLocal' } else { 'Disabled' } }
    $access = [IO.File]::ReadAllText((Join-Path $root '.local\demo-access.txt'))
    $accountScopes = @(
        @{ Username = 'pms-ortho.demo'; Role = 'PmsSpecialist'; Product = 'ortho' },
        @{ Username = 'pms-cardio.demo'; Role = 'PmsSpecialist'; Product = 'cardio' }
    )
    if ($Profile -in @('SignalReview', 'EudamedConfiguration', 'EudamedDossiers', 'PrimaryBaseline')) {
        $accountScopes += @{ Username = 'medical-cardio.demo'; Role = 'MedicalSafetyReviewer'; Product = 'cardio' }
        $accountScopes += @{ Username = 'ra-cardio.demo'; Role = 'RegulatoryAffairs'; Product = 'cardio' }
    }
    if ($Profile -eq 'InformationLifecycle') {
        $accountScopes += @{ Username = 'rfi-cardio.demo'; Role = 'MedicalSafetyReviewer'; Product = 'cardio' }
    }
    foreach ($account in $accountScopes) {
        $secret = 'Pilot!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(12))
        $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
        $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($secret, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
        $username = $account.Username
        if (@($config.Authentication.Users | Where-Object { $_.Username -ceq $username }).Count) {
            throw 'An isolated scoped test account already exists; do not overwrite or duplicate it.'
        }
        if ($HostedCi) {
            foreach ($value in @($secret, [Convert]::ToBase64String($salt), [Convert]::ToBase64String($hash))) {
                $CredentialInventory.Add($value)
                Write-Output "::add-mask::$value"
            }
        }
        $config.Authentication.Users = @($config.Authentication.Users) + @{
            Id = [Guid]::NewGuid().ToString(); Username = $username; Name = "Synthetic $($account.Product)-scoped reviewer"
            Role = $account.Role; Products = @($account.Product); Salt = [Convert]::ToBase64String($salt); Hash = [Convert]::ToBase64String($hash)
        }
        $access += "`n$username : $secret`n"
    }
    [IO.File]::WriteAllText($settingsPath, (ConvertTo-Json -InputObject $config -Depth 30))
    [IO.File]::WriteAllText($accessPath, $access)
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.ArgumentList.Add((Join-Path $application 'Cohors.Api.dll'))
    $start.WorkingDirectory = $application
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['COHORS_HOME'] = $runtime
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Testing'
    $start.Environment['DOTNET_ENVIRONMENT'] = 'Testing'
    $start.Environment['ASPNETCORE_CONTENTROOT'] = $application
    $start.Environment['ASPNETCORE_URLS'] = "http://localhost:$Port"
    $apiProcess = [Diagnostics.Process]::Start($start)
    $apiPid = $apiProcess.Id
    WriteDurableJson (Join-Path $artifacts 'owned-resources-start.json') @{
        apiPid = $apiPid; database = $database; port = $Port; runtime = $runtime; intentSha256 = $intentHash
        apiStartedUtc = $apiProcess.StartTime.ToUniversalTime().ToString('O'); executable = $dotnet
        application = $application; sourceCheckout = $root
    }
    $stdout = $apiProcess.StandardOutput.ReadToEndAsync()
    $stderr = $apiProcess.StandardError.ReadToEndAsync()
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($apiProcess.HasExited) { throw 'The owned browser-test API exited before readiness; inspect its private log.' }
        try {
            $response = Invoke-RestMethod -Uri "http://localhost:$Port/health/ready" -TimeoutSec 5
            $ready = $response.status -eq 'ready'
        } catch [Net.Http.HttpRequestException] { $ready = $false }
        catch [Net.WebException] { $ready = $false }
        catch [OperationCanceledException] { $ready = $false }
        if ($ready) { break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'The owned browser-test API did not become ready.' }
    ResourceStage 'SchemaReady'
    ResourceStage 'Ready'
    if ($Profile -eq 'SignalReview') {
        $testAssembly = Join-Path $root 'tests\Cohors.Tests\bin\Debug\net10.0\Cohors.Tests.dll'
        [void][Reflection.Assembly]::LoadFrom($testAssembly)
        if ([Cohors.Tests.SignalReviewBrowserFixture].Assembly.Location -cne $testAssembly -or
            [Cohors.Domain.Canonical].Assembly.Location -cne (Join-Path $application 'Cohors.Domain.dll')) {
            throw 'The synthetic fixture must use the explicitly bound test and application assemblies in this fresh process.'
        }
        # This in-process fixture must not retain SQL sessions after the owned API stops.
        $fixtureSql = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($sql.ConnectionString)
        $fixtureSql.set_Pooling($false)
        $fixture = [Cohors.Tests.SignalReviewBrowserFixture]::Create($fixtureSql.ConnectionString, (Join-Path $root 'policies')).GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $artifacts 'synthetic-signal-fixture.json'), [Cohors.Domain.Canonical]::Serialize($fixture))
        $sourceFiles['tests\Cohors.Tests\bin\Debug\net10.0\Cohors.Tests.dll'] = (Get-FileHash -LiteralPath $testAssembly).Hash.ToLowerInvariant()
    }
    $env:COHORS_URL = "http://localhost:$Port"
    $env:COHORS_TEST_ACCESS_FILE = $accessPath
    $env:COHORS_UI_ARTIFACT_DIR = $artifacts
    $project = switch ($Profile) { 'PhysicalRetention' { 'physical-retention' } 'AuditExport' { 'audit-export' } 'SummaryReport' { 'summary-report' } 'CaseDocuments' { 'case-documents' } 'ReportPrefill' { 'report-prefill' } 'InformationLifecycle' { 'information-lifecycle' } 'EudamedConfiguration' { 'eudamed-configuration' } 'EudamedDossiers' { 'eudamed-dossiers' } 'CockpitCalendar' { 'cockpit-calendar' } 'SignalReview' { 'signal-review' } default { 'preview' } }
    [string[]]$browserArguments = if ($selection) { $selection.arguments } else { @("--project=$project") }
    & npm --prefix (Join-Path $root 'src\Cohors.Web') run test:e2e -- @browserArguments *> (Join-Path $artifacts 'private-browser.log')
    if ($LASTEXITCODE -ne 0) { throw 'Current browser acceptance failed; private diagnostics and JUnit were preserved.' }
    [xml]$junit = Get-Content -LiteralPath (Join-Path $artifacts 'e2e-results.xml') -Raw
    $cases = $junit.SelectNodes('//testcase')
    $expected = if ($Profile -eq 'Preview') { 11 } elseif ($Profile -eq 'SignalReview') { 6 } elseif ($Profile -eq 'CockpitCalendar') { 4 } elseif ($Profile -eq 'EudamedDossiers') { 3 } elseif ($Profile -in @('PrimaryBaseline','CaseDocuments','InformationLifecycle','EudamedConfiguration')) { 2 } else { 1 }
    if ($cases.Count -ne $expected -or $junit.SelectNodes('//testcase/skipped | //testcase/failure | //testcase/error').Count) {
        throw 'Browser acceptance requires every named scenario of the explicitly selected profile, without failures or skips.'
    }
    if ($selection -and @(Compare-Object @($selection.titles) @($cases | ForEach-Object { $_.name })).Count) {
        throw 'Executed browser cases differ from the exact declared selection.'
    }
    foreach ($file in $applicationFiles.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $application $file)).Hash.ToLowerInvariant() -cne $applicationFiles[$file]) {
            throw 'Published application or browser assets changed during acceptance.'
        }
    }
    foreach ($file in $sourceFiles.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $root $file)).Hash.ToLowerInvariant() -cne $sourceFiles[$file]) {
            throw 'Browser test source changed during acceptance.'
        }
    }
    $assemblies = [ordered]@{}
    foreach ($name in $assemblyNames) {
        $assemblies[$name] = $applicationFiles[$name]
    }
    $report = [ordered]@{
        scope = 'Isolated synthetic Replay browser; local outbox/watchdog emulation, not separate Functions/Azure/live AI'
        hostKind = if ($HostedCi) { 'GitHubActionsLinux' } else { 'Local' }
        profile = $Profile; retentionPhysicalMode = $config.Retention.PhysicalMode
        startedUtc = $started; finishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sourceCommit = (& git -C $root rev-parse HEAD); sourceTreeDirty = @(& git -C $root status --porcelain --untracked-files=normal).Count -gt 0
        sourceFiles = $sourceFiles; applicationFiles = $applicationFiles
        applicationAssemblies = $assemblies; testCount = $cases.Count
        tests = @($cases | ForEach-Object { $_.name }); database = $database
        originalDemoUnchanged = $true; ownApiStopped = $false; ownDatabaseRemoved = $false
    }
    $passed = $true
} catch {
    $originalError = $_
} finally {
    CleanupStep { Restore-BrowserEnvironment $previous }
    if ($registered) { CleanupStep { ResourceStage 'CleanupRequested' } }
    CleanupStep {
        if ($apiProcess) {
            if (-not $apiProcess.HasExited) { $apiProcess.Kill($true) }
            if (-not $apiProcess.WaitForExit(10000)) { throw 'The exact owned API did not stop within ten seconds.' }
            $script:apiStopped = $true
            if (-not $stdout -or -not $stderr -or
                -not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr)).Wait(3000)) {
                throw 'API streams retained after three seconds; independent cleanup is required.'
            }
            [IO.File]::WriteAllText((Join-Path $artifacts 'private-api.log'), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        } else { $script:apiStopped = $true }
    }
    CleanupStep { if ($apiProcess) { $apiProcess.Dispose() } }
    if ($apiStopped) {
        CleanupStep {
            if ($createRequested) {
                if ($master.State -ne [Data.ConnectionState]::Open) { $master.Open() }
                if ($database -cin @(DatabaseNames)) {
                    if ((Invoke-BrowserCleanupSql "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process=1 AND database_id=DB_ID(N'$database')" -Scalar) -ne 0) {
                        throw 'An active connection remains in the exact owned database; independent cleanup is required.'
                    }
                    Invoke-BrowserCleanupSql "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]"
                }
                $afterDatabases = @(DatabaseNames)
                if ($database -cin $afterDatabases -or @($baselineDatabases | Where-Object { $_ -cnotin $afterDatabases }).Count) {
                    throw 'Owned database removal or baseline preservation failed.'
                }
            }
            $script:databaseRemoved = $true
        }
        if ($databaseRemoved) {
            if ($runtimeCreated) { CleanupStep { RemoveRuntime } }
        } else { $cleanupErrors.Add([InvalidOperationException]::new('Database removal unproven; runtime was preserved.')) }
    } else { $cleanupErrors.Add([InvalidOperationException]::new('API stop unproven; database and runtime were preserved.')) }
    CleanupStep { $master.Dispose() }
    $objects = Join-Path $private 'blobs'
    if ($registered -and $apiStopped -and $databaseRemoved -and -not (Test-Path -LiteralPath $runtime)) {
        CleanupStep { ResourceStage 'Disposed' }
    }
    CleanupStep {
        WriteDurableJson (Join-Path $artifacts 'owned-resources-terminal.json') @{
            apiPid = $apiPid; ownApiStopped = $apiStopped; database = $database; ownDatabaseRemoved = $databaseRemoved
            settingsRemoved = -not (Test-Path -LiteralPath $settingsPath); accessFileRemoved = -not (Test-Path -LiteralPath $accessPath)
            objectDirectoryRemoved = -not (Test-Path -LiteralPath $objects); runtimeParentRemoved = -not (Test-Path -LiteralPath $runtime)
            runtimeCreatedByRunner = $runtimeCreated; browserPassed = $passed; executionFailed = $null -ne $originalError
            cleanupErrorCount = $cleanupErrors.Count; intentSha256 = $intentHash
            resourceJournalSha256 = if (Test-Path -LiteralPath $journalPath) { HashFile $journalPath } else { $null }
            finishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
    }
}
if ($cleanupErrors.Count) {
    if ($originalError) { $cleanupErrors.Insert(0, $originalError.Exception) }
    throw [AggregateException]::new('Browser execution and cleanup errors were preserved independently.', $cleanupErrors.ToArray())
}
if ($originalError) { throw $originalError }
if (-not $passed -or -not $apiStopped -or -not $databaseRemoved -or (Test-Path -LiteralPath $runtime)) {
    throw 'No complete browser/cleanup success.'
}
$report.ownApiStopped = $true; $report.ownDatabaseRemoved = $true
$report.runtimeParentRemoved = $true; $report.resourceIntentSha256 = $intentHash; $report.resourceJournalSha256 = HashFile $journalPath
WriteDurableJson (Join-Path $artifacts 'browser-results.json') $report
Write-Output "Current $Profile browser acceptance: $expected/$expected passed, none skipped; owned API/database and complete runtime removed. Existing 5187 demo unchanged."
