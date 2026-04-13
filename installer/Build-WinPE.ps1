#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Builds a custom WinPE boot image with the RollyRoll WinPE Agent embedded.

.DESCRIPTION
    Creates a bootable WinPE .wim file that:
    - Contains the RollyRoll.WinPEAgent executable
    - Auto-starts the agent on boot (via winpeshl.ini)
    - Includes WinPE optional components: WMI, scripting, .NET, PowerShell, DISM, SecureStartup
    - Supports both UEFI and Legacy BIOS boot
    - Is Secure Boot compatible (Microsoft-signed WinPE)

    Prerequisites:
    - Windows ADK (Assessment and Deployment Kit) with WinPE add-on
    - .NET 8 Runtime for self-contained publish of WinPEAgent

.PARAMETER OutputPath
    Directory to output the WinPE files. Default: C:\RollyRoll\WinPE

.PARAMETER AgentPath
    Path to published RollyRoll.WinPEAgent files. Default: auto-detect from bin directory.
#>

param(
    [string]$OutputPath = "C:\RollyRoll\WinPE",
    [string]$AgentPath
)

$ErrorActionPreference = "Stop"

function Write-Step { param([string]$Message); Write-Host "`n=== $Message ===" -ForegroundColor Cyan }
function Write-Success { param([string]$Message); Write-Host "  [OK] $Message" -ForegroundColor Green }

# --------------------------------------------------
# Step 1: Check/Install Windows ADK
# --------------------------------------------------

Write-Step "Checking Windows ADK"

$adkPath = "${env:ProgramFiles(x86)}\Windows Kits\10\Assessment and Deployment Kit"
$winpePath = "$adkPath\Windows Preinstallation Environment"
$deployToolsPath = "$adkPath\Deployment Tools"

if (-not (Test-Path $winpePath)) {
    Write-Host "  Windows ADK with WinPE add-on not found." -ForegroundColor Yellow
    Write-Host "  Downloading and installing Windows ADK..." -ForegroundColor Yellow

    # Download ADK installer
    $adkUrl = "https://go.microsoft.com/fwlink/?linkid=2243390"  # Windows ADK for Windows 11
    $adkInstaller = Join-Path $env:TEMP "adksetup.exe"
    Invoke-WebRequest -Uri $adkUrl -OutFile $adkInstaller -UseBasicParsing

    # Install ADK (Deployment Tools feature)
    Start-Process -FilePath $adkInstaller -ArgumentList "/quiet /features OptionId.DeploymentTools" -Wait

    # Download WinPE add-on
    $winpeAddonUrl = "https://go.microsoft.com/fwlink/?linkid=2243391"
    $winpeInstaller = Join-Path $env:TEMP "adkwinpesetup.exe"
    Invoke-WebRequest -Uri $winpeAddonUrl -OutFile $winpeInstaller -UseBasicParsing

    # Install WinPE add-on
    Start-Process -FilePath $winpeInstaller -ArgumentList "/quiet" -Wait

    Write-Success "Windows ADK with WinPE installed"
} else {
    Write-Success "Windows ADK found at $adkPath"
}

# --------------------------------------------------
# Step 2: Set Up Environment
# --------------------------------------------------

Write-Step "Setting up WinPE build environment"

# Import ADK environment
$dism = "$deployToolsPath\amd64\DISM\dism.exe"
$copype = "$winpePath\copype.cmd"
$makewinpe = "$winpePath\MakeWinPEMedia\MakeWinPEMedia.cmd"

$workingDir = Join-Path $env:TEMP "RollyRoll_WinPE_Build"
if (Test-Path $workingDir) { Remove-Item $workingDir -Recurse -Force }
New-Item -ItemType Directory -Path $workingDir -Force | Out-Null

# Copy PE base files
& cmd /c "`"$copype`" amd64 `"$workingDir`""
Write-Success "WinPE base files copied to $workingDir"

# --------------------------------------------------
# Step 3: Mount and Customize WinPE Image
# --------------------------------------------------

Write-Step "Customizing WinPE image"

$mountDir = "$workingDir\mount"
$wimPath = "$workingDir\media\sources\boot.wim"

# Mount WinPE image
& $dism /Mount-Image /ImageFile:"$wimPath" /Index:1 /MountDir:"$mountDir"
Write-Success "WinPE image mounted"

# Add optional components for DISM, PowerShell, .NET, WMI, Secure Boot
$winpeOcPath = "$winpePath\amd64\WinPE_OCs"
$optionalComponents = @(
    "WinPE-WMI",
    "WinPE-NetFx",
    "WinPE-Scripting",
    "WinPE-PowerShell",
    "WinPE-DismCmdlets",
    "WinPE-SecureStartup",
    "WinPE-EnhancedStorage",
    "WinPE-StorageWMI",
    "WinPE-FMAPI"
)

foreach ($component in $optionalComponents) {
    $cabPath = "$winpeOcPath\$component.cab"
    if (Test-Path $cabPath) {
        & $dism /Image:"$mountDir" /Add-Package /PackagePath:"$cabPath" 2>$null
        # Also add language pack if available
        $langCab = "$winpeOcPath\en-us\${component}_en-us.cab"
        if (Test-Path $langCab) {
            & $dism /Image:"$mountDir" /Add-Package /PackagePath:"$langCab" 2>$null
        }
        Write-Success "Added: $component"
    }
}

# --------------------------------------------------
# Step 4: Inject RollyRoll Agent
# --------------------------------------------------

Write-Step "Injecting RollyRoll WinPE Agent"

$agentDir = "$mountDir\RollyRoll"
New-Item -ItemType Directory -Path $agentDir -Force | Out-Null

# Find agent files
if (-not $AgentPath) {
    $AgentPath = "C:\RollyRoll\bin"
}

$agentExe = Join-Path $AgentPath "RollyRoll.WinPEAgent.exe"
if (Test-Path $agentExe) {
    Copy-Item -Path "$AgentPath\RollyRoll.WinPEAgent*" -Destination $agentDir -Force
    Copy-Item -Path "$AgentPath\RollyRoll.Core*" -Destination $agentDir -Force -ErrorAction SilentlyContinue
    Write-Success "Agent files injected"
} else {
    Write-Warning "WinPEAgent not found at $AgentPath. Build it first."
    Write-Host "  dotnet publish src/RollyRoll.WinPEAgent -c Release -r win-x64 --self-contained" -ForegroundColor Yellow
}

# Configure auto-start: winpeshl.ini tells WinPE to run our agent on boot
$winpeshlIni = @"
[LaunchApps]
%SYSTEMDRIVE%\RollyRoll\RollyRoll.WinPEAgent.exe
"@

Set-Content -Path "$mountDir\Windows\System32\winpeshl.ini" -Value $winpeshlIni
Write-Success "Auto-start configured (winpeshl.ini)"

# Add startup script for network initialization
$startupScript = @"
@echo off
echo Initializing network...
wpeinit
echo Starting RollyRoll Agent...
X:\RollyRoll\RollyRoll.WinPEAgent.exe
pause
"@

Set-Content -Path "$mountDir\RollyRoll\startnet.cmd" -Value $startupScript

# --------------------------------------------------
# Step 5: Unmount and Save
# --------------------------------------------------

Write-Step "Saving WinPE image"

& $dism /Unmount-Image /MountDir:"$mountDir" /Commit
Write-Success "WinPE image saved"

# --------------------------------------------------
# Step 6: Copy to Output
# --------------------------------------------------

Write-Step "Copying to output directory"

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
Copy-Item -Path "$workingDir\media\*" -Destination $OutputPath -Recurse -Force
Write-Success "WinPE files copied to $OutputPath"

# Copy boot.wim specifically for TFTP serving
$tftpBootDir = "C:\RollyRoll\TFTP"
if (Test-Path $tftpBootDir) {
    $bootDir = "$tftpBootDir\boot"
    New-Item -ItemType Directory -Path $bootDir -Force | Out-Null
    Copy-Item -Path "$OutputPath\sources\boot.wim" -Destination "$bootDir\boot.wim" -Force
    Copy-Item -Path "$OutputPath\boot\bcd" -Destination "$bootDir\bcd" -Force
    Copy-Item -Path "$OutputPath\boot\boot.sdi" -Destination "$bootDir\boot.sdi" -Force
    Write-Success "Boot files copied to TFTP directory"
}

# Cleanup
Remove-Item $workingDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n  WinPE build complete!" -ForegroundColor Green
Write-Host "  Output: $OutputPath" -ForegroundColor White
Write-Host "  Boot WIM: $OutputPath\sources\boot.wim" -ForegroundColor White
