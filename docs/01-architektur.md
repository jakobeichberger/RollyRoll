# Architektur

Warum die Dinge so gebaut sind, wie sie gebaut sind.

---

## Das Grundprinzip: Geräte melden sich selbst

Der übliche Schmerz beim Monitoring ist die Pflege der Geräteliste. Neuer
Rechner im EDV-Saal – wieder etwas eintragen. Rechner ausgemustert – wieder
etwas löschen. Nach einem Jahr stimmt die Liste nicht mehr.

Hier läuft es umgekehrt. Der Agent kommt per Gruppenrichtlinie auf jedes
Gerät, meldet sich beim Registrierungs-Dienst an und der schreibt daraus
die Zieldateien, die Prometheus einliest. Ein neuer Rechner erscheint von
allein. Ein Gerät, das sich 30 Tage nicht meldet, verschwindet von allein.

Für alles, was keinen Agenten bekommen kann – Switches, Access Points,
Drucker, USV – gibt es eine einzige lesbare Datei, `inventar/inventar.yml`.
Auch daraus erzeugt der Registrierungs-Dienst die Überwachungsziele: ICMP,
SNMP und Dienstprüfungen. Änderungen greifen binnen 30 Sekunden, ohne
Neustart.

---

## Die Bestandteile

### Auf der Monitoring-VM

| Container | Aufgabe |
|---|---|
| **Prometheus** | Sammelt und speichert alle Messwerte, wertet die Alarmregeln aus |
| **Loki** | Speichert die Protokolle, wertet die Sicherheitsregeln aus |
| **Grafana** | Dashboards |
| **Alertmanager** | Bündelt Alarme, unterdrückt Folgefehler, verschickt Mails |
| **Alloy** | Nimmt Syslog von FortiGate und UniFi entgegen und zerlegt es |
| **Registrierung** | Agenten-Anmeldung, Zieldateien, Auslieferung der Agent-Pakete |
| **Caddy** | Einziger Einstiegspunkt, verteilt auf Grafana, Registrierung und Loki |
| **Blackbox** | Ping, TCP-Ports, HTTP, Zertifikatslaufzeiten |
| **SNMP** | Switches, Access Points, FortiGate-Sensoren, USV, Drucker |
| **unpoller** | Holt die Detailwerte aus dem UniFi-Controller |
| **fortigate_exporter** | FortiGate über die REST-API |
| **node_exporter / cadvisor** | Die VM überwacht sich selbst mit |

### Auf den Windows-Geräten

| Bestandteil | Aufgabe |
|---|---|
| **windows_exporter** | CPU, Speicher, Datenträger, Dienste, Netzwerk, AD, IIS, Hyper-V |
| **Grafana Alloy** | Ereignisprotokolle nach Loki |
| **Collect-HardwareHealth.ps1** | Alle fünf Minuten: alles, was windows_exporter nicht kann |

---

## Warum ein eigenes Hardware-Skript?

windows_exporter liefert gut, was das Betriebssystem im laufenden Betrieb
misst. Was fehlt, ist genau das, woran man einen bevorstehenden Ausfall
erkennt:

* die S.M.A.R.T.-Ausfallvorhersage der Platte,
* den Verschleiß einer SSD,
* den Zustand eines RAID-Verbunds und seiner Cache-Batterie,
* WHEA-Fehler von RAM, CPU und PCIe,
* ob die Datensicherung überhaupt gelaufen ist.

Das Skript liest diese Werte per WMI, aus dem Ereignisprotokoll und – wenn
vorhanden – über die Werkzeuge des RAID-Herstellers. Es schreibt eine
`.prom`-Datei, die windows_exporter über seinen textfile-Sammler einfach
mit ausliefert. Kein zusätzlicher Dienst, kein offener Port.

Jeder Prüfabschnitt läuft in einem eigenen `try`/`catch`. Ein Gerät ohne
Temperaturfühler liefert eben keinen Temperaturwert – alles andere kommt
trotzdem an. Ob ein Abschnitt durchgelaufen ist, steht selbst als Messwert
in `schule_pruefabschnitt_erfolgreich` und ist so nachvollziehbar.

---

## Warum Access Points doppelt überwacht werden

Die Detailwerte eines Access Points – Clientzahl, Kanalauslastung,
Funkqualität – weiß nur der UniFi-Controller. Deshalb `unpoller`.

Für die Frage „ist der AP überhaupt da?" ist der Controller aber die
schlechtere Quelle: Hat der Controller selbst ein Problem, sieht es aus,
als wären alle APs weg. Deshalb steht jeder AP zusätzlich mit seiner IP in
`inventar.yml` und wird angepingt. Der Ping ist der Ausfallalarm, der
Controller liefert die Details.

Dasselbe gilt für die FortiGate: Last und VPN-Tunnel über die REST-API,
Lüfter und Temperaturen über SNMP, Angriffe über Syslog. Drei Quellen,
weil jede etwas kann, was die anderen nicht können.

---

## Datenfluss

**Messwerte** – Prometheus holt sie alle 30 Sekunden ab (Pull). Die Geräte
schicken nichts von sich aus. Das hat den angenehmen Nebeneffekt, dass ein
Gerät, das nicht antwortet, automatisch als „offline" erkannt wird.

**Protokolle** – die Geräte schicken sie zu Loki (Push), weil Ereignisse
im Moment ihres Auftretens wichtig sind. Alloy puffert auf Platte, wenn der
Server kurz nicht erreichbar ist; nach der Störung kommt alles nach.

**Alarme** – Prometheus und Loki werten ihre Regeln selbst aus und schicken
Treffer an den Alertmanager. Der bündelt, unterdrückt Folgefehler und
verschickt die Mail.

---

## Kardinalität – warum nicht alles ein Label ist

Prometheus und Loki werden langsam, wenn zu viele Kombinationen von Labels
entstehen. Deshalb ein paar bewusste Entscheidungen:

* Die **Ereignisnummer** ist bei den Windows-Protokollen kein Label,
  sondern strukturierte Metadaten. Bei 300 Geräten mal einigen hundert
  Ereignisnummern gäbe es sonst zehntausende Datenströme. Filtern lässt
  sich trotzdem: `{job="windows_events"} | json | event_id="4625"`.
* Der **Prozess-Sammler** von windows_exporter ist auf die tatsächlich
  interessanten Dienste begrenzt (SQL Server, IIS, LSASS, Defender, …).
  Ohne diese Begrenzung entstünden pro Gerät hunderte Zeitreihen.
* Bei den **Zertifikaten** werden höchstens 20 je Gerät gemeldet, sortiert
  nach Ablaufdatum.

---

## Sicherheit des Monitorings selbst

* Die Agenten weisen sich mit einem gemeinsamen Token aus
  (`X-Agent-Token`), das beim Aufbau erzeugt wird.
* Die Log-Annahme ist zusätzlich mit Basic-Auth abgesichert; das Kennwort
  ist dasselbe Token, hinterlegt als bcrypt-Hash.
* Der Agent öffnet die Windows-Firewall auf Port 9182 **nur für die IP des
  Monitoring-Servers**, nicht für das ganze Netz.
* Prometheus und Alertmanager horchen nur auf `127.0.0.1` der VM. Von
  außen erreichbar sind allein Grafana und die Registrierung über Caddy.
* Die Container laufen, wo möglich, unter eigenen Benutzerkonten.

Der Monitoring-Server sieht Ereignisprotokolle der gesamten Schule und ist
damit selbst schützenswert. Er gehört in ein Verwaltungsnetz und nicht ins
Schülernetz.
