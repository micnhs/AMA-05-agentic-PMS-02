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
        'scripts/Test-Local.ps1', 'scripts/Start-Local.ps1', 'src/Cohors.Web/package-lock.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $path))) { throw "Required application contract is missing: $path" }
}

function Assert-Exit([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE." }
}

function Stop-OwnedProcessTree([int]$ProcessId) {
    $children = @(& ps -o pid= --ppid $ProcessId)
    if ($LASTEXITCODE -notin @(0, 1)) { throw 'Could not enumerate the owned CI process tree.' }
    foreach ($child in $children) { Stop-OwnedProcessTree -ProcessId ([int]$child.Trim()) }
    $process = [System.Diagnostics.Process]::GetProcesses() | Where-Object Id -EQ $ProcessId
    if ($process) { Stop-Process -Id $ProcessId -Force }
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

$api = $null
$initialized = $false
$credentialsCollected = $false
$prohibitedValues = [System.Collections.Generic.List[string]]::new()
$stdout = Join-Path $env:RUNNER_TEMP 'cohors-api.stdout.log'
$stderr = Join-Path $env:RUNNER_TEMP 'cohors-api.stderr.log'
$trxPath = Join-Path $root '.local/test-results/acceptance.trx'
$junitPath = Join-Path $root '.local/e2e-results.xml'
foreach ($path in @($trxPath, $junitPath, (Join-Path $root '.local/cohors-cockpit.png'),
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

    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection.ConnectionString = $settings.ConnectionStrings.Cohors
    $prohibitedValues.Add($connection['Password'])
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
    $env:COHORS_URL = 'http://localhost:5187'
    $env:COHORS_BROWSER = 'chromium'
    & pwsh -NoProfile -File (Join-Path $root 'scripts/Build-Local.ps1')
    Assert-Exit 'Locked restore and Web/API/.NET build'
    & pwsh -NoProfile -File (Join-Path $root 'scripts/Test-Local.ps1') -SkipBrowser
    Assert-Exit 'Real SQL/.NET acceptance'
    & (Join-Path $PSScriptRoot 'Test-CohorsResults.ps1') -TrxPath $trxPath

    $api = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile', '-File', 'scripts/Start-Local.ps1') `
        -WorkingDirectory $root -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $ready = $false
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    try {
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $api.Refresh()
            if ($api.HasExited) { throw 'API startup process exited before readiness. Raw host logs are not uploaded.' }
            try {
                $response = $client.GetAsync("$env:COHORS_URL/health/ready").GetAwaiter().GetResult()
                try {
                    if ($response.IsSuccessStatusCode) {
                        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                        if ($body.status -eq 'ready') { $ready = $true; break }
                    }
                } finally { $response.Dispose() }
            } catch [System.Net.Http.HttpRequestException] {
                Write-Output "Waiting for API readiness ($($attempt + 1)/60)."
            } catch [System.Threading.Tasks.TaskCanceledException] {
                Write-Output "API readiness request timed out ($($attempt + 1)/60)."
            }
            Start-Sleep -Seconds 2
        }
    } finally { $client.Dispose() }
    if (-not $ready) { throw 'API never reported SQL readiness; browser tests were not run.' }

    Push-Location (Join-Path $root 'src/Cohors.Web')
    try {
        & npx playwright install --with-deps chromium
        Assert-Exit 'Chromium installation'
    } finally { Pop-Location }
    & npm --prefix (Join-Path $root 'src/Cohors.Web') run test:e2e
    Assert-Exit 'Browser acceptance'
    & (Join-Path $PSScriptRoot 'Test-CohorsResults.ps1') -TrxPath $trxPath -JUnitPath $junitPath
} finally {
    try {
        if ($api) {
            $api.Refresh()
            if (-not $api.HasExited) { Stop-OwnedProcessTree -ProcessId $api.Id }
            $api.Dispose()
        }
        if ($initialized) {
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
        }
        if ($credentialsCollected) {
            $reports = @($trxPath, $junitPath) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
            foreach ($report in $reports) {
                $content = Get-Content -LiteralPath $report -Raw
                if ($content -match '(?i)(?:Authorization|Set-Cookie|Cookie|X-CSRF-Token)\s*[:=]\s*\S|(?:access_token|refresh_token|id_token)(?:&quot;|")?\s*:') {
                    throw 'A test report contains authentication material; artifact publication is blocked.'
                }
                foreach ($value in $prohibitedValues) {
                    if ($content.Contains($value)) { throw 'A test report contains a generated credential; artifact publication is blocked.' }
                }
            }
            New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
            foreach ($report in $reports) { Copy-Item -LiteralPath $report -Destination $ArtifactDirectory }
            foreach ($name in @('cohors-cockpit.png', 'cohors-workbench.png')) {
                $screenshot = Join-Path $root ".local/$name"
                if (Test-Path -LiteralPath $screenshot -PathType Leaf) {
                    Copy-Item -LiteralPath $screenshot -Destination $ArtifactDirectory
                }
            }
        } else {
            Write-Warning 'No artifacts are released because the fresh credential inventory is incomplete.'
        }
    } finally {
        foreach ($file in @($settingsPath, (Join-Path $root '.local/demo-access.txt'), $stdout, $stderr)) {
            if ($initialized -and (Test-Path -LiteralPath $file -PathType Leaf)) { Remove-Item -LiteralPath $file -Force }
        }
        foreach ($key in @('COHORS_TEST_CONNECTION', 'MSSQL_SA_PASSWORD')) {
            if (Test-Path "Env:$key") { Remove-Item "Env:$key" }
        }
    }
}
