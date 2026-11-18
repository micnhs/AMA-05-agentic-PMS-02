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

$classes = @('IndependentReferencesTests', 'CaseProcessTests', 'FabricCommitContractTests', 'DispatchPreviewTests', 'PsurMonthlyTests', 'FabricC1PreparationTests', 'FabricC1TransportTests', 'FabricC1DeploymentTests')
$filter = ($classes | ForEach-Object { "FullyQualifiedName~Cohors.Tests.$_" }) -join '|'
$results = Join-Path $temporary 'test-results'
dotnet test tests/Cohors.Tests/Cohors.Tests.csproj --configuration Release --no-build --no-restore --filter $filter --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=contracts.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw 'Selected in-process business/commit contract tests failed.' }
$preflightJson = dotnet src/Cohors.Api/bin/Release/net10.0/Cohors.Api.dll --fabric-c1 check --preparation fabric/c1/inputs.template.json
if ($LASTEXITCODE -ne 2) { throw 'The executable C1 entry must reject the unbound public template without starting the API or acquiring credentials.' }
$preflight = $preflightJson | ConvertFrom-Json
if ($preflight.status -cne 'Blocked' -or $preflight.cloudExecutionAllowed -ne $false -or
    'identity.tenantId' -notin $preflight.missingOrInvalid -or 'target.workspaceId' -notin $preflight.missingOrInvalid) {
    throw 'The C1 executable did not return the expected offline missing-bindings result.'
}
$preflightJson | Write-Output
$deploymentJson = dotnet src/Cohors.Api/bin/Release/net10.0/Cohors.Api.dll --fabric-c1-deploy plan --manifest fabric/c1/deployment.template.json
if ($LASTEXITCODE -ne 2) { throw 'The incomplete deployment must remain blocked without acquiring credentials or creating resources.' }
$deploymentPlan = $deploymentJson | ConvertFrom-Json
if ($deploymentPlan.status -cne 'Blocked' -or $deploymentPlan.canApply -ne $false -or
    'tenantId' -notin $deploymentPlan.missingInputs -or $deploymentPlan.implementationBlockers.Count -eq 0) {
    throw 'The deployment entry hid missing inputs or its incomplete bootstrap.'
}
$deploymentJson | Write-Output
$applyLog = Join-Path $temporary 'identity-apply-refusal.log'
dotnet src/Cohors.Api/bin/Release/net10.0/Cohors.Api.dll --fabric-c1-deploy apply --manifest fabric/c1/deployment.template.json --journal (Join-Path $temporary 'not-created.json') --through identities 2> $applyLog
if ($LASTEXITCODE -ne 2) { throw 'Identity apply must be refused on the credential-free build runner.' }
$applyRefusal = Get-Content -LiteralPath $applyLog -Raw | ConvertFrom-Json
if ($applyRefusal.code -cne 'c1_deploy_ci_prohibited' -or
    (Test-Path -LiteralPath (Join-Path $temporary 'not-created.json'))) {
    throw 'The identity entry did not refuse before credential acquisition and journal creation.'
}
[xml]$trx = Get-Content -LiteralPath (Join-Path $results 'contracts.trx') -Raw
$testSummary = @()
foreach ($class in $classes) {
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
    '- C1 executable unbound-template preflight: Blocked, exit 2, cloudExecutionAllowed=false (expected offline rejection).'
    '- C1 deployment entry: Blocked, exit 2, canApply=false; incomplete private bootstrap is not a deployment pass.'
    '- C1 identity apply is refused on this build runner before credentials, journal creation or cloud access.'
)
$summary += $testSummary
foreach ($path in @('fabric/delivery.json', 'docs/fabric/requirement-register.json', 'src/Cohors.Domain/bin/Release/net10.0/Cohors.Domain.dll')) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $summary += "- SHA256 ${path}: $hash"
}
$summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
$summary | Write-Output
exit 0
