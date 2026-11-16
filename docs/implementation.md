# Implementierungspraezisierungen

Diese Arbeitsfassung ergaenzt den unveraendert uebernommenen freigegebenen [Entwurf](architecture/approved-design.md). Sie aendert weder den fachlichen Gesamtauftrag noch die Deployment-Grenzen.

## Fachlicher und technischer Zuschnitt

Die Pilotdomaene besteht aus versionierten SQL-Aggregaten mit typisierten .NET-Vertraegen. Relationale Tabellen fuer Historie, Befehls-Idempotenz, Fristenprojektion, Audit und Outbox ergaenzen den kanonisch serialisierten Aggregatinhalt. SQL bleibt fuehrend; Search ist nicht die Sicherheitsakte. Der Pilot ist bewusst kein Netz aus sechs Agenten-Microservices.

Version, Identitaet, Produktscope und Befehlsinhalt werden serverseitig geprueft. Eine erwartete Version ist keine optionale UI-Empfehlung. Historische Belege und Freigaben bleiben nachvollziehbar. Der Auditnachweis muss sowohl die Historie als auch den aktuellen SQL-Zustand erfassen; eine intakte historische Hashkette allein beweist keinen unveraenderten aktuellen Datensatz.

Schreibtransaktionen nutzen `READ COMMITTED`, transaktionsgebundene SQL-Application-Locks fuer Befehl und Aggregat sowie einen Update-Lock auf den aktuellen Datensatz. Das serialisiert konkurrierende Domaenenbefehle, ohne bei verschiedenen Idempotenzschluesseln leere `Commands`-Indexbereiche bis zum Commit gegeneinander zu sperren. Ein tatsaechlicher SQL-Paralleltest hatte diesen Range-Lock-Deadlock sichtbar gemacht. Manifest-/Portfolio-Lesungen verwenden dagegen eine konsistente serialisierbare Lesetransaktion.

Ein idempotenter Claim ist nicht gleichbedeutend mit einer zweiten Activity-Ausfuehrungsberechtigung. Eine persistierte zufaellige Owner-ID bestimmt den einzigen ausfuehrenden Aufrufer. Einzelne Modellantworten werden vor fachlicher Uebernahme als eigene Auftraege persistiert; Wiederholungen verwenden nur eindeutig abgeschlossene Antworten. Aktive, fehlgeschlagene oder unklare Modellauftraege werden nicht verdeckt erneut gesendet.

## Lokale und Azure-Abhaengigkeiten

| Bestandteil | Lokal | Zielarchitektur / separate Abnahme |
| --- | --- | --- |
| Fuehrender Zustand | SQL Server 2022 Developer im eigenen Docker-Volume | Azure SQL, Entra-only, minimale Domaenenrollen |
| Objekte | Inhaltsadressierte lokale Dateien mit SHA-256-Pruefung | Blob, Managed Identity, private Endpunkte |
| Retrieval | SQL-abgeleitete Projektion, autoritative Freigabe-/Produkt-/Zeitfilter | Azure AI Search mit erneuter SQL-/Objektpruefung |
| Identitaet | Getrennte PBKDF2-Konten, HttpOnly-Cookies, CSRF, Loopback-only | Entra Delegated Scope, App-Rollen, Produktscope aus Serverkonfiguration |
| Agenten | Explizit markierter Replay oder sichtbarer Fehler; In-Process nur Development/Testing | Eine getrennte interne Agent Runtime mit eigener Identitaet, sechs logische Rollen; Foundry mit kontrolliertem EU-Mitgliedstaaten-Nachweis |
| Orchestrierung | Sichtbar gekennzeichnete lokale Outbox-/Watchdog-Emulation; Functions-Container nur lokale Hostpruefung | Zwei .NET-Isolated Flex-Function-Apps als Codepakete/OneDeploy, getrennte Operations-Funktion |

Live-Konfiguration ist keine Live-Abnahme. Ohne genehmigte Azure-Umgebung werden weder Modelle angelegt noch Endpoint-/Netzwerk-/Identity-Erfolge behauptet.

## Freigabe, Versand und unbekannter Empfang

Der Scope bindet Assessment/Evidenz/QA, Berichtsversion, Meldeart, Sprache, Jurisdiktion, Empfaenger, Formular, Schema, Regelversion, Anlagen und **explizite regulatorische Verpflichtungs-IDs**.

Das echte MIR-XML wird gegen die unveraenderten, hashgepinnten Original-XSDs 7.3.1 fuer Initial, FollowUp beziehungsweise Final (Reportable) validiert. Medizinische Pflichtangaben, Kodierbegruendung und konkrete Belege werden explizit erfasst. Der neue MIR-Editor bietet keine erfundenen Ereignis-, UDI- oder Ursachenwerte an. XSD-Strukturpruefung ersetzt weder eine semantische IMDRF-Kodierungspruefung noch behoerdliche Annahme. Exporte verwenden den authentifizierten API-Pfad, auch mit Bearer-Identitaet.

`TransmissionUnknown` sperrt auch ein neues Initialpaket mit neuem Hash fuer dieselbe Jurisdiktion des Falls. Ein neuer Hash legitimiert keinen zweiten Versuch. Ein abgeglichener Empfang erledigt ausschliesslich die im eingefrorenen Paket referenzierten Verpflichtungen. Spaeter hinzugekommene Pflichten bleiben offen.

Eine neue Duplikatquelle oeffnet eine bereits erledigte Originalpflicht nicht implizit erneut. Neue Evidenz erzeugt erneuten Pruefbedarf; ein neuer Fristbeginn braucht die gesondert begruendete Ausnahme. Eine Nichtmeldepflicht wird erst nach medizinischem Vorschlag und getrennter RA-Bestaetigung als abgeschlossen behandelt.

## Fristen und Anzeige

Alle Instants bleiben unveraendert in SQL. Berechnung und Anzeige verwenden die versionierte Jurisdiktionszeitzone. Kalenderdaten ohne Uhrzeit (etwa ein PSUR-Berichtsfenster) sind keine UTC-Instants und duerfen in westlichen Browserzonen nicht auf den Vortag rutschen.

Die synthetische Feiertagsliste ist ausdruecklich unvollstaendig. Die EU-Mindestarbeitstagsregel stammt aus Art. 3(5) VO 1182/71, die Fristendverschiebung aus Art. 3(4); die vertauschte Guidance-Fussnote wurde nicht uebernommen. GB wird nicht durch eine EU-Regel ersetzt.

Ein menschlich im MIR zugesagtes `NextReportDate` ist **keine neue gesetzliche Initialfrist**. Das Mapping `cohors-mir-map-2026.2` friert Kalendertag, Tagesende in der Jurisdiktionszeitzone, Kalender-/Regelversion und zwoelf Stunden fruehere interne SafetyDue im signierten Paket ein. Auf diesen festen Termin werden weder 2/10/15-Tage-Regeln noch Wochenend-/Feiertagsverlaengerung oder Mindestarbeitstage angewandt.

Erst ein akzeptierter simulierter Empfang oder dessen erfolgreicher Abgleich erzeugt genau eine persistente `FollowUpObligation`, deterministisch aus Paket-Hash/Jurisdiktion/Zusage. Ein spaeter Abgleich verschiebt den Termin nicht. FollowUp-/Final-Pakete binden die zu erledigenden Folgepflicht-IDs ausdruecklich; Initialpakete erledigen keine bestehenden Folgepflichten. Ein akzeptierter FollowUp erzeugt seine neue Zusage, Final keine neue. Ein alter Idempotenzbeleg veraendert keine neuere Folgepflicht. Der unabhaengige SQL-Watchdog eskaliert offene Zusagen, und der Fallabschluss verweigert offene Folgepflichten auch anderer Jurisdiktionen. Cockpit, Falltabelle, Workbench und Fristenliste bezeichnen sie als **zugesagten Folgetermin**, nicht als LegalDue.

Historische MIR-Artefakte mit Datum, aber ohne gebundenen Zeit-/Kalenderscope werden nicht still migriert oder neu datiert. Ihr Abgleich erfordert einen kontrollierten Migrationsnachweis; dieser Altformatpfad ist kein automatischer Pilot-Erfolg.

## PSUR-Quellen und unterschiedliche Stichtage

Der SQL-Freeze speichert ein `cohors-psur-sources-2`-Manifest. Jeder N/N2/N3/N4-Zaehler pro Region bindet seine eigenen eindeutigen Fall-IDs, Versionen und Payload-Hashes. Das gilt auch fuer Worldwide-Faelle ausserhalb einer ausgewaehlten Teilregion und historische abgeschlossene Vergleichsfaelle, die nicht in die aktuelle offene Narrativliste gehoeren.

Die organisatorische Kenntnis muss fuer jeden Basiszaehler vor dem exklusiven UTC-Ende seiner Periode liegen. Spaeter bekannt gewordene Ereignisse sind separat identifizierte Nachtraege, nicht verdeckte Basiszaehler. Ein spaeter Systemeingang schliesst vorherige belegte Organisationskenntnis nicht aus. Aktuell eingefrorener Bearbeitungsstand und historischer Status am Stichtag bleiben getrennt: letzterer benoetigt eine konkrete SQL-Historienversion, sonst steht `NotRecordedAtCutoff` beziehungsweise `NotKnownAtCutoff`. Ein Freeze-Zeitstempel allein ist kein Statusbeweis.

Fuer aktuelle technische/klinische Dokumente gilt dagegen bewusst **FrozenAt**: `EvidenceRules.Effective` waehlt pro Dokument-ID die zu diesem Zeitpunkt wirksame, freigegebene Revision. Alte und neue Fassungen erscheinen nicht parallel als aktuell. Vorversionen bleiben in der Historie; vorhandene PSUR-Snapshots werden durch spaetere Dokumentrevisionen nicht veraendert. Freigabe/Simulation pruefen den gespeicherten Quellenstand erneut. Altberichte ohne ausreichendes Manifest werden nicht still repariert oder freigegeben.

Das Massnahmenmanifest trennt tatsaechliche historische CAPA-/Change-Versionen
am Kenntnisstichtag von ihrem aktuellen Freeze-Stand. Offene Altfaelle,
abgeschlossene Vorperioden, Aktualisierungen und Nachtraege sind ausdruecklich
gekennzeichnet. Das Auswahlverzeichnis bindet auch ausgeschlossene Staende und
die tatsaechlichen Quellversionen; ein selbst konsistenter Snapshot-Hash allein
ersetzt keinen Abgleich mit der SQL-Historie.

`cohors-psur-series-1` bindet stabile Referenz, fortlaufende Berichtsnummer
(getrennt von der technischen Aggregatversion), Leitprodukt, Produktprofil und
Benannte Stelle. Bestehende Einzelfamilien-Snapshots bleiben in ihrem
unveraenderten Format lesbar; eine Gruppe wird nicht nachtraeglich hineinkopiert.
Vorfassung und erwartete Version sind fuer jede neue Fassung ausdruecklich
anzugeben. Dieselbe Periode kann begruendet neu eingefroren werden; der
Folgezeitraum muss lueckenlos anschliessen. Alte Fassungen bleiben unveraendert.
Ein transaktionsgebundener Produktserien-Lock umfasst die konsistente
Portfolio-Lesung und den atomaren neuen Aggregat-/Audit-/Outbox-Eintrag; keine
separate Zaehlerreservierung und keine verschluckten Versionsluecken.
Die Portfolio-Lesung bleibt eine eigene serialisierbare Lesetransaktion,
nicht die Behauptung einer zeitgleichen Aenderung aller Quellaggregate.

Der erste Einreichungstermin kommt aus dem kontrollierten Produktzeitplan.
Folgeperioden oder Terminabweichungen brauchen einen ausdruecklichen Termin
und eine Vereinbarungsreferenz; es gibt keine erfundene pauschale Tagesfrist
nach Periodenende. Serienlose Altberichte werden nicht automatisch
ruecknummeriert. Erneute Freigabe prueft Produktprofil und Vorberichtsstatus
gegen die gebundenen SQL-Historienversionen.

### Begruendete Mehrfamilien-Gruppierung

`cohors-psur-group-1` bindet eine menschlich begruendete, versionierte
Mitgliedschaft einschliesslich jeder Produktprofil-ID/-Version/-SHA256.
Das feste Leitprodukt gehoert zur hoechsten Risikoklasse und alle Mitglieder
brauchen dieselbe verantwortliche Benannte Stelle. Dieser Pilot verwendet
ausschliesslich die synthetischen Klasse-III-Implantatfamilien. Die technische
Testgruppierung belegt keine Eignung einer realen Produktkombination.
Leitprodukt/Basic-UDI, BS, Serienreferenz und Zeitplananker koennen nicht
still gewechselt werden. Mitglieder aendern sich nur ueber eine neue
Definition und einen neuen Berichtssnapshot. Alte Berichte, Termine und
Serien werden dadurch nicht geschlossen.

Jede Basic-UDI behaelt einen eigenen vollstaendigen Snapshot mit Faellen,
N/N2/N3/N4-/Regionszaehlern, Nennern, Kodierung, Massnahmen und Quellen.
Evidenz und Verzeichnis werden pro Produkt gefiltert. Der fuehrende
Snapshot ist exakt die Kopie des Leitprodukt-Mitglieds; die Gruppe bindet
zusaetzlich alle anderen Mitglieder. Aktuelle Mitgliedschaft behauptet
keine damalige historische Gruppenzugehoerigkeit. Keine produktuebergreifend
gepoolte Inzidenz.

Fuehrende Periodenverwendung ist pro **Basic-UDI**, nicht nur pro interner
Produkt-ID, exklusiv. Ein zusaetzlicher Basic-UDI-Lock und ein hashgepruefter
SQL-Metadatenabgleich verhindern eine Umgehung ueber interne Identifier.
Dieser Abgleich liefert nur interne Periodenbelegungen; fremde klinische
Snapshots werden nicht in die Nutzerausgabe oder den Agentenauftrag
uebernommen. Nichtfuehrende Mitgliedschaft in einer weiteren Serie bleibt
erlaubt. Alle Datenfenster und Vereinbarungen richten sich am festen
Leitprodukt aus.

Erstellung, Listen/Detail, Nachbearbeitung, beide Human Reviews, Simulation,
Exports und AgentHost brauchen den **gesamten eingefrorenen Mitgliedsscope**,
auch auf Idempotenz-/Retry-Pfaden. Eine spaeter verkleinerte Definition
verkleinert nicht den Zugriffsschutz eines alten Snapshots. Abbruch und
Anzeige gruppierter Agentenauftraege verwenden ebenfalls deren gebundenen
Mitgliedsscope. Es gibt keine zusaetzliche Pflichtsignatur: Medical Safety
und RA genehmigen denselben vollstaendigen Gruppen-Scope.

Die PSUR-Ansicht bietet Definition/Revision, ausdrueckliche Serienwahl und
eine Basic-UDI-Einzelauswertung fuer Tabellen und Dokumente. Gesamttext,
Freigaben und Exports bleiben gruppenweit. Gruppierte Kennzahlenmarker sind
produktqualifiziert, etwa `[metric:ortho:N:Worldwide:events=1]`; alte
Einzelfamilienmarker bleiben unterstuetzt. Der Autorenvertrag ist
`cohors-agent-contract-2026.2`.

Erzeugte und menschlich revidierte PSUR-Narrative verwenden dieselbe endliche
Obergrenze von 200.000 tatsaechlichen Zeichen einschliesslich Leerraum.
Die gemeinsame Narrativpruefung zaehlt vor dem Trimmen; innerhalb der
Grenze bleiben die Originalbytes erhalten. Ein bereits akzeptierter laengerer Entwurf
wird bei der Bearbeitung nicht auf 60.000 Zeichen reduziert; keine Quelle
oder Kennzahl wird abgeschnitten. Eine Textrevision loescht weiterhin alle
Freigaben, laesst aber die eingefrorenen Snapshotbytes unveraendert.

### Getrennte BS-Rueckfragen, Antworten und Bewertungen

`PsurReview` ist ein eigener versionierter, ausschliesslich synthetischer
Vorgang zu einem bereits quittierten PSUR. Technischer Eingang, konkrete
Rueckfrage, begruendete Antwort, getrennte Medical-/RA-Antwortfreigaben,
simulierte Antwortquittung und explizite Bewertung sind verschiedene
Schritte. Termin und Vereinbarungsgrund werden ausdruecklich erfasst;
ein unabhaengiger Watchdog eskaliert, ohne alte Termine zu verschieben.
Eine neue Runde oder Serie schliesst keine fruehere Massnahmenpflicht.
Der Abschluss verlangt wirksame/umgesetzte Massnahmen aus **allen**
Antworten. Bericht, ausgewaehlte Quellen und Massnahmen sind versioniert
gebunden; ein SQL-Transaktionsgate prueft aktuelle Abhaengigkeiten atomar.
Revisionen invalidieren Antwortfreigaben, nicht die Original-PSUR-Bytes.

Neue PSUR-Schnitte verwenden Vorlage `cohors-psur-2026.4` und das optionale
Manifest `cohors-psur-regulatory-history-1`. Es bindet unveraenderte
BS-Quellversionen und den tatsaechlichen SQL-Stichtagsstand, einschliesslich
Vorgaengen frueherer Serien. Spaetere Rueckfragen/Bewertungen erscheinen
als Nachtrag, nicht als damals bekannte Sicherheitsbewertung. Ein
Fragenabschluss ersetzt nicht den separat eingefrorenen CAPA-Stichtagsstand.
Jeder relevante Vorgang braucht einen `[psurReview:ID@Version]`-Beleg.
Die 200.000-Zeichen-Narrativgrenze bleibt bestehen; die komprimierte
Darstellung verweist auf den vollstaendigen unverkuerzten Korrespondenzanhang.

Einzelfamilien-Schnitte enthalten nur Vorgaenge innerhalb ihrer eigenen
Mitgliedschaft. Gruppenweite Korrespondenz steht in einem getrennten
Gruppenmanifest, ohne die unveraenderte Leitproduktkopie zu veraendern.
Breitere Vorgaenge werden weder in engere Datenkopien aufgenommen noch
als erledigt oder nicht vorhanden ausgegeben. Lesezugriff und Exports
verlangen saemtliche behaltenen Produktscopes. Alte Snapshotformate
lassen neue Nullfelder weiter aus und werden nicht rueckwirkend umgeformt.
Die UI trennt Antwortzeitpunkt, letzte dokumentierte Bewertung, historischen
Snapshot und aktuelle Vorgaenge. PDF/JSON/ZIP binden die vollstaendige
synthetische Korrespondenz; fehlende oder abweichende BS-Quellversionen
scheitern ausdruecklich. Kein echter BS-Zugang und keine Risikoakzeptanz.

### Authentifizierte PSUR-Leseexports

`GET /api/psurs/{id}/export/{json|pdf|zip}?version=N` prueft Produktscopes,
erwartete Version, Snapshot-/Entwurfsscope und Auditmanifest.
JSON behaelt den Originalbericht. PDF ist eine paginierte, gekennzeichnete
Lesekopie, keine neue Signatur oder Annahme eines Behoerdenformats.
ZIP enthaelt `report.json`, `snapshot.json`, `report.pdf`, `audit.json`,
`metadata.json` und `checksums.json`; alle fuenf Nutzdateien sind gehasht.
Gruppenmetadaten enthalten jede Mitgliedsprofil- und Snapshotbindung.
Aktuelle Nachweisbefunde stehen neben dem unveraenderten Altbericht;
fehlende historische Daten werden beim Export nicht ergaenzt.

PDFsharp/PDFsharp-MigraDoc sind bewusst auf die MIT-Version **6.2.4** gepinnt.
Kein stilles Upgrade auf einen anders lizenzierten Release. Unveraenderte
OFL-Noto-Sans-Schriften, Lizenztexte und Provenienz stehen unter
`src\Cohors.Api\ReportFonts`; Schriftbytes werden vor Verwendung geprueft.

## Entwurfsqualitaet und Quellen

### Kontrollierte A/C/D/F-Kodierung

`contracts/terminology/fda-imdrf-2026.json` ist der unveraenderte, hashgebundene
FDA-Mappingstand vom 17.08.2026. Die unveraenderte FDA-Arbeitsmappe steht unter
`source/imdrf-2026-fda-mapping-source.xlsx`; der Extraktor unter `docs/requirements`
ist Herkunftsdokumentation, keine Anwendungskomponente. Dieser Nachweis ist
**kein behaupteter byteidentischer nativer IMDRF-Release**. FDA-/NCIt-/MedDRA-
Felder wurden nicht in die Laufzeitreferenz uebernommen.

Nur Medical Safety kann konkrete Fallkodes mit Originalbelegen und Begruendung
versionieren. Dies ersetzt keine Freigabe und macht bestehende Assessments/
Pakete pruefbeduerftig. A/C/D/F-Mitgliedschaft, aktuelle Selektierbarkeit und
Gruppierbarkeit sind getrennte Pruefungen. Retired D1401/D1402 sind fuer eine
neue aktuelle Kodierung gesperrt. Historische Angaben werden unveraendert
erhalten, aber nicht allein durch `HistoricalAsOf` als damals gueltig
anerkannt. Sie werden nicht still umgeschluesselt. C1901/C1902 bleiben gueltige
Mitglieder, ihre widerspruechliche Hierarchie wird aber nicht repariert und
blockiert eine PSUR-Freigabe bis zur kontrollierten Klaerung.

PSUR-Kodetabellen binden pro Periode/Region eindeutige Quellfallversionen,
Originalkodes und Terme; gleiche Faelle zaehlen je aktueller Level-2-Gruppe
(sonst Level 1) nur einmal. Historische Originalzeilen sind separat und
ungegroupiert. F02/Death wird unabhaengig von
Haeufigkeit ausgewiesen; F01 oder die kombinierte Dringlichkeitsklasse Tod/
Verschlechterung erzeugen keinen Todesfall. Fehlende Kodierung ist ausdruecklich
unbekannt, kein Null-Todesfallnachweis. Auch ein intern konsistenter, neu
gehashter PSUR darf den tatsaechlichen SQL-Quellinhalt nicht ersetzen.

Ohne `HistoricalAsOf` ist die Erfassung eine **heutige retrospektive
Kodierungsentscheidung** (`CurrentRetrospective`), auch bei einem alten
Ereignis. Sie verwendet nur aktuell selektierbare Kodes. Mit
`HistoricalAsOf` ist sie eine **behauptete damalige Originalangabe**:
`HistoricalValidityUnknown` ohne spezifischen Beleg;
`HistoricalOriginalDocumentedReleaseUnknown` nach einem exakt gebundenen
synthetischen Beschwerde-/Befundauszug. Dieser muss ID, Version, Hash,
Fundstelle, wortgetreues Zitat, Originalkode/-term, Datum und behaupteten
Release enthalten. Ein beliebiger genehmigter Produktbeleg reicht nicht.
Der Auszug belegt die Originalangabe, nicht die damalige Kataloggueltigkeit;
deshalb auch dann keine automatische historische Level-2-Gruppierung.

Das im gehashten FDA-Datensatz ausdruecklich genannte Einfuehrungsjahr ist
eine negative Chronologiegrenze, kein rekonstruierter Mehrjahreskatalog:
F0104 als 2025 und D1401 als 2023 werden als
`HistoricalChronologyConflict` erhalten, ungegroupiert und mit Major-Befund
versehen. Historische F02-Angaben werden unter `historicalDeathClaimed`
separat von heutigen `deathCoded`-Kodierungen ausgegeben. Unbekannt bedeutet
nicht null Todesfaelle. Herkunfts-/Belegaenderung ist eine neue
Fall-/Kodierungsversion und invalidiert aktuelle Fallfreigaben; bestehende
PSUR-Bytes bleiben identisch. Neue Freigabe/Simulation prueft die aktuellen
Assurance-Regeln, statt frueher fehlende Herkunft im Hintergrund zu ergaenzen.

Die strukturelle Assurance verlangt belegte Claims, aufloesbare fachliche Abschnittsbezüge und begruendete Empfehlungen. Leere Claims-/Empfehlungslisten sind kein QA-Pass. Reine Metadaten muessen nicht kuenstlich zitiert werden. Hash-/Zitatuebereinstimmung ist ein maschinell pruefbarer Mindeststandard, **kein semantischer Beweis**, dass die Quelle eine Schlussfolgerung traegt.

Technische Replay-Aussagen verwenden keine Beschwerde als Ersatz fuer eine fehlende Spezifikation oder Pruefevidenz. Ein solcher Mangel bleibt sichtbar und verweist auf den menschlichen Mindestbericht. Dieser menschliche Weg ruft weder KI noch Search auf; beide Freigaben bleiben erforderlich.

Oeffentliche FDA-/PubMed-Adapter lesen nur fest definierte, begrenzte Metadatenabfragen. FDA-Zaehler sind keine Inzidenz und keine Cohors-Herstellerfallzahlen. PubMed-Titelmetadaten sind kein lizenzierter Volltext. Quelleninhalt hat keine Tool- oder Berechtigungsmacht. Eine menschliche Relevanz-/Quellenrechtspruefung ist vor kontrollierter Evidenzverwendung erforderlich.

Deutsch und Englisch sind die expliziten Pilot-Berichtssprachen. Englische Replay-Berichte markieren unveraenderte fremdsprachige Assessment-Auszüge als Originalzitate; sie behaupten keine zertifizierte Uebersetzung. Weitere NCA-Sprachen und regulaertaugliche Uebersetzungen sind ein fachliches Gate.

## Unveraenderte Originale

`source\Konzept_PMS-System_Klasse-III-Implantate_MDR.docx`, die freigegebenen Eingangsunterlagen sowie Logo und Animation werden nicht veraendert. Die unveraenderte `source\PMS_Anforderungsmatrix_MDR_KlasseIII_Implantate.xlsx` hat SHA256 `6a70b6ca1d287f340abc43097b8538ba9b0b14b676e2063a4aa70245831785ca`.

Alle 118 Original-IDs (104 Muss, 14 Soll) wurden vom Koordinator einzeln zugeordnet; die uebernommenen MD-/XLSX-/JSON-Nachweise stehen unter `docs\requirements`. Jeder Originalstatus bleibt **Offen**. Beschaffung und Einzelzuordnung sind abgeschlossen, nicht die Erfuellung aller Anforderungen. P/S/D praezisieren den genehmigten Piloten; Z bleibt Zielbetrieb. Die Originalformulierungen C-13/C-14/L-06 ersetzen nicht die korrigierten Awareness-Regeln; F-11 ist keine automatische 15-Jahre-Loesch- oder PMS-Stoppfreigabe.
