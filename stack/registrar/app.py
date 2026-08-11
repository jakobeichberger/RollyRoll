#!/usr/bin/env python3
"""
Registrierungs-Dienst des Schul-Monitorings.

Aufgaben:
  1. Windows- und Linux-Agenten melden sich hier selbst an. Daraus entstehen
     die Prometheus-Ziellisten (file_sd) – niemand muss Geraete von Hand
     eintragen.
  2. Die Geraeteliste inventar.yml (Switches, APs, Firewall, Drucker, USV)
     wird laufend in Ziellisten fuer ICMP, SNMP und Dienstpruefungen
     uebersetzt.
  3. Die Geraeteerkennung durchsucht in regelmaessigen Abstaenden das Netz
     und fragt den UniFi-Controller ab. Was dabei auftaucht und noch
     nirgends erfasst ist, wird selbsttaetig mit ueberwacht.
  4. Die Agenten holen sich hier ihre Pakete (windows_exporter, Alloy) und
     ihre Alloy-Konfiguration ab. Updates gehen damit zentral, ohne die
     Gruppenrichtlinie anzufassen.
  5. Eigene Kennzahlen fuer Prometheus unter /metrics.

Bewusst nur Standardbibliothek + PyYAML: wenig Angriffsflaeche, kaum
Abhaengigkeiten, laeuft auch in fuenf Jahren noch.
"""

from __future__ import annotations

import hmac
import json
import logging
import os
import re
import threading
import time
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

import yaml

import erkennung

# --------------------------------------------------------------------- #
# Konfiguration
# --------------------------------------------------------------------- #
AGENT_TOKEN = os.environ.get("AGENT_TOKEN", "")
STANDORT = os.environ.get("STANDORT", "Schule")
MON_HOSTNAME = os.environ.get("MON_HOSTNAME", "monitoring")
AGENT_TIMEOUT_TAGE = int(os.environ.get("AGENT_TIMEOUT_TAGE", "30"))
SNMP_MODUL_STANDARD = os.environ.get("SNMP_MODUL_STANDARD", "if_mib")
HORCH_PORT = int(os.environ.get("PORT", "8000"))

ZIEL_VERZEICHNIS = Path(os.environ.get("ZIEL_VERZEICHNIS", "/targets"))
INVENTAR_DATEI = Path(os.environ.get("INVENTAR_DATEI", "/inventar/inventar.yml"))
DIST_VERZEICHNIS = Path(os.environ.get("DIST_VERZEICHNIS", "/dist"))
ZUSTAND_DATEI = Path(os.environ.get("ZUSTAND_DATEI", "/state/agents.json"))

# --- Geraeteerkennung ------------------------------------------------- #
# Leer = ausgeschaltet. Mehrere Netze mit Komma trennen.
ERKENNUNG_NETZE = [
    n.strip() for n in os.environ.get("ERKENNUNG_NETZE", "").split(",") if n.strip()
]
# "automatisch" = Fundstuecke werden sofort mitueberwacht.
# "vorschlag"   = nur auflisten, der Mensch entscheidet.
ERKENNUNG_MODUS = os.environ.get("ERKENNUNG_MODUS", "automatisch").strip().lower()
ERKENNUNG_INTERVALL_MINUTEN = int(os.environ.get("ERKENNUNG_INTERVALL_MINUTEN", "60"))
ERKENNUNG_HOECHSTZAHL = int(os.environ.get("ERKENNUNG_HOECHSTZAHL", "4096"))
SNMP_COMMUNITY = os.environ.get("SNMP_COMMUNITY", "public")
UNIFI_URL = os.environ.get("UNIFI_URL", "")
UNIFI_BENUTZER = os.environ.get("UNIFI_BENUTZER", "")
UNIFI_PASSWORT = os.environ.get("UNIFI_PASSWORT", "")
UNIFI_SITES = os.environ.get("UNIFI_SITES", "all")

GEFUNDEN_ZUSTAND = Path(os.environ.get("GEFUNDEN_ZUSTAND", "/state/gefunden.json"))
GEFUNDEN_DATEI = Path(os.environ.get("GEFUNDEN_DATEI", "/inventar/gefunden.yml"))

WINDOWS_EXPORTER_PORT = 9182
NODE_EXPORTER_PORT = 9100

# Nur diese Zeichen sind in Hostnamen und Labelwerten erlaubt.
SAUBER = re.compile(r"[^A-Za-z0-9._\- äöüÄÖÜß]")
IPV4 = re.compile(r"^(?:\d{1,3}\.){3}\d{1,3}$")

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
)
log = logging.getLogger("registrar")

_sperre = threading.RLock()


# --------------------------------------------------------------------- #
# Hilfsfunktionen
# --------------------------------------------------------------------- #
def jetzt() -> float:
    return time.time()


def saeubern(wert: Any, maxlaenge: int = 120) -> str:
    """Macht aus beliebiger Eingabe einen sicheren Label-Wert."""
    if wert is None:
        return ""
    text = str(wert).strip()[:maxlaenge]
    return SAUBER.sub("", text)


def atomar_schreiben(pfad: Path, inhalt: str) -> None:
    """Erst in eine temporaere Datei, dann umbenennen – Prometheus soll nie
    eine halb geschriebene Zielliste lesen."""
    pfad.parent.mkdir(parents=True, exist_ok=True)
    temp = pfad.with_suffix(pfad.suffix + ".tmp")
    temp.write_text(inhalt, encoding="utf-8")
    os.replace(temp, pfad)


def ziel_schreiben(dateiname: str, eintraege: list[dict]) -> None:
    atomar_schreiben(
        ZIEL_VERZEICHNIS / dateiname,
        json.dumps(eintraege, ensure_ascii=False, indent=2) + "\n",
    )


# --------------------------------------------------------------------- #
# Zustand der Agenten
# --------------------------------------------------------------------- #
class Agentenverzeichnis:
    def __init__(self) -> None:
        self.agenten: dict[str, dict] = {}
        self._laden()

    def _laden(self) -> None:
        if not ZUSTAND_DATEI.exists():
            return
        try:
            self.agenten = json.loads(ZUSTAND_DATEI.read_text(encoding="utf-8"))
            log.info("%d Agenten aus dem gespeicherten Zustand geladen", len(self.agenten))
        except (OSError, ValueError) as fehler:
            log.error("Zustandsdatei nicht lesbar (%s) – starte mit leerer Liste", fehler)
            self.agenten = {}

    def _sichern(self) -> None:
        atomar_schreiben(
            ZUSTAND_DATEI,
            json.dumps(self.agenten, ensure_ascii=False, indent=2) + "\n",
        )

    def eintragen(self, daten: dict, absender_ip: str) -> dict:
        name = saeubern(daten.get("hostname") or "").lower()
        if not name:
            raise ValueError("hostname fehlt")

        ip = saeubern(daten.get("ip") or "")
        if not IPV4.match(ip):
            # Faellt der Agent auf keine brauchbare IP, nehmen wir die
            # Adresse, von der die Anmeldung kam.
            ip = absender_ip

        rolle = saeubern(daten.get("rolle") or "client").lower()
        if rolle not in ("server", "client", "hyperv", "dc"):
            rolle = "client"

        plattform = saeubern(daten.get("plattform") or "windows").lower()
        if plattform not in ("windows", "linux"):
            plattform = "windows"

        with _sperre:
            vorher = self.agenten.get(name, {})
            eintrag = {
                "hostname": name,
                "ip": ip,
                "rolle": rolle,
                "plattform": plattform,
                "betriebssystem": saeubern(daten.get("betriebssystem")),
                "domaene": saeubern(daten.get("domaene")),
                "raum": saeubern(daten.get("raum")),
                "standort": saeubern(daten.get("standort")) or STANDORT,
                "agent_version": saeubern(daten.get("agent_version")),
                "exporter_port": int(daten.get("exporter_port") or WINDOWS_EXPORTER_PORT),
                "modell": saeubern(daten.get("modell")),
                "seriennummer": saeubern(daten.get("seriennummer")),
                "erste_meldung": vorher.get("erste_meldung", jetzt()),
                "letzte_meldung": jetzt(),
            }
            self.agenten[name] = eintrag
            self._sichern()

        neu = "hostname" not in vorher
        log.info(
            "%s: %s (%s, %s, Agent %s)",
            "Neuer Agent" if neu else "Agent aktualisiert",
            name, ip, rolle, eintrag["agent_version"] or "?",
        )
        return eintrag

    def lebenszeichen(self, hostname: str) -> bool:
        name = saeubern(hostname).lower()
        with _sperre:
            if name not in self.agenten:
                return False
            self.agenten[name]["letzte_meldung"] = jetzt()
            self._sichern()
        return True

    def aufraeumen(self) -> int:
        grenze = jetzt() - AGENT_TIMEOUT_TAGE * 86400
        with _sperre:
            alt = [n for n, a in self.agenten.items() if a.get("letzte_meldung", 0) < grenze]
            for name in alt:
                log.info("Agent %s seit %d Tagen still – wird entfernt", name, AGENT_TIMEOUT_TAGE)
                del self.agenten[name]
            if alt:
                self._sichern()
        return len(alt)

    def liste(self) -> list[dict]:
        with _sperre:
            return sorted(self.agenten.values(), key=lambda a: a["hostname"])


verzeichnis = Agentenverzeichnis()


# --------------------------------------------------------------------- #
# Ziellisten erzeugen
# --------------------------------------------------------------------- #
def agenten_ziele_schreiben() -> None:
    windows, linux, icmp = [], [], []

    for agent in verzeichnis.liste():
        labels = {
            "geraet": agent["hostname"],
            "rolle": agent["rolle"],
            "typ": agent["plattform"],
            "standort": agent["standort"],
        }
        for schluessel in ("betriebssystem", "domaene", "raum", "agent_version", "modell"):
            if agent.get(schluessel):
                labels[schluessel] = agent[schluessel]

        port = agent.get("exporter_port") or (
            WINDOWS_EXPORTER_PORT if agent["plattform"] == "windows" else NODE_EXPORTER_PORT
        )
        eintrag = {"targets": [f"{agent['ip']}:{port}"], "labels": labels}

        if agent["plattform"] == "windows":
            windows.append(eintrag)
        else:
            linux.append(eintrag)

        icmp.append({
            "targets": [agent["ip"]],
            "labels": {**labels, "kritisch": "ja" if agent["rolle"] in ("server", "dc", "hyperv") else "nein"},
        })

    ziel_schreiben("windows-agenten.json", windows)
    ziel_schreiben("linux-agenten.json", linux)
    ziel_schreiben("icmp-agenten.json", icmp)


def _inventar_lesen() -> dict:
    if not INVENTAR_DATEI.exists():
        return {}
    try:
        daten = yaml.safe_load(INVENTAR_DATEI.read_text(encoding="utf-8")) or {}
        if not isinstance(daten, dict):
            raise ValueError("oberste Ebene ist kein Zuordnungsblock")
        return daten
    except (OSError, ValueError, yaml.YAMLError) as fehler:
        log.error("inventar.yml kann nicht gelesen werden: %s", fehler)
        return {}


def inventar_ziele_schreiben() -> int:
    daten = _inventar_lesen()
    geraete = daten.get("geraete") or []
    vorgaben = daten.get("vorgaben") or {}
    standort = saeubern(daten.get("standort")) or STANDORT

    icmp, snmp, dienste, fortigate = [], [], [], []
    adressen: set[str] = set()

    for eintrag in geraete:
        if not isinstance(eintrag, dict):
            continue
        name = saeubern(eintrag.get("name"))
        ip = saeubern(eintrag.get("ip"))
        if not name or not ip:
            log.warning("Inventar-Eintrag ohne name oder ip wird uebersprungen: %r", eintrag)
            continue
        adressen.add(ip)

        basis = {
            "geraet": name,
            "typ": saeubern(eintrag.get("typ") or "sonstiges"),
            "standort": standort,
        }
        for schluessel in ("raum", "hersteller", "modell"):
            if eintrag.get(schluessel):
                basis[schluessel] = saeubern(eintrag[schluessel])
        basis["kritisch"] = "ja" if eintrag.get("kritisch") else "nein"

        if eintrag.get("ping", vorgaben.get("ping", True)):
            icmp.append({"targets": [ip], "labels": dict(basis)})

        modul = eintrag.get("snmp_modul")
        if modul:
            snmp.append({
                "targets": [ip],
                "labels": {
                    **basis,
                    "modul": saeubern(modul) or SNMP_MODUL_STANDARD,
                    "auth": saeubern(eintrag.get("snmp_auth") or vorgaben.get("snmp_auth") or "schule_v2"),
                },
            })

        for dienst in eintrag.get("dienste") or []:
            if not isinstance(dienst, dict):
                continue
            port = dienst.get("port")
            dienstmodul = saeubern(dienst.get("modul") or "tcp")
            if not port:
                continue
            # HTTP-Pruefungen brauchen eine vollstaendige URL, TCP nur host:port
            if dienstmodul in ("http", "https"):
                ziel = f"{dienstmodul}://{ip}:{port}"
            else:
                ziel = f"{ip}:{port}"
            dienste.append({
                "targets": [ziel],
                "labels": {
                    **basis,
                    "modul": dienstmodul,
                    "dienst": saeubern(dienst.get("name") or dienstmodul),
                },
            })

        if eintrag.get("fortigate_api"):
            fortigate.append({"targets": [f"https://{ip}"], "labels": dict(basis)})

    ziel_schreiben("icmp-inventar.json", icmp)
    ziel_schreiben("snmp-inventar.json", snmp)
    ziel_schreiben("dienst-inventar.json", dienste)
    ziel_schreiben("fortigate-inventar.json", fortigate)

    global _inventar_adressen
    _inventar_adressen = adressen
    return len(geraete)


_inventar_anzahl = 0
_inventar_mtime = 0.0
_inventar_adressen: set[str] = set()


# --------------------------------------------------------------------- #
# Geraeteerkennung
# --------------------------------------------------------------------- #
class Erkennungsverzeichnis:
    """
    Merkt sich, was im Netz gefunden wurde – samt Zeitpunkt der ersten
    Sichtung. Nur so laesst sich spaeter sagen "das Geraet ist neu", und
    genau das ist die sicherheitstechnisch interessante Meldung.
    """

    def __init__(self) -> None:
        self.funde: dict[str, dict] = {}
        self.letzter_lauf: float = 0.0
        self.letzte_dauer: float = 0.0
        self._laden()

    def _laden(self) -> None:
        if not GEFUNDEN_ZUSTAND.exists():
            return
        try:
            gespeichert = json.loads(GEFUNDEN_ZUSTAND.read_text(encoding="utf-8"))
            self.funde = gespeichert.get("funde", {})
            self.letzter_lauf = float(gespeichert.get("letzter_lauf", 0.0))
            log.info("%d fruehere Fundstuecke geladen", len(self.funde))
        except (OSError, ValueError) as fehler:
            log.error("Erkennungszustand nicht lesbar (%s) – beginne von vorn", fehler)
            self.funde = {}

    def _sichern(self) -> None:
        atomar_schreiben(GEFUNDEN_ZUSTAND, json.dumps(
            {"funde": self.funde, "letzter_lauf": self.letzter_lauf},
            ensure_ascii=False, indent=2,
        ) + "\n")

    def uebernehmen(self, treffer: list[dict], dauer: float) -> int:
        """Fuehrt einen Suchlauf mit dem bisherigen Stand zusammen."""
        neu = 0
        with _sperre:
            belegt = {f.get("name", "") for f in self.funde.values()}
            for fund in treffer:
                ip = fund["ip"]
                vorher = self.funde.get(ip, {})
                if not vorher:
                    neu += 1
                    name = erkennung.namen_vorschlagen(fund, belegt)
                    belegt.add(name)
                else:
                    name = vorher.get("name") or erkennung.namen_vorschlagen(fund, belegt)

                self.funde[ip] = {
                    **fund,
                    "name": name,
                    "erste_sichtung": vorher.get("erste_sichtung", jetzt()),
                    "letzte_sichtung": jetzt(),
                }

            self.letzter_lauf = jetzt()
            self.letzte_dauer = dauer
            self._sichern()
        return neu

    def liste(self) -> list[dict]:
        with _sperre:
            return sorted(self.funde.values(), key=lambda f: f.get("name", ""))

    def unerfasst(self) -> list[dict]:
        """Fundstuecke, die weder im Inventar stehen noch einen Agenten haben."""
        bekannt = set(_inventar_adressen) | {a["ip"] for a in verzeichnis.liste()}
        return [f for f in self.liste() if f["ip"] not in bekannt]


erkennungsverzeichnis = Erkennungsverzeichnis()


def erkennung_ziele_schreiben() -> int:
    """
    Erzeugt Ziellisten aus den Fundstuecken. Die Dateinamen passen in die
    vorhandenen Suchmuster von prometheus.yml (icmp-*, snmp-*), es ist also
    keine Aenderung an der Prometheus-Konfiguration noetig.

    Im Modus "vorschlag" bleiben die Listen leer – dann wird nur angezeigt,
    was da ist, ueberwacht wird es aber erst nach Eintrag in inventar.yml.
    """
    icmp, snmp = [], []

    if ERKENNUNG_MODUS == "automatisch":
        for fund in erkennungsverzeichnis.unerfasst():
            labels = {
                "geraet": saeubern(fund.get("name")) or fund["ip"],
                "typ": saeubern(fund.get("typ") or "sonstiges"),
                "standort": STANDORT,
                "hersteller": saeubern(fund.get("hersteller") or "unbekannt"),
                "erkannt": "ja",
                "kritisch": "nein",
            }
            if fund.get("modell"):
                labels["modell"] = saeubern(fund["modell"])

            icmp.append({"targets": [fund["ip"]], "labels": dict(labels)})

            # Per SNMP nur abfragen, was auch per SNMP gefunden wurde –
            # UniFi-Geraete aus dem Controller haben oft gar kein SNMP an.
            if fund.get("quelle") == "snmp":
                snmp.append({
                    "targets": [fund["ip"]],
                    "labels": {
                        **labels,
                        "modul": saeubern(fund.get("snmp_modul") or SNMP_MODUL_STANDARD),
                        "auth": "schule_v2",
                    },
                })

    ziel_schreiben("icmp-gefunden.json", icmp)
    ziel_schreiben("snmp-gefunden.json", snmp)
    return len(icmp)


def gefunden_yml_schreiben() -> None:
    """
    Schreibt die Fundstuecke als lesbare YAML-Datei neben inventar.yml.
    Bewusst im selben Aufbau: einzelne Bloecke lassen sich unveraendert
    nach inventar.yml uebernehmen, sobald der richtige Name und der Raum
    feststehen. inventar.yml selbst wird nie angefasst.
    """
    funde = erkennungsverzeichnis.liste()
    bekannt = set(_inventar_adressen) | {a["ip"] for a in verzeichnis.liste()}

    zeilen = [
        "---",
        "# ===================================================================",
        "# Automatisch gefundene Geraete – NICHT von Hand bearbeiten.",
        "# Diese Datei wird bei jedem Suchlauf neu geschrieben.",
        "# ===================================================================",
        "#",
        "# Uebernahme ins richtige Inventar: den gewuenschten Block nach",
        "# inventar.yml kopieren, Namen und Raum eintragen, fertig. Danach",
        "# verschwindet der Eintrag hier von selbst.",
        "#",
        f"# Letzter Suchlauf: {datetime.fromtimestamp(erkennungsverzeichnis.letzter_lauf, timezone.utc).isoformat(timespec='seconds') if erkennungsverzeichnis.letzter_lauf else 'noch keiner'}",
        f"# Gefunden: {len(funde)} Geraete, davon {len([f for f in funde if f['ip'] not in bekannt])} noch nicht erfasst",
        "",
        "geraete:",
    ]

    if not funde:
        zeilen.append("  []")

    for fund in funde:
        erfasst = fund["ip"] in bekannt
        vermerk = "  # bereits erfasst" if erfasst else ""
        zeilen.append("")
        zeilen.append(f"  - name: {fund.get('name') or fund['ip']}{vermerk}")
        zeilen.append(f"    ip: {fund['ip']}")
        zeilen.append(f"    typ: {fund.get('typ') or 'sonstiges'}")
        if fund.get("hersteller"):
            zeilen.append(f"    hersteller: {fund['hersteller']}")
        if fund.get("quelle") == "snmp" and fund.get("snmp_modul"):
            zeilen.append(f"    snmp_modul: {fund['snmp_modul']}")
        if fund.get("modell"):
            zeilen.append(f"    # Modell:      {fund['modell']}")
        if fund.get("beschreibung"):
            zeilen.append(f"    # Auskunft:    {fund['beschreibung'][:110]}")
        if fund.get("mac"):
            zeilen.append(f"    # MAC:         {fund['mac']}")
        zeilen.append(f"    # Quelle:      {fund.get('quelle', '?')}")
        zeilen.append(
            f"    # Erstmals:    "
            f"{datetime.fromtimestamp(fund.get('erste_sichtung', 0), timezone.utc).strftime('%d.%m.%Y %H:%M')} UTC"
        )

    try:
        atomar_schreiben(GEFUNDEN_DATEI, "\n".join(zeilen) + "\n")
    except OSError as fehler:
        log.error("gefunden.yml kann nicht geschrieben werden: %s", fehler)


def suchlauf_ausfuehren() -> int:
    """Einen kompletten Erkennungsdurchgang fahren."""
    if not ERKENNUNG_NETZE and not UNIFI_URL:
        return 0

    beginn = time.monotonic()
    unifi = None
    if UNIFI_URL and UNIFI_BENUTZER and UNIFI_PASSWORT:
        unifi = {
            "url": UNIFI_URL, "benutzer": UNIFI_BENUTZER,
            "passwort": UNIFI_PASSWORT, "sites": UNIFI_SITES,
        }

    treffer = erkennung.suchlauf(
        netze=ERKENNUNG_NETZE,
        community=SNMP_COMMUNITY,
        unifi=unifi,
        hoechstzahl=ERKENNUNG_HOECHSTZAHL,
    )
    dauer = time.monotonic() - beginn
    neu = erkennungsverzeichnis.uebernehmen(treffer, dauer)

    erkennung_ziele_schreiben()
    gefunden_yml_schreiben()

    if neu:
        log.info("Geraeteerkennung: %d neue Geraete im Netz (%d insgesamt)",
                 neu, len(treffer))
    return neu


def erkennungsarbeit() -> None:
    """Hintergrundschleife der Geraeteerkennung."""
    if not ERKENNUNG_NETZE and not UNIFI_URL:
        log.info("Geraeteerkennung ist ausgeschaltet (ERKENNUNG_NETZE ist leer)")
        return

    log.info(
        "Geraeteerkennung aktiv: Netze %s, Modus %s, alle %d Minuten",
        ", ".join(ERKENNUNG_NETZE) or "(nur UniFi)",
        ERKENNUNG_MODUS, ERKENNUNG_INTERVALL_MINUTEN,
    )
    # Kurz warten, damit Inventar und Agentenliste zuerst stehen –
    # sonst gilt beim allerersten Lauf alles als unerfasst.
    time.sleep(20)

    while True:
        try:
            suchlauf_ausfuehren()
        except Exception as fehler:  # niemals sterben
            log.exception("Fehler bei der Geraeteerkennung: %s", fehler)
        time.sleep(max(5, ERKENNUNG_INTERVALL_MINUTEN) * 60)


def hintergrundarbeit() -> None:
    """Haelt Ziellisten aktuell und raeumt verschwundene Agenten weg."""
    global _inventar_anzahl, _inventar_mtime
    while True:
        try:
            mtime = INVENTAR_DATEI.stat().st_mtime if INVENTAR_DATEI.exists() else 0.0
            if mtime != _inventar_mtime:
                _inventar_mtime = mtime
                _inventar_anzahl = inventar_ziele_schreiben()
                log.info("Geraeteliste neu eingelesen: %d Eintraege", _inventar_anzahl)

            verzeichnis.aufraeumen()
            agenten_ziele_schreiben()

            # Sobald ein Fundstueck ins Inventar uebernommen wurde oder
            # einen Agenten bekommen hat, faellt es hier wieder heraus –
            # sonst wuerde es doppelt ueberwacht.
            if erkennungsverzeichnis.funde:
                erkennung_ziele_schreiben()
        except Exception as fehler:  # niemals sterben
            log.exception("Fehler in der Hintergrundarbeit: %s", fehler)
        time.sleep(30)


# --------------------------------------------------------------------- #
# Metriken
# --------------------------------------------------------------------- #
def metriken() -> str:
    agenten = verzeichnis.liste()
    zeilen: list[str] = []

    def m(name: str, hilfe: str, typ: str, werte: list[str]) -> None:
        zeilen.append(f"# HELP {name} {hilfe}")
        zeilen.append(f"# TYPE {name} {typ}")
        zeilen.extend(werte)

    m("schule_agenten_gesamt", "Anzahl registrierter Agenten", "gauge",
      [f"schule_agenten_gesamt {len(agenten)}"])

    nach_rolle: dict[str, int] = {}
    for a in agenten:
        nach_rolle[a["rolle"]] = nach_rolle.get(a["rolle"], 0) + 1
    m("schule_agenten_nach_rolle", "Registrierte Agenten je Rolle", "gauge",
      [f'schule_agenten_nach_rolle{{rolle="{r}"}} {n}' for r, n in sorted(nach_rolle.items())] or
      ['schule_agenten_nach_rolle{rolle="keine"} 0'])

    jetzt_ = jetzt()
    m("schule_agent_letzte_meldung_sekunden",
      "Sekunden seit der letzten Meldung des Agenten", "gauge",
      [f'schule_agent_letzte_meldung_sekunden{{geraet="{a["hostname"]}"}} '
       f'{jetzt_ - a.get("letzte_meldung", jetzt_):.0f}' for a in agenten])

    versionen = {a.get("agent_version", "") for a in agenten if a.get("agent_version")}
    neueste = max(versionen) if versionen else ""
    alt = sum(1 for a in agenten if a.get("agent_version") and a["agent_version"] != neueste)
    m("schule_agent_version_alt", "Agenten mit veralteter Version", "gauge",
      [f"schule_agent_version_alt {alt}"])

    m("schule_inventar_geraete_gesamt",
      "Geraete aus inventar.yml (Netzwerk, Drucker, USV ...)", "gauge",
      [f"schule_inventar_geraete_gesamt {_inventar_anzahl}"])

    m("schule_registrar_bereit", "1 wenn der Registrierungs-Dienst laeuft", "gauge",
      ["schule_registrar_bereit 1"])

    # --- Geraeteerkennung --------------------------------------------- #
    funde = erkennungsverzeichnis.liste()
    unerfasst = erkennungsverzeichnis.unerfasst()

    m("schule_erkennung_aktiv", "1 wenn die Geraeteerkennung eingeschaltet ist", "gauge",
      [f"schule_erkennung_aktiv {1 if (ERKENNUNG_NETZE or UNIFI_URL) else 0}"])

    m("schule_erkennung_geraete_gesamt", "Im Netz gefundene Geraete", "gauge",
      [f"schule_erkennung_geraete_gesamt {len(funde)}"])

    m("schule_erkennung_unerfasst",
      "Gefundene Geraete, die weder im Inventar stehen noch einen Agenten haben", "gauge",
      [f"schule_erkennung_unerfasst {len(unerfasst)}"])

    nach_typ: dict[str, int] = {}
    for f in funde:
        schluessel = f.get("typ") or "sonstiges"
        nach_typ[schluessel] = nach_typ.get(schluessel, 0) + 1
    m("schule_erkennung_nach_typ", "Gefundene Geraete je Geraetetyp", "gauge",
      [f'schule_erkennung_nach_typ{{typ="{t}"}} {n}' for t, n in sorted(nach_typ.items())] or
      ['schule_erkennung_nach_typ{typ="keine"} 0'])

    m("schule_erkennung_letzter_lauf_zeitstempel",
      "Zeitpunkt des letzten Suchlaufs (Unixzeit)", "gauge",
      [f"schule_erkennung_letzter_lauf_zeitstempel {erkennungsverzeichnis.letzter_lauf:.0f}"])

    m("schule_erkennung_dauer_sekunden", "Dauer des letzten Suchlaufs", "gauge",
      [f"schule_erkennung_dauer_sekunden {erkennungsverzeichnis.letzte_dauer:.1f}"])

    # Ein Eintrag je Geraet – damit steht im Dashboard die ganze Liste mit
    # Name, Hersteller und Typ, ohne dass eine zweite Datenquelle noetig ist.
    bekannt = set(_inventar_adressen) | {a["ip"] for a in agenten}
    m("schule_erkanntes_geraet",
      "Im Netz gefundenes Geraet (1 = gesehen)", "gauge",
      [f'schule_erkanntes_geraet{{geraet="{f.get("name", "")}",adresse="{f["ip"]}",'
       f'typ="{f.get("typ", "sonstiges")}",hersteller="{f.get("hersteller", "unbekannt")}",'
       f'quelle="{f.get("quelle", "?")}",erfasst="{"ja" if f["ip"] in bekannt else "nein"}"}} 1'
       for f in funde])

    m("schule_erkanntes_geraet_erste_sichtung_zeitstempel",
      "Wann das Geraet zum ersten Mal im Netz gesehen wurde (Unixzeit)", "gauge",
      [f'schule_erkanntes_geraet_erste_sichtung_zeitstempel{{geraet="{f.get("name", "")}",'
       f'adresse="{f["ip"]}"}} {f.get("erste_sichtung", 0):.0f}' for f in funde])

    return "\n".join(zeilen) + "\n"


# --------------------------------------------------------------------- #
# HTTP
# --------------------------------------------------------------------- #
class Handler(BaseHTTPRequestHandler):
    server_version = "SchulMonitoringRegistrar/1.0"
    protocol_version = "HTTP/1.1"

    # ---------------- Hilfsmethoden ---------------- #
    def _antwort(self, status: int, koerper: bytes, typ: str = "application/json; charset=utf-8") -> None:
        self.send_response(status)
        self.send_header("Content-Type", typ)
        self.send_header("Content-Length", str(len(koerper)))
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(koerper)

    def _json(self, status: int, daten: dict) -> None:
        self._antwort(status, json.dumps(daten, ensure_ascii=False, indent=2).encode("utf-8"))

    def _fehler(self, status: int, meldung: str) -> None:
        self._json(status, {"fehler": meldung})

    def _token_gueltig(self) -> bool:
        if not AGENT_TOKEN:
            return True  # kein Token gesetzt = offener Betrieb (nur im Labor sinnvoll)
        mitgeschickt = self.headers.get("X-Agent-Token", "")
        return hmac.compare_digest(mitgeschickt, AGENT_TOKEN)

    def _absender_ip(self) -> str:
        weitergeleitet = self.headers.get("X-Forwarded-For", "")
        if weitergeleitet:
            return saeubern(weitergeleitet.split(",")[0].strip())
        return saeubern(self.client_address[0])

    def _koerper_lesen(self, maximal: int = 65536) -> dict:
        laenge = int(self.headers.get("Content-Length") or 0)
        if laenge <= 0 or laenge > maximal:
            raise ValueError("ungueltige Nachrichtenlaenge")
        return json.loads(self.rfile.read(laenge).decode("utf-8"))

    def log_message(self, format: str, *args) -> None:  # noqa: A002
        log.debug("%s %s", self.address_string(), format % args)

    # ---------------- Routen ---------------- #
    def do_GET(self) -> None:  # noqa: N802
        pfad = self.path.split("?", 1)[0].rstrip("/") or "/"

        if pfad in ("/", "/api/v1/health"):
            self._json(HTTPStatus.OK, {
                "status": "bereit",
                "dienst": "Schul-Monitoring Registrierung",
                "agenten": len(verzeichnis.liste()),
                "inventar": _inventar_anzahl,
                "gefunden": len(erkennungsverzeichnis.liste()),
                "zeit": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            })
            return

        if pfad == "/metrics":
            self._antwort(HTTPStatus.OK, metriken().encode("utf-8"),
                          "text/plain; version=0.0.4; charset=utf-8")
            return

        if not self._token_gueltig():
            self._fehler(HTTPStatus.UNAUTHORIZED, "Ungueltiges oder fehlendes Agent-Token")
            return

        if pfad == "/api/v1/agents":
            self._json(HTTPStatus.OK, {"agenten": verzeichnis.liste()})
            return

        if pfad == "/api/v1/discovery":
            self._json(HTTPStatus.OK, {
                "modus": ERKENNUNG_MODUS,
                "netze": ERKENNUNG_NETZE,
                "letzter_lauf": erkennungsverzeichnis.letzter_lauf,
                "gefunden": erkennungsverzeichnis.liste(),
                "unerfasst": erkennungsverzeichnis.unerfasst(),
            })
            return

        if pfad.startswith("/api/v1/config/"):
            self._konfiguration_ausliefern(pfad.rsplit("/", 1)[-1])
            return

        if pfad.startswith("/dist/"):
            self._datei_ausliefern(pfad[len("/dist/"):])
            return

        self._fehler(HTTPStatus.NOT_FOUND, "Unbekannter Pfad")

    def do_HEAD(self) -> None:  # noqa: N802
        self.do_GET()

    def do_POST(self) -> None:  # noqa: N802
        pfad = self.path.split("?", 1)[0].rstrip("/")

        if not self._token_gueltig():
            self._fehler(HTTPStatus.UNAUTHORIZED, "Ungueltiges oder fehlendes Agent-Token")
            return

        # Suchlauf von Hand anstossen – braucht keinen Nachrichtenkoerper.
        if pfad == "/api/v1/discovery/scan":
            if not (ERKENNUNG_NETZE or UNIFI_URL):
                self._fehler(HTTPStatus.CONFLICT,
                             "Die Geraeteerkennung ist ausgeschaltet (ERKENNUNG_NETZE ist leer)")
                return
            try:
                neu = suchlauf_ausfuehren()
            except Exception as fehler:  # pragma: no cover
                log.exception("Suchlauf fehlgeschlagen")
                self._fehler(HTTPStatus.INTERNAL_SERVER_ERROR, str(fehler))
                return
            self._json(HTTPStatus.OK, {
                "status": "fertig",
                "neu": neu,
                "gefunden": len(erkennungsverzeichnis.liste()),
                "unerfasst": len(erkennungsverzeichnis.unerfasst()),
            })
            return

        try:
            daten = self._koerper_lesen()
        except (ValueError, UnicodeDecodeError) as fehler:
            self._fehler(HTTPStatus.BAD_REQUEST, f"Nachricht nicht lesbar: {fehler}")
            return

        if pfad == "/api/v1/register":
            try:
                eintrag = verzeichnis.eintragen(daten, self._absender_ip())
            except ValueError as fehler:
                self._fehler(HTTPStatus.BAD_REQUEST, str(fehler))
                return
            agenten_ziele_schreiben()
            self._json(HTTPStatus.OK, {
                "status": "registriert",
                "geraet": eintrag["hostname"],
                "dashboard": f"http://{MON_HOSTNAME}/",
                "naechste_meldung_in_sekunden": 3600,
            })
            return

        if pfad == "/api/v1/heartbeat":
            if verzeichnis.lebenszeichen(daten.get("hostname", "")):
                self._json(HTTPStatus.OK, {"status": "angenommen"})
            else:
                self._fehler(HTTPStatus.NOT_FOUND, "Unbekanntes Geraet – bitte neu registrieren")
            return

        self._fehler(HTTPStatus.NOT_FOUND, "Unbekannter Pfad")

    # ---------------- Auslieferung ---------------- #
    def _datei_ausliefern(self, name: str) -> None:
        if "/" in name or "\\" in name or name.startswith("."):
            self._fehler(HTTPStatus.BAD_REQUEST, "Ungueltiger Dateiname")
            return
        datei = DIST_VERZEICHNIS / name
        if not datei.is_file():
            self._fehler(HTTPStatus.NOT_FOUND, f"{name} steht nicht bereit")
            return
        inhalt = datei.read_bytes()
        typ = "application/octet-stream"
        if name.endswith(".ps1"):
            typ = "text/plain; charset=utf-8"
        elif name.endswith(".sh"):
            typ = "text/x-shellscript; charset=utf-8"
        self._antwort(HTTPStatus.OK, inhalt, typ)

    def _konfiguration_ausliefern(self, was: str) -> None:
        if was not in ("alloy-server", "alloy-client"):
            self._fehler(HTTPStatus.NOT_FOUND, "Unbekannte Konfiguration")
            return
        datei = DIST_VERZEICHNIS / f"{was}.alloy"
        if not datei.is_file():
            self._fehler(HTTPStatus.NOT_FOUND, f"{was}.alloy steht nicht bereit")
            return
        self._antwort(HTTPStatus.OK, datei.read_bytes(), "text/plain; charset=utf-8")


def main() -> None:
    ZIEL_VERZEICHNIS.mkdir(parents=True, exist_ok=True)
    ZUSTAND_DATEI.parent.mkdir(parents=True, exist_ok=True)
    GEFUNDEN_ZUSTAND.parent.mkdir(parents=True, exist_ok=True)

    # Leere Ziellisten sofort anlegen, damit Prometheus beim ersten Start
    # nicht ueber fehlende Dateien stolpert.
    for datei in ("icmp-gefunden.json", "snmp-gefunden.json"):
        if not (ZIEL_VERZEICHNIS / datei).exists():
            ziel_schreiben(datei, [])

    if not AGENT_TOKEN:
        log.warning("AGENT_TOKEN ist leer – jeder im Netz kann sich anmelden!")

    threading.Thread(target=hintergrundarbeit, daemon=True).start()
    threading.Thread(target=erkennungsarbeit, daemon=True).start()

    server = ThreadingHTTPServer(("0.0.0.0", HORCH_PORT), Handler)
    server.daemon_threads = True
    log.info("Registrierungs-Dienst horcht auf Port %d", HORCH_PORT)
    server.serve_forever()


if __name__ == "__main__":
    main()
