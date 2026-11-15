# Kontrollierte Aufbewahrung im synthetischen Piloten

**T18-Arbeitsstand, keine produktive Retention-/WORM-Freigabe.**
Die Regeln sind von Meldeterminen, Fallfreigaben, PSUR-Laufzeiten und
Wiederherstellungsfreigaben getrennt. Kein Fristablauf schliesst einen PSUR.

## Zwei ausdrueckliche Datenklassen

- `ApplicableImplantTechnicalDocumentation`: synthetisches Mindestfristbeispiel
  nach MDR Art. 10(8). Bestaetigtes letztes Inverkehrbringen plus 15 Jahre,
  bis einschliesslich des betreffenden lokalen Kalendertags in `Europe/Dublin`.
  Ohne bestaetigten Lifecycle bleibt das Ergebnis unbekannt; Eingang, Upload,
  Datenbankerfassung und Snapshot-Freeze ersetzen diesen Zeitpunkt nicht.
  Auch nach Ablauf ist physische Dokumentloeschung im Piloten gesperrt.
- `UnreferencedSyntheticRawQuarantine`: ausschliesslich serverseitig erzeugte
  synthetische Testbytes. Ein expliziter simulierter Empfang startet sieben
  volle Tage; exakt an der Grenze ist das Mindestintervall beendet. Dies ist
  keine allgemeine Rohdaten-, Personendaten- oder Produktionsregel.

Der Quality Manager dokumentiert den synthetischen Lifecycle mit `DEMO-`-
Herkunftsreferenz, Begruendung, Identitaet und Version. Das ist eine menschliche
synthetische Erklaerung, kein automatisch verifizierter realer Vertriebsbeleg.
Regelversion: `cohors-retention-demo-2026.1`. Die unabhaengigen Datumsreferenzen
bleiben unveraendert in
`tests\Cohors.Tests\Fixtures\cohors-retention-recovery-references.json`,
SHA256 `66de6157cd25132ab691a371eb386a3fe9060a42401dca61f8a09402d787c7e9`.

## Aktivierung und Bedienung

Standard ist `Retention:PhysicalMode=Disabled`. Das explizite
`SyntheticLocal`-Profil wird nur in `Development`/`Testing`, mit lokalen
Objekten und ohne Azure Search akzeptiert. Es gibt keinen Azure-Loeschpfad.
Fuer einen eigens freigegebenen lokalen Testprozess kann vor dem Start
`$env:Retention__PhysicalMode = 'SyntheticLocal'` gesetzt werden. Bestehende
Demo, Datenbank und gemeinsam genutzte Dienste deshalb nicht neu initialisieren
oder konkurrierend mit alten Anwendungsassemblies starten.

Die Oberflaeche **Aufbewahrung** bietet versionierte Lifecycle-Erklaerungen,
Klassifikation konkreter genehmigter synthetischer Dokumentversionen,
isolierte Rohobjekte, Rechenwege, Referenzsperren, Hold, Autorisierung und
kontrollierten Abgleich eines Teilvorgangs. Rohinhalte werden dort nicht
offengelegt oder als regulatorische Evidenz umklassifiziert.

Alle Aenderungen verlangen ein menschliches `QualityManager`-Konto,
berechtigten Produktscope, erwartete Version und Begruendung. Die
Loeschfreigabe bindet Eintrag, Objekt, Datenklasse, Policy, Zeitbasis und
Scope-Hash. Die bestaetigte Kopienpruefung bedeutet, dass Backup-/Export-/
Cache-/Agentenkopien inventarisiert und beruecksichtigt sind; unbekannte oder
ungeloeste Kopien sind keine positive Freigabe. Dies ist eine dokumentierte
menschliche Feststellung, keine automatische Garantie ueber fremde Systeme.

Hold-Aenderungen vor dem Beginn verwerfen vorhandene Autorisierungen.
Begonnene/fehlgeschlagene physische Schritte brauchen Abgleich, keinen
nachtraeglichen Hold, der faelschlich bereits entfernte Daten als erhalten
ausgeben wuerde. Medizinische und RA-Freigaben bleiben davon unberuehrt:
Es wird keine dritte PSUR-/Fallpflichtsignatur eingefuehrt.

## Physischer und transaktionaler Vertrag

`RetentionRecord` traegt `cohors-retention-record-1`. Seine Objektbindung
traegt `cohors-retention-object-1` mit expliziter Datenklasse, `objectKey`
und SHA256. Diese Felder sind keine umbenannten Ausnahmen fuer Recovery.

Regulatorische Objekte bleiben unveraendert im bisherigen Hash-Namensraum.
Neue Rohobjekte liegen gesondert in `raw-quarantine\<hash>`; der logische
Objektschluessel lautet `raw-quarantine/<hash>`. Auch gleiche Hashes in
anderen Datenklassen beziehungsweise im regulatorischen Objektbereich
blockieren eine behauptete vollstaendige Rohdatenloeschung.

Vor der Entfernung prueft SQL aktuelle und historische Aggregate,
eingefrorene Pakete, gespeicherte Agentenkontexte, Outbox-Payloads und
Command-Ergebnisse. Nur der eigene, ausdruecklich typisierte
Aufbewahrungseintrag samt seinen historischen Command-Ergebnissen ist
Metadatum statt weiterer Nutzdatenbindung. Historische/shared-hash-
Referenzen werden nicht ausgenommen. Fremde Produktscopes erscheinen in
der Diagnose nicht mit ihren Inhalten; sie bleiben trotzdem Loeschblocker.

Normale Domaenenschreibvorgaenge halten eine gemeinsame Referenzsperre;
der destruktive Scan und dessen Abschluss halten sie exklusiv. Neue
Bindungen an autorisierte, laufende, fehlgeschlagene oder geloeschte
Rohobjekte werden serverseitig abgewiesen. Die normalen Transaktionen
bleiben `READ COMMITTED`, nicht global `SERIALIZABLE`.

Die Datenbank und das Dateisystem bilden **keine** gemeinsame Transaktion:

1. `Deleting` mit unveraenderter Autorisierungs-ID und Versuchszahl wird
   atomar mit Audit/Outbox gespeichert.
2. Nach dem gesperrten Referenzscan werden die wirklichen Rohbytes vor
   dem Loeschen gegen SHA256 und striktes UTF-8 geprueft. Ein gespueltes,
   auftragsgebundenes lokales Intent-Journal geht der Entfernung voraus.
3. Der lokale Beleg `cohors-raw-deletion-receipt-1` unter
   `.deletion-receipts\<hash>.json` bestaetigt die beobachtete Entfernung.
   Unerwartet fehlende Bytes ohne passenden Intent-/Abschlussbeleg sind
   ein Fehler, kein Erfolg. Ein wieder aufgetauchtes Objekt oder eine
   andere Autorisierungs-ID wird nicht still akzeptiert.
4. Erst der atomare SQL-Abschluss entfernt die betroffene
   `SearchProjection`, schreibt `Deleted`, Beleg, Audit, Outbox und
   Command-Receipt. Ein Datenbank-/Projektionsfehler laesst einen
   ausdruecklichen Teilvorgang offen.
5. Kontrollierter Retry mit aktueller Version verwendet dieselbe
   Autorisierungs-ID und den vorhandenen physischen Beleg. Ein bereits
   bekannter erfolgreicher Command wird nicht nochmals ausgefuehrt.

Der Audit-/Loeschbeleg kopiert keine geloeschten Rohinhalte. Das lokale
Journal ist kein extern signierter, unabhaengig geschuetzter Ledger-Digest.
Aktive Dateien zu entfernen ist auch kein Nachweis sicherer
Datentraegerueberschreibung, Cloud-Sync-Bereinigung oder produktiver
Backup-Loeschung.

## Recovery-Anschluss und noch offene Abnahme

Die uebergebenen T20-Dateien sind integriert und erkennen die expliziten
Retention-Vertraege statt einer pauschalen `objectKey`-Ausnahme.
Format-v2-Backups fuehren regulatorische Bytes, aktive Rohquarantaene
und physische Loeschjournale getrennt. Nur ein passender aktueller
SQL-`Deleted`-Stand plus exaktes Journal entbindet die eigene historische
Rohdaten-Metadatenreferenz. Jede unabhaengige oder regulatorische
Shared-Hash-Bindung bleibt erforderlich; fehlende Objekte werden nicht
allgemein ausgenommen.

Restore verlangt bei Rohdaten ein aktuell bestaetigtes Manifest und
einen separat vorgehaltenen Digest. Ein aelterer Backupstand, dessen
Rohdaten-Katalog davon abweicht, wird abgewiesen, nicht automatisch
umgebaut. Fehlende Aktualitaetsbestaetigung, Teilvorgaenge, fehlende
Journale oder wieder aufgetauchte Bytes sind Sperren. Der Operator
muss den aktuellen Stand kennen; das Offline-Skript kann diese
Aktualitaet nicht unabhaengig beweisen. Einzelheiten und Aufrufe:
[Wiederherstellung](recovery.md#t18-explizite-rohdaten--und-loeschklassen).

Ausgefuehrt sind **20/20** gezielte Nicht-Docker-Tests einschliesslich
echter lokaler Objekt-/PowerShell-Vertragstests, **5/5** Service-/SQL-Faelle
nach Korrektur des Autorisierungs-Hashfeldnamens und **13/13** integrierte
Recovery-Faelle. Das sind getrennte Laeufe, keine zusammengesetzte Vollsuite.
Ein spaeterer kompletter Anwendungslauf blieb **109/110** wegen eines
bisher ungeklaerten `retention_object_io` im parallelen Raw-Delete-Test.
Die danach bestandenen 5/5 und 110/110 erklaeren die damalige I/O-Ursache
nicht; genaue Ursache/HResult/Schritt waren damals nicht erhalten.
InnerException und gefilterte Testdiagnostik sind inzwischen vorgesehen.

Die erste Browser-Teilabnahme prueft ausschliesslich das unveraenderte
Standardprofil **Disabled**: gesperrter physischer Clientpfad, versionierter
Lifecycle und regulatorisches Mindestfristbeispiel. **Der isolierte
physische Rohdatenloeschpfad ist nicht freigegeben.** Aktuelle
Recovery-Wiederholung nach Diagnostikaenderungen und abschliessende
Linux-/Gesamtabnahme bleiben offen. Die frueheren T20-Baselineergebnisse
werden nicht als Nachweis der Erweiterung ausgegeben. Siehe
[Nachweisstand](validation.md).
