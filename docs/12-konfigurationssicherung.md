# Konfigurationssicherung der Netzgeräte

Jede Nacht um 03:20 holt die VM die Konfiguration von FortiGate und
UniFi-Controller und legt sie in einem Git-Verzeichnis ab. Das bringt zwei sehr
verschiedene Dinge auf einmal:

**Wiederherstellung.** Stirbt die FortiGate, steht der letzte Stand bereit –
statt Regeln aus dem Gedächtnis nachzubauen.

**Sicherheit.** Jede Änderung wird sichtbar. Kommt nachts um drei eine
Firewall-Regel dazu, die niemand angelegt hat, ist das ein Befund.

---

## Warum kein UniFi-Backup

Naheliegend wäre die `.unf`-Sicherung des Controllers. Die ist aber ein
undurchsichtiges Archiv: Man kann sie zurückspielen, aber nicht vergleichen.
Für die zweite Hälfte des Nutzens – zu sehen, *was* sich geändert hat – ist sie
wertlos.

Stattdessen werden die Einstellungen als JSON abgerufen: Netze, WLANs,
Firewallregeln und -gruppen, Portprofile, Portweiterleitungen, Routing,
Benutzergruppen. Das ist lesbar und zeigt im Vergleich genau die geänderte
Zeile. Die JSON-Dateien werden vor dem Ablegen sortiert und eingerückt – sonst
erzeugte schon eine andere Reihenfolge einen scheinbaren Unterschied.

Bei der FortiGate ist es umgekehrt einfach: Die API liefert die vollständige
Konfiguration als Text. Herausgefiltert wird nur die Kopfzeile
`#conf_file_ver=`, die bei jedem Abruf einen neuen Zeitstempel enthält. Ohne
das meldete die Sicherung **jede Nacht** eine Änderung – und nach zwei Wochen
schaut niemand mehr hin.

---

## Einrichten

Im Regelfall nichts zu tun: `bootstrap.sh` legt den systemd-Zeitgeber an und
lässt einmal sofort laufen, damit gleich ein Ausgangsstand daliegt.

Voraussetzung sind die Zugangsdaten in `/opt/schulmonitoring/stack/.env`:

```bash
FORTIGATE_URL=https://10.0.0.1
FORTIGATE_TOKEN=...

UNIFI_URL=https://10.0.0.10:8443
UNIFI_BENUTZER=monitoring
UNIFI_PASSWORT=...
```

Beide Zugänge brauchen nur **Leserechte**. Fehlt einer, wird er stillschweigend
übersprungen.

Nachträglich eingetragen? Dann einmal von Hand anstoßen:

```bash
sudo systemctl start schulmonitoring-konfig.service
sudo journalctl -u schulmonitoring-konfig -n 40
```

---

## Änderungen ansehen

Die Ablage ist ein gewöhnliches Git-Verzeichnis:

```bash
cd /opt/schulmonitoring/konfig

git log --oneline                 # alle Stände
git show HEAD                     # was zuletzt anders wurde
git log -p -- fortigate.conf      # Verlauf einer einzelnen Datei
git diff HEAD~7 HEAD              # was sich in einer Woche getan hat
```

Beispielausgabe nach einer neu angelegten Firewall-Regel:

```diff
+edit 12
+set name "Freigabe Turnsaal"
+set srcintf "internal"
+set dstintf "wan1"
```

---

## Die Alarme

| Alarm | Grad | Wann |
|---|---|---|
| `NetzkonfigurationGeaendert` | info | irgendetwas hat sich geändert |
| `NetzkonfigurationNachtsGeaendert` | warning | Änderung zwischen 21 und 5 Uhr |
| `KonfigurationssicherungFehlgeschlagen` | warning | Abruf schlägt fehl |
| `KeineKonfigurationssicherungMehr` | warning | seit über zwei Tagen kein Lauf |

Die erste ist bewusst nur `info` und wird gesammelt zugestellt: Wer tagsüber an
der Firewall arbeitet, soll keine Warnung bekommen. **Dieselbe Änderung
nachts** ist eine andere Geschichte – da arbeitet normalerweise niemand, und
diese Regel läuft über die Sicherheitsadresse.

> Die Uhrzeit wird in **UTC** ausgewertet. In Österreich ist das im Winter eine
> und im Sommer zwei Stunden vor der Ortszeit. Wer das genauer haben will,
> passt die Grenzen in `stack/prometheus/rules/90-konfiguration-netzplan.yml`
> an.

Ein Fehlschlag ist fast immer ein abgelaufenes API-Token oder ein geändertes
Kennwort:

```bash
sudo journalctl -u schulmonitoring-konfig --since today
```

---

## Wichtig zum Aufbewahren

**Die Ablage enthält Geheimnisse.** In einer FortiGate-Konfiguration stehen
VPN-Schlüssel, RADIUS-Kennwörter und Zertifikate. Das Verzeichnis liegt deshalb
unter `0700` und wird bei jedem Lauf erneut so gesetzt.

Zwei Dinge, die daraus folgen:

* Die Ablage gehört **nicht** in ein entferntes Git-Verzeichnis, schon gar
  nicht in ein öffentliches. Der Zeitgeber pusht nirgendwohin.
* Sie gehört aber in die **Datensicherung der VM** – genau dafür ist sie da.
  Liegt sie nur auf der Maschine, die auch ausfallen kann, ist der halbe Nutzen
  weg.

Der Platzbedarf ist vernachlässigbar: Git speichert nur Unterschiede, eine
FortiGate-Konfiguration ist wenige hundert Kilobyte. Auch nach Jahren bleibt
das im zweistelligen Megabytebereich.

---

## Zeitpunkt ändern

```bash
sudo systemctl edit --full schulmonitoring-konfig.timer
```

`OnCalendar` anpassen, dann:

```bash
sudo systemctl daemon-reload
sudo systemctl restart schulmonitoring-konfig.timer
systemctl list-timers schulmonitoring-konfig.timer
```

`Persistent=true` sorgt dafür, dass ein verpasster Lauf – etwa weil die VM
nachts aus war – beim nächsten Start nachgeholt wird, statt einen Tag zu
überspringen.
