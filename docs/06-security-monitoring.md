# Angriffserkennung

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
