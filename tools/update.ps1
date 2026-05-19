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
Set-Location $RootDir
$ConfigPath = Join-Path $RootDir "appsettings.json"

# Prevent MSBuild process reuse and shared compiler (VBCSCompiler) caching globally to avoid compilation locks
$env:MSBUILDDISABLENODEREUSE = "1"
$env:UseSharedCompilation = "false"


function Reset-RepositoryPermissions {
    Write-Host "Securing workspace permissions to prevent SYSTEM ownership lockouts..." -ForegroundColor Gray
    try {
        # Secure the root folder itself (non-recursive) - extremely fast
        takeown /F "$RootDir" /A *>$null
        icacls "$RootDir" /grant "Administrators:(OI)(CI)F" /Q *>$null
        icacls "$RootDir" /grant "Everyone:(OI)(CI)F" /Q *>$null

        # Secure key target folders recursively (only folders that are modified by service/installer)
        $TargetFolders = @(
            "$PublishDir",
            (Join-Path $RootDir ".references\SoundSwitch.Banner")
        )
        foreach ($folder in $TargetFolders) {
            if ($folder -and (Test-Path $folder)) {
                takeown /F "$folder" /R /A /D Y *>$null
                icacls "$folder" /grant "Administrators:(OI)(CI)F" /T /C /Q *>$null
                icacls "$folder" /grant "Everyone:(OI)(CI)F" /T /C /Q *>$null
            }
        }
    } catch {
        Write-Warning "Could not fully reset permissions: $_"
    }
}

function Unlock-CompileFiles {
    Write-Host "Releasing compilation locks and shutting down background build nodes..." -ForegroundColor Gray
    try {
        # Shutdown build-servers and compilers
        dotnet build-server shutdown | Out-Null
        Get-Process -Name "VBCSCompiler" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        
        # Shutdown any background dotnet.exe MSBuild processes that might be keeping files locked
        $CurrentPID = $PID
        Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $CurrentPID } | Stop-Process -Force -ErrorAction SilentlyContinue

        # Clean cache files specifically in references
        $RefPaths = @(
            (Join-Path $RootDir ".references\SoundSwitch.Banner"),
            (Join-Path $RootDir ".references\SoundSwitch.Common"),
            (Join-Path $RootDir ".references\Modern-Windows-Message-Box-Generator")
        )
        foreach ($path in $RefPaths) {
            if ($path -and (Test-Path $path)) {
                # Recursively remove any cache files to prevent MSB3101/CS2012 errors
                Get-ChildItem -Path $path -Filter "*.cache" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        # Silently absorb any temporary file access or termination errors
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
    Write-Host "Building WinAgent solution as Debug (Warnings as Errors)..." -ForegroundColor Cyan
    
    Unlock-CompileFiles
    Reset-RepositoryPermissions
    dotnet build WinAgent.slnx -c Debug /p:TreatWarningsAsErrors=true /p:UseSharedCompilation=false /p:NodeReuse=false
    if ($LASTEXITCODE -ne 0) { Write-Error "Solution debug build failed."; Exit-Script $LASTEXITCODE }
}

$MsiPath = Join-Path "$PublishDir\installer" "WinAgent-Installer.msi"
$newVersion = $null

if ($Publish) {
    $newVersion = Bump-Version
    Write-Host "Publishing Release $newVersion to GitHub..." -ForegroundColor Cyan
    
    # Git operations
    git add .
    git commit -m "v$newVersion"
    git push
}

if ($Deploy -or $Publish) {
    $InstallerProj = Join-Path $RootDir "WinAgent.Installer\WinAgent.Installer.wixproj"

    Write-Host "Building WiX MSI Installer as Release (automatically publishing dependencies)..." -ForegroundColor Cyan
    Unlock-CompileFiles
    Reset-RepositoryPermissions
    dotnet build $InstallerProj -c Release
    if ($LASTEXITCODE -ne 0) { Write-Error "Installer build failed."; Exit-Script $LASTEXITCODE }
    
    # WiX outputs the MSI to its bin directory. We need to copy it to the expected $MsiPath.
    $InstallerOutDir = Split-Path $MsiPath -Parent
    if (-not (Test-Path $InstallerOutDir)) { New-Item -ItemType Directory -Path $InstallerOutDir -Force | Out-Null }
    
    $SourceMsi = Join-Path $RootDir "WinAgent.Installer\bin\x64\Release\WinAgent-Installer.msi"
    if (-not (Test-Path $SourceMsi)) {
        $SourceMsi = Join-Path $RootDir "WinAgent.Installer\bin\Release\WinAgent-Installer.msi"
    }
    
    if (Test-Path $SourceMsi) {
        $Copied = $false
        for ($i = 1; $i -le 5; $i++) {
            try {
                if (Test-Path $MsiPath) { Remove-Item $MsiPath -Force -ErrorAction Stop }
                Copy-Item -Path $SourceMsi -Destination $MsiPath -Force -ErrorAction Stop
                $Copied = $true
                break
            } catch {
                Write-Host "Destination MSI locked, retrying in 1s ($i/5)..." -ForegroundColor Yellow
                Start-Sleep -Seconds 1
            }
        }
        if (-not $Copied) {
            Write-Error "CRITICAL: Failed to copy MSI to destination because it is locked."
            Exit-Script 1
        }
    } else {
        Write-Error "CRITICAL: MSI Installer package was not generated."
        Exit-Script 1
    }
}

if ($Deploy) {
    # Run the MSI installer
    Write-Host "Installing/Updating WinAgent locally via MSI..." -ForegroundColor Cyan
    try {
        # Run MSI in quiet / passive mode. Quiet with status feedback (/passive) is ideal for local update scripts
        Write-Host "Launching MSI installation..." -ForegroundColor Gray
        $MsiArgs = "/i `"$MsiPath`" /passive /norestart"
        if ($StartTray) {
            $MsiArgs += " STARTTRAY=`"1`""
        } else {
            $MsiArgs += " STARTTRAY=`"0`""
        }
        Start-Process msiexec.exe -ArgumentList $MsiArgs -Wait -NoNewWindow
        Write-Host "WinAgent locally updated/installed successfully!" -ForegroundColor Green
    } catch {
        Write-Error "CRITICAL: MSI Installation failed.`n$($_.Exception.Message)"
        Exit-Script 1
    }
}

if ($Publish) {
    # 3. Consolidate published files to shared directory for portable Scoop ZIP
    Write-Host "Consolidating app files to shared directory for portable distribution..." -ForegroundColor Gray
    $SharedOutputDir = "$PublishDir\shared"
    if (Test-Path $SharedOutputDir) { Remove-Item $SharedOutputDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $SharedOutputDir -Force | Out-Null
    
    # Copy from harvesting directories to the shared portable folder
    Copy-Item -Path "$RootDir\WinAgent.Service\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\*" -Destination $SharedOutputDir -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path "$RootDir\WinAgent.Tray\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\*" -Destination $SharedOutputDir -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path "$RootDir\WinAgent.CLI\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\*" -Destination $SharedOutputDir -Recurse -Force -ErrorAction SilentlyContinue

    # 4. Create companion ZIP archive for Scoop
    Write-Host "Creating companion ZIP archive..." -ForegroundColor Gray
    $ZipPath = Join-Path $PublishDir "WinAgent-portable.zip"
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path "$SharedOutputDir\*" -DestinationPath $ZipPath -Force

    # 5. Attach to GitHub release
    Write-Host "Creating GitHub Release..." -ForegroundColor Gray
    if (-not $newVersion) { $newVersion = "2.0.0" }
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
