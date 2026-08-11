#!/usr/bin/env python3
"""Prueft Verweise zwischen den Dokumentationsdateien."""
import pathlib, re, sys

def main():
    wurzel = pathlib.Path(__file__).resolve().parent.parent
    befunde, geprueft = [], 0
    dateien = list((wurzel / "docs").glob("*.md")) + [wurzel / "README.md"]

    for p in dateien:
        text = p.read_text(encoding="utf-8")
        for ziel in re.findall(r"\]\(([^)#\s]+\.md)\)", text):
            geprueft += 1
            pfad = (wurzel / ziel) if ziel.startswith("docs/") else (p.parent / ziel)
            if not pfad.exists():
                befunde.append(f"{p.name} verweist auf {ziel} – gibt es nicht")

    vorhanden = {f.name for f in (wurzel / "docs").glob("*.md")}
    verlinkt = set(re.findall(r"\]\(docs/([^)#\s]+\.md)\)",
                              (wurzel / "README.md").read_text(encoding="utf-8")))
    for f in sorted(vorhanden - verlinkt):
        befunde.append(f"docs/{f} ist im README nirgends verlinkt")

    if befunde:
        print("\n".join(befunde)); return 1
    print(f"{geprueft} Verweise geprueft, alle Ziele vorhanden")
    return 0

sys.exit(main())
