#!/usr/bin/env bash
# =====================================================================
# Monitoring-Agent fuer Linux-Server der Schule
#
# Installiert node_exporter, richtet ihn als Systemdienst ein und meldet
# den Server beim Monitoring an. Wiederholt ausfuehrbar.
#
# Aufruf auf dem zu ueberwachenden Server:
#   curl -fsSL -H "X-Agent-Token: <TOKEN>" \
#        http://<monitoring-server>/mon/dist/install-agent.sh | \
#     sudo bash -s -- --server http://<monitoring-server> --token <TOKEN>
# =====================================================================
set -euo pipefail

AGENT_VERSION="1.0.0"
NODE_EXPORTER_VERSION="1.8.2"
SERVER_URL=""
TOKEN=""
ROLLE="server"
RAUM=""
STANDORT="Schule"
PORT="9100"

melde()  { echo -e "\033[1;36m==> $*\033[0m"; }
erfolg() { echo -e "\033[1;32m[OK] $*\033[0m"; }
warne()  { echo -e "\033[1;33m[!]  $*\033[0m"; }
fehler() { echo -e "\033[1;31m[FEHLER] $*\033[0m" >&2; }

# ---------------------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    --server)   SERVER_URL="${2%/}"; shift 2 ;;
    --token)    TOKEN="$2"; shift 2 ;;
    --rolle)    ROLLE="$2"; shift 2 ;;
    --raum)     RAUM="$2"; shift 2 ;;
    --standort) STANDORT="$2"; shift 2 ;;
    --port)     PORT="$2"; shift 2 ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) fehler "Unbekannter Parameter: $1"; exit 1 ;;
  esac
done

[[ $EUID -eq 0 ]]     || { fehler "Bitte mit sudo ausfuehren."; exit 1; }
[[ -n "$SERVER_URL" ]] || { fehler "--server fehlt."; exit 1; }
[[ -n "$TOKEN" ]]      || { fehler "--token fehlt."; exit 1; }

# ---------------------------------------------------------------------
melde "node_exporter installieren"

BENUTZER="node_exporter"
if ! id "$BENUTZER" >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin "$BENUTZER"
fi

ARCH="$(dpkg --print-architecture 2>/dev/null || uname -m)"
case "$ARCH" in
  amd64|x86_64) ARCH="amd64" ;;
  arm64|aarch64) ARCH="arm64" ;;
  *) fehler "Nicht unterstuetzte Architektur: $ARCH"; exit 1 ;;
esac

PAKET="node_exporter-${NODE_EXPORTER_VERSION}.linux-${ARCH}"
TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT

# Erst beim Monitoring-Server anfragen, dann erst im Internet
QUELLEN=(
  "${SERVER_URL}/mon/dist/${PAKET}.tar.gz"
  "https://github.com/prometheus/node_exporter/releases/download/v${NODE_EXPORTER_VERSION}/${PAKET}.tar.gz"
)

GELADEN=0
for quelle in "${QUELLEN[@]}"; do
  if curl -fsSL -H "X-Agent-Token: ${TOKEN}" --max-time 300 -o "${TEMP}/ne.tar.gz" "$quelle"; then
    GELADEN=1; break
  fi
  warne "Nicht verfuegbar: $quelle"
done
[[ $GELADEN -eq 1 ]] || { fehler "node_exporter konnte nicht geladen werden."; exit 1; }

tar -xzf "${TEMP}/ne.tar.gz" -C "$TEMP"
install -m 0755 "${TEMP}/${PAKET}/node_exporter" /usr/local/bin/node_exporter
erfolg "node_exporter ${NODE_EXPORTER_VERSION} installiert"

# ---------------------------------------------------------------------
melde "Systemdienst einrichten"

mkdir -p /var/lib/node_exporter/textfile
chown -R "${BENUTZER}:${BENUTZER}" /var/lib/node_exporter

cat > /etc/systemd/system/node_exporter.service <<EOF
[Unit]
Description=Prometheus node_exporter (Schul-Monitoring)
After=network-online.target
Wants=network-online.target

[Service]
User=${BENUTZER}
Group=${BENUTZER}
Type=simple
ExecStart=/usr/local/bin/node_exporter \\
  --web.listen-address=:${PORT} \\
  --collector.textfile.directory=/var/lib/node_exporter/textfile \\
  --collector.systemd \\
  --collector.processes
Restart=always
RestartSec=10
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/node_exporter

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now node_exporter
erfolg "Dienst laeuft auf Port ${PORT}"

# ---------------------------------------------------------------------
melde "Firewall"

SERVER_IP="$(echo "$SERVER_URL" | sed -E 's|https?://||; s|[:/].*||')"
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then
  ufw allow from "${SERVER_IP}" to any port "${PORT}" proto tcp comment 'Schul-Monitoring' >/dev/null
  erfolg "ufw-Regel fuer ${SERVER_IP} angelegt"
else
  warne "Keine aktive ufw gefunden – Firewall bitte selbst pruefen."
fi

# ---------------------------------------------------------------------
melde "Datentraeger regelmaessig auf S.M.A.R.T.-Warnungen pruefen"

if command -v smartctl >/dev/null 2>&1; then
  cat > /usr/local/bin/schule-smart-pruefung <<'PRUEFUNG'
#!/usr/bin/env bash
# Schreibt S.M.A.R.T.-Werte fuer den textfile-Sammler des node_exporter
set -uo pipefail
ZIEL="/var/lib/node_exporter/textfile/schule_hardware.prom"
TEMP="${ZIEL}.tmp"

{
  echo "# HELP schule_datentraeger_ausfall_vorhersage 1 = S.M.A.R.T. sagt einen Ausfall voraus"
  echo "# TYPE schule_datentraeger_ausfall_vorhersage gauge"
  echo "# HELP schule_datentraeger_temperatur_celsius Temperatur des Datentraegers"
  echo "# TYPE schule_datentraeger_temperatur_celsius gauge"

  for geraet in $(lsblk -dno NAME,TYPE | awk '$2=="disk"{print $1}'); do
    ausgabe="$(smartctl -H -A -j "/dev/${geraet}" 2>/dev/null)" || continue
    modell="$(echo "$ausgabe" | grep -oP '"model_name"\s*:\s*"\K[^"]+' | head -1)"
    seriennummer="$(echo "$ausgabe" | grep -oP '"serial_number"\s*:\s*"\K[^"]+' | head -1)"
    bestanden="$(echo "$ausgabe" | grep -oP '"passed"\s*:\s*\K(true|false)' | head -1)"
    temperatur="$(echo "$ausgabe" | grep -oP '"temperature"\s*:\s*\{\s*"current"\s*:\s*\K[0-9]+' | head -1)"

    etikett="modell=\"${modell:-unbekannt}\",seriennummer=\"${seriennummer:-}\",laufwerk=\"${geraet}\""
    [[ "$bestanden" == "false" ]] && echo "schule_datentraeger_ausfall_vorhersage{${etikett}} 1" \
                                  || echo "schule_datentraeger_ausfall_vorhersage{${etikett}} 0"
    [[ -n "$temperatur" ]] && echo "schule_datentraeger_temperatur_celsius{${etikett}} ${temperatur}"
  done
} > "$TEMP"

mv "$TEMP" "$ZIEL"
PRUEFUNG
  chmod +x /usr/local/bin/schule-smart-pruefung

  cat > /etc/systemd/system/schule-smart-pruefung.service <<'EOF'
[Unit]
Description=S.M.A.R.T.-Pruefung fuer das Schul-Monitoring

[Service]
Type=oneshot
ExecStart=/usr/local/bin/schule-smart-pruefung
EOF

  cat > /etc/systemd/system/schule-smart-pruefung.timer <<'EOF'
[Unit]
Description=S.M.A.R.T.-Pruefung alle 15 Minuten

[Timer]
OnBootSec=5min
OnUnitActiveSec=15min

[Install]
WantedBy=timers.target
EOF

  systemctl daemon-reload
  systemctl enable --now schule-smart-pruefung.timer
  erfolg "S.M.A.R.T.-Pruefung eingerichtet"
else
  warne "smartctl fehlt – fuer die Ausfallvorhersage bitte 'apt install smartmontools' nachholen."
fi

# ---------------------------------------------------------------------
melde "Beim Monitoring-Server anmelden"

EIGENE_IP="$(ip -4 route get "$SERVER_IP" 2>/dev/null | grep -oP 'src \K\S+' | head -1)"
[[ -n "$EIGENE_IP" ]] || EIGENE_IP="$(hostname -I | awk '{print $1}')"

KOERPER=$(cat <<EOF
{
  "hostname": "$(hostname -s)",
  "ip": "${EIGENE_IP}",
  "rolle": "${ROLLE}",
  "plattform": "linux",
  "betriebssystem": "$(. /etc/os-release && echo "$PRETTY_NAME")",
  "raum": "${RAUM}",
  "standort": "${STANDORT}",
  "agent_version": "${AGENT_VERSION}",
  "exporter_port": ${PORT}
}
EOF
)

for versuch in 1 2 3 4 5; do
  if curl -fsS -X POST "${SERVER_URL}/mon/api/v1/register" \
       -H "X-Agent-Token: ${TOKEN}" -H "Content-Type: application/json" \
       -d "$KOERPER" --max-time 30 >/dev/null; then
    erfolg "Angemeldet – der Server erscheint gleich im Dashboard"
    break
  fi
  warne "Anmeldung fehlgeschlagen (Versuch ${versuch}/5)"
  [[ $versuch -lt 5 ]] && sleep $((versuch * 4))
done

echo
erfolg "Fertig. Dashboard: ${SERVER_URL}/"
