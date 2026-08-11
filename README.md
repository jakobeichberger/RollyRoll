# Schul-Monitoring

Vollständige Überwachung einer Schul-IT: Windows-Server und -Clients, Linux-Server,
UniFi Access Points und Switches, FortiGate-Firewall. Mit Hardware-Vorwarnungen,
Angriffserkennung und einem Dashboard, das nach dem Anmelden sofort etwas zeigt.

Der komplette Aufbau läuft über **ein einziges PowerShell-Skript** auf dem
Hyper-V-Server. Danach folgt nur noch der Rollout auf die Windows-Geräte
per Gruppenrichtlinie.

---

## Der schnelle Weg

**1. Auf dem Hyper-V-Server** (PowerShell als Administrator):

```powershell
git clone https://github.com/jakobeichberger/rollyroll.git C:\SchulMonitoring
cd C:\SchulMonitoring\deploy

.\Deploy-MonitoringVM.ps1 `
    -IPAdresse   10.0.0.50/24 `
    -Gateway     10.0.0.1 `
    -DnsServer   10.0.0.20 `
    -SwitchName  "LAN" `
    -SmtpHost    smtp.schule.local `
    -AlarmEmpfaenger admin@schule.local
```

Das Skript lädt Ubuntu Server, baut daraus ein Installationsmedium, das
sich selbst installiert, legt die VM an, startet sie und wartet, bis das
Dashboard antwortet. Am Ende stehen Adresse, Kennwort und Agent-Token auf
dem Bildschirm. Dauer: etwa 30 bis 50 Minuten, davon ist nichts zu tun.

**2. Windows-Geräte anbinden** – das ausgegebene Kommando als
Computer-Startskript in eine Gruppenrichtlinie eintragen:

```powershell
\\schule.local\SYSVOL\schule.local\scripts\Install-MonitoringAgent.ps1 `
    -ServerUrl "http://10.0.0.50" -Token "<ausgegebenes Token>"
```

Beim nächsten Neustart installiert sich jedes Gerät selbst, meldet sich am
Server an und erscheint im Dashboard. Nichts von Hand eintragen.
→ Ausführlich in [docs/03-gpo-rollout.md](docs/03-gpo-rollout.md)

**3. Netzwerkgeräte** – finden sich selbst. Der Suchlauf klappert stündlich das
eigene Netz per SNMP ab und fragt den UniFi-Controller; Switches, Access
Points, Drucker und USV erscheinen von allein im Dashboard. Für sprechende
Namen und Raumangaben werden sie aus der Fundliste nach
`/opt/schulmonitoring/stack/inventar/inventar.yml` übernommen – Änderungen
daran greifen binnen 30 Sekunden ohne Neustart.
→ [docs/10-geraeteerkennung.md](docs/10-geraeteerkennung.md)

**4. Syslog von FortiGate und UniFi** auf `<VM-IP>:1514/UDP` umlenken.
→ [docs/04-unifi-fortigate.md](docs/04-unifi-fortigate.md)

Fertig.

---

## Was überwacht wird

### Hardware – und zwar bevor etwas ausfällt

| Vorwarnung | Woher |
|---|---|
| S.M.A.R.T. sagt Plattenausfall voraus | WMI-Ausfallvorhersage |
| SSD am Lebensende (Verschleiß in %) | Storage-Zuverlässigkeitszähler |
| RAID degradiert, Cache-Batterie defekt | storcli / perccli / ssacli / Storage Spaces |
| Defekter RAM, CPU oder PCIe | WHEA-Ereignisse |
| Überhitzung, Lüfterstörung, Netzteilfehler | ACPI, WMI, FortiGate über SNMP |
| Akkuverschleiß bei Notebooks | Design- gegen Ist-Kapazität |
| „Platte ist in vier Tagen voll" | Trend-Hochrechnung, kein Schwellwert |
| Zertifikat läuft ab | Zertifikatspeicher und TLS-Prüfung |
| USV auf Batterie, Batterie schwach | SNMP |
| Datensicherung fehlt oder schlug fehl | Windows Server-Sicherung, Veeam |
| Platte antwortet zunehmend langsam | Antwortzeit je Zugriff, Spitzenlatenz des Laufwerks |
| Laufwerk korrigiert immer mehr Lesefehler | Zuverlässigkeitszähler |
| Controller setzt Laufwerke zurück | Ereignis 129 im Hardware-Protokoll |
| Gigabit-Karte läuft nur noch mit 100 Mbit | Vergleich mit dem Vortag |

### Sicherheit

Zwei Ebenen, die sich ergänzen:

**Angriffserkennung** – „passiert gerade etwas?" Ausgewertet werden die
Ereignisprotokolle: Kennwortangriffe, Rechteausweitung, verschleierte
PowerShell, zweckentfremdete Bordmittel und die typischen Vorbereitungs­schritte
eines Verschlüsselungstrojaners. Dazu die FortiGate-Protokolle (IPS,
Virenfunde, Botnetz-Kontakte).

**Härtungs-Baseline** – „sind wir überhaupt richtig eingestellt?" 45 Prüfungen
gegen CIS Benchmarks, BSI-Grundschutz und die Microsoft Security Baseline:
LSA-Schutz, WDigest, LLMNR, SMBv1, NTLMv1, LAPS, ASR-Regeln,
Manipulationsschutz, Audit-Richtlinien, RDP, veraltete TLS-Versionen und im
Active Directory krbtgt-Alter, AS-REP-Konten und Kerberoasting-Angriffsfläche.
Jedes Gerät bekommt einen Erfüllungsgrad, das Dashboard sortiert nach Wirkung.

→ [docs/06-security-monitoring.md](docs/06-security-monitoring.md)

### Netzwerk

UniFi Access Points und Switches über den Controller **und** per Ping – so wird
ein AP-Ausfall auch dann erkannt, wenn der Controller selbst Probleme hat.
FortiGate über REST-API (Last, Sitzungen, VPN) und SNMP (Lüfter, Temperatur,
Netzteile). Switchports mit Übertragungsfehlern, weil das fast immer ein
defektes Kabel ankündigt.

### Geräteerkennung – nichts von Hand eintragen

Stündlich eine SNMP-Anfrage an jede Adresse der eigenen Netze, dazu die
Geräteliste des UniFi-Controllers. Was antwortet, wird nach Hersteller und
Bauart eingeordnet (Switch, Access Point, Drucker, USV, Firewall) und **sofort
mitüberwacht** – mit passendem SNMP-Modul, ohne Zutun. Geräte, die später in
`inventar.yml` einen richtigen Namen bekommen, fallen automatisch aus der
Fundliste heraus; doppelt überwacht wird nie.

Nebeneffekt fürs Auge: Ein unbekanntes Gerät, das im Servernetz auftaucht,
löst eine Meldung aus.

Aus den LLDP-Nachbarschaftstabellen entsteht dazu der **Netzplan** – welcher AP
an welchem Switchport, welcher Switch an welchem Uplink. Fällt eine Strecke
aus, sieht man sofort, was dahinter liegt.

→ [docs/10-geraeteerkennung.md](docs/10-geraeteerkennung.md)

### Active Directory

Nicht nur „läuft der Dienst" – der läuft auch dann noch, wenn sich niemand mehr
anmelden kann. Geprüft werden **Replikation** zwischen den Domänencontrollern,
**SYSVOL/DFSR** (klemmt das, kommen keine Gruppenrichtlinien mehr an – auch das
Agent-Skript nicht), die **FSMO-Rollen**, die **LDAP-Antwortzeit** als
Frühindikator für langsame Anmeldungen und **Kontosperrungen** als
Angriffsanzeichen.

Läuft ausschließlich auf Domänencontrollern, richtet sich selbst ein.

→ [docs/11-verzeichnisdienst.md](docs/11-verzeichnisdienst.md)

### Konfigurationssicherung

Jede Nacht werden FortiGate und UniFi-Controller ausgelesen und in einem
Git-Verzeichnis abgelegt. Stirbt ein Gerät, steht der letzte Stand bereit.
Und jede Änderung wird sichtbar: Kommt nachts um drei eine Firewall-Regel
dazu, die niemand angelegt hat, ist das ein Befund.

→ [docs/12-konfigurationssicherung.md](docs/12-konfigurationssicherung.md)

---

## Aufbau

```
   Windows-Server & Clients          UniFi / FortiGate / Drucker / USV
   ┌──────────────────────┐          ┌──────────────────────────────┐
   │ windows_exporter     │          │ SNMP · Syslog · REST-API     │
   │ Grafana Alloy        │          └──────────────┬───────────────┘
   │ Hardwareprüfung      │                         │
   └──────────┬───────────┘                         │
              │  meldet sich selbst an              │
              ▼                                     ▼
   ┌─────────────────────────────────────────────────────────────────┐
   │  Monitoring-VM (Ubuntu + Docker)                                │
   │                                                                 │
   │  Registrierung → erzeugt automatisch alle Überwachungsziele     │
   │  Prometheus (Messwerte)   Loki (Protokolle)   Alertmanager      │
   │  Blackbox · SNMP · unpoller · fortigate_exporter                │
   │                                                                 │
   │  Grafana ──────────────────────────► Dashboard + Alarm-Mails    │
   └─────────────────────────────────────────────────────────────────┘
```

Alles läuft in Containern auf einer einzigen VM. Kein Cloud-Dienst, keine
Lizenzkosten, alle Daten bleiben in der Schule.

→ Details in [docs/01-architektur.md](docs/01-architektur.md)

---

## Dashboards

| Dashboard | Zweck |
|---|---|
| **Gesamtübersicht** | Startseite: Lage, offene Meldungen, Auslastung, Sicherheit |
| **Windows – Detail je Gerät** | Ein Server oder Client im Einzelnen, inklusive Ereignisprotokoll |
| **Hardware & Vorwarnungen** | Alles, was auf einen bevorstehenden Ausfall hindeutet |
| **Netzwerk – UniFi & FortiGate** | Access Points, Switches, Firewall |
| **Sicherheit & Angriffserkennung** | Anmeldeversuche, verdächtige Befehle, Firewall-Vorfälle |
| **Sicherheits-Baseline** | Sind die Geräte überhaupt richtig eingestellt? Erfüllungsgrad je Gerät und Bereich |
| **Hardware-Leistung & Protokolle** | Antwortzeiten, Fehlerzähler und die Hardware-Meldungen im Klartext |
| **Geräteerkennung** | Was im Netz gefunden wurde, was noch keinen richtigen Namen hat, und der Netzplan |
| **Active Directory – Betrieb** | Replikation, SYSVOL, FSMO, Anmeldegeschwindigkeit |

---

## Verzeichnisse

```
deploy/     Das eine Skript für den Hyper-V-Server
agent/      Windows-Agent (GPO) und Linux-Agent
stack/      Der komplette Monitoring-Stack der VM
docs/       Anleitungen
```

---

## Anleitungen

| Datei | Inhalt |
|---|---|
| [01-architektur.md](docs/01-architektur.md) | Wie alles zusammenspielt und warum |
| [02-installation.md](docs/02-installation.md) | Voraussetzungen, Parameter, Ablauf |
| [03-gpo-rollout.md](docs/03-gpo-rollout.md) | Windows-Agent per Gruppenrichtlinie |
| [04-unifi-fortigate.md](docs/04-unifi-fortigate.md) | UniFi, FortiGate, SNMP, Syslog |
| [05-alarmierung.md](docs/05-alarmierung.md) | E-Mail, Schweregrade, eigene Regeln |
| [06-security-monitoring.md](docs/06-security-monitoring.md) | Angriffserkennung und Audit-Einstellungen |
| [07-betrieb.md](docs/07-betrieb.md) | Sicherung, Updates, Kapazität |
| [09-hardware.md](docs/09-hardware.md) | Hardware-Protokolle, Leistungs- und Fehlerdaten |
| [10-geraeteerkennung.md](docs/10-geraeteerkennung.md) | Geräte im Netz automatisch finden, Netzplan aus LLDP |
| [11-verzeichnisdienst.md](docs/11-verzeichnisdienst.md) | Active Directory im Betrieb: Replikation, SYSVOL, FSMO |
| [12-konfigurationssicherung.md](docs/12-konfigurationssicherung.md) | Nächtliche Sicherung von FortiGate und UniFi |
| [08-fehlersuche.md](docs/08-fehlersuche.md) | Wenn etwas nicht läuft |

---

## Systemanforderungen

**Hyper-V-Host:** Windows Server 2019 oder neuer, PowerShell 5.1,
freier Plattenplatz für die VM.

**Monitoring-VM** (Vorgabewerte, im Skript änderbar): 4 Kerne, 8 GB RAM,
200 GB Platte. Das reicht für rund 300 Geräte bei einem Jahr Messwerten und
90 Tagen Protokollen.

**Überwachte Geräte:** Windows 10/11, Windows Server 2016 und neuer,
Ubuntu/Debian. Für die Netzwerkgeräte genügt SNMP v2c.
