param([uri]$BaseUri = 'http://localhost:5187')
$ErrorActionPreference = 'Stop'
if (-not $BaseUri.IsLoopback -or $BaseUri.Scheme -notin @('http','https')) {
    throw 'This script uses a generated local demo account and is restricted to loopback.'
}
$root = Split-Path $PSScriptRoot -Parent
$line = Get-Content -LiteralPath (Join-Path $root '.local\demo-access.txt') | Where-Object { $_ -like 'pms.demo : *' }
if (-not $line) { throw 'Generated local PMS account not found.' }
$password = $line.Substring('pms.demo : '.Length)
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$url = $BaseUri.AbsoluteUri.TrimEnd('/')
$auth = Invoke-RestMethod "$url/api/auth/session" -WebSession $session
$headers = @{ 'X-CSRF-Token'=$auth.csrfToken; 'Idempotency-Key'=[Guid]::NewGuid().ToString('N') }
Invoke-RestMethod "$url/api/auth/login" -Method Post -WebSession $session -Headers $headers `
    -ContentType 'application/json' -Body (@{ username='pms.demo'; password=$password } | ConvertTo-Json) | Out-Null
$auth = Invoke-RestMethod "$url/api/auth/session" -WebSession $session
$cases = Invoke-RestMethod "$url/api/cases" -WebSession $session
$before = @($cases).Count
foreach ($source in @('fda','pubmed')) {
    $headers = @{ 'X-CSRF-Token'=$auth.csrfToken; 'Idempotency-Key'=[Guid]::NewGuid().ToString('N') }
    $run = Invoke-RestMethod "$url/api/sources/$source/sync" -Method Post -WebSession $session -Headers $headers `
        -ContentType 'application/json' -Body '{"productId":"ortho"}' -TimeoutSec 70
    if ($run.status -ne 'ReadOnlyMetadata') { throw "$source read failed explicitly: $($run.error)" }
    if ($run.items.Count -gt 5) { throw "$source exceeded its bounded reader contract." }
    $runs = Invoke-RestMethod "$url/api/sources" -WebSession $session
    $saved = $runs | Where-Object id -EQ $run.id
    if ($saved.version -ne $run.version) { throw "$source result was not persisted." }
    Write-Host "$source : actual public read; SQL version $($run.version); $($run.items.Count) metadata items; $($run.id)"
}
$cases = Invoke-RestMethod "$url/api/cases" -WebSession $session
$after = @($cases).Count
if ($after -ne $before) { throw 'Case count changed during the read-only source smoke test; inspect concurrent intake.' }
Write-Host 'Public metadata adapters verified through the application. No own-device cases or external communications created.'
