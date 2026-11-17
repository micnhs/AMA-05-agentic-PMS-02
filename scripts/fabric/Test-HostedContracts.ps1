$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    $env:GITHUB_REF -ne 'refs/heads/micnhs-cohors-fabric-umsetzung') {
    throw 'Fabric validation requires its allocated standard GitHub-hosted branch runner. No local fallback.'
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$sha = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sha -cne $env:GITHUB_SHA) { throw 'Validation source differs from the event commit.' }
$delivery = Get-Content -LiteralPath 'fabric/delivery.json' -Raw | ConvertFrom-Json
if ($delivery.deliveryId -ne 'cohors-fabric') { throw 'Wrong delivery identity.' }
foreach ($name in @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'npm_config_cache')) {
    $path = [Environment]::GetEnvironmentVariable($name)
    if (-not $path -or -not [IO.Path]::GetFullPath($path).StartsWith(
        [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::Ordinal)) { throw "Non-isolated validation directory: $name" }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}
$temporary = Join-Path $env:RUNNER_TEMP "cohors-fabric-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$env:TMPDIR = Join-Path $temporary 'tmp'
New-Item -ItemType Directory -Force -Path $env:TMPDIR | Out-Null
if ((dotnet --version).Trim() -ne '10.0.404') { throw 'The exact approved .NET SDK is required.' }
if ((node --version).Trim() -ne 'v24.10.0') { throw 'The exact approved Node version is required.' }

python3 scripts/fabric/baseline.py
if ($LASTEXITCODE -ne 0) { throw 'Bound baseline validation failed.' }
python3 scripts/fabric/capabilities.py
if ($LASTEXITCODE -ne 0) { throw 'Capability evidence-level validation failed.' }
python3 -m unittest discover -s scripts/fabric -p 'test_*.py' -v
if ($LASTEXITCODE -ne 0) { throw 'Fabric source contract tests failed.' }

$web = Join-Path $root 'src/Cohors.Web'
$webLog = Join-Path $temporary 'web-build.log'
npm --prefix $web run build *> $webLog
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath (Join-Path $web 'node_modules')) {
        Get-Content -LiteralPath $webLog -Tail 60
        throw 'Frontend build failed with existing dependencies.'
    }
    npm --prefix $web ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Locked frontend dependency restore failed.' }
    npm --prefix $web run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
}

$buildLog = Join-Path $temporary 'dotnet-build.log'
dotnet build Cohors.slnx --configuration Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal *> $buildLog
if ($LASTEXITCODE -ne 0) {
    if (-not (Select-String -LiteralPath $buildLog -SimpleMatch 'NETSDK1004' -Quiet)) {
        Get-Content -LiteralPath $buildLog -Tail 80
        throw 'Build failed for a reason other than absent restore assets.'
    }
    dotnet restore Cohors.slnx --locked-mode --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed; package versions must not change silently.' }
    dotnet build Cohors.slnx --configuration Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
}

$filter = 'FullyQualifiedName~Cohors.Tests.IndependentReferencesTests|FullyQualifiedName~Cohors.Tests.CaseProcessTests|FullyQualifiedName~Cohors.Tests.FabricCommitContractTests'
$results = Join-Path $temporary 'test-results'
dotnet test tests/Cohors.Tests/Cohors.Tests.csproj --configuration Release --no-build --no-restore --filter $filter --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=contracts.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw 'Selected in-process business/commit contract tests failed.' }
[xml]$trx = Get-Content -LiteralPath (Join-Path $results 'contracts.trx') -Raw
$testSummary = @()
foreach ($class in @('IndependentReferencesTests', 'CaseProcessTests', 'FabricCommitContractTests')) {
    $ids = @($trx.TestRun.TestDefinitions.UnitTest | Where-Object { $_.TestMethod.className -eq "Cohors.Tests.$class" } | ForEach-Object { $_.id })
    $executed = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testId -in $ids })
    if ($executed.Count -eq 0 -or @($executed | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) {
        throw "The selected contract class has missing or non-passing execution evidence: $class"
    }
    $testSummary += "- ${class}: $($executed.Count) passed; 0 skipped/failed."
}

$summary = @(
    '# Cohors Fabric: build and local contracts'
    "- Source: $sha"
    '- Standard isolated hosted runner; no SQL, Docker, browser, Azure login, secrets, shared caches or artifact uploads.'
    '- Evidence level: local contract execution only. NOT Fabric/IQ/Hosted Agents/WORM/Ledger/recovery or production acceptance.'
)
$summary += $testSummary
foreach ($path in @('fabric/delivery.json', 'docs/fabric/requirement-register.json', 'src/Cohors.Domain/bin/Release/net10.0/Cohors.Domain.dll')) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $summary += "- SHA256 ${path}: $hash"
}
$summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
$summary | Write-Output
