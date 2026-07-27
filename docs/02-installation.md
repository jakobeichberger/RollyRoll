# Installation

---

## Voraussetzungen

**Auf dem Hyper-V-Server:**

* Windows Server 2019 oder neuer mit Hyper-V-Rolle
* Windows PowerShell 5.1 (ist enthalten), Ausführung als Administrator
* ein virtueller Switch mit Verbindung ins Schulnetz
* etwa 20 GB freier Plattenplatz für Abbild und Arbeitsdateien
* Internetzugang für den Download von Ubuntu und der Container

**Im Netz:**

* eine freie IP-Adresse für die Monitoring-VM (fest empfohlen)
* ein Mailserver oder Relay für die Alarm-Mails

Das Windows ADK ist **nicht** nötig. Ist es vorhanden, wird `oscdimg`
verwendet, sonst greift das Skript auf die Bordmittel von Windows zurück.

---

## Der Aufruf

```powershell
cd C:\SchulMonitoring\deploy

.\Deploy-MonitoringVM.ps1 `
    -IPAdresse       10.0.0.50/24 `
    -Gateway         10.0.0.1 `
    -DnsServer       10.0.0.20,10.0.0.21 `
    -DnsSuchdomaene  schule.local `
    -SwitchName      "LAN" `
    -SmtpHost        smtp.schule.local `
    -SmtpVon         monitoring@schule.local `
    -AlarmEmpfaenger admin@schule.local `
    -UnifiUrl        https://10.0.0.10:8443 `
    -UnifiBenutzer   monitoring `
    -UnifiKennwort   "..." `
    -FortiGateUrl    https://10.0.0.1 `
    -FortiGateToken  "..." `
    -SnmpCommunity   "..."
```

Nichts davon ist Pflicht. Ohne Angaben holt sich die VM eine Adresse per
DHCP, nimmt den ersten externen Switch und lässt UniFi und FortiGate
zunächst außen vor – alles lässt sich später in der `.env` nachtragen.

### Wichtige Parameter

| Parameter | Vorgabe | Bedeutung |
|---|---|---|
| `-VMName` | `SchulMonitoring` | Name der VM in Hyper-V |
| `-SwitchName` | erster externer | Virtueller Switch |
| `-ProzessorKerne` | 4 | Ab etwa 300 Geräten eher 6 |
| `-ArbeitsspeicherGB` | 8 | Ab etwa 300 Geräten eher 12 |
| `-DatentraegerGB` | 200 | Siehe Kapazitätsplanung unten |
| `-IPAdresse` | DHCP | Form `10.0.0.50/24` |
| `-AdminBenutzer` | `monadmin` | Anmeldename der Linux-VM |
| `-SshOeffentlicherSchluessel` | – | Inhalt einer `.pub`-Datei |
| `-WartezeitMinuten` | 45 | Geduld für die Installation |
| `-Ueberschreiben` | – | Vorhandene VN gleichen Namens ersetzen |
| `-NurVorbereiten` | – | Nur das Medium bauen, keine VM anlegen |
| `-BasisVhdxPfad` | – | Rückfallebene, siehe unten |

Kennwörter, die auf der Kommandozeile stehen, landen in der
PowerShell-Verlaufsdatei. Wer das vermeiden will, trägt UniFi- und
FortiGate-Zugangsdaten stattdessen später auf der VM in die `.env` ein.

---

## Was das Skript der Reihe nach tut

1. **Prüft die Voraussetzungen** – Hyper-V, Switch, Plattenplatz, ob schon
   eine VM gleichen Namens existiert.
2. **Lädt Ubuntu Server 24.04** von `releases.ubuntu.com` und kontrolliert
   die Prüfsumme. Ein vorhandenes ISO lässt sich mit `-IsoPfad` übergeben.
3. **Baut das Installationsmedium um.** Es legt die Installationsvorgaben
   (cloud-init) und ein Archiv des Projekts hinein und ergänzt in der
   `grub.cfg` den Startparameter `autoinstall`. Ohne den fragt der
   Installer trotz vollständiger Vorgaben einmal nach – und genau das soll
   nicht passieren.
4. **Legt die VM an**: Generation 2, Secure Boot mit der
   Microsoft-UEFI-Zertifizierungsstelle (die Ubuntu braucht), fester
   Arbeitsspeicher, automatischer Start mit dem Host.
5. **Startet sie und wartet.** Die Installation endet laut Vorgabe mit dem
   Ausschalten der VM – daran erkennt das Skript, dass sie fertig ist.
6. **Entfernt das Installationsmedium**, stellt die Startreihenfolge auf
   die Festplatte um und startet neu.
7. Beim ersten Start läuft **`bootstrap.sh`** in der VM: installiert Docker,
   erzeugt die Konfigurationsdateien, holt die Agent-Pakete, startet alle
   Container und richtet den Systemdienst ein.
8. **Wartet, bis das Dashboard antwortet**, und gibt Adresse, Kennwörter
   und Agent-Token aus.

Gesamtdauer: 30 bis 50 Minuten, abhängig von der Internetanbindung.
Eingreifen muss man nicht.

---

## Danach

Die Ausgabe am Ende sieht so aus:

```
 DASHBOARD
   Adresse:        http://10.0.0.50/
   Benutzer:       admin
   Kennwort:       xxxxxxxxxxxxxxxxxxxx

 AGENT-TOKEN (fuer den Rollout auf Servern und Clients)
   xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

Dieselben Angaben stehen in
`C:\ProgramData\SchulMonitoring\Bereitstellung\Zugangsdaten-<VMName>.txt`
und auf der VM in `/root/ZUGANGSDATEN.txt`. Beide Dateien enthalten
Kennwörter im Klartext – nach dem Übertragen in den Kennwortspeicher der
Schule bitte löschen.

Beim ersten Anmelden zeigt Grafana sofort die Gesamtübersicht. Zu sehen ist
zunächst die Monitoring-VM selbst; alles Weitere kommt, sobald die Agenten
ausgerollt sind und die Geräteliste gefüllt ist.

---

## Rückfallebene: vorhandenes Ubuntu-Abbild

Der Umbau des Installationsmediums ist der einzige Schritt, der von den
Werkzeugen des Hyper-V-Servers abhängt. Falls er scheitert, gibt es einen
Weg ohne ihn: eine bereits vorhandene Ubuntu-24.04-VHDX mit cloud-init.

```powershell
.\Deploy-MonitoringVM.ps1 -BasisVhdxPfad "D:\Vorlagen\ubuntu-24.04.vhdx" `
    -IPAdresse 10.0.0.50/24 -Gateway 10.0.0.1
```

Dann entfällt die Installation komplett. Das Skript kopiert das Abbild,
legt eine kleine Konfigurations-CD daneben und cloud-init erledigt den
Rest.

---

## Kapazitätsplanung

Faustwerte für die Plattengröße:

| Geräte | Messwerte (1 Jahr) | Protokolle (90 Tage) | Empfehlung |
|---|---|---|---|
| bis 50 | ca. 8 GB | ca. 15 GB | 100 GB |
| bis 150 | ca. 20 GB | ca. 45 GB | 200 GB |
| bis 300 | ca. 40 GB | ca. 90 GB | 300 GB |
| bis 600 | ca. 80 GB | ca. 180 GB | 500 GB |

Die Aufbewahrungszeiten lassen sich jederzeit in der `.env` verkürzen
(`METRIK_AUFBEWAHRUNG`, `LOG_AUFBEWAHRUNG`). Prometheus hat zusätzlich eine
harte Obergrenze über `METRIK_MAX_GROESSE` – es räumt dann selbst auf,
statt die Platte volllaufen zu lassen.

Die Monitoring-VM überwacht sich selbst mit: Wird der Platz knapp, kommt
rechtzeitig eine Warnung.

---

## Konfiguration nachträglich ändern

Alles Einstellbare steht in einer Datei:

```bash
ssh monadmin@10.0.0.50
sudo nano /opt/schulmonitoring/stack/.env
sudo systemctl restart schulmonitoring
```

Änderungen an `inventar/inventar.yml` brauchen **keinen** Neustart – die
werden binnen 30 Sekunden übernommen.
