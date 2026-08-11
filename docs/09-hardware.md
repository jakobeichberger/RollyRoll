# Hardware: Protokolle, Leistungs- und Fehlerdaten

Drei Blickwinkel auf dieselbe Hardware, die sich gegenseitig ergänzen:

| Ebene | Frage | Wo |
|---|---|---|
| **Zustand** | Meldet ein Bauteil einen Defekt? | Dashboard „Hardware & Vorwarnungen" |
| **Leistung** | Wird etwas messbar langsamer? | Dashboard „Hardware-Leistung & Protokolle" |
| **Protokolle** | Was genau hat Windows gemeldet? | dasselbe Dashboard, unterer Bereich |

Die mittlere Ebene ist die interessanteste: Ein Bauteil, das noch
funktioniert, aber jede Woche ein bisschen langsamer wird, ist der
früheste Zeitpunkt, zu dem überhaupt etwas auffällt.

---

## Warum die Protokolle nach Anbieter gefiltert werden

Das ist der wichtigste Punkt dieser Seite.

Die naheliegende Filterung wäre nach Schweregrad: Fehler und Warnungen
einsammeln, den Rest weglassen. Genau daran scheitert die
Hardware-Früherkennung.

**WHEA protokolliert korrigierte Hardwarefehler als „Information".**
Wenn die Fehlerkorrektur des Arbeitsspeichers ein gekipptes Bit
zurechtrückt oder eine PCIe-Verbindung ein Paket wiederholt, ist das aus
Sicht von Windows kein Fehler — es wurde ja behoben. Für die Wartung ist
es das wichtigste Signal überhaupt: Diese Meldungen tauchen typischerweise
Wochen vor dem Totalausfall auf.

Deshalb sammelt der Agent den Hardware-Kanal über die **Anbieter** ein,
unabhängig vom Schweregrad:

```
WHEA-Logger · disk · Ntfs · volmgr · volsnap · storahci · stornvme
iaStorA · megasas · HpCISSs3 · mpio · msiscsi · StorPort
Kernel-Power · Kernel-Processor-Power · BugCheck · WER-SystemErrorReporting
```

Dazu die Verwaltungsprogramme der Hersteller aus dem Anwendungsprotokoll:
Dell OpenManage, HPE Smart Storage, Broadcom MegaRAID, Lenovo XClarity,
IPMI. Ist keines installiert, bleibt der Kanal leer — das kostet nichts.

Diese Anbieter sind im allgemeinen Systemprotokoll ausgeklammert, damit
nichts doppelt gespeichert wird.

---

## Was die Zahlen bedeuten

### Antwortzeit der Datenträger

Die aussagekräftigste Leistungskennzahl überhaupt. Berechnet als
Zeit pro Zugriff, nicht als Durchsatz:

| Wert | Bedeutung |
|---|---|
| unter 10 ms | unauffällig |
| über 25 ms | Alterungszeichen oder Überlastung — Alarm nach 20 Minuten |
| über 100 ms | das System steht für die Benutzer praktisch — Alarm nach 10 Minuten |

Der Alarm greift nur bei nennenswerter Last (mehr als 5 Zugriffe/s).
Ein Laufwerk, auf das niemand zugreift, soll nicht auffallen, bloß weil
der einzige Zugriff der Stunde langsam war.

Häufige Ursachen in dieser Reihenfolge: RAID-Verbund im Wiederaufbau,
Virenscan zur Unzeit, zu wenig Arbeitsspeicher (das System lagert aus und
belastet die Platte zusätzlich) — und erst dann die sterbende Platte.

### Spitzenlatenz

Kommt vom Laufwerk selbst (`ReadLatencyMax` / `WriteLatencyMax`). Werte
über einer Sekunde bedeuten: Das Laufwerk hat intern Sektoren umgelagert.
Das ist ein Alterungszeichen deutlich vor der S.M.A.R.T.-Warnung.

### Korrigierte Lesefehler

Genau wie bei WHEA: Kein Ausfall, sondern eine Zunahme, die nur dann
entsteht, wenn die Oberfläche schlechter wird. Alarm ab 100 zusätzlichen
korrigierten Fehlern in sechs Stunden.

Nicht korrigierbare **Lesefehler** bedeuten Datenverlust beim Lesen —
Warnung. Nicht korrigierbare **Schreibfehler** bedeuten unmittelbaren
Datenverlust — kritisch, sofort handeln.

### Controller-Rücksetzungen (Ereignis 129)

Der Controller hat auf eine Anfrage keine Antwort bekommen und das
Laufwerk neu gestartet. Häufen sie sich, geht das einem Plattenausfall
oft um Tage voraus. Zuerst Verkabelung und Firmware prüfen — nicht jede
129er-Meldung ist die Platte.

### Verbindungsgeschwindigkeit der Netzwerkkarten

Überwacht wird **nicht** gegen einen festen Wert, sondern gegen den
Vortag. Manche Geräte in der Schule hängen zu Recht an 100 Mbit; ein
*Abfall* gegenüber gestern ist dagegen immer ein Befund — praktisch
immer ein Kabel- oder Steckproblem. Ohne diese Regel fällt so etwas nur
als „das Netz ist langsam" auf und wird nie gefunden.

### Speichermodule

Meldet WHEA einen Speicherfehler, ist die erste Frage immer „welcher
Riegel?". Steckplatz, Hersteller, Teilenummer und Kapazität stehen
deshalb im Dashboard — statt dass jemand den Server aufschrauben muss.

### Speicherzusage

Nicht zu verwechseln mit der Auslastung. Ist die Zusage aufgebraucht,
verweigern Programme den Start, obwohl der Arbeitsspeicher noch nicht
voll ist. Alarm ab 90 %.

---

## Vorgehen bei einem Befund

1. **Dashboard „Hardware-Leistung & Protokolle"**, oben das Gerät
   auswählen.
2. Tabelle **„Hardwarefehler der letzten 24 Stunden"** — der Bereich sagt,
   wo zu suchen ist. „Speichercontroller" ist eine andere Baustelle als
   „Dateisystem", auch wenn beides nach Plattenproblem aussieht.
3. Unten **„Nur die ernsten Befunde"** — dort steht der Klartext samt
   Stoppcode, Laufwerksbezeichnung oder Sensornamen.
4. Verlauf ansehen: Ein einzelner Ausschlag ist Zufall, eine Treppe nach
   oben ist ein sterbendes Bauteil.

---

## Wenn nichts ankommt

```logql
{job="windows_events", channel="Hardware"}
```

Bleibt das leer, greift der Alarm `KeineHardwareProtokolleMehr` nach
zwei Stunden — ein völlig stiller Hardware-Kanal ist ungewöhnlich, weil
selbst im Normalbetrieb regelmäßig Start- und Stromereignisse anfallen.

Der Kanal entsteht erst ab **Agent-Version 1.2.0**. Steht in
`C:\ProgramData\SchulMonitoring\agent-version.txt` noch etwas Älteres,
hat das Gerät die neue Alloy-Konfiguration noch nicht:

```powershell
Get-Content "$env:ProgramData\SchulMonitoring\agent-version.txt"
Get-Service Alloy
```

Aktuelle Skripte nach SYSVOL kopieren, auf dem Server
`sudo ./stack/pakete-holen.sh` ausführen und das Gerät neu starten.

Bei den Leistungsdaten gilt: Sie kommen aus `windows_exporter`, nicht aus
dem Agent-Skript. Fehlen sie, obwohl das Gerät online ist, prüfen:

```powershell
Invoke-WebRequest http://localhost:9182/metrics -UseBasicParsing |
  Select-Object -ExpandProperty Content |
  Select-String "windows_logical_disk_read_seconds_total"
```

---

## Metriknamen

Falls einzelne Panels leer bleiben, lohnt der Abgleich mit dem, was der
Exporter tatsächlich liefert — die Namen ändern sich zwischen
Hauptversionen von `windows_exporter` gelegentlich:

```bash
curl -s http://localhost:9090/api/v1/label/__name__/values |
  python3 -m json.tool | grep -E "logical_disk|windows_net_packets|windows_tcp"
```

Vom Agenten selbst kommen:

| Metrik | Inhalt |
|---|---|
| `schule_hardware_ereignisse_24h` | Protokolleinträge je Bereich |
| `schule_hardware_fehler_24h` | davon Stufe „Fehler" oder „Kritisch" |
| `schule_hardware_letztes_ereignis_zeitstempel` | Zeitpunkt des jüngsten Eintrags |
| `schule_datentraeger_spitzenlatenz_sekunden` | vom Laufwerk gemeldete Spitzenlatenz |
| `schule_datentraeger_korrigierte_fehler` | selbst korrigierte Lesefehler |
| `schule_datentraeger_schreibfehler` | nicht korrigierbare Schreibfehler |
| `schule_speichermodul_kapazitaet_bytes` | Speichermodule je Steckplatz |
| `schule_netzwerkkarte_geschwindigkeit_bit` | ausgehandelte Verbindungsgeschwindigkeit |
