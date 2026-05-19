param (
    [switch]$Stop,
    [switch]$Build,
    [switch]$Publish,
    [switch]$Deploy,
    [switch]$Install,
    [switch]$Start,
    [switch]$StartTray,
    [string]$DeployPath = "D:\Scripts\WinAgent.Service.exe"
)

$RootDir = Split-Path -Parent $PSScriptRoot
$ConfigPath = Join-Path $RootDir "appsettings.json"

# Load config from environment or JSON if exists
$Token = $env:WINAGENT_TOKEN
if (-not $Token) {
    $Token = [System.Environment]::GetEnvironmentVariable('WINAGENT_TOKEN', 'Machine')
}
if (-not $Token) {
    $Token = [System.Environment]::GetEnvironmentVariable('WINAGENT_TOKEN', 'User')
}
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

# Resolve deploy directory from DeployPath
$DeployDir = if ($DeployPath -and (Test-Path $DeployPath -PathType Container)) {
    $DeployPath
} else {
    Split-Path $DeployPath -Parent
}
if (-not $DeployDir) { $DeployDir = "D:\Scripts" }

Write-Host "Loaded config (Token: ...$($Token.Substring($Token.Length - 4)), Port: $Port)" -ForegroundColor Gray
Write-Host "Target Deployment Directory: $DeployDir" -ForegroundColor Gray

$PublishDir = Join-Path $RootDir "publish"
$ServiceName = "WinAgent"

function Bump-Version {
    $ServiceCsproj = Join-Path $RootDir "WinAgent.Service\WinAgent.Service.csproj"
    $content = Get-Content $ServiceCsproj -Raw
    if ($content -match '<Version>(?<version>.*)</Version>') {
        $version = [version]$Matches['version']
        $newVersion = "{0}.{1}" -f $version.Major, ($version.Minor + 1)
        $content = $content -replace "<Version>.*</Version>", "<Version>$newVersion</Version>"
        $content | Set-Content $ServiceCsproj
        Write-Host "Bumped version in WinAgent.Service to $newVersion" -ForegroundColor Magenta
        return $newVersion
    }
    return "1.0.0"
}

if ($Stop) {
    Write-Host "Stopping WinAgent Service..." -ForegroundColor Cyan
    $ServiceExePath = Join-Path $DeployDir "WinAgent.Service.exe"
    if (Test-Path $ServiceExePath) {
        sudo $ServiceExePath --stop
    } else {
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            sudo sc.exe stop $ServiceName
            Start-Sleep -Seconds 2
        }
    }

    Write-Host "Cleaning up any remaining processes..." -ForegroundColor Gray
    Get-Process WinAgent.Service -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process WinAgent.Tray -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process winagent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process ipc-mcp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    taskkill /F /IM WinAgent.Service.exe /T 2>$null
    taskkill /F /IM WinAgent.Tray.exe /T 2>$null
    taskkill /F /IM winagent.exe /T 2>$null
    taskkill /F /IM ipc-mcp.exe /T 2>$null

    Write-Host "Waiting for file locks to release on all binaries..." -ForegroundColor Gray
    $retry = 10
    $FilesToCheck = @(
        "$DeployDir\WinAgent.Service.exe",
        "$DeployDir\WinAgent.Tray.exe",
        "$DeployDir\winagent.exe"
    )
    while ($retry -gt 0) {
        $locked = $false
        foreach ($file in $FilesToCheck) {
            if (Test-Path $file) {
                try {
                    $testStream = [System.IO.File]::Open($file, 'Open', 'Write', 'None')
                    $testStream.Close()
                } catch {
                    $locked = $true
                }
            }
        }
        if (-not $locked) { break }
        Start-Sleep -Seconds 1
        $retry--
    }
}

if ($Build) {
    dotnet build-server shutdown | Out-Null
    Write-Host "Building solution (Warnings as Errors)..." -ForegroundColor Cyan
    dotnet build -c Release $RootDir /p:TreatWarningsAsErrors=true /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with errors or warnings."
        exit $LASTEXITCODE
    }
}

if ($Deploy) {
    dotnet build-server shutdown | Out-Null
    Write-Host "Publishing all projects to shared directory..." -ForegroundColor Cyan
    
    $ServiceCsproj = Join-Path $RootDir "WinAgent.Service\WinAgent.Service.csproj"
    $TrayCsproj = Join-Path $RootDir "WinAgent.Tray\WinAgent.Tray.csproj"
    $CliCsproj = Join-Path $RootDir "WinAgent.CLI\WinAgent.CLI.csproj"

    # Publish to a single shared directory using uncompressed, sharing assemblies
    dotnet publish $ServiceCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -o "$PublishDir\shared"
    dotnet publish $TrayCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -o "$PublishDir\shared"
    dotnet publish $CliCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -o "$PublishDir\shared"

    Write-Host "Deploying shared folder to $DeployDir..." -ForegroundColor Cyan
    if (-not (Test-Path $DeployDir)) {
        New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null
    }

    try {
        # Copy the shared directory recursively
        Copy-Item -Path "$PublishDir\shared\*" -Destination $DeployDir -Recurse -Force -ErrorAction Stop
        Write-Host "Successfully deployed to $DeployDir" -ForegroundColor Green
    } catch {
        Write-Error "CRITICAL: Failed to copy files to $DeployDir. File is likely locked.`n$($_.Exception.Message)"
        exit 1
    }

    Copy-Item $ConfigPath $DeployDir -Force -ErrorAction SilentlyContinue
    Write-Host "Configuration copied to $DeployDir" -ForegroundColor Green
}

if ($Publish) {
    $newVersion = Bump-Version
    Write-Host "Publishing Release $newVersion to GitHub..." -ForegroundColor Cyan
    
    # Git operations
    git add .
    git commit -m "v$newVersion"
    git push

    # 1. Publish all projects to shared directory
    Write-Host "Publishing app files..." -ForegroundColor Gray
    $ServiceCsproj = Join-Path $RootDir "WinAgent.Service\WinAgent.Service.csproj"
    $TrayCsproj = Join-Path $RootDir "WinAgent.Tray\WinAgent.Tray.csproj"
    $CliCsproj = Join-Path $RootDir "WinAgent.CLI\WinAgent.CLI.csproj"

    dotnet publish $ServiceCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -o "$PublishDir\shared"
    dotnet publish $TrayCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -o "$PublishDir\shared"
    dotnet publish $CliCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -o "$PublishDir\shared"

    # 2. Build the WiX MSI Installer package
    Write-Host "Building WiX MSI Installer..." -ForegroundColor Gray
    $InstallerProj = Join-Path $RootDir "WinAgent.Installer\WinAgent.Installer.wixproj"
    dotnet build $InstallerProj -c Release -r win-x64 -o "$PublishDir\installer"
    
    $MsiPath = Join-Path "$PublishDir\installer" "WinAgent-Installer.msi"

    # 3. Create companion ZIP archive for Scoop
    Write-Host "Creating companion ZIP archive..." -ForegroundColor Gray
    $ZipPath = Join-Path $PublishDir "WinAgent-portable.zip"
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path "$PublishDir\shared\*" -DestinationPath $ZipPath -Force

    # 4. Attach to GitHub release
    Write-Host "Creating GitHub Release..." -ForegroundColor Gray
    gh release create "v$newVersion" $MsiPath $ZipPath --title "Release v$newVersion" --notes "Automated release via update.ps1 with WiX Installer (MSI) and Portable ZIP"
}

if ($Install) {
    Write-Host "Registering service and persistence via the agent itself..." -ForegroundColor Cyan
    $TargetServiceExe = Join-Path $DeployDir "WinAgent.Service.exe"
    if (-not (Test-Path $TargetServiceExe)) {
        $TargetServiceExe = Join-Path "$PublishDir\shared" "WinAgent.Service.exe"
    }
    
    # Use the agent's native install logic
    $InstallArgs = @("--install")
    if ($Stop) { $InstallArgs += "--stop" }
    if ($Start) { $InstallArgs += "--start" }
    if ($StartTray) { $InstallArgs += "--start-tray" }
    
    sudo $TargetServiceExe $InstallArgs
}

if ($Start -and -not $Install) {
    Write-Host "Starting WinAgent Service..." -ForegroundColor Cyan
    $TargetServiceExe = Join-Path $DeployDir "WinAgent.Service.exe"
    if (-not (Test-Path $TargetServiceExe)) {
        $TargetServiceExe = Join-Path "$PublishDir\shared" "WinAgent.Service.exe"
    }
    sudo $TargetServiceExe --start
    
    if (-not $StartTray) {
        Start-Sleep -Seconds 2
        $TrayExePath = Join-Path $DeployDir "WinAgent.Tray.exe"
        if (-not (Test-Path $TrayExePath)) {
            $TrayExePath = Join-Path "$PublishDir\shared" "WinAgent.Tray.exe"
        }
        if (Test-Path $TrayExePath) {
            Write-Host "Starting Tray companion..." -ForegroundColor Gray
            Start-Process $TrayExePath
        } else {
            Write-Warning "WinAgent.Tray.exe not found. Skipping tray start."
        }
    }
}

Write-Host "Done!" -ForegroundColor Green
