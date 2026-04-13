#Requires -RunAsAdministrator
<#
.SYNOPSIS
    RollyRoll Server Installer - Bootstraps everything on a fresh Windows Server.

.DESCRIPTION
    This script installs the RollyRoll deployment server and all dependencies:
    1. Checks prerequisites (Windows Server 2019+, admin privileges)
    2. Installs .NET 8 Runtime if needed
    3. Downloads/extracts RollyRoll binaries
    4. Configures Windows Firewall rules
    5. Generates HTTPS certificate
    6. Initializes SQLite database
    7. Sets up image store and TFTP directories
    8. Copies iPXE boot binaries
    9. Registers and starts Windows Service
    10. Builds custom WinPE image (installs Windows ADK if needed)
    11. Opens browser for initial setup wizard

.PARAMETER InstallPath
    Installation directory. Default: C:\RollyRoll

.PARAMETER ImageStorePath
    Path for storing WIM images. Default: C:\RollyRoll\Images

.PARAMETER SkipWinPE
    Skip WinPE build (can be done later via Build-WinPE.ps1)

.PARAMETER Version
    Specific version to install. Default: latest from GitHub Releases.

.EXAMPLE
    .\Install-RollyRoll.ps1
    .\Install-RollyRoll.ps1 -InstallPath "D:\RollyRoll" -ImageStorePath "D:\Images"
#>

param(
    [string]$InstallPath = "C:\RollyRoll",
    [string]$ImageStorePath = "C:\RollyRoll\Images",
    [switch]$SkipWinPE,
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# --------------------------------------------------
# Helper Functions
# --------------------------------------------------

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Warning {
    param([string]$Message)
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
}

function Test-CommandExists {
    param([string]$Command)
    $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

# --------------------------------------------------
# Step 1: Check Prerequisites
# --------------------------------------------------

Write-Step "Checking prerequisites"

# Check Windows Server
$os = Get-CimInstance Win32_OperatingSystem
if ($os.ProductType -ne 3 -and $os.ProductType -ne 2) {
    Write-Warning "This is not a Windows Server OS. RollyRoll is designed for Windows Server but will attempt to install."
}

$winVersion = [System.Environment]::OSVersion.Version
if ($winVersion.Build -lt 17763) {
    throw "Windows Server 2019 (build 17763) or later is required. Current build: $($winVersion.Build)"
}
Write-Success "Windows Server $($os.Caption) (build $($winVersion.Build))"

# Check admin privileges
$currentUser = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $currentUser.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator."
}
Write-Success "Running as Administrator"

# Check network connectivity
try {
    $null = Invoke-WebRequest -Uri "https://github.com" -UseBasicParsing -TimeoutSec 10
    Write-Success "Network connectivity OK"
} catch {
    Write-Warning "Cannot reach github.com. If installing from local files, this is OK."
}

# --------------------------------------------------
# Step 2: Install .NET 8 Runtime
# --------------------------------------------------

Write-Step "Checking .NET 8 Runtime"

$dotnetInstalled = $false
if (Test-CommandExists "dotnet") {
    $runtimes = & dotnet --list-runtimes 2>$null
    if ($runtimes -match "Microsoft\.AspNetCore\.App 8\.") {
        Write-Success ".NET 8 ASP.NET Core Runtime already installed"
        $dotnetInstalled = $true
    }
}

if (-not $dotnetInstalled) {
    Write-Host "  Installing .NET 8 Runtime..." -ForegroundColor Yellow

    $dotnetInstallerUrl = "https://dot.net/v1/dotnet-install.ps1"
    $dotnetInstallerPath = Join-Path $env:TEMP "dotnet-install.ps1"

    Invoke-WebRequest -Uri $dotnetInstallerUrl -OutFile $dotnetInstallerPath -UseBasicParsing
    & $dotnetInstallerPath -Channel 8.0 -Runtime aspnetcore -InstallDir "C:\Program Files\dotnet"
    & $dotnetInstallerPath -Channel 8.0 -Runtime windowsdesktop -InstallDir "C:\Program Files\dotnet"

    # Add to PATH if not already
    $dotnetPath = "C:\Program Files\dotnet"
    if ($env:PATH -notmatch [regex]::Escape($dotnetPath)) {
        [Environment]::SetEnvironmentVariable("PATH", "$env:PATH;$dotnetPath", "Machine")
        $env:PATH = "$env:PATH;$dotnetPath"
    }

    Write-Success ".NET 8 Runtime installed"
}

# --------------------------------------------------
# Step 3: Create Directory Structure
# --------------------------------------------------

Write-Step "Creating directory structure"

$directories = @(
    $InstallPath,
    "$InstallPath\bin",
    "$InstallPath\TFTP",
    "$InstallPath\TFTP\ipxe",
    "$InstallPath\WinPE",
    "$InstallPath\data",
    "$InstallPath\logs",
    "$InstallPath\certs",
    "$InstallPath\profiles",
    $ImageStorePath
)

foreach ($dir in $directories) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Success "Created: $dir"
    } else {
        Write-Success "Exists: $dir"
    }
}

# --------------------------------------------------
# Step 4: Download/Extract RollyRoll Binaries
# --------------------------------------------------

Write-Step "Installing RollyRoll binaries"

# In production, this downloads from GitHub Releases
# For now, we check if binaries exist locally (built from source)
$binPath = "$InstallPath\bin"

if (-not (Test-Path "$binPath\RollyRoll.WindowsService.exe")) {
    Write-Host "  Binaries not found at $binPath" -ForegroundColor Yellow
    Write-Host "  To install from source, build the solution and copy output to $binPath" -ForegroundColor Yellow
    Write-Host "  Example: dotnet publish src\RollyRoll.WindowsService -c Release -o $binPath" -ForegroundColor Yellow
} else {
    Write-Success "Binaries found at $binPath"
}

# --------------------------------------------------
# Step 5: Configure Windows Firewall
# --------------------------------------------------

Write-Step "Configuring Windows Firewall rules"

$firewallRules = @(
    @{ Name = "RollyRoll-WebUI-HTTPS"; Port = 443; Protocol = "TCP"; Description = "RollyRoll Web UI (HTTPS)" },
    @{ Name = "RollyRoll-API"; Port = 8080; Protocol = "TCP"; Description = "RollyRoll API (WinPE Agent)" },
    @{ Name = "RollyRoll-DHCP-Proxy"; Port = 4011; Protocol = "UDP"; Description = "RollyRoll DHCP Proxy (PXE)" },
    @{ Name = "RollyRoll-TFTP"; Port = 69; Protocol = "UDP"; Description = "RollyRoll TFTP (iPXE boot files)" },
    @{ Name = "RollyRoll-WoL"; Port = 9; Protocol = "UDP"; Description = "RollyRoll Wake-on-LAN" },
    @{ Name = "RollyRoll-Discovery"; Port = 5150; Protocol = "UDP"; Description = "RollyRoll Auto-Discovery" }
)

foreach ($rule in $firewallRules) {
    $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Success "Firewall rule exists: $($rule.Name)"
    } else {
        New-NetFirewallRule -DisplayName $rule.Name `
            -Direction Inbound `
            -Protocol $rule.Protocol `
            -LocalPort $rule.Port `
            -Action Allow `
            -Description $rule.Description `
            -Profile Domain,Private | Out-Null
        Write-Success "Created firewall rule: $($rule.Name) ($($rule.Protocol) $($rule.Port))"
    }
}

# --------------------------------------------------
# Step 6: Generate HTTPS Certificate
# --------------------------------------------------

Write-Step "Setting up HTTPS certificate"

$certPath = "$InstallPath\certs\rollyroll.pfx"
if (-not (Test-Path $certPath)) {
    $cert = New-SelfSignedCertificate `
        -DnsName "localhost", $env:COMPUTERNAME, "rollyroll" `
        -CertStoreLocation "Cert:\LocalMachine\My" `
        -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName "RollyRoll Server"

    $certPassword = ConvertTo-SecureString -String "RollyRoll_$(Get-Random -Maximum 999999)" -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $certPath -Password $certPassword | Out-Null

    # Save password hint (in production, use DPAPI or Key Vault)
    $certPassword | ConvertFrom-SecureString | Set-Content "$InstallPath\certs\cert-password.enc"

    Write-Success "Self-signed certificate created: $certPath"
    Write-Warning "For production, replace with a trusted certificate."
} else {
    Write-Success "Certificate already exists: $certPath"
}

# --------------------------------------------------
# Step 7: Initialize Database
# --------------------------------------------------

Write-Step "Initializing database"

$dbPath = "$InstallPath\data\rollyroll.db"
Write-Success "SQLite database will be initialized at: $dbPath"
Write-Host "  (Database schema will be created on first run via EF Core migrations)" -ForegroundColor Gray

# --------------------------------------------------
# Step 8: Copy iPXE Binaries
# --------------------------------------------------

Write-Step "Setting up iPXE boot files"

$tftpRoot = "$InstallPath\TFTP"
$ipxeDir = "$tftpRoot\ipxe"

# iPXE binaries should be included in the installer package or downloaded
$ipxeFiles = @("undionly.kpxe", "ipxe.efi", "ipxe32.efi")
foreach ($file in $ipxeFiles) {
    $filePath = Join-Path $ipxeDir $file
    if (-not (Test-Path $filePath)) {
        Write-Warning "iPXE binary missing: $file (download from ipxe.org or build from source)"
    } else {
        Write-Success "iPXE binary: $file"
    }
}

# --------------------------------------------------
# Step 9: Register Windows Service
# --------------------------------------------------

Write-Step "Registering Windows Service"

$serviceName = "RollyRoll"
$serviceExe = "$binPath\RollyRoll.WindowsService.exe"

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Success "Service '$serviceName' already registered"
} else {
    if (Test-Path $serviceExe) {
        New-Service -Name $serviceName `
            -BinaryPathName $serviceExe `
            -DisplayName "RollyRoll Deployment Server" `
            -Description "Open-source Windows fleet deployment and management platform" `
            -StartupType Automatic | Out-Null
        Write-Success "Service '$serviceName' registered (auto-start)"
    } else {
        Write-Warning "Service executable not found: $serviceExe"
        Write-Host "  Build the project first, then re-run the installer." -ForegroundColor Yellow
    }
}

# --------------------------------------------------
# Step 10: Build WinPE Image (Optional)
# --------------------------------------------------

if (-not $SkipWinPE) {
    Write-Step "Building WinPE boot image"

    $buildWinPeScript = Join-Path $PSScriptRoot "Build-WinPE.ps1"
    if (Test-Path $buildWinPeScript) {
        & $buildWinPeScript -OutputPath "$InstallPath\WinPE"
    } else {
        Write-Warning "Build-WinPE.ps1 not found. Run it manually after installation."
    }
} else {
    Write-Step "Skipping WinPE build (-SkipWinPE specified)"
}

# --------------------------------------------------
# Step 11: Write Configuration
# --------------------------------------------------

Write-Step "Writing configuration"

$configPath = "$InstallPath\bin\appsettings.json"
if (-not (Test-Path $configPath)) {
    $config = @{
        ConnectionStrings = @{
            DefaultConnection = "Data Source=$dbPath"
        }
        RollyRoll = @{
            ImageStorePath = $ImageStorePath
            TftpRoot = $tftpRoot
            CertificatePath = $certPath
            ProfilesPath = "$InstallPath\profiles"
            LogsPath = "$InstallPath\logs"
            ServerPort = 443
            ApiPort = 8080
        }
        Logging = @{
            LogLevel = @{
                Default = "Information"
                "Microsoft.AspNetCore" = "Warning"
            }
        }
    } | ConvertTo-Json -Depth 4

    Set-Content -Path $configPath -Value $config
    Write-Success "Configuration written: $configPath"
} else {
    Write-Success "Configuration exists: $configPath"
}

# --------------------------------------------------
# Step 12: Start Service and Open Browser
# --------------------------------------------------

Write-Step "Starting RollyRoll"

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne "Running") {
    try {
        Start-Service -Name $serviceName
        Write-Success "Service started"
    } catch {
        Write-Warning "Could not start service: $_"
        Write-Host "  Ensure binaries are built and try: Start-Service $serviceName" -ForegroundColor Yellow
    }
}

# --------------------------------------------------
# Summary
# --------------------------------------------------

Write-Host "`n" -NoNewline
Write-Host "============================================" -ForegroundColor Green
Write-Host "  RollyRoll Installation Complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Install Path:    $InstallPath" -ForegroundColor White
Write-Host "  Image Store:     $ImageStorePath" -ForegroundColor White
Write-Host "  Database:        $dbPath" -ForegroundColor White
Write-Host "  TFTP Root:       $tftpRoot" -ForegroundColor White
Write-Host "  Web UI:          https://localhost:443" -ForegroundColor White
Write-Host "  API Endpoint:    https://localhost:8080" -ForegroundColor White
Write-Host ""
Write-Host "  Next Steps:" -ForegroundColor Yellow
Write-Host "    1. Open https://localhost in a browser for initial setup" -ForegroundColor White
Write-Host "    2. Configure your DHCP server to point PXE clients to this server" -ForegroundColor White
Write-Host "       Option 66 (Boot Server): $(hostname)" -ForegroundColor White
Write-Host "       Option 67 (Boot File):   ipxe/ipxe.efi" -ForegroundColor White
Write-Host "    3. Capture your first gold image from a reference PC" -ForegroundColor White
Write-Host ""

# Try to open browser
try {
    Start-Process "https://localhost"
} catch {
    # Ignore if no browser
}
