# Aufgabe für den IT-Assistenten

Dieses Dokument ist der Auftrag. Es ist so geschrieben, dass ein Assistent
damit allein zurechtkommt – ohne Rückfragen, ohne Vorwissen über die Schule.

---

## Was dieses Projekt ist

Eine vollständige Überwachung der Schul-IT: Windows-Server und -Clients,
Linux, UniFi Access Points und Switches, FortiGate-Firewall, Active
Directory. Aufgebaut über ein einziges PowerShell-Skript auf dem
Hyper-V-Server, ausgerollt per Gruppenrichtlinie.

| Verzeichnis | Inhalt |
|---|---|
| `deploy/` | PowerShell-Skript, das die Monitoring-VM erzeugt |
| `agent/` | Windows-Agent (GPO) und Linux-Agent |
| `stack/` | Der Monitoring-Stack: Prometheus, Loki, Grafana, Alertmanager |
| `werkzeuge/` | Prüfskripte |
| `docs/` | 13 Anleitungen |
| `pruefen.sh` | Prüft alles auf einmal |

**Fang hier an:** `README.md`, dann `docs/01-architektur.md`, dann
`docs/13-pruefen.md`.

---

## Die goldene Regel

```bash
./pruefen.sh
```

Muss **vor und nach** jeder Änderung fehlerfrei durchlaufen. 17 Prüfungen,
etwa zwanzig Sekunden, kein laufender Stack nötig. Rückgabewert 0 heißt
in Ordnung.

Beim ersten Aufruf lädt das Skript `promtool` und `amtool` nach – dafür
braucht es einmalig Internetzugang.

Eine Änderung, nach der `./pruefen.sh` fehlschlägt, ist nicht fertig.
Das ist nicht verhandelbar und ersetzt jede Diskussion darüber, ob etwas
funktioniert.

---

## Aufgaben, aufsteigend nach Schwierigkeit

Arbeite sie der Reihe nach ab. Jede baut auf der vorigen auf.

### Aufgabe 1 – Orientieren (nichts ändern)

Lass `./pruefen.sh` laufen und beantworte schriftlich:

1. Wie viele Alarmregeln gibt es, aufgeteilt nach Prometheus und Loki?
2. Was macht `stack/registrar/erkennung.py`, in drei Sätzen?
3. Warum sind die Agent-Skripte auf `Set-StrictMode -Version 1.0`
   gesetzt und nicht auf `Latest`? Die Begründung steht im Quelltext.
4. Welche drei Dinge prüft `pruefen.sh`, die **kein** reiner
   Syntaxprüfer finden würde?

**Fertig, wenn:** Die vier Antworten stimmen und nichts geändert wurde.

Das ist der eigentliche Test. Wer ein fremdes Projekt nicht lesen kann,
soll es nicht ändern.

### Aufgabe 2 – Tonerstände ergänzen

Die Drucker stehen bereits per SNMP im Inventar, aber die Füllstände
werden nicht ausgelesen.

1. Ergänze in `stack/snmp/snmp-schule.yml.tmpl` ein Modul `drucker_schule`,
   das die Printer-MIB abfragt (`1.3.6.1.2.1.43.11.1.1` –
   `prtMarkerSuppliesLevel` und `prtMarkerSuppliesMaxCapacity`, dazu
   `prtMarkerSuppliesDescription` als Beschriftung).
2. Leg eine Alarmregel an: Warnung unter 15 % Restfüllung.
3. Ergänze eine Kachel im Dashboard `stack/grafana/dashboards/30-netzwerk.json`.
4. Schreib einen Regeltest in `stack/prometheus/tests/alarmregeln_test.yml`,
   der beides prüft: dass der Alarm bei 8 % kommt und bei 40 % schweigt.

**Fertig, wenn:** `./pruefen.sh` fehlerfrei durchläuft und der neue
Regeltest gegen eine absichtlich verdrehte Schwelle fehlschlägt.

Der letzte Halbsatz ist wichtig. Ein Test, der immer besteht, ist kein Test.

### Aufgabe 3 – Totmannschalter

Das ist die einzige echte Lücke im System: **Stirbt die Monitoring-VM,
meldet das niemand.** Host-Neustart nachts, Platte voll, VM abgestürzt –
das Dashboard ist weg und die Alarme kommen einfach nicht mehr.

Bau beide Hälften:

**Nach außen.** Der Alertmanager pingt regelmäßig einen externen Dienst
an (healthchecks.io oder Vergleichbares). Bleibt der Ping aus, schickt
der Dienst eine Mail – von außerhalb der Schule, also unabhängig davon,
ob dort gerade etwas läuft.

**Nach innen.** Eine geplante Aufgabe auf dem Domänencontroller, die
stündlich `http://<VM>/api/health` abfragt und bei drei Fehlschlägen in
Folge über den Mailserver der Schule eine Nachricht schickt. PowerShell
5.1, keine Fremdmodule.

Beachte dabei:

* Der interne Weg darf **nicht** über den Alertmanager laufen – der ist
  ja mit ausgefallen. Das ist der ganze Punkt.
* Die Zugangsdaten gehören in die `.env`, nicht ins Skript.
* Die Einrichtung muss ohne Handgriff mitkommen, wie alles andere auch.
  Sieh dir an, wie `Install-AdPruefung` in
  `agent/Install-MonitoringAgent.ps1` das macht.
* Eine Anleitung unter `docs/` gehört dazu, und ein Verweis darauf im
  README.

**Fertig, wenn:** `./pruefen.sh` fehlerfrei durchläuft, das PowerShell-Skript
gegen `werkzeuge/powershell-pruefung.py` sauber ist, und du beschreiben
kannst, wie du geprüft hast, dass der interne Weg auch ohne laufenden
Alertmanager funktioniert.

### Aufgabe 4 – Einen Fehler finden, den ich übersehen habe

In `docs/13-pruefen.md` steht unter „Was nicht geprüft wird", welche
Bereiche keine Prüfung abdecken. Such dort. **LogQL** ist der
aussichtsreichste Kandidat: 42 Loki-Regeln und 17 Dashboard-Abfragen,
die nie jemand gegen einen Parser gehalten hat.

Ein Befund ist erst dann einer, wenn du zeigen kannst, wann er im Betrieb
weh tut. „Könnte man schöner schreiben" zählt nicht.

**Fertig, wenn:** Entweder ein belegter Befund samt Behebung und
Regressionstest – oder eine begründete Aussage, dass du keinen gefunden
hast, mit Angabe, wo du gesucht hast.

Die zweite Antwort ist eine gültige Antwort. Erfundene Befunde sind
schlimmer als keine.

---

## Regeln für die Arbeit

**Sprache.** Bezeichner, Kommentare und Dokumentation auf Deutsch, wie im
bestehenden Code. Keine Umlaute in PowerShell-Bezeichnern und
Metriknamen – das Projekt schreibt dort `ae`, `oe`, `ue`.

**Kommentare erklären das Warum, nicht das Was.** Sieh dir an, wie es die
bestehenden Dateien machen. `# Zaehler erhoehen` ist wertlos.

**PowerShell ist 5.1**, nicht 7. Kein ternärer Operator, kein `??`, kein
`-Parallel`. Der Hyper-V-Server hat kein PowerShell 7 und bekommt auch
keines. `werkzeuge/powershell-pruefung.py` fängt das ab.

**Jede neue Alarmregel braucht einen Regeltest.** Und zwar einen, der
beides prüft: dass der Alarm kommt, wenn er soll, und dass er im
Normalbetrieb schweigt. Das zweite ist das wichtigere.

**Nichts direkt auf Produktivsystemen.** Du arbeitest ausschließlich im
Git-Verzeichnis. Kein Zugriff auf Domänencontroller, FortiGate oder
UniFi-Controller. Was du baust, wird von einem Menschen ausgerollt.

**Ein Zweig je Aufgabe**, ein Pull Request, aussagekräftige
Commit-Nachrichten. Sieh dir `git log` an – die Nachrichten in diesem
Projekt erklären, *warum* etwas so gemacht wurde.

---

## Was du nicht tun sollst

* Keine Abhängigkeiten hinzufügen. Der Registrierungs-Dienst läuft
  bewusst auf reiner Standardbibliothek plus PyYAML. Wenn du eine
  Bibliothek brauchst, begründe es – meistens braucht man sie nicht.
* Keine bestehenden Prüfungen abschwächen, damit deine Änderung
  durchläuft. Wenn eine Prüfung im Weg steht, ist entweder die Änderung
  falsch oder die Prüfung – beides gehört besprochen, nicht umgangen.
* Nichts über die Hardware behaupten, was du nicht geprüft hast. Das
  Projekt ist nie auf echter Hyper-V-Hardware gelaufen. Wenn du
  vermutest, schreib dazu, dass du vermutest.
* Keine personenbezogenen Daten in Beispiele. Keine echten Benutzernamen,
  keine echten IP-Adressen aus der Schule.

---

## Wie du merkst, dass du fertig bist

```bash
./pruefen.sh && echo "FERTIG" || echo "NOCH NICHT"
```

Dazu ein kurzer Bericht: Was hast du geändert, warum, und wie hast du
geprüft, dass es stimmt. Drei Absätze reichen.

Wenn etwas nicht geklappt hat, schreib das hin. Ein ehrliches „Aufgabe 3
läuft, Aufgabe 4 habe ich nicht geschafft" ist mehr wert als ein
Bericht, der Vollständigkeit behauptet.
