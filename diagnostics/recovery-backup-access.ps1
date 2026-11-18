#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupScriptPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$BackupScriptSha256,
    [Parameter(Mandatory)][string]$RestoreScriptPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$RestoreScriptSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ContainerId,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$OwnerId,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][System.Collections.IDictionary]$Baseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsLinux -or $env:GITHUB_ACTIONS -cne 'true') {
    throw 'This extension requires the actual hosted Linux copy diagnostic, not a local native lane.'
}
foreach ($required in @('Assert-DiagnosticDeadline','Invoke-DiagnosticNative','Docker-Diagnostic','Sql-Diagnostic','Require-Diagnostic')) {
    if (-not (Get-Command $required -CommandType Function -ErrorAction SilentlyContinue)) {
        throw 'The original owned Linux probe must supply its deadline and bounded native operations.'
    }
}
Assert-DiagnosticDeadline
$accessStart = [DateTimeOffset]::Parse($env:COHORS_OWNED_STARTED_UTC,[Globalization.CultureInfo]::InvariantCulture)
$accessDeadline = [DateTimeOffset]::Parse($env:COHORS_OWNED_DEADLINE_UTC,[Globalization.CultureInfo]::InvariantCulture)
if (($accessDeadline - $accessStart).TotalSeconds -ne 180) {
    throw 'This extension shares the original180s probe window; it never starts a fresh window.'
}
$accessEvidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$accessTemporary = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not $accessEvidence.StartsWith($accessTemporary + [IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal) -or
    -not (Test-Path -LiteralPath $accessEvidence -PathType Container)) {
    throw 'The existing private RUNNER_TEMP evidence directory is required.'
}
$accessImage = 'mcr.microsoft.com/mssql/server@sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f'
$accessIdentity = Require-Diagnostic (Docker-Diagnostic @('inspect','--format',
    '{{.Id}}|{{.Name}}|{{index .Config.Labels "cohors.recovery.diagnostic"}}|{{.Config.Image}}',$ContainerId))
if ($accessIdentity -cne "$ContainerId|/cohors-hostcopy-$OwnerId|$OwnerId|$accessImage" -or
    $Baseline.owner -cne $OwnerId -or $Baseline.image -cne $accessImage -or
    -not $Baseline.exactHostFileToContainerFile -or $Baseline.tarStdinTransfer -or $Baseline.metadataProjection -or
    $Baseline.permissionRepairApplied -or -not $Baseline.identicalSourceHostTargetBytes -or
    -not $Baseline.permissionFailureReproduced -or $Baseline.sqlUserCanRead -or
    3201 -notin $Baseline.sqlErrors -or 3013 -notin $Baseline.sqlErrors -or 5 -notin $Baseline.operatingSystemErrors) {
    throw 'An exact owned container and the genuine unmodified Linux access failure are prerequisites.'
}
function Assert-AccessSourceBinding {
    foreach ($pair in @(@($BackupScriptPath,$BackupScriptSha256),@($RestoreScriptPath,$RestoreScriptSha256))) {
        Assert-DiagnosticDeadline
        if ((Get-FileHash -LiteralPath $pair[0]).Hash.ToLowerInvariant() -cne $pair[1]) {
            throw 'The supplied production recovery source bytes changed.'
        }
    }
}
Assert-AccessSourceBinding
. $BackupScriptPath
$script:accessNativeCalls = 0
function Invoke-RecoveryDocker([string[]]$Arguments) {
    Assert-DiagnosticDeadline
    $script:accessNativeCalls++
    $nativeResult = Docker-Diagnostic $Arguments
    if ($nativeResult.exitCode -ne 0) {
        throw 'recovery_docker: The explicit owned-container operation failed; no success is asserted.'
    }
    return @($nativeResult.output,$nativeResult.error)
}
function Read-AccessHash([string]$Path) {
    return (Require-Diagnostic (Docker-Diagnostic @('exec','--user','0',$ContainerId,'sha256sum','--',$Path))).Split(' ')[0]
}
function Read-AccessVerification([string]$Path) {
    $verification = Sql-Diagnostic "RESTORE VERIFYONLY FROM DISK=N'$Path' WITH CHECKSUM"
    return @{
        exitCode = $verification.exitCode
        sqlErrors = @([regex]::Matches($verification.output + $verification.error,'Msg (\d+),') |
            ForEach-Object { [int]$_.Groups[1].Value })
        operatingSystemErrors = @([regex]::Matches($verification.output + $verification.error,'Operating system error\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value })
    }
}
function Require-AccessRefusal([string]$Path) {
    $refused = $false
    try { Set-RecoverySqlBackupAccess $ContainerId $Path }
    catch {
        if (-not $_.Exception.Message.StartsWith('recovery_docker:')) { throw }
        $refused = $true
    }
    if (-not $refused) { throw 'A linked backup reached the metadata repair instead of being refused.' }
}
$accessSource = "/var/opt/mssql/data/cohors_source_${OwnerId}.bak"
$accessHost = Join-Path $accessEvidence 'database.bak'
$accessDirectory = "/var/opt/mssql/data/cohors_recovery_${OwnerId}-backup"
$accessFile = "$accessDirectory/database.bak"
$accessResult = [ordered]@{
    contract = 'cohors-hosted-linux-backup-access-1'; passed = $false
    applicationAcceptance = $false; recoveryAcceptance = $false; cloudRelease = $false
    backupScriptSha256 = $BackupScriptSha256; restoreScriptSha256 = $RestoreScriptSha256
    extensionSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
    productionFunction = 'Set-RecoverySqlBackupAccess'
    transport = 'Original bounded Linux diagnostic Docker transport; production function body is unchanged.'
    sameOriginal180SecondWindow = $true; separateContainerCreated = $false
    originalNegativeObservationPreserved = $true; rawOutputPublished = $false
    scopeRefusals = 0; nativeLinkRefusals = 0; ownedTransientFilesRemoved = $false
}
$accessOperation = 'scope-refusals'
try {
    foreach ($invalid in @(
        @{ container = 'not-an-owned-id'; path = $accessFile },
        @{ container = $ContainerId; path = '/var/opt/mssql/data/master.mdf' },
        @{ container = $ContainerId; path = "/var/opt/mssql/data/cohors_recovery_${OwnerId}.bak" },
        @{ container = $ContainerId; path = "$accessDirectory/../database.bak" }
    )) {
        $refused = $false
        try { Set-RecoverySqlBackupAccess $invalid.container $invalid.path }
        catch {
            if (-not $_.Exception.Message.StartsWith('recovery_ownership:')) { throw }
            $refused = $true
        }
        if (-not $refused -or $script:accessNativeCalls -ne 0) {
            throw 'A foreign-scope input reached a privileged operation.'
        }
        $accessResult.scopeRefusals++
    }
    $accessOperation = 'production-path-copy'
    Require-Diagnostic (Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'mkdir','-m','700','--',$accessDirectory)) | Out-Null
    Require-Diagnostic (Docker-Diagnostic @('cp',$accessHost,"${ContainerId}:$accessFile")) | Out-Null
    $accessResult.beforeMode = Require-Diagnostic (Docker-Diagnostic @('exec',$ContainerId,'stat','-c','%u:%g:%a:%s','--',$accessFile))
    $accessResult.directoryMode = Require-Diagnostic (Docker-Diagnostic @('exec',$ContainerId,'stat','-c','%u:%g:%a','--',$accessDirectory))
    $accessResult.beforeSha256 = Read-AccessHash $accessFile
    $beforeRead = Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'test','-r',$accessFile)
    $accessResult.beforeVerification = Read-AccessVerification $accessFile
    if ($accessResult.beforeMode -cne $Baseline.targetMode -or
        $accessResult.beforeSha256 -cne $Baseline.nativeBackupSha256 -or $beforeRead.exitCode -ne 1 -or
        $accessResult.directoryMode -cne '10001:10001:700' -or
        $accessResult.beforeVerification.exitCode -eq 0 -or
        3201 -notin $accessResult.beforeVerification.sqlErrors -or 3013 -notin $accessResult.beforeVerification.sqlErrors -or
        5 -notin $accessResult.beforeVerification.operatingSystemErrors) {
        throw 'The exact production-path copy did not reproduce the observed unreadable but byte-identical backup.'
    }
    $accessOperation = 'production-helper'
    Assert-AccessSourceBinding
    Set-RecoverySqlBackupAccess $ContainerId $accessFile
    $accessResult.afterMode = Require-Diagnostic (Docker-Diagnostic @('exec',$ContainerId,'stat','-c','%u:%g:%a:%s','--',$accessFile))
    $accessResult.afterSha256 = Read-AccessHash $accessFile
    $afterRead = Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'test','-r',$accessFile)
    $otherRead = Docker-Diagnostic @('exec','--user','65534:65534',$ContainerId,'test','-r',$accessFile)
    $accessResult.afterVerification = Read-AccessVerification $accessFile
    $length = $accessResult.beforeMode.Split(':')[-1]
    if ($accessResult.afterMode -cne "10001:10001:600:$length" -or
        $accessResult.afterSha256 -cne $accessResult.beforeSha256 -or $afterRead.exitCode -ne 0 -or $otherRead.exitCode -ne 1 -or
        $accessResult.afterVerification.exitCode -ne 0 -or $accessResult.afterVerification.sqlErrors.Count -or
        $accessResult.afterVerification.operatingSystemErrors.Count) {
        throw 'The production helper did not restore private SQL readability and VERIFYONLY without changing bytes.'
    }
    $accessResult.sqlUserCanReadAfter = $true
    $accessResult.otherUnprivilegedUserCannotReadAfter = $true
    $accessOperation = 'native-link-refusals'
    foreach ($kind in @('symlink','hardlink','directory-symlink')) {
        $guardDirectory = '/var/opt/mssql/data/cohors_recovery_' + [Guid]::NewGuid().ToString('N') + '-backup'
        $guardFile = "$guardDirectory/database.bak"
        if ($kind -ceq 'directory-symlink') {
            Require-Diagnostic (Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'ln','-s','--',$accessDirectory,$guardDirectory)) | Out-Null
        } else {
            Require-Diagnostic (Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'mkdir','-m','700','--',$guardDirectory)) | Out-Null
            $linkArguments = @('exec','--user','mssql',$ContainerId,'ln')
            if ($kind -ceq 'symlink') { $linkArguments += '-s' }
            Require-Diagnostic (Docker-Diagnostic ($linkArguments + @('--',$accessSource,$guardFile))) | Out-Null
        }
        $guardPath = if ($kind -ceq 'directory-symlink') { $guardDirectory } else { $guardFile }
        $beforeGuard = Require-Diagnostic (Docker-Diagnostic @('exec','--user','0',$ContainerId,'stat','-c','%F:%u:%g:%a:%h','--',$guardPath))
        Require-AccessRefusal $guardFile
        $afterGuard = Require-Diagnostic (Docker-Diagnostic @('exec','--user','0',$ContainerId,'stat','-c','%F:%u:%g:%a:%h','--',$guardPath))
        $sourceMode = Require-Diagnostic (Docker-Diagnostic @('exec',$ContainerId,'stat','-c','%u:%g:%a:%s','--',$accessSource))
        if ($beforeGuard -cne $afterGuard -or $sourceMode -cne $Baseline.sourceMode -or
            (Read-AccessHash $accessSource) -cne $Baseline.nativeBackupSha256) {
            throw 'A rejected link or its original target changed.'
        }
        Require-Diagnostic (Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'rm','--',$guardPath)) | Out-Null
        if ($kind -cne 'directory-symlink') {
            Require-Diagnostic (Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'rmdir','--',$guardDirectory)) | Out-Null
        }
        $accessResult.nativeLinkRefusals++
    }
    $accessOperation = 'unchanged-source-and-host'
    Assert-DiagnosticDeadline
    $accessResult.hostSha256After = (Get-FileHash -LiteralPath $accessHost).Hash.ToLowerInvariant()
    $accessResult.hostModeAfter = Require-Diagnostic (Invoke-DiagnosticNative 'stat' @('-c','%u:%g:%a:%s','--',$accessHost))
    if ($accessResult.hostSha256After -cne $Baseline.nativeBackupSha256 -or $accessResult.hostModeAfter -cne $Baseline.hostMode -or
        (Read-AccessHash $accessFile) -cne $Baseline.nativeBackupSha256) {
        throw 'The unchanged native source/host/corrected target binding differs.'
    }
    $accessOperation = 'exact-file-cleanup'
    Require-Diagnostic (Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'rm','--',$accessFile)) | Out-Null
    Require-Diagnostic (Docker-Diagnostic @('exec','--user','mssql',$ContainerId,'rmdir','--',$accessDirectory)) | Out-Null
    Require-Diagnostic (Docker-Diagnostic @('exec',$ContainerId,'sh','-c',
        'test ! -e "$1" && test ! -L "$1"','cohors-access-absence',$accessDirectory)) | Out-Null
    $accessResult.ownedTransientFilesRemoved = $true
    Assert-AccessSourceBinding
    $accessResult.passed = $true
} catch {
    $accessResult.failurePhase = $accessOperation
    $accessResult.failureType = $_.Exception.GetType().FullName
} finally {
    Assert-DiagnosticDeadline
    $accessResult.observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $accessStream = [IO.FileStream]::new((Join-Path $accessEvidence 'access-observation.json'),
        [IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read,4096,[IO.FileOptions]::WriteThrough)
    try {
        $accessStream.Write([Text.Encoding]::UTF8.GetBytes(($accessResult | ConvertTo-Json -Depth 10)))
        $accessStream.Flush($true)
    } finally { $accessStream.Dispose() }
}
if (-not $accessResult.passed) {
    throw 'Production backup access regression failed; the independently owned cleanup phase remains mandatory.'
}
return $accessResult
