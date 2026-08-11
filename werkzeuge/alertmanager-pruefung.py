#!/usr/bin/env python3
"""Rendert die Alertmanager-Vorlage mit Beispielwerten und laesst sie
von amtool pruefen. Ohne das Rendern liesse sich die Datei nicht
pruefen, weil sie Platzhalter enthaelt."""
import os, pathlib, re, subprocess, sys, tempfile, shutil

BEISPIEL = {
    "SMTP_HOST": "smtp.schule.local", "SMTP_PORT": "587",
    "SMTP_VON": "monitoring@schule.local", "SMTP_BENUTZER": "mon",
    "SMTP_PASSWORT": "geheim", "SMTP_TLS": "true", "MON_HOSTNAME": "10.0.0.50",
    "ALARM_EMPFAENGER": "admin@schule.local",
    "ALARM_EMPFAENGER_KRITISCH": "admin@schule.local,handy@schule.local",
    "ALARM_EMPFAENGER_SECURITY": "sicherheit@schule.local",
}

def main():
    amtool = sys.argv[1] if len(sys.argv) > 1 else shutil.which("amtool")
    if not amtool or not pathlib.Path(amtool).exists():
        print("amtool nicht gefunden"); return 2

    wurzel = pathlib.Path(__file__).resolve().parent.parent
    vorlage = wurzel / "stack" / "alertmanager" / "alertmanager.yml.tmpl"
    text = re.sub(r"\$\{(\w+)\}", lambda m: BEISPIEL.get(m.group(1), ""),
                  vorlage.read_text(encoding="utf-8"))
    text = text.replace("#WEBHOOK#\n", "")

    with tempfile.TemporaryDirectory() as tmp:
        ordner = pathlib.Path(tmp)
        (ordner / "vorlagen").mkdir()
        for v in (wurzel / "stack" / "alertmanager" / "vorlagen").glob("*.tmpl"):
            shutil.copy(v, ordner / "vorlagen" / v.name)
        ziel = ordner / "alertmanager.yml"
        ziel.write_text(text.replace("/etc/alertmanager/", f"{ordner}/"), encoding="utf-8")

        e = subprocess.run([amtool, "check-config", str(ziel)],
                           capture_output=True, text=True)
        print((e.stdout + e.stderr).strip())
        return e.returncode

sys.exit(main())
