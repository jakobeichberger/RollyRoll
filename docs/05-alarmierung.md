# Alarmierung

---

## Die drei Schweregrade

| Grad | Bedeutung | Zustellung |
|---|---|---|
| **critical** | Etwas ist ausgefallen oder fällt gleich aus | nach 20 s, Wiederholung alle 3 h |
| **warning** | Etwas läuft schief oder kündigt sich an | nach 45 s, Wiederholung alle 12 h |
| **info** | Nur zur Kenntnis | gesammelt, Wiederholung alle 7 Tage |

Dazu kommt eine eigene Markierung: **`vorwarnung: ja`**. Diese Regeln
schlagen an, *bevor* etwas kaputt ist – SSD am Lebensende, Platte läuft in
vier Tagen voll, Zertifikat läuft ab, Lüfter meldet Störung. In der
Gesamtübersicht haben sie eine eigene Kachel, weil sie die eigentlich
wertvollen Meldungen sind: Hier lässt sich noch in Ruhe handeln.

Sicherheitsvorfälle (`kategorie: security`) laufen an der normalen Bündelung
vorbei und gehen nach 10 Sekunden raus.

---

## E-Mail einrichten

In `/opt/schulmonitoring/stack/.env`:

```bash
SMTP_HOST=smtp.schule.local
SMTP_PORT=587
SMTP_BENUTZER=monitoring@schule.local
SMTP_PASSWORT=...
SMTP_VON=monitoring@schule.local
SMTP_TLS=true

ALARM_EMPFAENGER=admin@schule.local
ALARM_EMPFAENGER_KRITISCH=admin@schule.local,handy@schule.local
ALARM_EMPFAENGER_SECURITY=admin@schule.local
```

Danach:

```bash
cd /opt/schulmonitoring/stack
sudo ./bootstrap.sh          # erzeugt die Alertmanager-Konfiguration neu
```

Bei einem Relay ohne Authentifizierung `SMTP_BENUTZER` und `SMTP_PASSWORT`
leer lassen und `SMTP_PORT=25` setzen; je nach Relay dann auch
`SMTP_TLS=false`.

### Testen

```bash
docker exec mon-alertmanager amtool alert add \
  alertname=Testalarm severity=warning geraet=testrechner \
  --annotation=summary="Test der Alarmierung" \
  --alertmanager.url=http://localhost:9093
```

Kommt nichts an:

```bash
docker logs mon-alertmanager --tail 50 | grep -i -E "error|smtp"
```

---

## Zusätzlich Teams, Slack oder ntfy

```bash
WEBHOOK_URL=https://schule.webhook.office.com/webhookb2/...
```

Nach `sudo ./bootstrap.sh` gehen alle Alarme zusätzlich dorthin.

---

## Was von selbst unterdrückt wird

Ein ausgefallener Server löst sonst eine Lawine aus: Der Rechner ist weg,
also sind auch alle Dienste weg, alle Datenträger unbekannt, die
Datensicherung veraltet. Der Alertmanager unterdrückt das:

* Ist ein Server offline, kommen keine Meldungen mehr zu Diensten,
  Kapazität oder Leistung desselben Geräts.
* Meldet ein Gerät `critical`, entfällt die gleichlautende `warning`.
* Ist ein ganzer Switch nicht erreichbar, entfallen die Meldungen zu
  einzelnen Ports.

Umgekehrt gibt es eine Regel, die zuschlägt, wenn mehr als 30 % aller
Agenten gleichzeitig offline gehen. Das ist dann kein Geräteproblem,
sondern eines des Netzes – und wird auch so gemeldet.

---

## Alarme anschauen und stummschalten

Der Alertmanager horcht aus Sicherheitsgründen nur auf `127.0.0.1` der VM.
Für den Zugriff einen SSH-Tunnel aufbauen:

```bash
ssh -L 9093:localhost:9093 -L 9090:localhost:9090 monadmin@10.0.0.50
```

Dann im Browser `http://localhost:9093` (Alertmanager) bzw.
`http://localhost:9090` (Prometheus).

Stummschalten geht auch ohne Weboberfläche:

```bash
# Wartungsarbeiten an einem Server: vier Stunden Ruhe
docker exec mon-alertmanager amtool silence add \
  geraet=srv-datei-01 \
  --duration=4h --comment="Wartung Speichererweiterung" --author="admin" \
  --alertmanager.url=http://localhost:9093

docker exec mon-alertmanager amtool silence query \
  --alertmanager.url=http://localhost:9093
```

Ein regelmäßiger Blick auf die Stummschaltungen lohnt sich – eine vergessene
Stummschaltung ist der zuverlässigste Weg, einen echten Ausfall zu verpassen.

---

## Eigene Regeln

Die Regeln liegen in `/opt/schulmonitoring/stack/prometheus/rules/`:

| Datei | Inhalt |
|---|---|
| `00-aggregation.yml` | Vorberechnete Werte für die Dashboards |
| `10-hardware.yml` | Hardware und Vorwarnungen |
| `20-verfuegbarkeit.yml` | Erreichbarkeit |
| `30-windows.yml` | Dienste, Sicherheitshygiene, Sicherung, Hyper-V |
| `40-netzwerk.yml` | SNMP, UniFi, FortiGate |

Eine eigene Regel, zum Beispiel für den Server der Schulverwaltung:

```yaml
      - alert: SchulverwaltungNichtErreichbar
        expr: probe_success{geraet="srv-verwaltung"} == 0
        for: 3m
        labels: { severity: critical, kategorie: verfuegbarkeit }
        annotations:
          summary: "Der Schulverwaltungsserver antwortet nicht"
          description: "Sekretariat kann nicht arbeiten – hat Vorrang."
```

Prüfen und übernehmen:

```bash
cd /opt/schulmonitoring/stack
docker exec mon-prometheus promtool check rules /etc/prometheus/rules/*.yml
curl -X POST http://localhost:9090/-/reload
```

Der `promtool`-Lauf lohnt sich immer: Prometheus lädt bei einem Fehler in
einer Regeldatei die Änderungen gar nicht erst – und meldet das nur im
Protokoll.

---

## Schwellwerte anpassen

Die Vorgaben sind für eine Schule gewählt und eher zurückhaltend. Wenn
etwas zu oft meldet, lieber den Schwellwert anheben als die Regel löschen –
sonst fällt später niemandem auf, dass da mal etwas war.

Häufig angepasst:

| Regel | Vorgabe | Wann ändern |
|---|---|---|
| `DauerhafteHoheCpuLast` | 92 % über 45 min | Bei Terminalservern eher 95 % |
| `SpeicherplatzKnapp` | unter 10 % frei | Bei sehr großen Datenträgern eher 5 % |
| `AccessPointSehrVieleClients` | mehr als 50 | Je nach AP-Modell |
| `BruteForceAnmeldeversuche` | 20 in 5 min | In Schulen mit vielen Tippfehlern eher 40 |
| `PatchstandVeraltet` | 45 Tage | Je nach Wartungsfenster |
