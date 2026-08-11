#!/usr/bin/env python3
"""
Geraeteerkennung fuer das Schul-Monitoring.

Zwei Quellen, die sich gegenseitig ergaenzen:

  1. UniFi-Controller. Der kennt jeden adoptierten Access Point, Switch
     und jedes Gateway samt Name, Modell, IP und MAC. Fuer die
     UniFi-Landschaft ist das die zuverlaessigste Quelle ueberhaupt –
     ganz ohne Suchlauf im Netz und unabhaengig davon, ob auf den
     Geraeten SNMP eingeschaltet ist.

  2. SNMP-Suchlauf. Eine einzige UDP-Anfrage an jede Adresse der
     angegebenen Netze. Wer antwortet, liefert dabei gleich Systemname,
     Systembeschreibung, Herstellerkennung und Schnittstellenanzahl mit.
     Damit werden Drucker, USV, Fremdswitches, NAS und alles andere
     Verwaltbare gefunden.

Bewusst ohne Fremdbibliotheken. Die SNMP-Kodierung (BER) steht hier zu
Fuss – das sind rund 120 Zeilen und erspart eine Abhaengigkeit, die alle
paar Jahre Aerger macht.

Der Suchlauf ist absichtlich leise: vier Abfragewerte in einem einzigen
UDP-Paket je Adresse. Das ist kein Portscan und faellt keiner Firewall
als Angriff auf.
"""

from __future__ import annotations

import ipaddress
import json
import logging
import select
import socket
import ssl
import time
import urllib.error
import urllib.parse
import urllib.request
from http.cookiejar import CookieJar
from typing import Any, Iterable

log = logging.getLogger("registrar.erkennung")


# ===================================================================== #
# BER-Kodierung (nur so viel, wie SNMPv2c braucht)
# ===================================================================== #
def _laenge_kodieren(anzahl: int) -> bytes:
    if anzahl < 0x80:
        return bytes([anzahl])
    roh = anzahl.to_bytes((anzahl.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(roh)]) + roh


def _tlv(kennung: int, inhalt: bytes) -> bytes:
    return bytes([kennung]) + _laenge_kodieren(len(inhalt)) + inhalt


def _ganzzahl(wert: int) -> bytes:
    # Nur nicht negative Werte noetig (Anfrage-Nummern, Fehlercodes).
    laenge = max(1, (wert.bit_length() // 8) + 1)
    return _tlv(0x02, wert.to_bytes(laenge, "big", signed=True))


def _basis128(wert: int) -> bytes:
    if wert == 0:
        return b"\x00"
    gruppen: list[int] = []
    while wert:
        gruppen.append(wert & 0x7F)
        wert >>= 7
    gruppen.reverse()
    return bytes([g | 0x80 for g in gruppen[:-1]] + [gruppen[-1]])


def _oid_kodieren(text: str) -> bytes:
    teile = [int(t) for t in text.split(".")]
    roh = bytearray(_basis128(teile[0] * 40 + teile[1]))
    for teil in teile[2:]:
        roh += _basis128(teil)
    return _tlv(0x06, bytes(roh))


def _tlv_lesen(daten: bytes, pos: int) -> tuple[int, bytes, int]:
    """Liest ein einzelnes TLV ab pos und liefert (Kennung, Inhalt, neue Position)."""
    if pos + 2 > len(daten):
        raise ValueError("Antwort zu kurz")
    kennung = daten[pos]
    pos += 1
    laenge = daten[pos]
    pos += 1
    if laenge & 0x80:
        anzahl = laenge & 0x7F
        if anzahl == 0 or pos + anzahl > len(daten):
            raise ValueError("ungueltige Laengenangabe")
        laenge = int.from_bytes(daten[pos:pos + anzahl], "big")
        pos += anzahl
    if pos + laenge > len(daten):
        raise ValueError("Inhalt reicht ueber das Paketende hinaus")
    return kennung, daten[pos:pos + laenge], pos + laenge


def _oid_lesen(roh: bytes) -> str:
    if not roh:
        return ""
    werte = [roh[0] // 40, roh[0] % 40]
    aktuell = 0
    for byte in roh[1:]:
        aktuell = (aktuell << 7) | (byte & 0x7F)
        if not byte & 0x80:
            werte.append(aktuell)
            aktuell = 0
    return ".".join(str(w) for w in werte)


def _wert_lesen(kennung: int, roh: bytes) -> Any:
    if kennung == 0x04:          # OCTET STRING
        return roh.decode("utf-8", errors="replace").replace("\x00", "").strip()
    if kennung == 0x06:          # OBJECT IDENTIFIER
        return _oid_lesen(roh)
    if kennung in (0x02, 0x41, 0x42, 0x43, 0x46):   # INTEGER, Counter, Gauge, TimeTicks
        return int.from_bytes(roh, "big", signed=(kennung == 0x02)) if roh else 0
    if kennung == 0x40:          # IpAddress
        return ".".join(str(b) for b in roh)
    if kennung == 0x05:          # NULL
        return None
    # 0x80 noSuchObject, 0x81 noSuchInstance, 0x82 endOfMibView
    return None


# ===================================================================== #
# SNMPv2c GET
# ===================================================================== #
# Was jedes verwaltbare Geraet beantworten koennen muss.
ABFRAGE_OIDS = [
    "1.3.6.1.2.1.1.1.0",   # sysDescr    – Klartextbeschreibung
    "1.3.6.1.2.1.1.2.0",   # sysObjectID – Herstellerkennung
    "1.3.6.1.2.1.1.5.0",   # sysName     – konfigurierter Geraetename
    "1.3.6.1.2.1.2.1.0",   # ifNumber    – Anzahl Schnittstellen
]


def _get_paket(community: str, oids: Iterable[str], anfrage_nummer: int) -> bytes:
    bindungen = b"".join(
        _tlv(0x30, _oid_kodieren(oid) + _tlv(0x05, b"")) for oid in oids
    )
    pdu = (
        _ganzzahl(anfrage_nummer)
        + _ganzzahl(0)                  # error-status
        + _ganzzahl(0)                  # error-index
        + _tlv(0x30, bindungen)
    )
    nachricht = (
        _ganzzahl(1)                    # Version 1 = SNMPv2c
        + _tlv(0x04, community.encode("utf-8"))
        + _tlv(0xA0, pdu)               # 0xA0 = GetRequest
    )
    return _tlv(0x30, nachricht)


def _antwort_lesen(daten: bytes) -> dict[str, Any] | None:
    """Zerlegt eine GetResponse und liefert {oid: wert}."""
    try:
        kennung, inhalt, _ = _tlv_lesen(daten, 0)
        if kennung != 0x30:
            return None

        pos = 0
        _, _, pos = _tlv_lesen(inhalt, pos)          # version
        _, _, pos = _tlv_lesen(inhalt, pos)          # community
        kennung, pdu, _ = _tlv_lesen(inhalt, pos)
        if kennung != 0xA2:                          # GetResponse
            return None

        pos = 0
        _, _, pos = _tlv_lesen(pdu, pos)             # request-id
        _, fehler, pos = _tlv_lesen(pdu, pos)        # error-status
        _, _, pos = _tlv_lesen(pdu, pos)             # error-index
        _, bindungen, _ = _tlv_lesen(pdu, pos)

        # Ein Fehlerstatus ungleich 0 heisst nur, dass einzelne Werte
        # fehlen – das Geraet hat trotzdem geantwortet.
        if fehler and int.from_bytes(fehler, "big") not in (0, 1, 2):
            log.debug("SNMP-Fehlerstatus %s", fehler.hex())

        ergebnis: dict[str, Any] = {}
        pos = 0
        while pos < len(bindungen):
            _, bindung, pos = _tlv_lesen(bindungen, pos)
            innen = 0
            _, oid_roh, innen = _tlv_lesen(bindung, innen)
            wert_kennung, wert_roh, _ = _tlv_lesen(bindung, innen)
            ergebnis[_oid_lesen(oid_roh)] = _wert_lesen(wert_kennung, wert_roh)
        return ergebnis
    except (ValueError, IndexError) as fehler:
        log.debug("Antwort nicht auswertbar: %s", fehler)
        return None


# --------------------------------------------------------------------- #
# SNMP-Walk (GetBulk) – fuer Tabellen wie die LLDP-Nachbarschaft
# --------------------------------------------------------------------- #
def _getbulk_paket(community: str, oid: str, anfrage_nummer: int,
                   wiederholungen: int = 25) -> bytes:
    """
    GetBulk hat denselben Aufbau wie GetRequest, nur bedeuten die beiden
    Felder nach der Anfragenummer etwas anderes: Anzahl der nicht zu
    wiederholenden Werte und maximale Wiederholungen. Damit holt man eine
    ganze Tabellenspalte in wenigen Paketen statt in hundert.
    """
    pdu = (
        _ganzzahl(anfrage_nummer)
        + _ganzzahl(0)                  # non-repeaters
        + _ganzzahl(wiederholungen)     # max-repetitions
        + _tlv(0x30, _tlv(0x30, _oid_kodieren(oid) + _tlv(0x05, b"")))
    )
    nachricht = (
        _ganzzahl(1)
        + _tlv(0x04, community.encode("utf-8"))
        + _tlv(0xA5, pdu)               # 0xA5 = GetBulkRequest
    )
    return _tlv(0x30, nachricht)


def _antwort_varbinds(daten: bytes) -> list[tuple[str, Any]]:
    """Wie _antwort_lesen, behaelt aber die Reihenfolge – die zaehlt beim Walk."""
    try:
        kennung, inhalt, _ = _tlv_lesen(daten, 0)
        if kennung != 0x30:
            return []
        pos = 0
        _, _, pos = _tlv_lesen(inhalt, pos)          # version
        _, _, pos = _tlv_lesen(inhalt, pos)          # community
        kennung, pdu, _ = _tlv_lesen(inhalt, pos)
        if kennung != 0xA2:
            return []
        pos = 0
        _, _, pos = _tlv_lesen(pdu, pos)             # request-id
        _, _, pos = _tlv_lesen(pdu, pos)             # error-status
        _, _, pos = _tlv_lesen(pdu, pos)             # error-index
        _, bindungen, _ = _tlv_lesen(pdu, pos)

        ergebnis: list[tuple[str, Any]] = []
        pos = 0
        while pos < len(bindungen):
            _, bindung, pos = _tlv_lesen(bindungen, pos)
            innen = 0
            _, oid_roh, innen = _tlv_lesen(bindung, innen)
            wert_kennung, wert_roh, _ = _tlv_lesen(bindung, innen)
            ergebnis.append((_oid_lesen(oid_roh), _wert_lesen(wert_kennung, wert_roh)))
        return ergebnis
    except (ValueError, IndexError):
        return []


def snmp_walk(ip: str, community: str, basis_oid: str,
              zeitlimit: float = 3.0, hoechstzahl: int = 500) -> dict[str, Any]:
    """
    Laeuft eine Tabellenspalte ab und liefert {oid: wert}.

    Bricht ab, sobald die Antworten den Basisbereich verlassen, nichts
    mehr dazukommt oder die Obergrenze erreicht ist. Letzteres ist die
    Reissleine gegen Geraete, die im Kreis antworten.
    """
    ergebnis: dict[str, Any] = {}
    aktuell = basis_oid
    praefix = basis_oid + "."

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(zeitlimit)
    try:
        for runde in range(hoechstzahl):
            try:
                sock.sendto(_getbulk_paket(community, aktuell, runde + 1), (ip, 161))
                daten, _ = sock.recvfrom(65535)
            except (socket.timeout, OSError):
                break

            varbinds = _antwort_varbinds(daten)
            if not varbinds:
                break

            neu = 0
            for oid, wert in varbinds:
                if not (oid == basis_oid or oid.startswith(praefix)):
                    return ergebnis            # Bereich verlassen: fertig
                if oid in ergebnis:
                    continue
                ergebnis[oid] = wert
                aktuell = oid
                neu += 1
                if len(ergebnis) >= hoechstzahl:
                    return ergebnis

            if neu == 0:                        # kein Fortschritt mehr
                break
    finally:
        sock.close()

    return ergebnis


# --------------------------------------------------------------------- #
# LLDP: Wer haengt an welchem Port?
# --------------------------------------------------------------------- #
# LLDP-MIB. Die Zahlen sind fest, es muss keine MIB geladen werden.
LLDP_ENTFERNT_SYSNAME = "1.0.8802.1.1.2.1.4.1.1.9"    # lldpRemSysName
LLDP_ENTFERNT_PORTID  = "1.0.8802.1.1.2.1.4.1.1.7"    # lldpRemPortId
LLDP_ENTFERNT_PORTBES = "1.0.8802.1.1.2.1.4.1.1.8"    # lldpRemPortDesc
LLDP_LOKAL_PORTID     = "1.0.8802.1.1.2.1.3.7.1.3"    # lldpLocPortId
LLDP_LOKAL_PORTBES    = "1.0.8802.1.1.2.1.3.7.1.4"    # lldpLocPortDesc


def _lldp_index(oid: str, basis: str) -> str | None:
    """
    Aus der lldpRem-Tabelle den lokalen Port herausloesen. Der Index ist
    dreiteilig: Zeitmarke, lokale Portnummer, laufende Nummer. Die
    mittlere Zahl ist die gesuchte.
    """
    if not oid.startswith(basis + "."):
        return None
    rest = oid[len(basis) + 1:].split(".")
    return rest[1] if len(rest) >= 2 else None


def lldp_nachbarn(ip: str, community: str, zeitlimit: float = 3.0) -> list[dict[str, str]]:
    """
    Liest die Nachbarschaftstabelle eines Geraets aus.

    Die Erkennung weiss, WAS es im Netz gibt. Das hier beantwortet die
    andere Haelfte: was haengt woran. Bei einem ausgefallenen Uplink sieht
    man damit sofort, welche Geraete dahinter liegen, statt zu raten.
    """
    sysnamen = snmp_walk(ip, community, LLDP_ENTFERNT_SYSNAME, zeitlimit)
    if not sysnamen:
        return []

    portids = snmp_walk(ip, community, LLDP_ENTFERNT_PORTID, zeitlimit)
    portbes = snmp_walk(ip, community, LLDP_ENTFERNT_PORTBES, zeitlimit)
    lokale = snmp_walk(ip, community, LLDP_LOKAL_PORTID, zeitlimit)
    lokale_bes = snmp_walk(ip, community, LLDP_LOKAL_PORTBES, zeitlimit)

    # Lokale Portnummer -> lesbarer Portname
    lokal_namen: dict[str, str] = {}
    for quelle, basis in ((lokale_bes, LLDP_LOKAL_PORTBES), (lokale, LLDP_LOKAL_PORTID)):
        for oid, wert in quelle.items():
            nummer = oid.rsplit(".", 1)[-1]
            text = str(wert or "").strip()
            if text and nummer not in lokal_namen:
                lokal_namen[nummer] = text

    nachbarn: list[dict[str, str]] = []
    for oid, name in sysnamen.items():
        name = str(name or "").strip()
        if not name:
            continue
        nummer = _lldp_index(oid, LLDP_ENTFERNT_SYSNAME)
        if nummer is None:
            continue
        schwanz = oid[len(LLDP_ENTFERNT_SYSNAME) + 1:]

        entfernt_port = str(
            portbes.get(f"{LLDP_ENTFERNT_PORTBES}.{schwanz}")
            or portids.get(f"{LLDP_ENTFERNT_PORTID}.{schwanz}")
            or ""
        ).strip()

        nachbarn.append({
            "lokal_port": lokal_namen.get(nummer, f"Port {nummer}"),
            "entfernt_geraet": name,
            "entfernt_port": entfernt_port,
        })

    return nachbarn


def _adressen(netze: Iterable[str], hoechstzahl: int) -> list[str]:
    """Loest die konfigurierten Netze in Einzeladressen auf."""
    liste: list[str] = []
    for eintrag in netze:
        eintrag = eintrag.strip()
        if not eintrag:
            continue
        try:
            netz = ipaddress.ip_network(eintrag, strict=False)
        except ValueError:
            log.warning("Unbrauchbare Netzangabe wird uebersprungen: %r", eintrag)
            continue
        if netz.version != 4:
            log.warning("Nur IPv4 wird durchsucht – %s wird uebersprungen", eintrag)
            continue
        if netz.num_addresses > hoechstzahl:
            log.warning(
                "%s umfasst %d Adressen – das ist fuer einen Suchlauf zu gross "
                "und wird uebersprungen. Bitte enger fassen (z. B. /24).",
                eintrag, netz.num_addresses,
            )
            continue
        liste.extend(str(adresse) for adresse in netz.hosts())

    if len(liste) > hoechstzahl:
        log.warning("Suchlauf auf die ersten %d Adressen begrenzt", hoechstzahl)
        liste = liste[:hoechstzahl]
    # Reihenfolge stabil, aber ohne Doppelte
    return list(dict.fromkeys(liste))


def snmp_suchlauf(
    netze: Iterable[str],
    community: str,
    zeitlimit: float = 2.5,
    runden: int = 2,
    hoechstzahl: int = 4096,
) -> list[dict[str, Any]]:
    """
    Fragt jede Adresse der angegebenen Netze einmal per SNMP an.

    Alle Anfragen laufen ueber einen einzigen nicht blockierenden Socket:
    erst alles hinausschicken, dann einsammeln, was zurueckkommt. Ein /24
    ist damit in wenigen Sekunden durch. UDP kann Pakete verlieren, darum
    wird standardmaessig eine zweite Runde fuer die Stillen gefahren.
    """
    ziele = _adressen(netze, hoechstzahl)
    if not ziele:
        return []

    gefunden: dict[str, dict[str, Any]] = {}
    beginn = time.monotonic()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.setblocking(False)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
        except OSError:
            pass

        for runde in range(max(1, runden)):
            offen = [ip for ip in ziele if ip not in gefunden]
            if not offen:
                break

            paket = _get_paket(community, ABFRAGE_OIDS, anfrage_nummer=runde + 1)

            for nummer, ip in enumerate(offen, start=1):
                try:
                    sock.sendto(paket, (ip, 161))
                except OSError:
                    continue
                # Kurz Luft holen, damit der Sendepuffer nicht ueberlaeuft
                # und Switches nicht unnoetig in die Knie gehen.
                if nummer % 64 == 0:
                    time.sleep(0.02)

            ende = time.monotonic() + zeitlimit
            while time.monotonic() < ende:
                bereit, _, _ = select.select([sock], [], [], 0.2)
                if not bereit:
                    continue
                try:
                    daten, absender = sock.recvfrom(8192)
                except OSError:
                    continue
                ip = absender[0]
                if ip in gefunden:
                    continue
                werte = _antwort_lesen(daten)
                if werte is None:
                    continue
                gefunden[ip] = {
                    "ip": ip,
                    "quelle": "snmp",
                    "sysname": str(werte.get("1.3.6.1.2.1.1.5.0") or "").strip(),
                    "beschreibung": str(werte.get("1.3.6.1.2.1.1.1.0") or "").strip(),
                    "objekt_id": str(werte.get("1.3.6.1.2.1.1.2.0") or "").strip(),
                    "schnittstellen": werte.get("1.3.6.1.2.1.2.1.0") or 0,
                }
    finally:
        sock.close()

    dauer = time.monotonic() - beginn
    log.info(
        "SNMP-Suchlauf: %d von %d Adressen haben geantwortet (%.1f s)",
        len(gefunden), len(ziele), dauer,
    )
    return list(gefunden.values())


# ===================================================================== #
# Einordnung: Wer ist das und wie fragt man es am besten ab?
# ===================================================================== #
# Herstellerkennungen aus sysObjectID (1.3.6.1.4.1.<Nummer>...)
HERSTELLERNUMMERN = {
    9: "cisco",
    11: "hp",
    171: "dlink",
    236: "samsung",
    253: "xerox",
    318: "apc",
    367: "ricoh",
    368: "axis",
    534: "eaton",
    641: "lexmark",
    674: "dell",
    1248: "epson",
    1347: "kyocera",
    1602: "canon",
    1991: "brocade",
    2011: "huawei",
    2435: "brother",
    2636: "juniper",
    4413: "broadcom",
    4526: "netgear",
    6574: "synology",
    8072: "linux",
    10002: "ubiquiti",
    11863: "tplink",
    12356: "fortinet",
    14823: "aruba",
    14988: "mikrotik",
    24681: "qnap",
    25506: "h3c",
    41112: "unifi",
}

# Schluesselwoerter in der Systembeschreibung, wenn die Herstellernummer
# nicht weiterhilft. Reihenfolge zaehlt: der erste Treffer gewinnt.
BESCHREIBUNGSMUSTER = [
    ("unifi",    ("unifi", "ubnt", "ubiquiti")),
    ("fortinet", ("fortigate", "fortios", "fortiswitch", "fortiap")),
    ("apc",      ("apc ", "smart-ups", "back-ups")),
    ("eaton",    ("eaton", "powerware")),
    ("hp",       ("hewlett", "procurve", "aruba", "hp ", "laserjet", "officejet")),
    ("brother",  ("brother",)),
    ("kyocera",  ("kyocera",)),
    ("canon",    ("canon",)),
    ("epson",    ("epson",)),
    ("lexmark",  ("lexmark",)),
    ("ricoh",    ("ricoh",)),
    ("xerox",    ("xerox",)),
    ("cisco",    ("cisco", "ios ")),
    ("mikrotik", ("mikrotik", "routeros")),
    ("netgear",  ("netgear",)),
    ("synology", ("synology",)),
    ("qnap",     ("qnap",)),
    ("axis",     ("axis",)),
    ("linux",    ("linux", "ubuntu", "debian")),
    ("windows",  ("windows", "microsoft")),
]

DRUCKERHERSTELLER = {
    "brother", "kyocera", "canon", "epson", "lexmark", "ricoh", "xerox", "samsung"
}
USVHERSTELLER = {"apc", "eaton"}

# Auch Hersteller, die beides bauen (HP, Dell), haben eindeutige
# Drucker-Kennzeichnungen. "ethernet multi-environment" ist die klassische
# Beschreibung einer HP-JetDirect-Karte.
DRUCKERMUSTER = (
    "printer", "laserjet", "officejet", "deskjet", "jetdirect",
    "ethernet multi-environment", "imagerunner", "workcentre", "ecosys",
)


def _hersteller_bestimmen(objekt_id: str, beschreibung: str) -> str:
    if objekt_id.startswith("1.3.6.1.4.1."):
        rest = objekt_id[len("1.3.6.1.4.1."):].split(".")
        if rest and rest[0].isdigit():
            name = HERSTELLERNUMMERN.get(int(rest[0]))
            if name:
                return name

    klein = beschreibung.lower()
    for name, muster in BESCHREIBUNGSMUSTER:
        if any(wort in klein for wort in muster):
            return name
    return "unbekannt"


def einordnen(fund: dict[str, Any]) -> dict[str, Any]:
    """
    Bestimmt aus den SNMP-Angaben Hersteller, Geraetetyp und das passende
    snmp_exporter-Modul. Bewusst grosszuegig: lieber als "sonstiges" mit
    if_mib erfassen als gar nicht. Den endgueltigen Namen vergibt ohnehin
    der Mensch, wenn er den Fund nach inventar.yml uebernimmt.
    """
    beschreibung = fund.get("beschreibung", "") or ""
    klein = beschreibung.lower()
    hersteller = _hersteller_bestimmen(fund.get("objekt_id", "") or "", beschreibung)
    schnittstellen = int(fund.get("schnittstellen") or 0)

    typ = "sonstiges"
    modul = "if_mib"

    if hersteller in DRUCKERHERSTELLER or any(
        wort in klein for wort in DRUCKERMUSTER
    ):
        typ, modul = "drucker", "printer_mib"
    elif hersteller in USVHERSTELLER or "ups" in klein.split():
        typ = "usv"
        modul = "apc_usv" if hersteller == "apc" else "if_mib"
    elif hersteller == "fortinet":
        typ, modul = "firewall", "fortigate_schule"
    elif hersteller in ("unifi", "ubiquiti"):
        # Zuerst die Modellkuerzel – die sind eindeutig. Erst wenn keines
        # passt, entscheidet die Anzahl der Schnittstellen: ein Switch hat
        # viele Ports, ein Access Point zwei bis drei.
        if any(wort in klein for wort in ("udm", "ugw", "uxg", "usg", "dream machine", "gateway")):
            typ, modul = "gateway", "if_mib"
        elif any(wort in klein for wort in ("usw", "switch")):
            typ, modul = "switch", "if_mib"
        elif any(wort in klein for wort in ("uap", "u6", "u7", "uk-", "access point", "nanohd", "flexhd")):
            typ, modul = "accesspoint", "ubiquiti_unifi"
        elif schnittstellen >= 8:
            typ, modul = "switch", "if_mib"
        else:
            typ, modul = "accesspoint", "ubiquiti_unifi"
    elif hersteller == "axis" or "camera" in klein:
        typ = "kamera"
    elif hersteller in ("synology", "qnap"):
        typ = "nas"
    elif hersteller in ("linux", "windows"):
        typ = "server"
    elif schnittstellen >= 8:
        typ = "switch"
    elif hersteller in ("cisco", "hp", "netgear", "dlink", "tplink", "zyxel",
                        "aruba", "juniper", "brocade", "huawei", "h3c", "mikrotik"):
        typ = "switch"

    return {**fund, "hersteller": hersteller, "typ": typ, "snmp_modul": modul}


# ===================================================================== #
# UniFi-Controller abfragen
# ===================================================================== #
UNIFI_TYPEN = {
    "uap": "accesspoint",
    "usw": "switch",
    "ugw": "gateway",
    "udm": "gateway",
    "uxg": "gateway",
}


def _unifi_oeffner(zeitlimit: int) -> urllib.request.OpenerDirector:
    # Der Controller hat praktisch immer ein selbst signiertes Zertifikat.
    kontext = ssl.create_default_context()
    kontext.check_hostname = False
    kontext.verify_mode = ssl.CERT_NONE
    return urllib.request.build_opener(
        urllib.request.HTTPSHandler(context=kontext),
        urllib.request.HTTPCookieProcessor(CookieJar()),
    )


def _unifi_abrufen(oeffner, url: str, koerper: dict | None, zeitlimit: int) -> Any:
    daten = json.dumps(koerper).encode("utf-8") if koerper is not None else None
    anfrage = urllib.request.Request(
        url,
        data=daten,
        headers={"Content-Type": "application/json", "Accept": "application/json"},
        method="POST" if daten else "GET",
    )
    with oeffner.open(anfrage, timeout=zeitlimit) as antwort:
        roh = antwort.read()
    return json.loads(roh.decode("utf-8")) if roh else {}


def unifi_geraete(
    basis_url: str,
    benutzer: str,
    passwort: str,
    sites: str = "all",
    zeitlimit: int = 15,
) -> list[dict[str, Any]]:
    """
    Holt alle adoptierten Geraete aus dem UniFi-Controller.

    Funktioniert mit beiden Bauarten: dem klassischen Controller
    (/api/login) und UniFi OS auf Cloud Key, Dream Machine und Co.
    (/api/auth/login, danach alles unter /proxy/network).
    """
    if not (basis_url and benutzer and passwort):
        return []

    basis = basis_url.rstrip("/")
    oeffner = _unifi_oeffner(zeitlimit)
    anmeldung = {"username": benutzer, "password": passwort}

    praefix = None
    for pfad, unterbau in (("/api/auth/login", "/proxy/network"), ("/api/login", "")):
        try:
            _unifi_abrufen(oeffner, basis + pfad, anmeldung, zeitlimit)
            praefix = unterbau
            break
        except (urllib.error.URLError, ValueError, OSError) as fehler:
            log.debug("UniFi-Anmeldung ueber %s nicht moeglich: %s", pfad, fehler)

    if praefix is None:
        log.warning("Anmeldung am UniFi-Controller %s fehlgeschlagen", basis)
        return []

    # Welche Standorte gibt es?
    standorte: list[str] = []
    if sites.strip().lower() in ("", "all"):
        try:
            antwort = _unifi_abrufen(oeffner, f"{basis}{praefix}/api/self/sites", None, zeitlimit)
            standorte = [s.get("name") for s in antwort.get("data", []) if s.get("name")]
        except (urllib.error.URLError, ValueError, OSError) as fehler:
            log.debug("Standortliste nicht abrufbar: %s", fehler)
    else:
        standorte = [t.strip() for t in sites.split(",") if t.strip()]
    if not standorte:
        standorte = ["default"]

    gefunden: list[dict[str, Any]] = []
    for standort in standorte:
        url = f"{basis}{praefix}/api/s/{urllib.parse.quote(standort)}/stat/device"
        try:
            antwort = _unifi_abrufen(oeffner, url, None, zeitlimit)
        except (urllib.error.URLError, ValueError, OSError) as fehler:
            log.warning("UniFi-Geraeteliste fuer Standort %s nicht abrufbar: %s", standort, fehler)
            continue

        for geraet in antwort.get("data", []):
            ip = geraet.get("ip") or ""
            if not ip:
                continue
            bauart = str(geraet.get("type") or "").lower()
            gefunden.append({
                "ip": ip,
                "quelle": "unifi",
                "sysname": geraet.get("name") or geraet.get("hostname") or "",
                "beschreibung": " ".join(filter(None, [
                    str(geraet.get("model") or ""),
                    str(geraet.get("version") or ""),
                ])).strip(),
                "objekt_id": "",
                "schnittstellen": 0,
                "hersteller": "unifi",
                "typ": UNIFI_TYPEN.get(bauart, "accesspoint"),
                "snmp_modul": "if_mib" if bauart == "usw" else "ubiquiti_unifi",
                "modell": str(geraet.get("model") or ""),
                "mac": str(geraet.get("mac") or ""),
                "unifi_standort": standort,
                "unifi_zustand": int(geraet.get("state") or 0),
            })

    log.info("UniFi-Controller meldet %d Geraete", len(gefunden))
    return gefunden


# ===================================================================== #
# Beides zusammenfuehren
# ===================================================================== #
def namen_vorschlagen(fund: dict[str, Any], belegt: set[str]) -> str:
    """
    Baut aus dem Systemnamen einen brauchbaren, eindeutigen Bezeichner.
    Faellt auf typ-letztes-oktett zurueck, wenn das Geraet keinen Namen
    hat – das ist immer noch besser als eine nackte IP im Dashboard.
    """
    roh = (fund.get("sysname") or "").strip().lower()
    roh = roh.split(".")[0]
    sauber = "".join(z if (z.isalnum() or z in "-_") else "-" for z in roh).strip("-")

    if not sauber or sauber in ("localhost", "unknown"):
        letztes = fund["ip"].rsplit(".", 1)[-1]
        sauber = f"{fund.get('typ', 'geraet')}-{letztes}"

    name = sauber[:60]
    if name in belegt:
        name = f"{name}-{fund['ip'].rsplit('.', 1)[-1]}"[:60]
    zaehler = 2
    while name in belegt:
        name = f"{sauber[:55]}-{zaehler}"
        zaehler += 1
    return name


# Nur bei diesen Geraetearten lohnt die LLDP-Abfrage. Ein Drucker oder
# eine USV hat keine Nachbarschaftstabelle, und jede Abfrage kostet Zeit.
LLDP_TYPEN = {"switch", "accesspoint", "gateway", "firewall"}


def topologie_erfassen(
    funde: Iterable[dict[str, Any]],
    community: str,
    zeitlimit: float = 3.0,
) -> list[dict[str, str]]:
    """
    Fragt bei allen Netzgeraeten die LLDP-Nachbarschaft ab und liefert
    eine Kantenliste.

    Jede Verbindung taucht zweimal auf – einmal von jeder Seite. Das ist
    Absicht: Meldet nur eine Seite die Verbindung, ist auf der anderen
    LLDP aus, und genau das will man im Dashboard sehen koennen.
    """
    kanten: list[dict[str, str]] = []

    for fund in funde:
        if fund.get("quelle") != "snmp":
            continue                      # ohne SNMP keine Abfrage moeglich
        if fund.get("typ") not in LLDP_TYPEN:
            continue

        try:
            nachbarn = lldp_nachbarn(fund["ip"], community, zeitlimit)
        except OSError as fehler:
            log.debug("LLDP-Abfrage an %s fehlgeschlagen: %s", fund["ip"], fehler)
            continue

        for nachbar in nachbarn:
            kanten.append({
                "geraet": fund.get("name") or fund.get("sysname") or fund["ip"],
                "adresse": fund["ip"],
                "lokal_port": nachbar["lokal_port"],
                "nachbar": nachbar["entfernt_geraet"],
                "nachbar_port": nachbar["entfernt_port"],
            })

    log.info("LLDP: %d Verbindungen erfasst", len(kanten))
    return kanten


def mermaid_diagramm(kanten: Iterable[dict[str, str]]) -> str:
    """
    Baut aus den Kanten einen Mermaid-Netzplan. Wird als Text
    ausgeliefert und laesst sich in jede Dokumentation einbetten.

    Doppelte Verbindungen (beide Seiten melden dieselbe Strecke) werden
    zu einer Linie zusammengefasst.
    """
    def kennung(name: str) -> str:
        sauber = "".join(z if z.isalnum() else "_" for z in name)
        return sauber[:40] or "unbekannt"

    knoten: dict[str, str] = {}
    linien: list[str] = []
    gesehen: set[frozenset] = set()

    for kante in kanten:
        a, b = kante["geraet"], kante["nachbar"]
        if not a or not b:
            continue
        knoten[kennung(a)] = a
        knoten[kennung(b)] = b

        paar = frozenset((a, b))
        if paar in gesehen:
            continue
        gesehen.add(paar)

        beschriftung = kante.get("lokal_port", "")
        if kante.get("nachbar_port"):
            beschriftung = f"{beschriftung} – {kante['nachbar_port']}".strip(" –")
        beschriftung = beschriftung.replace('"', "'")[:40]

        if beschriftung:
            linien.append(f'  {kennung(a)} ---|"{beschriftung}"| {kennung(b)}')
        else:
            linien.append(f"  {kennung(a)} --- {kennung(b)}")

    if not linien:
        return "graph TD\n  keine[Keine LLDP-Verbindungen gefunden]\n"

    kopf = ["graph TD"]
    kopf += [f'  {k}["{v}"]' for k, v in sorted(knoten.items())]
    return "\n".join(kopf + linien) + "\n"


def suchlauf(
    netze: Iterable[str],
    community: str,
    unifi: dict[str, str] | None = None,
    zeitlimit: float = 2.5,
    runden: int = 2,
    hoechstzahl: int = 4096,
) -> list[dict[str, Any]]:
    """
    Fuehrt beide Erkennungswege aus und liefert eine zusammengefuehrte,
    nach IP sortierte Liste. Bei doppelten Adressen gewinnt der
    UniFi-Controller – der kennt den richtigen Namen und das Modell.
    """
    ergebnis: dict[str, dict[str, Any]] = {}

    for fund in snmp_suchlauf(netze, community, zeitlimit, runden, hoechstzahl):
        ergebnis[fund["ip"]] = einordnen(fund)

    if unifi:
        for fund in unifi_geraete(
            unifi.get("url", ""), unifi.get("benutzer", ""),
            unifi.get("passwort", ""), unifi.get("sites", "all"),
        ):
            vorher = ergebnis.get(fund["ip"], {})
            # SNMP-Details behalten, UniFi-Angaben haben Vorrang
            ergebnis[fund["ip"]] = {**vorher, **{k: v for k, v in fund.items() if v != ""}}

    def sortierschluessel(eintrag: dict[str, Any]) -> tuple:
        try:
            return (0, int(ipaddress.ip_address(eintrag["ip"])))
        except ValueError:
            return (1, 0)

    return sorted(ergebnis.values(), key=sortierschluessel)
