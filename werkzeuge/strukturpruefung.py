#!/usr/bin/env python3
"""Prueft, ob alle YAML- und JSON-Dateien lesbar sind und keine
doppelten Dashboard-Kennungen vorkommen."""
import json, pathlib, sys, yaml

def main():
    wurzel = pathlib.Path(__file__).resolve().parent.parent
    befunde, yamls, jsons = [], 0, 0

    for p in list(wurzel.rglob("*.yml")) + list(wurzel.rglob("*.yaml")):
        if ".git" in str(p) or p.name.endswith(".tmpl") or ".werkzeuge" in str(p):
            continue
        try:
            yaml.safe_load(p.read_text(encoding="utf-8")); yamls += 1
        except yaml.YAMLError as e:
            befunde.append(f"{p.relative_to(wurzel)}: {str(e).splitlines()[0]}")

    uids, titel = {}, {}
    for p in wurzel.rglob("*.json"):
        if ".git" in str(p) or ".werkzeuge" in str(p):
            continue
        try:
            d = json.loads(p.read_text(encoding="utf-8")); jsons += 1
        except json.JSONDecodeError as e:
            befunde.append(f"{p.relative_to(wurzel)}: {e}"); continue
        if "dashboards" in str(p) and isinstance(d, dict):
            uids.setdefault(d.get("uid"), []).append(p.name)
            titel.setdefault(d.get("title"), []).append(p.name)

    for u, v in uids.items():
        if len(v) > 1: befunde.append(f"doppelte Dashboard-Kennung {u}: {v}")
    for t, v in titel.items():
        if len(v) > 1: befunde.append(f"doppelter Dashboard-Titel {t}: {v}")

    if befunde:
        print("\n".join(befunde)); return 1
    print(f"{yamls} YAML- und {jsons} JSON-Dateien in Ordnung")
    return 0

sys.exit(main())
