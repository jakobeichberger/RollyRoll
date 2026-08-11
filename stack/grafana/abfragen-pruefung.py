#!/usr/bin/env python3
"""
Prueft jede Abfrage aus den Dashboards auf gueltiges PromQL.

Bisher pruefte promtool nur die Alarmregeln. Die Abfragen in den
Dashboards sind aber genauso PromQL – und ein Tippfehler dort faellt
erst auf, wenn jemand das Panel oeffnet und ein leeres Feld sieht.

Der Trick: Jede Abfrage wird in eine Wegwerf-Regeldatei verpackt und
durch promtool geschickt. Damit prueft genau der Ausdrucksparser, der
spaeter auch in Prometheus laeuft.

Grafana-Variablen ($geraet, $__rate_interval, ...) werden vorher durch
gueltige Platzhalter ersetzt – sonst scheitert die Pruefung an etwas,
das zur Laufzeit von Grafana eingesetzt wird.

    python3 stack/grafana/abfragen-pruefung.py [pfad/zu/promtool]

LogQL-Abfragen (Datenquelle Loki) werden uebersprungen: Dafuer gibt es
kein entsprechendes Werkzeug, das ohne laufenden Loki auskommt.
"""

from __future__ import annotations

import json
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

VERZEICHNIS = pathlib.Path(__file__).parent / "dashboards"

# Grafana setzt diese zur Laufzeit ein. Fuer die Syntaxpruefung werden
# gueltige Werte eingesetzt, die den Ausdruck nicht veraendern.
PLATZHALTER = [
    (r"\$__rate_interval", "5m"),
    (r"\$__interval", "5m"),
    (r"\$__range", "1h"),
    (r"\$__auto_interval\w*", "5m"),
    (r"\[\$__auto\]", "[5m]"),
    (r"\$__from", "0"),
    (r"\$__to", "0"),
    # Benutzervariablen: alles Uebrige der Form $name
    (r"\$\{(\w+)(:\w+)?\}", "platzhalter"),
    (r"\$(\w+)", "platzhalter"),
]


def aufloesen(ausdruck: str) -> str:
    for muster, ersatz in PLATZHALTER:
        ausdruck = re.sub(muster, ersatz, ausdruck)
    return ausdruck


def alle_panels(liste):
    for p in liste:
        yield p
        yield from alle_panels(p.get("panels", []))


def sammeln() -> tuple[list[tuple[str, str, str]], int]:
    """Liefert (Datei, Panel, Ausdruck) fuer alle Prometheus-Abfragen."""
    abfragen: list[tuple[str, str, str]] = []
    uebersprungen = 0

    for pfad in sorted(VERZEICHNIS.glob("*.json")):
        d = json.loads(pfad.read_text(encoding="utf-8"))

        for panel in alle_panels(d.get("panels", [])):
            panel_quelle = (panel.get("datasource") or {}).get("uid", "")
            for ziel in panel.get("targets", []):
                ausdruck = ziel.get("expr")
                if not ausdruck:
                    continue
                quelle = (ziel.get("datasource") or {}).get("uid", "") or panel_quelle
                if quelle == "loki":
                    uebersprungen += 1
                    continue
                abfragen.append((pfad.name, panel.get("title", "?"), ausdruck))

        # Variablen vom Typ "query" enthalten ebenfalls PromQL
        for v in (d.get("templating") or {}).get("list") or []:
            if v.get("type") != "query":
                continue
            quelle = (v.get("datasource") or {}).get("uid", "")
            roh = v.get("query")
            if isinstance(roh, dict):
                roh = roh.get("query", "")
            if not roh:
                continue
            if quelle == "loki":
                uebersprungen += 1
                continue
            # label_values(...) ist Grafana-eigen; der innere Teil ist PromQL
            treffer = re.match(r"label_values\((.+),\s*\w+\)\s*$", roh.strip())
            if treffer:
                roh = treffer.group(1)
            elif roh.strip().startswith("label_values("):
                continue        # label_values(label) ohne Metrik – kein PromQL
            abfragen.append((pfad.name, f"Variable {v.get('name')}", roh))

    return abfragen, uebersprungen


def main() -> int:
    promtool = sys.argv[1] if len(sys.argv) > 1 else shutil.which("promtool")
    if not promtool or not pathlib.Path(promtool).exists():
        print("promtool nicht gefunden.")
        print("Pfad als Argument uebergeben oder promtool in den PATH legen.")
        return 2

    abfragen, uebersprungen = sammeln()
    if not abfragen:
        print("Keine Prometheus-Abfragen gefunden.")
        return 1

    # Alles in eine einzige Wegwerf-Regeldatei – ein Aufruf statt hundert
    regeln = []
    for nr, (_, _, ausdruck) in enumerate(abfragen):
        regeln.append({"record": f"pruefung:abfrage{nr}", "expr": aufloesen(ausdruck)})

    with tempfile.NamedTemporaryFile("w", suffix=".yml", delete=False,
                                     encoding="utf-8") as f:
        json.dump({"groups": [{"name": "dashboardabfragen", "rules": regeln}]}, f)
        temp = f.name

    ergebnis = subprocess.run([promtool, "check", "rules", temp],
                              capture_output=True, text=True)
    pathlib.Path(temp).unlink(missing_ok=True)

    if ergebnis.returncode == 0:
        print(f"{len(abfragen)} Prometheus-Abfragen aus {len(list(VERZEICHNIS.glob('*.json')))} "
              f"Dashboards geprueft: alle syntaktisch in Ordnung")
        print(f"({uebersprungen} Loki-Abfragen uebersprungen – dafuer gibt es kein "
              f"Pruefwerkzeug ohne laufenden Loki)")
        return 0

    # Aus der promtool-Meldung die betroffene Abfrage heraussuchen
    print("Fehlerhafte Abfragen:\n")
    ausgabe = ergebnis.stdout + ergebnis.stderr
    print(ausgabe)
    for nr, (datei, panel, ausdruck) in enumerate(abfragen):
        if f"abfrage{nr}" in ausgabe:
            print(f"  {datei} [{panel}]")
            print(f"    {ausdruck}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
