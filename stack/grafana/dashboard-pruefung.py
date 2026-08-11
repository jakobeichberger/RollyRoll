#!/usr/bin/env python3
"""
Prueft die Dashboards gegen die Hausregeln.

Der Sinn: Dashboards verfallen leise. Ein Panel ohne Einheit, ein
fehlender Verweis, eine Tabelle ohne "instant" – einzeln jeweils
harmlos, zusammen wird daraus ein Dashboard, das im Ernstfall niemand
lesen will. Diese Pruefung laeuft in Sekunden und faengt das ab.

    python3 stack/grafana/dashboard-pruefung.py

Rueckgabewert 0 = alles in Ordnung, 1 = Befunde.

Die Regeln folgen den ueblichen Empfehlungen fuer Ueberwachungs-
Dashboards (Grafana-Leitfaden, USE- und RED-Methode) und ein paar
eigenen, die sich in dieser Umgebung als noetig erwiesen haben.
"""

from __future__ import annotations

import json
import pathlib
import re
import sys

VERZEICHNIS = pathlib.Path(__file__).parent / "dashboards"

# Einheiten, die ohne Angabe durchgehen: reine Zaehlwerte und Texttafeln.
# Zustandsdarstellungen zeigen Zustaende, keine Groessen – eine Einheit
# waere dort sinnlos. Dasselbe gilt fuer Panels mit Wertzuordnung
# (mappings): "1 = in Ordnung" ist aussagekraeftiger als jede Einheit.
OHNE_EINHEIT_ERLAUBT = {
    "table", "logs", "text", "row", "nodeGraph", "news",
    "state-timeline", "status-history", "piechart",
}
# Panelarten, bei denen ein Schwellwert erwartet wird – ohne ihn ist die
# Farbe bedeutungslos und die Kachel damit reine Dekoration.
BRAUCHT_SCHWELLWERT = {"stat", "gauge", "bargauge"}

# Dashboards, die als Einstieg gedacht sind, brauchen einen kurzen
# Zeitraum. Wer "ist gerade etwas kaputt" fragt, will nicht 30 Tage sehen.
EINSTIEG = {"schule-uebersicht"}


class Befund:
    def __init__(self, datei: str, schwere: str, panel: str, text: str) -> None:
        self.datei, self.schwere, self.panel, self.text = datei, schwere, panel, text

    def __str__(self) -> str:
        ort = f" [{self.panel}]" if self.panel else ""
        return f"  {self.schwere:7} {self.datei}{ort}: {self.text}"


def alle_panels(liste):
    for p in liste:
        yield p
        yield from alle_panels(p.get("panels", []))


def pruefe(pfad: pathlib.Path) -> list[Befund]:
    befunde: list[Befund] = []
    d = json.loads(pfad.read_text(encoding="utf-8"))
    name = pfad.name

    def melde(schwere, panel, text):
        befunde.append(Befund(name, schwere, panel, text))

    # ---------------- Kopfdaten ----------------
    for feld in ("uid", "title", "description", "tags"):
        if not d.get(feld):
            melde("FEHLER", "", f"Kopfdaten: {feld} fehlt")

    if d.get("timezone") != "browser":
        melde("HINWEIS", "", "timezone sollte 'browser' sein")
    if not d.get("refresh"):
        melde("WARNUNG", "", "kein automatisches Neuladen (refresh) gesetzt")
    if "schule" not in (d.get("tags") or []):
        melde("HINWEIS", "", "Kennzeichnung 'schule' fehlt in tags")

    # Einstiegs-Dashboards brauchen einen kurzen Zeitraum
    von = (d.get("time") or {}).get("from", "")
    if d.get("uid") in EINSTIEG and von not in ("now-1h", "now-3h", "now-6h", "now-12h", "now-24h"):
        melde("WARNUNG", "", f"Einstiegs-Dashboard mit Zeitraum {von} – kuerzer waere besser")

    # ---------------- Navigation ----------------
    if not d.get("links"):
        melde("WARNUNG", "", "keine Verweise auf die anderen Dashboards")

    liste = (d.get("annotations") or {}).get("list") or []
    if not any("alert" in json.dumps(a).lower() for a in liste):
        melde("WARNUNG", "", "keine Alarm-Einblendung (annotations) eingerichtet")

    # ---------------- Panels ----------------
    panels = list(alle_panels(d.get("panels", [])))
    kennungen = [p.get("id") for p in panels]
    doppelt = {k for k in kennungen if kennungen.count(k) > 1}
    if doppelt:
        melde("FEHLER", "", f"doppelte Panel-Kennungen: {sorted(doppelt)}")

    for p in panels:
        art = p.get("type", "")
        titel = p.get("title", "?")
        if art == "row":
            continue

        if not p.get("description"):
            melde("WARNUNG", titel, "keine Beschreibung – ein Vertreter versteht das Panel nicht")

        vorgaben = (p.get("fieldConfig") or {}).get("defaults") or {}

        if (art not in OHNE_EINHEIT_ERLAUBT
                and not vorgaben.get("unit")
                and not vorgaben.get("mappings")):
            melde("WARNUNG", titel, "keine Einheit gesetzt")

        if art in BRAUCHT_SCHWELLWERT:
            stufen = ((vorgaben.get("thresholds") or {}).get("steps") or [])
            fest = (vorgaben.get("color") or {}).get("mode") == "fixed"
            zuordnung = vorgaben.get("mappings")
            if len(stufen) < 2 and not fest and not zuordnung:
                melde("WARNUNG", titel, "Schwellwert fehlt – die Farbe sagt nichts aus")

        for ziel in p.get("targets", []):
            if not ziel.get("expr"):
                continue
            if ziel.get("format") == "table" and not ziel.get("instant"):
                melde("FEHLER", titel,
                      "Tabellenabfrage ohne instant=true – laedt den ganzen Zeitraum")
            if art in ("stat", "gauge", "bargauge", "piechart") and not ziel.get("instant"):
                melde("HINWEIS", titel, "Kennzahl ohne instant=true – unnoetig viele Datenpunkte")

        if art == "timeseries":
            if not (p.get("options") or {}).get("legend"):
                melde("HINWEIS", titel, "keine Legende eingerichtet")

        # Datenquelle muss ausdruecklich benannt sein, sonst greift die
        # Vorgabe des Servers – und die kann sich aendern.
        if p.get("datasource") is None:
            melde("FEHLER", titel, "keine Datenquelle angegeben")

    # ---------------- Variablen ----------------
    for v in (d.get("templating") or {}).get("list") or []:
        if v.get("type") == "query" and not v.get("refresh"):
            melde("WARNUNG", f"Variable {v.get('name')}",
                  "kein refresh gesetzt – die Auswahl veraltet")
        if not v.get("label"):
            melde("HINWEIS", f"Variable {v.get('name')}", "keine Beschriftung")

    return befunde


def main() -> int:
    dateien = sorted(VERZEICHNIS.glob("*.json"))
    if not dateien:
        print("Keine Dashboards gefunden.")
        return 1

    alle: list[Befund] = []
    for pfad in dateien:
        alle.extend(pruefe(pfad))

    nach_schwere = {"FEHLER": [], "WARNUNG": [], "HINWEIS": []}
    for b in alle:
        nach_schwere[b.schwere].append(b)

    for schwere in ("FEHLER", "WARNUNG", "HINWEIS"):
        if nach_schwere[schwere]:
            print(f"\n{schwere} ({len(nach_schwere[schwere])})")
            for b in nach_schwere[schwere]:
                print(b)

    print(f"\n{len(dateien)} Dashboards geprueft: "
          f"{len(nach_schwere['FEHLER'])} Fehler, "
          f"{len(nach_schwere['WARNUNG'])} Warnungen, "
          f"{len(nach_schwere['HINWEIS'])} Hinweise")

    return 1 if nach_schwere["FEHLER"] or nach_schwere["WARNUNG"] else 0


if __name__ == "__main__":
    sys.exit(main())
