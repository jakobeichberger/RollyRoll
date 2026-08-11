#!/usr/bin/env python3
"""Prueft, dass jede in Regeln und Dashboards benutzte schule_-Metrik
auch irgendwo erzeugt wird - und umgekehrt, dass keine erzeugte Metrik
voellig ungenutzt bleibt (das waere unnoetige Last auf jedem Geraet)."""
import json, os, pathlib, re, sys

def main():
    wurzel = pathlib.Path(__file__).resolve().parent.parent
    sys.path.insert(0, str(wurzel / "stack" / "registrar"))
    os.environ.setdefault("AGENT_TOKEN", "pruefung")

    erzeugt = set()
    import app
    erzeugt |= set(re.findall(r"\bschule_[a-z0-9_]+", app.metriken()))
    for f in (wurzel / "agent").glob("*.ps1"):
        erzeugt |= set(re.findall(r"schule_[a-z0-9_]+", f.read_text(encoding="utf-8")))
    erzeugt |= set(re.findall(r"schule_[a-z0-9_]+",
                   (wurzel / "stack" / "konfig-sichern.sh").read_text(encoding="utf-8")))
    for f in (wurzel / "stack" / "prometheus" / "rules").glob("*.yml"):
        erzeugt |= set(re.findall(r"record:\s*(schule[a-z0-9_:]+)", f.read_text(encoding="utf-8")))

    benutzt = set()
    for f in (wurzel / "stack" / "prometheus" / "rules").glob("*.yml"):
        benutzt |= set(re.findall(r"\bschule[a-z0-9_:]*", f.read_text(encoding="utf-8")))
    for f in (wurzel / "stack" / "grafana" / "dashboards").glob("*.json"):
        benutzt |= set(re.findall(r"\bschule[a-z0-9_:]*", f.read_text(encoding="utf-8")))
    for f in (wurzel / "stack" / "loki" / "rules").rglob("*.yml"):
        benutzt |= set(re.findall(r"\bschule[a-z0-9_:]*", f.read_text(encoding="utf-8")))

    fehlend = sorted(m for m in benutzt if m.startswith("schule_") and m not in erzeugt)
    if fehlend:
        print("Benutzt, aber nirgends erzeugt:")
        for m in fehlend: print(f"  {m}")
        return 1
    print(f"{len(benutzt)} benutzte Metriken, alle erzeugt "
          f"({len(erzeugt)} insgesamt bekannt)")
    return 0

sys.exit(main())
