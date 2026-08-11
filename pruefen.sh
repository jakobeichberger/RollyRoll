#!/usr/bin/env bash
# =====================================================================
# Prueft das gesamte Projekt.
#
#   ./pruefen.sh
#
# Laeuft ohne laufenden Stack und ohne Docker. promtool und amtool
# werden bei Bedarf einmalig geladen (dieselben Fassungen, die auch im
# Stack laufen) und unter .werkzeuge/ abgelegt.
#
# Rueckgabewert 0 = alles in Ordnung. Damit laesst sich das Skript
# unveraendert als Vorbedingung fuer einen Commit oder in einer
# Bauleitung verwenden.
# =====================================================================
set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

WERKZEUGE="${WERKZEUGE:-.werkzeuge}"
PROMETHEUS_FASSUNG="3.1.0"
ALERTMANAGER_FASSUNG="0.28.0"

ROT=$'\033[0;31m'; GRUEN=$'\033[0;32m'; GELB=$'\033[0;33m'; BLAU=$'\033[0;36m'; AUS=$'\033[0m'

BESTANDEN=0
FEHLGESCHLAGEN=0
UEBERSPRUNGEN=0
declare -a FEHLERLISTE=()

abschnitt() { printf "\n%s== %s ==%s\n" "$BLAU" "$1" "$AUS"; }

pruefe() {
  local name="$1"; shift
  local ausgabe
  if ausgabe="$("$@" 2>&1)"; then
    printf "  %s✓%s %s\n" "$GRUEN" "$AUS" "$name"
    BESTANDEN=$((BESTANDEN + 1))
  else
    printf "  %s✗%s %s\n" "$ROT" "$AUS" "$name"
    printf "%s\n" "$ausgabe" | sed 's/^/      /' | head -25
    FEHLGESCHLAGEN=$((FEHLGESCHLAGEN + 1))
    FEHLERLISTE+=("$name")
  fi
}

ueberspringe() {
  printf "  %s–%s %s (%s)\n" "$GELB" "$AUS" "$1" "$2"
  UEBERSPRUNGEN=$((UEBERSPRUNGEN + 1))
}

# ---------------------------------------------------------------------
# Werkzeuge besorgen
# ---------------------------------------------------------------------
werkzeug_holen() {
  local name="$1" projekt="$2" fassung="$3"
  if [[ -x "$WERKZEUGE/$name" ]]; then return 0; fi
  command -v "$name" >/dev/null 2>&1 && { ln -sf "$(command -v "$name")" "$WERKZEUGE/$name"; return 0; }

  local arch; arch="$(uname -m)"
  case "$arch" in x86_64) arch=amd64 ;; aarch64|arm64) arch=arm64 ;; esac
  local paket="${projekt}-${fassung}.linux-${arch}.tar.gz"
  local url="https://github.com/prometheus/${projekt}/releases/download/v${fassung}/${paket}"

  echo "  Lade $name $fassung ..."
  mkdir -p "$WERKZEUGE"
  if curl -sSL --max-time 300 -o "$WERKZEUGE/$paket" "$url" 2>/dev/null &&
     tar xzf "$WERKZEUGE/$paket" -C "$WERKZEUGE" 2>/dev/null; then
    find "$WERKZEUGE" -maxdepth 2 -name "$name" -type f -exec cp {} "$WERKZEUGE/$name" \; 2>/dev/null
    rm -rf "$WERKZEUGE/${projekt}-${fassung}.linux-${arch}" "$WERKZEUGE/$paket"
  fi
  [[ -x "$WERKZEUGE/$name" ]]
}

mkdir -p "$WERKZEUGE"
werkzeug_holen promtool prometheus "$PROMETHEUS_FASSUNG" || true
werkzeug_holen amtool alertmanager "$ALERTMANAGER_FASSUNG" || true
PROMTOOL="$WERKZEUGE/promtool"
AMTOOL="$WERKZEUGE/amtool"

# ---------------------------------------------------------------------
abschnitt "Syntax"

pruefe "PowerShell-Skripte: keine PS7-Syntax, keine automatischen Variablen" \
  python3 werkzeuge/powershell-pruefung.py

for f in stack/bootstrap.sh stack/pakete-holen.sh stack/konfig-sichern.sh \
         agent/linux/install-agent.sh pruefen.sh; do
  pruefe "$f" bash -n "$f"
done

pruefe "Python: registrar" python3 -m py_compile stack/registrar/app.py stack/registrar/erkennung.py
pruefe "YAML und JSON durchgehend lesbar" python3 werkzeuge/strukturpruefung.py

# ---------------------------------------------------------------------
abschnitt "Prometheus"

if [[ -x "$PROMTOOL" ]]; then
  pruefe "Alarm- und Aufzeichnungsregeln (promtool check rules)" \
    "$PROMTOOL" check rules stack/prometheus/rules/*.yml
  pruefe "Regeltests mit erfundenen Messwerten (promtool test rules)" \
    "$PROMTOOL" test rules stack/prometheus/tests/alarmregeln_test.yml
  pruefe "Dashboard-Abfragen sind gueltiges PromQL" \
    python3 stack/grafana/abfragen-pruefung.py "$PROMTOOL"
else
  ueberspringe "promtool-Pruefungen" "promtool nicht verfuegbar"
fi

pruefe "Meldungstexte benutzen nur vorhandene Labels" \
  python3 stack/prometheus/tests/labels-pruefung.py

# ---------------------------------------------------------------------
abschnitt "Alertmanager"

if [[ -x "$AMTOOL" ]]; then
  pruefe "Konfiguration und Empfaenger (amtool check-config)" \
    python3 werkzeuge/alertmanager-pruefung.py "$AMTOOL"
else
  ueberspringe "amtool-Pruefung" "amtool nicht verfuegbar"
fi

# ---------------------------------------------------------------------
abschnitt "Grafana"

pruefe "Dashboards gegen die Hausregeln" python3 stack/grafana/dashboard-pruefung.py

# ---------------------------------------------------------------------
abschnitt "Geraeteerkennung"

pruefe "SNMP-Kodierung, Einordnung, Suchlauf, Netzplan" \
  bash -c 'cd stack/registrar && python3 test_erkennung.py'

# ---------------------------------------------------------------------
abschnitt "Zusammenhalt"

pruefe "Jede benutzte Metrik wird auch erzeugt" python3 werkzeuge/metrikpruefung.py
pruefe "Keine toten Verweise in der Dokumentation" python3 werkzeuge/dokupruefung.py

# ---------------------------------------------------------------------
printf "\n%s%s%s\n" "$BLAU" "$(printf '=%.0s' {1..60})" "$AUS"
printf "  %s%d bestanden%s" "$GRUEN" "$BESTANDEN" "$AUS"
[[ $UEBERSPRUNGEN -gt 0 ]] && printf ", %s%d uebersprungen%s" "$GELB" "$UEBERSPRUNGEN" "$AUS"
if [[ $FEHLGESCHLAGEN -gt 0 ]]; then
  printf ", %s%d fehlgeschlagen%s\n" "$ROT" "$FEHLGESCHLAGEN" "$AUS"
  printf "\nFehlgeschlagen:\n"
  for f in "${FEHLERLISTE[@]}"; do printf "  - %s\n" "$f"; done
  echo
  exit 1
fi
printf "\n\n"
exit 0
