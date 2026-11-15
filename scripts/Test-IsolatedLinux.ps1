param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [int]$SqlPort = 14348,
    [int]$WebPort = 5198
)
$ErrorActionPreference = 'Stop'
if (-not $IsLinux -or $env:GITHUB_ACTIONS -eq 'true') {
    throw 'Local Linux acceptance only. Use a fresh isolated source copy, never the Actions harness or a developer database.'
}
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (Test-Path -LiteralPath (Join-Path $root '.local')) { throw 'A fresh isolated source copy without .local is required.' }
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) { throw 'Use a new artifact directory; existing evidence will not be overwritten.' }
if ($ArtifactDirectory.StartsWith($root.TrimEnd('/') + '/', [StringComparison]::Ordinal) -or $ArtifactDirectory -eq $root) {
    throw 'Artifacts must be outside the disposable source copy, not beside its private credentials and raw logs.'
}
if ($SqlPort -eq $WebPort -or $SqlPort -lt 1024 -or $WebPort -lt 1024 -or $SqlPort -gt 65535 -or $WebPort -gt 65535) {
    throw 'Two distinct unprivileged loopback ports are required.'
}
$containerName = 'cohors-sql-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($root))).Substring(0, 8).ToLowerInvariant()
$volumeName = "$containerName-data"
$existing = @(& docker container ls --all --filter "name=^/$containerName$" --format '{{.ID}}')
if ($LASTEXITCODE -ne 0 -or $existing.Count) { throw 'Docker unavailable or the intended SQL container already exists; nothing will be reused or deleted.' }
$existing = @(& docker volume ls --filter "name=^$volumeName$" --format '{{.Name}}')
if ($LASTEXITCODE -ne 0 -or $existing.Count) { throw 'The intended SQL volume must not already exist.' }
$private = Join-Path $root '.local/private-validation'
New-Item -ItemType Directory -Path $private -Force | Out-Null
$settingsPath = Join-Path $root '.local/settings.json'
$accessPath = Join-Path $root '.local/demo-access.txt'
$secrets = [System.Collections.Generic.List[string]]::new()
$api = $null
$containerId = $null
$completed = $false

function Invoke-Step([string]$Name, [string]$Command, [string[]]$Arguments) {
    $log = Join-Path $private "$Name.log"
    & $Command @Arguments *> $log
    $result = $LASTEXITCODE
    if ($result -ne 0) {
        if ($secrets.Count) {
            $text = Get-Content -LiteralPath $log -Raw
            foreach ($secret in $secrets) { $text = $text.Replace($secret, '[REDACTED]') }
            if ($text -notmatch '(?i)(?:Authorization|Set-Cookie|Cookie|X-CSRF-Token)\s*[:=]\s*\S|(?:access_token|refresh_token|id_token)(?:&quot;|")?\s*:') {
                $text -split "`n" | Select-Object -Last 80 | Write-Output
            }
        }
        throw "$Name failed (exit $result); unfiltered logs are private and are not published."
    }
    Write-Output "${Name}: passed"
}

try {
    $initLog = Join-Path $private 'initialize.log'
    & pwsh -NoProfile -File (Join-Path $root 'scripts/Initialize-Local.ps1') -SqlPort $SqlPort -WebPort $WebPort *> $initLog
    $initExit = $LASTEXITCODE
    $idLines = @(Get-Content -LiteralPath $initLog | Where-Object { $_ -match '^COHORS_SQL_CONTAINER_ID=[a-f0-9]{64}$' })
    if ($idLines.Count -eq 1) { $containerId = $idLines[0].Split('=')[1] }
    if ($initExit -ne 0 -or -not $containerId) { throw 'Fresh SQL initialization failed; no raw credential-bearing output is published.' }
    $identity = & docker container inspect --format '{{.Name}}|{{index .Config.Labels "app"}}|{{range .Mounts}}{{if eq .Destination "/var/opt/mssql"}}{{.Name}}{{end}}{{end}}' $containerId
    if ($LASTEXITCODE -ne 0 -or $identity -ne "/$containerName|cohors-pilot|$volumeName") {
        throw 'The exact initialized SQL container does not match its isolated name, label and volume.'
    }
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($settings.Local.ContainerName -ne $containerName) { throw 'SQL ownership contract changed.' }
    $settings.Cohors.PublicSources = $false
    $settings | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $settingsPath -Encoding utf8NoBOM
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection.set_ConnectionString($settings.ConnectionStrings.Cohors)
    $sqlPassword = $null
    if (-not $connection.TryGetValue('Password', [ref]$sqlPassword) -or [string]::IsNullOrWhiteSpace($sqlPassword)) {
        throw 'The generated SQL password could not be inventoried; no logs or artifacts can be released.'
    }
    $secrets.Add([string]$sqlPassword)
    $secrets.Add($settings.Authentication.WorkloadKey)
    foreach ($user in $settings.Authentication.Users) { $secrets.Add($user.Salt); $secrets.Add($user.Hash) }
    $passwords = 0
    foreach ($line in Get-Content -LiteralPath $accessPath) {
        if ($line -match '\.demo : (.+)$') { $secrets.Add($Matches[1]); $passwords++ }
    }
    if ($passwords -ne @($settings.Authentication.Users).Count -or $passwords -eq 0 -or
        @($secrets | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count) {
        throw 'Credential inventory incomplete; no logs or artifacts can be released.'
    }
    $env:COHORS_TEST_CONNECTION = $settings.ConnectionStrings.Cohors
    $env:Cohors__PublicSources = 'false'
    $env:COHORS_URL = "http://localhost:$WebPort"
    $env:COHORS_BROWSER = 'chromium'
    Invoke-Step 'build' 'pwsh' @('-NoProfile', '-File', (Join-Path $root 'scripts/Build-Local.ps1'))
    Invoke-Step 'sql-tests' 'pwsh' @('-NoProfile', '-File', (Join-Path $root 'scripts/Test-Local.ps1'), '-SkipBrowser')
    $trx = Join-Path $root '.local/test-results/acceptance.trx'
    & (Join-Path $root 'infra/scripts/Test-CohorsResults.ps1') -TrxPath $trx
    $api = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile', '-File',
        (Join-Path $root 'scripts/Start-Local.ps1'), '-Port', "$WebPort") -PassThru `
        -RedirectStandardOutput (Join-Path $private 'api.stdout.log') -RedirectStandardError (Join-Path $private 'api.stderr.log')
    $ready = $false
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    try {
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $api.Refresh()
            if ($api.HasExited) { throw 'Local API exited before SQL readiness; host logs remain private.' }
            try {
                $response = $client.GetAsync("$env:COHORS_URL/health/ready").GetAwaiter().GetResult()
                try {
                    if ($response.IsSuccessStatusCode -and
                        ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).status -eq 'ready') {
                        $ready = $true; break
                    }
                } finally { $response.Dispose() }
            } catch [System.Net.Http.HttpRequestException] {
                Write-Output "Waiting for local API readiness ($($attempt + 1)/60)."
            } catch [System.Threading.Tasks.TaskCanceledException] {
                Write-Output "Local API readiness timed out ($($attempt + 1)/60)."
            }
            Start-Sleep -Seconds 2
        }
    } finally { $client.Dispose() }
    if (-not $ready) { throw 'No SQL readiness; browser acceptance was not run.' }
    Invoke-Step 'chromium-tests' 'npm' @('--prefix', (Join-Path $root 'src/Cohors.Web'), 'run', 'test:e2e')
    $junit = Join-Path $root '.local/e2e-results.xml'
    & (Join-Path $root 'infra/scripts/Test-CohorsResults.ps1') -TrxPath $trx -JUnitPath $junit
    Invoke-Step 'flex-packages' 'pwsh' @('-NoProfile', '-File', (Join-Path $root 'scripts/Publish-Functions.ps1'))
    $images = @()
    foreach ($hostName in @('Api', 'AgentHost')) {
        $tag = "cohors-local-$($hostName.ToLowerInvariant()):$containerName"
        Invoke-Step "$($hostName.ToLowerInvariant())-image" 'docker' @('build', '--quiet', '--file',
            (Join-Path $root "src/Cohors.$hostName/Dockerfile"), '--tag', $tag, $root)
        $imageId = & docker image inspect --format '{{.Id}}' $tag
        if ($LASTEXITCODE -ne 0 -or $imageId -notmatch '^sha256:[a-f0-9]{64}$') { throw "$hostName image identity missing." }
        $images += @{ host = $hostName; tag = $tag; imageId = $imageId }
    }
    $packages = @()
    foreach ($hostName in @('Workflows', 'Operations')) {
        $zip = Join-Path $root ".local/publish/cohors-$($hostName.ToLowerInvariant()).zip"
        $metadata = Get-Content -LiteralPath (Join-Path $root ".local/publish/$hostName/functions.metadata") -Raw | ConvertFrom-Json
        $packages += @{ host = $hostName; sha256 = (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
            functions = @($metadata.name); validation = 'Code package, required files and discovered trigger metadata. Not host execution or OneDeploy.' }
    }
    $sourceManifest = Join-Path $root '.source-manifest.json'
    $buildEvidence = @{
        scope = 'Local isolated Linux acceptance'; githubActions = $false; azureDeployment = $false
        dotnet = (& dotnet --version); node = (& node --version); powershell = $PSVersionTable.PSVersion.ToString()
        images = $images; flexPackages = $packages; capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
        sourceManifestSha256 = if (Test-Path -LiteralPath $sourceManifest) { (Get-FileHash -LiteralPath $sourceManifest).Hash.ToLowerInvariant() } else { $null }
    }
    foreach ($report in @($trx, $junit)) {
        $text = Get-Content -LiteralPath $report -Raw
        if ($text -match '(?i)(?:Authorization|Set-Cookie|Cookie|X-CSRF-Token)\s*[:=]\s*\S|(?:access_token|refresh_token|id_token)(?:&quot;|")?\s*:') {
            throw 'Authentication material detected; artifact publication blocked.'
        }
        foreach ($secret in $secrets) { if ($text.Contains($secret)) { throw 'Credential detected; artifact publication blocked.' } }
    }
    New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
    foreach ($file in @($trx, $junit, (Join-Path $root '.local/cohors-cockpit.png'), (Join-Path $root '.local/cohors-workbench.png'))) {
        Copy-Item -LiteralPath $file -Destination $ArtifactDirectory
    }
    $buildEvidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'local-linux-builds.json') -Encoding utf8NoBOM
    if (Test-Path -LiteralPath $sourceManifest) { Copy-Item -LiteralPath $sourceManifest -Destination (Join-Path $ArtifactDirectory 'source-manifest.json') }
    $completed = $true
} finally {
    if ($api) {
        $api.Refresh()
        if (-not $api.HasExited) { $api.Kill($true); $api.WaitForExit() }
        $api.Dispose()
    }
    $cleaned = $false
    if ($containerId) {
        $identity = & docker container inspect --format '{{.Name}}|{{index .Config.Labels "app"}}|{{range .Mounts}}{{if eq .Destination "/var/opt/mssql"}}{{.Name}}{{end}}{{end}}' $containerId
        if ($LASTEXITCODE -ne 0 -or $identity -ne "/$containerName|cohors-pilot|$volumeName") {
            throw 'SQL ownership cannot be verified for cleanup. Private recovery files were retained.'
        }
        & docker container rm --force $containerId | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned SQL container cleanup failed.' }
        & docker volume rm $volumeName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned SQL volume cleanup failed.' }
        $cleaned = $true
    }
    if ($cleaned) {
        foreach ($file in @($settingsPath, $accessPath)) {
            if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force }
        }
    }
    Remove-Item Env:\COHORS_TEST_CONNECTION -ErrorAction SilentlyContinue
}
if ($completed) { Write-Output 'LOCAL LINUX acceptance passed. Not GitHub Actions, Azure, live Foundry or clinical acceptance.' }
