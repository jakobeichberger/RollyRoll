# Geräteerkennung

Der Grundgedanke: Niemand soll Gerätelisten pflegen müssen. Windows-Rechner
melden sich über die Gruppenrichtlinie von selbst an. Alles andere – Switches,
Access Points, Drucker, USV – findet der Suchlauf.

---

## Was gefunden wird und wie

Zwei Wege, die sich gegenseitig absichern:

| Weg | Findet | Braucht |
|---|---|---|
| **UniFi-Controller** | jeden adoptierten AP, Switch und jedes Gateway | Zugangsdaten in der `.env` |
| **SNMP-Suchlauf** | alles Verwaltbare: Drucker, USV, Fremdswitches, NAS, Kameras | SNMP am Gerät eingeschaltet |

Der Umweg über den Controller ist für UniFi-Geräte der zuverlässigere: Er
liefert den echten Gerätenamen, das Modell und die MAC-Adresse – und
funktioniert auch dann, wenn auf den Geräten gar kein SNMP läuft. Bei neueren
Access Points ist das inzwischen der Normalfall.

Der SNMP-Suchlauf schickt **ein einziges UDP-Paket je Adresse** mit vier
Abfragewerten darin: Systembeschreibung, Herstellerkennung, Systemname und
Anzahl der Schnittstellen. Ein /24 ist damit in wenigen Sekunden durch. Das ist
kein Portscan und fällt der FortiGate nicht als Angriff auf.

---

## Einrichten

Im Regelfall steht schon alles: Wird die VM mit fester IP ausgerollt, trägt
`Deploy-MonitoringVM.ps1` das eigene Netz automatisch ein. Bei
`-IPAdresse 10.0.0.50/24` also `10.0.0.0/24`.

Weitere Netze kommen in `/opt/schulmonitoring/stack/.env`:

```bash
ERKENNUNG_NETZE=10.0.0.0/24,10.0.1.0/24,10.0.2.0/24
ERKENNUNG_MODUS=automatisch
ERKENNUNG_INTERVALL_MINUTEN=60
```

Danach:

```bash
cd /opt/schulmonitoring/stack
sudo docker compose up -d registrar
```

Ein Netz je Eintrag, und bitte nicht größer als /22. Ein /16 sind 65.000
Adressen – der Suchlauf lehnt das ab, statt eine Stunde lang das Netz zu
belasten.

### Die zwei Betriebsarten

| Modus | Was passiert |
|---|---|
| `automatisch` | Gefundene Geräte werden **sofort überwacht** – Ping, und wo SNMP antwortet auch die Schnittstellen. Sie bekommen einen aus dem Systemnamen abgeleiteten Namen. |
| `vorschlag` | Es wird nur aufgelistet. Überwacht wird erst nach Eintrag in `inventar.yml`. |

Vorgabe ist `automatisch`. Der Gedanke dahinter: Ein Switch, der nicht
überwacht wird, weil ihn niemand eingetragen hat, ist der schlechtere Zustand.
Lieber ein unschöner Name im Dashboard als ein blinder Fleck.

---

## Vom Fund zum ordentlichen Eintrag

Der Suchlauf schreibt `inventar/gefunden.yml` – gleicher Aufbau wie
`inventar.yml`, damit sich Blöcke unverändert übernehmen lassen:

```yaml
  - name: usw-pro-24-poe
    ip: 10.0.0.7
    typ: switch
    hersteller: unifi
    snmp_modul: if_mib
    # Auskunft:    USW-Pro-24-PoE, 7.0.50.14550
    # Quelle:      snmp
    # Erstmals:    14.08.2026 07:12 UTC
```

Zur Übernahme den Block nach `inventar.yml` kopieren, einen sprechenden Namen
und den Raum eintragen:

```yaml
  - name: sw-edv-saal-2
    ip: 10.0.0.7
    typ: switch
    hersteller: unifi
    raum: EDV-Saal 2
    kritisch: true
    snmp_modul: if_mib
```

Danach verschwindet die Zeile aus `gefunden.yml` und aus dem Dashboard-Panel
„Gefunden, aber noch nicht erfasst" von selbst. Doppelt überwacht wird nie:
Sobald eine Adresse im Inventar steht oder einen Agenten hat, fällt sie aus den
Erkennungszielen heraus.

> `gefunden.yml` wird bei **jedem** Suchlauf neu geschrieben. Änderungen darin
> gehen verloren – sie gehören nach `inventar.yml`.

---

## Wo man das Ergebnis sieht

Dashboard **„Geräteerkennung"**. Die beiden wichtigen Tabellen:

* **„Gefunden, aber noch nicht erfasst"** – die Arbeitsliste. Idealerweise leer.
* **„Vollständige Liste"** – jedes je gesehene Gerät mit dem Zeitpunkt der
  ersten Sichtung.

Von Hand nachsehen geht auch:

```bash
curl -s -H "X-Agent-Token: <Token>" http://localhost/mon/api/v1/discovery | python3 -m json.tool
```

Suchlauf sofort anstoßen, statt bis zur nächsten Stunde zu warten:

```bash
curl -s -X POST -H "X-Agent-Token: <Token>" http://localhost/mon/api/v1/discovery/scan
```

---

## Die Sicherheitsseite

Ein Gerät, das im Servernetz auftaucht und dort nichts zu suchen hat, ist ein
Befund. In der Schule ist das häufiger ein privat mitgebrachter Router im
Klassenzimmer als ein Angriff – gesehen gehört beides.

| Alarm | Wann |
|---|---|
| `NeuesGeraetImNetz` | irgendwo ein neues Gerät, `info` |
| `UnbekanntesGeraetImServernetz` | neues Gerät im Servernetz, `warning`, geht an die Sicherheitsadresse |

Der Adressbereich in `UnbekanntesGeraetImServernetz` steht auf `10.0.0.0/24`.
Weicht die eigene Adressvergabe davon ab, in
`stack/prometheus/rules/70-erkennung.yml` anpassen:

```yaml
        expr: |
          schule_erkanntes_geraet{erfasst="nein", adresse=~"192\\.168\\.10\\..*"} == 1
```

`NeuesGeraetImNetz` ist bewusst nur `info` und wird gesammelt zugestellt: Beim
allerersten Suchlauf ist naturgemäß **alles** neu. Als Warnung hätte das
niemand länger als einen Tag ertragen.

---

## Wenn nichts gefunden wird

Erst prüfen, ob der Suchlauf überhaupt läuft:

```bash
docker logs mon-registrar --tail 30 | grep -i erkennung
```

Erwartete Zeile beim Start:

```
Geraeteerkennung aktiv: Netze 10.0.0.0/24, Modus automatisch, alle 60 Minuten
```

Steht dort stattdessen „ist ausgeschaltet", ist `ERKENNUNG_NETZE` leer.

**Es antwortet niemand.** Fast immer ist SNMP auf den Geräten aus oder der
Community-String stimmt nicht. Bei UniFi wird SNMP zentral eingeschaltet:
Netzwerk-Anwendung → Einstellungen → System → SNMP, Version v1/v2c, und der
Community-String muss zu `SNMP_COMMUNITY` in der `.env` passen. Gegenprobe für
ein einzelnes Gerät:

```bash
docker exec mon-snmp snmp_exporter --help >/dev/null 2>&1
curl -s "http://localhost:9116/snmp?target=10.0.0.2&module=if_mib&auth=schule_v2" | head -20
```

**Die Access Points fehlen, die Switches sind da.** Das ist der Normalfall bei
neueren UniFi-APs – die können kein SNMP mehr. Dafür gibt es den Weg über den
Controller: `UNIFI_URL`, `UNIFI_BENUTZER` und `UNIFI_PASSWORT` in der `.env`
setzen. Der Benutzer braucht nur Leserechte.

**Es wird zu viel gefunden.** Jeder Windows-Rechner mit eingeschaltetem
SNMP-Dienst taucht ebenfalls auf. Er wird korrekt als `server` eingeordnet und
fällt heraus, sobald sein Agent sich gemeldet hat. Wer die Client-Netze gar
nicht durchsuchen will, nimmt sie aus `ERKENNUNG_NETZE` heraus – die Clients
melden sich ohnehin per Gruppenrichtlinie selbst.

---

## Was die Erkennung nicht kann

Sie findet **nur, was antwortet**. Ein Gerät ohne SNMP, das dem
UniFi-Controller unbekannt ist, bleibt unsichtbar – etwa ein unmanaged Switch
oder eine Kamera mit abgeschaltetem SNMP. Solche Geräte gehören weiterhin von
Hand in `inventar.yml`.

Sie ist deshalb **keine Zugangskontrolle**. Wer verlässlich wissen will, was
sich im Netz befindet, braucht 802.1X oder die Client-Liste der FortiGate. Die
Erkennung ist ein Komfortwerkzeug für die Überwachung – mit einem nützlichen
Nebeneffekt fürs Auge.
