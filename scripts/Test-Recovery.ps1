#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [int]$SourceSqlPort = 14358,
    [int]$TargetSqlPort = 14359
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -eq 'true') {
    throw 'This is the explicit local recovery runner, not an execution of the GitHub Actions acceptance harness.'
}
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Backup-Local.ps1')
$artifacts = Assert-RecoveryPath $ArtifactDirectory -New
if ($SourceSqlPort -eq $TargetSqlPort) { throw 'Recovery requires two distinct SQL loopback ports.' }
foreach ($port in @($SourceSqlPort, $TargetSqlPort)) {
    if ($port -lt 1024 -or $port -gt 65535) { throw 'Recovery requires unprivileged SQL loopback ports.' }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
    try { $listener.Start() } finally { $listener.Stop() }
}
$run = Join-Path $root ('.local\recovery-run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$variables = @('COHORS_RECOVERY_DIRECTORY', 'COHORS_RECOVERY_SOURCE_PORT', 'COHORS_RECOVERY_TARGET_PORT', 'COHORS_RECOVERY_PWSH')
$previous = @{}
foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:COHORS_RECOVERY_DIRECTORY = Join-Path $run 'fixture'
    $env:COHORS_RECOVERY_SOURCE_PORT = [string]$SourceSqlPort
    $env:COHORS_RECOVERY_TARGET_PORT = [string]$TargetSqlPort
    $env:COHORS_RECOVERY_PWSH = (Get-Process -Id $PID).Path
    & $dotnet test (Join-Path $root 'tests\Cohors.Tests\Cohors.Tests.csproj') --no-restore --disable-build-servers `
        -m:1 -p:UseSharedCompilation=false --filter 'Category=Recovery' --verbosity quiet `
        --logger 'trx;LogFileName=recovery.trx' --results-directory (Join-Path $run 'results') *> (Join-Path $run 'private-tests.log')
    if ($LASTEXITCODE -ne 0) {
        throw 'Local recovery failed. No public success evidence was created; diagnostics remain in the dedicated private recovery-run directory.'
    }
    $trxPath = Join-Path $run 'results\recovery.trx'
    & (Join-Path $root 'infra\scripts\Test-CohorsResults.ps1') -TrxPath $trxPath
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
    $verified = Get-Content -LiteralPath (Join-Path $run 'fixture\verified.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 30 -DateKind String
    if ([int]$counters.total -ne 14 -or $verified.completedChecks.Count -ne 11 -or
        -not $verified.sourceDatabaseRemoved -or -not $verified.sourceObjectsRemoved -or
        -not $verified.ownedContainersAndVolumesRemoved -or -not $verified.retention.deletedRawAbsent -or
        -not $verified.retention.exactDeletionJournalRestored -or -not $verified.retention.staleBackupRejected -or
        -not $verified.psur.sourceAndTargetContentVerified -or $verified.psur.reports.Count -ne 3 -or
        $verified.psur.cutoffReview.status -cne 'ReplySubmitted' -or $verified.psur.currentReview.status -cne 'QuestionClosed' -or
        $verified.psur.pendingReview.status -cne 'MedicalApproved' -or $verified.psur.pendingReview.approvals -ne 1 -or
        $verified.psur.pendingReview.submissions -ne 0 -or
        $verified.applicationAssemblies.Count -ne 5) {
        throw 'Incomplete recovery execution or cleanup; no public success evidence was created.'
    }
    $implementation = @()
    foreach ($file in @('scripts\Backup-Local.ps1', 'scripts\Restore-Local.ps1', 'scripts\Test-Recovery.ps1', 'tests\Cohors.Tests\RecoveryTests.cs')) {
        $implementation += @{ path = $file; sha256 = (Get-FileHash -LiteralPath (Join-Path $root $file)).Hash.ToLowerInvariant() }
    }
    $testResults = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | ForEach-Object {
        @{ name = $_.testName; outcome = $_.outcome }
    })
    $result = [ordered]@{
        scope = 'Executed local isolated T20; not GitHub Actions, HTTP/browser, Azure DR, RPO/RTO or an external ledger witness'
        capturedAt = [DateTimeOffset]::UtcNow.ToString('O'); sourceCommit = (& git -C $root rev-parse HEAD)
        sourceTreeDirty = @(& git -C $root status --porcelain --untracked-files=normal).Count -gt 0
        implementation = $implementation; dotnet = (& $dotnet --version); powershell = $PSVersionTable.PSVersion.ToString()
        total = [int]$counters.total; executed = [int]$counters.executed; passed = [int]$counters.passed
        failed = [int]$counters.failed; skipped = [int]$counters.notExecuted; tests = $testResults; verification = $verified
    }
    New-Item -ItemType Directory -Path $artifacts | Out-Null
    [IO.File]::WriteAllText((Join-Path $artifacts 'recovery-results.json'),
        (ConvertTo-Json -InputObject $result -Depth 30), [Text.UTF8Encoding]::new($false))
    Write-Output 'T18/T20 local recovery including PSUR/BS: 14/14 passed, none skipped. Only allowlisted results were published; raw logs and backups remain private.'
} finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}
