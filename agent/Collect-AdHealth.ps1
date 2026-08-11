#Requires -Version 5.1
<#
.SYNOPSIS
    Erfasst den Betriebszustand des Active Directory und legt das Ergebnis
    als Prometheus-Messwerte ab.

.DESCRIPTION
    Die Sicherheitspruefung sieht sich das Verzeichnis inhaltlich an:
    krbtgt-Alter, inaktive Konten, Angriffsflaeche. Dieses Skript hier
    prueft etwas anderes – ob der Laden ueberhaupt laeuft:

      * Replikation zwischen den Domaenencontrollern
      * SYSVOL und NETLOGON: freigegeben, und repliziert DFSR sauber?
      * FSMO-Rollen: wer haelt sie, antworten die Halter?
      * Antwortzeit einer LDAP-Anfrage
      * Kontosperrungen je Stunde

    Der Grund fuer ein eigenes Skript: Der Dienst NTDS laeuft auch dann
    noch froehlich weiter, wenn sich in der Schule niemand mehr anmelden
    kann. "Dienst laeuft" ist keine Aussage ueber die Funktion.

    Alles laeuft ueber das PowerShell-Modul ActiveDirectory und ueber WMI,
    nicht ueber die Textausgabe von repadmin oder dcdiag. Deren Ausgabe
    ist uebersetzt – auf einem deutschen Server steht dort etwas anderes
    als auf einem englischen, und jeder Parser darauf ist eine Zeitbombe.
    Objekteigenschaften und WMI-Zustandsnummern sind dagegen ueberall
    gleich.

    Wird alle 15 Minuten von einer geplanten Aufgabe aufgerufen und
    ausschliesslich auf Domaenencontrollern eingerichtet.

.PARAMETER AusgabePfad
    Verzeichnis, das windows_exporter als textfile-Verzeichnis liest.
#>
[CmdletBinding()]
param(
    [string]$AusgabePfad = "$env:ProgramData\SchulMonitoring\textfile"
)

# Version 1.0 statt Latest – gleiche Begruendung wie bei den anderen
# Sammlern: WMI- und AD-Objekte bringen nicht immer alle Eigenschaften mit.
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
    param([string]$Name, [scriptblock]$Aktion)
    try {
        & $Aktion
        Add-Metrik -Name 'schule_ad_pruefabschnitt_erfolgreich' -Wert 1 -Labels @{ abschnitt = $Name } `
            -Hilfe 'Ob der jeweilige AD-Pruefabschnitt durchgelaufen ist'
    } catch {
        Add-Metrik -Name 'schule_ad_pruefabschnitt_erfolgreich' -Wert 0 -Labels @{ abschnitt = $Name }
        Write-Verbose "Abschnitt '$Name' fehlgeschlagen: $($_.Exception.Message)"
    }
}

function Get-ServerAusDn {
    <#
        Aus "CN=NTDS Settings,CN=DC02,CN=Servers,CN=Standort,..." wird "DC02".
        Der Servername steht immer im zweiten CN-Bestandteil.
    #>
    param([string]$Dn)
    if (-not $Dn) { return 'unbekannt' }
    $teile = $Dn -split '(?<!\\),' | Where-Object { $_ -match '^\s*CN=' }
    if ($teile.Count -ge 2) {
        return ($teile[1] -replace '^\s*CN=', '').Trim()
    }
    if ($teile.Count -ge 1) {
        return ($teile[0] -replace '^\s*CN=', '').Trim()
    }
    return 'unbekannt'
}

# ===================================================================== #
# Vorbedingung: Nur auf einem Domaenencontroller sinnvoll
# ===================================================================== #
$IstDomaenencontroller = $false
try {
    # DomainRole 4 = Sicherungs-DC, 5 = primaerer DC. Die Zahl ist
    # sprachunabhaengig, der Anzeigename waere es nicht.
    $rolle = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop).DomainRole
    $IstDomaenencontroller = ($rolle -eq 4 -or $rolle -eq 5)
} catch {
    Write-Verbose "Rolle nicht ermittelbar: $($_.Exception.Message)"
}

Add-Metrik -Name 'schule_ad_ist_domaenencontroller' -Wert ([int]$IstDomaenencontroller) `
    -Hilfe '1 = dieses Geraet ist ein Domaenencontroller'

if (-not $IstDomaenencontroller) {
    # Nichts weiter zu tun. Die Datei wird trotzdem geschrieben, damit
    # sichtbar bleibt, dass das Skript gelaufen ist.
    $Zieldatei = Join-Path $AusgabePfad 'schule_ad.prom'
    $temp = "$Zieldatei.tmp"
    [System.IO.File]::WriteAllLines($temp, $Zeilen, (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $temp -Destination $Zieldatei -Force
    exit 0
}

$AdModulDa = $null -ne (Get-Module -ListAvailable -Name ActiveDirectory)
Add-Metrik -Name 'schule_ad_modul_vorhanden' -Wert ([int]$AdModulDa) `
    -Hilfe '1 = das PowerShell-Modul ActiveDirectory steht zur Verfuegung'

if ($AdModulDa) {
    Import-Module ActiveDirectory -ErrorAction SilentlyContinue
}

# ===================================================================== #
# Replikation zwischen den Domaenencontrollern
#
# Der wichtigste Abschnitt. Bricht die Replikation, merkt man es tagelang
# nicht – bis Gruppenrichtlinien auf der Haelfte der Rechner veraltet sind
# und Kennwortaenderungen nicht mehr ankommen.
# ===================================================================== #
Invoke-Abschnitt 'replikation' {
    if (-not $AdModulDa) { return }

    $partner = Get-ADReplicationPartnerMetadata -Target $env:COMPUTERNAME `
        -Partition * -ErrorAction SilentlyContinue

    if (-not $partner) {
        # Keine Replikationspartner ist bei einer Domaene mit nur einem
        # Domaenencontroller voellig normal – und das ist in Schulen der
        # haeufigste Fall. Ohne diese Unterscheidung wuerde der Abschnitt
        # dort dauerhaft als fehlgeschlagen gelten und eine Meldung
        # erzeugen, die niemand abstellen kann.
        $anzahlDcs = 1
        try {
            $anzahlDcs = @((Get-ADDomain -ErrorAction Stop).ReplicaDirectoryServers).Count
        } catch {
            Write-Verbose "Anzahl der Domaenencontroller nicht ermittelbar: $($_.Exception.Message)"
        }

        Add-Metrik -Name 'schule_ad_replikation_partner_gesamt' -Wert 0 `
            -Hilfe 'Anzahl der Replikationsbeziehungen dieses Domaenencontrollers'
        Add-Metrik -Name 'schule_ad_replikation_fehler_gesamt' -Wert 0 `
            -Hilfe 'Anzahl der Replikationsbeziehungen mit Fehler'

        if ($anzahlDcs -gt 1) {
            # Mehrere DCs, aber kein Partner: Das ist sehr wohl ein Befund.
            throw "Keine Replikationspartner gefunden, obwohl die Domaene $anzahlDcs Domaenencontroller hat."
        }
        return
    }

    $fehlerGesamt = 0
    $anzahl = 0

    foreach ($eintrag in $partner) {
        $anzahl++
        $partnerName = Get-ServerAusDn $eintrag.Partner
        $partition = ($eintrag.Partition -split '(?<!\\),')[0] -replace '^(CN|DC)=', ''

        $labels = @{ partner = $partnerName; partition = $partition }

        # LastReplicationResult: 0 = in Ordnung, alles andere ist ein
        # Win32-Fehlercode. Der Code selbst hilft beim Nachschlagen.
        $ergebnis = [int]($eintrag.LastReplicationResult)
        Add-Metrik -Name 'schule_ad_replikation_fehlercode' -Wert $ergebnis -Labels $labels `
            -Hilfe 'Ergebnis des letzten Replikationsversuchs, 0 = in Ordnung'

        if ($ergebnis -ne 0) { $fehlerGesamt++ }

        $misserfolge = 0
        if ($null -ne $eintrag.ConsecutiveReplicationFailures) {
            $misserfolge = [int]$eintrag.ConsecutiveReplicationFailures
        }
        Add-Metrik -Name 'schule_ad_replikation_fehlversuche_hintereinander' `
            -Wert $misserfolge -Labels $labels `
            -Hilfe 'Anzahl der Replikationsversuche in Folge, die fehlgeschlagen sind'

        # Wie lange ist die letzte erfolgreiche Replikation her? Das ist
        # aussagekraeftiger als der Fehlercode: Ein Partner kann seit
        # Tagen still sein, ohne je einen Fehler gemeldet zu haben.
        if ($eintrag.LastReplicationSuccess) {
            $alter = ((Get-Date) - $eintrag.LastReplicationSuccess).TotalSeconds
            if ($alter -lt 0) { $alter = 0 }
            Add-Metrik -Name 'schule_ad_replikation_letzter_erfolg_sekunden' `
                -Wert ([math]::Round($alter)) -Labels $labels `
                -Hilfe 'Sekunden seit der letzten erfolgreichen Replikation mit diesem Partner'
        }
    }

    Add-Metrik -Name 'schule_ad_replikation_partner_gesamt' -Wert $anzahl `
        -Hilfe 'Anzahl der Replikationsbeziehungen dieses Domaenencontrollers'
    Add-Metrik -Name 'schule_ad_replikation_fehler_gesamt' -Wert $fehlerGesamt `
        -Hilfe 'Anzahl der Replikationsbeziehungen mit Fehler'
}

# ===================================================================== #
# SYSVOL und NETLOGON
#
# Klemmt die Replikation des SYSVOL, werden keine Gruppenrichtlinien mehr
# verteilt. In dieser Umgebung besonders heikel: Das Agent-Skript liegt
# dort und kaeme auf neuen Rechnern nicht mehr an.
# ===================================================================== #
Invoke-Abschnitt 'sysvol' {

    foreach ($freigabe in @('SYSVOL', 'NETLOGON')) {
        $vorhanden = $false
        try {
            $vorhanden = $null -ne (Get-SmbShare -Name $freigabe -ErrorAction Stop)
        } catch {
            $vorhanden = $false
        }
        Add-Metrik -Name 'schule_ad_freigabe_vorhanden' -Wert ([int]$vorhanden) `
            -Labels @{ freigabe = $freigabe } `
            -Hilfe '1 = die Freigabe ist vorhanden. Fehlt sie, verteilt der DC keine Richtlinien.'
    }

    # --- DFSR-Zustand der replizierten Ordner --------------------------
    # State: 0 unbekannt, 1 bereit, 2 Erstsynchronisierung,
    #        3 automatische Wiederherstellung, 4 normal, 5 Fehler
    $ordner = Get-CimInstance -Namespace 'root\MicrosoftDfs' `
        -ClassName 'DfsrReplicatedFolderInfo' -ErrorAction SilentlyContinue

    $dfsrDa = $null -ne $ordner
    Add-Metrik -Name 'schule_ad_dfsr_vorhanden' -Wert ([int]$dfsrDa) `
        -Hilfe '1 = DFSR ist eingerichtet (der heutige Weg der SYSVOL-Replikation)'

    $imFehler = 0
    foreach ($eintrag in $ordner) {
        $zustand = 0
        if ($null -ne $eintrag.State) { $zustand = [int]$eintrag.State }
        Add-Metrik -Name 'schule_ad_dfsr_zustand' -Wert $zustand `
            -Labels @{
                ordner = [string]$eintrag.ReplicatedFolderName
                gruppe = [string]$eintrag.ReplicationGroupName
            } `
            -Hilfe 'DFSR-Zustand: 4 = normal, 5 = Fehler, 2 = Erstsynchronisierung'
        if ($zustand -eq 5) { $imFehler++ }
    }
    Add-Metrik -Name 'schule_ad_dfsr_ordner_im_fehler' -Wert $imFehler `
        -Hilfe 'Anzahl der DFSR-Ordner im Fehlerzustand'

    # --- Wird SYSVOL noch mit dem alten FRS repliziert? ----------------
    # LocalState 3 = Migration auf DFSR abgeschlossen. Alles darunter
    # heisst: laeuft noch ganz oder teilweise ueber FRS. FRS gibt es ab
    # Server 2016 nicht mehr – ein DC-Neuzugang scheitert dann.
    $migration = -1
    try {
        $schluessel = 'HKLM:\SYSTEM\CurrentControlSet\Services\DFSR\Parameters\SysVols\Migrating Sysvols'
        $wert = Get-ItemProperty -Path $schluessel -Name 'LocalState' -ErrorAction Stop
        $migration = [int]$wert.LocalState
    } catch {
        Write-Verbose 'SYSVOL-Migrationszustand nicht lesbar (bei reinen DFSR-Umgebungen normal).'
    }
    Add-Metrik -Name 'schule_ad_sysvol_migrationszustand' -Wert $migration `
        -Hilfe '3 = vollstaendig auf DFSR umgestellt, kleiner = noch FRS beteiligt, -1 = nicht lesbar'
}

# ===================================================================== #
# FSMO-Rollen
# ===================================================================== #
Invoke-Abschnitt 'fsmo' {
    if (-not $AdModulDa) { return }

    $wald = Get-ADForest -ErrorAction Stop
    $domaene = Get-ADDomain -ErrorAction Stop

    $rollen = [ordered]@{
        schemamaster          = $wald.SchemaMaster
        domaenennamenmaster   = $wald.DomainNamingMaster
        pdc_emulator          = $domaene.PDCEmulator
        rid_master            = $domaene.RIDMaster
        infrastrukturmaster   = $domaene.InfrastructureMaster
    }

    foreach ($rolle in $rollen.Keys) {
        $inhaber = [string]$rollen[$rolle]
        if (-not $inhaber) { continue }

        Add-Metrik -Name 'schule_ad_fsmo_inhaber' -Wert 1 `
            -Labels @{ rolle = $rolle; inhaber = $inhaber } `
            -Hilfe 'Welcher Domaenencontroller welche FSMO-Rolle haelt'

        # Antwortet der Halter ueberhaupt auf LDAP?
        $erreichbar = 0
        try {
            $verbindung = New-Object System.Net.Sockets.TcpClient
            $versuch = $verbindung.BeginConnect($inhaber, 389, $null, $null)
            if ($versuch.AsyncWaitHandle.WaitOne(3000, $false) -and $verbindung.Connected) {
                $erreichbar = 1
                $verbindung.EndConnect($versuch)
            }
            $verbindung.Close()
        } catch {
            $erreichbar = 0
        }

        Add-Metrik -Name 'schule_ad_fsmo_erreichbar' -Wert $erreichbar `
            -Labels @{ rolle = $rolle; inhaber = $inhaber } `
            -Hilfe '1 = der Halter der Rolle nimmt LDAP-Verbindungen an'
    }

    Add-Metrik -Name 'schule_ad_domaenencontroller_gesamt' `
        -Wert (@($domaene.ReplicaDirectoryServers).Count) `
        -Hilfe 'Anzahl der Domaenencontroller in dieser Domaene'
}

# ===================================================================== #
# Antwortzeit einer LDAP-Anfrage
#
# Der beste Fruehindikator fuer "die Anmeldung dauert heute so lang".
# Gemessen wird gegen den eigenen Verzeichnisdienst, damit die Zahl nicht
# von der Netzstrecke abhaengt.
# ===================================================================== #
Invoke-Abschnitt 'ldap' {
    $uhr = [System.Diagnostics.Stopwatch]::StartNew()
    $erfolgreich = 0
    try {
        $sucher = New-Object System.DirectoryServices.DirectorySearcher
        $sucher.SearchRoot = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$env:COMPUTERNAME/RootDSE")
        $sucher.Filter = '(objectClass=*)'
        $sucher.SearchScope = 'Base'
        $sucher.ClientTimeout = [TimeSpan]::FromSeconds(10)
        $null = $sucher.FindOne()
        $erfolgreich = 1
    } catch {
        Write-Verbose "LDAP-Abfrage fehlgeschlagen: $($_.Exception.Message)"
    } finally {
        $uhr.Stop()
    }

    Add-Metrik -Name 'schule_ad_ldap_erreichbar' -Wert $erfolgreich `
        -Hilfe '1 = eine LDAP-Anfrage an den eigenen Verzeichnisdienst war erfolgreich'
    if ($erfolgreich) {
        Add-Metrik -Name 'schule_ad_ldap_antwortzeit_sekunden' `
            -Wert ([math]::Round($uhr.Elapsed.TotalSeconds, 4)) `
            -Hilfe 'Dauer einer einfachen LDAP-Anfrage gegen den eigenen Verzeichnisdienst'
    }

    # Globaler Katalog auf Port 3268 – ohne ihn schlagen Anmeldungen in
    # Umgebungen mit mehreren Domaenen fehl.
    $gcOffen = 0
    try {
        $verbindung = New-Object System.Net.Sockets.TcpClient
        $versuch = $verbindung.BeginConnect($env:COMPUTERNAME, 3268, $null, $null)
        if ($versuch.AsyncWaitHandle.WaitOne(3000, $false) -and $verbindung.Connected) {
            $gcOffen = 1
            $verbindung.EndConnect($versuch)
        }
        $verbindung.Close()
    } catch {
        $gcOffen = 0
    }
    Add-Metrik -Name 'schule_ad_globaler_katalog_erreichbar' -Wert $gcOffen `
        -Hilfe '1 = dieser DC beantwortet Anfragen an den globalen Katalog (Port 3268)'
}

# ===================================================================== #
# Kontosperrungen
#
# Ein ploetzlicher Anstieg ist entweder ein Kennwortangriff oder ein
# Geraet mit gespeichertem altem Kennwort. Beides will man wissen, und
# der Unterschied zeigt sich daran, ob es viele Konten sind oder immer
# dasselbe.
# ===================================================================== #
Invoke-Abschnitt 'kontosperrungen' {

    foreach ($fenster in @(
        @{ Stunden = 1;  Name = 'schule_ad_kontosperrungen_1h' }
        @{ Stunden = 24; Name = 'schule_ad_kontosperrungen_24h' }
    )) {
        $seit = (Get-Date).AddHours(-$fenster.Stunden)
        $anzahl = 0
        try {
            # 4740 = Ein Benutzerkonto wurde gesperrt.
            $ereignisse = Get-WinEvent -FilterHashtable @{
                LogName = 'Security'; Id = 4740; StartTime = $seit
            } -ErrorAction Stop
            $anzahl = @($ereignisse).Count
        } catch {
            # Kein Treffer wirft ebenfalls eine Ausnahme – das ist der
            # Normalfall und bedeutet schlicht null.
            $anzahl = 0
        }
        Add-Metrik -Name $fenster.Name -Wert $anzahl `
            -Hilfe 'Anzahl der Kontosperrungen im Zeitfenster'
    }

    # Wie viele Konten sind gerade gesperrt? Der Bestand sagt etwas
    # anderes als die Rate: viele gleichzeitig gesperrte Konten sind ein
    # deutlicheres Zeichen als eine Sperrung hier und da.
    if ($AdModulDa) {
        try {
            $gesperrt = @(Search-ADAccount -LockedOut -UsersOnly -ErrorAction Stop)
            Add-Metrik -Name 'schule_ad_konten_gesperrt' -Wert $gesperrt.Count `
                -Hilfe 'Anzahl der derzeit gesperrten Benutzerkonten'
        } catch {
            Write-Verbose "Gesperrte Konten nicht abfragbar: $($_.Exception.Message)"
        }
    }
}

# ===================================================================== #
# Abschluss
# ===================================================================== #
Add-Metrik -Name 'schule_ad_pruefung_dauer_sekunden' `
    -Wert ([math]::Round(((Get-Date) - $Beginn).TotalSeconds, 2)) `
    -Hilfe 'Laufzeit dieser AD-Pruefung'

Add-Metrik -Name 'schule_ad_pruefung_zeitstempel' `
    -Wert ([int64][System.DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) `
    -Hilfe 'Zeitpunkt der letzten AD-Pruefung (Unixzeit)'

if (-not (Test-Path -LiteralPath $AusgabePfad)) {
    New-Item -ItemType Directory -Path $AusgabePfad -Force | Out-Null
}

# Erst in eine temporaere Datei, dann umbenennen: windows_exporter soll
# nie eine halb geschriebene Datei einlesen.
$Zieldatei = Join-Path $AusgabePfad 'schule_ad.prom'
$temp = "$Zieldatei.tmp"
[System.IO.File]::WriteAllLines($temp, $Zeilen, (New-Object Text.UTF8Encoding $false))
Move-Item -LiteralPath $temp -Destination $Zieldatei -Force

Write-Verbose "AD-Pruefung fertig: $($Zeilen.Count) Zeilen nach $Zieldatei"
