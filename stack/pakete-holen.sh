#!/usr/bin/env bash
# =====================================================================
# Holt die Agent-Pakete und legt sie im Ordner dist/ ab.
#
# Sinn der Sache: Die Windows-Clients in der Schule brauchen dann KEINEN
# eigenen Internetzugang. Sie ziehen alles vom Monitoring-Server.
#
# Kann jederzeit erneut aufgerufen werden, z. B. nach dem Anheben der
# Versionen in .env.
# =====================================================================
set -euo pipefail

STACK_VERZEICHNIS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJEKT_VERZEICHNIS="$(dirname "$STACK_VERZEICHNIS")"
DIST="${STACK_VERZEICHNIS}/dist"

cd "$STACK_VERZEICHNIS"
mkdir -p "$DIST"

if [[ -f .env ]]; then
  set -a; # shellcheck disable=SC1091
  source .env; set +a
fi

WINDOWS_EXPORTER_VERSION="${WINDOWS_EXPORTER_VERSION:-0.30.5}"
ALLOY_WINDOWS_VERSION="${ALLOY_WINDOWS_VERSION:-1.7.1}"
MON_HOSTNAME="${MON_HOSTNAME:-monitoring}"

melde() { echo -e "\033[1;36m  ==> $*\033[0m"; }
warne() { echo -e "\033[1;33m  [!] $*\033[0m"; }

holen() {
  local url="$1" ziel="$2"
  if [[ -s "$ziel" ]]; then
    echo "      bereits vorhanden: $(basename "$ziel")"
    return 0
  fi
  echo "      lade $(basename "$ziel") ..."
  if curl -fsSL --retry 3 --retry-delay 2 --max-time 300 -o "${ziel}.teil" "$url"; then
    mv "${ziel}.teil" "$ziel"
    return 0
  fi
  rm -f "${ziel}.teil"
  warne "Download fehlgeschlagen: $url"
  return 1
}

# ---------------------------------------------------------------------
melde "windows_exporter ${WINDOWS_EXPORTER_VERSION}"
holen "https://github.com/prometheus-community/windows_exporter/releases/download/v${WINDOWS_EXPORTER_VERSION}/windows_exporter-${WINDOWS_EXPORTER_VERSION}-amd64.msi" \
      "${DIST}/windows_exporter.msi" || true

# ---------------------------------------------------------------------
melde "Grafana Alloy ${ALLOY_WINDOWS_VERSION} (Windows)"
if holen "https://github.com/grafana/alloy/releases/download/v${ALLOY_WINDOWS_VERSION}/alloy-installer-windows-amd64.exe.zip" \
         "${DIST}/alloy-installer-windows-amd64.exe.zip"; then
  :
else
  warne "Alloy konnte nicht geladen werden – die Protokollsammlung auf den Clients wird dann uebersprungen."
fi

# ---------------------------------------------------------------------
melde "Agent-Skripte bereitstellen"
for datei in Install-MonitoringAgent.ps1 Uninstall-MonitoringAgent.ps1 \
             Collect-HardwareHealth.ps1 Collect-SecurityBaseline.ps1; do
  if [[ -f "${PROJEKT_VERZEICHNIS}/agent/${datei}" ]]; then
    cp "${PROJEKT_VERZEICHNIS}/agent/${datei}" "${DIST}/${datei}"
    echo "      ${datei}"
  else
    warne "${datei} nicht gefunden"
  fi
done

if [[ -f "${PROJEKT_VERZEICHNIS}/agent/linux/install-agent.sh" ]]; then
  cp "${PROJEKT_VERZEICHNIS}/agent/linux/install-agent.sh" "${DIST}/install-agent.sh"
  echo "      install-agent.sh"
fi

# ---------------------------------------------------------------------
melde "Alloy-Konfiguration fuer die Clients erzeugen"

if [[ ! -f "${PROJEKT_VERZEICHNIS}/agent/alloy-client.alloy.tmpl" ]]; then
  warne "Vorlage agent/alloy-client.alloy.tmpl fehlt"
else
  sed -e "s|__MON_HOSTNAME__|${MON_HOSTNAME}|g" \
      -e "s|__AGENT_TOKEN__|${AGENT_TOKEN:-}|g" \
      "${PROJEKT_VERZEICHNIS}/agent/alloy-client.alloy.tmpl" > "${DIST}/alloy-client.alloy"
  chmod 640 "${DIST}/alloy-client.alloy"
  echo "      alloy-client.alloy"
fi

echo
echo "  Bereitgestellt in ${DIST}:"
ls -1sh "$DIST" | sed 's/^/    /'
