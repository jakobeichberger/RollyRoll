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

**3. Netzwerkgeräte eintragen** – Switches, Access Points, Drucker und USV in
`/opt/schulmonitoring/stack/inventar/inventar.yml` auf der VM. Änderungen
werden binnen 30 Sekunden ohne Neustart übernommen.

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

### Sicherheit

Angriffserkennung auf Basis der Ereignisprotokolle – von Kennwortangriffen über
Rechteausweitung bis zu den typischen Vorbereitungsschritten eines
Verschlüsselungstrojaners. Dazu die Auswertung der FortiGate-Protokolle
(IPS, Virenfunde, Botnetz-Kontakte).
→ [docs/06-security-monitoring.md](docs/06-security-monitoring.md)

### Netzwerk

UniFi Access Points und Switches über den Controller **und** per Ping – so wird
ein AP-Ausfall auch dann erkannt, wenn der Controller selbst Probleme hat.
FortiGate über REST-API (Last, Sitzungen, VPN) und SNMP (Lüfter, Temperatur,
Netzteile). Switchports mit Übertragungsfehlern, weil das fast immer ein
defektes Kabel ankündigt.

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
