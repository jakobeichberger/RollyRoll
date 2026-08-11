#!/usr/bin/env python3
"""
Tests der Geraeteerkennung.

    python3 stack/registrar/test_erkennung.py

Ohne Fremdbibliotheken, damit das auch auf der Monitoring-VM laeuft, wo
nur die Standardbibliothek und PyYAML installiert sind.

Geprueft werden drei Dinge:

  1. Die SNMP-Kodierung. Gegen die allgemein bekannte Bytefolge von
     sysDescr.0 und auf Laengenkonsistenz der ganzen Struktur. Ein Fehler
     hier faellt sonst erst auf, wenn ein echtes Geraet nicht antwortet –
     und dann sucht man ihn im Netz statt im Code.

  2. Die Einordnung. An echten Geraeteangaben, die so im Schulnetz
     vorkommen. Beim Bauen sind hier zwei Fehler aufgefallen: ein UDM-Pro
     als Switch und ein HP-Drucker als Switch.

  3. Der Suchlauf und die LLDP-Abfrage gegen ein simuliertes Geraet.
"""

from __future__ import annotations

import socket
import sys
import threading
import time

import erkennung as e

FEHLER: list[str] = []


def pruefe(bedingung: bool, was: str, zusatz: str = "") -> None:
    if bedingung:
        print(f"  ok    {was}")
    else:
        FEHLER.append(was)
        print(f"  FEHLT {was}" + (f"  ({zusatz})" if zusatz else ""))


# ===================================================================== #
# 1. SNMP-Kodierung
# ===================================================================== #
def test_kodierung() -> None:
    print("\nSNMP-Kodierung")

    # sysDescr.0 hat eine allgemein bekannte Kodierung.
    oid = e._oid_kodieren("1.3.6.1.2.1.1.1.0")
    pruefe(oid == bytes.fromhex("06082b06010201010100"),
           "sysDescr.0 wird richtig kodiert", oid.hex())

    # Grosse Zahlen brauchen Basis-128 (Ubiquiti hat die Nummer 41112)
    gross = "1.3.6.1.4.1.41112.1.6"
    pruefe(e._oid_lesen(e._oid_kodieren(gross)[2:]) == gross,
           "grosse Herstellernummern ueberstehen den Rundlauf")

    # Jede Laengenangabe muss exakt aufgehen
    def durchlaufen(daten: bytes, pfad: str = "wurzel") -> bool:
        pos = 0
        while pos < len(daten):
            kennung, inhalt, neu = e._tlv_lesen(daten, pos)
            if kennung in (0x30, 0xA0, 0xA2, 0xA5):
                if not durchlaufen(inhalt, f"{pfad}/{kennung:02x}"):
                    return False
            pos = neu
        return pos == len(daten)

    pruefe(durchlaufen(e._get_paket("public", ["1.3.6.1.2.1.1.1.0"], 1)),
           "GetRequest ist strukturell schluessig")
    pruefe(durchlaufen(e._get_paket("gemeinschaft", e.ABFRAGE_OIDS, 65535)),
           "GetRequest mit vier Werten ist schluessig")
    pruefe(durchlaufen(e._getbulk_paket("public", "1.0.8802.1.1.2.1.4.1.1.9", 1)),
           "GetBulk ist strukturell schluessig")

    # Inhalte ueber 127 Byte brauchen die lange Laengenform
    lang = e._tlv(0x04, b"x" * 300)
    _, inhalt, ende = e._tlv_lesen(lang, 0)
    pruefe(len(inhalt) == 300 and ende == len(lang),
           "lange Laengenform wird richtig geschrieben und gelesen")


# ===================================================================== #
# 2. Einordnung
# ===================================================================== #
def test_einordnung() -> None:
    print("\nEinordnung gefundener Geraete")

    faelle = [
        ("USW-Pro-24-PoE, 7.0.50.14550", "1.3.6.1.4.1.41112.1.6", 26, "unifi", "switch"),
        ("USW-Lite-8-PoE, 7.0.50",       "1.3.6.1.4.1.41112.1.6", 10, "unifi", "switch"),
        ("U6-Pro, 6.6.65",               "1.3.6.1.4.1.41112.1.6",  3, "unifi", "accesspoint"),
        ("UAP-AC-Pro-Gen2, 6.6.65",      "1.3.6.1.4.1.41112.1.6",  4, "unifi", "accesspoint"),
        ("U7-Pro-Max, 7.0.92",           "1.3.6.1.4.1.41112.1.6",  3, "unifi", "accesspoint"),
        # Beim Bauen falsch eingeordnet: viele Schnittstellen, aber ein Gateway
        ("UDM-Pro, 3.2.12",              "1.3.6.1.4.1.41112.1.6",  9, "unifi", "gateway"),
        ("USG-3P, 4.4.56",               "1.3.6.1.4.1.41112.1.6",  6, "unifi", "gateway"),
        ("FortiGate-60F v7.2.8",         "1.3.6.1.4.1.12356.101.1.60", 12, "fortinet", "firewall"),
        ("APC Web/SNMP Management Card", "1.3.6.1.4.1.318.1.3.27", 1, "apc", "usv"),
        ("Brother HL-L2350DW series",    "1.3.6.1.4.1.2435.2.3.9.1", 2, "brother", "drucker"),
        # Beim Bauen falsch eingeordnet: HP baut Drucker UND Switches
        ("HP ETHERNET MULTI-ENVIRONMENT", "1.3.6.1.4.1.11.2.3.9.1", 1, "hp", "drucker"),
        ("HP J9773A 2530-24G-PoEP Switch", "1.3.6.1.4.1.11.2.3.7.11.157", 28, "hp", "switch"),
        ("Kyocera ECOSYS M5526cdw",      "1.3.6.1.4.1.1347.41",    2, "kyocera", "drucker"),
        ("Linux srv-datei 6.8.0-51",     "1.3.6.1.4.1.8072.3.2.10", 3, "linux", "server"),
        ("AXIS P3245-LV Network Camera", "1.3.6.1.4.1.368.4.1",    2, "axis", "kamera"),
        ("irgendein Kastl ohne Kennung", "",                       1, "unbekannt", "sonstiges"),
    ]

    for text, oid, anzahl, soll_hersteller, soll_typ in faelle:
        fund = e.einordnen({"ip": "10.0.0.9", "beschreibung": text, "objekt_id": oid,
                            "schnittstellen": anzahl, "sysname": ""})
        pruefe(fund["hersteller"] == soll_hersteller and fund["typ"] == soll_typ,
               f"{text[:38]:40} -> {soll_hersteller}/{soll_typ}",
               f"war {fund['hersteller']}/{fund['typ']}")

    # Namensvorschlaege muessen eindeutig bleiben
    belegt: set[str] = set()
    namen: list[str] = []
    for i in (1, 2, 3):
        n = e.namen_vorschlagen({"ip": f"10.0.0.{i}", "sysname": "switch", "typ": "switch"}, belegt)
        belegt.add(n)
        namen.append(n)
    pruefe(len(set(namen)) == 3, "gleiche Systemnamen ergeben eindeutige Bezeichner", str(namen))

    ohne = e.namen_vorschlagen({"ip": "10.0.0.44", "sysname": "", "typ": "drucker"}, set())
    pruefe(ohne == "drucker-44", "Geraete ohne Namen bekommen typ-oktett", ohne)


# ===================================================================== #
# 3. Suchlauf und LLDP gegen ein simuliertes Geraet
# ===================================================================== #
TABELLE: dict[str, bytes] = {}


def _fuellen() -> None:
    def setze(basis: str, index: str, wert: str) -> None:
        TABELLE[f"{basis}.{index}"] = e._tlv(0x04, wert.encode())
    setze(e.LLDP_ENTFERNT_SYSNAME, "1.5.1", "ap-turnsaal")
    setze(e.LLDP_ENTFERNT_PORTBES, "1.5.1", "eth0")
    setze(e.LLDP_ENTFERNT_SYSNAME, "1.24.2", "sw-core-01")
    setze(e.LLDP_ENTFERNT_PORTBES, "1.24.2", "Port 12")
    setze(e.LLDP_LOKAL_PORTBES, "5", "Port 5")
    setze(e.LLDP_LOKAL_PORTBES, "24", "Port 24 Uplink")


def _simuliertes_geraet(port: int, bereit: threading.Event) -> None:
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.bind(("127.0.0.1", port))
    bereit.set()
    sortiert = sorted(TABELLE, key=lambda o: [int(x) for x in o.split(".")])

    while True:
        try:
            daten, absender = s.recvfrom(65535)
        except OSError:
            return
        _, inhalt, _ = e._tlv_lesen(daten, 0)
        pos = 0
        _, _, pos = e._tlv_lesen(inhalt, pos)
        _, _, pos = e._tlv_lesen(inhalt, pos)
        art, pdu, _ = e._tlv_lesen(inhalt, pos)

        p = 0
        for _ in range(3):
            _, _, p = e._tlv_lesen(pdu, p)
        _, bindungen, _ = e._tlv_lesen(pdu, p)
        _, erste, _ = e._tlv_lesen(bindungen, 0)
        _, oid_roh, _ = e._tlv_lesen(erste, 0)
        gefragt = e._oid_lesen(oid_roh)

        if art == 0xA0:      # GetRequest: Systemangaben
            paare = [
                ("1.3.6.1.2.1.1.1.0", e._tlv(0x04, b"USW-Lite-16-PoE, 7.0.50")),
                ("1.3.6.1.2.1.1.2.0", e._oid_kodieren("1.3.6.1.4.1.41112.1.6")),
                ("1.3.6.1.2.1.1.5.0", e._tlv(0x04, b"sw-edv-saal-1")),
                ("1.3.6.1.2.1.2.1.0", e._ganzzahl(18)),
            ]
            vb = b"".join(e._tlv(0x30, e._oid_kodieren(o) + v) for o, v in paare)
        else:                # GetBulk: Tabelle ablaufen
            def kleiner(a: str, b: str) -> bool:
                return [int(x) for x in a.split(".")] < [int(x) for x in b.split(".")]
            folgende = [o for o in sortiert if kleiner(gefragt, o)][:25]
            vb = (b"".join(e._tlv(0x30, e._oid_kodieren(o) + TABELLE[o]) for o in folgende)
                  or e._tlv(0x30, e._oid_kodieren("1.9.9") + e._tlv(0x82, b"")))

        antwort_pdu = e._ganzzahl(1) + e._ganzzahl(0) + e._ganzzahl(0) + e._tlv(0x30, vb)
        s.sendto(e._tlv(0x30, e._ganzzahl(1) + e._tlv(0x04, b"public")
                        + e._tlv(0xA2, antwort_pdu)), absender)


def test_suchlauf() -> None:
    print("\nSuchlauf und Netzplan")

    _fuellen()
    bereit = threading.Event()
    threading.Thread(target=_simuliertes_geraet, args=(161, bereit), daemon=True).start()
    if not bereit.wait(timeout=5):
        pruefe(False, "simuliertes Geraet startet", "Port 161 nicht belegbar (Rechte?)")
        return

    treffer = e.snmp_suchlauf(["127.0.0.1/32"], "public", zeitlimit=2.0, runden=1)
    pruefe(len(treffer) == 1, "der Suchlauf findet das Geraet")
    if not treffer:
        return

    fund = e.einordnen(treffer[0])
    pruefe(fund["sysname"] == "sw-edv-saal-1", "der Systemname kommt an", fund["sysname"])
    pruefe(fund["typ"] == "switch" and fund["snmp_modul"] == "if_mib",
           "das Geraet wird als Switch mit if_mib eingeordnet")

    nachbarn = e.lldp_nachbarn("127.0.0.1", "public", zeitlimit=2.0)
    pruefe(len(nachbarn) == 2, "beide LLDP-Nachbarn werden gefunden", str(nachbarn))

    nach_name = {n["entfernt_geraet"]: n for n in nachbarn}
    pruefe("ap-turnsaal" in nach_name and nach_name["ap-turnsaal"]["lokal_port"] == "Port 5",
           "der Access Point haengt an Port 5")
    pruefe("sw-core-01" in nach_name and nach_name["sw-core-01"]["lokal_port"] == "Port 24 Uplink",
           "der Uplink wird richtig zugeordnet")

    # Ein leerer Bereich darf nicht haengen bleiben
    beginn = time.monotonic()
    leer = e.snmp_walk("127.0.0.1", "public", "1.3.6.1.4.1.99999", zeitlimit=1.0)
    pruefe(len(leer) == 0 and time.monotonic() - beginn < 5,
           "ein leerer Bereich endet zuegig statt endlos zu laufen")

    fund["name"] = "sw-edv-saal-1"
    kanten = e.topologie_erfassen([fund], "public")
    pruefe(len(kanten) == 2, "die Kantenliste entsteht aus den Nachbarn")

    plan = e.mermaid_diagramm(kanten)
    pruefe("graph TD" in plan and "ap_turnsaal" in plan and "sw_core_01" in plan,
           "der Mermaid-Netzplan enthaelt alle Knoten")

    # Ein Drucker hat keine Nachbarschaftstabelle - gar nicht erst fragen
    drucker = {"ip": "10.0.0.30", "quelle": "snmp", "typ": "drucker", "name": "drucker-01"}
    pruefe(e.topologie_erfassen([drucker], "public") == [],
           "bei Druckern wird die LLDP-Abfrage uebersprungen")


def main() -> int:
    print("Tests der Geraeteerkennung")
    test_kodierung()
    test_einordnung()
    test_suchlauf()

    print()
    if FEHLER:
        print(f"{len(FEHLER)} Test(s) fehlgeschlagen:")
        for f in FEHLER:
            print(f"  - {f}")
        return 1
    print("Alle Tests bestanden.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
