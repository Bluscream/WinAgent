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

# Self-elevate script if not running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    $passedArgs = @()
    foreach ($key in $PSBoundParameters.Keys) {
        $val = $PSBoundParameters[$key]
        if ($val -is [switch]) {
            if ($val) { $passedArgs += "-$key" }
        } else {
            $passedArgs += "-$key `"$val`""
        }
    }
    $passedArgs += $args
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $passedArgs" -Verb RunAs
    exit
}

$RootDir = Split-Path -Parent $PSScriptRoot
$ConfigPath = Join-Path $RootDir "appsettings.json"

function Reset-RepositoryPermissions {
    Write-Host "Securing workspace permissions to prevent SYSTEM ownership lockouts..." -ForegroundColor Gray
    try {
        # Take ownership of the repository directory and subdirectories for Administrators
        takeown /F "$RootDir" /R /A /D Y *>$null
        # Grant Full Control with inheritance to Administrators and Users/Everyone
        icacls "$RootDir" /grant Administrators:(OI)(CI)F /T /C /Q *>$null
        icacls "$RootDir" /grant Everyone:(OI)(CI)F /T /C /Q *>$null
    } catch {
        Write-Warning "Could not fully reset permissions: $_"
    }
}

# Helper for clean exits with log transcripts
function Exit-Script ($code = 0) {
    Reset-RepositoryPermissions
    Stop-Transcript -ErrorAction SilentlyContinue
    exit $code
}

# Set up logging transcript
$LogDir = Join-Path $RootDir "publish"
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory $LogDir -Force | Out-Null }
$LogPath = Join-Path $LogDir "update_run.log"

Start-Transcript -Path $LogPath -Append -Force -ErrorAction SilentlyContinue

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
    Exit-Script 1
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

# Clean up permissions before building/cleaning to ensure we can overwrite everything
Reset-RepositoryPermissions

function Bump-Version {
    $ServiceCsproj = Join-Path $RootDir "WinAgent.Service\WinAgent.Service.csproj"
    $TrayCsproj = Join-Path $RootDir "WinAgent.Tray\WinAgent.Tray.csproj"
    $CliCsproj = Join-Path $RootDir "WinAgent.CLI\WinAgent.CLI.csproj"
    $WxsPath = Join-Path $RootDir "WinAgent.Installer\Package.wxs"

    $content = Get-Content $ServiceCsproj -Raw
    if ($content -match '<Version>(?<version>.*)</Version>') {
        $version = [version]$Matches['version']
        $newVersion = "{0}.{1}.{2}" -f $version.Major, ($version.Minor + 1), 0
        
        # 1. Update Service
        $content = $content -replace "<Version>.*</Version>", "<Version>$newVersion</Version>"
        $content | Set-Content $ServiceCsproj
        
        # 2. Update Tray
        if (Test-Path $TrayCsproj) {
            $c = Get-Content $TrayCsproj -Raw
            $c = $c -replace "<Version>.*</Version>", "<Version>$newVersion</Version>"
            $c | Set-Content $TrayCsproj
        }

        # 3. Update CLI
        if (Test-Path $CliCsproj) {
            $c = Get-Content $CliCsproj -Raw
            $c = $c -replace "<Version>.*</Version>", "<Version>$newVersion</Version>"
            $c | Set-Content $CliCsproj
        }

        # 4. Update Package.wxs
        if (Test-Path $WxsPath) {
            $c = Get-Content $WxsPath -Raw
            $c = $c -replace 'Version="[^"]+"', "Version=""$newVersion"""
            $c | Set-Content $WxsPath
        }

        Write-Host "Synchronized and bumped version to $newVersion in all projects and installer configuration!" -ForegroundColor Magenta
        return $newVersion
    }
    return "2.0.0"
}

if ($Stop -or $Deploy) {
    Write-Host "Stopping WinAgent Service..." -ForegroundColor Cyan
    $ServiceExePath = Join-Path $DeployDir "WinAgent.Service.exe"
    if (-not (Test-Path $ServiceExePath)) {
        $ServiceExePath = "C:\Program Files\WinAgent\WinAgent.Service.exe"
    }
    if (Test-Path $ServiceExePath) {
        & $ServiceExePath --stop
    } else {
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            sc.exe stop $ServiceName
            Start-Sleep -Seconds 2
        }
    }

    Write-Host "Cleaning up any remaining processes..." -ForegroundColor Gray
    Get-Process WinAgent.Service -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process WinAgent.Tray -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process winagent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process SoundSwitch -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process SoundSwitch.Banner -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Host "Waiting for file locks to release on all binaries..." -ForegroundColor Gray
    $retry = 10
    $FilesToCheck = @(
        "$DeployDir\WinAgent.Service.exe",
        "$DeployDir\WinAgent.Tray.exe",
        "$DeployDir\winagent.exe",
        "C:\Program Files\WinAgent\WinAgent.Service.exe",
        "C:\Program Files\WinAgent\WinAgent.Tray.exe",
        "C:\Program Files\WinAgent\winagent.exe"
    )
    
    # Dynamically find compiled SoundSwitch DLLs and EXEs to check for file locks
    $SoundSwitchFiles = Get-ChildItem -Path (Join-Path $RootDir ".references") -Include "SoundSwitch.*.dll","SoundSwitch.*.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
    if ($SoundSwitchFiles) {
        $FilesToCheck += $SoundSwitchFiles
    }
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
    $SolutionPath = Join-Path $RootDir "WinAgent.slnx"
    dotnet build -c Release $SolutionPath /p:TreatWarningsAsErrors=true /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with errors or warnings."
        Exit-Script $LASTEXITCODE
    }
}

if ($Deploy) {
    dotnet build-server shutdown | Out-Null
    
    $ServiceCsproj = Join-Path $RootDir "WinAgent.Service\WinAgent.Service.csproj"
    $TrayCsproj = Join-Path $RootDir "WinAgent.Tray\WinAgent.Tray.csproj"
    $CliCsproj = Join-Path $RootDir "WinAgent.CLI\WinAgent.CLI.csproj"
    $InstallerProj = Join-Path $RootDir "WinAgent.Installer\WinAgent.Installer.wixproj"

    Write-Host "Publishing C# projects to harvesting directory..." -ForegroundColor Cyan
    dotnet publish $ServiceCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -p:ErrorOnDuplicatePublishOutputFiles=false -o "$RootDir\WinAgent.Installer\obj\x64\Release\publish\WinAgent.Service"
    dotnet publish $TrayCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -p:ErrorOnDuplicatePublishOutputFiles=false -o "$RootDir\WinAgent.Installer\obj\x64\Release\publish\WinAgent.Tray"
    dotnet publish $CliCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -p:ErrorOnDuplicatePublishOutputFiles=false -o "$RootDir\WinAgent.Installer\obj\x64\Release\publish\WinAgent.CLI"

    Write-Host "Building WiX MSI Installer package..." -ForegroundColor Cyan
    dotnet build $InstallerProj -c Release -r win-x64 -p:BuildProjectReferences=false -o "$PublishDir\installer"
    
    $MsiPath = Join-Path "$PublishDir\installer" "WinAgent-Installer.msi"
    if (-not (Test-Path $MsiPath)) {
        Write-Error "CRITICAL: MSI Installer package was not generated."
        Exit-Script 1
    }

    # Run the MSI installer
    Write-Host "Installing/Updating WinAgent locally via MSI..." -ForegroundColor Cyan
    try {
        # Run MSI in quiet / passive mode. Quiet with status feedback (/passive) is ideal for local update scripts
        Write-Host "Launching MSI installation..." -ForegroundColor Gray
        Start-Process msiexec.exe -ArgumentList "/i `"$MsiPath`" /passive /norestart" -Wait -NoNewWindow
        Write-Host "WinAgent locally updated/installed successfully!" -ForegroundColor Green
    } catch {
        Write-Error "CRITICAL: MSI Installation failed.`n$($_.Exception.Message)"
        Exit-Script 1
    }
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
    $TargetServiceExe = Join-Path "C:\Program Files\WinAgent" "WinAgent.Service.exe"
    if (-not (Test-Path $TargetServiceExe)) {
        $TargetServiceExe = Join-Path $DeployDir "WinAgent.Service.exe"
    }
    if (-not (Test-Path $TargetServiceExe)) {
        $TargetServiceExe = Join-Path "$PublishDir\shared" "WinAgent.Service.exe"
    }
    
    # Use the agent's native install logic
    $InstallArgs = @("--install")
    if ($Stop) { $InstallArgs += "--stop" }
    if ($Start) { $InstallArgs += "--start" }
    if ($StartTray) { $InstallArgs += "--start-tray" }
    
    & $TargetServiceExe $InstallArgs
}

if ($Start -and -not $Install) {
    Write-Host "Starting WinAgent Service..." -ForegroundColor Cyan
    $TargetServiceExe = Join-Path "C:\Program Files\WinAgent" "WinAgent.Service.exe"
    if (-not (Test-Path $TargetServiceExe)) {
        $TargetServiceExe = Join-Path $DeployDir "WinAgent.Service.exe"
    }
    if (-not (Test-Path $TargetServiceExe)) {
        $TargetServiceExe = Join-Path "$PublishDir\shared" "WinAgent.Service.exe"
    }
    & $TargetServiceExe --start
    
    if (-not $StartTray) {
        Start-Sleep -Seconds 2
        $TrayExePath = Join-Path "C:\Program Files\WinAgent" "WinAgent.Tray.exe"
        if (-not (Test-Path $TrayExePath)) {
            $TrayExePath = Join-Path $DeployDir "WinAgent.Tray.exe"
        }
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
Exit-Script 0
