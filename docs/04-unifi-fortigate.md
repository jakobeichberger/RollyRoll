# UniFi, FortiGate und alles andere im Netz

---

## Die Geräteliste

Alles, was keinen Agenten bekommen kann, steht in einer einzigen Datei auf
der VM:

```
/opt/schulmonitoring/stack/inventar/inventar.yml
```

```yaml
geraete:
  - name: ap-turnsaal
    ip: 10.0.1.13
    typ: accesspoint        # switch | accesspoint | firewall | server
                            # drucker | usv | kamera | sonstiges
    hersteller: unifi
    raum: Turnsaal
    kritisch: false         # true = schnellerer und schärferer Alarm
    snmp_modul: ubiquiti_unifi
    dienste:
      - { modul: https, port: 443, name: Weboberflaeche }
```

Der Registrierungs-Dienst liest die Datei alle 30 Sekunden neu ein und
erzeugt daraus die Ziele für Ping, SNMP und Dienstprüfung. **Kein Neustart
nötig.**

Fehlerhafte Einträge werden übersprungen und protokolliert – der Rest läuft
weiter:

```bash
docker logs mon-registrar --tail 30
```

Nützliche SNMP-Module: `if_mib` (jeder Switch), `ubiquiti_unifi`,
`fortigate_schule`, `apc_usv`, `printer_mib`, `synology`.

---

## UniFi

### Nur-Lese-Benutzer anlegen

Im UniFi-Controller: **Einstellungen → Administratoren → Neuen
Administrator hinzufügen**

* Rolle: **Nur Ansicht** (View Only)
* **Lokaler Zugriff**, nicht Ubiquiti-Konto – das ist wichtig, sonst
  scheitert die Anmeldung über die API
* Kennwort notieren

Danach auf der VM in die `.env`:

```bash
UNIFI_URL=https://10.0.0.10:8443
UNIFI_BENUTZER=monitoring
UNIFI_PASSWORT=...
UNIFI_SITES=all
```

```bash
cd /opt/schulmonitoring/stack
docker compose --profile unifi up -d
```

Bei einer UniFi-Konsole (UDM, Cloud Key Gen2+) lautet die Adresse
`https://10.0.0.10` **ohne** Port 8443.

Prüfen:

```bash
docker logs mon-unpoller --tail 20
curl -s http://localhost:9090/api/v1/query?query=up%7Bjob%3D%22unifi%22%7D
```

### SNMP auf den Geräten einschalten

**Einstellungen → System → Erweitert → SNMP** → v2c aktivieren,
Community-String setzen. Denselben String in die `.env` unter
`SNMP_COMMUNITY` eintragen.

### Syslog

**Einstellungen → System → Erweitert → Remote Logging**
→ Server `10.0.0.50`, Port `1514`.

---

## FortiGate

Die FortiGate wird über drei Wege erfasst, weil jeder etwas kann, was die
anderen nicht können:

| Weg | Liefert |
|---|---|
| REST-API | CPU, Speicher, Sitzungen, Schnittstellen, VPN-Tunnel |
| SNMP | Lüfter, Temperaturen, Netzteile – die Hardware-Vorwarnung |
| Syslog | Angriffe, Virenfunde, Botnetz-Kontakte, Anmeldungen |

### API-Token

**System → Administratoren → REST-API-Administrator erstellen**

* Benutzername: `monitoring`
* Administratorprofil: eines mit **Nur-Lese-Rechten** (`read only`)
* **Trusted Hosts**: `10.0.0.50/32` – nur der Monitoring-Server
* Das angezeigte Token erscheint genau einmal; sofort notieren.

```bash
FORTIGATE_URL=https://10.0.0.1
FORTIGATE_TOKEN=...
```

```bash
cd /opt/schulmonitoring/stack
docker compose --profile fortigate up -d
```

### SNMP

**System → SNMP** → SNMP-Agent aktivieren → v2c-Community anlegen →
Hosts: `10.0.0.50`. Auf der Schnittstelle zum Monitoring-Server muss unter
**Administrativer Zugriff** ebenfalls `SNMP` erlaubt sein.

In `inventar.yml` bekommt die FortiGate dann:

```yaml
  - name: fw-fortigate-01
    ip: 10.0.0.1
    typ: firewall
    kritisch: true
    snmp_modul: fortigate_schule
    fortigate_api: true
```

### Syslog

Auf der CLI der FortiGate:

```
config log syslogd setting
    set status enable
    set server "10.0.0.50"
    set port 1514
    set mode udp
    set facility local7
    set format default
end
```

Damit auch die Angriffserkennung Daten bekommt, muss protokolliert werden,
was interessiert:

```
config log setting
    set fwpolicy-implicit-log enable
end
```

Und in den Sicherheitsprofilen (IPS, Antivirus, Webfilter) das Protokollieren
einschalten. Ohne das bleibt das Security-Dashboard leer.

Prüfen, ob etwas ankommt:

```bash
docker logs mon-alloy --tail 20
# oder in Grafana: Explore -> Loki -> {job="syslog", quelle="fortigate"}
```

---

## Drucker, USV und alles Weitere

```yaml
  - name: drucker-sekretariat
    ip: 10.0.2.30
    typ: drucker
    raum: Sekretariat
    snmp_modul: printer_mib

  - name: usv-serverraum
    ip: 10.0.0.5
    typ: usv
    hersteller: apc
    kritisch: true
    snmp_modul: apc_usv
```

Die USV ist ein lohnender Eintrag: Sie meldet den Stromausfall im
Serverraum, bevor irgendein Server etwas davon merkt – und sie warnt
rechtzeitig, wenn die Batterie altert.

Geräte ganz ohne SNMP kommen mit reinem Ping hinein:

```yaml
  - name: kamera-eingang
    ip: 10.0.3.10
    typ: kamera
    raum: Eingang
```

---

## Wichtige Dienste zusätzlich von außen prüfen

Auch auf Servern mit Agent lohnt sich die Prüfung von außen: So sieht man,
ob der **Dienst** antwortet und nicht nur, ob der Rechner läuft.

```yaml
  - name: dc01
    ip: 10.0.0.20
    typ: server
    kritisch: true
    dienste:
      - { modul: ldap,  port: 389, name: Active Directory }
      - { modul: tcp,   port: 445, name: Dateifreigaben }
      - { modul: https, port: 443, name: Intranet }
```

Verfügbare Module: `tcp`, `http`, `https`, `ldap`, `smtp`, `dns_schule`.
Bei `https` wird zusätzlich die Restlaufzeit des Zertifikats überwacht und
21 Tage vor Ablauf gewarnt.

---

## Wenn SNMP nichts liefert

```bash
# Von der VM aus direkt fragen
docker exec mon-snmp wget -qO- \
  'http://localhost:9116/snmp?target=10.0.0.2&module=if_mib&auth=schule_v2' | head -30
```

Kommt nichts zurück:

1. Stimmt der Community-String in der `.env`? Nach einer Änderung
   `sudo systemctl restart schulmonitoring`, damit die SNMP-Konfiguration
   neu erzeugt wird.
2. Erlaubt das Gerät SNMP-Abfragen von der IP der Monitoring-VM?
3. Blockiert eine Zwischenfirewall UDP/161?
