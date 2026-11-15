param(
    [Parameter(Mandatory)][ValidateSet('what-if', 'apply')][string]$Operation,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
    [Parameter(Mandatory)][string]$ReviewId,
    [ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedDiffHash
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_EVENT_NAME -ne 'workflow_dispatch' -or
    $env:COHORS_DEPLOY_ENABLED -ne 'true' -or $env:COHORS_PRIVATE_RUNNER_READY -ne 'true') {
    throw 'Deployment is disabled, not manual, or the ephemeral private runner has not been approved.'
}
if ($Operation -eq 'apply' -and -not $ExpectedDiffHash) { throw 'Apply requires the separately approved what-if digest.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$parameters = Join-Path $root 'infra/parameters/pilot.reviewed.parameters.json'
$approval = Join-Path $root 'infra/parameters/pilot.approval.json'
foreach ($path in @($parameters, $approval)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Reviewed deployment inputs are missing. Examples are not accepted.' }
}
foreach ($key in @('AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID', 'AZURE_CLIENT_ID', 'AZURE_RESOURCE_GROUP',
        'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_URL', 'RUNNER_TEMP')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key))) { throw "Protected deployment input is missing: $key" }
}
$actualSha = & git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualSha -ne $SourceSha) { throw 'Checkout is not the reviewed immutable source revision.' }
$contextPath = Join-Path $env:RUNNER_TEMP 'cohors-gate-context.json'
@{
    sourceSha = $SourceSha
    reviewId = $ReviewId
    tenantId = $env:AZURE_TENANT_ID
    subscriptionId = $env:AZURE_SUBSCRIPTION_ID
    resourceGroup = $env:AZURE_RESOURCE_GROUP
} | ConvertTo-Json | Set-Content -LiteralPath $contextPath -Encoding utf8NoBOM
& python (Join-Path $PSScriptRoot 'deployment_gate.py') --parameters $parameters --approval $approval --context $contextPath
if ($LASTEXITCODE -ne 0) { throw 'Offline deployment input validation refused the request.' }

$env:AZURE_CONFIG_DIR = Join-Path $env:RUNNER_TEMP "cohors-azure-$Operation"
$env:AZURE_CORE_COLLECT_TELEMETRY = 'no'
$template = Join-Path $env:RUNNER_TEMP 'cohors-reviewed-template.json'
& az bicep build --file (Join-Path $root 'infra/main.bicep') --outfile $template
if ($LASTEXITCODE -ne 0) { throw 'Bicep validation failed before Azure authentication.' }

$loggedIn = $false
try {
    $tokenResponse = Invoke-RestMethod -Uri "$env:ACTIONS_ID_TOKEN_REQUEST_URL&audience=api%3A%2F%2FAzureADTokenExchange" `
        -Headers @{ Authorization = "Bearer $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN" } -Method Get
    if ([string]::IsNullOrWhiteSpace($tokenResponse.value)) { throw 'GitHub did not return an OIDC token.' }
    Write-Output "::add-mask::$($tokenResponse.value)"
    & az login --service-principal --username $env:AZURE_CLIENT_ID --tenant $env:AZURE_TENANT_ID `
        --federated-token $tokenResponse.value --output none --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'OIDC login failed; no credentials or alternative identity are attempted.' }
    $loggedIn = $true
    $tokenResponse = $null
    & az account set --subscription $env:AZURE_SUBSCRIPTION_ID
    if ($LASTEXITCODE -ne 0) { throw 'Approved subscription selection failed.' }
    $accountJson = & az account show --output json --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify the deployment account.' }
    $account = $accountJson | ConvertFrom-Json
    if ($account.tenantId -ne $env:AZURE_TENANT_ID -or $account.id -ne $env:AZURE_SUBSCRIPTION_ID -or
        $account.environmentName -ne 'AzureCloud') { throw 'Authenticated Azure context differs from the approved public-cloud context.' }

    $arguments = @('--resource-group', $env:AZURE_RESOURCE_GROUP, '--template-file', $template,
        '--parameters', "@$parameters", '--mode', 'Incremental', '--only-show-errors')
    $previewText = & az deployment group what-if @arguments --validation-level ProviderNoRbac `
        --result-format FullResourcePayloads --no-pretty-print --output json
    if ($LASTEXITCODE -ne 0) { throw 'Azure what-if failed; nothing is applied.' }
    $preview = $previewText | ConvertFrom-Json
    if ($preview.status -ne 'Succeeded' -or $null -eq $preview.changes) { throw 'What-if did not return a successful, reviewable change set.' }
    foreach ($change in $preview.changes) {
        if ($change.changeType -notin @('Create', 'Modify', 'NoChange')) {
            throw "What-if includes a forbidden or unsupported change type: $($change.changeType)"
        }
        $prefix = "/subscriptions/$env:AZURE_SUBSCRIPTION_ID/resourceGroups/$env:AZURE_RESOURCE_GROUP/"
        if (-not $change.resourceId.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'What-if changes a resource outside the dedicated approved resource group.'
        }
    }
    $canonicalChanges = ConvertTo-Json -InputObject @($preview.changes | Sort-Object resourceId) -Depth 100 -Compress
    $diffHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($canonicalChanges))).ToLowerInvariant()
    if ($ExpectedDiffHash -and $diffHash -ne $ExpectedDiffHash) { throw 'What-if changed since the approval stage; a new review is required.' }
    "diff_hash=$diffHash" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "### Cohors reviewed what-if`nSource: $SourceSha`n`nReview: $ReviewId`n`nChange digest: $diffHash`n" |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    $preview.changes | Select-Object changeType, resourceId | Format-Table | Out-String |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    if ($Operation -eq 'apply') {
        & az deployment group create @arguments --name "cohors-$($SourceSha.Substring(0, 12))" --output none
        if ($LASTEXITCODE -ne 0) { throw 'Approved deployment failed. Inspect the protected Azure deployment; no destructive rollback is attempted.' }
        Write-Output 'Approved incremental infrastructure deployment completed. Runtime package release and operational acceptance are separate.'
    }
} finally {
    if ($loggedIn) {
        & az logout --output none --only-show-errors
        if ($LASTEXITCODE -ne 0) { Write-Error 'OIDC session cleanup failed; discard this ephemeral runner.' }
    }
}
