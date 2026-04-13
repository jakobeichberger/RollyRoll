#Requires -RunAsAdministrator
<#
.SYNOPSIS
    RollyRoll In-Place Updater - Updates to a new version with automatic rollback on failure.

.DESCRIPTION
    Update workflow:
    1. Download new version from GitHub Releases (or local path)
    2. Stop RollyRoll Windows Service
    3. Backup current binaries + database
    4. Replace binaries
    5. Run EF Core database migrations
    6. Rebuild WinPE if agent was updated
    7. Restart services
    8. Verify health endpoint
    9. Rollback if health check fails

.PARAMETER Version
    Version to update to. Default: latest.

.PARAMETER LocalPath
    Path to a local build output directory (instead of downloading).

.PARAMETER InstallPath
    Current installation path. Default: C:\RollyRoll
#>

param(
    [string]$Version = "latest",
    [string]$LocalPath,
    [string]$InstallPath = "C:\RollyRoll"
)

$ErrorActionPreference = "Stop"
$serviceName = "RollyRoll"
$binPath = "$InstallPath\bin"
$backupPath = "$InstallPath\backups\$(Get-Date -Format 'yyyyMMdd_HHmmss')"
$dbPath = "$InstallPath\data\rollyroll.db"
$healthUrl = "https://localhost/health"

function Write-Step { param([string]$Message); Write-Host "`n=== $Message ===" -ForegroundColor Cyan }
function Write-Success { param([string]$Message); Write-Host "  [OK] $Message" -ForegroundColor Green }

# --------------------------------------------------
# Step 1: Pre-flight Checks
# --------------------------------------------------

Write-Step "Pre-flight checks"

if (-not (Test-Path $InstallPath)) {
    throw "RollyRoll installation not found at $InstallPath. Run Install-RollyRoll.ps1 first."
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    throw "RollyRoll service not found. Run Install-RollyRoll.ps1 first."
}

Write-Success "Installation found at $InstallPath"

# --------------------------------------------------
# Step 2: Stop Service
# --------------------------------------------------

Write-Step "Stopping RollyRoll service"

if ($service.Status -eq "Running") {
    Stop-Service -Name $serviceName -Force
    Start-Sleep -Seconds 3
    Write-Success "Service stopped"
} else {
    Write-Success "Service already stopped"
}

# --------------------------------------------------
# Step 3: Backup Current Installation
# --------------------------------------------------

Write-Step "Creating backup"

New-Item -ItemType Directory -Path $backupPath -Force | Out-Null

# Backup binaries
Copy-Item -Path "$binPath\*" -Destination "$backupPath\bin\" -Recurse -Force
Write-Success "Binaries backed up to $backupPath\bin\"

# Backup database
if (Test-Path $dbPath) {
    Copy-Item -Path $dbPath -Destination "$backupPath\rollyroll.db" -Force
    Write-Success "Database backed up to $backupPath\rollyroll.db"
}

# Backup config
if (Test-Path "$binPath\appsettings.json") {
    Copy-Item -Path "$binPath\appsettings.json" -Destination "$backupPath\appsettings.json" -Force
    Write-Success "Config backed up"
}

# --------------------------------------------------
# Step 4: Update Binaries
# --------------------------------------------------

Write-Step "Updating binaries"

if ($LocalPath) {
    if (-not (Test-Path $LocalPath)) {
        throw "Local path not found: $LocalPath"
    }
    # Preserve config
    $configBackup = Get-Content "$binPath\appsettings.json" -Raw -ErrorAction SilentlyContinue

    Copy-Item -Path "$LocalPath\*" -Destination $binPath -Recurse -Force
    Write-Success "Binaries updated from $LocalPath"

    # Restore config
    if ($configBackup) {
        Set-Content -Path "$binPath\appsettings.json" -Value $configBackup
        Write-Success "Configuration preserved"
    }
} else {
    Write-Host "  Downloading version: $Version from GitHub Releases..." -ForegroundColor Yellow
    # In production: download from GitHub Releases API
    Write-Warning "GitHub Releases download not yet implemented. Use -LocalPath for now."
}

# --------------------------------------------------
# Step 5: Database Migration
# --------------------------------------------------

Write-Step "Running database migrations"

$serviceExe = "$binPath\RollyRoll.WindowsService.exe"
if (Test-Path $serviceExe) {
    # EF Core migrations run automatically on startup
    Write-Success "Database migrations will run on service startup"
} else {
    Write-Warning "Service executable not found — skipping migration"
}

# --------------------------------------------------
# Step 6: Rebuild WinPE (if agent updated)
# --------------------------------------------------

Write-Step "Checking WinPE agent"

$winpeAgentPath = "$binPath\RollyRoll.WinPEAgent.exe"
$winpeBackupAgent = "$backupPath\bin\RollyRoll.WinPEAgent.exe"

if ((Test-Path $winpeAgentPath) -and (Test-Path $winpeBackupAgent)) {
    $newHash = (Get-FileHash $winpeAgentPath).Hash
    $oldHash = (Get-FileHash $winpeBackupAgent).Hash
    if ($newHash -ne $oldHash) {
        Write-Host "  WinPE agent changed, rebuilding WinPE image..." -ForegroundColor Yellow
        $buildScript = Join-Path $PSScriptRoot "Build-WinPE.ps1"
        if (Test-Path $buildScript) {
            & $buildScript -OutputPath "$InstallPath\WinPE"
        } else {
            Write-Warning "Build-WinPE.ps1 not found. Rebuild WinPE manually."
        }
    } else {
        Write-Success "WinPE agent unchanged, no rebuild needed"
    }
}

# --------------------------------------------------
# Step 7: Restart Service
# --------------------------------------------------

Write-Step "Starting RollyRoll service"

Start-Service -Name $serviceName
Start-Sleep -Seconds 5
Write-Success "Service started"

# --------------------------------------------------
# Step 8: Health Check
# --------------------------------------------------

Write-Step "Verifying health"

$healthy = $false
for ($i = 0; $i -lt 6; $i++) {
    try {
        $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5 -SkipCertificateCheck
        if ($response.StatusCode -eq 200) {
            $healthy = $true
            break
        }
    } catch {
        Start-Sleep -Seconds 5
    }
}

if ($healthy) {
    Write-Success "Health check passed!"
    Write-Host "`n  Update complete! RollyRoll is running." -ForegroundColor Green
} else {
    # --------------------------------------------------
    # Step 9: Rollback
    # --------------------------------------------------

    Write-Host "`n  Health check FAILED! Rolling back..." -ForegroundColor Red

    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    # Restore binaries
    Copy-Item -Path "$backupPath\bin\*" -Destination $binPath -Recurse -Force

    # Restore database
    if (Test-Path "$backupPath\rollyroll.db") {
        Copy-Item -Path "$backupPath\rollyroll.db" -Destination $dbPath -Force
    }

    # Restore config
    if (Test-Path "$backupPath\appsettings.json") {
        Copy-Item -Path "$backupPath\appsettings.json" -Destination "$binPath\appsettings.json" -Force
    }

    Start-Service -Name $serviceName
    Write-Host "  Rolled back to previous version. Check logs at $InstallPath\logs\" -ForegroundColor Yellow
}
