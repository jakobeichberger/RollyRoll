#!/usr/bin/env python3
"""
Prueft die PowerShell-Skripte auf die Fehlerklassen, die hier schon
zugeschlagen haben:

  * PowerShell-7-Syntax (?? , ?. , ternaerer Operator, -Parallel).
    Auf dem Hyper-V-Server laeuft 5.1 – das faellt sonst erst dort auf.
  * Zuweisungen an automatische Variablen wie $profile oder $host.
  * Fremdzeichen, die aussehen wie Ziffern, aber keine sind. Eine
    Devanagari-Ziffer in einer Farbangabe hat hier schon einmal gesteckt.
  * Unausgeglichene Klammern.

Zeichenketten und Here-Strings werden ausgenommen – dort ist alles
erlaubt.
"""
import pathlib, re, sys

def code_zeilen(text):
    drin = False
    for nr, z in enumerate(text.splitlines(), 1):
        s = z.strip()
        if not drin and (s.endswith('@"') or s.endswith("@'")):
            drin = True; continue
        if drin and s in ('"@', "'@"):
            drin = False; continue
        if not drin:
            yield nr, z

def ohne_text(zeile):
    z = re.sub(r"'[^']*'", "''", zeile)
    z = re.sub(r'"[^"]*"', '""', z)
    return re.sub(r"#.*$", "", z)

AUTOMATISCH = ("profile", "host", "error", "input", "args", "matches", "pwd", "pid")

def main():
    wurzel = pathlib.Path(__file__).resolve().parent.parent
    befunde = []
    dateien = sorted(list(wurzel.rglob("*.ps1")) + list(wurzel.rglob("*.psm1")))
    dateien = [d for d in dateien if ".git" not in str(d)]

    for p in dateien:
        text = p.read_text(encoding="utf-8")
        rel = p.relative_to(wurzel)

        for nr, zeile in code_zeilen(text):
            o = ohne_text(zeile)
            if re.search(r"\?\?|\?\.", o):
                befunde.append(f"{rel}:{nr}: ?? oder ?. gibt es erst ab PowerShell 7")
            # Ternaer: <bedingung> ? <a> : <b>. Nicht auf eine Klammer
            # davor verlassen - "$x = $a -eq 4 ? $true : $false" hat keine.
            # Ausgenommen: "? {" ist der Kurzname von Where-Object, und
            # "::" ist ein statischer Zugriff.
            if (re.search(r"\s\?\s", o) and re.search(r"\s:\s", o)
                    and "::" not in o and not re.search(r"\|\s*\?\s*\{", o)):
                befunde.append(f"{rel}:{nr}: ternaerer Operator, erst ab PowerShell 7")
            if re.search(r"\bForEach-Object\s+-Parallel\b", o):
                befunde.append(f"{rel}:{nr}: -Parallel gibt es erst ab PowerShell 7")
            for a in AUTOMATISCH:
                if re.search(rf"\$\b{a}\b\s*=(?!=)", o, re.I):
                    befunde.append(f"{rel}:{nr}: ${a} ist eine automatische Variable")

        for nr, zeile in enumerate(text.splitlines(), 1):
            for c in zeile:
                if ord(c) > 127 and not ("À" <= c <= "ÿ") and c not in "–—„“”…·":
                    befunde.append(f"{rel}:{nr}: verdaechtiges Zeichen U+{ord(c):04X} {c!r}")
                    break

        bilanz = {"{": 0, "(": 0, "[": 0}
        paare = {"}": "{", ")": "(", "]": "["}
        for _, zeile in code_zeilen(text):
            for c in ohne_text(zeile):
                if c in bilanz: bilanz[c] += 1
                elif c in paare: bilanz[paare[c]] -= 1
        if any(bilanz.values()):
            befunde.append(f"{rel}: Klammern unausgeglichen {bilanz}")

        # Alle script-Variablen muessen zugewiesen werden
        gelesen = set(re.findall(r"\$script:(\w+)", text))
        gesetzt = set(re.findall(r"\$script:(\w+)\s*=", text))
        for name in sorted(gelesen - gesetzt):
            befunde.append(f"{rel}: $script:{name} wird gelesen, aber nie zugewiesen")

    if befunde:
        print("\n".join(befunde))
        return 1
    print(f"{len(dateien)} PowerShell-Dateien ohne Befund")
    return 0

sys.exit(main())
