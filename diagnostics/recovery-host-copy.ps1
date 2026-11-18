#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][ValidateSet('Probe','Cleanup')][string]$Phase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsLinux -or $env:GITHUB_ACTIONS -cne 'true') {
    throw 'This diagnostic requires the actual hosted Linux filesystem, not NTFS or a tar metadata projection.'
}
$limit = if ($Phase -ceq 'Probe') { 180 } else { 60 }
$started = [DateTimeOffset]::Parse($env:COHORS_OWNED_STARTED_UTC,[Globalization.CultureInfo]::InvariantCulture)
$deadline = [DateTimeOffset]::Parse($env:COHORS_OWNED_DEADLINE_UTC,[Globalization.CultureInfo]::InvariantCulture)
if (($deadline - $started).TotalSeconds -ne $limit) {
    throw 'The outer Linux owner must supply the original absolute180s probe or60s cleanup window.'
}
$lastObservation = $started
function Assert-DiagnosticDeadline {
    $now = [DateTimeOffset]::UtcNow
    if ($now -lt $script:lastObservation -or $now -ge $script:deadline) {
        throw 'diagnostic_deadline: No additional operation is admitted outside the original absolute window.'
    }
    $script:lastObservation = $now
}
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$temporary = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not $evidence.StartsWith($temporary + [IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal)) {
    throw 'A dedicated evidence directory under RUNNER_TEMP is required.'
}
Assert-DiagnosticDeadline
if ($Phase -ceq 'Probe') {
    if (Test-Path -LiteralPath $evidence) { throw 'Never reuse previous probe output.' }
    [IO.Directory]::CreateDirectory($evidence,
        [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) | Out-Null
}
function Save-Diagnostic([string]$Name, $Value) {
    Assert-DiagnosticDeadline
    $stream = [IO.FileStream]::new((Join-Path $script:evidence $Name),[IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,[IO.FileShare]::Read,4096,[IO.FileOptions]::WriteThrough)
    try {
        $stream.Write([Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 15)))
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}
$operation = 'preflight'
function Invoke-DiagnosticNative([string]$Executable, [string[]]$Arguments, [hashtable]$Environment = @{}) {
    Assert-DiagnosticDeadline
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    Assert-DiagnosticDeadline
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    try {
        while (-not $process.HasExited) {
            Assert-DiagnosticDeadline
            $null = $process.WaitForExit(100)
        }
        Assert-DiagnosticDeadline
        $null = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($output,$errors)).
            WaitAsync([TimeSpan]::FromSeconds([Math]::Min(15,($script:deadline - [DateTimeOffset]::UtcNow).TotalSeconds))).
            GetAwaiter().GetResult()
        Assert-DiagnosticDeadline
        return @{ exitCode = $process.ExitCode; output = $output.Result; error = $errors.Result }
    } finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $null = $process.WaitForExit(10000)
        }
        $process.Dispose()
    }
}
function Require-Diagnostic($Result) {
    if ($Result.exitCode -ne 0) { throw "diagnostic_native_failure: $script:operation exited with code $($Result.exitCode)." }
    return $Result.output.Trim()
}
function Docker-Diagnostic([string[]]$Arguments, [hashtable]$Environment = @{}) {
    Invoke-DiagnosticNative 'docker' $Arguments $Environment
}
function Read-DiagnosticSet([string]$Text) {
    return @($Text -split '\r?\n' | Where-Object { $_ } | Sort-Object)
}
$image = 'mcr.microsoft.com/mssql/server@sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f'
Assert-DiagnosticDeadline
$scriptHash = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
if ($Phase -ceq 'Cleanup') {
    Assert-DiagnosticDeadline
    $before = Get-Content -LiteralPath (Join-Path $evidence 'before.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($before.owner -cnotmatch '^[a-f0-9]{32}$' -or
        $before.name -cne ('cohors-hostcopy-' + $before.owner) -or
        $before.scriptSha256 -cne $scriptHash -or $before.image -cne $image) {
        throw 'diagnostic_ownership: Invalid prospective cleanup binding.'
    }
    $operation = 'owned-container-close'
    $found = @(Read-DiagnosticSet (Require-Diagnostic (Docker-Diagnostic @('ps','-aq','--no-trunc',
        '--filter',"label=cohors.recovery.diagnostic=$($before.owner)"))))
    if ($found.Count -gt 1) { throw 'diagnostic_ownership: Ambiguous owned container set.' }
    foreach ($id in $found) {
        if ($id -cnotmatch '^[a-f0-9]{64}$' -or $id -cin $before.containers) {
            throw 'diagnostic_ownership: Refusing an unowned stop.'
        }
        $identity = Docker-Diagnostic @('inspect','--format',
            '{{.Id}}|{{.Name}}|{{index .Config.Labels "cohors.recovery.diagnostic"}}',$id)
        if ($identity.exitCode -eq 0) {
            if ($identity.output.Trim() -cne "$id|/$($before.name)|$($before.owner)") {
                throw 'diagnostic_ownership: Refusing a mismatched owned stop.'
            }
            $stop = Docker-Diagnostic @('stop','--timeout','5',$id)
            if ($stop.exitCode -ne 0) {
                $remaining = @(Read-DiagnosticSet (Require-Diagnostic (Docker-Diagnostic @('ps','-aq','--no-trunc',
                    '--filter',"label=cohors.recovery.diagnostic=$($before.owner)"))))
                if ($remaining.Count) { throw 'diagnostic_cleanup: Owned container remains.' }
            }
        }
    }
    $afterContainers = @(Read-DiagnosticSet (Require-Diagnostic (Docker-Diagnostic @('ps','-aq','--no-trunc'))))
    $afterVolumes = @(Read-DiagnosticSet (Require-Diagnostic (Docker-Diagnostic @('volume','ls','--format','{{.Name}}'))))
    $remaining = @(Read-DiagnosticSet (Require-Diagnostic (Docker-Diagnostic @('ps','-aq','--no-trunc',
        '--filter',"label=cohors.recovery.diagnostic=$($before.owner)"))))
    $observationPath = Join-Path $evidence 'observation.json'
    Assert-DiagnosticDeadline
    $result = if (Test-Path -LiteralPath $observationPath) {
        Get-Content -LiteralPath $observationPath -Raw | ConvertFrom-Json -AsHashtable
    } else {
        @{
            contract = 'cohors-hosted-linux-backup-copy-1'; diagnosticObservationComplete = $false
            applicationAcceptance = $false; cloudRelease = $false; failurePhase = 'probe-did-not-persist-an-observation'
            scriptSha256 = $scriptHash; image = $image
        }
    }
    $result.ownedContainerAbsent = $remaining.Count -eq 0
    $result.originalDockerSetPreserved = (@($before.containers) -join '|') -ceq ($afterContainers -join '|') -and
        (@($before.volumes) -join '|') -ceq ($afterVolumes -join '|')
    $result.cleanupLimitSeconds = 60
    $result.cleanupStartedUtc = $started.ToString('O')
    $result.cleanupDeadlineUtc = $deadline.ToString('O')
    $result.cleanupObservedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $result.outerLinuxProcessContainmentProofRequired = $true
    Save-Diagnostic 'result.json' $result
    $result | ConvertTo-Json -Depth 10
    if (-not $result.diagnosticObservationComplete -or -not $result.ownedContainerAbsent -or -not $result.originalDockerSetPreserved) {
        throw 'Diagnostic observation or cleanup incomplete; no application or recovery acceptance is claimed.'
    }
    return
}
$imageId = Require-Diagnostic (Docker-Diagnostic @('image','inspect','--format','{{.Id}}',$image))
$containers = @(Read-DiagnosticSet (Require-Diagnostic (Docker-Diagnostic @('ps','-aq','--no-trunc'))))
$volumes = @(Read-DiagnosticSet (Require-Diagnostic (Docker-Diagnostic @('volume','ls','--format','{{.Name}}'))))
$owner = [Guid]::NewGuid().ToString('N')
$name = 'cohors-hostcopy-' + $owner
$container = $null
$sourceFile = "/var/opt/mssql/data/cohors_source_${owner}.bak"
$nativeDirectory = "/var/opt/mssql/data/cohors_copy_${owner}"
$nativeFile = "$nativeDirectory/database.bak"
$hostFile = Join-Path $evidence 'database.bak'
$result = [ordered]@{
    contract = 'cohors-hosted-linux-backup-copy-1'
    diagnosticObservationComplete = $false; applicationAcceptance = $false; cloudRelease = $false
    host = 'Linux'; exactHostFileToContainerFile = $true; tarStdinTransfer = $false
    metadataProjection = $false; permissionRepairApplied = $false
    image = $image; imageId = $imageId; owner = $owner
    scriptSha256 = $scriptHash
    startedUtc = $started.ToString('O'); executionDeadlineUtc = $deadline.ToString('O')
    executionLimitSeconds = 180; containerMaximumLifetimeSeconds = 120
    noHostMounts = $true; noPublishedPorts = $true; noExistingResourceMutation = $true
}
Save-Diagnostic 'before.json' @{
    owner = $owner; name = $name; containers = $containers; volumes = $volumes; scriptSha256 = $scriptHash
    image = $image; noHostMounts = $true; noPublishedPorts = $true; network = 'none'
    automaticRemoval = $true; maximumContainerLifetimeSeconds = 120
}
try {
    $operation = 'owned-container-start'
    $password = 'Cohors!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'a7'
    $container = Require-Diagnostic (Docker-Diagnostic @('run','--detach','--rm','--pull','never','--name',$name,
        '--label',"cohors.recovery.diagnostic=$owner",'--network','none','--memory','3g',
        '--env','ACCEPT_EULA=Y','--env','MSSQL_PID=Developer','--env','MSSQL_MEMORY_LIMIT_MB=2048',
        '--env','MSSQL_SA_PASSWORD','--entrypoint','/usr/bin/timeout',$image,
        '--signal=TERM','--kill-after=5s','115s','/opt/mssql/bin/sqlservr') @{ MSSQL_SA_PASSWORD = $password })
    $password = $null
    if ($container -cnotmatch '^[a-f0-9]{64}$' -or $container -cin $containers) { throw 'diagnostic_ownership: Ambiguous container.' }
    Save-Diagnostic 'created.json' @{ owner = $owner; containerId = $container; name = $name }
    $identity = (Require-Diagnostic (Docker-Diagnostic @('inspect','--format',
        '{{json .Mounts}}|{{.HostConfig.NetworkMode}}|{{.HostConfig.AutoRemove}}|{{.Name}}|{{index .Config.Labels "cohors.recovery.diagnostic"}}',
        $container))).Split('|')
    if ($identity.Count -ne 5 -or $identity[4] -cne $owner -or $identity[3] -cne "/$name" -or
        @($identity[0] | ConvertFrom-Json).Count -or $identity[1] -cne 'none' -or $identity[2] -cne 'true') {
        throw 'diagnostic_ownership: Container boundary differs.'
    }
    function Sql-Diagnostic([string]$Query) {
        Docker-Diagnostic @('exec',$container,'sh','-c',
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -b -l 2 -t 15 -Q "$1"',
            'cohors-synthetic-diagnostic',$Query)
    }
    $operation = 'owned-sql-readiness'
    $ready = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if ((Sql-Diagnostic 'SET NOCOUNT ON; SELECT 1').exitCode -eq 0) { $ready = $true; break }
        Assert-DiagnosticDeadline
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'diagnostic_readiness: Owned SQL did not become ready.' }
    $result.sqlUser = Require-Diagnostic (Docker-Diagnostic @('exec',$container,'id','-u'))
    $result.sqlGroup = Require-Diagnostic (Docker-Diagnostic @('exec',$container,'id','-g'))
    $result.sqlProcessUidGid = Require-Diagnostic (Docker-Diagnostic @('exec',$container,'ps','-C','sqlservr','-o','uid=,gid='))
    $operation = 'synthetic-native-backup'
    $database = 'Cohors_Recovery_' + $owner
    Require-Diagnostic (Sql-Diagnostic "CREATE DATABASE [$database]; BACKUP DATABASE [$database] TO DISK=N'$sourceFile' WITH CHECKSUM") | Out-Null
    $result.sourceMode = Require-Diagnostic (Docker-Diagnostic @('exec',$container,'stat','-c','%u:%g:%a:%s',$sourceFile))
    $sourceHash = (Require-Diagnostic (Docker-Diagnostic @('exec',$container,'sha256sum',$sourceFile))).Split(' ')[0]
    $operation = 'native-backup-to-linux-host-file'
    Require-Diagnostic (Docker-Diagnostic @('cp',"${container}:$sourceFile",$hostFile)) | Out-Null
    $result.hostMode = Require-Diagnostic (Invoke-DiagnosticNative 'stat' @('-c','%u:%g:%a:%s',$hostFile))
    Assert-DiagnosticDeadline
    $hostHash = (Get-FileHash -LiteralPath $hostFile).Hash.ToLowerInvariant()
    if ($hostHash -cne $sourceHash) { throw 'diagnostic_bytes: Host backup differs.' }
    Require-Diagnostic (Docker-Diagnostic @('exec',$container,'mkdir','-m','700',$nativeDirectory)) | Out-Null
    $result.privateDirectoryMode = Require-Diagnostic (Docker-Diagnostic @('exec',$container,'stat','-c','%u:%g:%a',$nativeDirectory))
    $operation = 'linux-host-file-to-owned-container-file'
    Require-Diagnostic (Docker-Diagnostic @('cp',$hostFile,"${container}:$nativeFile")) | Out-Null
    $result.targetMode = Require-Diagnostic (Docker-Diagnostic @('exec',$container,'stat','-c','%u:%g:%a:%s',$nativeFile))
    $targetHash = (Require-Diagnostic (Docker-Diagnostic @('exec','--user','0',$container,'sha256sum',$nativeFile))).Split(' ')[0]
    if ($targetHash -cne $hostHash) { throw 'diagnostic_bytes: Container backup differs.' }
    $result.identicalSourceHostTargetBytes = $true
    $result.nativeBackupSha256 = $hostHash
    $otherRead = Docker-Diagnostic @('exec','--user','65534:65534',$container,'test','-r',$nativeFile)
    if ($otherRead.exitCode -ne 1) { throw 'diagnostic_private_file: Another unprivileged user can read the backup.' }
    $result.otherUnprivilegedUserCannotRead = $true
    $sqlRead = Docker-Diagnostic @('exec',$container,'test','-r',$nativeFile)
    if ($sqlRead.exitCode -notin @(0,1)) { throw 'diagnostic_read_probe: SQL-user readability is unknown.' }
    $result.sqlUserCanRead = $sqlRead.exitCode -eq 0
    $operation = 'restore-verifyonly-with-checksum'
    $verification = Sql-Diagnostic "RESTORE VERIFYONLY FROM DISK=N'$nativeFile' WITH CHECKSUM"
    $result.verifyOnlyExitCode = $verification.exitCode
    $result.sqlErrors = @([regex]::Matches($verification.output + $verification.error, 'Msg (\d+),') |
        ForEach-Object { [int]$_.Groups[1].Value })
    $result.operatingSystemErrors = @([regex]::Matches($verification.output + $verification.error, 'Operating system error\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value })
    if ($verification.exitCode -ne 0 -and (3201 -notin $result.sqlErrors -or 3013 -notin $result.sqlErrors)) {
        throw 'diagnostic_unexpected_sql: Do not reinterpret another SQL failure as permissions evidence.'
    }
    $result.permissionFailureReproduced = -not $result.sqlUserCanRead -and $verification.exitCode -ne 0
    $result.verifyOnlyPassed = $verification.exitCode -eq 0
    $operation = 'production-backup-access-regression'
    # Keep the bounded transport in the original script-scoped deadline and clock state.
    $result.backupAccessRepair = . (Join-Path $PSScriptRoot 'recovery-backup-access.ps1') `
        -BackupScriptPath (Join-Path $PSScriptRoot 'recovery-source-1e8acd4/Backup-Local.ps1') `
        -BackupScriptSha256 '8bdf2b3644277b00f77793d7232ef715d98f7e19e81ad6c83b9f34c07e03ab87' `
        -RestoreScriptPath (Join-Path $PSScriptRoot 'recovery-source-1e8acd4/Restore-Local.ps1') `
        -RestoreScriptSha256 '5a152cf38550b095b2de08dc84484ca38d87cd5f2f64f2d8b9baeb6d74cfdeda' `
        -ContainerId $container -OwnerId $owner -EvidenceDirectory $evidence -Baseline $result
    $result.diagnosticObservationComplete = $true
} catch {
    $result.failurePhase = $operation
    $result.failureType = $_.Exception.GetType().FullName
    $accessObservation = Join-Path $evidence 'access-observation.json'
    if ($operation -ceq 'production-backup-access-regression' -and (Test-Path -LiteralPath $accessObservation -PathType Leaf)) {
        Assert-DiagnosticDeadline
        if ((Get-Item -LiteralPath $accessObservation).Length -gt 16384) { throw 'Excessive private access observation.' }
        $result.backupAccessRepair = Get-Content -LiteralPath $accessObservation -Raw | ConvertFrom-Json -AsHashtable
    }
} finally {
    $result.observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Save-Diagnostic 'observation.json' $result
}
if (-not $result.diagnosticObservationComplete) {
    throw 'Hosted copy observation is incomplete; the separately owned cleanup phase is still required.'
}
$result | ConvertTo-Json -Depth 10
