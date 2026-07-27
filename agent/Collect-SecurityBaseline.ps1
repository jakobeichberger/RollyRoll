#Requires -Version 5.1
<#
.SYNOPSIS
    Prueft ein Windows-Geraet gegen eine Sicherheits-Grundhaertung und
    liefert das Ergebnis als Prometheus-Messwerte.

.DESCRIPTION
    Die Angriffserkennung im Schul-Monitoring beantwortet die Frage
    "passiert gerade etwas?". Dieses Skript beantwortet die andere,
    genauso wichtige Frage: "sind wir ueberhaupt richtig eingestellt?"

    Geprueft wird gegen die ueblichen Empfehlungen (CIS Benchmarks,
    BSI-Grundschutz, Microsoft Security Baseline) – aber bewusst nur die
    Punkte, die in einer Schule realistisch umsetzbar sind und einen
    echten Unterschied machen:

      * Zugangsdaten-Diebstahl  (LSA-Schutz, WDigest, NTLMv1, LLMNR)
      * Ausfuehrung             (PowerShell-Protokollierung, PS2, AutoRun)
      * Nachvollziehbarkeit     (Audit-Richtlinien, Protokollgroesse)
      * Virenschutz             (Manipulationsschutz, ASR, Netzwerkschutz)
      * Konten                  (Gastkonto, LAPS, lokale Administratoren)
      * Fernzugriff             (RDP mit Netzwerkauthentifizierung)
      * Verschluesselung        (veraltete TLS-Versionen, SMB-Signierung)
      * Active Directory        (krbtgt, AS-REP, Kerberoasting, Alt-Konten)

    Jede Pruefung liefert:
      1 = eingestellt wie empfohlen
      0 = Abweichung
      2 = auf diesem Geraet nicht pruefbar oder nicht zutreffend

    Der Wert 2 ist wichtig: Ein Standrechner hat kein Credential Guard und
    ein Client ist kein Domaenencontroller. Solche Faelle duerfen die
    Bewertung nicht verfaelschen, tauchen aber nachvollziehbar auf.

    Wie beim Hardware-Kollektor laeuft jeder Abschnitt gekapselt: Eine
    Abfrage, die auf einem bestimmten Geraet nicht funktioniert, kostet
    nur diesen einen Wert.

.PARAMETER AusgabePfad
    Verzeichnis, das windows_exporter als textfile-Verzeichnis liest.

.EXAMPLE
    .\Collect-SecurityBaseline.ps1 -Verbose
#>
[CmdletBinding()]
param(
    [string]$AusgabePfad = "$env:ProgramData\SchulMonitoring\textfile"
)

Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$Beginn = Get-Date
$Zeilen = [System.Collections.Generic.List[string]]::new()
$BekannteMetriken = [System.Collections.Generic.HashSet[string]]::new()
$Ergebnisse = [System.Collections.Generic.List[object]]::new()

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
            $wertText = $wertText -replace '\\', '\\\\' -replace '"', '\"' -replace "`r?`n", ' '
            $wertText = $wertText.Trim()
            if ($wertText.Length -gt 120) { $wertText = $wertText.Substring(0, 117) + '...' }
            '{0}="{1}"' -f $schluessel, $wertText
        }
        $Zeilen.Add(('{0}{{{1}}} {2}' -f $Name, ($teile -join ','),
            $Wert.ToString([Globalization.CultureInfo]::InvariantCulture)))
    } else {
        $Zeilen.Add(('{0} {1}' -f $Name,
            $Wert.ToString([Globalization.CultureInfo]::InvariantCulture)))
    }
}

# ===================================================================== #
# Eine Pruefung festhalten
# ===================================================================== #
function Add-Pruefung {
    <#
        $Konform:
          $true  -> eingestellt wie empfohlen
          $false -> Abweichung
          $null  -> nicht pruefbar / nicht zutreffend
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Kategorie,
        [Parameter(Mandatory)][ValidateSet('hoch', 'mittel', 'niedrig')][string]$Schwere,
        [Parameter(Mandatory)][string]$Massnahme,
        $Konform
    )

    $wert = if ($null -eq $Konform) { 2 } elseif ($Konform) { 1 } else { 0 }

    Add-Metrik -Name 'schule_sicherheit_pruefung' -Wert $wert `
        -Labels @{
            pruefung  = $Name
            kategorie = $Kategorie
            schwere   = $Schwere
            massnahme = $Massnahme
        } `
        -Hilfe '1 = wie empfohlen eingestellt, 0 = Abweichung, 2 = nicht pruefbar'

    $Ergebnisse.Add([pscustomobject]@{ Name = $Name; Wert = $wert; Schwere = $Schwere })
    Write-Verbose ("{0,-42} {1}" -f $Name, @('ABWEICHUNG', 'ok', 'n/a')[$wert])
}

function Invoke-Abschnitt {
    param([string]$Name, [scriptblock]$Aktion)
    try {
        & $Aktion
        Add-Metrik -Name 'schule_sicherheit_abschnitt_erfolgreich' -Wert 1 `
            -Labels @{ abschnitt = $Name } `
            -Hilfe 'Ob der jeweilige Pruefabschnitt durchgelaufen ist'
    } catch {
        Add-Metrik -Name 'schule_sicherheit_abschnitt_erfolgreich' -Wert 0 `
            -Labels @{ abschnitt = $Name }
        Write-Verbose "Abschnitt '$Name' fehlgeschlagen: $($_.Exception.Message)"
    }
}

# ===================================================================== #
# Hilfsfunktionen
# ===================================================================== #
function Get-RegWert {
    param([Parameter(Mandatory)][string]$Pfad, [Parameter(Mandatory)][string]$Name)
    try {
        return (Get-ItemProperty -LiteralPath $Pfad -Name $Name -ErrorAction Stop).$Name
    } catch {
        return $null
    }
}

function Get-AuditEinstellung {
    <#
        Liest eine Ueberwachungs-Unterkategorie ueber ihre GUID aus.
        Die GUID ist sprachunabhaengig – die Klartextnamen von auditpol
        sind uebersetzt und damit auf einem deutschen Windows nicht
        verwertbar.

        Rueckgabe: 0 = keine, 1 = Erfolg, 2 = Fehler, 3 = beides, $null = unbekannt
    #>
    param([Parameter(Mandatory)][string]$Guid)

    try {
        $ausgabe = & auditpol.exe /get /subcategory:"$Guid" /r 2>$null
        if (-not $ausgabe) { return $null }

        # Letzte nicht leere Zeile ist der Datensatz; das letzte Feld ist
        # der numerische Wert und damit ebenfalls sprachunabhaengig.
        $datenzeile = ($ausgabe | Where-Object { $_ -and $_.Trim() }) | Select-Object -Last 1
        if (-not $datenzeile) { return $null }

        $felder = $datenzeile -split ','
        $letztes = $felder[-1].Trim()

        $zahl = 0
        if ([int]::TryParse($letztes, [ref]$zahl) -and $zahl -ge 0 -and $zahl -le 3) {
            return $zahl
        }
        return $null
    } catch {
        return $null
    }
}

# --- Systemrolle bestimmen -------------------------------------------
$Betriebssystem = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$Produkttyp = if ($Betriebssystem) { [int]$Betriebssystem.ProductType } else { 1 }
$IstDomaenencontroller = ($Produkttyp -eq 2)
$IstServer = ($Produkttyp -ne 1)

Write-Verbose "Rolle: $(if ($IstDomaenencontroller) { 'Domaenencontroller' } elseif ($IstServer) { 'Server' } else { 'Client' })"

# ===================================================================== #
# 1. Diebstahl von Zugangsdaten
#    Die haeufigste Art, wie sich ein Angreifer im Netz ausbreitet.
# ===================================================================== #
Invoke-Abschnitt 'zugangsdaten' {

    $lsa = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa'

    # --- LSA-Schutz: verhindert das Auslesen von Kennwoertern aus dem
    #     Speicher (Mimikatz & Co.) --------------------------------------
    Add-Pruefung -Name 'lsa_schutz_aktiv' -Kategorie 'Zugangsdaten' -Schwere 'hoch' `
        -Massnahme 'RunAsPPL=1 unter HKLM\SYSTEM\CurrentControlSet\Control\Lsa setzen (per GPO)' `
        -Konform ((Get-RegWert $lsa 'RunAsPPL') -eq 1)

    # --- WDigest: legt Kennwoerter im Klartext im Speicher ab -----------
    # Ab Windows 10/2016 ist das ab Werk aus; ein gesetzter Wert 1 ist
    # fast immer die Handschrift eines Angreifers oder einer Altlast.
    $wdigest = Get-RegWert 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\WDigest' 'UseLogonCredential'
    Add-Pruefung -Name 'wdigest_klartext_aus' -Kategorie 'Zugangsdaten' -Schwere 'hoch' `
        -Massnahme 'UseLogonCredential=0 setzen – sonst liegen Kennwoerter im Klartext im Speicher' `
        -Konform ($null -eq $wdigest -or $wdigest -eq 0)

    # --- NTLMv1 und LM: uralt und in Minuten knackbar -------------------
    $lmLevel = Get-RegWert $lsa 'LmCompatibilityLevel'
    Add-Pruefung -Name 'ntlmv1_unterbunden' -Kategorie 'Zugangsdaten' -Schwere 'hoch' `
        -Massnahme 'LmCompatibilityLevel auf 5 setzen (nur NTLMv2 senden und annehmen)' `
        -Konform ($null -ne $lmLevel -and $lmLevel -ge 5)

    # --- Anonyme Abfrage von Konten und Freigaben -----------------------
    $anonym = Get-RegWert $lsa 'RestrictAnonymous'
    $anonymSam = Get-RegWert $lsa 'RestrictAnonymousSAM'
    Add-Pruefung -Name 'anonyme_abfrage_gesperrt' -Kategorie 'Zugangsdaten' -Schwere 'mittel' `
        -Massnahme 'RestrictAnonymous=1 und RestrictAnonymousSAM=1 setzen' `
        -Konform (($anonym -eq 1) -and ($null -eq $anonymSam -or $anonymSam -eq 1))

    # --- LLMNR: erlaubt das Abfangen von Anmeldedaten im gleichen Netz --
    # Der Klassiker fuer Angriffe aus dem Schuelernetz.
    $llmnr = Get-RegWert 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient' 'EnableMulticast'
    Add-Pruefung -Name 'llmnr_deaktiviert' -Kategorie 'Zugangsdaten' -Schwere 'hoch' `
        -Massnahme 'Per GPO "Multicastnamensaufloesung deaktivieren" – verhindert das Abgreifen von Anmeldedaten' `
        -Konform ($llmnr -eq 0)

    # --- NetBIOS ueber TCP/IP: dasselbe Problem wie LLMNR ---------------
    try {
        $schnittstellen = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces' -ErrorAction Stop
        $mitNetbios = @($schnittstellen | Where-Object {
            (Get-RegWert $_.PSPath 'NetbiosOptions') -ne 2
        })
        Add-Pruefung -Name 'netbios_deaktiviert' -Kategorie 'Zugangsdaten' -Schwere 'mittel' `
            -Massnahme 'NetBIOS ueber TCP/IP in den Adaptereinstellungen bzw. per DHCP-Option deaktivieren' `
            -Konform ($mitNetbios.Count -eq 0)
    } catch {
        Add-Pruefung -Name 'netbios_deaktiviert' -Kategorie 'Zugangsdaten' -Schwere 'mittel' `
            -Massnahme 'NetBIOS ueber TCP/IP deaktivieren' -Konform $null
    }

    # --- Credential Guard -----------------------------------------------
    try {
        $deviceGuard = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' `
            -ClassName 'Win32_DeviceGuard' -ErrorAction Stop
        $laufend = @($deviceGuard.SecurityServicesRunning)
        Add-Pruefung -Name 'credential_guard_aktiv' -Kategorie 'Zugangsdaten' -Schwere 'mittel' `
            -Massnahme 'Credential Guard per GPO aktivieren (braucht UEFI, Secure Boot und Virtualisierung)' `
            -Konform ($laufend -contains 1)
    } catch {
        Add-Pruefung -Name 'credential_guard_aktiv' -Kategorie 'Zugangsdaten' -Schwere 'mittel' `
            -Massnahme 'Credential Guard aktivieren' -Konform $null
    }
}

# ===================================================================== #
# 2. Netzwerkprotokolle
# ===================================================================== #
Invoke-Abschnitt 'netzwerk' {

    # --- SMBv1: Einfallstor von WannaCry und Verwandten ------------------
    try {
        $smbServer = Get-SmbServerConfiguration -ErrorAction Stop
        Add-Pruefung -Name 'smb1_deaktiviert' -Kategorie 'Netzwerk' -Schwere 'hoch' `
            -Massnahme 'SMBv1 entfernen: Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol' `
            -Konform (-not $smbServer.EnableSMB1Protocol)

        Add-Pruefung -Name 'smb_signierung_server' -Kategorie 'Netzwerk' -Schwere 'mittel' `
            -Massnahme 'SMB-Signierung erzwingen – verhindert das Umleiten von Anmeldungen (Relay-Angriffe)' `
            -Konform ([bool]$smbServer.RequireSecuritySignature)
    } catch {
        Add-Pruefung -Name 'smb1_deaktiviert' -Kategorie 'Netzwerk' -Schwere 'hoch' `
            -Massnahme 'SMBv1 entfernen' -Konform $null
        Add-Pruefung -Name 'smb_signierung_server' -Kategorie 'Netzwerk' -Schwere 'mittel' `
            -Massnahme 'SMB-Signierung erzwingen' -Konform $null
    }

    try {
        $smbClient = Get-SmbClientConfiguration -ErrorAction Stop
        Add-Pruefung -Name 'smb_signierung_client' -Kategorie 'Netzwerk' -Schwere 'mittel' `
            -Massnahme 'SMB-Signierung auch clientseitig erzwingen' `
            -Konform ([bool]$smbClient.RequireSecuritySignature)
    } catch {
        Add-Pruefung -Name 'smb_signierung_client' -Kategorie 'Netzwerk' -Schwere 'mittel' `
            -Massnahme 'SMB-Signierung clientseitig erzwingen' -Konform $null
    }

    # --- Veraltete TLS-Versionen -----------------------------------------
    $veraltet = @('SSL 2.0', 'SSL 3.0', 'TLS 1.0', 'TLS 1.1')
    $nochAktiv = [System.Collections.Generic.List[string]]::new()
    foreach ($protokoll in $veraltet) {
        $pfad = "HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\$protokoll\Server"
        $aktiviert = Get-RegWert $pfad 'Enabled'
        # Kein Eintrag bedeutet: Windows-Standard, und der ist bei diesen
        # Protokollen "aktiv" – also als Abweichung werten.
        if ($null -eq $aktiviert -or $aktiviert -ne 0) { $nochAktiv.Add($protokoll) }
    }
    Add-Pruefung -Name 'veraltetes_tls_aus' -Kategorie 'Netzwerk' -Schwere 'mittel' `
        -Massnahme "SSL 2.0/3.0 und TLS 1.0/1.1 in SCHANNEL abschalten (noch aktiv: $($nochAktiv -join ', '))" `
        -Konform ($nochAktiv.Count -eq 0)

    # --- Firewall ---------------------------------------------------------
    try {
        $firewallProfile = Get-NetFirewallProfile -ErrorAction Stop
        $alleBlockieren = @($firewallProfile | Where-Object { $_.DefaultInboundAction -ne 'Block' }).Count -eq 0
        Add-Pruefung -Name 'firewall_eingehend_blockiert' -Kategorie 'Netzwerk' -Schwere 'hoch' `
            -Massnahme 'Standardverhalten fuer eingehende Verbindungen in allen Profilen auf "Blockieren" stellen' `
            -Konform $alleBlockieren

        $alleProtokollieren = @($firewallProfile | Where-Object { -not $_.LogBlocked -or $_.LogBlocked -eq 'False' }).Count -eq 0
        Add-Pruefung -Name 'firewall_protokolliert' -Kategorie 'Netzwerk' -Schwere 'niedrig' `
            -Massnahme 'Protokollierung verworfener Pakete einschalten – hilft bei jeder Fehlersuche' `
            -Konform $alleProtokollieren
    } catch {
        Add-Pruefung -Name 'firewall_eingehend_blockiert' -Kategorie 'Netzwerk' -Schwere 'hoch' `
            -Massnahme 'Eingehende Verbindungen standardmaessig blockieren' -Konform $null
    }

    # --- Fernzugriff ------------------------------------------------------
    $rdpPfad = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
    $rdpAus = (Get-RegWert 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' 'fDenyTSConnections') -eq 1

    if ($rdpAus) {
        # RDP ist abgeschaltet – dann sind die Detaileinstellungen egal
        Add-Pruefung -Name 'rdp_netzwerkauthentifizierung' -Kategorie 'Fernzugriff' -Schwere 'hoch' `
            -Massnahme 'RDP ist deaktiviert – nichts zu tun' -Konform $null
        Add-Pruefung -Name 'rdp_verschluesselung_hoch' -Kategorie 'Fernzugriff' -Schwere 'mittel' `
            -Massnahme 'RDP ist deaktiviert – nichts zu tun' -Konform $null
    } else {
        Add-Pruefung -Name 'rdp_netzwerkauthentifizierung' -Kategorie 'Fernzugriff' -Schwere 'hoch' `
            -Massnahme 'Netzwerkauthentifizierung (NLA) fuer RDP verlangen – sonst ist die Anmeldemaske offen im Netz' `
            -Konform ((Get-RegWert $rdpPfad 'UserAuthentication') -eq 1)

        Add-Pruefung -Name 'rdp_verschluesselung_hoch' -Kategorie 'Fernzugriff' -Schwere 'mittel' `
            -Massnahme 'RDP-Verschluesselungsstufe auf "Hoch" oder "FIPS" stellen' `
            -Konform ((Get-RegWert $rdpPfad 'MinEncryptionLevel') -ge 3)

    }

    # Auf Arbeitsplatzrechnern hat RDP in der Schule meist nichts verloren.
    # Die Pruefung wird bewusst immer gemeldet – auch wenn sie erfuellt ist,
    # sonst verschwaende die Zeitreihe genau im guten Fall.
    if (-not $IstServer) {
        Add-Pruefung -Name 'rdp_auf_client_aus' -Kategorie 'Fernzugriff' -Schwere 'mittel' `
            -Massnahme 'Auf Arbeitsplatzrechnern den Remotedesktop abschalten, sofern nicht gebraucht' `
            -Konform $rdpAus
    }
}

# ===================================================================== #
# 3. Ausfuehrung und Nachvollziehbarkeit
#    Ohne diese Einstellungen laeuft auch die Angriffserkennung leer.
# ===================================================================== #
Invoke-Abschnitt 'ausfuehrung' {

    $psPolicy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\PowerShell'

    Add-Pruefung -Name 'ps_skriptblock_protokollierung' -Kategorie 'Nachvollziehbarkeit' -Schwere 'hoch' `
        -Massnahme 'PowerShell-Skriptblockprotokollierung per GPO einschalten – ohne sie sieht das Monitoring verschleierte Befehle nicht' `
        -Konform ((Get-RegWert "$psPolicy\ScriptBlockLogging" 'EnableScriptBlockLogging') -eq 1)

    Add-Pruefung -Name 'ps_modul_protokollierung' -Kategorie 'Nachvollziehbarkeit' -Schwere 'mittel' `
        -Massnahme 'PowerShell-Modulprotokollierung per GPO einschalten' `
        -Konform ((Get-RegWert "$psPolicy\ModuleLogging" 'EnableModuleLogging') -eq 1)

    Add-Pruefung -Name 'ps_transkription' -Kategorie 'Nachvollziehbarkeit' -Schwere 'niedrig' `
        -Massnahme 'PowerShell-Transkription auf eine geschuetzte Freigabe einschalten' `
        -Konform ((Get-RegWert "$psPolicy\Transcription" 'EnableTranscripting') -eq 1)

    # --- PowerShell 2.0: umgeht saemtliche Protokollierung ---------------
    $ps2Vorhanden = Test-Path 'HKLM:\SOFTWARE\Microsoft\PowerShell\1\PowerShellEngine'
    Add-Pruefung -Name 'powershell2_entfernt' -Kategorie 'Ausfuehrung' -Schwere 'hoch' `
        -Massnahme 'PowerShell 2.0 entfernen – damit laesst sich jede Protokollierung umgehen' `
        -Konform (-not $ps2Vorhanden)

    # --- Befehlszeile in Ereignis 4688 -----------------------------------
    Add-Pruefung -Name 'befehlszeile_protokolliert' -Kategorie 'Nachvollziehbarkeit' -Schwere 'hoch' `
        -Massnahme 'GPO "Befehlszeile in Prozesserstellungsereignisse einbeziehen" aktivieren – ohne sie ist Ereignis 4688 wertlos' `
        -Konform ((Get-RegWert 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit' 'ProcessCreationIncludeCmdLine_Enabled') -eq 1)

    # --- AutoRun ----------------------------------------------------------
    $autoRun = Get-RegWert 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoDriveTypeAutoRun'
    Add-Pruefung -Name 'autorun_deaktiviert' -Kategorie 'Ausfuehrung' -Schwere 'mittel' `
        -Massnahme 'AutoRun fuer alle Laufwerkstypen abschalten (NoDriveTypeAutoRun=255) – USB-Sticks in der Schule' `
        -Konform ($autoRun -eq 255)

    # --- Benutzerkontensteuerung ------------------------------------------
    $systemPolicy = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    Add-Pruefung -Name 'uac_aktiv' -Kategorie 'Ausfuehrung' -Schwere 'hoch' `
        -Massnahme 'Benutzerkontensteuerung eingeschaltet lassen (EnableLUA=1)' `
        -Konform ((Get-RegWert $systemPolicy 'EnableLUA') -eq 1)

    Add-Pruefung -Name 'uac_nachfrage_streng' -Kategorie 'Ausfuehrung' -Schwere 'mittel' `
        -Massnahme 'UAC-Nachfrage fuer Administratoren auf mindestens "Zustimmung verlangen" stellen' `
        -Konform ((Get-RegWert $systemPolicy 'ConsentPromptBehaviorAdmin') -ge 2)

    # --- Secure Boot -------------------------------------------------------
    try {
        $secureBoot = Confirm-SecureBootUEFI -ErrorAction Stop
        Add-Pruefung -Name 'secure_boot_aktiv' -Kategorie 'System' -Schwere 'mittel' `
            -Massnahme 'Secure Boot im UEFI einschalten' -Konform ([bool]$secureBoot)
    } catch {
        # Aeltere Geraete starten noch im BIOS-Modus – dann gibt es das nicht
        Add-Pruefung -Name 'secure_boot_aktiv' -Kategorie 'System' -Schwere 'mittel' `
            -Massnahme 'Secure Boot im UEFI einschalten (Geraet startet derzeit im BIOS-Modus)' -Konform $null
    }
}

# ===================================================================== #
# 4. Ueberwachungsrichtlinien
#    Die Grundlage der gesamten Angriffserkennung.
# ===================================================================== #
Invoke-Abschnitt 'ueberwachung' {

    # GUIDs statt Klartextnamen – die Namen sind uebersetzt.
    $unterkategorien = @(
        @{ Guid = '{0CCE922B-69AE-11D9-BED3-505054503030}'; Name = 'audit_prozesserstellung'; Erwartet = 1; Schwere = 'hoch'
           Text = 'Ueberwachung "Prozesserstellung" auf Erfolg stellen – Grundlage fuer Ereignis 4688' }
        @{ Guid = '{0CCE9215-69AE-11D9-BED3-505054503030}'; Name = 'audit_anmeldung'; Erwartet = 3; Schwere = 'hoch'
           Text = 'Ueberwachung "Anmelden" auf Erfolg und Fehler stellen – Grundlage der Angriffserkennung' }
        @{ Guid = '{0CCE9217-69AE-11D9-BED3-505054503030}'; Name = 'audit_kontosperrung'; Erwartet = 1; Schwere = 'mittel'
           Text = 'Ueberwachung "Kontosperrung" auf Erfolg stellen' }
        @{ Guid = '{0CCE9235-69AE-11D9-BED3-505054503030}'; Name = 'audit_benutzerkonten'; Erwartet = 1; Schwere = 'hoch'
           Text = 'Ueberwachung "Benutzerkontenverwaltung" auf Erfolg stellen – erkennt neu angelegte Konten' }
        @{ Guid = '{0CCE9237-69AE-11D9-BED3-505054503030}'; Name = 'audit_gruppenverwaltung'; Erwartet = 1; Schwere = 'hoch'
           Text = 'Ueberwachung "Sicherheitsgruppenverwaltung" auf Erfolg stellen – erkennt neue Administratoren' }
        @{ Guid = '{0CCE922F-69AE-11D9-BED3-505054503030}'; Name = 'audit_richtlinienaenderung'; Erwartet = 1; Schwere = 'mittel'
           Text = 'Ueberwachung "Aenderung der Ueberwachungsrichtlinie" auf Erfolg stellen' }
    )

    foreach ($eintrag in $unterkategorien) {
        $ist = Get-AuditEinstellung -Guid $eintrag.Guid
        $konform = if ($null -eq $ist) { $null } else {
            # Erwartet 3 = Erfolg und Fehler, 1 = mindestens Erfolg
            if ($eintrag.Erwartet -eq 3) { $ist -eq 3 } else { ($ist -band 1) -eq 1 }
        }
        Add-Pruefung -Name $eintrag.Name -Kategorie 'Nachvollziehbarkeit' `
            -Schwere $eintrag.Schwere -Massnahme $eintrag.Text -Konform $konform
    }

    # --- Groesse des Sicherheitsprotokolls --------------------------------
    # Zu klein bedeutet: Ereignisse werden ueberschrieben, bevor sie
    # abgeholt werden. In einer Schule mit vielen Anmeldungen geht das
    # schnell.
    try {
        $protokoll = Get-WinEvent -ListLog 'Security' -ErrorAction Stop
        $megabyte = [math]::Round($protokoll.MaximumSizeInBytes / 1MB, 0)

        Add-Metrik -Name 'schule_sicherheitsprotokoll_groesse_megabyte' -Wert $megabyte `
            -Hilfe 'Maximale Groesse des Windows-Sicherheitsprotokolls'

        Add-Pruefung -Name 'sicherheitsprotokoll_gross_genug' -Kategorie 'Nachvollziehbarkeit' -Schwere 'mittel' `
            -Massnahme "Sicherheitsprotokoll auf mindestens 192 MB vergroessern (derzeit $megabyte MB)" `
            -Konform ($megabyte -ge 192)
    } catch {
        Add-Pruefung -Name 'sicherheitsprotokoll_gross_genug' -Kategorie 'Nachvollziehbarkeit' -Schwere 'mittel' `
            -Massnahme 'Sicherheitsprotokoll auf mindestens 192 MB vergroessern' -Konform $null
    }
}

# ===================================================================== #
# 5. Virenschutz
# ===================================================================== #
Invoke-Abschnitt 'virenschutz' {

    if (-not (Get-Command Get-MpComputerStatus -ErrorAction SilentlyContinue)) {
        foreach ($name in @('defender_manipulationsschutz', 'defender_cloudschutz',
                            'defender_pua_schutz', 'defender_netzwerkschutz',
                            'defender_asr_aktiv', 'defender_lsass_geschuetzt')) {
            Add-Pruefung -Name $name -Kategorie 'Virenschutz' -Schwere 'mittel' `
                -Massnahme 'Microsoft Defender ist auf diesem Geraet nicht verfuegbar' -Konform $null
        }
        return
    }

    $status = Get-MpComputerStatus -ErrorAction SilentlyContinue
    $einstellungen = Get-MpPreference -ErrorAction SilentlyContinue

    if ($status) {
        Add-Pruefung -Name 'defender_manipulationsschutz' -Kategorie 'Virenschutz' -Schwere 'hoch' `
            -Massnahme 'Manipulationsschutz einschalten – verhindert, dass Schadsoftware den Virenschutz abschaltet' `
            -Konform ([bool]$status.IsTamperProtected)
    }

    if ($einstellungen) {
        Add-Pruefung -Name 'defender_cloudschutz' -Kategorie 'Virenschutz' -Schwere 'mittel' `
            -Massnahme 'Cloudbasierten Schutz (MAPS) einschalten – erkennt neue Schadsoftware deutlich frueher' `
            -Konform ($einstellungen.MAPSReporting -ne 0)

        Add-Pruefung -Name 'defender_pua_schutz' -Kategorie 'Virenschutz' -Schwere 'niedrig' `
            -Massnahme 'Schutz vor potenziell unerwuenschten Anwendungen einschalten (PUAProtection=1)' `
            -Konform ($einstellungen.PUAProtection -eq 1)

        Add-Pruefung -Name 'defender_netzwerkschutz' -Kategorie 'Virenschutz' -Schwere 'mittel' `
            -Massnahme 'Netzwerkschutz einschalten – blockiert den Aufruf bekannt boesartiger Adressen' `
            -Konform ($einstellungen.EnableNetworkProtection -eq 1)

        # --- Regeln zur Angriffsflaechenreduzierung (ASR) -----------------
        $ids = @($einstellungen.AttackSurfaceReductionRules_Ids)
        $aktionen = @($einstellungen.AttackSurfaceReductionRules_Actions)

        $imBlockmodus = 0
        for ($i = 0; $i -lt $ids.Count; $i++) {
            if ($i -lt $aktionen.Count -and $aktionen[$i] -eq 1) { $imBlockmodus++ }
        }

        Add-Metrik -Name 'schule_asr_regeln_blockierend' -Wert $imBlockmodus `
            -Hilfe 'Anzahl der ASR-Regeln im Blockiermodus'

        Add-Pruefung -Name 'defender_asr_aktiv' -Kategorie 'Virenschutz' -Schwere 'mittel' `
            -Massnahme "Regeln zur Angriffsflaechenreduzierung im Blockiermodus einrichten (derzeit $imBlockmodus)" `
            -Konform ($imBlockmodus -ge 5)

        # Die wichtigste Einzelregel: kein Auslesen von Anmeldedaten aus LSASS
        $lsassRegel = '9e6c4e1f-7d60-472f-ba1a-a39ef669e4b2'
        $lsassGeschuetzt = $false
        for ($i = 0; $i -lt $ids.Count; $i++) {
            if ([string]$ids[$i] -eq $lsassRegel -and $i -lt $aktionen.Count -and $aktionen[$i] -eq 1) {
                $lsassGeschuetzt = $true
            }
        }
        Add-Pruefung -Name 'defender_lsass_geschuetzt' -Kategorie 'Virenschutz' -Schwere 'hoch' `
            -Massnahme 'ASR-Regel 9e6c4e1f-… auf "Blockieren" stellen – verhindert das Auslesen von Anmeldedaten' `
            -Konform $lsassGeschuetzt
    }
}

# ===================================================================== #
# 6. Konten
# ===================================================================== #
Invoke-Abschnitt 'konten' {

    # --- Gastkonto (endet immer auf -501) --------------------------------
    try {
        $gast = Get-LocalUser -ErrorAction Stop | Where-Object { $_.SID.Value -like '*-501' }
        Add-Pruefung -Name 'gastkonto_deaktiviert' -Kategorie 'Konten' -Schwere 'mittel' `
            -Massnahme 'Gastkonto deaktivieren' `
            -Konform ($null -eq $gast -or -not $gast.Enabled)

        # --- Eingebautes Administratorkonto (endet auf -500) --------------
        $admin = Get-LocalUser -ErrorAction Stop | Where-Object { $_.SID.Value -like '*-500' }
        if ($IstDomaenencontroller) {
            Add-Pruefung -Name 'lokales_admin_konto_aus' -Kategorie 'Konten' -Schwere 'mittel' `
                -Massnahme 'Auf einem Domaenencontroller gibt es kein lokales Administratorkonto' -Konform $null
        } else {
            Add-Pruefung -Name 'lokales_admin_konto_aus' -Kategorie 'Konten' -Schwere 'mittel' `
                -Massnahme 'Eingebautes Administratorkonto deaktivieren und stattdessen benannte Konten verwenden' `
                -Konform ($null -eq $admin -or -not $admin.Enabled)
        }
    } catch {
        Add-Pruefung -Name 'gastkonto_deaktiviert' -Kategorie 'Konten' -Schwere 'mittel' `
            -Massnahme 'Gastkonto deaktivieren' -Konform $null
    }

    # --- Anzahl lokaler Administratoren -----------------------------------
    try {
        # SID der Gruppe statt des Namens – "Administratoren" heisst je
        # nach Sprache anders.
        $mitglieder = @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop)
        Add-Metrik -Name 'schule_lokale_administratoren' -Wert $mitglieder.Count `
            -Hilfe 'Anzahl der Mitglieder in der lokalen Administratorengruppe'

        if (-not $IstServer) {
            Add-Pruefung -Name 'wenige_lokale_administratoren' -Kategorie 'Konten' -Schwere 'mittel' `
                -Massnahme "Lokale Administratorengruppe aufraeumen (derzeit $($mitglieder.Count) Mitglieder)" `
                -Konform ($mitglieder.Count -le 4)
        }
    } catch {
        # Verwaiste SIDs aus geloeschten Domaenenkonten lassen das Cmdlet
        # gelegentlich scheitern – das ist kein Fehler des Monitorings.
        Add-Pruefung -Name 'wenige_lokale_administratoren' -Kategorie 'Konten' -Schwere 'mittel' `
            -Massnahme 'Lokale Administratorengruppe pruefen' -Konform $null
    }

    # --- LAPS: eindeutige lokale Administratorkennwoerter -----------------
    $lapsNeu = Test-Path 'HKLM:\SOFTWARE\Microsoft\Policies\LAPS'
    $lapsAlt = (Get-RegWert 'HKLM:\SOFTWARE\Policies\Microsoft\Services\AdmPwd' 'AdmPwdEnabled') -eq 1
    Add-Pruefung -Name 'laps_im_einsatz' -Kategorie 'Konten' -Schwere 'hoch' `
        -Massnahme 'Windows LAPS einrichten – sonst oeffnet ein einziges erbeutetes Kennwort alle Rechner der Schule' `
        -Konform ($lapsNeu -or $lapsAlt)
}

# ===================================================================== #
# 7. Rechteausweitung ueber Dienste
# ===================================================================== #
Invoke-Abschnitt 'dienste' {

    # Ein Dienstpfad mit Leerzeichen ohne Anfuehrungszeichen laesst sich
    # entfuehren: Windows probiert C:\Programme.exe, bevor es
    # "C:\Programme\Werkzeug\dienst.exe" startet.
    try {
        $dienste = Get-CimInstance Win32_Service -ErrorAction Stop |
            Where-Object {
                $_.PathName -and
                $_.PathName -notmatch '^\s*"' -and
                $_.PathName -match '^\s*\S+\s+\S' -and
                $_.PathName -notmatch '^\s*[A-Za-z]:\\[Ww]indows\\[Ss]ystem32\\'
            }

        $anzahl = @($dienste).Count
        Add-Metrik -Name 'schule_dienste_ungeschuetzter_pfad' -Wert $anzahl `
            -Hilfe 'Dienste mit nicht in Anfuehrungszeichen gesetztem Pfad (Weg zur Rechteausweitung)'

        Add-Pruefung -Name 'dienstpfade_in_anfuehrungszeichen' -Kategorie 'System' -Schwere 'mittel' `
            -Massnahme "Dienstpfade mit Leerzeichen in Anfuehrungszeichen setzen (betroffen: $anzahl)" `
            -Konform ($anzahl -eq 0)
    } catch {
        Add-Pruefung -Name 'dienstpfade_in_anfuehrungszeichen' -Kategorie 'System' -Schwere 'mittel' `
            -Massnahme 'Dienstpfade pruefen' -Konform $null
    }

    # --- Druckwarteschlange auf Domaenencontrollern ------------------------
    if ($IstDomaenencontroller) {
        $spooler = Get-Service -Name 'Spooler' -ErrorAction SilentlyContinue
        $aus = ($null -eq $spooler) -or ($spooler.StartType -eq 'Disabled')
        Add-Pruefung -Name 'spooler_auf_dc_aus' -Kategorie 'System' -Schwere 'hoch' `
            -Massnahme 'Druckwarteschlange auf Domaenencontrollern deaktivieren (PrintNightmare)' `
            -Konform $aus
    }
}

# ===================================================================== #
# 8. Active Directory – nur auf Domaenencontrollern
# ===================================================================== #
Invoke-Abschnitt 'active-directory' {

    if (-not $IstDomaenencontroller) { return }
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        Add-Pruefung -Name 'ad_krbtgt_aktuell' -Kategorie 'Active Directory' -Schwere 'hoch' `
            -Massnahme 'PowerShell-Modul ActiveDirectory installieren, damit die AD-Pruefungen laufen' -Konform $null
        return
    }

    Import-Module ActiveDirectory -ErrorAction Stop

    # --- krbtgt: Grundlage aller Kerberos-Tickets --------------------------
    # Ein Angreifer, der den Schluessel einmal erbeutet, kann sich damit
    # jahrelang beliebige Tickets ausstellen (Golden Ticket). Der einzige
    # Gegenzauber ist ein regelmaessiger Kennwortwechsel.
    try {
        $krbtgt = Get-ADUser -Identity 'krbtgt' -Properties PasswordLastSet -ErrorAction Stop
        if ($krbtgt.PasswordLastSet) {
            $alter = [math]::Round(((Get-Date) - $krbtgt.PasswordLastSet).TotalDays, 0)
            Add-Metrik -Name 'schule_ad_krbtgt_kennwort_alter_tage' -Wert $alter `
                -Hilfe 'Alter des krbtgt-Kennworts in Tagen'
            Add-Pruefung -Name 'ad_krbtgt_aktuell' -Kategorie 'Active Directory' -Schwere 'hoch' `
                -Massnahme "krbtgt-Kennwort turnusmaessig zweimal zuruecksetzen (derzeit $alter Tage alt)" `
                -Konform ($alter -le 180)
        }
    } catch { }

    # --- Konten ohne Kerberos-Vorauthentifizierung -------------------------
    # Bei diesen Konten laesst sich das Kennwort ohne jede Anmeldung
    # offline knacken (AS-REP Roasting).
    try {
        $ohneVorauth = @(Get-ADUser -Filter { DoesNotRequirePreAuth -eq $true } -ErrorAction Stop)
        Add-Metrik -Name 'schule_ad_konten_ohne_vorauthentifizierung' -Wert $ohneVorauth.Count `
            -Hilfe 'Konten ohne Kerberos-Vorauthentifizierung (AS-REP Roasting)'
        Add-Pruefung -Name 'ad_vorauthentifizierung_erzwungen' -Kategorie 'Active Directory' -Schwere 'hoch' `
            -Massnahme "Kerberos-Vorauthentifizierung fuer alle Konten verlangen (betroffen: $($ohneVorauth.Count))" `
            -Konform ($ohneVorauth.Count -eq 0)
    } catch { }

    # --- Dienstkonten mit altem Kennwort (Kerberoasting) -------------------
    try {
        $dienstkonten = @(Get-ADUser -Filter { ServicePrincipalName -like '*' } `
            -Properties ServicePrincipalName, PasswordLastSet -ErrorAction Stop |
            Where-Object { $_.PasswordLastSet -and $_.PasswordLastSet -lt (Get-Date).AddDays(-365) })
        Add-Metrik -Name 'schule_ad_dienstkonten_altes_kennwort' -Wert $dienstkonten.Count `
            -Hilfe 'Dienstkonten mit SPN, deren Kennwort aelter als ein Jahr ist'
        Add-Pruefung -Name 'ad_dienstkonten_kennwoerter_frisch' -Kategorie 'Active Directory' -Schwere 'mittel' `
            -Massnahme "Kennwoerter von Dienstkonten erneuern oder auf gMSA umstellen (betroffen: $($dienstkonten.Count))" `
            -Konform ($dienstkonten.Count -eq 0)
    } catch { }

    # --- Groesse der privilegierten Gruppen --------------------------------
    try {
        $domaene = Get-ADDomain -ErrorAction Stop
        $domaenenAdmins = @(Get-ADGroupMember -Identity "$($domaene.DomainSID)-512" -Recursive -ErrorAction Stop)
        Add-Metrik -Name 'schule_ad_domaenen_administratoren' -Wert $domaenenAdmins.Count `
            -Hilfe 'Anzahl der Mitglieder in der Gruppe der Domaenen-Administratoren'
        Add-Pruefung -Name 'ad_wenige_domaenenadmins' -Kategorie 'Active Directory' -Schwere 'hoch' `
            -Massnahme "Kreis der Domaenen-Administratoren klein halten (derzeit $($domaenenAdmins.Count))" `
            -Konform ($domaenenAdmins.Count -le 5)
    } catch { }

    # --- Karteileichen ------------------------------------------------------
    try {
        $grenze = (Get-Date).AddDays(-90)
        $inaktiv = @(Get-ADUser -Filter { Enabled -eq $true } -Properties LastLogonDate -ErrorAction Stop |
            Where-Object { $_.LastLogonDate -and $_.LastLogonDate -lt $grenze })
        Add-Metrik -Name 'schule_ad_inaktive_konten' -Wert $inaktiv.Count `
            -Hilfe 'Aktivierte Konten ohne Anmeldung in den letzten 90 Tagen'
        Add-Pruefung -Name 'ad_keine_karteileichen' -Kategorie 'Active Directory' -Schwere 'mittel' `
            -Massnahme "Konten ohne Anmeldung seit 90 Tagen deaktivieren (betroffen: $($inaktiv.Count))" `
            -Konform ($inaktiv.Count -le 5)
    } catch { }

    # --- Kennwortrichtlinie der Domaene -------------------------------------
    try {
        $richtlinie = Get-ADDefaultDomainPasswordPolicy -ErrorAction Stop
        Add-Pruefung -Name 'ad_kennwortlaenge' -Kategorie 'Active Directory' -Schwere 'mittel' `
            -Massnahme "Mindestlaenge fuer Kennwoerter auf 12 Zeichen anheben (derzeit $($richtlinie.MinPasswordLength))" `
            -Konform ($richtlinie.MinPasswordLength -ge 12)

        Add-Pruefung -Name 'ad_kontosperrung_aktiv' -Kategorie 'Active Directory' -Schwere 'hoch' `
            -Massnahme 'Kontosperrschwelle setzen – ohne sie laufen Kennwortangriffe unbegrenzt weiter' `
            -Konform ($richtlinie.LockoutThreshold -gt 0)
    } catch { }
}

# ===================================================================== #
# Bewertung
# ===================================================================== #
$bewertbar = @($Ergebnisse | Where-Object { $_.Wert -ne 2 })
$konform = @($bewertbar | Where-Object { $_.Wert -eq 1 })

$score = if ($bewertbar.Count -gt 0) {
    [math]::Round(100 * $konform.Count / $bewertbar.Count, 1)
} else { 0 }

Add-Metrik -Name 'schule_sicherheit_score_prozent' -Wert $score `
    -Hilfe 'Anteil der Pruefungen, die wie empfohlen eingestellt sind'

Add-Metrik -Name 'schule_sicherheit_pruefungen_gesamt' -Wert $bewertbar.Count `
    -Hilfe 'Anzahl der auf diesem Geraet bewertbaren Pruefungen'

foreach ($stufe in @('hoch', 'mittel', 'niedrig')) {
    $abweichungen = @($Ergebnisse | Where-Object { $_.Wert -eq 0 -and $_.Schwere -eq $stufe })
    Add-Metrik -Name 'schule_sicherheit_abweichungen' -Wert $abweichungen.Count `
        -Labels @{ schwere = $stufe } `
        -Hilfe 'Anzahl der Abweichungen von der Grundhaertung je Schweregrad'
}

Add-Metrik -Name 'schule_sicherheit_pruefung_dauer_sekunden' `
    -Wert ([math]::Round(((Get-Date) - $Beginn).TotalSeconds, 2)) `
    -Hilfe 'Laufzeit der Baseline-Pruefung'

Add-Metrik -Name 'schule_sicherheit_pruefung_zeitstempel' `
    -Wert ([int64][System.DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) `
    -Hilfe 'Zeitpunkt der letzten Baseline-Pruefung'

# ===================================================================== #
# Schreiben – erst daneben, dann umbenennen
# ===================================================================== #
if (-not (Test-Path -LiteralPath $AusgabePfad)) {
    New-Item -ItemType Directory -Path $AusgabePfad -Force | Out-Null
}

$zieldatei = Join-Path $AusgabePfad 'schule_sicherheit.prom'
$zwischendatei = "$zieldatei.tmp"

$inhalt = ($Zeilen -join "`n") + "`n"
[System.IO.File]::WriteAllText($zwischendatei, $inhalt, (New-Object System.Text.UTF8Encoding($false)))
Move-Item -LiteralPath $zwischendatei -Destination $zieldatei -Force

Write-Verbose ""
Write-Verbose ("Bewertung: {0} % ({1} von {2} Pruefungen wie empfohlen)" -f $score, $konform.Count, $bewertbar.Count)
Write-Verbose ("{0} Zeilen geschrieben nach {1}" -f $Zeilen.Count, $zieldatei)
