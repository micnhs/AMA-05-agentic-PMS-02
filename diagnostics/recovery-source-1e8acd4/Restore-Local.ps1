#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigurationPath,
    [Parameter(Mandatory)][string]$BackupDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ManifestSha256,
    [string]$ApplicationDirectory,
    [string]$CurrentRetentionManifestPath,
    [string]$CurrentRetentionManifestSha256,
    [switch]$CurrentRetentionConfirmed,
    [string]$TimingPath
)

. (Join-Path $PSScriptRoot 'Backup-Local.ps1') -ConfigurationPath $ConfigurationPath `
    -BackupDirectory $BackupDirectory -ApplicationDirectory $ApplicationDirectory -TimingPath $TimingPath
Initialize-RecoveryTiming $TimingPath 'RecoveryRestore'
Initialize-RecoveryRuntime $ApplicationDirectory
Write-RecoveryPhase 'runtime-ready'
$config = Read-RecoveryConfiguration $ConfigurationPath
$targetObjects = Assert-RecoveryPath $config.objectRoot -New
$readyPath = Assert-RecoveryPath ($targetObjects + '.recovery-ready.json') -New
$backup = Assert-RecoveryPath $BackupDirectory
Assert-RecoverySeparatePaths $backup $targetObjects
$manifestPath = Assert-RecoveryPath (Join-Path $backup 'manifest.json')
Write-RecoveryPhase 'configuration-ready'
$master = Open-RecoverySql $config 'master'
$stageDatabase = 'Cohors_Recovery_' + [Guid]::NewGuid().ToString('N') + '_staging'
$stageObjects = $targetObjects + '.staging-' + [Guid]::NewGuid().ToString('N')
$native = '/var/opt/mssql/data/cohors_recovery_' + [Guid]::NewGuid().ToString('N')
$nativeBackupDirectory = "$native-backup"
$nativeBackup = "$nativeBackupDirectory/database.bak"
$ownedDatabase = $null
$ownedObjects = $null
$nativeBackupDirectoryCreated = $false
$backupLock = $null
$manifestLock = $null
$objects = @()
$readyTemporary = $readyPath + '.partial-' + [Guid]::NewGuid().ToString('N')
try {
    if ((Invoke-RecoverySql $master "SELECT COUNT(*) FROM sys.databases WHERE name=N'$($config.database)'" -Scalar) -ne 0) {
        throw 'recovery_target_exists: The target database already exists; nothing will be restored over it.'
    }
    Write-RecoveryPhase 'target-absence-confirmed'
    $manifestLock = [IO.File]::Open($manifestPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ((Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant() -cne $ManifestSha256) {
        throw 'recovery_manifest_hash: Manifest differs from the independently supplied backup digest.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
    if ($manifest.format -cnotin @('cohors-local-recovery-1', 'cohors-local-recovery-2') -or $manifest.syntheticOnly -ne $true -or
        $manifest.schemaVersion -ne [Cohors.Infrastructure.SqlStateStore]::SchemaVersion) {
        throw 'recovery_manifest: Unsupported recovery format or scope.'
    }
    if ($manifest.format -ceq 'cohors-local-recovery-1' -and
        ($manifest.Contains('retention') -or @($manifest.objects | Where-Object { $_.Contains('dataClass') }).Count -gt 0)) {
        throw 'recovery_manifest: Legacy format cannot carry new raw retention classes.'
    }
    if ($manifest.format -ceq 'cohors-local-recovery-2' -and -not $manifest.Contains('retention')) {
        throw 'recovery_retention_contract: A v2 manifest requires its explicit retention cut, including an empty one.'
    }
    Write-RecoveryPhase 'manifest-shape-verified'
    $retentionCheckpoint = Assert-RecoveryRetentionCheckpoint $manifest $CurrentRetentionManifestPath `
        $CurrentRetentionManifestSha256 ([bool]$CurrentRetentionConfirmed)
    Write-RecoveryPhase 'current-retention-verified'
    $backupFile = Assert-RecoveryPath (Join-Path $backup 'database.bak')
    $backupLock = [IO.File]::Open($backupFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Write-RecoveryPhase 'native-backup-hash-start'
    if ((Get-FileHash -LiteralPath $backupFile).Hash.ToLowerInvariant() -cne $manifest.databaseSha256) {
        throw 'recovery_database_hash: Native backup bytes differ from the pinned manifest.'
    }
    Write-RecoveryPhase 'native-backup-hash-complete'
    $backupObjects = Join-Path $backup 'objects'
    foreach ($item in $manifest.objects) {
        if ($item.hash -cnotmatch '^[a-f0-9]{64}$' -or $item.length -lt 0) { throw 'recovery_manifest: Invalid object inventory.' }
        if (-not (Test-Path -LiteralPath (Get-RecoveryObjectPath $backupObjects $item) -PathType Leaf)) {
            throw 'recovery_object_missing: A declared object is missing; no target was activated.'
        }
    }
    Write-RecoveryPhase 'backup-object-inventory-start'
    $objects = Get-RecoveryObjects $backupObjects
    if ((Get-RecoveryHash $objects) -cne (Get-RecoveryHash $manifest.objects)) {
        throw 'recovery_object_inventory: Object inventory differs from the pinned manifest.'
    }
    Assert-RecoveryCoverage $manifest $objects
    Write-RecoveryPhase 'backup-object-inventory-verified' $objects.Count
    Assert-RecoveryPath $stageObjects -New | Out-Null
    New-Item -ItemType Directory -Path $stageObjects | Out-Null
    $ownedObjects = $stageObjects
    Write-RecoveryPhase 'staged-object-copy-start'
    Copy-RecoveryObjects $backupObjects $stageObjects $objects
    if ((Get-RecoveryHash (Get-RecoveryObjects $stageObjects)) -cne (Get-RecoveryHash $objects)) {
        throw 'recovery_object_hash: Staged object bytes differ.'
    }
    Write-RecoveryPhase 'staged-object-copy-verified' $objects.Count
    Invoke-RecoveryDocker @('exec', '--user', 'mssql', $config.containerId,
        'mkdir', '-m', '700', '--', $nativeBackupDirectory) | Out-Null
    $nativeBackupDirectoryCreated = $true
    Invoke-RecoveryDocker @('cp', $backupFile, "$($config.containerId):$nativeBackup") | Out-Null
    Set-RecoverySqlBackupAccess $config.containerId $nativeBackup
    Write-RecoveryPhase 'native-backup-copied'
    Invoke-RecoverySql $master "RESTORE VERIFYONLY FROM DISK=N'$nativeBackup' WITH CHECKSUM"
    Write-RecoveryPhase 'native-backup-verified'
    $command = [Microsoft.Data.SqlClient.SqlCommand]::new("RESTORE FILELISTONLY FROM DISK=N'$nativeBackup'", $master)
    $files = [Collections.Generic.List[object]]::new()
    try {
        $reader = $command.ExecuteReader()
        try { while ($reader.Read()) { $files.Add(@{ name = $reader['LogicalName']; type = $reader['Type'] }) } }
        finally { $reader.Dispose() }
    } finally { $command.Dispose() }
    if ($files.Count -ne 2 -or @($files | Where-Object type -EQ 'D').Count -ne 1 -or @($files | Where-Object type -EQ 'L').Count -ne 1) {
        throw 'recovery_database_shape: Only the pilot single-data-file/single-log-file backup is supported.'
    }
    if ((Invoke-RecoverySql $master "SELECT COUNT(*) FROM sys.databases WHERE name=N'$stageDatabase'" -Scalar) -ne 0) {
        throw 'recovery_target_exists: Generated staging database already exists.'
    }
    $dataName = ($files | Where-Object type -EQ 'D').name.Replace("'", "''")
    $logName = ($files | Where-Object type -EQ 'L').name.Replace("'", "''")
    $ownedDatabase = $stageDatabase
    Write-RecoveryPhase 'native-database-restore-start'
    Invoke-RecoverySql $master @"
RESTORE DATABASE [$stageDatabase] FROM DISK=N'$nativeBackup'
WITH MOVE N'$dataName' TO N'$native.mdf',MOVE N'$logName' TO N'$native.ldf',RECOVERY,RESTRICTED_USER,CHECKSUM
"@
    Write-RecoveryPhase 'native-database-restored'
    $stage = Open-RecoverySql $config $stageDatabase
    try {
        Assert-RecoveryEudamedOriginals $stage $stageObjects
        Write-RecoveryPhase 'restored-state-start'
        $state = Get-RecoveryState $stage $config
        Assert-RecoveryCoverage $state $objects
        if ((Get-RecoveryHash $state.tables) -cne (Get-RecoveryHash $manifest.tables) -or
            (Get-RecoveryHash $state.references) -cne (Get-RecoveryHash $manifest.references)) {
            throw 'recovery_sql_cut: Restored authoritative SQL does not match the declared object/SQL cut.'
        }
        $declaredRetention = if ($manifest.Contains('retention')) { $manifest.retention } else {
            [ordered]@{ contract = 'cohors-retention-cut-1'; rows = @() }
        }
        if ((Get-RecoveryHash $state.retention) -cne (Get-RecoveryHash $declaredRetention)) {
            throw 'recovery_retention_sql_cut: Restored raw lifecycle/deletion bindings differ from the pinned manifest.'
        }
        Write-RecoveryPhase 'restored-state-verified'
    } finally { $stage.Dispose() }
    Invoke-RecoverySql $master "ALTER DATABASE [$stageDatabase] SET READ_WRITE"
    $stage = Open-RecoverySql $config $stageDatabase
    try {
        Write-RecoveryPhase 'search-rebuild-start'
        Invoke-RecoverySql $stage 'TRUNCATE TABLE dbo.SearchProjection'
        $sql = New-RecoveryStore $config $stageDatabase
        $objectStore = [Cohors.Infrastructure.LocalObjectStore]::new($stageObjects)
        $search = [Cohors.Infrastructure.KnowledgeSearch]::new($sql, $objectStore, $sql, $null)
        $rebuilt = $search.Rebuild([Threading.CancellationToken]::None).GetAwaiter().GetResult()
        foreach ($product in @('ortho', 'cardio')) {
            $search.Retrieve($product, [DateTimeOffset]::UtcNow, '', [Cohors.Application.CaseService]::Workflow,
                [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
        }
        Write-RecoveryPhase 'search-rebuilt' $rebuilt
        Write-RecoveryPhase 'post-search-state-start'
        $after = Get-RecoveryState $stage $config
        if ((Get-RecoveryHash $after) -cne (Get-RecoveryHash $state)) {
            throw 'recovery_sql_changed: Rebuilding search changed authoritative state.'
        }
        Write-RecoveryPhase 'post-search-state-verified'
    } finally { $stage.Dispose() }
    Invoke-RecoveryDocker @('exec', '--user', 'mssql', $config.containerId, 'rm', '--', $nativeBackup) | Out-Null
    Invoke-RecoveryDocker @('exec', '--user', 'mssql', $config.containerId, 'rmdir', '--', $nativeBackupDirectory) | Out-Null
    $nativeBackupDirectoryCreated = $false
    Write-RecoveryPhase 'activation-start'
    # Directory.Move and database rename both reject a concurrently created destination.
    [IO.Directory]::Move($stageObjects, $targetObjects)
    $ownedObjects = $targetObjects
    Invoke-RecoverySql $master "ALTER DATABASE [$stageDatabase] MODIFY NAME=[$($config.database)]"
    $ownedDatabase = $config.database
    Invoke-RecoverySql $master "ALTER DATABASE [$($config.database)] SET MULTI_USER"
    $ready = [ordered]@{
        status = 'Verified'; manifestSha256 = $ManifestSha256; database = $config.database
        referencedObjects = $state.references.Count; copiedObjects = $objects.Count; evidenceRevisionsRebuilt = $rebuilt
        searchAdapter = 'KnowledgeSearch without SearchClient (SQL-derived local projection)'
        retentionCheckpoint = $retentionCheckpoint
        workersStarted = $false; sourceRead = $false; externalLedgerWitness = $false
    }
    $stream = [IO.File]::Open($readyTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $ready -Depth 10))
        $stream.Write($bytes); $stream.Flush($true)
    } finally { $stream.Dispose() }
    [IO.File]::Move($readyTemporary, $readyPath, $false)
    $ownedDatabase = $null
    $ownedObjects = $null
    Write-RecoveryPhase 'restore-activated'
    Write-Output 'RECOVERY_RESTORE=Verified; workers remain stopped; no submission or deadline was replayed.'
} finally {
    if ($backupLock) { $backupLock.Dispose() }
    if ($manifestLock) { $manifestLock.Dispose() }
    try {
        if ($ownedDatabase) {
            Invoke-RecoverySql $master "IF DB_ID(N'$ownedDatabase') IS NOT NULL BEGIN ALTER DATABASE [$ownedDatabase] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$ownedDatabase]; END"
            Invoke-RecoveryDocker @('exec', $config.containerId, 'rm', '-f', '--', "$native.mdf", "$native.ldf") | Out-Null
        }
        if ($ownedObjects) { Remove-RecoveryObjects $ownedObjects $objects }
        if ($nativeBackupDirectoryCreated) {
            Invoke-RecoveryDocker @('exec', '--user', 'mssql', $config.containerId, 'rm', '-f', '--', $nativeBackup) | Out-Null
            Invoke-RecoveryDocker @('exec', '--user', 'mssql', $config.containerId, 'rmdir', '--', $nativeBackupDirectory) | Out-Null
        }
        if ([IO.File]::Exists($readyTemporary)) { [IO.File]::Delete($readyTemporary) }
    } finally { $master.Dispose() }
    Write-RecoveryPhase 'restore-cleanup-complete'
}
