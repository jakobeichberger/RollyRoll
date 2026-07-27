# Windows-Agent per Gruppenrichtlinie ausrollen

Ziel: Jeder Server und jeder Client installiert sich beim nächsten Neustart
selbst und erscheint danach von allein im Dashboard.

---

## 1. Skripte nach SYSVOL kopieren

Auf einem Domänencontroller oder einem Rechner mit den
Verwaltungswerkzeugen:

```powershell
$ziel = "\\schule.local\SYSVOL\schule.local\scripts"
Copy-Item .\agent\Install-MonitoringAgent.ps1   $ziel
Copy-Item .\agent\Collect-HardwareHealth.ps1    $ziel
Copy-Item .\agent\Collect-SecurityBaseline.ps1  $ziel
Copy-Item .\agent\Uninstall-MonitoringAgent.ps1 $ziel
```

Die beiden `Collect-*.ps1` gehören mit dazu: Der Agent lädt sie zwar
normalerweise vom Monitoring-Server, greift aber auf die Kopie daneben
zurück, falls der Server gerade nicht erreichbar ist.

> Das Agent-Token steht später im Skriptaufruf und ist damit für jeden
> lesbar, der SYSVOL lesen darf – also für jeden Domänenbenutzer. Es
> berechtigt nur zum Anmelden am Monitoring und zum Abholen der Pakete,
> nicht zum Lesen von Daten anderer Geräte. Wer strenger sein möchte, legt
> die Skripte stattdessen in eine Freigabe, auf die nur
> `Domänencomputer` Leserechte hat, und verweist von der GPO dorthin.

---

## 2. Gruppenrichtlinie anlegen

Gruppenrichtlinienverwaltung → Rechtsklick auf die OU mit den Computern →
**Gruppenrichtlinienobjekt hier erstellen** → Name z. B.
`Schul-Monitoring Agent`.

Bearbeiten → **Computerkonfiguration** → Richtlinien → Windows-Einstellungen
→ Skripts → **Starten** → Registerkarte **PowerShell-Skripts** →
**Hinzufügen**:

* **Skriptname:**
  `\\schule.local\SYSVOL\schule.local\scripts\Install-MonitoringAgent.ps1`
* **Skriptparameter:**
  `-ServerUrl "http://10.0.0.50" -Token "<Agent-Token>"`

Wichtig ist die Registerkarte **PowerShell-Skripts**, nicht „Skripts".
Damit läuft es als `SYSTEM` – die Rechte, die für MSI-Installation und
Firewallregel nötig sind.

Optional auf derselben Seite: **„PowerShell-Skripts zuerst ausführen"**
aktivieren.

### Räume mitgeben

Für aussagekräftige Dashboards und Alarm-Mails empfiehlt es sich, je
Computer-OU eine eigene GPO mit passendem Raum anzulegen:

```
-ServerUrl "http://10.0.0.50" -Token "<Token>" -Raum "EDV-Saal 1"
```

Der Raum taucht dann in den Tabellen und in jeder Alarm-Mail auf – bei
„Drucker antwortet nicht" ist das der Unterschied zwischen Suchen und
Hingehen.

---

## 3. Ausführungsrichtlinie sicherstellen

Wenn PowerShell-Skripte in der Domäne gesperrt sind, in derselben GPO:

Computerkonfiguration → Richtlinien → Administrative Vorlagen →
Windows-Komponenten → **Windows PowerShell** →
**Skriptausführung aktivieren** → `Nur signierte Skripts zulassen` oder
`Alle Skripts zulassen`.

Der Aufruf über die GPO umgeht die Richtlinie ohnehin meist; die
geplante Aufgabe der Hardwareprüfung startet zusätzlich mit
`-ExecutionPolicy Bypass`.

---

## 4. Ausrollen

```powershell
# Auf einem Testrechner sofort anwenden
gpupdate /force
Restart-Computer
```

Startskripte greifen erst beim **Neustart**. In der Schule heißt das in
der Praxis: am nächsten Morgen sind die meisten Geräte drin.

Zum sofortigen Prüfen auf einem einzelnen Rechner (als Administrator):

```powershell
\\schule.local\SYSVOL\schule.local\scripts\Install-MonitoringAgent.ps1 `
    -ServerUrl "http://10.0.0.50" -Token "<Token>" -Raum "Serverraum"
```

---

## Was der Agent auf dem Gerät tut

1. **Erkennt die Rolle** – Client, Server, Domänencontroller oder
   Hyper-V-Host – und wählt danach die Sammler aus. Ein DC bekommt AD-,
   DNS- und DHCP-Werte, ein Client nicht. Läuft IIS oder SQL Server,
   kommen die passenden Sammler dazu.
2. **Installiert windows_exporter** aus dem MSI, das der Monitoring-Server
   bereitstellt. Die Clients brauchen dafür **keinen Internetzugang**.
3. **Öffnet die Firewall** auf Port 9182 – nur für die IP des
   Monitoring-Servers.
4. **Richtet zwei geplante Aufgaben ein**, beide als `SYSTEM`:
   die Hardwareprüfung alle fünf Minuten und die Sicherheits-Baseline
   stündlich. Die Baseline läuft seltener, weil sich Einstellungen selten
   ändern und die AD-Abfragen auf einem Domänencontroller mehr kosten als
   eine Registry-Abfrage. Der Startzeitpunkt ist je Gerät zufällig
   versetzt, damit nicht die ganze Schule zur vollen Stunde gleichzeitig
   den Domänencontroller befragt.
5. **Installiert Grafana Alloy** und holt sich dessen Konfiguration vom
   Server. Dadurch lassen sich später zentral andere Ereignisse einsammeln,
   ohne die GPO anzufassen.
6. **Meldet sich am Server an** – mit Hostname, IP, Rolle, Betriebssystem,
   Modell und Seriennummer.

Alles wird protokolliert nach
`C:\ProgramData\SchulMonitoring\logs\installation-JJJJ-MM.log`.

---

## Bei jedem weiteren Neustart

Das Skript prüft nur kurz, ob Version und Dienst stimmen, sendet ein
Lebenszeichen und ist nach wenigen Sekunden fertig. Der Anmeldevorgang der
Schülerinnen und Schüler wird dadurch nicht spürbar verzögert.

Neu installiert wird erst, wenn sich die Agent-Version ändert – ein Update
besteht also darin, die neuen Skripte nach SYSVOL zu kopieren. Beim
nächsten Neustart aktualisiert sich jedes Gerät selbst.

---

## Prüfen, ob es geklappt hat

**Auf dem Gerät:**

```powershell
Get-Service windows_exporter, Alloy
Get-ScheduledTask SchulMonitoring-*
Invoke-WebRequest http://localhost:9182/metrics -UseBasicParsing |
    Select-Object -ExpandProperty Content |
    Select-String "schule_"
Get-Content "$env:ProgramData\SchulMonitoring\logs\installation-*.log" -Tail 30
```

**Auf dem Monitoring-Server:**

```bash
curl -s -H "X-Agent-Token: <Token>" http://localhost/mon/api/v1/agents | head -40
```

**Im Dashboard:** „Gesamtübersicht" → die Zähler für Server und Clients
steigen. Neue Geräte tauchen binnen einer Minute auf.

---

## Agent wieder entfernen

`Uninstall-MonitoringAgent.ps1` genauso als Startskript in einer GPO
verteilen, die auf die betreffenden Geräte angewendet wird. Das Gerät
verschwindet danach nach `AGENT_TIMEOUT_TAGE` (Vorgabe: 30) von selbst aus
dem Dashboard.

---

## Linux-Server

```bash
sudo bash install-agent.sh \
  --server http://10.0.0.50 \
  --token "<Agent-Token>" \
  --raum "Serverraum"
```

Installiert node_exporter als Systemdienst, richtet – falls `smartctl`
vorhanden ist – eine S.M.A.R.T.-Prüfung ein und meldet den Server an.
Ist `smartmontools` nicht installiert, lohnt sich das Nachholen: Ohne das
Paket fehlt die Ausfallvorhersage der Platten.
