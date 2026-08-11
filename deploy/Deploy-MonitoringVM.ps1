#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Richtet die komplette Monitoring-Umgebung der Schule als Hyper-V-VM ein.

.DESCRIPTION
    Das ist das einzige Skript, das von Hand gestartet werden muss. Es

      1. laedt das Ubuntu-Server-Abbild (oder nimmt ein vorhandenes),
      2. baut daraus ein ISO, das sich vollautomatisch installiert,
      3. legt die VM in Hyper-V an und startet sie,
      4. wartet, bis sich die VM selbst fertig eingerichtet hat,
      5. gibt am Ende Adresse, Kennwort und Agent-Token aus.

    Danach ist nur noch der Rollout auf den Windows-Geraeten per
    Gruppenrichtlinie noetig – dafuer gibt das Skript den fertigen Befehl aus.

.PARAMETER VMName
    Name der virtuellen Maschine in Hyper-V.

.PARAMETER SwitchName
    Name des virtuellen Switches. Ohne Angabe wird der erste externe Switch
    genommen.

.PARAMETER IPAdresse
    Feste IP-Adresse inklusive Praefixlaenge, z. B. "10.0.0.50/24".
    Ohne Angabe holt sich die VM ihre Adresse per DHCP.

.PARAMETER Gateway
    Standardgateway, wenn eine feste IP verwendet wird.

.PARAMETER DnsServer
    DNS-Server der Schule. Standard: Gateway.

.PARAMETER AdminBenutzer
    Anmeldename fuer die Linux-VM.

.PARAMETER AdminKennwort
    Kennwort fuer diesen Benutzer. Ohne Angabe wird eines erzeugt und
    am Ende ausgegeben.

.PARAMETER SshOeffentlicherSchluessel
    Optional: Inhalt eines oeffentlichen SSH-Schluessels fuer die Anmeldung.

.PARAMETER IsoPfad
    Vorhandenes Ubuntu-Server-ISO. Ohne Angabe wird es heruntergeladen.

.PARAMETER SmtpHost
    Mailserver fuer die Alarm-Mails.

.PARAMETER AlarmEmpfaenger
    E-Mail-Adresse, an die Alarme gehen.

.PARAMETER UnifiUrl
    Adresse des UniFi-Controllers, z. B. https://10.0.0.10:8443

.PARAMETER UnifiBenutzer
    Nur-Lese-Benutzer im UniFi-Controller.

.PARAMETER UnifiKennwort
    Kennwort dieses Benutzers.

.PARAMETER FortiGateUrl
    Adresse der FortiGate, z. B. https://10.0.0.1

.PARAMETER FortiGateToken
    API-Token der FortiGate (Rolle "read only").

.PARAMETER BasisVhdxPfad
    Rueckfallebene: ein bereits vorhandenes Ubuntu-24.04-VHDX-Abbild.
    Dann entfaellt die Installation komplett und es wird nur noch per
    cloud-init eingerichtet. Nuetzlich, wenn der Umbau des ISO scheitert.

.PARAMETER Ueberschreiben
    Vorhandene VM gleichen Namens vorher entfernen.

.EXAMPLE
    .\Deploy-MonitoringVM.ps1 -IPAdresse 10.0.0.50/24 -Gateway 10.0.0.1 `
        -DnsServer 10.0.0.20 -AlarmEmpfaenger admin@schule.local

.EXAMPLE
    .\Deploy-MonitoringVM.ps1 -SwitchName "LAN" -UnifiUrl https://10.0.0.10:8443 `
        -UnifiBenutzer monitoring -UnifiKennwort "geheim" `
        -FortiGateUrl https://10.0.0.1 -FortiGateToken "abc123"
#>
[CmdletBinding()]
param(
    [string]$VMName = 'SchulMonitoring',
    [string]$SwitchName,
    [ValidateRange(2, 32)][int]$ProzessorKerne = 4,
    [ValidateRange(4, 256)][int]$ArbeitsspeicherGB = 8,
    [ValidateRange(60, 4096)][int]$DatentraegerGB = 200,
    [string]$VMPfad,

    [string]$IPAdresse,
    [string]$Gateway,
    [string[]]$DnsServer,
    [string]$DnsSuchdomaene = 'schule.local',
    [string]$Hostname = 'monitoring',

    [string]$AdminBenutzer = 'monadmin',
    [string]$AdminKennwort,
    [string]$SshOeffentlicherSchluessel,

    [string]$Zeitzone = 'Europe/Vienna',
    [string]$Standort = 'Schule',

    [string]$IsoPfad,
    [string]$BasisVhdxPfad,
    [string]$ArbeitsVerzeichnis = "$env:ProgramData\SchulMonitoring\Bereitstellung",

    [string]$SmtpHost,
    [int]$SmtpPort = 587,
    [string]$SmtpBenutzer,
    [string]$SmtpKennwort,
    [string]$SmtpVon = 'monitoring@schule.local',
    [string]$AlarmEmpfaenger,

    [string]$UnifiUrl,
    [string]$UnifiBenutzer,
    [string]$UnifiKennwort,
    [string]$FortiGateUrl,
    [string]$FortiGateToken,
    [string]$SnmpCommunity = 'public',

    [string[]]$ErkennungNetze,
    [ValidateSet('automatisch', 'vorschlag', 'aus')][string]$ErkennungModus = 'automatisch',

    [int]$WartezeitMinuten = 45,
    [switch]$Ueberschreiben,
    [switch]$NurVorbereiten
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # macht Invoke-WebRequest um ein Vielfaches schneller

$SkriptVerzeichnis = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjektVerzeichnis = Split-Path -Parent $SkriptVerzeichnis

Import-Module (Join-Path $SkriptVerzeichnis 'lib\IsoWerkzeuge.psm1') -Force

# ===================================================================== #
# Ausgabe
# ===================================================================== #
$script:Startzeit = Get-Date

function Write-Titel {
    param([string]$Text)
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor DarkCyan
    Write-Host " $Text" -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor DarkCyan
}

function Write-Schritt { param([string]$Text) Write-Host "  -> $Text" -ForegroundColor White }
function Write-Erfolg  { param([string]$Text) Write-Host "  [OK] $Text" -ForegroundColor Green }
function Write-Hinweis { param([string]$Text) Write-Host "  [i]  $Text" -ForegroundColor DarkGray }
function Write-Warnung { param([string]$Text) Write-Host "  [!]  $Text" -ForegroundColor Yellow }

function New-Geheimnis {
    param([int]$Laenge = 32)
    $zeichen = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $puffer = New-Object byte[] $Laenge
    $zufall = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $zufall.GetBytes($puffer) } finally { $zufall.Dispose() }
    -join ($puffer | ForEach-Object { $zeichen[$_ % $zeichen.Length] })
}

# ===================================================================== #
# 1. Voraussetzungen pruefen
# ===================================================================== #
function Test-Voraussetzungen {
    Write-Titel 'Voraussetzungen pruefen'

    if (-not (Get-Module -ListAvailable -Name Hyper-V)) {
        throw 'Das Hyper-V-PowerShell-Modul fehlt. Bitte die Hyper-V-Verwaltungswerkzeuge installieren.'
    }
    Import-Module Hyper-V -ErrorAction Stop
    Write-Erfolg 'Hyper-V-Modul geladen'

    try {
        $dienst = Get-Service -Name vmms -ErrorAction Stop
        if ($dienst.Status -ne 'Running') {
            throw 'Der Hyper-V-Verwaltungsdienst (vmms) laeuft nicht.'
        }
    } catch {
        throw "Hyper-V ist auf diesem Rechner nicht einsatzbereit: $($_.Exception.Message)"
    }
    Write-Erfolg 'Hyper-V laeuft'

    # --- Virtueller Switch ---
    if (-not $script:GewaehlterSwitch) {
        if ($SwitchName) {
            $sw = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
            if (-not $sw) {
                $vorhandene = (Get-VMSwitch | Select-Object -ExpandProperty Name) -join ', '
                throw "Der virtuelle Switch '$SwitchName' existiert nicht. Vorhanden: $vorhandene"
            }
        } else {
            $sw = Get-VMSwitch | Where-Object SwitchType -eq 'External' | Select-Object -First 1
            if (-not $sw) { $sw = Get-VMSwitch | Select-Object -First 1 }
            if (-not $sw) {
                throw 'Es ist kein virtueller Switch vorhanden. Bitte in Hyper-V einen externen Switch anlegen.'
            }
            Write-Hinweis "Kein Switch angegeben – verwende '$($sw.Name)' ($($sw.SwitchType))"
        }
        $script:GewaehlterSwitch = $sw
    }
    Write-Erfolg "Virtueller Switch: $($script:GewaehlterSwitch.Name)"

    # --- Vorhandene VM ---
    $vorhandeneVm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if ($vorhandeneVm) {
        if (-not $Ueberschreiben) {
            throw "Es gibt bereits eine VM namens '$VMName'. Mit -Ueberschreiben wird sie ersetzt."
        }
        Write-Warnung "Vorhandene VM '$VMName' wird entfernt"
        if ($vorhandeneVm.State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
        Get-VMHardDiskDrive -VMName $VMName | ForEach-Object {
            if (Test-Path -LiteralPath $_.Path) { Remove-Item -LiteralPath $_.Path -Force }
        }
        Remove-VM -Name $VMName -Force
        Write-Erfolg 'Alte VM entfernt'
    }

    # --- Speicherort ---
    if (-not $VMPfad) {
        $script:ZielVMPfad = (Get-VMHost).VirtualMachinePath
    } else {
        $script:ZielVMPfad = $VMPfad
    }
    if (-not (Test-Path -LiteralPath $script:ZielVMPfad)) {
        New-Item -ItemType Directory -Path $script:ZielVMPfad -Force | Out-Null
    }

    # --- Platz ---
    $laufwerk = (Get-Item -LiteralPath $script:ZielVMPfad).PSDrive
    if ($laufwerk -and $laufwerk.Free) {
        $freiGB = [math]::Round($laufwerk.Free / 1GB, 1)
        $benoetigtGB = 20
        if ($freiGB -lt $benoetigtGB) {
            throw "Auf $($laufwerk.Name): sind nur $freiGB GB frei, mindestens $benoetigtGB GB werden gebraucht."
        }
        Write-Erfolg "Speicherort $script:ZielVMPfad ($freiGB GB frei)"
    }

    New-Item -ItemType Directory -Path $ArbeitsVerzeichnis -Force | Out-Null
    Write-Erfolg "Arbeitsverzeichnis $ArbeitsVerzeichnis"
}

# ===================================================================== #
# 2. Ubuntu-ISO besorgen
# ===================================================================== #
function Get-UbuntuIso {
    Write-Titel 'Ubuntu-Server-Abbild bereitstellen'

    if ($IsoPfad) {
        if (-not (Test-Path -LiteralPath $IsoPfad)) {
            throw "Das angegebene ISO wurde nicht gefunden: $IsoPfad"
        }
        Write-Erfolg "Verwende vorhandenes ISO: $IsoPfad"
        return (Get-Item -LiteralPath $IsoPfad).FullName
    }

    $basisUrl = 'https://releases.ubuntu.com/24.04/'
    Write-Schritt "Suche aktuelle Fassung unter $basisUrl"

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    try {
        $verzeichnis = Invoke-WebRequest -Uri $basisUrl -UseBasicParsing -TimeoutSec 60
    } catch {
        throw @"
Die Ubuntu-Downloadseite ist nicht erreichbar ($($_.Exception.Message)).
Bitte das ISO von Hand herunterladen und mit -IsoPfad uebergeben:
  https://releases.ubuntu.com/24.04/
"@
    }

    $treffer = [regex]::Matches($verzeichnis.Content, 'ubuntu-24\.04(?:\.\d+)?-live-server-amd64\.iso') |
        ForEach-Object { $_.Value } | Sort-Object -Unique | Select-Object -Last 1

    if (-not $treffer) { throw 'Auf der Downloadseite wurde kein passendes ISO gefunden.' }

    $isoUrl = "$basisUrl$treffer"
    $ziel = Join-Path $ArbeitsVerzeichnis $treffer

    if (Test-Path -LiteralPath $ziel) {
        Write-Erfolg "Bereits heruntergeladen: $treffer"
    } else {
        Write-Schritt "Lade $treffer (etwa 3 GB, das dauert ein paar Minuten)"
        $temp = "$ziel.teil"
        Invoke-WebRequest -Uri $isoUrl -OutFile $temp -UseBasicParsing -TimeoutSec 7200
        Move-Item -LiteralPath $temp -Destination $ziel -Force
        Write-Erfolg "Heruntergeladen: $treffer"
    }

    # --- Pruefsumme kontrollieren -------------------------------------
    try {
        Write-Schritt 'Pruefsumme kontrollieren'
        $summen = (Invoke-WebRequest -Uri "${basisUrl}SHA256SUMS" -UseBasicParsing -TimeoutSec 60).Content
        $erwartet = ($summen -split "`n" | Where-Object { $_ -match [regex]::Escape($treffer) } |
            Select-Object -First 1) -split '\s+' | Select-Object -First 1

        if ($erwartet) {
            $tatsaechlich = (Get-FileHash -LiteralPath $ziel -Algorithm SHA256).Hash
            if ($tatsaechlich -ine $erwartet) {
                Remove-Item -LiteralPath $ziel -Force
                throw "Die Pruefsumme stimmt nicht. Die Datei wurde geloescht, bitte das Skript erneut starten."
            }
            Write-Erfolg 'Pruefsumme in Ordnung'
        } else {
            Write-Warnung 'Pruefsumme konnte nicht ermittelt werden – wird uebersprungen.'
        }
    } catch [System.Net.WebException] {
        Write-Warnung 'SHA256SUMS nicht abrufbar – Pruefsumme wird uebersprungen.'
    }

    return $ziel
}

# ===================================================================== #
# 3. cloud-init erzeugen
# ===================================================================== #
function New-CloudInitDaten {
    param(
        [Parameter(Mandatory)][string]$Zielverzeichnis,
        # Bei einem fertigen VHDX-Abbild laeuft cloud-init direkt – dann darf
        # die autoinstall-Klammer nicht drumherum, die nur der Installer kennt.
        [switch]$FuerFertigesAbbild
    )

    Write-Titel 'Automatische Installation vorbereiten'

    New-Item -ItemType Directory -Path $Zielverzeichnis -Force | Out-Null

    # --- Netzwerkteil -------------------------------------------------
    if ($IPAdresse) {
        if ($IPAdresse -notmatch '^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$') {
            throw "IPAdresse muss die Form 10.0.0.50/24 haben, angegeben war '$IPAdresse'."
        }
        if (-not $Gateway) { throw 'Bei fester IP-Adresse muss auch -Gateway angegeben werden.' }
        $namensserver = if ($DnsServer) { $DnsServer } else { @($Gateway) }
        $dnsListe = ($namensserver | ForEach-Object { "`"$_`"" }) -join ', '

        $netzwerk = @"
    version: 2
    ethernets:
      hauptkarte:
        match:
          name: "e*"
        dhcp4: false
        addresses: ["$IPAdresse"]
        routes:
          - to: default
            via: $Gateway
        nameservers:
          addresses: [$dnsListe]
          search: ["$DnsSuchdomaene"]
"@
        $script:VorhergesagteIp = $IPAdresse.Split('/')[0]
    } else {
        $netzwerk = @"
    version: 2
    ethernets:
      hauptkarte:
        match:
          name: "e*"
        dhcp4: true
        dhcp6: false
"@
        $script:VorhergesagteIp = $null
        Write-Hinweis 'Keine feste IP angegeben – die VM holt sich eine Adresse per DHCP.'
    }

    # --- SSH-Schluessel ----------------------------------------------
    $sshBlock = if ($SshOeffentlicherSchluessel) {
        "    authorized-keys:`n      - `"$SshOeffentlicherSchluessel`""
    } else { '' }

    # --- Konfigurationsdatei der Anwendung ---------------------------
    $envInhalt = New-EnvInhalt
    $envKodiert = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($envInhalt))

    if ($FuerFertigesAbbild) {
        New-CloudInitFuerAbbild -Zielverzeichnis $Zielverzeichnis `
            -EnvKodiert $envKodiert -Netzwerk $netzwerk
        return
    }

    # --- user-data ----------------------------------------------------
    $userData = @"
#cloud-config
autoinstall:
  version: 1

  # Nichts nachfragen – das Skript soll komplett ohne Zutun durchlaufen.
  interactive-sections: []

  locale: de_AT.UTF-8
  keyboard:
    layout: de
    variant: nodeadkeys

  timezone: $Zeitzone

  identity:
    hostname: $Hostname
    username: $AdminBenutzer
    # Wird weiter unten in late-commands richtig gesetzt.
    password: "!"

  ssh:
    install-server: true
    allow-pw: true
$sshBlock

  storage:
    layout:
      name: direct

  network:
$netzwerk

  apt:
    preserve_sources_list: false

  packages:
    - ca-certificates
    - curl
    - gnupg
    - python3
    - python3-yaml
    - chrony
    - unattended-upgrades
    - linux-tools-virtual
    - linux-cloud-tools-virtual

  # Sicherheitsupdates waehrend der Installation gleich mitnehmen
  updates: security

  shutdown: poweroff

  late-commands:
    # 1) Kennwort des Verwaltungsbenutzers setzen
    - curtin in-target --target=/target -- bash -c 'echo "$($AdminBenutzer):$($script:LinuxKennwort)" | chpasswd'

    # 2) Projektdateien von der Installations-CD uebernehmen
    - mkdir -p /target/opt/schulmonitoring
    - tar -xzf /cdrom/nocloud/schulmonitoring.tar.gz -C /target/opt/schulmonitoring
    - chmod +x /target/opt/schulmonitoring/stack/bootstrap.sh /target/opt/schulmonitoring/stack/pakete-holen.sh

    # 3) Vorbelegte Konfiguration ablegen
    - bash -c 'echo "$envKodiert" | base64 -d > /target/opt/schulmonitoring/stack/.env'
    - chmod 600 /target/opt/schulmonitoring/stack/.env

    # 4) Dienst, der die Einrichtung beim ersten Start uebernimmt
    - |
      cat > /target/etc/systemd/system/schulmonitoring-einrichtung.service <<'DIENST'
      [Unit]
      Description=Schul-Monitoring beim ersten Start einrichten
      After=network-online.target
      Wants=network-online.target
      ConditionPathExists=!/opt/schulmonitoring/.eingerichtet

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      TimeoutStartSec=3600
      ExecStart=/opt/schulmonitoring/stack/bootstrap.sh
      ExecStartPost=/usr/bin/touch /opt/schulmonitoring/.eingerichtet
      StandardOutput=journal+console
      StandardError=journal+console

      [Install]
      WantedBy=multi-user.target
      DIENST
    - curtin in-target --target=/target -- systemctl enable schulmonitoring-einrichtung.service

    # 5) Automatische Sicherheitsupdates einschalten
    - curtin in-target --target=/target -- systemctl enable unattended-upgrades

  user-data:
    package_update: false
    package_upgrade: false
    timezone: $Zeitzone
    write_files:
      - path: /etc/motd
        permissions: '0644'
        content: |
          ------------------------------------------------------------
           Schul-Monitoring
           Dashboard:      http://<diese-adresse>/
           Konfiguration:  /opt/schulmonitoring/stack/.env
           Geraeteliste:   /opt/schulmonitoring/stack/inventar/inventar.yml
           Zugangsdaten:   /root/ZUGANGSDATEN.txt
           Neustart:       sudo systemctl restart schulmonitoring
          ------------------------------------------------------------
"@

    $metaData = @"
instance-id: schulmonitoring-$([guid]::NewGuid().ToString('N').Substring(0,12))
local-hostname: $Hostname
"@

    # Ohne abschliessenden Zeilenumbruch stolpert cloud-init gelegentlich.
    Set-Content -LiteralPath (Join-Path $Zielverzeichnis 'user-data') -Value $userData -Encoding UTF8 -NoNewline
    Add-Content -LiteralPath (Join-Path $Zielverzeichnis 'user-data') -Value "`n" -NoNewline -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Zielverzeichnis 'meta-data') -Value $metaData -Encoding UTF8

    Write-Erfolg 'Installationsvorgaben erstellt'
}

function New-CloudInitFuerAbbild {
    <#
        Variante fuer den Weg ueber ein bereits vorhandenes Ubuntu-VHDX.
        Hier laeuft cloud-init im fertigen System, es gibt also keinen
        Installer und damit auch keine autoinstall-Klammer.
    #>
    param(
        [Parameter(Mandatory)][string]$Zielverzeichnis,
        [Parameter(Mandatory)][string]$EnvKodiert,
        [Parameter(Mandatory)][string]$Netzwerk
    )

    $sshSchluessel = if ($SshOeffentlicherSchluessel) {
        "    ssh_authorized_keys:`n      - `"$SshOeffentlicherSchluessel`""
    } else { '' }

    $userData = @"
#cloud-config
hostname: $Hostname
fqdn: $Hostname.$DnsSuchdomaene
timezone: $Zeitzone
locale: de_AT.UTF-8

users:
  - name: $AdminBenutzer
    groups: [sudo, adm]
    shell: /bin/bash
    lock_passwd: false
    sudo: "ALL=(ALL) NOPASSWD:ALL"
$sshSchluessel

ssh_pwauth: true

chpasswd:
  expire: false
  users:
    - name: $AdminBenutzer
      password: "$($script:LinuxKennwort)"
      type: text

package_update: true
packages:
  - ca-certificates
  - curl
  - gnupg
  - python3
  - python3-yaml
  - chrony
  - linux-tools-virtual
  - linux-cloud-tools-virtual

write_files:
  - path: /opt/schulmonitoring-vorbelegt.env
    permissions: '0600'
    encoding: b64
    content: $EnvKodiert

  - path: /etc/systemd/system/schulmonitoring-einrichtung.service
    permissions: '0644'
    content: |
      [Unit]
      Description=Schul-Monitoring beim ersten Start einrichten
      After=network-online.target
      Wants=network-online.target
      ConditionPathExists=!/opt/schulmonitoring/.eingerichtet

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      TimeoutStartSec=3600
      ExecStart=/opt/schulmonitoring/stack/bootstrap.sh
      ExecStartPost=/usr/bin/touch /opt/schulmonitoring/.eingerichtet
      StandardOutput=journal+console
      StandardError=journal+console

      [Install]
      WantedBy=multi-user.target

runcmd:
  - [ mkdir, -p, /opt/schulmonitoring, /mnt/seed ]
  - [ sh, -c, "mount -o ro LABEL=CIDATA /mnt/seed || mount -o ro /dev/sr0 /mnt/seed" ]
  - [ sh, -c, "tar -xzf /mnt/seed/schulmonitoring.tar.gz -C /opt/schulmonitoring" ]
  - [ sh, -c, "mv /opt/schulmonitoring-vorbelegt.env /opt/schulmonitoring/stack/.env" ]
  - [ sh, -c, "chmod +x /opt/schulmonitoring/stack/bootstrap.sh /opt/schulmonitoring/stack/pakete-holen.sh" ]
  - [ umount, /mnt/seed ]
  - [ systemctl, daemon-reload ]
  - [ systemctl, enable, --now, schulmonitoring-einrichtung.service ]
"@

    $metaData = @"
instance-id: schulmonitoring-$([guid]::NewGuid().ToString('N').Substring(0,12))
local-hostname: $Hostname
"@

    Set-Content -LiteralPath (Join-Path $Zielverzeichnis 'user-data') -Value $userData -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Zielverzeichnis 'meta-data') -Value $metaData -Encoding UTF8

    # NoCloud liest die Netzwerkkonfiguration aus einer eigenen Datei
    Set-Content -LiteralPath (Join-Path $Zielverzeichnis 'network-config') `
        -Value $Netzwerk.TrimStart() -Encoding UTF8

    Write-Erfolg 'Konfiguration fuer das vorhandene Abbild erstellt'
}

function Get-ErkennungsNetze {
    <#
        Bestimmt, welche Netze der Suchlauf abklappern soll.

        Ohne Angabe wird das eigene Netz der VM genommen – wer eine feste
        IP vergibt, meint fast immer genau dieses Netz. Das erspart einen
        weiteren Parameter und liefert vom ersten Start an Ergebnisse.
        Weitere Netze (WLAN, Drucker, Klassenzimmer) traegt man spaeter in
        der .env nach; die Geraete tauchen dann beim naechsten Lauf auf.
    #>
    # Nur einmal ermitteln – die Zusammenfassung am Ende fragt noch einmal
    # nach und soll die Hinweise nicht ein zweites Mal ausgeben.
    if ($null -ne $script:ErkennungsNetzeWert) { return $script:ErkennungsNetzeWert }

    if ($ErkennungModus -eq 'aus') {
        $script:ErkennungsNetzeWert = ''
        return ''
    }

    if ($ErkennungNetze) {
        $script:ErkennungsNetzeWert = ($ErkennungNetze -join ',')
        return $script:ErkennungsNetzeWert
    }

    if (-not $IPAdresse) {
        Write-Hinweis 'Ohne feste IP kann das eigene Netz nicht bestimmt werden – Suchlauf bleibt vorerst aus.'
        Write-Hinweis 'Nachtragen in der .env:  ERKENNUNG_NETZE=10.0.0.0/24'
        $script:ErkennungsNetzeWert = ''
        return ''
    }

    $teile = $IPAdresse.Split('/')
    $laenge = [int]$teile[1]

    if ($laenge -lt 22) {
        Write-Warnung "Das eigene Netz /$laenge ist fuer einen Suchlauf sehr gross."
        Write-Hinweis 'Bitte in der .env engere Netze eintragen, z. B. ERKENNUNG_NETZE=10.0.0.0/24'
        $script:ErkennungsNetzeWert = ''
        return ''
    }

    # Netzadresse aus IP und Praefixlaenge errechnen
    $adressBytes = ([System.Net.IPAddress]::Parse($teile[0])).GetAddressBytes()
    [array]::Reverse($adressBytes)
    $adressZahl = [BitConverter]::ToUInt32($adressBytes, 0)
    # PowerShell rechnet -shl in Int64. Ohne das Abschneiden auf 32 Bit
    # laeuft der Rueckcast nach uint32 ueber.
    $maske = if ($laenge -eq 0) { [uint32]0 } else {
        [uint32]((([uint32]::MaxValue -shl (32 - $laenge))) -band 0xFFFFFFFF)
    }
    $netzZahl = $adressZahl -band $maske
    $netzBytes = [BitConverter]::GetBytes([uint32]$netzZahl)
    [array]::Reverse($netzBytes)
    $netz = "$([System.Net.IPAddress]::new($netzBytes))/$laenge"

    Write-Hinweis "Geraeteerkennung durchsucht das eigene Netz: $netz"
    return $netz
}

function New-EnvInhalt {
    <#
        Baut die .env fuer den Stack. Geheimnisse werden hier auf dem
        Hyper-V-Server erzeugt, damit dieses Skript sie am Ende auch
        ausgeben kann – sonst muesste man sich erst auf der VM anmelden.
    #>
    $zeilen = [System.Collections.Generic.List[string]]::new()
    $add = { param($s) $zeilen.Add($s) }

    & $add "# Automatisch erzeugt von Deploy-MonitoringVM.ps1 am $(Get-Date -Format 'dd.MM.yyyy HH:mm')"
    & $add "TZ=$Zeitzone"
    & $add "STANDORT=$Standort"
    & $add "MON_HOSTNAME=$(if ($script:VorhergesagteIp) { $script:VorhergesagteIp } else { $Hostname })"
    & $add "GRAFANA_ADMIN_USER=admin"
    & $add "GRAFANA_ADMIN_PASSWORT=$script:GrafanaKennwort"
    & $add "AGENT_TOKEN=$script:AgentToken"
    & $add "LOKI_BASICAUTH_HASH="
    & $add "METRIK_AUFBEWAHRUNG=365d"
    & $add "METRIK_MAX_GROESSE=$([math]::Max(10, [int]($DatentraegerGB * 0.35)))GB"
    & $add "LOG_AUFBEWAHRUNG=90d"
    & $add "AGENT_TIMEOUT_TAGE=30"
    & $add "SMTP_HOST=$(if ($SmtpHost) { $SmtpHost } else { 'smtp.schule.local' })"
    & $add "SMTP_PORT=$SmtpPort"
    & $add "SMTP_BENUTZER=$SmtpBenutzer"
    & $add "SMTP_PASSWORT=$SmtpKennwort"
    & $add "SMTP_VON=$SmtpVon"
    & $add "SMTP_TLS=true"
    $empfaenger = if ($AlarmEmpfaenger) { $AlarmEmpfaenger } else { 'admin@schule.local' }
    & $add "ALARM_EMPFAENGER=$empfaenger"
    & $add "ALARM_EMPFAENGER_KRITISCH=$empfaenger"
    & $add "ALARM_EMPFAENGER_SECURITY=$empfaenger"
    & $add "WEBHOOK_URL="
    & $add "UNIFI_URL=$UnifiUrl"
    & $add "UNIFI_BENUTZER=$UnifiBenutzer"
    & $add "UNIFI_PASSWORT=$UnifiKennwort"
    & $add "UNIFI_SITES=all"
    & $add "UNIFI_IDS=true"
    & $add "UNIFI_DPI=false"
    & $add "FORTIGATE_URL=$FortiGateUrl"
    & $add "FORTIGATE_TOKEN=$FortiGateToken"
    & $add "FORTIGATE_INSECURE=true"
    & $add "SNMP_COMMUNITY=$SnmpCommunity"
    & $add "SNMP_MODUL_STANDARD=if_mib"
    & $add "ERKENNUNG_NETZE=$(Get-ErkennungsNetze)"
    & $add "ERKENNUNG_MODUS=$(if ($ErkennungModus -eq 'aus') { 'vorschlag' } else { $ErkennungModus })"
    & $add "ERKENNUNG_INTERVALL_MINUTEN=60"
    & $add "ERKENNUNG_HOECHSTZAHL=4096"
    & $add "PROMETHEUS_IMAGE=prom/prometheus:v3.1.0"
    & $add "ALERTMANAGER_IMAGE=prom/alertmanager:v0.28.0"
    & $add "BLACKBOX_IMAGE=prom/blackbox-exporter:v0.25.0"
    & $add "SNMP_IMAGE=prom/snmp-exporter:v0.26.0"
    & $add "NODE_EXPORTER_IMAGE=prom/node-exporter:v1.8.2"
    & $add "CADVISOR_IMAGE=gcr.io/cadvisor/cadvisor:v0.49.1"
    & $add "LOKI_IMAGE=grafana/loki:3.4.1"
    & $add "GRAFANA_IMAGE=grafana/grafana:11.6.0"
    & $add "ALLOY_IMAGE=grafana/alloy:v1.7.1"
    & $add "CADDY_IMAGE=caddy:2.9-alpine"
    & $add "UNPOLLER_IMAGE=ghcr.io/unpoller/unpoller:latest"
    & $add "FORTIGATE_EXPORTER_IMAGE=ghcr.io/bluecmd/fortigate_exporter:latest"
    & $add "WINDOWS_EXPORTER_VERSION=0.30.5"
    & $add "ALLOY_WINDOWS_VERSION=1.7.1"

    return ($zeilen -join "`n") + "`n"
}

# ===================================================================== #
# 4. Projektdateien einpacken
# ===================================================================== #
function New-ProjektArchiv {
    param([string]$Zieldatei)

    Write-Schritt 'Projektdateien einpacken'

    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if (-not $tar) {
        throw @'
tar.exe wurde nicht gefunden. Es gehoert ab Windows Server 2019 zum
Lieferumfang. Auf aelteren Systemen bitte das Projekt von Hand nach
/opt/schulmonitoring auf die VM kopieren.
'@
    }

    if (Test-Path -LiteralPath $Zieldatei) { Remove-Item -LiteralPath $Zieldatei -Force }

    # tar mag Vorwaertsschraegstriche lieber
    $zielFuerTar = $Zieldatei -replace '\\', '/'

    Push-Location $ProjektVerzeichnis
    try {
        $auszupacken = @('stack', 'agent', 'docs', 'README.md') |
            Where-Object { Test-Path -LiteralPath (Join-Path $ProjektVerzeichnis $_) }

        & tar.exe --format=gnu -czf $zielFuerTar $auszupacken
        if ($LASTEXITCODE -ne 0) { throw "tar ist mit Code $LASTEXITCODE fehlgeschlagen." }
    }
    finally { Pop-Location }

    $groesseKB = [math]::Round((Get-Item -LiteralPath $Zieldatei).Length / 1KB, 0)
    Write-Erfolg "Archiv erstellt ($groesseKB KB)"
}

# ===================================================================== #
# 5. Installations-ISO umbauen
# ===================================================================== #
function New-AutomatischesIso {
    param([string]$QuellIso)

    Write-Titel 'Installationsmedium umbauen'

    $entpackt = Join-Path $ArbeitsVerzeichnis 'iso-inhalt'
    $zielIso = Join-Path $ArbeitsVerzeichnis 'schulmonitoring-installation.iso'

    Write-Schritt 'ISO entpacken (dauert ein bis zwei Minuten)'
    Expand-Iso -IsoPfad $QuellIso -Zielverzeichnis $entpackt | Out-Null
    Write-Erfolg 'ISO entpackt'

    # --- cloud-init und Projektdateien hineinlegen --------------------
    $nocloud = Join-Path $entpackt 'nocloud'
    New-CloudInitDaten -Zielverzeichnis $nocloud
    New-ProjektArchiv -Zieldatei (Join-Path $nocloud 'schulmonitoring.tar.gz')

    # Zweiter, unabhaengiger Weg zur selben Konfiguration: Der Installer
    # sucht auch nach /autoinstall.yaml im Wurzelverzeichnis des Mediums.
    # Sollte cloud-init die Angabe "ds=nocloud;s=/cdrom/nocloud/" einmal
    # anders auslegen – zwischen den Fassungen hat sich das schon geaendert –
    # greift dieser Weg. Beide zeigen auf denselben Inhalt, es kann also
    # nichts auseinanderlaufen.
    Copy-Item -LiteralPath (Join-Path $nocloud 'user-data') `
        -Destination (Join-Path $entpackt 'autoinstall.yaml') -Force
    Write-Hinweis 'Installationsvorgaben zusaetzlich als /autoinstall.yaml hinterlegt'

    # --- Startparameter ergaenzen -------------------------------------
    # Ohne "autoinstall" auf der Kernel-Befehlszeile fragt der Installer
    # trotz vorhandener Konfiguration einmal nach. Genau das wollen wir
    # nicht, also wird die grub.cfg angepasst.
    $startparameter = 'autoinstall ds=nocloud\;s=/cdrom/nocloud/'
    $angepasst = 0

    $grubDateien = @(
        (Join-Path $entpackt 'boot\grub\grub.cfg')
        (Join-Path $entpackt 'EFI\boot\grub.cfg')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    foreach ($datei in $grubDateien) {
        $inhalt = Get-Content -LiteralPath $datei -Raw

        # Wartezeit im Startmenue auf ein Minimum setzen
        $inhalt = $inhalt -replace '(?m)^\s*set\s+timeout=.*$', 'set timeout=1'

        # An jede Kernel-Zeile die Parameter anhaengen
        $inhalt = [regex]::Replace(
            $inhalt,
            '(?m)^(\s*linux\s+/casper/(?:hwe-)?vmlinuz\s+)(.*)$',
            {
                param($treffer)
                $rest = $treffer.Groups[2].Value
                if ($rest -match 'autoinstall') { return $treffer.Value }
                return "$($treffer.Groups[1].Value)$startparameter $rest"
            })

        Set-Content -LiteralPath $datei -Value $inhalt -Encoding UTF8 -NoNewline
        $angepasst++
        Write-Hinweis "Startkonfiguration angepasst: $(Split-Path -Leaf $datei)"
    }

    if ($angepasst -eq 0) {
        throw @'
In dem ISO wurde keine grub.cfg gefunden. Damit laesst sich die
unbeaufsichtigte Installation nicht einrichten.

Ausweg: eine bereits vorhandene Ubuntu-24.04-VHDX mit -BasisVhdxPfad
uebergeben. Dann entfaellt der Umbau des Installationsmediums.
'@
    }

    # --- ISO neu bauen ------------------------------------------------
    $efiAbbild = Join-Path $entpackt 'boot\grub\efi.img'
    if (-not (Test-Path -LiteralPath $efiAbbild)) {
        $efiAbbild = Get-ChildItem -LiteralPath $entpackt -Filter 'efi.img' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if (-not $efiAbbild) {
        throw 'Im ISO wurde kein EFI-Startabbild (efi.img) gefunden – das Medium waere nicht startfaehig.'
    }

    Write-Schritt 'Neues Installationsmedium bauen'
    if (-not (Find-Oscdimg)) {
        Write-Hinweis 'Das Windows ADK ist nicht installiert – es werden die Windows-Bordmittel verwendet.'
    }

    New-BootfaehigesIso -Quellverzeichnis $entpackt -Zieldatei $zielIso `
        -Datentraegername 'SCHULMONITORING' -EfiStartabbild $efiAbbild | Out-Null

    $groesseMB = [math]::Round((Get-Item -LiteralPath $zielIso).Length / 1MB, 0)
    Write-Erfolg "Installationsmedium fertig ($groesseMB MB)"

    Remove-Item -LiteralPath $entpackt -Recurse -Force -ErrorAction SilentlyContinue

    return $zielIso
}

# ===================================================================== #
# 5b. Alternative: Seed-ISO fuer ein fertiges VHDX
# ===================================================================== #
function New-SeedIso {
    Write-Titel 'Konfigurationsmedium erstellen'

    $seedVerzeichnis = Join-Path $ArbeitsVerzeichnis 'seed'
    $zielIso = Join-Path $ArbeitsVerzeichnis 'schulmonitoring-seed.iso'

    if (Test-Path -LiteralPath $seedVerzeichnis) {
        Remove-Item -LiteralPath $seedVerzeichnis -Recurse -Force
    }
    New-Item -ItemType Directory -Path $seedVerzeichnis -Force | Out-Null

    New-CloudInitDaten -Zielverzeichnis $seedVerzeichnis -FuerFertigesAbbild
    New-ProjektArchiv -Zieldatei (Join-Path $seedVerzeichnis 'schulmonitoring.tar.gz')

    New-DatenIso -Quellverzeichnis $seedVerzeichnis -Zieldatei $zielIso -Datentraegername 'CIDATA' | Out-Null
    Write-Erfolg 'Konfigurationsmedium erstellt'

    return $zielIso
}

# ===================================================================== #
# 6. VM anlegen
# ===================================================================== #
function New-MonitoringVm {
    param([string]$InstallationsIso, [string]$BasisVhdx)

    Write-Titel 'Virtuelle Maschine anlegen'

    $vhdxPfad = Join-Path $script:ZielVMPfad "$VMName\Virtual Hard Disks\$VMName.vhdx"
    New-Item -ItemType Directory -Path (Split-Path -Parent $vhdxPfad) -Force | Out-Null

    if ($BasisVhdx) {
        Write-Schritt "Basis-Abbild kopieren: $BasisVhdx"
        Copy-Item -LiteralPath $BasisVhdx -Destination $vhdxPfad -Force
        Resize-VHD -Path $vhdxPfad -SizeBytes ($DatentraegerGB * 1GB) -ErrorAction SilentlyContinue
    } else {
        New-VHD -Path $vhdxPfad -SizeBytes ($DatentraegerGB * 1GB) -Dynamic | Out-Null
    }
    Write-Erfolg "Datentraeger angelegt ($DatentraegerGB GB, dynamisch)"

    $vm = New-VM -Name $VMName -Generation 2 `
        -MemoryStartupBytes ($ArbeitsspeicherGB * 1GB) `
        -VHDPath $vhdxPfad `
        -SwitchName $script:GewaehlterSwitch.Name `
        -Path $script:ZielVMPfad

    Set-VMProcessor -VMName $VMName -Count $ProzessorKerne

    # Fester Arbeitsspeicher: Prometheus und Loki moegen keinen Ballon,
    # der ihnen unter Last den Speicher wegnimmt.
    Set-VMMemory -VMName $VMName -DynamicMemoryEnabled $false `
        -StartupBytes ($ArbeitsspeicherGB * 1GB)

    # Ubuntu startet nur mit der Microsoft-UEFI-Zertifizierungsstelle.
    Set-VMFirmware -VMName $VMName -EnableSecureBoot On `
        -SecureBootTemplate 'MicrosoftUEFICertificateAuthority'

    Add-VMDvdDrive -VMName $VMName -Path $InstallationsIso
    $dvd = Get-VMDvdDrive -VMName $VMName

    if ($BasisVhdx) {
        # Fertiges Abbild: von der Platte starten, die CD liefert nur die Konfiguration
        Set-VMFirmware -VMName $VMName -FirstBootDevice (Get-VMHardDiskDrive -VMName $VMName)
    } else {
        Set-VMFirmware -VMName $VMName -FirstBootDevice $dvd
    }

    # Automatisch starten, wenn der Host hochfaehrt – Monitoring soll immer laufen.
    Set-VM -Name $VMName -AutomaticStartAction Start -AutomaticStartDelay 60 `
        -AutomaticStopAction ShutDown -AutomaticCheckpointsEnabled $false

    Enable-VMIntegrationService -VMName $VMName -Name 'Guest Service Interface' -ErrorAction SilentlyContinue
    Set-VMComPort -VMName $VMName -Number 1 -Path "\\.\pipe\$VMName-konsole" -ErrorAction SilentlyContinue

    Write-Erfolg "VM '$VMName' angelegt: $ProzessorKerne Kerne, $ArbeitsspeicherGB GB RAM"
    return $vm
}

# ===================================================================== #
# 7. Auf die Installation warten
# ===================================================================== #
function Wait-Installation {
    Write-Titel 'Automatische Installation laeuft'

    Write-Host ''
    Write-Host '  Die VM installiert sich jetzt selbst. Das dauert ueblicherweise' -ForegroundColor Gray
    Write-Host '  15 bis 30 Minuten. Zusehen kann man im Hyper-V-Manager, aber es' -ForegroundColor Gray
    Write-Host '  ist kein Eingriff noetig.' -ForegroundColor Gray
    Write-Host ''

    Start-VM -Name $VMName
    Write-Erfolg 'VM gestartet'

    # Die Installation endet laut Vorgabe mit dem Ausschalten der VM.
    $ende = (Get-Date).AddMinutes($WartezeitMinuten)
    $letzteMeldung = Get-Date

    while ((Get-Date) -lt $ende) {
        $vm = Get-VM -Name $VMName
        if ($vm.State -eq 'Off') {
            Write-Host ''
            Write-Erfolg 'Installation abgeschlossen'
            return $true
        }
        if (((Get-Date) - $letzteMeldung).TotalMinutes -ge 2) {
            $verstrichen = [int]((Get-Date) - $script:Startzeit).TotalMinutes
            Write-Host "  ... laeuft seit $verstrichen Minuten" -ForegroundColor DarkGray
            $letzteMeldung = Get-Date
        }
        Start-Sleep -Seconds 15
    }

    throw @"
Die Installation ist nach $WartezeitMinuten Minuten noch nicht fertig.

Bitte im Hyper-V-Manager auf die Konsole der VM '$VMName' schauen:
  - Haengt der Installer bei einer Rueckfrage? Dann hat der Umbau des
    ISO nicht gegriffen; einmal "Ja" bestaetigen und es laeuft weiter.
  - Kommt eine Fehlermeldung? Diese notieren.
Mit -WartezeitMinuten laesst sich die Wartezeit erhoehen.
"@
}

function Start-NachInstallation {
    Write-Titel 'Erststart'

    # Installationsmedium entfernen, damit nicht wieder davon gestartet wird
    Get-VMDvdDrive -VMName $VMName | ForEach-Object {
        Set-VMDvdDrive -VMName $VMName -ControllerNumber $_.ControllerNumber `
            -ControllerLocation $_.ControllerLocation -Path $null
    }
    Set-VMFirmware -VMName $VMName -FirstBootDevice (Get-VMHardDiskDrive -VMName $VMName)
    Write-Erfolg 'Installationsmedium entfernt, Startreihenfolge angepasst'

    Start-VM -Name $VMName
    Write-Erfolg 'VM gestartet – die Einrichtung des Monitorings laeuft nun automatisch'
}

function Get-VmIpAdresse {
    param([int]$TimeoutSekunden = 600)

    if ($script:VorhergesagteIp) { return $script:VorhergesagteIp }

    Write-Schritt 'Auf die IP-Adresse der VM warten'
    $ende = (Get-Date).AddSeconds($TimeoutSekunden)

    while ((Get-Date) -lt $ende) {
        $adressen = (Get-VMNetworkAdapter -VMName $VMName).IPAddresses |
            Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' -and $_ -ne '127.0.0.1' }
        if ($adressen) {
            $ip = $adressen | Select-Object -First 1
            Write-Erfolg "IP-Adresse: $ip"
            return $ip
        }
        Start-Sleep -Seconds 10
    }

    Write-Warnung 'Die IP-Adresse liess sich nicht ermitteln (Integrationsdienste noch nicht bereit).'
    return $null
}

function Wait-Monitoring {
    param([string]$IpAdresse, [int]$TimeoutMinuten = 25)

    if (-not $IpAdresse) {
        Write-Warnung 'Ohne bekannte IP-Adresse kann nicht geprueft werden, ob das Dashboard laeuft.'
        return $false
    }

    Write-Titel 'Auf das Dashboard warten'
    Write-Host '  Beim ersten Start laedt die VM die Container herunter.' -ForegroundColor Gray
    Write-Host '  Je nach Internetanbindung dauert das 5 bis 20 Minuten.' -ForegroundColor Gray
    Write-Host ''

    $ende = (Get-Date).AddMinutes($TimeoutMinuten)
    $letzteMeldung = Get-Date

    while ((Get-Date) -lt $ende) {
        try {
            $antwort = Invoke-WebRequest -Uri "http://$IpAdresse/api/health" `
                -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
            if ($antwort.StatusCode -eq 200) {
                Write-Host ''
                Write-Erfolg 'Das Dashboard antwortet'
                return $true
            }
        } catch {
            # noch nicht bereit – das ist in dieser Phase normal
        }

        if (((Get-Date) - $letzteMeldung).TotalMinutes -ge 2) {
            Write-Host '  ... die VM richtet sich noch ein' -ForegroundColor DarkGray
            $letzteMeldung = Get-Date
        }
        Start-Sleep -Seconds 15
    }

    Write-Warnung 'Das Dashboard hat sich noch nicht gemeldet.'
    Write-Hinweis "Auf der VM nachsehen mit:  sudo journalctl -u schulmonitoring-einrichtung -f"
    return $false
}

# ===================================================================== #
# 8. Abschluss
# ===================================================================== #
function Write-Zusammenfassung {
    param([string]$IpAdresse, [bool]$Erreichbar)

    $adresse = if ($IpAdresse) { $IpAdresse } else { "<IP der VM $VMName>" }
    $dauer = [int]((Get-Date) - $script:Startzeit).TotalMinutes

    $bericht = @"
======================================================================
 Schul-Monitoring ist eingerichtet
 Dauer: $dauer Minuten
======================================================================

 DASHBOARD
   Adresse:        http://$adresse/
   Benutzer:       admin
   Kennwort:       $script:GrafanaKennwort

 LINUX-VM
   Name:           $VMName
   Anmeldung:      ssh $AdminBenutzer@$adresse
   Kennwort:       $script:LinuxKennwort

 AGENT-TOKEN (fuer den Rollout auf Servern und Clients)
   $script:AgentToken

 NAECHSTER SCHRITT – Windows-Geraete anbinden
   Das Skript agent\Install-MonitoringAgent.ps1 nach SYSVOL kopieren und
   als Computer-Startskript per Gruppenrichtlinie verteilen. Parameter:

     -ServerUrl "http://$adresse" -Token "$script:AgentToken"

   Ausfuehrliche Anleitung: docs\03-gpo-rollout.md

 NETZWERKGERAETE
   Switches, Access Points, Drucker und USV findet der Suchlauf selbst und
   ueberwacht sie sofort. Fuer sprechende Namen und Raumangaben die Bloecke
   aus  inventar/gefunden.yml  nach  inventar/inventar.yml  uebernehmen.
   Beides liegt unter /opt/schulmonitoring/stack/
   Durchsucht wird: $(Get-ErkennungsNetze)
   Weitere Netze in der .env unter ERKENNUNG_NETZE nachtragen.
   Ausfuehrlich: docs\10-geraeteerkennung.md

 SYSLOG VON FORTIGATE UND UNIFI
   Ziel: $adresse, Port 1514/UDP
   Genaue Schritte: docs\04-unifi-fortigate.md

======================================================================
"@

    Write-Host ''
    Write-Host $bericht -ForegroundColor Cyan

    if (-not $Erreichbar) {
        Write-Warnung 'Das Dashboard war beim Abschluss dieses Skripts noch nicht erreichbar.'
        Write-Hinweis 'Das ist bei langsamer Internetanbindung normal – einfach in ein paar Minuten erneut aufrufen.'
    }

    $berichtsdatei = Join-Path $ArbeitsVerzeichnis "Zugangsdaten-$VMName.txt"
    Set-Content -LiteralPath $berichtsdatei -Value $bericht -Encoding UTF8
    Write-Host "  Diese Angaben wurden gespeichert unter:" -ForegroundColor Gray
    Write-Host "  $berichtsdatei" -ForegroundColor Gray
    Write-Host ''
    Write-Warnung 'Die Datei enthaelt Kennwoerter – bitte sicher verwahren und danach loeschen.'
    Write-Host ''
}

# ===================================================================== #
# Ablauf
# ===================================================================== #
try {
    Write-Host ''
    Write-Host '  SCHUL-MONITORING – AUTOMATISCHE EINRICHTUNG' -ForegroundColor Cyan
    Write-Host '  ------------------------------------------' -ForegroundColor DarkCyan

    # Geheimnisse einmalig erzeugen, damit sie am Ende ausgegeben werden koennen
    $script:GrafanaKennwort = if ($AdminKennwort) { $AdminKennwort } else { New-Geheimnis -Laenge 20 }
    $script:LinuxKennwort   = if ($AdminKennwort) { $AdminKennwort } else { New-Geheimnis -Laenge 20 }
    $script:AgentToken      = New-Geheimnis -Laenge 48
    $script:GewaehlterSwitch = $null
    $script:VorhergesagteIp = $null
    # Muss vorbelegt sein: Set-StrictMode bricht sonst beim ersten Lesen ab.
    $script:ErkennungsNetzeWert = $null

    Test-Voraussetzungen

    if ($BasisVhdxPfad) {
        if (-not (Test-Path -LiteralPath $BasisVhdxPfad)) {
            throw "Das angegebene Basis-Abbild wurde nicht gefunden: $BasisVhdxPfad"
        }
        Write-Hinweis 'Basis-Abbild angegeben – die Installation wird uebersprungen.'
        $iso = New-SeedIso
        $medium = $iso
    } else {
        $quellIso = Get-UbuntuIso
        $medium = New-AutomatischesIso -QuellIso $quellIso
    }

    if ($NurVorbereiten) {
        Write-Titel 'Vorbereitung abgeschlossen'
        Write-Erfolg "Medium liegt bereit: $medium"
        Write-Hinweis 'Mit -NurVorbereiten wurde keine VM angelegt.'
        return
    }

    New-MonitoringVm -InstallationsIso $medium -BasisVhdx $BasisVhdxPfad | Out-Null

    if ($BasisVhdxPfad) {
        Start-VM -Name $VMName
        Write-Erfolg 'VM gestartet'
    } else {
        Wait-Installation | Out-Null
        Start-NachInstallation
    }

    $ip = Get-VmIpAdresse
    $erreichbar = Wait-Monitoring -IpAdresse $ip

    Write-Zusammenfassung -IpAdresse $ip -Erreichbar $erreichbar
}
catch {
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor Red
    Write-Host ' Die Einrichtung ist fehlgeschlagen' -ForegroundColor Red
    Write-Host ('=' * 70) -ForegroundColor Red
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Write-Host ' Hilfe zur Fehlersuche: docs\08-fehlersuche.md' -ForegroundColor Yellow
    Write-Host ''
    Write-Verbose ($_.ScriptStackTrace)
    exit 1
}
