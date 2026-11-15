#requires -Version 7.6
[CmdletBinding()]
param(
    [string]$ConfigurationPath,
    [string]$BackupDirectory,
    [switch]$Quiesced,
    [string]$ApplicationDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Restore-Local dot-sources these recovery-specific checks; dot-sourcing never starts a backup.
function Initialize-RecoveryRuntime([string]$Directory) {
    if ([Environment]::Version.Major -lt 10) { throw 'recovery_runtime: PowerShell on .NET 10 is required.' }
    if (-not $Directory) {
        $Directory = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Cohors.Api\bin\Debug\net10.0'
    }
    $Directory = [IO.Path]::GetFullPath($Directory)
    $platform = if ($IsWindows) { 'win' } else { 'unix' }
    [AppContext]::SetSwitch('Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows', $true)
    foreach ($name in @('Cohors.Domain.dll', 'Cohors.Application.dll', 'Azure.Core.dll', 'Azure.Identity.dll',
        'Azure.Search.Documents.dll', 'Microsoft.Data.SqlClient.Internal.Logging.dll',
        'Microsoft.Data.SqlClient.Extensions.Abstractions.dll', 'Microsoft.SqlServer.Server.dll',
        "runtimes\$platform\lib\net9.0\Microsoft.Data.SqlClient.dll", 'Cohors.Infrastructure.dll')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw 'recovery_runtime: Build the existing API before running recovery.'
        }
        [Reflection.Assembly]::LoadFrom($path) | Out-Null
    }
}

function Assert-RecoveryPath([string]$Path, [switch]$New) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'recovery_path: An explicit path is required.' }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $local = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) '.local'))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $full.StartsWith($local + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw 'recovery_path: Recovery data must be in a dedicated directory below this worktree .local.'
    }
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if ((Test-Path -LiteralPath $part) -and (Get-Item -LiteralPath $part -Force).LinkTarget) {
            throw 'recovery_path: Linked files or directories are not accepted.'
        }
    }
    if ($New -and (Test-Path -LiteralPath $full)) {
        throw 'recovery_target_exists: Existing paths are never reused or overwritten.'
    }
    return $full
}

function Invoke-RecoveryDocker([string[]]$Arguments) {
    $output = @(& docker @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw 'recovery_docker: The explicit owned-container operation failed; no success is asserted.'
    }
    return $output
}

function Read-RecoveryConfiguration([string]$Path) {
    $path = Assert-RecoveryPath $Path
    if ([IO.Path]::GetFileName($path) -in @('settings.json', 'demo-access.txt')) {
        throw 'recovery_configuration: Use a dedicated recovery configuration, never the running demo settings.'
    }
    $config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -Depth 20
    if ($config.syntheticOnly -ne $true -or $config.ownerId -cnotmatch '^[a-f0-9]{32}$' -or
        $config.containerId -cnotmatch '^[a-f0-9]{64}$') {
        throw 'recovery_configuration: Explicit synthetic-only scope and owned container identity are required.'
    }
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder.set_ConnectionString($config.connectionString)
    $aliases = [ordered]@{
        Server = @('Server', 'Data Source'); Database = @('Database', 'Initial Catalog')
        'User Id' = @('User Id'); Password = @('Password'); Encrypt = @('Encrypt')
        TrustServerCertificate = @('TrustServerCertificate', 'Trust Server Certificate')
    }
    $values = @{}
    foreach ($key in $aliases.Keys) {
        $found = @()
        foreach ($alias in $aliases[$key]) {
            $value = $null
            if ($builder.TryGetValue($alias, [ref]$value)) { $found += [string]$value }
        }
        if ($found.Count -ne 1 -or [string]::IsNullOrWhiteSpace($found[0])) {
            throw 'recovery_configuration: Incomplete explicit local SQL connection.'
        }
        $values[$key] = $found[0]
    }
    if ($values.Server -cnotmatch '^127\.0\.0\.1,([1-9][0-9]{3,4})$' -or
        $values.Database -cnotmatch '^Cohors_(Test|Recovery)_[a-f0-9]{32}(_[a-z0-9_]{1,20})?$' -or
        $values['User Id'] -ne 'sa' -or $values.Encrypt -ne 'True' -or $values.TrustServerCertificate -ne 'True') {
        throw 'recovery_configuration: Only named disposable Cohors databases on explicit loopback SQL are supported.'
    }
    $port = [int]($values.Server.Split(',')[1])
    if ($port -gt 65535) { throw 'recovery_configuration: Invalid SQL port.' }
    foreach ($key in $builder.Keys) {
        if ($key -notin @('Server', 'Data Source', 'Database', 'Initial Catalog', 'User Id', 'Password',
            'Encrypt', 'TrustServerCertificate', 'Trust Server Certificate', 'Pooling', 'Connect Timeout')) {
            throw 'recovery_configuration: Unsupported SQL connection option.'
        }
    }
    $identity = (Invoke-RecoveryDocker @('inspect', '--format',
        '{{.Id}}|{{index .Config.Labels "cohors.recovery.owner"}}|{{.State.Running}}', $config.containerId)) -join ''
    if ($identity -cne "$($config.containerId)|$($config.ownerId)|true") {
        throw 'recovery_ownership: Container identity, ownership label or running state differs.'
    }
    $ports = ((Invoke-RecoveryDocker @('inspect', '--format', '{{json .NetworkSettings.Ports}}', $config.containerId)) -join '') |
        ConvertFrom-Json -AsHashtable
    $bindings = @($ports['1433/tcp'])
    if ($bindings.Count -ne 1 -or $bindings[0].HostIp -ne '127.0.0.1' -or [int]$bindings[0].HostPort -ne $port) {
        throw 'recovery_ownership: SQL must resolve to the exact owned loopback container.'
    }
    $config.objectRoot = Assert-RecoveryPath $config.objectRoot
    $config.database = $values.Database
    return $config
}

function Open-RecoverySql($Config, [string]$Database) {
    $builder = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($Config.connectionString)
    if ($Database) { $builder.set_InitialCatalog($Database) }
    $builder.set_Pooling($false)
    $builder.set_ConnectTimeout(15)
    $connection = [Microsoft.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try { $connection.Open(); return $connection }
    catch { $connection.Dispose(); throw 'recovery_sql: Cannot open the explicitly configured local database.' }
}

function Invoke-RecoverySql($Connection, [string]$Sql, [switch]$Scalar) {
    $command = [Microsoft.Data.SqlClient.SqlCommand]::new($Sql, $Connection)
    $command.CommandTimeout = 180
    try {
        if ($Scalar) { return $command.ExecuteScalar() }
        $command.ExecuteNonQuery() | Out-Null
    } finally { $command.Dispose() }
}

function Get-RecoveryHash($Value) {
    return [Cohors.Domain.Canonical]::HashText((ConvertTo-Json -InputObject $Value -Depth 100 -Compress))
}

function Assert-RecoverySeparatePaths([string]$First, [string]$Second) {
    $First = [IO.Path]::TrimEndingDirectorySeparator($First)
    $Second = [IO.Path]::TrimEndingDirectorySeparator($Second)
    $separator = [IO.Path]::DirectorySeparatorChar
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ($First.Equals($Second, $comparison) -or
        $First.StartsWith($Second + $separator, $comparison) -or
        $Second.StartsWith($First + $separator, $comparison)) {
        throw 'recovery_path_overlap: Backup and object roots must be disjoint.'
    }
}

function Get-RecoveryReferences([string[]]$Payloads) {
    $hashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $parsed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.Stack[object]]::new()
    foreach ($payload in $Payloads) { $pending.Push($payload) }
    while ($pending.Count) {
        $node = $pending.Pop()
        if ($node -is [string]) {
            if (-not $parsed.Add([Cohors.Domain.Canonical]::HashText($node))) { continue }
            $node = ConvertFrom-Json -InputObject $node -AsHashtable -Depth 100 -DateKind String -NoEnumerate
        }
        if ($node -is [Collections.IDictionary]) {
            $references = [Collections.Generic.List[string]]::new()
            if ($node.Contains('objectKey')) {
                $evidence = [Text.Json.JsonSerializer]::Deserialize(
                    (ConvertTo-Json -InputObject $node -Depth 100 -Compress),
                    [Cohors.Domain.Evidence], [Cohors.Domain.Canonical]::Json)
                [Cohors.Domain.EvidenceRules]::RequireIntegrity($evidence)
                if (-not $evidence.Synthetic) { throw 'recovery_scope: Only synthetic evidence is supported.' }
                $references.Add($node.objectKey)
            }
            if ($node.Contains('xmlHash')) {
                if (-not $node.Contains('xml') -or [Cohors.Domain.Canonical]::HashText($node.xml) -cne $node.xmlHash) {
                    throw 'recovery_sql_integrity: MIR bytes and bound hash differ.'
                }
                $references.Add($node.xmlHash)
            }
            if (($node.Contains('uri') -or $node.Contains('evidenceId')) -and $node.Contains('hash')) {
                $references.Add($node.hash)
            }
            if ($node.Contains('attachmentHashes')) {
                foreach ($hash in $node.attachmentHashes) { $references.Add($hash) }
            }
            foreach ($hash in $references) {
                if ($hash -cnotmatch '^[a-f0-9]{64}$') { throw 'recovery_reference: Invalid content-addressed reference.' }
                $hashes.Add($hash) | Out-Null
            }
            foreach ($entry in $node.GetEnumerator()) {
                if ($entry.Value -is [Collections.IDictionary] -or $entry.Value -is [array]) {
                    $pending.Push($entry.Value)
                } elseif ($entry.Key -in @('Json', 'Payload', 'Result', 'frozenJson', 'inputJson', 'outputJson') -and $entry.Value) {
                    $pending.Push([string]$entry.Value)
                }
            }
        } elseif ($node -is [array]) {
            foreach ($item in $node) {
                if ($item -is [Collections.IDictionary] -or $item -is [array]) { $pending.Push($item) }
            }
        }
    }
    return ,@($hashes | Sort-Object -CaseSensitive)
}

function New-RecoveryStore($Config, [string]$Database) {
    $builder = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($Config.connectionString)
    $builder.set_InitialCatalog($Database)
    $builder.set_Pooling($false)
    return [Cohors.Infrastructure.SqlStateStore]::new($builder.ConnectionString, $null)
}

function Get-RecoveryState($Connection, $Config) {
    $orders = [ordered]@{
        Aggregates = 'Kind,Id'; AggregateVersions = 'Kind,Id,Version'; AuditEvents = 'Sequence'
        Commands = 'IdempotencyKey'; Deadlines = 'Id'; Inbox = 'EventId,Consumer'
        Outbox = 'Id'; SchemaVersions = 'Version'
    }
    $names = [string](Invoke-RecoverySql $Connection "SELECT STRING_AGG(name,',') WITHIN GROUP(ORDER BY name) FROM sys.tables WHERE schema_id=SCHEMA_ID('dbo') AND is_ms_shipped=0" -Scalar)
    if ($names -cne ((@($orders.Keys) + 'SearchProjection' | Sort-Object) -join ',') -or
        (Invoke-RecoverySql $Connection 'SELECT MAX(Version) FROM dbo.SchemaVersions' -Scalar) -ne [Cohors.Infrastructure.SqlStateStore]::SchemaVersion) {
        throw 'recovery_schema: Unsupported schema or unaccounted authoritative tables.'
    }
    $rows = @{}
    $tables = @()
    foreach ($name in $orders.Keys) {
        $json = [string](Invoke-RecoverySql $Connection "SELECT (SELECT * FROM dbo.[$name] ORDER BY $($orders[$name]) FOR JSON PATH,INCLUDE_NULL_VALUES)" -Scalar)
        $rows[$name] = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 100 -DateKind String -NoEnumerate
        $tables += [ordered]@{ name = $name; count = $rows[$name].Count; sha256 = [Cohors.Domain.Canonical]::HashText($json) }
    }
    $store = New-RecoveryStore $Config $Connection.Database
    $history = @{}
    $historyHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $expectedDeadlines = @{}
    $aggregates = @{}
    $versionCounts = @{}
    foreach ($row in $rows.Aggregates) {
        $key = "$($row.Kind)|$($row.Id)"
        $aggregates[$key] = $row
        $versionCounts[$key] = 0
    }
    foreach ($row in $rows.AggregateVersions) {
        $key = "$($row.Kind)|$($row.Id)"
        if (-not $aggregates.ContainsKey($key) -or $row.Version -lt 1 -or $row.Version -gt $aggregates[$key].Version) {
            throw 'recovery_sql_integrity: Orphan or out-of-range historical revision.'
        }
        $versionCounts[$key]++
        if ([Cohors.Domain.Canonical]::HashText($row.Json) -cne $row.Hash) {
            throw 'recovery_sql_integrity: Historical SQL payload hash differs.'
        }
        $history["$($row.Kind)|$($row.Id)|$($row.Version)"] = $row.Json
        $historyHashes.Add($row.Hash) | Out-Null
        if ($row.Kind -eq 'case') {
            $case = [Text.Json.JsonSerializer]::Deserialize($row.Json, [Cohors.Domain.SafetyCase], [Cohors.Domain.Canonical]::Json)
            if (-not $case.Synthetic) { throw 'recovery_scope: Only synthetic cases are supported.' }
            foreach ($report in $case.Reports | Where-Object FrozenJson) {
                $frozen = [Text.Json.JsonSerializer]::Deserialize($report.FrozenJson, [Cohors.Domain.ReportDraft], [Cohors.Domain.Canonical]::Json)
                $assessment = @($case.Assessments | Where-Object Version -EQ $report.AssessmentVersion)
                if ($assessment.Count -ne 1 -or $frozen.State -ne [Cohors.Domain.ReportState]::Frozen -or
                    $frozen.ScopeHash -cne $report.ScopeHash -or
                    [Cohors.Domain.ReportRules]::Scope($assessment[0], $frozen) -cne $report.ScopeHash -or
                    [Cohors.Domain.ReportRules]::Scope($assessment[0], $report) -cne $report.ScopeHash) {
                    throw 'recovery_sql_integrity: Frozen report or its historical assessment binding differs.'
                }
            }
        }
    }
    foreach ($row in $rows.Aggregates) {
        if ($versionCounts["$($row.Kind)|$($row.Id)"] -ne $row.Version) {
            throw 'recovery_sql_integrity: Missing historical revision.'
        }
        $store.Manifest($row.Id, [Threading.CancellationToken]::None, $row.Kind).GetAwaiter().GetResult() | Out-Null
        if ($row.Kind -eq 'case') {
            $case = [Text.Json.JsonSerializer]::Deserialize($row.Json, [Cohors.Domain.SafetyCase], [Cohors.Domain.Canonical]::Json)
            foreach ($deadline in $case.Deadlines) {
                if ($expectedDeadlines.ContainsKey($deadline.Id)) { throw 'recovery_sql_integrity: Duplicate deadline identity.' }
                $expectedDeadlines[$deadline.Id] = @{ deadline = $deadline; caseId = $case.Id; productId = $case.ProductId; version = $case.Version }
            }
        }
    }
    if ($expectedDeadlines.Count -ne $rows.Deadlines.Count) { throw 'recovery_sql_integrity: Incomplete deadline projection.' }
    foreach ($row in $rows.Deadlines) {
        $expected = $expectedDeadlines[$row.Id]
        if (-not $expected -or $row.CaseId -cne $expected.caseId -or $row.ProductId -cne $expected.productId -or
            $row.Version -ne $expected.version -or $row.Status -cne $expected.deadline.Status -or
            -not $expected.deadline.LegalDue.EqualsExact([DateTimeOffset]::Parse($row.LegalDue)) -or
            -not $expected.deadline.SafetyDue.EqualsExact([DateTimeOffset]::Parse($row.SafetyDue))) {
            throw 'recovery_sql_integrity: SQL deadline projection differs from the authoritative case.'
        }
    }
    $events = @{}
    foreach ($row in $rows.Outbox) { $events[$row.Id] = $row }
    $receipts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows.Inbox) {
        if (-not $events.ContainsKey($row.EventId) -or -not $events[$row.EventId].CompletedAt) {
            throw 'recovery_sql_integrity: Receipt has no completed outbox event.'
        }
        $receipts.Add($row.EventId) | Out-Null
    }
    foreach ($row in $rows.Outbox) {
        $key = "$($row.AggregateKind)|$($row.AggregateId)|$($row.Version)"
        if (-not $history.ContainsKey($key) -or $history[$key] -cne $row.Payload -or
            $aggregates["$($row.AggregateKind)|$($row.AggregateId)"].ProductId -cne $row.ProductId -or
            ($row.CompletedAt -and -not $receipts.Contains($row.Id))) {
            throw 'recovery_sql_integrity: Outbox payload/history or completion/receipt coverage differs.'
        }
    }
    foreach ($row in $rows.Commands) {
        if (-not $historyHashes.Contains([Cohors.Domain.Canonical]::HashText($row.Result))) {
            throw 'recovery_sql_integrity: Command receipt does not bind a retained revision.'
        }
    }
    $payloads = @($rows.Aggregates.Json) + @($rows.AggregateVersions.Json) + @($rows.Commands.Result) + @($rows.Outbox.Payload)
    return @{ tables = $tables; references = (Get-RecoveryReferences $payloads) }
}

function Get-RecoveryObjects([string]$Directory) {
    $directory = Assert-RecoveryPath $Directory
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw 'recovery_object_missing: The explicit object root is missing.'
    }
    $result = @()
    foreach ($file in Get-ChildItem -LiteralPath $directory -Force | Sort-Object Name -CaseSensitive) {
        if ($file.PSIsContainer -or $file.Name -cnotmatch '^[a-f0-9]{64}$' -or $file.LinkTarget) {
            throw 'recovery_object_shape: Only flat, regular content-addressed files are accepted.'
        }
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))
            if ($hash -cne $file.Name) { throw 'recovery_object_hash: Object bytes differ from their address.' }
            $result += [ordered]@{ hash = $hash; length = $stream.Length }
        } finally { $stream.Dispose() }
    }
    return ,$result
}

function Assert-RecoveryCoverage($State, $Objects) {
    $available = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Objects) { $available.Add($item.hash) | Out-Null }
    foreach ($hash in $State.references) {
        if (-not $available.Contains($hash)) { throw 'recovery_reference_coverage: SQL references bytes absent from this object cut.' }
    }
}

function Copy-RecoveryObjects([string]$Source, [string]$Destination, $Objects) {
    foreach ($item in $Objects) {
        $inputStream = [IO.File]::Open((Join-Path $Source $item.hash), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $outputStream = [IO.File]::Open((Join-Path $Destination $item.hash), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) }
            finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
    }
}

function Remove-RecoveryObjects([string]$Directory, $Objects) {
    $directory = Assert-RecoveryPath $Directory
    foreach ($item in $Objects) { [IO.File]::Delete((Join-Path $directory $item.hash)) }
    if (Test-Path -LiteralPath $directory) { [IO.Directory]::Delete($directory, $false) }
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $Quiesced) { throw 'recovery_quiescence: Stop writers and explicitly acknowledge -Quiesced.' }
Initialize-RecoveryRuntime $ApplicationDirectory
$config = Read-RecoveryConfiguration $ConfigurationPath
$backup = Assert-RecoveryPath $BackupDirectory -New
Assert-RecoverySeparatePaths $backup $config.objectRoot
$connection = Open-RecoverySql $config $config.database
$native = '/var/opt/mssql/data/cohors_recovery_' + [Guid]::NewGuid().ToString('N') + '.bak'
$nativeCreated = $false
try {
    if ((Invoke-RecoverySql $connection 'SELECT is_read_only FROM sys.databases WHERE database_id=DB_ID()' -Scalar) -ne $true) {
        throw 'recovery_quiescence: The source database must already be READ_ONLY; this script never stops or changes source writers.'
    }
    $before = Get-RecoveryState $connection $config
    $objects = Get-RecoveryObjects $config.objectRoot
    Assert-RecoveryCoverage $before $objects
    New-Item -ItemType Directory -Path $backup | Out-Null
    $objectCopy = Join-Path $backup 'objects'
    New-Item -ItemType Directory -Path $objectCopy | Out-Null
    $nativeCreated = $true
    Invoke-RecoverySql $connection "BACKUP DATABASE [$($config.database)] TO DISK=N'$native' WITH COPY_ONLY,CHECKSUM"
    Invoke-RecoverySql $connection "RESTORE VERIFYONLY FROM DISK=N'$native' WITH CHECKSUM"
    Invoke-RecoveryDocker @('cp', "$($config.containerId):$native", (Join-Path $backup 'database.bak')) | Out-Null
    Copy-RecoveryObjects $config.objectRoot $objectCopy $objects
    $after = Get-RecoveryState $connection $config
    if ((Invoke-RecoverySql $connection 'SELECT is_read_only FROM sys.databases WHERE database_id=DB_ID()' -Scalar) -ne $true -or
        (Get-RecoveryHash $before) -cne (Get-RecoveryHash $after) -or
        (Get-RecoveryHash $objects) -cne (Get-RecoveryHash (Get-RecoveryObjects $config.objectRoot)) -or
        (Get-RecoveryHash $objects) -cne (Get-RecoveryHash (Get-RecoveryObjects $objectCopy))) {
        throw 'recovery_cut_changed: Source SQL or object inventory changed; the partial backup is not complete.'
    }
    Invoke-RecoveryDocker @('exec', $config.containerId, 'rm', '--', $native) | Out-Null
    $nativeCreated = $false
    $manifest = [ordered]@{
        format = 'cohors-local-recovery-1'; schemaVersion = [Cohors.Infrastructure.SqlStateStore]::SchemaVersion
        syntheticOnly = $true; capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
        sourceDatabase = $config.database; databaseSha256 = (Get-FileHash -LiteralPath (Join-Path $backup 'database.bak')).Hash.ToLowerInvariant()
        tables = $before.tables; references = $before.references; objects = $objects
        cut = 'Explicitly quiesced, READ_ONLY SQL; verified object inventory before and after copy. No distributed transaction.'
        search = 'Rebuild KnowledgeSearch without SearchClient; SQL-derived local projection, not Azure AI Search.'
        externalLedgerWitness = $false
    }
    $manifestPath = Join-Path $backup 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 30), [Text.UTF8Encoding]::new($false))
    Write-Output ('RECOVERY_MANIFEST_SHA256=' + (Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant())
} finally {
    $connection.Dispose()
    if ($nativeCreated) { Invoke-RecoveryDocker @('exec', $config.containerId, 'rm', '-f', '--', $native) | Out-Null }
}
