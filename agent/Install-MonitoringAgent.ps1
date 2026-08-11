#Requires -Version 5.1
<#
.SYNOPSIS
    Richtet den Monitoring-Agenten auf einem Windows-Server oder -Client ein.

.DESCRIPTION
    Gedacht als Computer-Startskript in einer Gruppenrichtlinie. Das Skript

      * erkennt selbst, ob es auf einem Client, einem Server, einem
        Domaenencontroller oder einem Hyper-V-Host laeuft,
      * installiert windows_exporter mit den dazu passenden Sammlern,
      * installiert Grafana Alloy fuer die Ereignisprotokolle,
      * legt eine geplante Aufgabe fuer die Hardwarepruefung an,
      * oeffnet die Firewall gezielt nur fuer den Monitoring-Server,
      * meldet das Geraet beim Monitoring-Server an.

    Das Skript ist wiederholbar: Bei jedem Neustart prueft es nur kurz, ob
    alles aktuell ist, und ist dann in ein paar Sekunden wieder fertig.
    Erst wenn sich die Version aendert, wird tatsaechlich neu installiert.

.PARAMETER ServerUrl
    Basisadresse des Monitoring-Servers, z. B. http://10.0.0.50

.PARAMETER Token
    Gemeinsames Geheimnis. Steht nach der Einrichtung in der Ausgabe des
    Bereitstellungsskripts bzw. in /root/ZUGANGSDATEN.txt auf der VM.

.PARAMETER Profil
    Automatisch (Standard), Server oder Client. Uebersteuert die Erkennung.

.PARAMETER Raum
    Optionale Raumangabe, taucht in Dashboards und Alarm-Mails auf.

.PARAMETER Standort
    Standortangabe, falls mehrere Schulstandorte zusammenlaufen.

.PARAMETER Neuinstallation
    Erzwingt die Installation, auch wenn die Version schon stimmt.

.EXAMPLE
    .\Install-MonitoringAgent.ps1 -ServerUrl "http://10.0.0.50" -Token "geheim"

.EXAMPLE
    .\Install-MonitoringAgent.ps1 -ServerUrl "http://10.0.0.50" -Token "geheim" `
        -Raum "EDV-Saal 1" -Profil Client
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerUrl,
    [Parameter(Mandatory)][string]$Token,
    [ValidateSet('Automatisch', 'Server', 'Client')][string]$Profil = 'Automatisch',
    [string]$Raum = '',
    [string]$Standort = 'Schule',
    [int]$ExporterPort = 9182,
    [switch]$OhneProtokollsammlung,
    [switch]$Neuinstallation
)

# Version 1.0: siehe Collect-HardwareHealth.ps1 – WMI-Objekte bringen je
# nach Hardware nicht immer alle Eigenschaften mit.
Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ===================================================================== #
# Konstanten
# ===================================================================== #
$AgentVersion   = '1.3.0'
$BasisPfad      = "$env:ProgramData\SchulMonitoring"
$TextdateiPfad  = "$BasisPfad\textfile"
$ProtokollPfad  = "$BasisPfad\logs"
$ArbeitsPfad    = "$BasisPfad\temp"
$VersionsDatei  = "$BasisPfad\agent-version.txt"
$AufgabenName   = 'SchulMonitoring-Hardwarepruefung'
$AufgabeSicherheit = 'SchulMonitoring-Sicherheitspruefung'
$AufgabeAd      = 'SchulMonitoring-AdPruefung'

$ServerUrl = $ServerUrl.TrimEnd('/')

# ===================================================================== #
# Protokollierung
# ===================================================================== #
foreach ($ordner in @($BasisPfad, $TextdateiPfad, $ProtokollPfad, $ArbeitsPfad)) {
    if (-not (Test-Path -LiteralPath $ordner)) {
        New-Item -ItemType Directory -Path $ordner -Force | Out-Null
    }
}

$ProtokollDatei = Join-Path $ProtokollPfad "installation-$(Get-Date -Format 'yyyy-MM').log"

function Schreibe {
    param(
        [string]$Text,
        [ValidateSet('Info', 'Erfolg', 'Warnung', 'Fehler')][string]$Art = 'Info'
    )
    $zeile = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Art.ToUpper(), $Text
    try { Add-Content -LiteralPath $ProtokollDatei -Value $zeile -Encoding UTF8 } catch { }

    $farbe = switch ($Art) {
        'Erfolg'  { 'Green' }
        'Warnung' { 'Yellow' }
        'Fehler'  { 'Red' }
        default   { 'Gray' }
    }
    Write-Host $zeile -ForegroundColor $farbe
}

# Alte Protokolle aufraeumen (aelter als sechs Monate)
Get-ChildItem -LiteralPath $ProtokollPfad -Filter 'installation-*.log' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddMonths(-6) } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# ===================================================================== #
# Hilfsfunktionen
# ===================================================================== #
function Invoke-ServerAbruf {
    <#
        Ruft etwas vom Monitoring-Server ab. Faellt bei Bedarf auf die
        Originalquelle im Internet zurueck, damit ein einzelnes Geraet auch
        dann versorgt werden kann, wenn der Server die Datei nicht hat.
    #>
    param(
        [Parameter(Mandatory)][string]$Pfad,
        [Parameter(Mandatory)][string]$Zieldatei,
        [string]$Ersatzquelle
    )

    $quellen = @("$ServerUrl/mon/dist/$Pfad")
    if ($Ersatzquelle) { $quellen += $Ersatzquelle }

    foreach ($quelle in $quellen) {
        try {
            Schreibe "Lade $quelle"
            $kopf = @{ 'X-Agent-Token' = $Token }
            Invoke-WebRequest -Uri $quelle -OutFile $Zieldatei -Headers $kopf `
                -UseBasicParsing -TimeoutSec 600
            if ((Get-Item -LiteralPath $Zieldatei).Length -gt 0) { return $true }
        } catch {
            Schreibe "Nicht verfuegbar: $quelle ($($_.Exception.Message))" -Art Warnung
        }
    }
    return $false
}

function Get-SystemRolle {
    <# Ermittelt, was fuer ein Geraet das hier ist. #>
    $betriebssystem = Get-CimInstance Win32_OperatingSystem
    $rechner = Get-CimInstance Win32_ComputerSystem

    $rolle = switch ($betriebssystem.ProductType) {
        1 { 'client' }
        2 { 'dc' }
        3 { 'server' }
        default { 'client' }
    }

    if ($Profil -eq 'Server' -and $rolle -eq 'client') { $rolle = 'server' }
    if ($Profil -eq 'Client') { $rolle = 'client' }

    # Hyper-V-Hosts bekommen eigene Sammler
    $istHyperV = $false
    if ($rolle -ne 'client') {
        $vmms = Get-Service -Name 'vmms' -ErrorAction SilentlyContinue
        if ($vmms) { $istHyperV = $true; if ($rolle -eq 'server') { $rolle = 'hyperv' } }
    }

    return [pscustomobject]@{
        Rolle          = $rolle
        IstServer      = ($rolle -ne 'client')
        IstHyperV      = $istHyperV
        Betriebssystem = $betriebssystem.Caption
        Version        = $betriebssystem.Version
        Domaene        = $rechner.Domain
        Modell         = "$($rechner.Manufacturer) $($rechner.Model)".Trim()
    }
}

function Get-Sammlerliste {
    param([object]$System)

    $sammler = [System.Collections.Generic.List[string]]::new()
    @('cpu', 'cs', 'logical_disk', 'physical_disk', 'memory', 'net', 'os',
      'service', 'system', 'textfile', 'time', 'logon') |
        ForEach-Object { $sammler.Add($_) }

    # Temperaturfuehler liefern nicht alle Geraete, schadet aber nicht
    $sammler.Add('thermalzone')

    if ($System.IstServer) {
        @('process', 'tcp', 'terminal_services', 'smb', 'scheduled_task', 'printer') |
            ForEach-Object { $sammler.Add($_) }
    }

    if ($System.Rolle -eq 'dc') {
        @('ad', 'dns', 'dhcp') | ForEach-Object { $sammler.Add($_) }
    }

    if ($System.IstHyperV) { $sammler.Add('hyperv') }

    if (Get-Service -Name 'W3SVC' -ErrorAction SilentlyContinue) { $sammler.Add('iis') }
    if (Get-Service -Name 'MSSQLSERVER' -ErrorAction SilentlyContinue) { $sammler.Add('mssql') }

    return ($sammler | Select-Object -Unique) -join ','
}

function Get-EigeneIpAdresse {
    <# Die IP-Adresse, ueber die dieses Geraet den Monitoring-Server erreicht. #>
    try {
        $serverName = ([uri]$ServerUrl).Host
        $ziel = if ($serverName -match '^\d{1,3}(\.\d{1,3}){3}$') {
            $serverName
        } else {
            (Resolve-DnsName -Name $serverName -Type A -ErrorAction Stop |
                Select-Object -First 1).IPAddress
        }

        $route = Find-NetRoute -RemoteIPAddress $ziel -ErrorAction Stop | Select-Object -First 1
        if ($route -and $route.IPAddress) { return $route.IPAddress }
    } catch {
        Schreibe "Route zum Server nicht ermittelbar: $($_.Exception.Message)" -Art Warnung
    }

    # Rueckfallebene: erste aktive Adresse, die keine Loopback- oder APIPA-Adresse ist
    $adresse = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' -and $_.PrefixOrigin -ne 'WellKnown' } |
        Select-Object -First 1
    if ($adresse) { return $adresse.IPAddress }
    return ''
}

function Test-AgentAktuell {
    if ($Neuinstallation) { return $false }
    if (-not (Test-Path -LiteralPath $VersionsDatei)) { return $false }

    $gespeichert = (Get-Content -LiteralPath $VersionsDatei -Raw -ErrorAction SilentlyContinue).Trim()
    if ($gespeichert -ne $AgentVersion) { return $false }

    # Auch der Dienst muss wirklich laufen
    $dienst = Get-Service -Name 'windows_exporter' -ErrorAction SilentlyContinue
    if (-not $dienst) { return $false }
    if ($dienst.Status -ne 'Running') {
        Schreibe 'windows_exporter laeuft nicht – wird gestartet' -Art Warnung
        try { Start-Service -Name 'windows_exporter' } catch { return $false }
    }

    return $true
}

# ===================================================================== #
# Installationsschritte
# ===================================================================== #
function Install-WindowsExporter {
    param([object]$System)

    $sammler = Get-Sammlerliste -System $System
    Schreibe "Sammler: $sammler"

    $paket = Join-Path $ArbeitsPfad 'windows_exporter.msi'
    $ersatz = 'https://github.com/prometheus-community/windows_exporter/releases/latest/download/windows_exporter-0.30.5-amd64.msi'

    if (-not (Invoke-ServerAbruf -Pfad 'windows_exporter.msi' -Zieldatei $paket -Ersatzquelle $ersatz)) {
        throw 'windows_exporter konnte weder vom Monitoring-Server noch aus dem Internet geladen werden.'
    }

    # Prozess-Sammler auf die wirklich interessanten Dienste begrenzen –
    # sonst entstehen pro Geraet hunderte Zeitreihen.
    $prozessMuster = '(sqlservr|w3wp|vmms|vmwp|lsass|ntds|dns|dhcpserver|MsMpEng|spoolsv|veeam.*|sqlwriter|inetinfo)'

    $eigenschaften = @(
        "ENABLED_COLLECTORS=$sammler"
        "LISTEN_PORT=$ExporterPort"
        "TEXTFILE_DIRS=$TextdateiPfad"
        "TEXTFILE_DIR=$TextdateiPfad"
        "EXTRA_FLAGS=--collector.process.include=`"$prozessMuster`" --collector.service.include=`".+`""
    )

    Schreibe 'windows_exporter wird installiert'
    $argumente = @('/i', "`"$paket`"", '/qn', '/norestart') + $eigenschaften
    $vorgang = Start-Process -FilePath 'msiexec.exe' -ArgumentList $argumente -Wait -PassThru -NoNewWindow

    # 3010 = Erfolg, Neustart empfohlen
    if ($vorgang.ExitCode -notin @(0, 3010)) {
        throw "Die Installation von windows_exporter ist fehlgeschlagen (Code $($vorgang.ExitCode))."
    }

    Start-Sleep -Seconds 3
    $dienst = Get-Service -Name 'windows_exporter' -ErrorAction SilentlyContinue
    if (-not $dienst) { throw 'Der Dienst windows_exporter wurde nach der Installation nicht gefunden.' }
    if ($dienst.Status -ne 'Running') { Start-Service -Name 'windows_exporter' }

    Set-Service -Name 'windows_exporter' -StartupType Automatic

    # Nach einem Absturz von selbst wieder starten
    & sc.exe failure windows_exporter reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null

    Schreibe 'windows_exporter laeuft' -Art Erfolg
}

function Install-Alloy {
    param([object]$System)

    if ($OhneProtokollsammlung) {
        Schreibe 'Protokollsammlung wurde per Parameter abgewaehlt' -Art Warnung
        return
    }

    $zip = Join-Path $ArbeitsPfad 'alloy-installer.zip'
    $ersatz = 'https://github.com/grafana/alloy/releases/latest/download/alloy-installer-windows-amd64.exe.zip'

    if (-not (Invoke-ServerAbruf -Pfad 'alloy-installer-windows-amd64.exe.zip' -Zieldatei $zip -Ersatzquelle $ersatz)) {
        Schreibe 'Alloy konnte nicht geladen werden – die Ereignisprotokolle werden vorerst nicht gesammelt.' -Art Warnung
        return
    }

    $entpackt = Join-Path $ArbeitsPfad 'alloy'
    if (Test-Path -LiteralPath $entpackt) { Remove-Item -LiteralPath $entpackt -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $entpackt -Force

    $installer = Get-ChildItem -LiteralPath $entpackt -Filter '*.exe' -Recurse |
        Select-Object -First 1
    if (-not $installer) {
        Schreibe 'Im Alloy-Paket wurde kein Installationsprogramm gefunden.' -Art Warnung
        return
    }

    Schreibe 'Grafana Alloy wird installiert'
    $vorgang = Start-Process -FilePath $installer.FullName -ArgumentList '/S' -Wait -PassThru -NoNewWindow
    if ($vorgang.ExitCode -ne 0) {
        Schreibe "Die Alloy-Installation meldet Code $($vorgang.ExitCode)" -Art Warnung
    }

    # --- Konfiguration holen und auf dieses Geraet anpassen ------------
    $konfigZiel = 'C:\Program Files\GrafanaLabs\Alloy\config.alloy'
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $konfigZiel))) {
        Schreibe 'Alloy wurde nicht am erwarteten Ort installiert – Konfiguration wird uebersprungen.' -Art Warnung
        return
    }

    $vorlage = Join-Path $ArbeitsPfad 'alloy-client.alloy'
    try {
        $kopf = @{ 'X-Agent-Token' = $Token }
        Invoke-WebRequest -Uri "$ServerUrl/mon/api/v1/config/alloy-client" -OutFile $vorlage `
            -Headers $kopf -UseBasicParsing -TimeoutSec 60
    } catch {
        Schreibe "Alloy-Konfiguration nicht abrufbar: $($_.Exception.Message)" -Art Warnung
        return
    }

    $inhalt = Get-Content -LiteralPath $vorlage -Raw
    $inhalt = $inhalt.Replace('__GERAET__',   $env:COMPUTERNAME.ToLower())
    $inhalt = $inhalt.Replace('__ROLLE__',    $System.Rolle)
    $inhalt = $inhalt.Replace('__STANDORT__', $Standort)

    New-Item -ItemType Directory -Path "$BasisPfad\alloy" -Force | Out-Null
    Set-Content -LiteralPath $konfigZiel -Value $inhalt -Encoding UTF8

    $dienst = Get-Service -Name 'Alloy' -ErrorAction SilentlyContinue
    if ($dienst) {
        Set-Service -Name 'Alloy' -StartupType Automatic
        Restart-Service -Name 'Alloy' -Force -ErrorAction SilentlyContinue
        & sc.exe failure Alloy reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null
        Schreibe 'Alloy laeuft und sammelt die Ereignisprotokolle' -Art Erfolg
    } else {
        Schreibe 'Der Alloy-Dienst wurde nicht gefunden.' -Art Warnung
    }
}

function Install-Hardwarepruefung {
    $skriptZiel = Join-Path $BasisPfad 'Collect-HardwareHealth.ps1'

    if (-not (Invoke-ServerAbruf -Pfad 'Collect-HardwareHealth.ps1' -Zieldatei $skriptZiel)) {
        # Liegt das Skript neben diesem hier (SYSVOL-Ordner), reicht das auch
        $daneben = Join-Path $PSScriptRoot 'Collect-HardwareHealth.ps1'
        if (Test-Path -LiteralPath $daneben) {
            Copy-Item -LiteralPath $daneben -Destination $skriptZiel -Force
        } else {
            Schreibe 'Collect-HardwareHealth.ps1 wurde nicht gefunden – die Hardwarepruefung entfaellt.' -Art Warnung
            return
        }
    }

    Unblock-File -LiteralPath $skriptZiel -ErrorAction SilentlyContinue

    # --- Geplante Aufgabe: alle fuenf Minuten ------------------------
    $aktion = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$skriptZiel`" -AusgabePfad `"$TextdateiPfad`" -ServerUrl `"$ServerUrl`" -Token `"$Token`""

    $ausloeser = New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
        -RepetitionInterval (New-TimeSpan -Minutes 5)

    $start = New-ScheduledTaskTrigger -AtStartup

    $konto = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

    $einstellungen = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
        -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 5)

    Unregister-ScheduledTask -TaskName $AufgabenName -Confirm:$false -ErrorAction SilentlyContinue

    Register-ScheduledTask -TaskName $AufgabenName `
        -Action $aktion -Trigger @($ausloeser, $start) -Principal $konto `
        -Settings $einstellungen `
        -Description 'Erfasst Hardwarezustand, Sicherheitseinstellungen und Datensicherung fuer das Schul-Monitoring.' | Out-Null

    Schreibe 'Geplante Aufgabe fuer die Hardwarepruefung eingerichtet' -Art Erfolg

    # Einmal sofort ausfuehren, damit gleich Daten da sind
    Start-ScheduledTask -TaskName $AufgabenName -ErrorAction SilentlyContinue
}

function Install-Sicherheitspruefung {
    <#
        Prueft die Grundhaertung des Geraets gegen die ueblichen
        Empfehlungen (CIS, BSI-Grundschutz, Microsoft Security Baseline).

        Stuendlich statt alle fuenf Minuten: Diese Einstellungen aendern
        sich selten, die AD-Abfragen auf einem Domaenencontroller kosten
        aber spuerbar mehr als eine Registry-Abfrage.
    #>
    $skriptZiel = Join-Path $BasisPfad 'Collect-SecurityBaseline.ps1'

    if (-not (Invoke-ServerAbruf -Pfad 'Collect-SecurityBaseline.ps1' -Zieldatei $skriptZiel)) {
        $daneben = Join-Path $PSScriptRoot 'Collect-SecurityBaseline.ps1'
        if (Test-Path -LiteralPath $daneben) {
            Copy-Item -LiteralPath $daneben -Destination $skriptZiel -Force
        } else {
            Schreibe 'Collect-SecurityBaseline.ps1 wurde nicht gefunden – die Baseline-Pruefung entfaellt.' -Art Warnung
            return
        }
    }

    Unblock-File -LiteralPath $skriptZiel -ErrorAction SilentlyContinue

    $aktion = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$skriptZiel`" -AusgabePfad `"$TextdateiPfad`""

    # Zufaelliger Versatz, damit nicht alle Geraete der Schule zur vollen
    # Stunde gleichzeitig den Domaenencontroller befragen.
    $versatz = Get-Random -Minimum 0 -Maximum 55
    $ausloeser = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes($versatz) `
        -RepetitionInterval (New-TimeSpan -Hours 1)

    $start = New-ScheduledTaskTrigger -AtStartup

    $konto = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

    $einstellungen = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 20) `
        -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 10)

    Unregister-ScheduledTask -TaskName $AufgabeSicherheit -Confirm:$false -ErrorAction SilentlyContinue

    Register-ScheduledTask -TaskName $AufgabeSicherheit `
        -Action $aktion -Trigger @($ausloeser, $start) -Principal $konto `
        -Settings $einstellungen `
        -Description 'Prueft die Sicherheits-Grundhaertung des Geraets fuer das Schul-Monitoring.' | Out-Null

    Schreibe 'Geplante Aufgabe fuer die Sicherheits-Baseline eingerichtet' -Art Erfolg

    Start-ScheduledTask -TaskName $AufgabeSicherheit -ErrorAction SilentlyContinue
}

function Install-AdPruefung {
    <#
        Betriebszustand des Active Directory: Replikation, SYSVOL, FSMO,
        LDAP-Antwortzeit, Kontosperrungen.

        Wird ausschliesslich auf Domaenencontrollern eingerichtet – auf
        allen anderen Geraeten waere es sinnlose Last. Alle 15 Minuten:
        Eine gebrochene Replikation will man nicht erst nach einer Stunde
        sehen, oefter als alle 15 Minuten aendert sich der Zustand aber
        auch nicht.
    #>
    param([Parameter(Mandatory)]$System)

    if ($System.Rolle -ne 'dc') {
        # Eine frueher eingerichtete Aufgabe entfernen, falls das Geraet
        # einmal ein DC war und heruntergestuft wurde.
        Unregister-ScheduledTask -TaskName $AufgabeAd -Confirm:$false -ErrorAction SilentlyContinue
        return
    }

    $skriptZiel = Join-Path $BasisPfad 'Collect-AdHealth.ps1'

    if (-not (Invoke-ServerAbruf -Pfad 'Collect-AdHealth.ps1' -Zieldatei $skriptZiel)) {
        $daneben = Join-Path $PSScriptRoot 'Collect-AdHealth.ps1'
        if (Test-Path -LiteralPath $daneben) {
            Copy-Item -LiteralPath $daneben -Destination $skriptZiel -Force
        } else {
            Schreibe 'Collect-AdHealth.ps1 wurde nicht gefunden – die AD-Pruefung entfaellt.' -Art Warnung
            return
        }
    }

    Unblock-File -LiteralPath $skriptZiel -ErrorAction SilentlyContinue

    $aktion = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$skriptZiel`" -AusgabePfad `"$TextdateiPfad`""

    # Versatz auch hier: In einer Domaene mit mehreren DCs sollen die
    # Replikationsabfragen nicht im Gleichschritt laufen.
    $versatz = Get-Random -Minimum 0 -Maximum 14
    $ausloeser = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes($versatz) `
        -RepetitionInterval (New-TimeSpan -Minutes 15)

    $start = New-ScheduledTaskTrigger -AtStartup

    $konto = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

    $einstellungen = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
        -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 5)

    Unregister-ScheduledTask -TaskName $AufgabeAd -Confirm:$false -ErrorAction SilentlyContinue

    Register-ScheduledTask -TaskName $AufgabeAd `
        -Action $aktion -Trigger @($ausloeser, $start) -Principal $konto `
        -Settings $einstellungen `
        -Description 'Prueft Replikation, SYSVOL, FSMO und LDAP fuer das Schul-Monitoring.' | Out-Null

    Schreibe 'Geplante Aufgabe fuer die AD-Betriebspruefung eingerichtet' -Art Erfolg

    Start-ScheduledTask -TaskName $AufgabeAd -ErrorAction SilentlyContinue
}

function Set-Firewallregel {
    $regelName = 'Schul-Monitoring – Metrikabruf'

    try {
        $serverName = ([uri]$ServerUrl).Host
        $serverIp = if ($serverName -match '^\d{1,3}(\.\d{1,3}){3}$') {
            $serverName
        } else {
            (Resolve-DnsName -Name $serverName -Type A -ErrorAction Stop |
                Select-Object -First 1).IPAddress
        }
    } catch {
        $serverIp = $null
    }

    Get-NetFirewallRule -DisplayName $regelName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue

    $parameter = @{
        DisplayName = $regelName
        Direction   = 'Inbound'
        Action      = 'Allow'
        Protocol    = 'TCP'
        LocalPort   = $ExporterPort
        Profile     = 'Any'
        Description = 'Erlaubt dem Monitoring-Server das Abrufen der Messwerte.'
    }

    # Bewusst nur fuer den Monitoring-Server oeffnen, nicht fuer das ganze Netz.
    if ($serverIp) { $parameter['RemoteAddress'] = $serverIp }

    New-NetFirewallRule @parameter | Out-Null

    if ($serverIp) {
        Schreibe "Firewall fuer Port $ExporterPort geoeffnet – nur fuer $serverIp" -Art Erfolg
    } else {
        Schreibe "Firewall fuer Port $ExporterPort geoeffnet (Serveradresse nicht aufloesbar)" -Art Warnung
    }
}

function Register-BeimServer {
    param([object]$System)

    $seriennummer = try { (Get-CimInstance Win32_BIOS).SerialNumber } catch { '' }

    $daten = @{
        hostname       = $env:COMPUTERNAME.ToLower()
        ip             = Get-EigeneIpAdresse
        rolle          = $System.Rolle
        plattform      = 'windows'
        betriebssystem = $System.Betriebssystem
        domaene        = $System.Domaene
        raum           = $Raum
        standort       = $Standort
        agent_version  = $AgentVersion
        exporter_port  = $ExporterPort
        modell         = $System.Modell
        seriennummer   = $seriennummer
    }

    $koerper = $daten | ConvertTo-Json -Compress
    $kopf = @{ 'X-Agent-Token' = $Token; 'Content-Type' = 'application/json' }

    for ($versuch = 1; $versuch -le 5; $versuch++) {
        try {
            $antwort = Invoke-RestMethod -Uri "$ServerUrl/mon/api/v1/register" -Method Post `
                -Body $koerper -Headers $kopf -TimeoutSec 30
            Schreibe "Beim Monitoring-Server angemeldet: $($antwort.geraet)" -Art Erfolg
            Schreibe "Dashboard: $($antwort.dashboard)"
            return $true
        } catch {
            $wartezeit = [math]::Min(60, [math]::Pow(2, $versuch) * 2)
            Schreibe "Anmeldung fehlgeschlagen (Versuch $versuch/5): $($_.Exception.Message)" -Art Warnung
            if ($versuch -lt 5) { Start-Sleep -Seconds $wartezeit }
        }
    }

    Schreibe 'Die Anmeldung beim Monitoring-Server hat nicht geklappt.' -Art Fehler
    Schreibe 'Der Agent laeuft trotzdem – beim naechsten Neustart wird es erneut versucht.' -Art Warnung
    return $false
}

# ===================================================================== #
# Ablauf
# ===================================================================== #
try {
    Schreibe '====================================================='
    Schreibe "Schul-Monitoring Agent $AgentVersion auf $env:COMPUTERNAME"

    if (Test-AgentAktuell) {
        Schreibe 'Agent ist bereits aktuell – nur Lebenszeichen senden' -Art Erfolg
        try {
            $kopf = @{ 'X-Agent-Token' = $Token; 'Content-Type' = 'application/json' }
            $koerper = @{ hostname = $env:COMPUTERNAME.ToLower() } | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri "$ServerUrl/mon/api/v1/heartbeat" -Method Post `
                -Body $koerper -Headers $kopf -TimeoutSec 15 | Out-Null
        } catch {
            # Ist das Geraet dem Server unbekannt, melden wir uns neu an
            Schreibe 'Lebenszeichen abgelehnt – Geraet wird neu angemeldet' -Art Warnung
            $system = Get-SystemRolle
            Register-BeimServer -System $system | Out-Null
        }
        exit 0
    }

    $system = Get-SystemRolle
    Schreibe "Erkannt: $($system.Betriebssystem) – Rolle '$($system.Rolle)'"
    if ($system.Modell) { Schreibe "Hardware: $($system.Modell)" }

    Install-WindowsExporter -System $system
    Set-Firewallregel
    Install-Hardwarepruefung
    Install-Sicherheitspruefung
    Install-AdPruefung -System $system
    Install-Alloy -System $system
    Register-BeimServer -System $system | Out-Null

    Set-Content -LiteralPath $VersionsDatei -Value $AgentVersion -Encoding ASCII

    # Aufraeumen
    Remove-Item -LiteralPath "$ArbeitsPfad\*" -Recurse -Force -ErrorAction SilentlyContinue

    Schreibe "Fertig – dieses Geraet meldet sich jetzt automatisch im Dashboard." -Art Erfolg
    exit 0
}
catch {
    Schreibe "Abbruch: $($_.Exception.Message)" -Art Fehler
    Schreibe $_.ScriptStackTrace -Art Fehler
    exit 1
}
