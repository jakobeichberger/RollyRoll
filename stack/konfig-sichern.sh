#!/usr/bin/env bash
# =====================================================================
# Konfigurationssicherung der Netzgeraete
#
# Holt nachts die Konfiguration von FortiGate und UniFi-Controller und
# legt sie in einem Git-Verzeichnis ab. Das bringt zweierlei:
#
#   Wiederherstellung – stirbt die FortiGate, steht der letzte Stand
#   bereit, statt Regeln aus dem Gedaechtnis nachzubauen.
#
#   Sicherheit – jede Aenderung wird sichtbar. Kommt nachts um drei eine
#   Firewall-Regel dazu, die niemand angelegt hat, ist das ein Befund.
#
# Beim UniFi-Controller wird bewusst NICHT die .unf-Sicherung geholt:
# Die ist ein undurchsichtiges Archiv, das sich nicht vergleichen laesst.
# Stattdessen werden die Einstellungen als JSON abgerufen – Netze, WLANs,
# Firewallregeln, Portprofile. Das ist lesbar und zeigt im Vergleich
# genau, was sich geaendert hat.
#
# Aufruf ueber den systemd-Zeitgeber schulmonitoring-konfig.timer,
# eingerichtet von bootstrap.sh. Von Hand:
#   sudo /opt/schulmonitoring/stack/konfig-sichern.sh
# =====================================================================
set -uo pipefail

STACK_VERZEICHNIS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ABLAGE="${KONFIG_ABLAGE:-/opt/schulmonitoring/konfig}"
TEXTDATEI_ORDNER="${TEXTDATEI_ORDNER:-/opt/schulmonitoring/textfile}"
METRIKDATEI="${TEXTDATEI_ORDNER}/konfigsicherung.prom"

cd "$STACK_VERZEICHNIS"

if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi

melde()  { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"; }
warne()  { echo "[$(date '+%Y-%m-%d %H:%M:%S')] [!] $*" >&2; }

# Sammelt Metrikzeilen und schreibt sie am Ende in einem Rutsch.
METRIKEN=()
metrik() { METRIKEN+=("$1"); }

jetzt() { date +%s; }

# ---------------------------------------------------------------------
# Ablage vorbereiten
# ---------------------------------------------------------------------
mkdir -p "$ABLAGE" "$TEXTDATEI_ORDNER"

if ! command -v git >/dev/null 2>&1; then
  warne "git ist nicht installiert – die Versionsgeschichte entfaellt."
  warne "Nachinstallieren mit:  sudo apt-get install -y git"
  GIT_DA=0
else
  GIT_DA=1
  if [[ ! -d "$ABLAGE/.git" ]]; then
    git -C "$ABLAGE" init -q
    git -C "$ABLAGE" config user.email "monitoring@schule.local"
    git -C "$ABLAGE" config user.name  "Schul-Monitoring"
    # Die Konfigurationen enthalten Geheimnisse – niemals nach aussen.
    git -C "$ABLAGE" config receive.denyCurrentBranch refuse
    melde "Ablage $ABLAGE als Git-Verzeichnis angelegt"
  fi
fi

# ---------------------------------------------------------------------
# FortiGate
# ---------------------------------------------------------------------
sichere_fortigate() {
  if [[ -z "${FORTIGATE_TOKEN:-}" || -z "${FORTIGATE_URL:-}" ]]; then
    melde "FortiGate: kein Token hinterlegt, wird uebersprungen"
    return 0
  fi

  local ziel="${ABLAGE}/fortigate.conf"
  local temp="${ziel}.neu"
  local url="${FORTIGATE_URL%/}/api/v2/monitor/system/config/backup?scope=global"

  local kurven=(--silent --show-error --fail --max-time 120
                -H "Authorization: Bearer ${FORTIGATE_TOKEN}")
  [[ "${FORTIGATE_INSECURE:-true}" == "true" ]] && kurven+=(--insecure)

  if ! curl "${kurven[@]}" -o "$temp" "$url" 2>/dev/null; then
    warne "FortiGate: Abruf fehlgeschlagen"
    rm -f "$temp"
    metrik "schule_konfig_sicherung_erfolgreich{geraet=\"fortigate\"} 0"
    return 1
  fi

  # Eine gueltige FortiGate-Konfiguration beginnt mit "#config-version".
  if ! head -c 200 "$temp" | grep -q "config-version"; then
    warne "FortiGate: Antwort sieht nicht nach einer Konfiguration aus"
    rm -f "$temp"
    metrik "schule_konfig_sicherung_erfolgreich{geraet=\"fortigate\"} 0"
    return 1
  fi

  # Die Kopfzeile enthaelt einen Zeitstempel und aendert sich bei jedem
  # Abruf. Ohne Herausfiltern meldete die Sicherung jede Nacht eine
  # "Aenderung", und nach zwei Wochen schaut niemand mehr hin.
  sed -i '/^#conf_file_ver=/d' "$temp"

  mv "$temp" "$ziel"
  metrik "schule_konfig_sicherung_erfolgreich{geraet=\"fortigate\"} 1"
  metrik "schule_konfig_groesse_bytes{geraet=\"fortigate\"} $(stat -c%s "$ziel")"
  melde "FortiGate: Konfiguration gesichert ($(stat -c%s "$ziel") Byte)"
}

# ---------------------------------------------------------------------
# UniFi-Controller
# ---------------------------------------------------------------------
sichere_unifi() {
  if [[ -z "${UNIFI_PASSWORT:-}" || -z "${UNIFI_URL:-}" ]]; then
    melde "UniFi: keine Zugangsdaten hinterlegt, wird uebersprungen"
    return 0
  fi

  local basis="${UNIFI_URL%/}"
  local kekse
  kekse="$(mktemp)"
  local kurven=(--silent --show-error --fail --max-time 60 --insecure
                -c "$kekse" -b "$kekse"
                -H "Content-Type: application/json")

  # UniFi OS (Cloud Key, Dream Machine) meldet anders an als der
  # klassische Controller. Erst das eine probieren, dann das andere.
  local praefix=""
  if curl "${kurven[@]}" -X POST -o /dev/null \
       -d "{\"username\":\"${UNIFI_BENUTZER}\",\"password\":\"${UNIFI_PASSWORT}\"}" \
       "${basis}/api/auth/login" 2>/dev/null; then
    praefix="/proxy/network"
  elif curl "${kurven[@]}" -X POST -o /dev/null \
       -d "{\"username\":\"${UNIFI_BENUTZER}\",\"password\":\"${UNIFI_PASSWORT}\"}" \
       "${basis}/api/login" 2>/dev/null; then
    praefix=""
  else
    warne "UniFi: Anmeldung fehlgeschlagen"
    rm -f "$kekse"
    metrik "schule_konfig_sicherung_erfolgreich{geraet=\"unifi\"} 0"
    return 1
  fi

  local standort="${UNIFI_SITES:-default}"
  [[ "$standort" == "all" || -z "$standort" ]] && standort="default"
  standort="${standort%%,*}"

  local fehler=0
  local gesamt=0

  # Genau die Bereiche, deren Aenderung man mitbekommen will.
  for bereich in networkconf wlanconf firewallrule firewallgroup \
                 portconf portforward routing setting usergroup; do
    local ziel="${ABLAGE}/unifi-${bereich}.json"
    local temp="${ziel}.neu"

    if curl "${kurven[@]}" -o "$temp" \
         "${basis}${praefix}/api/s/${standort}/rest/${bereich}" 2>/dev/null; then
      # Sortiert und eingerueckt ablegen, sonst erzeugt schon eine
      # geaenderte Reihenfolge einen scheinbaren Unterschied.
      if python3 -c "
import json, sys
roh = json.load(open('$temp'))
daten = roh.get('data', roh)
json.dump(daten, open('$ziel', 'w'), indent=2, sort_keys=True, ensure_ascii=False)
" 2>/dev/null; then
        gesamt=$((gesamt + 1))
      else
        warne "UniFi: ${bereich} nicht auswertbar"
        fehler=$((fehler + 1))
      fi
    else
      # Nicht jeder Bereich existiert in jeder Fassung – kein Beinbruch.
      :
    fi
    rm -f "$temp"
  done

  rm -f "$kekse"

  if [[ $gesamt -eq 0 ]]; then
    warne "UniFi: kein einziger Bereich konnte abgerufen werden"
    metrik "schule_konfig_sicherung_erfolgreich{geraet=\"unifi\"} 0"
    return 1
  fi

  metrik "schule_konfig_sicherung_erfolgreich{geraet=\"unifi\"} 1"
  metrik "schule_konfig_bereiche{geraet=\"unifi\"} ${gesamt}"
  melde "UniFi: ${gesamt} Konfigurationsbereiche gesichert (Standort ${standort})"
}

# ---------------------------------------------------------------------
# Ausfuehren
# ---------------------------------------------------------------------
melde "Konfigurationssicherung beginnt"

sichere_fortigate || true
sichere_unifi || true

# ---------------------------------------------------------------------
# Aenderungen festhalten
# ---------------------------------------------------------------------
GEAENDERT=0
if [[ $GIT_DA -eq 1 ]]; then
  chmod -R go-rwx "$ABLAGE" 2>/dev/null || true

  if [[ -n "$(git -C "$ABLAGE" status --porcelain)" ]]; then
    GEAENDERT=1
    ANZAHL="$(git -C "$ABLAGE" status --porcelain | wc -l)"
    git -C "$ABLAGE" add -A
    git -C "$ABLAGE" -c commit.gpgsign=false commit -q \
      -m "Konfigurationsstand $(date '+%d.%m.%Y %H:%M')" || true
    melde "Aenderung festgehalten: ${ANZAHL} Datei(en)"

    # Kurzfassung ins Protokoll, damit man im journal sieht, WAS anders ist
    git -C "$ABLAGE" show --stat --oneline HEAD | head -20
  else
    melde "Keine Aenderung gegenueber dem letzten Stand"
  fi

  metrik "schule_konfig_staende_gesamt $(git -C "$ABLAGE" rev-list --count HEAD 2>/dev/null || echo 0)"
fi

metrik "schule_konfig_geaendert ${GEAENDERT}"
metrik "schule_konfig_sicherung_zeitstempel $(jetzt)"

# ---------------------------------------------------------------------
# Messwerte ablegen
# ---------------------------------------------------------------------
{
  echo "# HELP schule_konfig_sicherung_erfolgreich 1 = Konfiguration des Geraets konnte gesichert werden"
  echo "# TYPE schule_konfig_sicherung_erfolgreich gauge"
  echo "# HELP schule_konfig_geaendert 1 = beim letzten Lauf hat sich etwas geaendert"
  echo "# TYPE schule_konfig_geaendert gauge"
  echo "# HELP schule_konfig_sicherung_zeitstempel Zeitpunkt der letzten Sicherung (Unixzeit)"
  echo "# TYPE schule_konfig_sicherung_zeitstempel gauge"
  echo "# HELP schule_konfig_staende_gesamt Anzahl der gespeicherten Konfigurationsstaende"
  echo "# TYPE schule_konfig_staende_gesamt gauge"
  echo "# HELP schule_konfig_groesse_bytes Groesse der gesicherten Konfiguration"
  echo "# TYPE schule_konfig_groesse_bytes gauge"
  echo "# HELP schule_konfig_bereiche Anzahl gesicherter Konfigurationsbereiche"
  echo "# TYPE schule_konfig_bereiche gauge"
  printf '%s\n' "${METRIKEN[@]}"
} > "${METRIKDATEI}.tmp"
mv "${METRIKDATEI}.tmp" "$METRIKDATEI"

melde "Fertig. Stand liegt in $ABLAGE"
