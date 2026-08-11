#!/usr/bin/env bash
# =====================================================================
# Schul-Monitoring – Einrichtung innerhalb der VM
#
# Wird von cloud-init beim ersten Start automatisch aufgerufen. Man kann
# es aber jederzeit von Hand nachtraeglich ausfuehren – es ist bewusst
# wiederholbar gebaut (idempotent).
#
#   sudo /opt/schulmonitoring/stack/bootstrap.sh
# =====================================================================
set -euo pipefail

STACK_VERZEICHNIS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$STACK_VERZEICHNIS"

PROTOKOLL="/var/log/schulmonitoring-bootstrap.log"
exec > >(tee -a "$PROTOKOLL") 2>&1

echo "======================================================================"
echo " Schul-Monitoring wird eingerichtet – $(date '+%d.%m.%Y %H:%M:%S')"
echo " Verzeichnis: $STACK_VERZEICHNIS"
echo "======================================================================"

melde()  { echo -e "\n\033[1;36m==> $*\033[0m"; }
warne()  { echo -e "\033[1;33m[!] $*\033[0m"; }
fehler() { echo -e "\033[1;31m[FEHLER] $*\033[0m" >&2; }

# ---------------------------------------------------------------------
# 1. Voraussetzungen
# ---------------------------------------------------------------------
melde "Voraussetzungen pruefen"

if [[ $EUID -ne 0 ]]; then
  fehler "Bitte mit sudo ausfuehren."
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  melde "Docker wird nachinstalliert"
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq ca-certificates curl gnupg
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
    | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  chmod a+r /etc/apt/keyrings/docker.gpg
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -qq
  apt-get install -y -qq docker-ce docker-ce-cli containerd.io \
    docker-buildx-plugin docker-compose-plugin
  systemctl enable --now docker
fi

docker compose version >/dev/null 2>&1 || {
  fehler "docker compose steht nicht zur Verfuegung."
  exit 1
}

# ---------------------------------------------------------------------
# 2. Konfiguration vorbereiten
# ---------------------------------------------------------------------
melde "Konfiguration vorbereiten"

if [[ ! -f .env ]]; then
  cp .env.example .env
  echo "  .env aus der Vorlage erzeugt"
fi

# Kleine Helfer zum Lesen/Schreiben einzelner Werte in der .env
wert() { grep -E "^${1}=" .env | head -1 | cut -d= -f2- || true; }
setze() {
  local schluessel="$1" wert="$2"
  if grep -qE "^${schluessel}=" .env; then
    # | als Trenner, damit Passwoerter mit / kein Problem sind
    sed -i "s|^${schluessel}=.*|${schluessel}=${wert}|" .env
  else
    echo "${schluessel}=${wert}" >> .env
  fi
}

geheimnis() { tr -dc 'A-Za-z0-9' </dev/urandom | head -c "${1:-32}"; }

# --- Zugangsdaten erzeugen, falls noch keine da sind ------------------
if [[ -z "$(wert GRAFANA_ADMIN_PASSWORT)" ]]; then
  setze GRAFANA_ADMIN_PASSWORT "$(geheimnis 24)"
  echo "  Grafana-Kennwort erzeugt"
fi

if [[ -z "$(wert AGENT_TOKEN)" ]]; then
  setze AGENT_TOKEN "$(geheimnis 48)"
  echo "  Agent-Token erzeugt"
fi

# Hostname/IP eintragen, falls noch der Platzhalter drinsteht
if [[ "$(wert MON_HOSTNAME)" == "monitoring.schule.local" ]]; then
  EIGENE_IP="$(hostname -I | awk '{print $1}')"
  [[ -n "$EIGENE_IP" ]] && setze MON_HOSTNAME "$EIGENE_IP" && \
    echo "  MON_HOSTNAME auf $EIGENE_IP gesetzt"
fi

# Suchbereich der Geraeteerkennung: ohne Angabe das eigene Netz nehmen.
# Wer die VM von Hand aufsetzt, soll nicht erst einen Wert nachschlagen
# muessen, um Switches und Drucker im Dashboard zu sehen.
if ! grep -qE "^ERKENNUNG_NETZE=." .env; then
  EIGENES_NETZ="$(ip -o -4 addr show scope global 2>/dev/null |
    awk '{print $4}' | head -1 |
    python3 -c 'import sys,ipaddress
roh = sys.stdin.read().strip()
if roh:
    netz = ipaddress.ip_network(roh, strict=False)
    # Zu weite Netze sind fuer einen Suchlauf nicht sinnvoll.
    print(netz if netz.prefixlen >= 22 else "")' 2>/dev/null)"

  if [[ -n "$EIGENES_NETZ" ]]; then
    setze ERKENNUNG_NETZE "$EIGENES_NETZ"
    echo "  Geraeteerkennung durchsucht das eigene Netz: $EIGENES_NETZ"
  else
    warne "Suchbereich der Geraeteerkennung liess sich nicht bestimmen."
    warne "Bei Bedarf in .env nachtragen:  ERKENNUNG_NETZE=10.0.0.0/24"
  fi
fi

set -a
# shellcheck disable=SC1091
source .env
set +a

# ---------------------------------------------------------------------
# 3. Erzeugte Konfigurationsdateien schreiben
# ---------------------------------------------------------------------
melde "Konfigurationsdateien erzeugen"

# --- Basic-Auth-Hash fuer die Log-Annahme ----------------------------
if [[ -z "${LOKI_BASICAUTH_HASH:-}" ]]; then
  echo "  Kennwort-Hash fuer die Log-Annahme wird berechnet"
  HASH="$(docker run --rm "${CADDY_IMAGE}" caddy hash-password --plaintext "${AGENT_TOKEN}" 2>/dev/null | tr -d '\r\n')"
  if [[ -z "$HASH" ]]; then
    fehler "Hash konnte nicht berechnet werden."
    exit 1
  fi
  # $ in der .env verdoppeln, sonst frisst Compose die Zeichen
  setze LOKI_BASICAUTH_HASH "$(echo "$HASH" | sed 's/\$/\$\$/g')"
  set -a; source .env; set +a
fi

# --- Alertmanager ----------------------------------------------------
python3 - <<'PYTHON'
import os, re, pathlib

vorlage = pathlib.Path("alertmanager/alertmanager.yml.tmpl").read_text(encoding="utf-8")

def platzhalter(treffer):
    return os.environ.get(treffer.group(1), "")

text = re.sub(r"\$\{(\w+)\}", platzhalter, vorlage)

# Webhook nur einbauen, wenn eine URL hinterlegt ist
webhook = os.environ.get("WEBHOOK_URL", "").strip()
if webhook:
    block = (
        "    webhook_configs:\n"
        f"      - url: {webhook}\n"
        "        send_resolved: true\n"
        "        max_alerts: 20\n"
    )
    text = text.replace("#WEBHOOK#\n", block)
else:
    text = text.replace("#WEBHOOK#\n", "")

pathlib.Path("alertmanager/alertmanager.yml").write_text(text, encoding="utf-8")
print("  alertmanager.yml erzeugt")
PYTHON

# --- SNMP ------------------------------------------------------------
sed "s|\${SNMP_COMMUNITY}|${SNMP_COMMUNITY:-public}|g" \
  snmp/snmp-schule.yml.tmpl > snmp/snmp-schule.yml
echo "  snmp-schule.yml erzeugt"

# --- Aufbewahrungszeit der Protokolle --------------------------------
sed -i "s|^  retention_period: .*|  retention_period: ${LOG_AUFBEWAHRUNG:-90d}|" \
  loki/loki-config.yml
echo "  Log-Aufbewahrung auf ${LOG_AUFBEWAHRUNG:-90d} gesetzt"

# --- FortiGate-Zugangsdaten ------------------------------------------
mkdir -p fortigate
if [[ -n "${FORTIGATE_TOKEN:-}" ]]; then
  cat > fortigate/fortigate-key.yaml <<EOF
"${FORTIGATE_URL}":
  token: "${FORTIGATE_TOKEN}"
EOF
  chmod 600 fortigate/fortigate-key.yaml
  echo "  FortiGate-API-Zugang hinterlegt"
else
  echo "---" > fortigate/fortigate-key.yaml
  warne "Kein FORTIGATE_TOKEN gesetzt – die FortiGate wird vorerst nur ueber SNMP und Syslog erfasst."
fi

# ---------------------------------------------------------------------
# 4. Verzeichnisse und Rechte
# ---------------------------------------------------------------------
melde "Verzeichnisse anlegen"

mkdir -p prometheus/targets dist
# Der Registrierungs-Dienst laeuft als Benutzer 10200 und muss die
# Ziellisten schreiben duerfen; Prometheus (65534) liest sie nur.
chown -R 10200:10200 prometheus/targets
chmod 755 prometheus/targets

# Leere Ziellisten anlegen, damit Prometheus beim ersten Start nicht meckert
for datei in windows-agenten linux-agenten icmp-agenten icmp-inventar \
             snmp-inventar dienst-inventar fortigate-inventar; do
  [[ -f "prometheus/targets/${datei}.json" ]] || echo "[]" > "prometheus/targets/${datei}.json"
done
chown -R 10200:10200 prometheus/targets

# ---------------------------------------------------------------------
# 5. Agent-Pakete bereitstellen
# ---------------------------------------------------------------------
melde "Agent-Pakete bereitstellen"
"${STACK_VERZEICHNIS}/pakete-holen.sh" || \
  warne "Agent-Pakete konnten nicht geladen werden – die Clients holen sie dann direkt aus dem Internet."

# ---------------------------------------------------------------------
# 6. Profile bestimmen und starten
# ---------------------------------------------------------------------
melde "Container starten"

PROFILE=()
[[ -n "${UNIFI_PASSWORT:-}" ]] && PROFILE+=(--profile unifi)
[[ -n "${FORTIGATE_TOKEN:-}" ]] && PROFILE+=(--profile fortigate)

if [[ ${#PROFILE[@]} -eq 0 ]]; then
  warne "Weder UniFi- noch FortiGate-Zugangsdaten hinterlegt."
  warne "Beides laesst sich jederzeit in .env nachtragen; danach:"
  warne "  sudo systemctl restart schulmonitoring"
fi

docker compose "${PROFILE[@]}" pull --ignore-pull-failures || \
  warne "Nicht alle Images konnten geladen werden – Details siehe oben."
docker compose "${PROFILE[@]}" build --pull registrar
docker compose "${PROFILE[@]}" up -d --remove-orphans

# ---------------------------------------------------------------------
# 7. Als Systemdienst verankern
# ---------------------------------------------------------------------
melde "Systemdienst einrichten"

cat > /etc/systemd/system/schulmonitoring.service <<EOF
[Unit]
Description=Schul-Monitoring (Docker Compose)
Requires=docker.service
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=${STACK_VERZEICHNIS}
ExecStart=/usr/bin/docker compose ${PROFILE[*]} up -d --remove-orphans
ExecStop=/usr/bin/docker compose ${PROFILE[*]} down
TimeoutStartSec=900

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable schulmonitoring.service >/dev/null 2>&1

# --- Naechtliche Konfigurationssicherung der Netzgeraete --------------
melde "Konfigurationssicherung einrichten"

mkdir -p /opt/schulmonitoring/textfile /opt/schulmonitoring/konfig
chmod 700 /opt/schulmonitoring/konfig
chmod +x "${STACK_VERZEICHNIS}/konfig-sichern.sh"

cat > /etc/systemd/system/schulmonitoring-konfig.service <<EOF
[Unit]
Description=Konfiguration der Netzgeraete sichern
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
WorkingDirectory=${STACK_VERZEICHNIS}
ExecStart=${STACK_VERZEICHNIS}/konfig-sichern.sh
TimeoutStartSec=600
EOF

cat > /etc/systemd/system/schulmonitoring-konfig.timer <<'EOF'
[Unit]
Description=Konfigurationssicherung der Netzgeraete

[Timer]
# Stuendlich, nicht naechtlich. Der Grund ist nicht die Sicherung selbst –
# dafuer waere einmal taeglich genug – sondern die Aussage "wann wurde
# geaendert". Bei einem naechtlichen Lauf wird JEDE Aenderung um 03:20
# erkannt, auch eine vom Vortag um 14:00. Damit liesse sich eine
# Aenderung ausserhalb der Schulzeit gar nicht von einer normalen
# unterscheiden. Stuendlich stimmt der Erkennungszeitpunkt auf eine
# Stunde genau mit dem Aenderungszeitpunkt ueberein.
OnCalendar=hourly
# Nach einem Ausfall einmal nachholen, statt die Luecke zu lassen
Persistent=true
RandomizedDelaySec=300

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now schulmonitoring-konfig.timer >/dev/null 2>&1

# Einmal sofort laufen lassen, damit gleich ein Ausgangsstand daliegt –
# ohne den waere der erste Vergleich erst in der uebernaechsten Nacht.
if [[ -n "${FORTIGATE_TOKEN:-}${UNIFI_PASSWORT:-}" ]]; then
  systemctl start schulmonitoring-konfig.service >/dev/null 2>&1 || \
    warne "Erste Konfigurationssicherung fehlgeschlagen – Details mit: journalctl -u schulmonitoring-konfig"
else
  echo "  Noch keine Zugangsdaten fuer FortiGate/UniFi – die Sicherung laeuft ab der ersten Nacht nach dem Eintrag."
fi

# ---------------------------------------------------------------------
# 8. Warten, bis alles antwortet
# ---------------------------------------------------------------------
melde "Auf die Dienste warten"

warte_auf() {
  local name="$1" url="$2" versuche="${3:-60}"
  printf "  %-16s" "$name"
  for ((i = 0; i < versuche; i++)); do
    if curl -fsS --max-time 3 "$url" >/dev/null 2>&1; then
      echo "bereit"
      return 0
    fi
    sleep 3
    printf "."
  done
  echo " keine Antwort"
  return 1
}

warte_auf "Prometheus" "http://127.0.0.1:9090/-/ready"          || true
warte_auf "Grafana"    "http://127.0.0.1/api/health"            || true
warte_auf "Registrar"  "http://127.0.0.1/mon/api/v1/health"     || true
warte_auf "Alertmanager" "http://127.0.0.1:9093/-/ready"        || true

# ---------------------------------------------------------------------
# 9. Zusammenfassung
# ---------------------------------------------------------------------
IP="$(hostname -I | awk '{print $1}')"

cat > /root/ZUGANGSDATEN.txt <<EOF
======================================================================
 Schul-Monitoring – Zugangsdaten
 Eingerichtet am $(date '+%d.%m.%Y %H:%M:%S')
======================================================================

 Dashboard:      http://${IP}/
 Benutzer:       ${GRAFANA_ADMIN_USER:-admin}
 Kennwort:       ${GRAFANA_ADMIN_PASSWORT}

 Agent-Token:    ${AGENT_TOKEN}
   (wird im GPO-Skript als -Token uebergeben)

 Prometheus:     http://${IP}:9090/   (nur lokal, ueber SSH-Tunnel)
 Alertmanager:   http://${IP}:9093/   (nur lokal, ueber SSH-Tunnel)

 Rollout auf den Windows-Geraeten:
   \\\\<Domaene>\\SYSVOL\\...\\Install-MonitoringAgent.ps1 \\
       -ServerUrl "http://${IP}" -Token "${AGENT_TOKEN}"

 Konfiguration:  ${STACK_VERZEICHNIS}/.env
 Geraeteliste:   ${STACK_VERZEICHNIS}/inventar/inventar.yml
 Protokoll:      ${PROTOKOLL}

 Diese Datei enthaelt Geheimnisse – bitte sicher verwahren und danach
 loeschen.
======================================================================
EOF
chmod 600 /root/ZUGANGSDATEN.txt

echo
echo "======================================================================"
echo " Fertig."
echo
echo "   Dashboard:  http://${IP}/"
echo "   Benutzer:   ${GRAFANA_ADMIN_USER:-admin}"
echo "   Kennwort:   ${GRAFANA_ADMIN_PASSWORT}"
echo
echo "   Agent-Token fuer den GPO-Rollout:"
echo "   ${AGENT_TOKEN}"
echo
echo " Alles noch einmal nachzulesen in /root/ZUGANGSDATEN.txt"
echo "======================================================================"
