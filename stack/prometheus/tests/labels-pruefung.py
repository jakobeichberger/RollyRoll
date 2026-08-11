#!/usr/bin/env python3
"""
Prueft, ob die Meldungstexte der Alarme nur Labels benutzen, die auf den
Metriken der jeweiligen Regel auch wirklich vorkommen.

Der Hintergrund: In einem Alarmtext steht

    Datentraeger {{ $labels.modell }} (Seriennummer {{ $labels.seriennummer }})

Setzt der Sammler diese Labels auf genau dieser Metrik nicht, rendert
Prometheus stillschweigend eine leere Zeichenkette. In der Alarm-Mail
steht dann

    "Datentraeger  (Seriennummer ) in srv-datei-01 meldet ..."

Nichts geht kaputt, nichts faellt auf – und im Ernstfall fehlt genau die
Angabe, die man gebraucht haette. Genau diese Sorte Fehler findet man
sonst erst, wenn der erste echte Alarm kommt.

Die Pruefung ist metrikgenau: Fuer jede Regel werden die Metriken aus dem
Ausdruck herausgeloest und nur deren Labels als zulaessig betrachtet.

    python3 stack/prometheus/tests/labels-pruefung.py
"""

from __future__ import annotations

import pathlib
import re
import sys

import yaml

WURZEL = pathlib.Path(__file__).resolve().parents[3]
REGELN = WURZEL / "stack" / "prometheus" / "rules"

# Von Prometheus selbst bzw. aus den Ziellisten. Liegen auf jeder Reihe an.
GRUNDLABELS = {
    "geraet", "instance", "job", "standort", "alertname", "severity",
    "kategorie", "vorwarnung", "baseline", "typ", "rolle", "raum",
    "adresse", "modul", "auth", "kritisch", "hersteller", "modell",
    "erkannt", "geraet_adresse", "ziel", "dienst", "betriebssystem",
    "domaene", "agent_version", "value",
}

# Labels von Fremd-Exportern. Die kann dieses Skript nicht aus dem
# Quelltext ableiten, deshalb stehen sie hier – mit Angabe woher, damit
# nachvollziehbar bleibt, warum sie als vorhanden gelten.
FREMDLABELS = {
    # windows_exporter
    "volume": "windows_logical_disk", "nic": "windows_net",
    "printer": "windows_printer", "name": "windows_service",
    "state": "windows_service", "mode": "windows_cpu",
    "core": "windows_cpu", "collector": "windows_exporter_*",
    # node_exporter
    "mountpoint": "node_filesystem", "device": "node_*",
    "fstype": "node_filesystem", "nodename": "node_uname_info",
    # snmp_exporter (if_mib und eigene Module aus snmp-schule.yml)
    "ifName": "if_mib", "ifAlias": "if_mib", "ifDescr": "if_mib",
    "ifIndex": "if_mib",
    "fgHwSensorEntName": "snmp-schule.yml fortigate_schule",
    "fgHwSensorEntIndex": "snmp-schule.yml fortigate_schule",
    "fgVpnTunEntPhase1Name": "snmp-schule.yml fortigate_schule",
    # unpoller
    "radio": "unpoller_radio_*", "vap": "unpoller_vap_*",
    "site_name": "unpoller_*", "ssid": "unpoller_vap_*",
    # fortigate_exporter
    "alias": "fortigate_interface_*", "vdom": "fortigate_*",
    "protocol": "fortigate_*",
    # Prometheus selbst
    "rule_group": "prometheus_rule_*", "reason": "prometheus_*",
    # blackbox_exporter
    "phase": "probe_http_duration_seconds",
}


def metrik_labels_aus_agenten() -> dict[str, set[str]]:
    """
    Liest aus den PowerShell-Sammlern heraus, welche Labels je Metrik
    tatsaechlich gesetzt werden.

    Erfasst beide Schreibweisen:
        -Labels @{ a = 1; b = 2 }
        -Labels ($labels + @{ c = 3 })      (mit $labels = @{...} davor)
    """
    ergebnis: dict[str, set[str]] = {}

    for datei in (WURZEL / "agent").glob("*.ps1"):
        text = datei.read_text(encoding="utf-8")

        # Zwischenvariablen wie $labels / $basis einsammeln
        vorbereitet: dict[str, set[str]] = {}
        for name, block in re.findall(r"\$(\w+)\s*=\s*@\{([^}]*)\}", text):
            vorbereitet[name] = set(re.findall(r"(\w+)\s*=", block))

        # Add-Metrik-Aufrufe einsammeln. In PowerShell geht ein Aufruf
        # ueber mehrere Zeilen weiter, solange die Zeile auf einen
        # Backtick endet. Genau daran wird das Ende erkannt – ein
        # einfacher Ausdruck bis zur naechsten schliessenden Klammer
        # bricht am Ende des Label-Blocks ab und uebersieht die Labels.
        zeilen = text.splitlines()
        for nr, zeile in enumerate(zeilen):
            treffer = re.search(r"Add-Metrik\s+-Name\s+'([^']+)'", zeile)
            if not treffer:
                continue
            metrik = treffer.group(1)

            # Der Aufruf laeuft weiter, solange die Zeile auf einen
            # Backtick endet ODER noch eine Klammer offen ist. Innerhalb
            # einer Hashtabelle braucht PowerShell keinen Backtick –
            # ohne die Klammerzaehlung bricht die Erfassung genau dort ab.
            aufruf = [zeile]
            tiefe = zeile.count("{") - zeile.count("}")
            i = nr
            while i < len(zeilen) - 1 and (
                tiefe > 0 or zeilen[i].rstrip().endswith("`")
            ):
                i += 1
                aufruf.append(zeilen[i])
                tiefe += zeilen[i].count("{") - zeilen[i].count("}")
            ganzer = "\n".join(aufruf)

            labels: set[str] = set()
            for block in re.findall(r"@\{([^}]*)\}", ganzer, re.S):
                labels |= set(re.findall(r"(?:^|[;{])\s*(\w+)\s*=", block, re.M))
            for var in re.findall(r"-Labels\s+\(?\s*\$(\w+)", ganzer):
                labels |= vorbereitet.get(var, set())
            ergebnis.setdefault(metrik, set()).update(labels)

    return ergebnis


def metrik_labels_aus_diensten() -> dict[str, set[str]]:
    """Dasselbe fuer den Registrierungs-Dienst und die Konfigurationssicherung."""
    ergebnis: dict[str, set[str]] = {}

    for datei in (WURZEL / "stack" / "registrar").glob("*.py"):
        text = datei.read_text(encoding="utf-8")
        # Lange f-Strings sind ueber mehrere Zeilen aneinandergehaengt:
        #     f'schule_x{{a="..",'
        #     f'b=".."}} 1'
        # Zum Auswerten die Nahtstellen entfernen, sonst faellt jedes
        # Label hinter dem ersten Zeilenumbruch unter den Tisch.
        text = re.sub(r"'\s*\n\s*f'", "", text)
        text = re.sub(r'"\s*\n\s*f"', "", text)

        # In f-Strings steht {{ fuer eine echte Klammer und {...} fuer
        # eingesetzte Werte. Die Einsetzungen muessen raus, sonst endet
        # die Labelsuche schon beim ersten eingesetzten Wert.
        text = text.replace("{{", "\x01").replace("}}", "\x02")
        while True:
            gekuerzt = re.sub(r"\{[^{}]*\}", "", text)
            if gekuerzt == text:
                break
            text = gekuerzt
        text = text.replace("\x01", "{").replace("\x02", "}")

        for metrik, labelteil in re.findall(r"(schule_[a-z0-9_]+)\{([^}]*)\}", text):
            ergebnis.setdefault(metrik, set()).update(re.findall(r"(\w+)=", labelteil))
        for metrik in re.findall(r'"(schule_[a-z0-9_]+)"', text):
            ergebnis.setdefault(metrik, set())

    text = (WURZEL / "stack" / "konfig-sichern.sh").read_text(encoding="utf-8")
    for metrik, labelteil in re.findall(r"(schule_[a-z0-9_]+)\{([^}]*)\}", text):
        ergebnis.setdefault(metrik, set()).update(re.findall(r"(\w+)=", labelteil))
    for metrik in re.findall(r"(schule_konfig_[a-z_]+) ", text):
        ergebnis.setdefault(metrik, set())

    return ergebnis


def aufzeichnungsregeln() -> dict[str, set[str]]:
    """Aufzeichnungsregeln erben die Labels ihrer Gruppierung."""
    ergebnis: dict[str, set[str]] = {}
    for datei in REGELN.glob("*.yml"):
        daten = yaml.safe_load(datei.read_text(encoding="utf-8"))
        for gruppe in daten.get("groups", []):
            for regel in gruppe.get("rules", []):
                if "record" not in regel:
                    continue
                labels = set(GRUNDLABELS)
                for teil in re.findall(r"by\s*\(([^)]*)\)", regel["expr"]):
                    labels |= {t.strip() for t in teil.split(",") if t.strip()}
                ergebnis[regel["record"]] = labels
    return ergebnis


def main() -> int:
    bekannt: dict[str, set[str]] = {}
    for quelle in (metrik_labels_aus_agenten(), metrik_labels_aus_diensten(),
                   aufzeichnungsregeln()):
        for metrik, labels in quelle.items():
            bekannt.setdefault(metrik, set()).update(labels)

    befunde: list[str] = []
    geprueft = 0
    ungeprueft = 0

    for datei in sorted(REGELN.glob("*.yml")):
        daten = yaml.safe_load(datei.read_text(encoding="utf-8"))
        for gruppe in daten.get("groups", []):
            for regel in gruppe.get("rules", []):
                if "alert" not in regel:
                    continue

                text = " ".join((regel.get("annotations") or {}).values())
                benutzt = set(re.findall(r"\$labels\.(\w+)", text))
                if not benutzt:
                    continue

                # Welche Metriken kommen im Ausdruck vor?
                metriken = set(re.findall(r"\b([a-z][a-z0-9_]*(?::[a-z0-9_:]+)?)\s*[{\[(]",
                                          regel["expr"]))
                metriken |= set(re.findall(r"\b(schule[a-z0-9_:]*)\b", regel["expr"]))
                eigene = {m for m in metriken if m in bekannt}

                if not eigene:
                    # Reine Fremd-Exporter-Regel: nur gegen die Liste pruefen
                    ungeprueft += 1
                    erlaubt = GRUNDLABELS | set(FREMDLABELS)
                else:
                    geprueft += 1
                    erlaubt = set(GRUNDLABELS) | set(FREMDLABELS)
                    for m in eigene:
                        erlaubt |= bekannt[m]

                fehlend = sorted(benutzt - erlaubt)
                if fehlend:
                    quellen = ", ".join(sorted(eigene)) or "(nur Fremdmetriken)"
                    befunde.append(
                        f"  {datei.name:30} {regel['alert']:34} benutzt {fehlend}\n"
                        f"  {'':30} Metriken der Regel: {quellen}"
                    )

    print(f"{geprueft} Alarme metrikgenau geprueft, {ungeprueft} gegen die "
          f"Fremdlabel-Liste, {len(bekannt)} Metriken bekannt\n")

    if befunde:
        print("Meldungstexte mit Labels, die auf ihren Metriken nicht vorkommen:")
        print("(rendern in der Alarm-Mail als Leerstelle)\n")
        print("\n\n".join(befunde))
        print(f"\n{len(befunde)} Befund(e)")
        return 1

    print("Alle Meldungstexte benutzen ausschliesslich vorhandene Labels.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
