param([int]$SqlPort = 14338, [int]$WebPort = 5187)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
New-Item -ItemType Directory -Force .local | Out-Null
$settingsPath = Join-Path $root '.local\settings.json'
if (Test-Path $settingsPath) {
    Write-Host 'Local configuration already exists; passwords and persisted SQL are unchanged.'
    exit 0
}
$containerName = 'cohors-sql-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($root))).Substring(0,8).ToLowerInvariant()
$password = 'Cohors!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$env:MSSQL_SA_PASSWORD = $password
$container = docker run --detach --name $containerName --label app=cohors-pilot --restart unless-stopped `
    --publish "127.0.0.1:${SqlPort}:1433" --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD `
    --volume "${containerName}-data:/var/opt/mssql" `
    mcr.microsoft.com/mssql/server@sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f
$containerExit = $LASTEXITCODE
if ($container -match '^[a-f0-9]{64}$') { Write-Output "COHORS_SQL_CONTAINER_ID=$container" }
if ($containerExit -ne 0) { throw 'Could not start the isolated Cohors SQL container.' }
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    $result = docker exec --env "SQLCMDPASSWORD=$password" $containerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q 'SELECT 1' 2>&1
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw "SQL did not become ready. Inspect container $containerName; no existing data was removed." }
docker exec --env "SQLCMDPASSWORD=$password" $containerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q "CREATE DATABASE Cohors;"
if ($LASTEXITCODE -ne 0) { throw 'Database creation failed.' }
$accounts = @()
$access = @('Cohors LOCAL SYNTHETIC DEMO ONLY. Do not share this file or reuse these passwords.', '')
$roles = @{ pms='PmsSpecialist'; medical='MedicalSafetyReviewer'; ra='RegulatoryAffairs'; quality='QualityManager'; auditor='Auditor' }
foreach ($name in @('pms','medical','ra','quality','auditor')) {
    $secret = 'Pilot!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(12))
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($secret,$salt,210000,[Security.Cryptography.HashAlgorithmName]::SHA256,32)
    $accounts += @{ Id=[Guid]::NewGuid().ToString(); Username="$name.demo"; Name="Demo $name"; Role=$roles[$name]; Products=@('ortho','cardio'); Salt=[Convert]::ToBase64String($salt); Hash=[Convert]::ToBase64String($hash) }
    $access += "$name.demo : $secret"
}
$workloadKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
@{
    ConnectionStrings=@{ Cohors="Server=127.0.0.1,$SqlPort;Database=Cohors;User Id=sa;Password=$password;Encrypt=True;TrustServerCertificate=True;" }
    Authentication=@{ Mode='LocalDemo'; Users=$accounts; WorkloadKey=$workloadKey }
    Cohors=@{ Home=$root; AutoMigrate=$true; Seed=$true; LocalOperations=$true; ObjectStore='Local'; Search='Local'; PublicSources=$true }
    Local=@{ ContainerName=$containerName; WebPort=$WebPort; SqlPort=$SqlPort }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding utf8
$access | Set-Content .local\demo-access.txt -Encoding utf8
Remove-Item Env:\MSSQL_SA_PASSWORD
Write-Host "SQL ready on loopback port $SqlPort. Private local account passwords are in .local\demo-access.txt."
