# Fehlersuche

---

## Die Bereitstellung

### Der Installer bleibt bei einer Rückfrage stehen

Auf der Konsole der VM steht sinngemäß *„Continue with autoinstall?"*.

Dann hat der Umbau des Installationsmediums nicht gegriffen – der
Startparameter `autoinstall` fehlt. Einmal bestätigen, dann läuft die
Installation vollständig automatisch weiter; das Ergebnis ist dasselbe.

Damit es beim nächsten Mal ohne Rückfrage klappt, gibt es zwei Wege:

* **Windows ADK installieren** (nur die Bereitstellungstools). Das Skript
  benutzt dann `oscdimg`, was zuverlässiger ist als die Bordmittel:
  <https://learn.microsoft.com/windows-hardware/get-started/adk-install>
* **Fertiges Abbild verwenden:**
  `.\Deploy-MonitoringVM.ps1 -BasisVhdxPfad "D:\Vorlagen\ubuntu-24.04.vhdx" …`

### „Es ist kein virtueller Switch vorhanden"

Im Hyper-V-Manager unter *Manager für virtuelle Switches* einen **externen**
Switch anlegen und mit `-SwitchName "<Name>"` übergeben.

### Die VM startet nicht vom Installationsmedium

Ubuntu braucht die Microsoft-UEFI-Zertifizierungsstelle. Das Skript setzt
das selbst, prüfen lässt es sich so:

```powershell
Get-VMFirmware -VMName SchulMonitoring |
  Select-Object SecureBoot, SecureBootTemplate
# Erwartet: On / MicrosoftUEFICertificateAuthority
```

### Das Skript wartet, aber das Dashboard antwortet nicht

Die VM ist installiert, richtet sich aber noch ein. Auf der VM nachsehen:

```bash
sudo journalctl -u schulmonitoring-einrichtung -f
sudo tail -f /var/log/schulmonitoring-bootstrap.log
```

Häufigste Ursache: langsamer Download der Container-Abbilder. Das ist kein
Fehler, das dauert nur.

### Ein Container-Abbild lässt sich nicht laden

```
Error response from daemon: manifest for … not found
```

Ein Versionsschild in der `.env` gibt es nicht (mehr). Aktuelles Schild auf
Docker Hub nachsehen, in der `.env` eintragen und
`docker compose up -d` erneut ausführen. Alle Versionen stehen gesammelt am
Ende der `.env`, genau dafür.

---

## Agenten

### Ein Gerät erscheint nicht im Dashboard

Der Reihe nach:

**1. Ist der Agent überhaupt gelaufen?**

```powershell
Get-Content "$env:ProgramData\SchulMonitoring\logs\installation-*.log" -Tail 40
```

Existiert die Datei nicht, hat das Startskript nicht ausgelöst:

```powershell
gpresult /scope computer /r     # Wird die GPO angewendet?
```

Typische Ursachen: Die GPO ist an der falschen OU verknüpft, oder das
Skript wurde unter *Skripts* statt unter *PowerShell-Skripts* eingetragen.

**2. Läuft der Dienst?**

```powershell
Get-Service windows_exporter
Invoke-WebRequest http://localhost:9182/metrics -UseBasicParsing |
  Select-Object -ExpandProperty StatusCode
```

**3. Kommt der Server durch?** Von der Monitoring-VM aus:

```bash
curl -s --max-time 5 http://<geraet-ip>:9182/metrics | head -3
```

Keine Antwort bei laufendem Dienst → Firewall. Die Regel prüfen:

```powershell
Get-NetFirewallRule -DisplayName "Schul-Monitoring – Metrikabruf" |
  Get-NetFirewallAddressFilter
```

Steht dort eine falsche Adresse, hat der Agent beim Ausrollen den Namen des
Monitoring-Servers nicht auflösen können. Abhilfe: im GPO-Aufruf statt des
Namens die IP verwenden.

**4. Ist die Anmeldung angekommen?**

```bash
curl -s -H "X-Agent-Token: <Token>" http://localhost/mon/api/v1/agents |
  python3 -m json.tool | grep hostname
docker logs mon-registrar --tail 40
```

`401` bedeutet: Das Token im GPO-Aufruf stimmt nicht mit dem in der `.env`
überein.

### Keine Ereignisprotokolle von einem Gerät

```powershell
Get-Service Alloy
Get-Content "C:\Program Files\GrafanaLabs\Alloy\logs\*.log" -Tail 30
```

In Grafana unter *Explore* → Loki prüfen:

```logql
{job="windows_events", geraet="pc-edv-12"}
```

Kommt nichts: Meist stimmt die Basic-Auth für die Log-Annahme nicht. Auf
dem Gerät nachsehen, ob in `C:\Program Files\GrafanaLabs\Alloy\config.alloy`
beim `password` das richtige Token steht. Nach einer Änderung des Tokens auf
dem Server muss `pakete-holen.sh` neu laufen, damit die Vorlage aktualisiert
wird.

Kommt zwar etwas, aber wenig: Dann fehlen die Audit-Richtlinien –
siehe [06-security-monitoring.md](06-security-monitoring.md).

### Keine Hardware-Messwerte (`schule_…`)

```powershell
Get-ScheduledTask -TaskName SchulMonitoring-Hardwarepruefung |
  Get-ScheduledTaskInfo
Get-ChildItem "$env:ProgramData\SchulMonitoring\textfile"

# Von Hand ausführen und zusehen
powershell -ExecutionPolicy Bypass `
  -File "$env:ProgramData\SchulMonitoring\Collect-HardwareHealth.ps1" -Verbose
```

Welcher Prüfabschnitt scheitert, steht in den Messwerten selbst:

```promql
schule_pruefabschnitt_erfolgreich == 0
```

Dass einzelne Abschnitte auf einzelnen Geräten scheitern, ist normal – ein
virtueller Server hat keine S.M.A.R.T.-Werte, ein Standrechner keinen Akku.

---

## Netzwerkgeräte

### SNMP liefert nichts

```bash
docker exec mon-snmp wget -qO- \
  'http://localhost:9116/snmp?target=10.0.0.2&module=if_mib&auth=schule_v2' | head -20
```

Leer oder Fehler:

1. Community-String in der `.env` prüfen, danach `sudo ./bootstrap.sh`
2. Erlaubt das Gerät Abfragen von der IP der Monitoring-VM?
3. Blockiert eine Firewall UDP/161?

Beschwert sich der SNMP-Container über eine unbekannte Option, versteht die
verwendete Version noch keine zwei `--config.file`-Angaben. Dann in der
`docker-compose.yml` beim Dienst `snmp` die zweite Zeile entfernen und
den Inhalt von `snmp-schule.yml` stattdessen an die mitgelieferte
`snmp.yml` anhängen.

### UniFi liefert keine Daten

```bash
docker logs mon-unpoller --tail 30
```

* `401` oder `403` → Der Benutzer ist ein Ubiquiti-Konto statt eines
  lokalen. Lokalen Nur-Ansicht-Benutzer anlegen.
* Zeitüberschreitung → Adresse falsch. UDM und Cloud Key Gen2+ nutzen
  `https://<ip>` ohne Port 8443.
* Container läuft gar nicht → Das Profil ist nicht aktiv:
  `docker compose --profile unifi up -d`

### FortiGate liefert keine Daten

```bash
docker logs mon-fortigate --tail 30
curl -sk "https://10.0.0.1/api/v2/monitor/system/status?access_token=<Token>" | head -5
```

* `403` → Die IP der Monitoring-VM fehlt bei den *Trusted Hosts* des
  API-Administrators.
* Nichts im Security-Dashboard → Syslog kommt nicht an. Auf der FortiGate
  `config log syslogd setting` prüfen und in den Sicherheitsprofilen das
  Protokollieren einschalten.

---

## Dashboards

### Panels bleiben leer

Erst die Quelle prüfen, dann das Panel. In Grafana unter *Explore* die
Abfrage direkt eingeben. Kommt dort auch nichts, fehlen die Daten – nicht
das Panel.

Häufige Gründe:

* **Zeitraum** – „Hardware & Vorwarnungen" steht auf sieben Tage. Direkt
  nach dem Aufbau gibt es so viel Verlauf noch nicht.
* **Vorhersage-Panels** brauchen mindestens zwölf Stunden Daten.
* Die Panels für UniFi-Details bleiben leer, solange `unpoller` nicht läuft.
* Metriknamen einzelner Exporter ändern sich zwischen Hauptversionen. Was
  tatsächlich da ist, zeigt:

```bash
curl -s http://localhost:9090/api/v1/label/__name__/values |
  python3 -m json.tool | grep -i unpoller
```

### „No data" bei den Sicherheits-Panels

Fast immer fehlen die Audit-Richtlinien. Ohne sie protokolliert Windows die
gesuchten Ereignisse gar nicht erst –
siehe [06-security-monitoring.md](06-security-monitoring.md).

---

## Alles auf Anfang

Wenn nichts mehr hilft: Die VM ist vollständig ersetzbar.

```powershell
.\Deploy-MonitoringVM.ps1 -Ueberschreiben -IPAdresse 10.0.0.50/24 -Gateway 10.0.0.1
```

Die Agenten auf den Geräten bleiben unverändert und melden sich beim
nächsten Neustart an der neuen VM an – **sofern IP-Adresse und Token gleich
bleiben.** Ändert sich das Token, muss der GPO-Aufruf angepasst werden.

Vorher unbedingt sichern:
`stack/.env`, `stack/inventar/`, eigene Regeln und Dashboards.

---

## Wo welches Protokoll steht

| Was | Wo |
|---|---|
| Bereitstellung (Hyper-V) | Ausgabe im PowerShell-Fenster |
| Einrichtung der VM | `/var/log/schulmonitoring-bootstrap.log` |
| Erststart der VM | `journalctl -u schulmonitoring-einrichtung` |
| Container | `docker compose logs <dienst>` |
| Agent-Installation | `C:\ProgramData\SchulMonitoring\logs\` |
| Alloy auf dem Client | `C:\Program Files\GrafanaLabs\Alloy\logs\` |
