param([Parameter(Mandatory)][string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $IsLinux) {
    throw 'This integration harness is for an ephemeral GitHub Actions Linux runner, not a developer machine.'
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$settingsPath = Join-Path $root '.local/settings.json'
if (Test-Path -LiteralPath $settingsPath) { throw 'CI requires fresh configuration; existing settings must never be read or reused.' }
foreach ($path in @('global.json', 'Cohors.slnx', 'scripts/Initialize-Local.ps1', 'scripts/Build-Local.ps1',
        'scripts/Test-Local.ps1', 'scripts/Test-Ui.ps1', 'src/Cohors.Web/package-lock.json',
        'src/Cohors.Bootstrap/Cohors.Bootstrap.csproj', 'tests/Cohors.Bootstrap.Tests/Cohors.Bootstrap.Tests.csproj')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $path))) { throw "Required application contract is missing: $path" }
}

function Assert-Exit([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE." }
}

function Get-CiSqlPassword {
    param([object]$ConnectionString)
    if ($ConnectionString -isnot [string] -or [string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw 'Fresh SQL connection string is missing or invalid; credential publication remains blocked.'
    }
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        # PowerShell's IDictionary adapter shadows the property; invoke the CLR setter.
        $connection.set_ConnectionString($ConnectionString)
    } catch [ArgumentException] {
        throw 'Fresh SQL connection string is malformed; credential publication remains blocked.'
    }
    $password = $null
    if (-not $connection.TryGetValue('Password', [ref]$password) -or
        $password -isnot [string] -or [string]::IsNullOrWhiteSpace($password) -or
        $password -match '[\x00-\x1F\x7F]') {
        throw 'Fresh SQL password is missing or invalid; credential publication remains blocked.'
    }
    return $password
}

function Get-CiBrowserProfiles {
    return [ordered]@{
        Preview = @{ project = 'preview'; tests = 11 }
        AuditExport = @{ project = 'audit-export'; tests = 1 }
        SummaryReport = @{ project = 'summary-report'; tests = 1 }
        CaseDocuments = @{ project = 'case-documents'; tests = 2 }
        ReportPrefill = @{ project = 'report-prefill'; tests = 1 }
        InformationLifecycle = @{ project = 'information-lifecycle'; tests = 2 }
        EudamedConfiguration = @{ project = 'eudamed-configuration'; tests = 2 }
        EudamedDossiers = @{ project = 'eudamed-dossiers'; tests = 3 }
        CockpitCalendar = @{ project = 'cockpit-calendar'; tests = 4 }
        SignalReview = @{ project = 'signal-review'; tests = 4 }
        PhysicalRetention = @{ project = 'physical-retention'; tests = 1 }
    }
}

function Get-CiBrowserEvidencePaths([string]$BrowserRoot) {
    $paths = [ordered]@{}
    foreach ($profile in (Get-CiBrowserProfiles).GetEnumerator()) {
        foreach ($name in @('e2e-results.xml', 'owned-resources-terminal.json')) {
            $paths["browser-originals/$($profile.Value.project)/$name"] =
                Join-Path (Join-Path $BrowserRoot $profile.Key) $name
        }
    }
    return $paths
}

function Assert-CiReportCredentials([byte[]]$Bytes, [Collections.Generic.List[string]]$ProhibitedValues) {
    try { $content = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes) }
    catch [Text.DecoderFallbackException] { throw 'A test report is not UTF-8; artifact publication is blocked.' }
    if ($content -match '(?i)(?:Authorization|Set-Cookie|Cookie|X-CSRF-Token)\s*[:=]\s*\S|(?:access_token|refresh_token|id_token)(?:&quot;|")?\s*:') {
        throw 'A test report contains authentication material; artifact publication is blocked.'
    }
    foreach ($value in $ProhibitedValues) {
        if ($content.Contains($value)) { throw 'A test report contains a generated credential; artifact publication is blocked.' }
    }
}

function Assert-CiBrowserCleanup([Collections.IDictionary]$Terminal) {
    foreach ($name in @('ownApiStopped', 'ownDatabaseRemoved', 'settingsRemoved', 'accessFileRemoved',
            'objectDirectoryRemoved', 'runtimeParentRemoved', 'runtimeCreatedByRunner')) {
        if ($Terminal[$name] -isnot [bool] -or -not $Terminal[$name]) {
            throw "Owned browser cleanup is not proven ($name); no later profile or parent SQL deletion is allowed."
        }
    }
    if (($Terminal['cleanupErrorCount'] -isnot [long] -and $Terminal['cleanupErrorCount'] -isnot [int]) -or
        $Terminal['cleanupErrorCount'] -ne 0 -or $Terminal['intentSha256'] -cnotmatch '^[0-9a-f]{64}$' -or
        $Terminal['resourceJournalSha256'] -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Owned browser cleanup lacks its error-free, journal-bound terminal observation.'
    }
}

function Add-CiBrowserReport([xml]$Result, [xml]$Report, [string]$Project, [int]$ExpectedTests,
    [Collections.IDictionary]$Properties) {
    $suites = @($Report.SelectNodes('/testsuites/testsuite'))
    $cases = @($Report.SelectNodes('/testsuites/testsuite/testcase'))
    if ($Report.DocumentElement.get_Name() -cne 'testsuites' -or -not $suites.Count -or
        $cases.Count -ne $ExpectedTests -or $Report.SelectNodes('//testcase').Count -ne $cases.Count -or
        @($suites | Where-Object { $_.GetAttribute('hostname') -cne $Project }).Count -or
        @($Result.SelectNodes('/testsuites/testsuite') | Where-Object { $_.GetAttribute('hostname') -ceq $Project }).Count) {
        throw "Browser JUnit must contain the complete, non-duplicate selected project: $Project."
    }
    $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($case in $cases) {
        if (-not $case.GetAttribute('name') -or
            -not $identities.Add($case.GetAttribute('classname') + "`n" + $case.GetAttribute('name'))) {
            throw "Browser JUnit has missing or duplicate case identities: $Project."
        }
    }
    foreach ($section in @($Report.DocumentElement) + $suites) {
        $children = if ($section -eq $Report.DocumentElement) { $cases } else { @($section.SelectNodes('testcase')) }
        $observed = @{
            tests = @($children).Count
            failures = @($children | Where-Object { $_.SelectSingleNode('failure') }).Count
            errors = @($children | Where-Object { $_.SelectSingleNode('error') }).Count
            skipped = @($children | Where-Object { $_.SelectSingleNode('skipped') }).Count
        }
        foreach ($name in $observed.Keys) {
            $value = $section.GetAttribute($name)
            if ($value -cnotmatch '^(0|[1-9][0-9]*)$' -or [int]$value -ne $observed[$name]) {
                throw "Browser JUnit counters disagree with actual case outcomes: $Project."
            }
        }
    }
    $seconds = [Xml.XmlConvert]::ToDouble($Report.DocumentElement.GetAttribute('time'))
    if (-not [double]::IsFinite($seconds) -or $seconds -lt 0) { throw 'Browser JUnit duration is invalid.' }
    foreach ($suite in $suites) {
        $copy = $Result.ImportNode($suite, $true)
        $metadata = $copy.SelectSingleNode('properties')
        if (-not $metadata) { $metadata = $Result.CreateElement('properties'); $null = $copy.AppendChild($metadata) }
        foreach ($name in $Properties.Keys) {
            $property = $Result.CreateElement('property')
            $property.SetAttribute('name', $name); $property.SetAttribute('value', [string]$Properties[$name])
            $null = $metadata.AppendChild($property)
        }
        $null = $Result.DocumentElement.AppendChild($copy)
    }
    foreach ($name in @('tests', 'failures', 'errors', 'skipped')) {
        $Result.DocumentElement.SetAttribute($name, [string](
            [int]$Result.DocumentElement.GetAttribute($name) + [int]$Report.DocumentElement.GetAttribute($name)))
    }
    $Result.DocumentElement.SetAttribute('time', [Xml.XmlConvert]::ToString(
        [Xml.XmlConvert]::ToDouble($Result.DocumentElement.GetAttribute('time')) + $seconds))
}

$containerName = 'cohors-sql-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($root))).Substring(0, 8).ToLowerInvariant()
$volumeName = "$containerName-data"
$dockerOs = & docker info --format '{{.OSType}}'
Assert-Exit 'Docker availability'
if ($dockerOs -ne 'linux') { throw 'SQL acceptance requires Docker Linux containers.' }
$existingContainers = @(& docker container ls --all --format '{{.Names}}')
Assert-Exit 'Docker container ownership check'
$existingVolumes = @(& docker volume ls --format '{{.Name}}')
Assert-Exit 'Docker volume ownership check'
if ($containerName -in $existingContainers -or $volumeName -in $existingVolumes) {
    throw 'The CI SQL container/volume name already exists; refusing to reuse or delete it.'
}

$initialized = $false
$credentialsCollected = $false
$browserCleanupProven = $true
$sqlCleanupComplete = $false
$executionErrors = [Collections.Generic.List[Exception]]::new()
$cleanupErrors = [Collections.Generic.List[Exception]]::new()
$prohibitedValues = [System.Collections.Generic.List[string]]::new()
$browserRoot = Join-Path $root ('.local/ci-browser-' + [Guid]::NewGuid().ToString('N'))
$application = Join-Path $browserRoot 'application'
$trxPath = Join-Path $root '.local/test-results/acceptance.trx'
$bootstrapTrxPath = Join-Path $root '.local/bootstrap-test-results/bootstrap.trx'
$junitPath = Join-Path $root '.local/e2e-results.xml'
foreach ($path in @($trxPath, $bootstrapTrxPath, $junitPath, (Join-Path $root '.local/cohors-cockpit.png'),
        (Join-Path $root '.local/cohors-workbench.png'))) {
    if (Test-Path -LiteralPath $path) { throw 'CI evidence must be freshly generated; pre-existing reports/screenshots are refused.' }
}

try {
    & npm --prefix (Join-Path $root 'src/Cohors.Web') ci --no-fund
    Assert-Exit 'Frontend locked install'
    $initialized = $true
    & pwsh -NoProfile -File (Join-Path $root 'scripts/Initialize-Local.ps1')
    Assert-Exit 'Fresh loopback SQL initialization'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($settings.Local.ContainerName -ne $containerName) { throw 'SQL initialization changed its ownership contract.' }
    $settings.Cohors.PublicSources = $false
    $settings | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $settingsPath -Encoding utf8NoBOM

    $prohibitedValues.Add((Get-CiSqlPassword -ConnectionString $settings.ConnectionStrings.Cohors))
    $prohibitedValues.Add($settings.Authentication.WorkloadKey)
    foreach ($account in $settings.Authentication.Users) {
        $prohibitedValues.Add($account.Salt)
        $prohibitedValues.Add($account.Hash)
    }
    $demoPasswordCount = 0
    foreach ($line in Get-Content -LiteralPath (Join-Path $root '.local/demo-access.txt')) {
        if ($line -match '\.demo : (.+)$') {
            $prohibitedValues.Add($Matches[1])
            $demoPasswordCount++
        }
    }
    if ($demoPasswordCount -ne @($settings.Authentication.Users).Count -or $demoPasswordCount -eq 0) {
        throw 'Demo credential export format changed; publication must remain blocked until every generated password is collected.'
    }
    foreach ($value in $prohibitedValues) {
        if ([string]::IsNullOrWhiteSpace($value)) { throw 'Fresh test credential collection is incomplete.' }
        Write-Output "::add-mask::$value"
    }
    $credentialsCollected = $true
    $env:COHORS_TEST_CONNECTION = $settings.ConnectionStrings.Cohors
    $env:Cohors__PublicSources = 'false'
    $env:COHORS_BROWSER = 'chromium'
    & pwsh -NoProfile -File (Join-Path $root 'scripts/Build-Local.ps1')
    Assert-Exit 'Locked restore and Web/API/.NET build'
    & pwsh -NoProfile -File (Join-Path $root 'scripts/Test-Local.ps1') -SkipBrowser
    Assert-Exit 'Real SQL/.NET acceptance'
    & (Join-Path $PSScriptRoot 'Test-CohorsResults.ps1') -TrxPath $trxPath
    Write-Output 'Validating Bootstrap.Tests TRX (synthetic SQL/Search fakes).'
    & (Join-Path $PSScriptRoot 'Test-CohorsResults.ps1') -TrxPath $bootstrapTrxPath

    if (Test-Path -LiteralPath $browserRoot) { throw 'The CI browser application and profile paths must be fresh.' }
    & dotnet publish (Join-Path $root 'src/Cohors.Api/Cohors.Api.csproj') --configuration Debug --no-build --no-restore --output $application --verbosity quiet
    Assert-Exit 'Publish the already-built browser-test API'
    Push-Location (Join-Path $root 'src/Cohors.Web')
    try {
        & npx playwright install --with-deps chromium
        Assert-Exit 'Chromium installation'
    } finally { Pop-Location }
    $profiles = Get-CiBrowserProfiles
    [xml]$junit = '<testsuites name="Cohors isolated Hosted CI, derived aggregate: 11 projects / 32 required cases" tests="0" failures="0" skipped="0" errors="0" time="0" />'
    $reportedProfiles = 0
    foreach ($profile in $profiles.Keys) {
        $artifacts = Join-Path $browserRoot $profile
        $browserCleanupProven = $false
        try {
            & (Join-Path $root 'scripts/Test-Ui.ps1') -ApplicationDirectory $application -ArtifactDirectory $artifacts `
                -Profile $profile -HostedCi -CredentialInventory $prohibitedValues
        } catch { $executionErrors.Add($_.Exception) }
        $terminalPath = Join-Path $artifacts 'owned-resources-terminal.json'
        try {
            $terminal = Get-Content -LiteralPath $terminalPath -Raw | ConvertFrom-Json -AsHashtable
            Assert-CiBrowserCleanup $terminal
            $browserCleanupProven = $true
            Write-Output "Browser profile ${profile}: owned API, database and private runtime removal proven; cleanup errors=0."
        } catch { $cleanupErrors.Add($_.Exception) }
        $reportPath = Join-Path $artifacts 'e2e-results.xml'
        try {
            [xml]$report = Get-Content -LiteralPath $reportPath -Raw
            $properties = [ordered]@{
                'cohors.profile' = $profile
                'cohors.originalJUnitSha256' = (Get-FileHash -LiteralPath $reportPath).Hash.ToLowerInvariant()
                'cohors.originalJUnitArtifactPath' = "browser-originals/$($profiles[$profile].project)/e2e-results.xml"
                'cohors.cleanupProven' = $browserCleanupProven.ToString().ToLowerInvariant()
            }
            if (Test-Path -LiteralPath $terminalPath -PathType Leaf) {
                $properties['cohors.ownedResourcesTerminalSha256'] = (Get-FileHash -LiteralPath $terminalPath).Hash.ToLowerInvariant()
                $properties['cohors.ownedResourcesTerminalArtifactPath'] = "browser-originals/$($profiles[$profile].project)/owned-resources-terminal.json"
            }
            Add-CiBrowserReport $junit $report $profiles[$profile].project $profiles[$profile].tests $properties
            $reportedProfiles++
        } catch { $executionErrors.Add($_.Exception) }
        $junit.Save($junitPath)
        if (-not $browserCleanupProven) { break }
    }
    if ($reportedProfiles -ne $profiles.Count -or [int]$junit.DocumentElement.GetAttribute('tests') -ne 32) {
        throw 'Browser acceptance is incomplete: all 11 existing projects and all 32 cases are required.'
    }
    if (-not $executionErrors.Count -and -not $cleanupErrors.Count) {
        & (Join-Path $PSScriptRoot 'Test-CohorsResults.ps1') -TrxPath $trxPath -JUnitPath $junitPath
    }
} catch {
    $executionErrors.Add($_.Exception)
} finally {
    try {
        if ($initialized -and $browserCleanupProven) {
            $ownedContainers = @(& docker container ls --all --format '{{.Names}}')
            Assert-Exit 'Owned SQL cleanup lookup'
            if ($containerName -in $ownedContainers) {
                & docker container rm --force $containerName | Out-Null
                Assert-Exit 'Owned SQL container cleanup'
            }
            $ownedVolumes = @(& docker volume ls --format '{{.Name}}')
            Assert-Exit 'Owned SQL volume lookup'
            if ($volumeName -in $ownedVolumes) {
                & docker volume rm $volumeName | Out-Null
                Assert-Exit 'Owned SQL volume cleanup'
            }
            $sqlCleanupComplete = $true
        } elseif (-not $browserCleanupProven) {
            Write-Warning 'Browser cleanup is unproven; no later profile ran and the owned SQL/runtime/configuration are preserved.'
        }
    } catch { $cleanupErrors.Add($_.Exception) }
    try {
        if ($credentialsCollected) {
            $reports = [ordered]@{ 'acceptance.trx' = $trxPath; 'bootstrap.trx' = $bootstrapTrxPath; 'e2e-results.xml' = $junitPath }
            foreach ($entry in (Get-CiBrowserEvidencePaths $browserRoot).GetEnumerator()) { $reports[$entry.Key] = $entry.Value }
            $reportBytes = [ordered]@{}
            foreach ($name in $reports.Keys) {
                if (Test-Path -LiteralPath $reports[$name] -PathType Leaf) {
                    $bytes = [IO.File]::ReadAllBytes($reports[$name])
                    Assert-CiReportCredentials $bytes $prohibitedValues
                    $reportBytes[$name] = $bytes
                }
            }
            New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
            foreach ($name in $reportBytes.Keys) {
                $destination = Join-Path $ArtifactDirectory $name
                New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
                $stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
                try { $stream.Write($reportBytes[$name]); $stream.Flush($true) } finally { $stream.Dispose() }
            }
            foreach ($name in @('cohors-cockpit.png', 'cohors-workbench.png')) {
                $screenshot = Join-Path (Join-Path $browserRoot 'Preview') $name
                if (Test-Path -LiteralPath $screenshot -PathType Leaf) {
                    Copy-Item -LiteralPath $screenshot -Destination $ArtifactDirectory
                }
            }
        } else {
            Write-Warning 'No artifacts are released because the fresh credential inventory is incomplete.'
        }
    } catch { $cleanupErrors.Add($_.Exception) }
    finally {
        foreach ($file in @($settingsPath, (Join-Path $root '.local/demo-access.txt'))) {
            try {
                if ($initialized -and $sqlCleanupComplete -and (Test-Path -LiteralPath $file -PathType Leaf)) {
                    Remove-Item -LiteralPath $file -Force
                }
            } catch { $cleanupErrors.Add($_.Exception) }
        }
        foreach ($key in @('COHORS_TEST_CONNECTION', 'MSSQL_SA_PASSWORD')) {
            if (Test-Path "Env:$key") { Remove-Item "Env:$key" }
        }
    }
}
if ($executionErrors.Count -or $cleanupErrors.Count) {
    throw [AggregateException]::new('CI execution and cleanup failures are preserved independently.',
        [Exception[]](@($executionErrors.ToArray()) + @($cleanupErrors.ToArray())))
}
