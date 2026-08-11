#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Entfernt den Monitoring-Agenten wieder vollstaendig vom Geraet.

.DESCRIPTION
    Entfernt windows_exporter, Grafana Alloy, die geplante Aufgabe, die
    Firewallregel und alle abgelegten Dateien.

    Zum Ausrollen eignet sich derselbe Weg wie beim Installieren: als
    Computer-Startskript in einer Gruppenrichtlinie, die auf die
    betreffenden Geraete angewendet wird.

.PARAMETER ProtokolleBehalten
    Laesst den Ordner mit den Installationsprotokollen stehen.

.EXAMPLE
    .\Uninstall-MonitoringAgent.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$ProtokolleBehalten
)

Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Continue'

$BasisPfad    = "$env:ProgramData\SchulMonitoring"
$Aufgaben = @('SchulMonitoring-Hardwarepruefung', 'SchulMonitoring-Sicherheitspruefung',
              'SchulMonitoring-AdPruefung')

function Melde { param([string]$Text) Write-Host "  $Text" -ForegroundColor Gray }

Write-Host ''
Write-Host "Monitoring-Agent wird von $env:COMPUTERNAME entfernt" -ForegroundColor Cyan
Write-Host ''

# --- Geplante Aufgabe -------------------------------------------------
foreach ($aufgabe in $Aufgaben) {
    if (Get-ScheduledTask -TaskName $aufgabe -ErrorAction SilentlyContinue) {
        if ($PSCmdlet.ShouldProcess($aufgabe, 'Geplante Aufgabe entfernen')) {
            Unregister-ScheduledTask -TaskName $aufgabe -Confirm:$false
            Melde "Geplante Aufgabe '$aufgabe' entfernt"
        }
    }
}

# --- windows_exporter -------------------------------------------------
$exporter = Get-CimInstance Win32_Product -Filter "Name LIKE '%windows_exporter%'" -ErrorAction SilentlyContinue
if ($exporter) {
    if ($PSCmdlet.ShouldProcess('windows_exporter', 'Deinstallieren')) {
        foreach ($paket in $exporter) {
            Start-Process msiexec.exe -ArgumentList @('/x', $paket.IdentifyingNumber, '/qn', '/norestart') `
                -Wait -NoNewWindow
        }
        Melde 'windows_exporter deinstalliert'
    }
} else {
    Melde 'windows_exporter war nicht installiert'
}

# --- Grafana Alloy ----------------------------------------------------
$alloyDeinstallation = 'C:\Program Files\GrafanaLabs\Alloy\uninstaller.exe'
if (Test-Path -LiteralPath $alloyDeinstallation) {
    if ($PSCmdlet.ShouldProcess('Grafana Alloy', 'Deinstallieren')) {
        Stop-Service -Name 'Alloy' -Force -ErrorAction SilentlyContinue
        Start-Process $alloyDeinstallation -ArgumentList '/S' -Wait -NoNewWindow
        Melde 'Grafana Alloy deinstalliert'
    }
} else {
    Melde 'Grafana Alloy war nicht installiert'
}

# --- Firewallregel ----------------------------------------------------
$regel = Get-NetFirewallRule -DisplayName 'Schul-Monitoring – Metrikabruf' -ErrorAction SilentlyContinue
if ($regel) {
    if ($PSCmdlet.ShouldProcess('Firewallregel', 'Entfernen')) {
        $regel | Remove-NetFirewallRule
        Melde 'Firewallregel entfernt'
    }
}

# --- Dateien ----------------------------------------------------------
if (Test-Path -LiteralPath $BasisPfad) {
    if ($ProtokolleBehalten) {
        Get-ChildItem -LiteralPath $BasisPfad -Exclude 'logs' |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Melde 'Dateien entfernt, Protokolle behalten'
    } else {
        if ($PSCmdlet.ShouldProcess($BasisPfad, 'Verzeichnis entfernen')) {
            Remove-Item -LiteralPath $BasisPfad -Recurse -Force -ErrorAction SilentlyContinue
            Melde 'Alle Dateien entfernt'
        }
    }
}

Write-Host ''
Write-Host 'Fertig.' -ForegroundColor Green
Write-Host 'Das Geraet verschwindet nach Ablauf von AGENT_TIMEOUT_TAGE von selbst aus dem Dashboard.' -ForegroundColor Gray
Write-Host ''
