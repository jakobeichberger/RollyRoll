# Active Directory im Betrieb

Zwei Dinge, die leicht verwechselt werden:

| Frage | Wo |
|---|---|
| Ist das Verzeichnis richtig **eingestellt**? | Dashboard „Sicherheits-Baseline" |
| **Läuft** das Verzeichnis überhaupt? | Dashboard „Active Directory – Betrieb" |

Diese Seite behandelt die zweite Frage. Der Grund für eine eigene Prüfung:
Der Dienst `NTDS` läuft auch dann noch fröhlich weiter, wenn sich in der
Schule niemand mehr anmelden kann. „Dienst läuft" ist keine Aussage über die
Funktion.

---

## Warum nicht `repadmin` und `dcdiag`

Weil deren Ausgabe übersetzt ist. Auf einem deutschen Server steht dort etwas
anderes als auf einem englischen, und jeder Parser darauf ist eine Zeitbombe –
spätestens beim nächsten Server mit anderem Sprachpaket.

Der Sammler geht deshalb ausschließlich über das PowerShell-Modul
`ActiveDirectory` und über WMI. Objekteigenschaften wie
`LastReplicationSuccess` und WMI-Zustandsnummern sind überall gleich.

Das Modul gehört zur AD-DS-Rolle und ist auf jedem Domänencontroller vorhanden.
Fehlt es doch, meldet das der Alarm `AdModulFehlt`; nachinstallieren mit
`Add-WindowsFeature RSAT-AD-PowerShell`.

---

## Was geprüft wird

### Replikation

Der wichtigste Teil. Bricht die Replikation, merkt man es tagelang nicht – bis
Gruppenrichtlinien auf der Hälfte der Rechner veraltet sind und
Kennwortänderungen nicht mehr ankommen.

Zwei Blickwinkel, die sich ergänzen:

* **Fehlercode** (`schule_ad_replikation_fehlercode`) – 0 heißt in Ordnung,
  alles andere ist ein Win32-Fehlercode zum Nachschlagen.
* **Zeit seit dem letzten Erfolg** (`schule_ad_replikation_letzter_erfolg_sekunden`)
  – der aussagekräftigere Wert. Ein Partner kann seit Tagen still sein, ohne je
  einen Fehler gemeldet zu haben.

Die Zeitschiene, die zählt: Ab **60 Tagen** ohne Replikation wird ein
Domänencontroller endgültig ausgeschlossen (Tombstone) und muss neu aufgesetzt
werden. Der Alarm greift nach einer Stunde – reichlich Vorlauf.

### SYSVOL und NETLOGON

Klemmt die SYSVOL-Replikation, werden keine Gruppenrichtlinien mehr verteilt.
In dieser Umgebung besonders heikel: **Das Agent-Skript liegt in SYSVOL** und
käme auf neuen Rechnern nicht mehr an.

Geprüft wird, ob beide Freigaben existieren und in welchem Zustand DFSR ist:

| Zustand | Bedeutung |
|---|---|
| 4 | normal |
| 2 | Erstsynchronisierung – nach einem neuen DC normal, über Stunden nicht |
| 5 | Fehler |

Dazu eine Prüfung, die selten nötig ist, aber teuer wenn sie fehlt: Repliziert
SYSVOL noch über das alte **FRS**? Windows Server 2016 und neuer unterstützen
das nicht mehr – ein neuer Domänencontroller lässt sich dann nicht aufnehmen.
Das merkt man üblicherweise im ungeeignetsten Moment. Der Alarm
`SysvolNochAufFrs` warnt vorher.

### FSMO-Rollen

Wer hält welche Rolle, und antwortet der Halter auf LDAP? Je nach Rolle fällt
Unterschiedliches aus:

* ohne **PDC-Emulator**: Kennwortänderungen und Zeitsynchronisierung
* ohne **RID-Master**: keine neuen Konten mehr anlegbar
* ohne **Infrastrukturmaster**: Gruppenmitgliedschaften über Domänen hinweg

### LDAP-Antwortzeit

Der beste Frühindikator für „die Anmeldung dauert heute so lang". Gemessen wird
gegen den eigenen Verzeichnisdienst, damit die Zahl nicht von der Netzstrecke
abhängt. Alarm ab einer Sekunde.

Steigt die Kurve über Tage langsam an, wird der Domänencontroller knapp –
meist Arbeitsspeicher oder Plattenlatenz auf der NTDS-Datenbank. Der Verlauf im
Dashboard zeigt das deutlich früher als ein Schwellwert.

### Kontosperrungen

Ereignis 4740, gezählt über eine und über 24 Stunden. Ein plötzlicher Anstieg
ist entweder ein Kennwortangriff oder ein Gerät mit gespeichertem altem
Kennwort.

Der Unterschied zeigt sich daran, ob es **viele verschiedene Konten** sind
(Angriff) oder **immer dasselbe** (vergessenes Handy im Spind, altes
Dienstkonto). Ab 10 Sperrungen pro Stunde eine Warnung, ab 40 kritisch – beides
läuft über die Sicherheitsadresse.

> Ereignis 4740 entsteht nur, wenn die Überwachung der Kontoverwaltung
> eingeschaltet ist. Die Sicherheits-Baseline prüft das ohnehin.

---

## Ausrollen

Nichts zu tun. Das Skript `Collect-AdHealth.ps1` kommt mit dem Agenten mit und
richtet sich **nur auf Domänencontrollern** als geplante Aufgabe ein –
`SchulMonitoring-AdPruefung`, alle 15 Minuten, mit zufälligem Versatz, damit
mehrere DCs nicht im Gleichschritt abfragen.

Auf allen anderen Geräten passiert nichts. Wird ein DC herabgestuft, entfernt
der Agent die Aufgabe beim nächsten Lauf von selbst.

Voraussetzung ist **Agent-Version 1.3.0**. Prüfen:

```powershell
Get-Content "$env:ProgramData\SchulMonitoring\agent-version.txt"
Get-ScheduledTask SchulMonitoring-AdPruefung | Get-ScheduledTaskInfo
```

Steht dort noch etwas Älteres: aktuelle Skripte nach SYSVOL kopieren, auf der
VM `sudo ./stack/pakete-holen.sh` ausführen, Gerät neu starten.

---

## Wenn Werte fehlen

Erst nachsehen, ob die Aufgabe überhaupt läuft:

```powershell
Get-ScheduledTask SchulMonitoring-AdPruefung | Get-ScheduledTaskInfo
```

Dann das Skript einmal von Hand laufen lassen – mit `-Verbose` schreibt es auf,
woran ein Abschnitt scheitert:

```powershell
& "$env:ProgramData\SchulMonitoring\Collect-AdHealth.ps1" -Verbose
Get-Content "$env:ProgramData\SchulMonitoring\textfile\schule_ad.prom"
```

Jeder Abschnitt läuft in einem eigenen `try`/`catch`: Scheitert einer, fehlen
nur dessen Werte, alles andere kommt weiterhin an. Welcher es war, steht in
`schule_ad_pruefabschnitt_erfolgreich`.

---

## Metriknamen

| Metrik | Inhalt |
|---|---|
| `schule_ad_replikation_fehlercode` | 0 = in Ordnung, sonst Win32-Fehlercode |
| `schule_ad_replikation_letzter_erfolg_sekunden` | Sekunden seit der letzten erfolgreichen Replikation |
| `schule_ad_replikation_fehlversuche_hintereinander` | Fehlschläge in Folge |
| `schule_ad_freigabe_vorhanden` | SYSVOL und NETLOGON |
| `schule_ad_dfsr_zustand` | 4 = normal, 5 = Fehler |
| `schule_ad_sysvol_migrationszustand` | 3 = vollständig auf DFSR |
| `schule_ad_fsmo_inhaber` / `schule_ad_fsmo_erreichbar` | Rolleninhaber und Erreichbarkeit |
| `schule_ad_ldap_antwortzeit_sekunden` | Dauer einer LDAP-Anfrage |
| `schule_ad_globaler_katalog_erreichbar` | Port 3268 |
| `schule_ad_kontosperrungen_1h` / `_24h` | Ereignis 4740 |
| `schule_ad_konten_gesperrt` | derzeit gesperrte Konten |
