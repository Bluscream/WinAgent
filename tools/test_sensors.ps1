# Resolve WINAGENT_TOKEN exactly like update.ps1 does
$Token = $env:WINAGENT_TOKEN
if (-not $Token) {
    $Token = [System.Environment]::GetEnvironmentVariable('WINAGENT_TOKEN', 'Machine')
}
if (-not $Token) {
    $Token = [System.Environment]::GetEnvironmentVariable('WINAGENT_TOKEN', 'User')
}
$Port = if ($env:WINAGENT_PORT) { [int]$env:WINAGENT_PORT } else { 23482 }

$ConfigPath = "p:\Visual Studio\source\repos\WinAgent\appsettings.json"
if (Test-Path $ConfigPath) {
    try {
        $json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
        if ($json.WinAgent.Token) { $Token = $json.WinAgent.Token }
        if ($json.WinAgent.Port) { $Port = $json.WinAgent.Port }
    } catch {}
}

Write-Host "Resolved Port: $Port"
if ($Token) {
    Write-Host "Resolved Token ending in: $($Token.Substring($Token.Length - 4))"
} else {
    Write-Host "No token found!"
}

$headers = @{ Authorization = "Bearer $Token" }
try {
    $resp = Invoke-RestMethod -Uri "http://localhost:$Port/api/sensors" -Headers $headers -ErrorAction Stop
    Write-Host "Success! Sensors retrieved:"
    $resp | ConvertTo-Json -Depth 5
} catch {
    Write-Error "Failed to retrieve sensors: $_"
}
