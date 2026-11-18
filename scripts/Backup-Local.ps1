#requires -Version 7.6
[CmdletBinding()]
param(
    [string]$ConfigurationPath,
    [string]$BackupDirectory,
    [switch]$Quiesced,
    [string]$ApplicationDirectory,
    [string]$TimingPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:recoveryTiming = $null

function Initialize-RecoveryTiming([string]$Path) {
    if (-not $Path) { return }
    $path = Assert-RecoveryPath $Path -New
    $stream = [IO.FileStream]::new($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $stream.Dispose()
    $script:recoveryTiming = @{ path = $path; started = [Diagnostics.Stopwatch]::GetTimestamp(); sequence = 0 }
    Write-RecoveryPhase 'timing-start'
}

function Write-RecoveryPhase([string]$Phase, [long]$Count = -1, [long]$Characters = -1) {
    if ($null -eq $script:recoveryTiming) { return }
    $timestamp = [Diagnostics.Stopwatch]::GetTimestamp()
    $record = [ordered]@{
        variant = 'RecoveryBackup'; phase = $Phase; sequence = ++$script:recoveryTiming.sequence
        timestampTicks = $timestamp; frequency = [Diagnostics.Stopwatch]::Frequency
        elapsedTicks = $timestamp - $script:recoveryTiming.started; clockScope = 'script-timing-initialization'
        at = [DateTimeOffset]::UtcNow.ToString('O')
    }
    if ($Count -ge 0) { $record['count'] = $Count }
    if ($Characters -ge 0) { $record['jsonCharacters'] = $Characters }
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $record -Compress) + "`n")
    $stream = [IO.FileStream]::new($script:recoveryTiming.path, [IO.FileMode]::Append, [IO.FileAccess]::Write,
        [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}

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

function Convert-RecoveryRetentionRecord($Node) {
    if ($Node.contract -cne 'cohors-retention-record-1') { throw 'recovery_retention_contract: Unknown retention record.' }
    $value = [Text.Json.JsonSerializer]::Deserialize((ConvertTo-Json -InputObject $Node -Depth 100 -Compress),
        [Cohors.Domain.RetentionRecord], [Cohors.Domain.Canonical]::Json)
    if (-not $value.Synthetic -or $value.PolicyVersion -cne [Cohors.Domain.RetentionRules]::PolicyVersion) {
        throw 'recovery_retention_contract: Only the explicit versioned synthetic retention profile is supported.'
    }
    [Cohors.Domain.RetentionRules]::RequireObject($value.Object)
    return $value
}

function Get-RecoveryRetentionBinding($Node) {
    return Get-RecoveryHash ([ordered]@{
        id = $Node.id; productId = $Node.productId; object = $Node.object
        policyVersion = $Node.policyVersion; reference = $Node.reference
        receivedAt = $Node.receivedAt; recordedAt = $Node.recordedAt; synthetic = $Node.synthetic
    })
}

function Get-RecoveryRetentionCut($Aggregates) {
    $result = @()
    foreach ($row in $Aggregates | Where-Object { $_.Kind -ceq 'retention' } | Sort-Object { $_.Id } -CaseSensitive) {
        $node = ConvertFrom-Json -InputObject $row.Json -AsHashtable -Depth 100 -DateKind String
        $value = Convert-RecoveryRetentionRecord $node
        if ($value.Id -cne $row.Id -or $value.Version -ne $row.Version -or $value.ProductId -cne $row.ProductId) {
            throw 'recovery_retention_contract: Retention identity/version differs from SQL.'
        }
        if ($value.Object.DataClass -cne [Cohors.Domain.RetentionRules]::Raw) { continue }
        if ($value.Status -cnotin @('Registered', 'Held', 'Authorized', 'Deleted')) {
            throw 'recovery_retention_incomplete: Reconcile in-flight/failed physical retention before capturing a cut.'
        }
        if ($value.Authorization -and
            ($value.Authorization.ScopeHash -cne [Cohors.Domain.RetentionRules]::Scope($value, $null) -or
             -not $value.Authorization.ExternalCopiesReviewed)) {
            throw 'recovery_retention_authorization: Raw authorization is not bound to this scope.'
        }
        $receiptHash = $null
        if ($value.Status -ceq 'Deleted') {
            if (-not $value.Deletion -or -not $value.Authorization -or $value.LegalHold) {
                throw 'recovery_retention_receipt: A deleted raw object needs its exact authorized receipt without hold.'
            }
            [Cohors.Domain.RetentionRules]::RequireDeletionReceipt($value, $value.Deletion)
            $evaluation = [Cohors.Domain.RetentionRules]::Evaluate($value, $null, $value.Deletion.At, 0, $true)
            if ($evaluation.MinimumIntervalElapsed -ne $true -or $value.Deletion.At -lt $value.Authorization.At) {
                throw 'recovery_retention_receipt: Deletion predates its minimum or authorization.'
            }
            $receiptHash = [Cohors.Domain.Canonical]::Hash[Cohors.Domain.ObjectDeletionReceipt]($value.Deletion)
        } elseif ($value.Deletion -or ($value.Status -ceq 'Authorized' -and -not $value.Authorization)) {
            throw 'recovery_retention_contract: Inconsistent raw retention state.'
        }
        $result += [ordered]@{
            id = $value.Id; version = $value.Version; productId = $value.ProductId
            sourceHash = [Cohors.Domain.Canonical]::HashText($row.Json)
            bindingHash = Get-RecoveryRetentionBinding $node
            objectKey = $value.Object.ObjectKey; objectHash = $value.Object.Hash; status = $value.Status
            deletionReceiptHash = $receiptHash
        }
    }
    return [ordered]@{ contract = 'cohors-retention-cut-1'; rows = $result }
}

function Get-RecoveryEudamedReferences($Revisions) {
    $hashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.Stack[object]]::new()
    $dossiers = @{}
    function Read-EudamedField($Value, [string]$Name) {
        if ($Value -isnot [Collections.IDictionary] -or -not $Value.Contains($Name)) {
            throw 'recovery_reference: Incomplete typed EUDAMED original binding.'
        }
        return ,$Value[$Name]
    }
    function Add-EudamedObject($Hash, [switch]$Nullable) {
        if ($Nullable -and $null -eq $Hash) { return }
        if ($Hash -isnot [string] -or $Hash -cnotmatch '^[a-f0-9]{64}$') {
            throw 'recovery_reference: Invalid typed EUDAMED object address.'
        }
        $hashes.Add($Hash) | Out-Null
    }
    function Push-EudamedNode([string]$Kind, $Value) {
        if ($Value -isnot [Collections.IDictionary]) {
            throw 'recovery_reference: Missing typed EUDAMED object node.'
        }
        $pending.Push(@{ kind = $Kind; value = $Value })
    }
    function Push-EudamedArray($Value, [string]$Name, [string]$Kind) {
        $items = Read-EudamedField $Value $Name
        if ($items -isnot [array]) { throw 'recovery_reference: Incomplete typed EUDAMED original collection.' }
        foreach ($item in $items) { Push-EudamedNode $Kind $item }
    }
    foreach ($revision in $Revisions) {
        $kind = [string]$revision.Kind
        if (-not $kind.StartsWith('eudamed', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($kind -cnotin @('eudamedActor', 'eudamedReferences', 'eudamedConfiguration', 'eudamedChannel',
            'eudamedDocument', 'eudamedDossier', 'eudamedDelivery', 'eudamedInbox')) {
            throw 'recovery_reference: Unsupported EUDAMED aggregate kind; original coverage is unknown.'
        }
        $json = $revision.Json
        if ($json -isnot [string] -or [string]::IsNullOrWhiteSpace($json)) {
            throw 'recovery_reference: Missing typed EUDAMED SQL snapshot.'
        }
        $digest = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json)))
        if (-not $seen.Add($kind + ':' + $digest)) { continue }
        $value = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 100 -DateKind String
        Push-EudamedNode $kind $value
        if ($kind -ceq 'eudamedDossier') { $dossiers[$digest] = $value }
    }
    while ($pending.Count) {
        $item = $pending.Pop()
        $node = $item.value
        switch -CaseSensitive ($item.kind) {
            { $_ -cin @('eudamedActor', 'eudamedConfiguration', 'eudamedChannel') } { break }
            'eudamedReferences' { Push-EudamedNode 'certificate' (Read-EudamedField $node 'certificate'); break }
            'certificate' { Add-EudamedObject (Read-EudamedField $node 'originalHash'); break }
            'eudamedDocument' { Add-EudamedObject (Read-EudamedField $node 'originalHash'); break }
            'eudamedDossier' {
                $frozen = Read-EudamedField $node 'frozenPackageHash'
                if ((Read-EudamedField $node 'reviewStatus') -ceq 'Frozen' -and $null -eq $frozen) {
                    throw 'recovery_reference: Frozen EUDAMED dossier has no stored package binding.'
                }
                Add-EudamedObject $frozen -Nullable
                Push-EudamedNode 'package' (Read-EudamedField $node 'package')
                Push-EudamedArray $node 'attempts' 'attempt'
                Push-EudamedArray $node 'businessResponses' 'original'
                Push-EudamedArray $node 'correspondence' 'original'
                Push-EudamedArray $node 'distributions' 'distribution'
                break
            }
            'package' {
                Push-EudamedNode 'eudamedReferences' (Read-EudamedField $node 'references')
                Push-EudamedArray $node 'attachments' 'attachment'
                Push-EudamedArray $node 'sources' 'source'
                $fsn = Read-EudamedField (Read-EudamedField $node 'content') 'fsn'
                if ($null -ne $fsn) { Add-EudamedObject (Read-EudamedField $fsn 'documentHash') }
                # BusinessXmlHash, Source.Hash, ScopeHash and signature hashes bind inline data, not separate objects.
                break
            }
            'source' {
                $json = Read-EudamedField $node 'json'
                $binding = Read-EudamedField $node 'source'
                if ($json -isnot [string] -or [string]::IsNullOrWhiteSpace($json) -or
                    [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))) -cne
                        (Read-EudamedField $binding 'hash')) {
                    throw 'recovery_sql_integrity: Embedded EUDAMED source differs from its version binding.'
                }
                $source = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 100 -DateKind String
                if ((Read-EudamedField $source 'id') -cne (Read-EudamedField $binding 'id') -or
                    (Read-EudamedField $source 'version') -ne (Read-EudamedField $binding 'version')) {
                    throw 'recovery_sql_integrity: Embedded EUDAMED source identity/version differs.'
                }
                $kind = Read-EudamedField $node 'kind'
                if ($kind -cin @('eudamedReferences', 'eudamedDossier', 'eudamedDocument')) {
                    Push-EudamedNode $kind $source
                } elseif ($kind -cnotin @('case', 'trend', 'summaryAgreement', 'summaryReport', 'psur', 'capa')) {
                    throw 'recovery_reference: Unsupported embedded EUDAMED source kind.'
                }
                # Legacy case/PSUR sources are also traversed by Get-RecoveryReferences, including nested JSON.
                break
            }
            'attachment' { Add-EudamedObject (Read-EudamedField $node 'hash'); break }
            'eudamedDelivery' { Push-EudamedArray $node 'entries' 'deliveryEntry'; break }
            'deliveryEntry' {
                # Unlike an ordinary VersionReference, QueueDossier stores this approved snapshot as an object.
                $binding = Read-EudamedField $node 'approvedDossier'
                $hash = Read-EudamedField $binding 'hash'
                Add-EudamedObject $hash
                if (-not $dossiers.ContainsKey($hash) -or
                    (Read-EudamedField $dossiers[$hash] 'id') -cne (Read-EudamedField $binding 'id') -or
                    (Read-EudamedField $dossiers[$hash] 'version') -ne (Read-EudamedField $binding 'version')) {
                    throw 'recovery_sql_integrity: Approved EUDAMED object snapshot is not the bound retained SQL version.'
                }
                Push-EudamedNode 'attempt' (Read-EudamedField $node 'attempt')
                Push-EudamedArray $node 'responses' 'original'
                Push-EudamedArray $node 'correspondence' 'original'
                break
            }
            'attempt' {
                Add-EudamedObject (Read-EudamedField $node 'packageHash')
                Add-EudamedObject (Read-EudamedField $node 'receiptHash') -Nullable
                break
            }
            'original' { Add-EudamedObject (Read-EudamedField $node 'originalHash'); break }
            'distribution' {
                Add-EudamedObject (Read-EudamedField $node 'packageHash')
                Add-EudamedObject (Read-EudamedField $node 'evidenceHash')
                break
            }
            'eudamedInbox' {
                Add-EudamedObject (Read-EudamedField $node 'originalHash')
                Add-EudamedObject (Read-EudamedField $node 'protectedTransportHash') -Nullable
                $original = Read-EudamedField $node 'original'
                if ($null -ne $original) { Add-EudamedObject (Read-EudamedField $original 'packageHash') }
                break
            }
            default { throw 'recovery_reference: Unsupported typed EUDAMED original node.' }
        }
    }
    return ,@($hashes | Sort-Object -CaseSensitive)
}

function Get-RecoveryReferences([string[]]$Payloads, $RetentionCut, $Revisions = @()) {
    $hashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($hash in (Get-RecoveryEudamedReferences $Revisions)) { $hashes.Add($hash) | Out-Null }
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
            $retentionRecord = $node.Contains('contract') -and $node.contract -ceq 'cohors-retention-record-1'
            if ($retentionRecord) {
                $value = Convert-RecoveryRetentionRecord $node
                if ($value.Object.DataClass -ceq [Cohors.Domain.RetentionRules]::Raw) {
                    $current = @($RetentionCut.rows | Where-Object { $_.id -ceq $value.Id })
                    if ($current.Count -ne 1 -or $current[0].version -lt $value.Version -or
                        $current[0].bindingHash -cne (Get-RecoveryRetentionBinding $node)) {
                        throw 'recovery_retention_binding: Historical raw identity or immutable binding differs.'
                    }
                    if ($current[0].status -cne 'Deleted') { $references.Add($value.Object.ObjectKey) }
                } else { $references.Add($value.Object.ObjectKey) }
            } elseif ($node.Contains('objectKey') -and $node.Contains('contract') -and
                $node.contract -ceq [Cohors.Domain.RetentionRules]::ObjectContract) {
                $binding = [Text.Json.JsonSerializer]::Deserialize(
                    (ConvertTo-Json -InputObject $node -Depth 100 -Compress),
                    [Cohors.Domain.RetentionObjectBinding], [Cohors.Domain.Canonical]::Json)
                [Cohors.Domain.RetentionRules]::RequireObject($binding)
                # An independent binding remains required even when its own raw record says Deleted.
                $references.Add($binding.ObjectKey)
            } elseif ($node.Contains('objectKey')) {
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
                if ($hash -cnotmatch '^([a-f0-9]{64}|raw-quarantine/[a-f0-9]{64})$') {
                    throw 'recovery_reference: Invalid content-addressed or explicitly classified raw reference.'
                }
                $hashes.Add($hash) | Out-Null
            }
            foreach ($entry in $node.GetEnumerator()) {
                if ($retentionRecord -and $entry.Key -in @('object', 'deletion')) { continue }
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
    Write-RecoveryPhase 'state-schema-start'
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
        Write-RecoveryPhase "state-table-$name-start"
        $json = [string](Invoke-RecoverySql $Connection "SELECT (SELECT * FROM dbo.[$name] ORDER BY $($orders[$name]) FOR JSON PATH,INCLUDE_NULL_VALUES)" -Scalar)
        $rows[$name] = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 100 -DateKind String -NoEnumerate
        $tables += [ordered]@{ name = $name; count = $rows[$name].Count; sha256 = [Cohors.Domain.Canonical]::HashText($json) }
        Write-RecoveryPhase "state-table-$name-complete" $rows[$name].Count $json.Length
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
    Write-RecoveryPhase 'state-historical-bindings-start' $rows.AggregateVersions.Count
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
    Write-RecoveryPhase 'state-audit-manifests-start' $rows.Aggregates.Count
    $checkedManifests = 0
    foreach ($row in $rows.Aggregates) {
        if ($versionCounts["$($row.Kind)|$($row.Id)"] -ne $row.Version) {
            throw 'recovery_sql_integrity: Missing historical revision.'
        }
        $store.Manifest($row.Id, [Threading.CancellationToken]::None, $row.Kind).GetAwaiter().GetResult() | Out-Null
        $checkedManifests++
        if ($checkedManifests % 16 -eq 0) { Write-RecoveryPhase 'state-audit-manifests-progress' $checkedManifests }
        if ($row.Kind -eq 'case') {
            $case = [Text.Json.JsonSerializer]::Deserialize($row.Json, [Cohors.Domain.SafetyCase], [Cohors.Domain.Canonical]::Json)
            foreach ($deadline in $case.Deadlines) {
                if ($expectedDeadlines.ContainsKey($deadline.Id)) { throw 'recovery_sql_integrity: Duplicate deadline identity.' }
                $expectedDeadlines[$deadline.Id] = @{ deadline = $deadline; caseId = $case.Id; productId = $case.ProductId; version = $case.Version }
            }
        }
    }
    Write-RecoveryPhase 'state-audit-manifests-complete' $checkedManifests
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
    Write-RecoveryPhase 'state-retention-bindings-start'
    $retention = Get-RecoveryRetentionCut $rows.Aggregates
    # Commands and Outbox are checked above against these exact retained SQL revisions.
    $revisions = @($rows.Aggregates) + @($rows.AggregateVersions)
    Write-RecoveryPhase 'state-original-references-start' $payloads.Count
    $references = Get-RecoveryReferences $payloads $retention $revisions
    Write-RecoveryPhase 'state-original-references-complete' $references.Count
    return @{ tables = $tables; references = $references; retention = $retention }
}

function Get-RecoveryObjectKey($Item) {
    if (-not $Item.Contains('dataClass')) {
        if ($Item.hash -cnotmatch '^[a-f0-9]{64}$') { throw 'recovery_manifest: Invalid regulatory object address.' }
        return $Item.hash
    }
    if ($Item.dataClass -ceq 'RawQuarantine' -and $Item.objectKey -ceq ('raw-quarantine/' + $Item.hash) -and
        $Item.hash -cmatch '^[a-f0-9]{64}$') { return $Item.objectKey }
    if ($Item.dataClass -ceq 'RawDeletionJournal' -and $Item.objectHash -cmatch '^[a-f0-9]{64}$' -and
        $Item.objectKey -ceq ('.deletion-receipts/' + $Item.objectHash + '.json') -and $Item.hash -cmatch '^[a-f0-9]{64}$') {
        return $Item.objectKey
    }
    throw 'recovery_manifest: Unknown object data class or relative address.'
}

function Get-RecoveryObjectPath([string]$Root, $Item) {
    $key = Get-RecoveryObjectKey $Item
    $parts = $key.Split('/')
    if ($parts.Count -eq 1) { return Join-Path $Root $parts[0] }
    return Join-Path (Join-Path $Root $parts[0]) $parts[1]
}

function Get-RecoveryObjects([string]$Directory) {
    $directory = Assert-RecoveryPath $Directory
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw 'recovery_object_missing: The explicit object root is missing.'
    }
    $result = @()
    $files = @()
    foreach ($entry in Get-ChildItem -LiteralPath $directory -Force | Sort-Object Name -CaseSensitive) {
        if ($entry.LinkTarget) { throw 'recovery_object_shape: Linked object entries are not accepted.' }
        if ($entry.PSIsContainer) {
            if ($entry.Name -cnotin @('raw-quarantine', '.deletion-receipts')) {
                throw 'recovery_object_shape: Unknown object data-class directory.'
            }
            foreach ($file in Get-ChildItem -LiteralPath $entry.FullName -Force | Sort-Object Name -CaseSensitive) {
                if ($file.PSIsContainer -or $file.LinkTarget) { throw 'recovery_object_shape: Nested or linked object entries are not accepted.' }
                $files += @{ file = $file; area = $entry.Name }
            }
        } else { $files += @{ file = $entry; area = '' } }
    }
    foreach ($entry in $files) {
        $file = $entry.file
        $journal = $entry.area -ceq '.deletion-receipts'
        if (($journal -and $file.Name -cnotmatch '^[a-f0-9]{64}\.json$') -or
            (-not $journal -and $file.Name -cnotmatch '^[a-f0-9]{64}$')) {
            throw 'recovery_object_shape: Invalid file in the declared object data class.'
        }
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))
            if (-not $journal -and $hash -cne $file.Name) { throw 'recovery_object_hash: Object bytes differ from their address.' }
            if ($journal) {
                $result += [ordered]@{ dataClass = 'RawDeletionJournal'; objectKey = '.deletion-receipts/' + $file.Name
                    objectHash = [IO.Path]::GetFileNameWithoutExtension($file.Name); hash = $hash; length = $stream.Length }
            } elseif ($entry.area -ceq 'raw-quarantine') {
                $result += [ordered]@{ dataClass = 'RawQuarantine'; objectKey = 'raw-quarantine/' + $file.Name; hash = $hash; length = $stream.Length }
            } else { $result += [ordered]@{ hash = $hash; length = $stream.Length } }
        } finally { $stream.Dispose() }
    }
    return ,@($result | Sort-Object { Get-RecoveryObjectKey $_ } -CaseSensitive)
}

function Assert-RecoveryCoverage($State, $Objects) {
    $available = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Objects) {
        if (-not $available.Add((Get-RecoveryObjectKey $item))) { throw 'recovery_object_inventory: Duplicate relative object key.' }
    }
    $retention = if ($State.Contains('retention')) { $State.retention } else { @{ contract = 'cohors-retention-cut-1'; rows = @() } }
    if ($retention.contract -cne 'cohors-retention-cut-1') { throw 'recovery_retention_contract: Unknown retention cut.' }
    foreach ($raw in $retention.rows) {
        if ($raw.objectHash -cnotmatch '^[a-f0-9]{64}$' -or $raw.objectKey -cne ('raw-quarantine/' + $raw.objectHash)) {
            throw 'recovery_retention_contract: Invalid raw address.'
        }
        $journalKey = '.deletion-receipts/' + $raw.objectHash + '.json'
        if ($raw.status -ceq 'Deleted') {
            if ($State.references -ccontains $raw.objectHash -or $State.references -ccontains $raw.objectKey) {
                throw 'recovery_retention_reference: A deletion receipt cannot waive a retained regulatory, independent or historical binding.'
            }
            if ($available.Contains($raw.objectKey) -or $available.Contains($raw.objectHash)) {
                throw 'recovery_retention_resurrection: Deleted raw bytes reappeared in the object cut.'
            }
            $journal = @($Objects | Where-Object { (Get-RecoveryObjectKey $_) -ceq $journalKey })
            if ($journal.Count -ne 1 -or $journal[0].hash -cne $raw.deletionReceiptHash) {
                throw 'recovery_retention_receipt: The exact canonical physical deletion receipt is missing or differs.'
            }
        } elseif ($raw.status -cnotin @('Registered', 'Held', 'Authorized') -or $available.Contains($journalKey)) {
            throw 'recovery_retention_incomplete: In-flight physical deletion is not a consistent backup cut.'
        } elseif (-not $available.Contains($raw.objectKey)) {
            throw 'recovery_reference_coverage: Active raw bytes are missing.'
        }
    }
    foreach ($item in $Objects | Where-Object { $_.Contains('dataClass') }) {
        $hash = if ($item.dataClass -ceq 'RawDeletionJournal') { $item.objectHash } else { $item.hash }
        if (@($retention.rows | Where-Object { $_.objectHash -ceq $hash }).Count -ne 1) {
            throw 'recovery_retention_untracked: Raw bytes or journal lack a unique authoritative retention record.'
        }
    }
    foreach ($hash in $State.references) {
        if (-not $available.Contains($hash)) { throw 'recovery_reference_coverage: SQL references bytes absent from this object cut.' }
    }
}

function Assert-RecoveryRetentionCheckpoint($Manifest, [string]$Path, [string]$Sha256, [bool]$ConfirmedCurrent) {
    $rows = if ($Manifest.Contains('retention')) { @($Manifest.retention.rows) } else { @() }
    if ($rows.Count -eq 0) { return @{ required = $false; automaticFreshnessProof = $false } }
    if (-not $Path -or $Sha256 -cnotmatch '^[a-f0-9]{64}$' -or -not $ConfirmedCurrent) {
        throw 'recovery_retention_checkpoint: Explicit current retention manifest, independent digest and operator freshness confirmation are required.'
    }
    $path = Assert-RecoveryPath $Path
    $lock = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $Sha256) {
            throw 'recovery_retention_checkpoint: Current retention manifest differs from its independent digest.'
        }
        $current = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
        if ($current.format -cne 'cohors-local-recovery-2' -or $current.syntheticOnly -ne $true -or
            $current.schemaVersion -ne $Manifest.schemaVersion -or $current.sourceDatabase -cne $Manifest.sourceDatabase -or
            -not $current.Contains('retention')) {
            throw 'recovery_retention_checkpoint: The current catalogue does not bind this source/profile.'
        }
        Assert-RecoveryCoverage $current $current.objects
        if ((Get-RecoveryHash $current.retention) -cne (Get-RecoveryHash $Manifest.retention)) {
            throw 'recovery_retention_stale_cut: Raw retention changed after this backup. Use a newer consistent cut; no automatic resurrection or SQL overlay.'
        }
    } finally { $lock.Dispose() }
    return @{ required = $true; manifestSha256 = $Sha256; operatorConfirmedCurrent = $true; automaticFreshnessProof = $false }
}

function Copy-RecoveryObjects([string]$Source, [string]$Destination, $Objects) {
    foreach ($item in $Objects) {
        $sourcePath = Get-RecoveryObjectPath $Source $item
        $destinationPath = Get-RecoveryObjectPath $Destination $item
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
        $inputStream = [IO.File]::Open($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $outputStream = [IO.File]::Open($destinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) }
            finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
    }
}

function Remove-RecoveryObjects([string]$Directory, $Objects) {
    $directory = Assert-RecoveryPath $Directory
    foreach ($item in $Objects) { [IO.File]::Delete((Get-RecoveryObjectPath $directory $item)) }
    foreach ($area in @('raw-quarantine', '.deletion-receipts')) {
        $child = Join-Path $directory $area
        if ([IO.Directory]::Exists($child)) { [IO.Directory]::Delete($child, $false) }
    }
    if (Test-Path -LiteralPath $directory) { [IO.Directory]::Delete($directory, $false) }
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $Quiesced) { throw 'recovery_quiescence: Stop writers and explicitly acknowledge -Quiesced.' }
Initialize-RecoveryRuntime $ApplicationDirectory
$config = Read-RecoveryConfiguration $ConfigurationPath
$backup = Assert-RecoveryPath $BackupDirectory -New
Assert-RecoverySeparatePaths $backup $config.objectRoot
if ($TimingPath) {
    $TimingPath = Assert-RecoveryPath $TimingPath -New
    Assert-RecoverySeparatePaths $TimingPath $config.objectRoot
    Assert-RecoverySeparatePaths $TimingPath $backup
    Initialize-RecoveryTiming $TimingPath
    Write-RecoveryPhase 'configuration-ready'
}
$connection = Open-RecoverySql $config $config.database
$native = '/var/opt/mssql/data/cohors_recovery_' + [Guid]::NewGuid().ToString('N') + '.bak'
$nativeCreated = $false
try {
    if ((Invoke-RecoverySql $connection 'SELECT is_read_only FROM sys.databases WHERE database_id=DB_ID()' -Scalar) -ne $true) {
        throw 'recovery_quiescence: The source database must already be READ_ONLY; this script never stops or changes source writers.'
    }
    Write-RecoveryPhase 'before-state-start'
    $before = Get-RecoveryState $connection $config
    Write-RecoveryPhase 'before-state-complete'
    $objects = Get-RecoveryObjects $config.objectRoot
    Assert-RecoveryCoverage $before $objects
    New-Item -ItemType Directory -Path $backup | Out-Null
    $objectCopy = Join-Path $backup 'objects'
    New-Item -ItemType Directory -Path $objectCopy | Out-Null
    Write-RecoveryPhase 'native-backup-start'
    $nativeCreated = $true
    Invoke-RecoverySql $connection "BACKUP DATABASE [$($config.database)] TO DISK=N'$native' WITH COPY_ONLY,CHECKSUM"
    Invoke-RecoverySql $connection "RESTORE VERIFYONLY FROM DISK=N'$native' WITH CHECKSUM"
    Write-RecoveryPhase 'native-backup-verified'
    Invoke-RecoveryDocker @('cp', "$($config.containerId):$native", (Join-Path $backup 'database.bak')) | Out-Null
    Write-RecoveryPhase 'native-backup-copied'
    Copy-RecoveryObjects $config.objectRoot $objectCopy $objects
    Write-RecoveryPhase 'objects-copied' $objects.Count
    $after = Get-RecoveryState $connection $config
    Write-RecoveryPhase 'after-state-complete'
    if ((Invoke-RecoverySql $connection 'SELECT is_read_only FROM sys.databases WHERE database_id=DB_ID()' -Scalar) -ne $true -or
        (Get-RecoveryHash $before) -cne (Get-RecoveryHash $after) -or
        (Get-RecoveryHash $objects) -cne (Get-RecoveryHash (Get-RecoveryObjects $config.objectRoot)) -or
        (Get-RecoveryHash $objects) -cne (Get-RecoveryHash (Get-RecoveryObjects $objectCopy))) {
        throw 'recovery_cut_changed: Source SQL or object inventory changed; the partial backup is not complete.'
    }
    Invoke-RecoveryDocker @('exec', $config.containerId, 'rm', '--', $native) | Out-Null
    $nativeCreated = $false
    $manifest = [ordered]@{
        format = 'cohors-local-recovery-2'; schemaVersion = [Cohors.Infrastructure.SqlStateStore]::SchemaVersion
        syntheticOnly = $true; capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
        sourceDatabase = $config.database; databaseSha256 = (Get-FileHash -LiteralPath (Join-Path $backup 'database.bak')).Hash.ToLowerInvariant()
        tables = $before.tables; references = $before.references; objects = $objects; retention = $before.retention
        cut = 'Explicitly quiesced, READ_ONLY SQL; verified object inventory before and after copy. No distributed transaction.'
        search = 'Rebuild KnowledgeSearch without SearchClient; SQL-derived local projection, not Azure AI Search.'
        externalLedgerWitness = $false
    }
    $manifestPath = Join-Path $backup 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 30), [Text.UTF8Encoding]::new($false))
    Write-RecoveryPhase 'manifest-written'
    Write-Output ('RECOVERY_MANIFEST_SHA256=' + (Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant())
} finally {
    $connection.Dispose()
    if ($nativeCreated) { Invoke-RecoveryDocker @('exec', $config.containerId, 'rm', '-f', '--', $native) | Out-Null }
}
