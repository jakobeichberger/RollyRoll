# Betrieb

---

## Die wichtigsten Befehle

```bash
ssh monadmin@10.0.0.50
cd /opt/schulmonitoring/stack

docker compose ps                    # Was läuft?
docker compose logs -f grafana       # Protokoll eines Dienstes
sudo systemctl restart schulmonitoring   # Alles neu starten
docker stats --no-stream             # Ressourcenverbrauch
```

Nach Änderungen an der `.env`:

```bash
sudo ./bootstrap.sh
```

Das erzeugt die abgeleiteten Konfigurationsdateien neu (Alertmanager, SNMP,
Aufbewahrungszeiten) und startet die Container. Es ist wiederholbar –
vorhandene Daten und bereits erzeugte Kennwörter bleiben erhalten.

Nach Änderungen an Prometheus-Regeln genügt ein Nachladen:

```bash
docker exec mon-prometheus promtool check rules /etc/prometheus/rules/*.yml
curl -X POST http://localhost:9090/-/reload
```

Änderungen an `inventar/inventar.yml` brauchen gar nichts – sie greifen
binnen 30 Sekunden.

---

## Datensicherung

Zu sichern sind zwei Dinge: die Konfiguration (klein, wichtig) und die
Messdaten (groß, ersetzbar).

### Konfiguration – das Wesentliche

```bash
sudo tar -czf /root/monitoring-konfiguration-$(date +%F).tar.gz \
  -C /opt/schulmonitoring stack/.env stack/inventar stack/prometheus/rules \
     stack/loki/rules stack/grafana/dashboards
```

Diese Datei enthält Kennwörter und gehört an einen sicheren Ort. Damit
lässt sich das Monitoring jederzeit neu aufsetzen.

### Vollständig – inklusive aller Messwerte und Protokolle

```bash
cd /opt/schulmonitoring/stack
docker compose stop
sudo tar -czf /root/monitoring-vollstaendig-$(date +%F).tar.gz \
  /var/lib/docker/volumes/schulmonitoring_prometheus-data \
  /var/lib/docker/volumes/schulmonitoring_loki-data \
  /var/lib/docker/volumes/schulmonitoring_grafana-data
docker compose start
```

Am einfachsten ist allerdings ein Hyper-V-Prüfpunkt oder die Aufnahme der
VM in die bestehende Sicherung der Schule. Wichtig ist nur: **Die VM darf
nicht das einzige sein, was die Datensicherung überwacht und gleichzeitig
selbst ungesichert ist.**

---

## Updates

### Container

```bash
cd /opt/schulmonitoring/stack
docker compose pull
docker compose up -d
docker image prune -f
```

Die Versionen stehen in der `.env`. Wer auf einer bestimmten Fassung
bleiben will, ändert dort den Tag – `docker compose pull` holt dann genau
diesen.

### Betriebssystem der VM

Sicherheitsupdates installiert die VM über `unattended-upgrades` selbst.
Größere Sprünge von Hand:

```bash
sudo apt update && sudo apt full-upgrade
sudo reboot
```

Die Container starten nach dem Neustart automatisch wieder.

### Agenten

1. Neue Agent-Version in `agent/Install-MonitoringAgent.ps1` eintragen
   (Konstante `$AgentVersion`).
2. Skripte nach SYSVOL kopieren.
3. Auf dem Monitoring-Server `sudo ./stack/pakete-holen.sh` ausführen,
   damit die aktuellen Pakete bereitliegen.

Beim nächsten Neustart erkennt jedes Gerät die neue Version und
aktualisiert sich selbst. Das Dashboard zeigt unter „Agenten mit veralteter
Version", wie viele noch fehlen.

---

## Kapazität im Blick behalten

```bash
# Wie viele Zeitreihen gibt es?
curl -s 'http://localhost:9090/api/v1/query?query=prometheus_tsdb_head_series' |
  python3 -c "import sys,json; print(json.load(sys.stdin)['data']['result'][0]['value'][1])"

# Belegter Platz
docker system df -v | grep -E "prometheus-data|loki-data"
df -h /
```

Faustregel: Ein Windows-Client erzeugt rund 700 Zeitreihen, ein Server mit
allen Rollen rund 2500. Wird es eng, gibt es drei Stellschrauben in der
`.env`:

| Einstellung | Wirkung |
|---|---|
| `METRIK_AUFBEWAHRUNG` | Wie lange Messwerte bleiben (Vorgabe 365 Tage) |
| `METRIK_MAX_GROESSE` | Harte Obergrenze – Prometheus räumt selbst auf |
| `LOG_AUFBEWAHRUNG` | Wie lange Protokolle bleiben (Vorgabe 90 Tage) |

Bei den Protokollen lässt sich außerdem an der Menge drehen: In
`agent/alloy-client.alloy.tmpl` steht, welche Ereignisse überhaupt
eingesammelt werden. Weniger Ereignisnummern im `xpath_query` bedeuten
weniger Daten – zulasten der Erkennung.

Die Monitoring-VM überwacht sich selbst: Wird der Platz knapp, kommt die
Warnung `SpeicherplatzMonitoringServer`, bevor etwas verloren geht.

---

## Wenn ein Gerät ausgemustert wird

Nichts tun. Nach `AGENT_TIMEOUT_TAGE` (Vorgabe 30) verschwindet es von
selbst aus allen Zielen und Dashboards.

Sofort entfernen:

```bash
sudo docker exec mon-registrar python3 -c "
import json, pathlib
p = pathlib.Path('/state/agents.json')
d = json.loads(p.read_text())
d.pop('pc-edv-12', None)
p.write_text(json.dumps(d, indent=2))
"
docker restart mon-registrar
```

---

## Grafana

**Weitere Benutzer** – Zahnrad → Administration → Users. Für Kolleginnen
und Kollegen, die nur schauen sollen, reicht die Rolle *Viewer*.

**Eigene Dashboards** – lassen sich in der Oberfläche anlegen und
speichern. Sie überstehen einen Neustart, weil sie in der Datenbank
liegen. Die mitgelieferten fünf Dashboards kommen dagegen aus Dateien und
werden bei jedem Start neu eingelesen: Direkte Änderungen daran gehen
verloren. Wer eines anpassen will, speichert es unter neuem Namen ab oder
ändert die JSON-Datei in `stack/grafana/dashboards/`.

**Kennwort ändern** – über die Oberfläche. Danach auch
`GRAFANA_ADMIN_PASSWORT` in der `.env` anpassen, damit beides wieder
zusammenpasst.

---

## Regelmäßiger Blick

Was sich als Routine bewährt:

**Täglich, halbe Minute** – Gesamtübersicht öffnen. Sind die Kacheln
„Kritische Alarme" und „Vorwarnungen" grün, ist alles in Ordnung.

**Wöchentlich** – Dashboard „Hardware & Vorwarnungen". Hier steht, was in
den nächsten Wochen zu bestellen oder zu tauschen ist. Und: aktive
Stummschaltungen kontrollieren.

**Monatlich** – Dashboard „Sicherheit": Geräte mit Schutzlücken,
Patchstand. Dazu die Zahl der Agenten mit dem tatsächlichen Gerätebestand
abgleichen – fehlt etwas, greift die GPO auf diesen Geräten nicht.
