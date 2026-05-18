param (
    [switch]$Stop,
    [switch]$Build,
    [switch]$Publish,
    [switch]$Deploy,
    [switch]$Install,
    [switch]$Start,
    [switch]$StartTray,
    [string]$DeployPath = "D:\Scripts\WinAgent.exe"
)

$RootDir = Split-Path -Parent $PSScriptRoot
$ConfigPath = Join-Path $RootDir "appsettings.json"

# Load config from JSON if exists
$Token = $env:WINAGENT_TOKEN
$Port = if ($env:WINAGENT_PORT) { [int]$env:WINAGENT_PORT } else { 23482 }

if (Test-Path $ConfigPath) {
    try {
        $json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
        if ($json.WinAgent.Token) { $Token = $json.WinAgent.Token }
        if ($json.WinAgent.Port) { $Port = $json.WinAgent.Port }
    } catch {
        Write-Warning "Failed to parse $ConfigPath."
    }
}

if (-not $Token) {
    Write-Error "CRITICAL: WINAGENT_TOKEN not found in config or environment. Deployment aborted for security."
    exit 1
}

Write-Host "Loaded config (Token: ...$($Token.Substring($Token.Length - 4)), Port: $Port)" -ForegroundColor Gray

$CsprojPath = Join-Path $RootDir "WinAgent.csproj"
$PublishDir = Join-Path $RootDir "publish"
$ExePath = Join-Path $PublishDir "WinAgent.exe"
$ServiceName = "WinAgent"

function Bump-Version {
    $content = Get-Content $CsprojPath -Raw
    if ($content -match '<Version>(?<version>.*)</Version>') {
        $version = [version]$Matches['version']
        $newVersion = "{0}.{1}" -f $version.Major, ($version.Minor + 1)
        $content = $content -replace "<Version>.*</Version>", "<Version>$newVersion</Version>"
        $content | Set-Content $CsprojPath
        Write-Host "Bumped version to $newVersion" -ForegroundColor Magenta
        return $newVersion
    }
    return "1.0.0"
}

if ($Stop) {
    Write-Host "Stopping WinAgent via native flag..." -ForegroundColor Cyan
    if (Test-Path $DeployPath) {
        sudo $DeployPath --stop
    } else {
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            sudo sc.exe stop $ServiceName
            Start-Sleep -Seconds 2
        }
    }

    Write-Host "Cleaning up any remaining processes..." -ForegroundColor Gray
    Get-Process WinAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    taskkill /F /IM WinAgent.exe /T 2>$null

    Write-Host "Waiting for file locks to release..." -ForegroundColor Gray
    $retry = 10
    while ($retry -gt 0) {
        try {
            if (Test-Path $DeployPath) {
                $testStream = [System.IO.File]::Open($DeployPath, 'Open', 'Write', 'None')
                $testStream.Close()
            }
            break
        } catch {
            Start-Sleep -Seconds 1
            $retry--
        }
    }
}

if ($Build) {
    Write-Host "Building project (Warnings as Errors)..." -ForegroundColor Cyan
    dotnet build -c Release $RootDir /p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with errors or warnings."
        exit $LASTEXITCODE
    }
}

if ($Deploy) {
    Write-Host "Deploying single-file to $DeployPath..." -ForegroundColor Cyan
    dotnet publish $RootDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PublishDir\single"
    $SingleExe = Join-Path "$PublishDir\single" "WinAgent.exe"
    if (Test-Path $SingleExe) {
        if (Test-Path $DeployPath) {
            try {
                $oldPath = $DeployPath + ".old"
                if (Test-Path $oldPath) { Remove-Item $oldPath -Force -ErrorAction SilentlyContinue }
                Rename-Item $DeployPath $oldPath -Force -ErrorAction SilentlyContinue
            } catch {
                Write-Warning "Could not rename $DeployPath. Attempting direct overwrite..."
            }
        }
        try {
            Copy-Item $SingleExe $DeployPath -Force -ErrorAction Stop
            $DeployDir = Split-Path $DeployPath
            Copy-Item $ConfigPath $DeployDir -Force -ErrorAction SilentlyContinue
            Write-Host "Deployed to $DeployPath (with config)" -ForegroundColor Green
        } catch {
            Write-Error "CRITICAL: Failed to copy to $DeployPath. File is likely still locked.`n$($_.Exception.Message)"
            exit 1
        }
    }
}

if ($Publish) {
    $newVersion = Bump-Version
    Write-Host "Publishing Release $newVersion to GitHub..." -ForegroundColor Cyan
    
    # Git operations
    git add .
    git commit -m "v$newVersion"
    git push

    # GH Release
    dotnet publish $RootDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$PublishDir\release"
    $ReleaseExe = Join-Path "$PublishDir\release" "WinAgent.exe"
    gh release create "v$newVersion" $ReleaseExe --title "Release v$newVersion" --notes "Automated release via update.ps1"
}

if ($Install) {
    Write-Host "Registering service and persistence via the agent itself..." -ForegroundColor Cyan
    $TargetExe = if ($Deploy) { $DeployPath } else { $ExePath }
    
    # Use the agent's native install logic
    $InstallArgs = @("--install")
    if ($Stop) { $InstallArgs += "--stop" }
    if ($Start) { $InstallArgs += "--start" }
    if ($StartTray) { $InstallArgs += "--start-tray" }
    
    sudo $TargetExe $InstallArgs
}

if ($Start -and -not $Install) {
    Write-Host "Starting WinAgent Service..." -ForegroundColor Cyan
    $TargetExe = if ($Deploy) { $DeployPath } else { $ExePath }
    sudo $TargetExe --start
    
    if (-not $StartTray) {
        Start-Sleep -Seconds 2
        Write-Host "Starting Tray companion..." -ForegroundColor Gray
        $StartArgs = @("--tray", "--token", $Token)
        Start-Process $TargetExe -ArgumentList $StartArgs
    }
}

Write-Host "Done!" -ForegroundColor Green
