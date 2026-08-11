# Sicherheit

Zwei Ebenen, die sich gegenseitig brauchen:

* **Angriffserkennung** – „passiert gerade etwas?" Wertet die
  Ereignisprotokolle aus.
* **Härtungs-Baseline** – „sind wir überhaupt richtig eingestellt?" Prüft
  die Konfiguration der Geräte.

Die Reihenfolge ist kein Zufall: Ohne die Baseline-Einstellungen
protokolliert Windows die gesuchten Ereignisse gar nicht erst — dann läuft
die beste Angriffserkennung ins Leere.

---

# Teil 1: Angriffserkennung

Was hier erkannt wird, was dafür eingeschaltet sein muss, und was bei einem
Treffer zu tun ist.

---

## Wichtig zuerst: ohne Audit-Richtlinien bleibt es leer

Windows protokolliert in der Standardeinstellung nur einen Teil dessen, was
für die Erkennung gebraucht wird. Die folgende Gruppenrichtlinie ist die
Voraussetzung dafür, dass das Security-Dashboard überhaupt Daten hat.

**Computerkonfiguration → Richtlinien → Windows-Einstellungen →
Sicherheitseinstellungen → Erweiterte Überwachungsrichtlinienkonfiguration**

| Kategorie | Unterkategorie | Einstellung |
|---|---|---|
| Anmelden/Abmelden | Anmeldung | Erfolg und Fehler |
| Anmelden/Abmelden | Kontosperrung | Erfolg |
| Anmelden/Abmelden | Spezielle Anmeldung | Erfolg |
| Kontenverwaltung | Benutzerkontenverwaltung | Erfolg und Fehler |
| Kontenverwaltung | Sicherheitsgruppenverwaltung | Erfolg |
| Detaillierte Verfolgung | Prozesserstellung | Erfolg |
| Rechteverwendung | Vertrauliche Rechteverwendung | Fehler |
| Richtlinienänderung | Änderung der Überwachungsrichtlinie | Erfolg |
| Kontoanmeldung | Kerberos-Dienstticketvorgänge | Erfolg und Fehler |
| Objektzugriff | Objektzugriff durch Verzeichnisdienst (nur DCs) | Erfolg |

**Ebenfalls einschalten** – ohne das fehlt die halbe Erkennung:

*Administrative Vorlagen → System → Überwachungsprozesserstellung →*
**Befehlszeile in Prozesserstellungsereignissen einbeziehen: Aktiviert**

Erst damit steht im Ereignis 4688 auch, *was* ausgeführt wurde. Ohne diese
Einstellung sieht man nur „powershell.exe wurde gestartet", was für die
Erkennung wertlos ist.

*Administrative Vorlagen → Windows-Komponenten → Windows PowerShell →*
**Einschalten der PowerShell-Skriptblockprotokollierung: Aktiviert**

Damit landen auch entschlüsselte, ursprünglich verschleierte Befehle im
Protokoll – der wirksamste einzelne Schalter gegen Schadsoftware, die sich
in kodierten PowerShell-Befehlen versteckt.

**Protokollgröße erhöhen**, sonst überschreiben sich die Ereignisse, bevor
sie abgeholt werden:

*Windows-Komponenten → Ereignisprotokolldienst → Sicherheit →*
**Maximale Protokollgröße: 262144 KB** (256 MB)

---

## Was erkannt wird

### Angriffe auf Kennwörter

| Erkennung | Auslöser |
|---|---|
| Kennwortangriff | über 20 Fehlanmeldungen in 5 Minuten auf einem Gerät |
| Massiver Angriff | über 100 Fehlanmeldungen in 10 Minuten |
| **Vermutlich erfolgreich** | erfolgreiche Anmeldung direkt nach über 30 Fehlversuchen |
| Kontosperrungen | mehr als 3 in 10 Minuten |
| Ungewöhnlich viele Fernzugriffe | über 5 RDP-Anmeldungen in 10 Minuten |

Die dritte Zeile ist die wichtigste: Sie bedeutet, dass jemand
wahrscheinlich hineingekommen ist.

### Rechteausweitung und Festsetzen

| Erkennung | Ereignis |
|---|---|
| Jemand einer privilegierten Gruppe hinzugefügt | 4732 / 4728 / 4756 |
| Neues Benutzerkonto | 4720 |
| Neuer Systemdienst installiert | 7045 |
| Neue geplante Aufgabe | 4698 |
| **Ereignisprotokoll geleert** | 1102 / 104 |

Ein geleertes Sicherheitsprotokoll ist praktisch immer ein Versuch, Spuren
zu verwischen. Das ist kein Wartungsvorgang.

### Verschlüsselungstrojaner

Die Regel **`SchattenkopienGeloescht`** ist die wichtigste im ganzen
Regelwerk. Fast jeder Verschlüsselungstrojaner löscht kurz vor der
Verschlüsselung die Schattenkopien, damit man nichts wiederherstellen kann:

```
vssadmin delete shadows /all /quiet
wbadmin delete catalog -quiet
```

Passiert das, bleiben typischerweise Minuten. **Gerät sofort vom Netz
trennen, Datensicherung prüfen.**

### Verdächtige Ausführung

* Verschleierte PowerShell (`-EncodedCommand`, `FromBase64String`,
  `DownloadString`, `-w hidden`)
* Zweckentfremdete Bordmittel (`certutil -urlcache`, `bitsadmin /transfer`,
  `mshta http…`, `regsvr32 … scrobj`)
* Bekannte Angriffswerkzeuge (Mimikatz, LaZagne, PsExec-Dienst,
  BloodHound, Rubeus)
* Defender meldet Schadsoftware oder wurde abgeschaltet

### Active Directory

* **Kerberoasting** – viele Ticketanforderungen mit schwacher
  RC4-Verschlüsselung (4769 mit `0x17`). So werden Dienstkonto-Kennwörter
  offline geknackt.
* Auffällig viele Verzeichnisdienst-Zugriffe (4662) – kann eine
  AD-Replikation durch einen Angreifer sein.

### FortiGate

* IPS meldet schwere Angriffe – und getrennt davon: IPS hat einen schweren
  Angriff nur **erkannt statt blockiert** (Profil steht auf „monitor")
* Virenfund im Datenverkehr
* Kontakt zu einem bekannten Befehlsserver eines Botnetzes – das ist der
  stärkste Hinweis auf eine bereits aktive Infektion im Schulnetz
* Fehlgeschlagene Anmeldungen an der Firewall-Verwaltung
* Angriffe auf den VPN-Zugang

### Die Überwachung selbst

Zwei Regeln passen darauf auf, dass überhaupt noch Protokolle ankommen:
`KeineWindowsProtokolleMehr` und `KeineFortiGateProtokolleMehr`. Ein
plötzlich versiegender Protokollstrom ist selbst ein Alarmsignal.

---

## Bei einem Treffer

1. **Gerät finden** – Der Alarm nennt Gerät und Raum.
2. **Kontext ansehen** – Dashboard „Sicherheit & Angriffserkennung", oben
   das Gerät auswählen. Dort stehen die Ereignisse um den Zeitpunkt herum.
3. **Genauer nachsehen** in Grafana unter *Explore* → Loki:

```logql
{job="windows_events", geraet="pc-edv-12"}
  | json
  | event_id="4625"

# Von welcher IP kamen die Fehlversuche?
{job="windows_events", channel="Security"} |= "4625"
  | json
  | line_format "{{.event_data_IpAddress}} -> {{.event_data_TargetUserName}}"

# Alles rund um einen Zeitpunkt, ohne Filter
{job="windows_events", geraet="pc-edv-12"}
```

4. **Bei begründetem Verdacht:** Gerät vom Netz trennen (nicht
   herunterfahren – der Arbeitsspeicher enthält Spuren), betroffene
   Kennwörter zurücksetzen, Quell-IP auf der FortiGate sperren.

Die Protokolle bleiben 90 Tage auf dem Monitoring-Server und liegen damit
außerhalb der Reichweite eines Angreifers, der lokale Protokolle löscht.
Das ist der eigentliche Wert der zentralen Sammlung.

---

## Fehlalarme abstellen

Ein Fehlalarm, der zehnmal am Tag kommt, sorgt dafür, dass irgendwann auch
der echte übersehen wird. Zwei Wege:

**Schwellwert anheben** – in
`/opt/schulmonitoring/stack/loki/rules/fake/security.yml`:

```yaml
      - alert: BruteForceAnmeldeversuche
        expr: |
          sum by (geraet) (
            count_over_time({job="windows_events", channel="Security"}
              |= "4625" | json | event_id="4625" [5m])
          ) > 40          # statt 20
```

**Ein bekanntes Gerät ausnehmen** – etwa einen Dienstrechner, der durch ein
hinterlegtes altes Kennwort dauernd Fehlanmeldungen erzeugt:

```yaml
            {job="windows_events", channel="Security", geraet!="srv-scanner-01"}
```

Danach:

```bash
docker restart mon-loki
```

Besser ist es allerdings meistens, die Ursache zu beheben: Ein Gerät, das
dauerhaft Fehlanmeldungen erzeugt, hat ein echtes Problem.

---

## Sinnvolle Ergänzung: Sysmon

Sysmon aus den Sysinternals liefert deutlich mehr Details – besonders
Prozessabstammung und Zugriffe auf den LSASS-Prozess (der klassische Weg,
Kennwörter aus dem Speicher zu stehlen).

Nach der Installation von Sysmon genügt ein Block in der Datei
`agent/alloy-client.alloy.tmpl` auf dem Monitoring-Server:

```hcl
loki.source.windowsevent "sysmon" {
  eventlog_name          = "Microsoft-Windows-Sysmon/Operational"
  bookmark_path          = "C:\\ProgramData\\SchulMonitoring\\alloy\\sysmon.xml"
  use_incoming_timestamp = true
  labels                 = { job = "windows_events", channel = "Sysmon" }
  forward_to             = [loki.process.ereignisse.receiver]
}
```

Danach `sudo ./stack/pakete-holen.sh` – beim nächsten Neustart holen sich
alle Geräte die erweiterte Konfiguration von selbst. Die GPO muss nicht
angefasst werden.

> Ist Sysmon **nicht** installiert, darf dieser Block nicht in der
> Konfiguration stehen: Alloy findet den Kanal dann nicht und meldet einen
> Fehler.

---
---

# Teil 2: Härtungs-Baseline

Die Angriffserkennung sagt, wenn etwas passiert. Die Baseline sagt, wie
wahrscheinlich es überhaupt passieren kann.

Jedes Gerät prüft sich stündlich selbst gegen **45 Punkte** aus den
CIS Benchmarks, dem BSI-Grundschutz und der Microsoft Security Baseline —
bewusst nur die, die in einer Schule realistisch umsetzbar sind und einen
echten Unterschied machen. Das Ergebnis steht im Dashboard
**„Sicherheits-Baseline"**.

Jede Prüfung liefert einen von drei Werten:

| Wert | Bedeutung |
|---|---|
| **1** | eingestellt wie empfohlen |
| **0** | Abweichung |
| **2** | auf diesem Gerät nicht prüfbar oder nicht zutreffend |

Der dritte Wert ist wichtig: Ein Standrechner hat kein Credential Guard,
ein Client ist kein Domänencontroller. Solche Fälle verfälschen den
Erfüllungsgrad nicht, tauchen aber nachvollziehbar auf.

---

## Was geprüft wird

### Diebstahl von Zugangsdaten

Der häufigste Weg, wie aus einem infizierten Rechner ein infiziertes
Schulnetz wird.

| Prüfung | Warum es zählt |
|---|---|
| LSA-Schutz (`RunAsPPL`) | Ohne ihn lassen sich Kennwörter direkt aus dem Speicher auslesen |
| WDigest-Klartext aus | Ist der Wert gesetzt, ist das fast immer die Handschrift eines Angreifers |
| NTLMv1 unterbunden | NTLMv1-Hashes sind in Minuten geknackt |
| LLMNR deaktiviert | Der Klassiker: Anmeldedaten aus dem Schülernetz abfangen |
| NetBIOS deaktiviert | Dasselbe Problem wie LLMNR |
| Anonyme Abfrage gesperrt | Verhindert das Auslesen von Konten- und Freigabelisten |
| Credential Guard | Kapselt Anmeldedaten hardwareseitig ab |

### Netzwerk und Verschlüsselung

SMBv1 (Einfallstor von WannaCry), SMB-Signierung gegen Relay-Angriffe,
veraltete TLS-Versionen, Firewall-Standardverhalten, RDP mit
Netzwerkauthentifizierung.

### Ausführung und Nachvollziehbarkeit

PowerShell-Skriptblock- und Modulprotokollierung, PowerShell 2.0 entfernt
(damit lässt sich jede Protokollierung umgehen), Befehlszeile in
Ereignis 4688, AutoRun, UAC, Secure Boot — dazu die sechs
Überwachungs-Unterkategorien und die Größe des Sicherheitsprotokolls.

> Die Unterkategorien werden über ihre **GUID** gelesen, nicht über den
> Namen. Auf einem deutschen Windows heißen sie „Prozesserstellung" statt
> „Process Creation" — ein Abgleich über Klartext würde dort reihenweise
> Fehlalarme erzeugen.

### Virenschutz

Manipulationsschutz, Cloudschutz, Netzwerkschutz, PUA-Schutz und die
Regeln zur Angriffsflächenreduzierung (ASR) — besonders die eine, die das
Auslesen von Anmeldedaten aus LSASS blockiert.

### Konten

Gastkonto, eingebautes Administratorkonto, Anzahl lokaler Administratoren
und **LAPS**. Letzteres ist der wirkungsvollste Einzelpunkt der ganzen
Liste: Ohne eindeutige lokale Administratorkennwörter öffnet ein einziges
erbeutetes Kennwort sämtliche Rechner der Schule.

### Rechteausweitung

Dienstpfade mit Leerzeichen ohne Anführungszeichen (Windows startet dann
unter Umständen ein untergeschobenes Programm mit Systemrechten) und die
Druckwarteschlange auf Domänencontrollern (PrintNightmare).

### Active Directory

Läuft nur auf Domänencontrollern und braucht das PowerShell-Modul
`ActiveDirectory`:

| Prüfung | Warum es zählt |
|---|---|
| krbtgt-Kennwortalter | Wer den Schlüssel erbeutet, stellt sich beliebige Tickets aus — bis das Kennwort **zweimal** gewechselt wurde |
| Konten ohne Vorauthentifizierung | Kennwort offline knackbar, ganz ohne Anmeldung (AS-REP Roasting) |
| Dienstkonten mit altem Kennwort | Angriffsfläche für Kerberoasting |
| Anzahl Domänen-Administratoren | Jedes Konto mehr ist ein Weg mehr, die Domäne zu übernehmen |
| Karteileichen | Aktivierte Konten ohne Anmeldung seit 90 Tagen |
| Kennwortlänge, Sperrschwelle | Ohne Sperrschwelle laufen Kennwortangriffe unbegrenzt |

Die Gruppen werden über ihre **SID** angesprochen (`…-512` für
Domänen-Admins), nicht über den Namen — auf einem deutschen AD heißt die
Gruppe „Domänen-Admins".

---

## Wie damit gearbeitet wird

Das Dashboard ist nach Wirkung sortiert, nicht alphabetisch. Der übliche
Ablauf:

1. **„Offene Punkte"** — ganz oben steht, was die meisten Geräte betrifft.
   Fast immer fehlt überall dieselbe Gruppenrichtlinie. Ein Handgriff hebt
   dann den Wert der ganzen Schule.
2. Die Spalte **„Was zu tun ist"** enthält die konkrete Maßnahme, teils
   samt Registry-Wert. Nichts nachschlagen müssen.
3. Prüfung oben im Kopf auswählen → die Tabelle **„Geräte, bei denen …
   abweicht"** listet genau die Rechner, die angefasst werden müssen.
4. **„Erfüllungsgrad je Bereich"** zeigt, wo der nächste Handgriff am
   meisten bringt.

Die Alarme sind **je Prüfung aggregiert**, nicht je Gerät: Fehlt in der
ganzen Schule dieselbe Einstellung, ist das eine Mail mit der Anzahl
betroffener Geräte.

### Zwei Alarme, die keine Baseline-Meldungen sind

Diese beiden gehen als `critical` sofort raus, weil sie keine
Fehlkonfiguration beschreiben, sondern einen laufenden Angriff:

* **`DefenderManipulationsschutzAus`** — der Manipulationsschutz schaltet
  sich nicht von selbst ab. Ist er aus, hat das jemand getan.
* **`WdigestKlartextAktiv`** — diese Einstellung ist seit Jahren nicht mehr
  Standard. Wird sie gesetzt, ist das die Vorbereitung eines Angriffs auf
  Zugangsdaten.

Dazu kommt **`HaertungNeueAbweichung`**: Eine Einstellung, die gestern noch
in Ordnung war und es heute nicht mehr ist. Entweder eine geplante Änderung
— oder jemand hat gezielt eine Schutzfunktion abgeschaltet.

---

## Realistisch bleiben

Ein frisch installiertes Windows erreicht typischerweise **40 bis 60 %**.
Das ist normal und kein Grund zur Panik — es ist der Ausgangspunkt.

Was sich in der Praxis bewährt hat: nicht alles auf einmal, sondern die
Punkte mit hoher Schwere zuerst und immer per Gruppenrichtlinie, nie von
Hand am einzelnen Gerät. Nach jeder Änderung einen Tag beobachten, ob im
Unterricht etwas klemmt. Der Verlauf im Dashboard zeigt, ob es vorangeht.

Zwei Punkte brauchen erfahrungsgemäß Absprache, weil sie im Schulbetrieb
etwas kaputt machen können:

* **Kontrollierter Ordnerzugriff** blockiert gern legitime Fachsoftware.
* **ASR-Regeln** können ältere Programme ausbremsen — erst im
  Überwachungsmodus (`AttackSurfaceReductionRules_Actions = 2`) laufen
  lassen, dann auf Blockieren stellen.

Eine Prüfung, die in der Schule bewusst anders gelöst ist, gehört nicht
ignoriert, sondern dokumentiert. Wer eine Prüfung dauerhaft ausblenden
will, kommentiert den entsprechenden `Add-Pruefung`-Aufruf in
`agent/Collect-SecurityBaseline.ps1` aus und rollt die neue Agent-Version
aus — dann fehlt der Punkt nachvollziehbar in der Bewertung, statt
dauerhaft rot zu leuchten.

---

## Von Hand prüfen

Auf einem Gerät nachsehen, was genau bemängelt wird:

```powershell
powershell -ExecutionPolicy Bypass `
  -File "$env:ProgramData\SchulMonitoring\Collect-SecurityBaseline.ps1" -Verbose
```

Die Ausgabe listet jede Prüfung mit `ok`, `ABWEICHUNG` oder `n/a` und
endet mit dem Erfüllungsgrad des Geräts.
