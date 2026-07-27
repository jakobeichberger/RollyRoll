#Requires -Version 5.1
<#
.SYNOPSIS
    Erfasst Hardwarezustand, Sicherheitseinstellungen und Datensicherung
    und legt das Ergebnis als Prometheus-Messwerte ab.

.DESCRIPTION
    windows_exporter liefert Last, Speicher und Dienste. Was fehlt, sind
    genau die Dinge, die einen Ausfall ANKUENDIGEN:

      * S.M.A.R.T.-Ausfallvorhersage und SSD-Verschleiss
      * RAID-Verbuende und Cache-Batterien
      * WHEA-Hardwarefehler (defekter RAM, CPU, PCIe)
      * Temperaturen, Luefter, Netzteile, Akkus
      * Virenschutz, BitLocker, Firewall, Patchstand
      * Datensicherung und Hyper-V-Zustand
      * ablaufende Zertifikate

    Das Skript wird alle fuenf Minuten von einer geplanten Aufgabe
    aufgerufen und schreibt eine .prom-Datei, die windows_exporter ueber
    seinen textfile-Sammler mit ausliefert.

    Jeder Abschnitt laeuft in einem eigenen try/catch: Wenn ein Geraet
    eine bestimmte Abfrage nicht beantwortet – etwa weil es keinen
    Temperaturfuehler hat – fehlt nur dieser eine Wert, alles andere
    kommt trotzdem an.

.PARAMETER AusgabePfad
    Verzeichnis, das windows_exporter als textfile-Verzeichnis liest.

.PARAMETER ServerUrl
    Optional: Adresse des Monitoring-Servers fuer das Lebenszeichen.

.PARAMETER Token
    Optional: Agent-Token fuer das Lebenszeichen.
#>
[CmdletBinding()]
param(
    [string]$AusgabePfad = "$env:ProgramData\SchulMonitoring\textfile",
    [string]$ServerUrl = '',
    [string]$Token = ''
)

# Version 1.0 statt Latest: faengt Tippfehler bei Variablennamen ab, steigt
# aber nicht aus, wenn ein WMI-Objekt eine Eigenschaft nicht mitbringt –
# und das ist bei Hardwareabfragen quer ueber alle Geraete der Normalfall.
Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$Beginn = Get-Date
$Zeilen = [System.Collections.Generic.List[string]]::new()
$BekannteMetriken = [System.Collections.Generic.HashSet[string]]::new()

# ===================================================================== #
# Ausgabe im Prometheus-Textformat
# ===================================================================== #
function Add-Metrik {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][double]$Wert,
        [hashtable]$Labels = @{},
        [string]$Hilfe = '',
        [ValidateSet('gauge', 'counter')][string]$Typ = 'gauge'
    )

    if ($BekannteMetriken.Add($Name)) {
        if ($Hilfe) { $Zeilen.Add("# HELP $Name $Hilfe") }
        $Zeilen.Add("# TYPE $Name $Typ")
    }

    if ($Labels.Count -gt 0) {
        $teile = foreach ($schluessel in ($Labels.Keys | Sort-Object)) {
            $wertText = [string]$Labels[$schluessel]
            # Nach Prometheus-Vorgabe muessen \, " und Zeilenumbrueche maskiert werden
            $wertText = $wertText -replace '\\', '\\\\' -replace '"', '\"' -replace "`r?`n", ' '
            $wertText = $wertText.Trim()
            if ($wertText.Length -gt 120) { $wertText = $wertText.Substring(0, 120) }
            '{0}="{1}"' -f $schluessel, $wertText
        }
        $Zeilen.Add(('{0}{{{1}}} {2}' -f $Name, ($teile -join ','), $Wert.ToString([Globalization.CultureInfo]::InvariantCulture)))
    } else {
        $Zeilen.Add(('{0} {1}' -f $Name, $Wert.ToString([Globalization.CultureInfo]::InvariantCulture)))
    }
}

function Invoke-Abschnitt {
    <# Fuehrt einen Abschnitt aus und vermerkt Fehler als eigene Metrik,
       statt den ganzen Durchlauf abzubrechen. #>
    param([string]$Name, [scriptblock]$Aktion)
    try {
        & $Aktion
        Add-Metrik -Name 'schule_pruefabschnitt_erfolgreich' -Wert 1 -Labels @{ abschnitt = $Name } `
            -Hilfe 'Ob der jeweilige Pruefabschnitt durchgelaufen ist'
    } catch {
        Add-Metrik -Name 'schule_pruefabschnitt_erfolgreich' -Wert 0 -Labels @{ abschnitt = $Name }
        Write-Verbose "Abschnitt '$Name' fehlgeschlagen: $($_.Exception.Message)"
    }
}

# ===================================================================== #
# Datentraeger: S.M.A.R.T., Zustand, Verschleiss, Temperatur
# ===================================================================== #
Invoke-Abschnitt 'datentraeger' {

    # --- S.M.A.R.T.-Ausfallvorhersage ---------------------------------
    # Das ist die wertvollste Vorwarnung ueberhaupt: Die Platte meldet
    # selbst, dass sie demnaechst ausfaellt.
    $vorhersagen = Get-CimInstance -Namespace 'root\wmi' `
        -ClassName 'MSStorageDriver_FailurePredictStatus' -ErrorAction SilentlyContinue

    foreach ($eintrag in $vorhersagen) {
        $kennung = $eintrag.InstanceName
        Add-Metrik -Name 'schule_datentraeger_ausfall_vorhersage' `
            -Wert ([int][bool]$eintrag.PredictFailure) `
            -Labels @{ geraetekennung = $kennung } `
            -Hilfe '1 = S.M.A.R.T. sagt einen Ausfall des Datentraegers voraus'
    }

    # --- Zustand, Verschleiss, Temperatur -----------------------------
    $platten = Get-PhysicalDisk -ErrorAction SilentlyContinue
    foreach ($platte in $platten) {

        $labels = @{
            modell       = if ($platte.FriendlyName) { $platte.FriendlyName } else { 'unbekannt' }
            seriennummer = if ($platte.SerialNumber) { $platte.SerialNumber.Trim() } else { '' }
            typ          = [string]$platte.MediaType
        }

        $zustandswert = switch ([string]$platte.HealthStatus) {
            'Healthy'   { 0 }
            'Warning'   { 1 }
            'Unhealthy' { 2 }
            default     { 3 }
        }
        Add-Metrik -Name 'schule_datentraeger_zustand' -Wert $zustandswert `
            -Labels ($labels + @{ zustand = [string]$platte.HealthStatus }) `
            -Hilfe '0 = gesund, 1 = Warnung, 2 = defekt, 3 = unbekannt'

        # Zuverlaessigkeitszaehler liefern nicht alle Controller
        $zaehler = $platte | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
        if ($zaehler) {
            if ($null -ne $zaehler.Wear) {
                Add-Metrik -Name 'schule_datentraeger_verschleiss_prozent' -Wert ([double]$zaehler.Wear) `
                    -Labels $labels `
                    -Hilfe 'Aufgebrauchte Lebensdauer einer SSD in Prozent (100 = Ende der Garantie)'
            }
            if ($zaehler.Temperature -and $zaehler.Temperature -gt 0 -and $zaehler.Temperature -lt 150) {
                Add-Metrik -Name 'schule_datentraeger_temperatur_celsius' `
                    -Wert ([double]$zaehler.Temperature) -Labels $labels `
                    -Hilfe 'Temperatur des Datentraegers in Grad Celsius'
            }
            if ($null -ne $zaehler.PowerOnHours) {
                Add-Metrik -Name 'schule_datentraeger_betriebsstunden' `
                    -Wert ([double]$zaehler.PowerOnHours) -Labels $labels `
                    -Hilfe 'Betriebsstunden des Datentraegers'
            }
            if ($null -ne $zaehler.ReadErrorsUncorrected) {
                Add-Metrik -Name 'schule_datentraeger_lesefehler' `
                    -Wert ([double]$zaehler.ReadErrorsUncorrected) -Labels $labels `
                    -Hilfe 'Nicht korrigierbare Lesefehler' -Typ counter
            }
        }
    }

    # --- Storage Spaces / virtuelle Datentraeger ----------------------
    $virtuelle = Get-VirtualDisk -ErrorAction SilentlyContinue
    foreach ($verbund in $virtuelle) {
        $wert = switch ([string]$verbund.HealthStatus) {
            'Healthy' { 0 } 'Warning' { 1 } 'Unhealthy' { 2 } default { 3 }
        }
        Add-Metrik -Name 'schule_raid_zustand' -Wert $wert `
            -Labels @{
                controller = 'Speicherplatz'
                verbund    = [string]$verbund.FriendlyName
                zustand    = [string]$verbund.HealthStatus
            } `
            -Hilfe '0 = optimal, 1 = Warnung, 2 = defekt, 3 = unbekannt'
    }
}

# ===================================================================== #
# Hardware-RAID-Controller
# ===================================================================== #
Invoke-Abschnitt 'raid' {

    # --- Broadcom/LSI MegaRAID, Dell PERC -----------------------------
    $werkzeuge = @(
        'C:\Program Files\Dell\perccli\perccli64.exe'
        'C:\Program Files\MegaRAID Storage Manager\storcli64.exe'
        'C:\Program Files\storcli\storcli64.exe'
        'C:\storcli\storcli64.exe'
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if ($werkzeuge) {
        $rohdaten = & $werkzeuge '/call' 'show' 'J' 2>$null | Out-String
        if ($rohdaten) {
            $daten = $rohdaten | ConvertFrom-Json
            foreach ($antwort in $daten.Controllers) {
                $inhalt = $antwort.'Response Data'
                if (-not $inhalt) { continue }

                $controllerName = if ($inhalt.'Basics'.Model) { $inhalt.'Basics'.Model } else { 'RAID-Controller' }

                foreach ($verbund in @($inhalt.'VD LIST')) {
                    if (-not $verbund) { continue }
                    $zustand = [string]$verbund.State
                    # Optl = optimal, Dgrd = degradiert, Pdgd = teildegradiert, Offln = offline
                    $wert = switch -Wildcard ($zustand) {
                        'Optl*'  { 0 }
                        'Pdgd*'  { 1 }
                        'Dgrd*'  { 2 }
                        'Offln*' { 2 }
                        default  { 3 }
                    }
                    Add-Metrik -Name 'schule_raid_zustand' -Wert $wert `
                        -Labels @{
                            controller = $controllerName
                            verbund    = [string]$verbund.'DG/VD'
                            zustand    = $zustand
                        }
                }

                # Cache-Batterie: ohne sie faellt der Schreibcache weg
                foreach ($batterie in @($inhalt.'BBU_Info')) {
                    if (-not $batterie) { continue }
                    $bbuZustand = [string]$batterie.State
                    Add-Metrik -Name 'schule_raid_bbu_zustand' `
                        -Wert $(if ($bbuZustand -match 'Optimal|Healthy') { 0 } else { 1 }) `
                        -Labels @{ controller = $controllerName; zustand = $bbuZustand } `
                        -Hilfe '0 = Cache-Batterie in Ordnung, 1 = Fehler'
                }
            }
        }
    }

    # --- HPE Smart Array ----------------------------------------------
    $ssacli = @(
        'C:\Program Files\Smart Storage Administrator\ssacli\bin\ssacli.exe'
        'C:\Program Files\Compaq\Hpacucli\Bin\hpacucli.exe'
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if ($ssacli) {
        $ausgabe = & $ssacli 'ctrl' 'all' 'show' 'config' 2>$null | Out-String
        foreach ($zeile in ($ausgabe -split "`r?`n")) {
            if ($zeile -match 'logicaldrive\s+(\S+)\s+\([^)]*?,\s*([^,)]+)\)') {
                $verbund = $Matches[1]
                $zustand = $Matches[2].Trim()
                Add-Metrik -Name 'schule_raid_zustand' `
                    -Wert $(if ($zustand -match 'OK') { 0 } elseif ($zustand -match 'Recover|Rebuild') { 1 } else { 2 }) `
                    -Labels @{ controller = 'HPE Smart Array'; verbund = $verbund; zustand = $zustand }
            }
        }
    }
}

# ===================================================================== #
# Hardware-Fehlerereignisse aus dem Systemprotokoll
# ===================================================================== #
Invoke-Abschnitt 'ereignisse' {

    $seit = (Get-Date).AddHours(-24)

    # WHEA meldet Fehler von CPU, Arbeitsspeicher und PCIe. Auch die als
    # "korrigiert" gemeldeten Faelle sind ein ernstes Vorzeichen.
    $whea = Get-WinEvent -FilterHashtable @{
        LogName      = 'System'
        ProviderName = 'Microsoft-Windows-WHEA-Logger'
        StartTime    = $seit
    } -ErrorAction SilentlyContinue

    Add-Metrik -Name 'schule_whea_fehler_24h' -Wert (@($whea).Count) `
        -Labels @{ quelle = 'WHEA' } `
        -Hilfe 'Hardwarefehler laut WHEA in den letzten 24 Stunden (CPU, RAM, PCIe)'

    # Datentraegerfehler: 7 = fehlerhafter Block, 11 = Controllerfehler,
    # 51 = Fehler beim Auslagern, 153 = Anforderung abgebrochen
    $platte = Get-WinEvent -FilterHashtable @{
        LogName   = 'System'
        ProviderName = @('disk', 'Disk')
        StartTime = $seit
        Level     = @(1, 2, 3)
    } -ErrorAction SilentlyContinue

    Add-Metrik -Name 'schule_hardware_ereignisse_24h' -Wert (@($platte).Count) `
        -Labels @{ quelle = 'Datentraeger' } `
        -Hilfe 'Hardwarenahe Fehlereintraege der letzten 24 Stunden'

    # Unerwartete Neustarts
    $abstuerze = Get-WinEvent -FilterHashtable @{
        LogName = 'System'
        Id      = @(41, 1001, 6008)
        StartTime = $seit
    } -ErrorAction SilentlyContinue

    Add-Metrik -Name 'schule_hardware_ereignisse_24h' -Wert (@($abstuerze).Count) `
        -Labels @{ quelle = 'Absturz' }

    # War der letzte Neustart geplant? 1074 = angefordertes Herunterfahren
    $geplant = Get-WinEvent -FilterHashtable @{
        LogName   = 'System'
        Id        = 1074
        StartTime = (Get-Date).AddHours(-2)
    } -ErrorAction SilentlyContinue

    Add-Metrik -Name 'schule_letzter_neustart_geplant' -Wert $(if (@($geplant).Count -gt 0) { 1 } else { 0 }) `
        -Hilfe '1 = der letzte Neustart wurde angefordert, 0 = unerwartet'
}

# ===================================================================== #
# Temperatur, Luefter, Netzteil, Akku
# ===================================================================== #
Invoke-Abschnitt 'sensoren' {

    # --- ACPI-Temperaturfuehler ---------------------------------------
    $temperaturen = Get-CimInstance -Namespace 'root\wmi' `
        -ClassName 'MSAcpi_ThermalZoneTemperature' -ErrorAction SilentlyContinue

    foreach ($fuehler in $temperaturen) {
        # Der Wert kommt in Zehntel-Kelvin
        $celsius = [math]::Round(($fuehler.CurrentTemperature / 10) - 273.15, 1)
        if ($celsius -gt 0 -and $celsius -lt 150) {
            $name = ($fuehler.InstanceName -split '\\')[-1]
            Add-Metrik -Name 'schule_temperatur_celsius' -Wert $celsius `
                -Labels @{ sensor = $name } `
                -Hilfe 'Temperatur in Grad Celsius'
        }
    }

    # --- Luefter ------------------------------------------------------
    # Die Drehzahl (DesiredSpeed) meldet kaum ein Geraet, der Status dagegen
    # schon. Deshalb wird der Status ausgewertet – sonst gaebe es auf fast
    # jedem Rechner einen Fehlalarm "Luefter steht still".
    $luefter = Get-CimInstance -ClassName 'Win32_Fan' -ErrorAction SilentlyContinue
    foreach ($l in $luefter) {
        $status = [string]$l.Status
        if (-not $status) { continue }
        Add-Metrik -Name 'schule_luefter_zustand' `
            -Wert $(if ($status -match '^(OK|Ok)$') { 0 } else { 1 }) `
            -Labels @{ sensor = [string]$l.DeviceID; zustand = $status } `
            -Hilfe '0 = Luefter in Ordnung, 1 = Stoerung'

        if ($l.DesiredSpeed -and $l.DesiredSpeed -gt 0) {
            Add-Metrik -Name 'schule_luefter_umdrehungen' -Wert ([double]$l.DesiredSpeed) `
                -Labels @{ sensor = [string]$l.DeviceID } `
                -Hilfe 'Luefterdrehzahl, sofern das Geraet sie meldet'
        }
    }

    # --- Netzteile ----------------------------------------------------
    # Win32_PowerSupply liefert bei vielen Servern nichts; wo es Werte
    # gibt, sind sie aber sehr aussagekraeftig.
    $netzteile = Get-CimInstance -ClassName 'Win32_PowerSupply' -ErrorAction SilentlyContinue
    foreach ($nt in $netzteile) {
        # Availability 3 = laeuft, alles andere ist auffaellig
        Add-Metrik -Name 'schule_netzteil_zustand' `
            -Wert $(if ($nt.Availability -eq 3) { 0 } else { 1 }) `
            -Labels @{ sensor = [string]$nt.DeviceID } `
            -Hilfe '0 = Netzteil in Ordnung, 1 = Fehler'
    }

    # --- Akku (Notebooks) ---------------------------------------------
    $akkus = Get-CimInstance -ClassName 'Win32_Battery' -ErrorAction SilentlyContinue
    if ($akkus) {
        $ladung = @($akkus)[0].EstimatedChargeRemaining
        if ($null -ne $ladung) {
            Add-Metrik -Name 'schule_akku_ladung_prozent' -Wert ([double]$ladung) `
                -Hilfe 'Akkuladung in Prozent'
        }

        $voll = Get-CimInstance -Namespace 'root\wmi' -ClassName 'BatteryFullChargedCapacity' -ErrorAction SilentlyContinue
        $ausgelegt = Get-CimInstance -Namespace 'root\wmi' -ClassName 'BatteryStaticData' -ErrorAction SilentlyContinue
        if ($voll -and $ausgelegt) {
            $ist = @($voll)[0].FullChargedCapacity
            $soll = @($ausgelegt)[0].DesignedCapacity
            if ($soll -gt 0 -and $ist -gt 0) {
                $verschleiss = [math]::Round(100 - (100 * $ist / $soll), 1)
                Add-Metrik -Name 'schule_akku_verschleiss_prozent' -Wert ([math]::Max(0, $verschleiss)) `
                    -Hilfe 'Verlorene Akkukapazitaet gegenueber dem Neuzustand in Prozent'
            }
        }
    }
}

# ===================================================================== #
# Sicherheitshygiene
# ===================================================================== #
Invoke-Abschnitt 'sicherheit' {

    # --- Microsoft Defender -------------------------------------------
    if (Get-Command Get-MpComputerStatus -ErrorAction SilentlyContinue) {
        $defender = Get-MpComputerStatus -ErrorAction SilentlyContinue
        if ($defender) {
            Add-Metrik -Name 'schule_defender_echtzeitschutz' `
                -Wert ([int][bool]$defender.RealTimeProtectionEnabled) `
                -Hilfe '1 = Echtzeitschutz aktiv'

            Add-Metrik -Name 'schule_defender_dienst_aktiv' `
                -Wert ([int][bool]$defender.AMServiceEnabled)

            if ($null -ne $defender.AntivirusSignatureAge) {
                Add-Metrik -Name 'schule_defender_signatur_alter_tage' `
                    -Wert ([double]$defender.AntivirusSignatureAge) `
                    -Hilfe 'Alter der Virensignaturen in Tagen'
            }

            $scanAlter = @($defender.QuickScanAge, $defender.FullScanAge) |
                Where-Object { $null -ne $_ -and $_ -lt 4000000 } |
                Measure-Object -Minimum
            if ($scanAlter.Count -gt 0) {
                Add-Metrik -Name 'schule_defender_letzter_scan_tage' -Wert ([double]$scanAlter.Minimum) `
                    -Hilfe 'Tage seit dem letzten Virenscan'
            }
        }
    }

    # --- Windows-Firewall ---------------------------------------------
    $firewallProfile = Get-NetFirewallProfile -ErrorAction SilentlyContinue
    foreach ($p in $firewallProfile) {
        Add-Metrik -Name 'schule_firewall_aktiv' -Wert ([int][bool]$p.Enabled) `
            -Labels @{ profil = [string]$p.Name } `
            -Hilfe '1 = Firewall-Profil aktiv'
    }

    # --- BitLocker ----------------------------------------------------
    if (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) {
        $laufwerke = Get-BitLockerVolume -ErrorAction SilentlyContinue
        foreach ($lw in $laufwerke) {
            if (-not $lw.MountPoint) { continue }
            Add-Metrik -Name 'schule_bitlocker_geschuetzt' `
                -Wert $(if ($lw.ProtectionStatus -eq 'On') { 1 } else { 0 }) `
                -Labels @{ volume = [string]$lw.MountPoint } `
                -Hilfe '1 = Laufwerk ist verschluesselt'
        }
    }

    # --- Patchstand ----------------------------------------------------
    $letztesUpdate = $null
    $schluessel = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install'
    if (Test-Path -LiteralPath $schluessel) {
        $roh = (Get-ItemProperty -LiteralPath $schluessel -ErrorAction SilentlyContinue).LastSuccessTime
        if ($roh) { $letztesUpdate = [datetime]::Parse($roh, [Globalization.CultureInfo]::InvariantCulture) }
    }
    if (-not $letztesUpdate) {
        $hotfix = Get-CimInstance Win32_QuickFixEngineering -ErrorAction SilentlyContinue |
            Where-Object InstalledOn | Sort-Object InstalledOn -Descending | Select-Object -First 1
        if ($hotfix) { $letztesUpdate = $hotfix.InstalledOn }
    }
    if ($letztesUpdate) {
        Add-Metrik -Name 'schule_patch_alter_tage' `
            -Wert ([math]::Round(((Get-Date) - $letztesUpdate).TotalDays, 1)) `
            -Hilfe 'Tage seit der letzten erfolgreichen Updateinstallation'
    }

    # --- Ausstehender Neustart ----------------------------------------
    $neustartNoetig = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
    ) | Where-Object { Test-Path -LiteralPath $_ }

    $umbenennungen = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' `
        -Name PendingFileRenameOperations -ErrorAction SilentlyContinue)

    Add-Metrik -Name 'schule_neustart_ausstehend' `
        -Wert $(if ($neustartNoetig -or $umbenennungen) { 1 } else { 0 }) `
        -Hilfe '1 = ein Neustart steht aus (Updates werden erst danach wirksam)'

    # --- Zertifikate ---------------------------------------------------
    $zertifikate = Get-ChildItem -Path 'Cert:\LocalMachine\My' -ErrorAction SilentlyContinue |
        Where-Object { $_.NotAfter -lt (Get-Date).AddDays(365) } |
        Sort-Object NotAfter | Select-Object -First 20

    foreach ($zert in $zertifikate) {
        $betreff = ($zert.Subject -split ',')[0] -replace '^CN=', ''
        Add-Metrik -Name 'schule_zertifikat_ablauf_tage' `
            -Wert ([math]::Round(($zert.NotAfter - (Get-Date)).TotalDays, 1)) `
            -Labels @{ betreff = $betreff; speicher = 'LocalMachine\My' } `
            -Hilfe 'Tage bis zum Ablauf des Zertifikats'
    }
}

# ===================================================================== #
# Datensicherung
# ===================================================================== #
Invoke-Abschnitt 'sicherung' {

    # --- Windows Server-Sicherung -------------------------------------
    if (Get-Command Get-WBSummary -ErrorAction SilentlyContinue) {
        $zusammenfassung = Get-WBSummary -ErrorAction SilentlyContinue
        if ($zusammenfassung -and $zusammenfassung.LastBackupTime) {
            Add-Metrik -Name 'schule_sicherung_alter_stunden' `
                -Wert ([math]::Round(((Get-Date) - $zusammenfassung.LastBackupTime).TotalHours, 1)) `
                -Labels @{ auftrag = 'Windows Server-Sicherung' } `
                -Hilfe 'Stunden seit der letzten Datensicherung'

            Add-Metrik -Name 'schule_sicherung_erfolgreich' `
                -Wert $(if ($zusammenfassung.LastBackupResultHR -eq 0) { 1 } else { 0 }) `
                -Labels @{ auftrag = 'Windows Server-Sicherung' } `
                -Hilfe '1 = letzte Sicherung war erfolgreich'
        }
    }

    # --- Ueber das Ereignisprotokoll (deckt auch Veeam Agent ab) -------
    $sicherungsereignisse = Get-WinEvent -FilterHashtable @{
        LogName   = 'Application'
        StartTime = (Get-Date).AddDays(-7)
    } -MaxEvents 500 -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -match 'Veeam|Backup' }

    if ($sicherungsereignisse) {
        $letztes = $sicherungsereignisse | Sort-Object TimeCreated -Descending | Select-Object -First 1
        Add-Metrik -Name 'schule_sicherung_alter_stunden' `
            -Wert ([math]::Round(((Get-Date) - $letztes.TimeCreated).TotalHours, 1)) `
            -Labels @{ auftrag = [string]$letztes.ProviderName }

        # Level 1 und 2 sind kritisch bzw. Fehler
        Add-Metrik -Name 'schule_sicherung_erfolgreich' `
            -Wert $(if ($letztes.Level -le 2) { 0 } else { 1 }) `
            -Labels @{ auftrag = [string]$letztes.ProviderName }
    }
}

# ===================================================================== #
# Hyper-V
# ===================================================================== #
Invoke-Abschnitt 'hyperv' {

    if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { return }
    if (-not (Get-Service -Name vmms -ErrorAction SilentlyContinue)) { return }

    $vms = Get-VM -ErrorAction SilentlyContinue
    foreach ($vm in $vms) {
        $zustandswert = switch ([string]$vm.State) {
            'Off'      { 1 }
            'Running'  { 2 }
            'Paused'   { 3 }
            'Saved'    { 4 }
            default    { 0 }
        }

        # Eine VM, die automatisch starten soll, wird als "erwartet laufend"
        # gefuehrt – nur dann ist "aus" auch wirklich ein Problem.
        $erwartet = if ($vm.AutomaticStartAction -eq 'Start') { '1' } else { '0' }

        Add-Metrik -Name 'schule_hyperv_vm_zustand' -Wert $zustandswert `
            -Labels @{
                vm = [string]$vm.Name
                zustand = [string]$vm.State
                erwartet_laufend = $erwartet
            } `
            -Hilfe '1 = aus, 2 = laeuft, 3 = angehalten, 4 = gespeichert'

        # --- Alte Pruefpunkte -----------------------------------------
        $pruefpunkte = Get-VMSnapshot -VMName $vm.Name -ErrorAction SilentlyContinue
        if ($pruefpunkte) {
            $aeltester = $pruefpunkte | Sort-Object CreationTime | Select-Object -First 1
            Add-Metrik -Name 'schule_hyperv_pruefpunkt_alter_tage' `
                -Wert ([math]::Round(((Get-Date) - $aeltester.CreationTime).TotalDays, 1)) `
                -Labels @{ vm = [string]$vm.Name } `
                -Hilfe 'Alter des aeltesten Pruefpunkts – alte Pruefpunkte lassen die Datentraeger unbegrenzt wachsen'
        }
    }

    # --- Replikation ---------------------------------------------------
    $replikationen = Get-VMReplication -ErrorAction SilentlyContinue
    foreach ($rep in $replikationen) {
        $wert = switch ([string]$rep.Health) {
            'Normal'   { 0 }
            'Warning'  { 1 }
            'Critical' { 2 }
            default    { 3 }
        }
        Add-Metrik -Name 'schule_hyperv_replikation_zustand' -Wert $wert `
            -Labels @{ vm = [string]$rep.Name; zustand = [string]$rep.Health } `
            -Hilfe '0 = in Ordnung, 1 = Warnung, 2 = kritisch'
    }

    # --- Freier Arbeitsspeicher am Host --------------------------------
    $betriebssystem = Get-CimInstance Win32_OperatingSystem
    if ($betriebssystem.TotalVisibleMemorySize -gt 0) {
        Add-Metrik -Name 'schule_hyperv_host_speicher_frei_prozent' `
            -Wert ([math]::Round(100 * $betriebssystem.FreePhysicalMemory / $betriebssystem.TotalVisibleMemorySize, 1)) `
            -Hilfe 'Freier Arbeitsspeicher des Hyper-V-Hosts in Prozent'
    }
}

# ===================================================================== #
# Angaben zum Agenten selbst
# ===================================================================== #
$dauer = ((Get-Date) - $Beginn).TotalSeconds

try {
    $bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
    $rechner = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
    Add-Metrik -Name 'schule_agent_info' -Wert 1 `
        -Labels @{
            version      = '1.0.0'
            hersteller   = if ($rechner) { [string]$rechner.Manufacturer } else { '' }
            modell       = if ($rechner) { [string]$rechner.Model } else { '' }
            seriennummer = if ($bios) { [string]$bios.SerialNumber } else { '' }
            bios_version = if ($bios) { [string]$bios.SMBIOSBIOSVersion } else { '' }
        } `
        -Hilfe 'Angaben zum Geraet und zum Agenten'
} catch { }

Add-Metrik -Name 'schule_hardwarepruefung_dauer_sekunden' -Wert ([math]::Round($dauer, 2)) `
    -Hilfe 'Laufzeit der Hardwarepruefung'
Add-Metrik -Name 'schule_hardwarepruefung_zeitstempel' -Wert ([int64][System.DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) `
    -Hilfe 'Zeitpunkt der letzten Hardwarepruefung'

# ===================================================================== #
# Schreiben – erst in eine Nebendatei, dann umbenennen, damit
# windows_exporter nie eine halb geschriebene Datei liest
# ===================================================================== #
if (-not (Test-Path -LiteralPath $AusgabePfad)) {
    New-Item -ItemType Directory -Path $AusgabePfad -Force | Out-Null
}

$zieldatei = Join-Path $AusgabePfad 'schule_hardware.prom'
$zwischendatei = "$zieldatei.tmp"

# Prometheus erwartet Zeilenumbrueche nach Unix-Art und keine BOM
$inhalt = ($Zeilen -join "`n") + "`n"
[System.IO.File]::WriteAllText($zwischendatei, $inhalt, (New-Object System.Text.UTF8Encoding($false)))
Move-Item -LiteralPath $zwischendatei -Destination $zieldatei -Force

Write-Verbose "$($Zeilen.Count) Zeilen geschrieben nach $zieldatei"

# ===================================================================== #
# Lebenszeichen an den Server
# ===================================================================== #
if ($ServerUrl -and $Token) {
    try {
        $kopf = @{ 'X-Agent-Token' = $Token; 'Content-Type' = 'application/json' }
        $koerper = @{ hostname = $env:COMPUTERNAME.ToLower() } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$($ServerUrl.TrimEnd('/'))/mon/api/v1/heartbeat" -Method Post `
            -Body $koerper -Headers $kopf -TimeoutSec 15 | Out-Null
    } catch {
        Write-Verbose "Lebenszeichen nicht zugestellt: $($_.Exception.Message)"
    }
}
