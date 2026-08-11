# Prüfen

```bash
./pruefen.sh
```

Läuft in etwa zwanzig Sekunden, braucht keinen laufenden Stack und kein
Docker. `promtool` und `amtool` werden beim ersten Aufruf einmalig geladen –
dieselben Fassungen, die auch im Stack laufen – und unter `.werkzeuge/`
abgelegt.

Rückgabewert 0 heißt: alles in Ordnung. Damit lässt sich das Skript
unverändert vor einem Commit oder in einer Bauleitung verwenden.

---

## Was geprüft wird

| Prüfung | Fängt ab |
|---|---|
| PowerShell-Skripte | PowerShell-7-Syntax auf einem 5.1-Server, Zuweisungen an automatische Variablen, Fremdzeichen, unausgeglichene Klammern, nie zugewiesene `$script:`-Variablen |
| Shell und Python | Syntaxfehler |
| YAML und JSON | Unlesbare Dateien, doppelte Dashboard-Kennungen |
| `promtool check rules` | Ungültiges PromQL in den Alarmregeln |
| `promtool test rules` | **Falsche Alarmlogik** – siehe unten |
| Dashboard-Abfragen | Ungültiges PromQL in den Panels |
| Meldungstexte | Labels, die es auf der Metrik gar nicht gibt |
| `amtool check-config` | Fehlerhafte Alarmierung, kaputte Empfängerlisten |
| Dashboards | Fehlende Einheiten, Beschreibungen, Schwellwerte, Verweise |
| Geräteerkennung | SNMP-Kodierung, Einordnung, Suchlauf, Netzplan |
| Zusammenhalt | Benutzte Metriken, die niemand erzeugt; tote Doku-Verweise |

---

## Die wichtigste Prüfung

`promtool test rules` ist der Kern. Alles andere prüft Syntax – diese
Prüfung schickt **erfundene Messwertverläufe durch die echten Regeln** und
kontrolliert, was dabei herauskommt.

Beispiel aus `stack/prometheus/tests/alarmregeln_test.yml`: Eine 200-GB-Platte
verliert gleichmäßig 1 GB alle fünf Minuten. Nach 14 Stunden muss
`SpeicherplatzLaeuftInVierTagenVoll` stehen – und zwar nur für diese Platte,
nicht für die danebenliegende, die sich nicht verändert.

Geprüft wird immer beides:

* **Der Alarm kommt**, wenn er soll – mit den richtigen Labels, zur richtigen
  Zeit (`for:` wird mitgeprüft) und mit einem Meldungstext, der die
  eingesetzten Werte tatsächlich enthält.
* **Der Alarm schweigt** im Normalbetrieb. Dafür gibt es einen eigenen
  Testfall mit einer vollständig gesunden Umgebung, in dem 21 Alarme
  ausdrücklich *nicht* auslösen dürfen.

Das zweite ist das wichtigere. Ein Monitoring, das ständig grundlos meldet,
schaut sich nach zwei Wochen niemand mehr an.

---

## Warum die Label-Prüfung

Sie fängt eine Sorte Fehler ab, die sonst erst beim ersten echten Alarm
auffällt. In einem Meldungstext steht:

```
Datentraeger {{ $labels.modell }} (Seriennummer {{ $labels.seriennummer }})
```

Setzt der Sammler diese Labels auf genau dieser Metrik nicht, rendert
Prometheus stillschweigend eine leere Zeichenkette. In der Mail steht dann:

```
Datentraeger  (Seriennummer ) in srv-datei-01 meldet ...
```

Nichts geht kaputt, nichts fällt auf – und im Ernstfall fehlt genau die
Angabe, die man gebraucht hätte, nämlich welche Platte im Server zu tauschen
ist. Genau dieser Fehler steckte hier tatsächlich drin und wurde von der
Prüfung gefunden.

---

## Prüfungen einzeln laufen lassen

```bash
python3 werkzeuge/powershell-pruefung.py
python3 stack/grafana/dashboard-pruefung.py
python3 stack/prometheus/tests/labels-pruefung.py
python3 werkzeuge/metrikpruefung.py

cd stack/registrar && python3 test_erkennung.py

.werkzeuge/promtool test rules stack/prometheus/tests/alarmregeln_test.yml
```

---

## Prüft die Prüfung überhaupt etwas?

Eine Prüfsuite, die immer grün sagt, ist wertlos. Gegenprobe: etwas
absichtlich kaputt machen und nachsehen, ob es auffällt.

| Sabotage | Wird gefunden von |
|---|---|
| Schwellwert so verdreht, dass ein Alarm immer feuert | `promtool test rules` |
| Tippfehler in einem Metriknamen im Dashboard | Metrikprüfung |
| Ternärer Operator im Agent-Skript | PowerShell-Prüfung |
| PromQL mit fehlender Klammer im Dashboard | Abfragenprüfung |
| Meldungstext mit erfundenem Label | Label-Prüfung |
| Panel ohne Einheit | Dashboard-Prüfung |
| Toter Verweis im README | Doku-Prüfung |

Alle sieben werden erkannt. Beim ersten Durchlauf wurde die dritte *nicht*
gefunden – die Ternär-Erkennung verlangte eine Klammer vor dem `?`, die es
bei `$x = $a -eq 4 ? $true : $false` nicht gibt. Das ist genau der Grund,
warum man diese Gegenprobe macht.

---

## Was nicht geprüft wird

Ehrlichkeitshalber:

* **LogQL** – die Loki-Abfragen und -Regeln. Dafür gibt es kein Werkzeug,
  das ohne laufenden Loki auskommt. 17 Dashboard-Abfragen und 42 Loki-Regeln
  bleiben damit ungeprüft.
* **Das Zusammenspiel im Betrieb.** Ob `windows_exporter` die erwarteten
  Metriknamen liefert, zeigt erst ein echtes Gerät. Die Namen ändern sich
  zwischen Hauptversionen gelegentlich.
* **Der Umbau des Installationsmediums.** Braucht Hyper-V und Windows.
