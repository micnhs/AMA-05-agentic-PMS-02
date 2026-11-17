# Implementierungspraezisierungen

Diese Arbeitsfassung ergaenzt den unveraendert uebernommenen freigegebenen [Entwurf](architecture/approved-design.md). Sie aendert weder den fachlichen Gesamtauftrag noch die Deployment-Grenzen.

## Fachlicher und technischer Zuschnitt

Die Pilotdomaene besteht aus versionierten SQL-Aggregaten mit typisierten .NET-Vertraegen. Relationale Tabellen fuer Historie, Befehls-Idempotenz, Fristenprojektion, Audit und Outbox ergaenzen den kanonisch serialisierten Aggregatinhalt. SQL bleibt fuehrend; Search ist nicht die Sicherheitsakte. Der Pilot ist bewusst kein Netz aus sechs Agenten-Microservices.

Version, Identitaet, Produktscope und Befehlsinhalt werden serverseitig geprueft. Eine erwartete Version ist keine optionale UI-Empfehlung. Historische Belege und Freigaben bleiben nachvollziehbar. Der Auditnachweis muss sowohl die Historie als auch den aktuellen SQL-Zustand erfassen; eine intakte historische Hashkette allein beweist keinen unveraenderten aktuellen Datensatz.

Schreibtransaktionen nutzen `READ COMMITTED`, transaktionsgebundene SQL-Application-Locks fuer Befehl und Aggregat sowie einen Update-Lock auf den aktuellen Datensatz. Das serialisiert konkurrierende Domaenenbefehle, ohne bei verschiedenen Idempotenzschluesseln leere `Commands`-Indexbereiche bis zum Commit gegeneinander zu sperren. Ein tatsaechlicher SQL-Paralleltest hatte diesen Range-Lock-Deadlock sichtbar gemacht. Manifest-/Portfolio-Lesungen verwenden dagegen eine konsistente serialisierbare Lesetransaktion.

Ein idempotenter Claim ist nicht gleichbedeutend mit einer zweiten Activity-Ausfuehrungsberechtigung. Eine persistierte zufaellige Owner-ID bestimmt den einzigen ausfuehrenden Aufrufer. Einzelne Modellantworten werden vor fachlicher Uebernahme als eigene Auftraege persistiert; Wiederholungen verwenden nur eindeutig abgeschlossene Antworten. Aktive, fehlgeschlagene oder unklare Modellauftraege werden nicht verdeckt erneut gesendet.

## Signalablehnung und unmittelbarer menschlicher Review

Signaltriage liegt als optionale, bei Null nicht serialisierte `SignalReviews`-Historie
an der bestehenden Fall- oder Quellenakte. Sie ist kein zweiter Intake und keine
Umdeutung von `ReportState.Rejected`, Meldungsrueckgabe oder Nichtmeldepflicht.
Jede Entscheidung bindet Originalquell-ID und Hash, Aktenversion, Begruendung,
Zeitpunkt und entweder die tatsaechliche menschliche Identitaet/Rolle oder die
unveraenderte Ausgabe eines konkreten abgeschlossenen Ingestion-Laufs. Unbekannte
historische Herkunft bleibt unbekannt. Die Standardliste zeigt nur aktuelle
Signalablehnungen; vorherige Entscheidungen bleiben im Aggregat und SQL-Audit.

Medical Safety **oder** RA nehmen mit erwarteter Version eigenstaendig wieder auf.
Ein vorhandener Fall wird in derselben SQL-/Historien-/Audit-/Outbox-Transaktion
direkt `AwaitingMedicalReview`. Originalkenntnis, Fristen und Erledigungsstaende
bleiben unveraendert. Die begruendete Wiederaufnahme wird neue Evidenz: alte
Bewertung bleibt erhalten, ist aber nicht aktuell; aktive Berichtsentwuerfe werden
supersediert, alte Signaturen nicht reaktiviert. Es wird keine KI beauftragt.
Noch offene alte Laeufe werden fachlich `Abandoned`; eine bereits laufende externe
Antwort kann technisch noch eintreffen, wird aber nicht als aktuelles Ergebnis
uebernommen. Erneute KI-Arbeit braucht einen neuen expliziten Auftrag.

Oeffentliche Vergleichsmetadaten ohne eigenen Fall bekommen stattdessen einen
stabilen `SIG-...`-Review-Vorgang **innerhalb derselben Quellenakte**, sichtbar in
derselben Review Queue. Es entsteht kein erfundener Hersteller-/Patientenfall und
keine Frist aus Abruf-/Publikationszeit. Eine ausdrueckliche menschliche
Relevanzbestaetigung beendet diesen Quellenreview, erteilt aber keine Fall- oder
Meldepaketfreigabe. Die vorhandene medizinische Vergleichsevidenzfreigabe bleibt
eine getrennte Handlung.

Die UI liest nach erfolgreicher Wiederaufnahme sofort Fallliste und Quellenreviews,
prueft den persistierten Zielstand und wechselt direkt in die Review Queue.
Manuelles Neuladen und das Zehnsekunden-Polling sind dafuer keine Voraussetzung.
Signalhinweise, Quellenreview-Daten und offene Dialoge werden beim Abmelden bzw.
Identitaetswechsel geloescht. Verspaetete Refresh-/Wiederaufnahmeantworten sind
an eine Identitaetsepoche gebunden und duerfen keine Daten oder Erfolgshinweise
des vorherigen Kontos in die neue Sitzung eintragen.
Rollen und Produktscope werden unabhaengig von sichtbaren Schaltflaechen auf dem
Server durchgesetzt. Gleichzeitige Wiederaufnahmen erzeugen genau eine Version;
derselbe Command-Receipt ist idempotent.

Der normale Replay-Ingestion-Lauf behauptet bei Datenluecken keine Irrelevanz.
Das separat waehlenbare Profil `signal-triage-replay-reject` demonstriert eine
explizit synthetische Agenten-Ablehnung, **keine verifizierte Inhaltsklassifikation**.
Live-Ingestion kann nur einen quellengebundenen Vorschlag abgeben; eine
menschliche Wiederaufnahme darf er nicht wieder verwerfen. Live-Foundry-Abnahme
und On-demand-Dateiimport sind dadurch nicht behauptet oder aktiviert.

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

Das echte MIR-XML wird gegen die unveraenderten, hashgepinnten Original-XSDs 7.3.1 fuer Initial, FollowUp beziehungsweise Final (Reportable) validiert. Die Vorbefuellung `cohors-report-prefill-2` (lesekompatibel zu v1) uebernimmt Fall-/Produkt-/Herstellerdaten deterministisch und entwirft Ursachen-/Untersuchungstexte ueber die bestehende Vigilance-Autorenstrecke. Jede Angabe bindet Herkunft, Originalfundstellen und den wirklichen Belegstatus. Replay bleibt Replay. Unbekanntes Ereignisdatum, genauer Patientenausgang, Kodierung, bereits ausgefuehrte Massnahmen und menschliche Termine werden nicht erfunden. Eindeutige aktuelle menschliche Kodierungen werden aus ihren belegten Annexen uebernommen, keine Codes aus Freitext erzeugt. Mehrere belegte Codes fuer ein einzelnes Pilotformularfeld werden als ausdrueckliche Mapping-Grenze gezeigt, nicht willkuerlich auf einen Code reduziert oder als fehlende Quelle ausgegeben.

`PrepareReportCommand.CompleteCurrent=true` verwendet denselben Autorenweg fuer einen ausdruecklichen erneuten Vervollstaendigungsversuch durch Medical/PMS. Erwartete Fallversion und unveraenderter Berichtsauftrag (Art, Ziel, Sprache, Untersuchungsstand, offene Fragen) werden vor dem Autorenlauf geprueft. Bestehende Werte und ihre Herkunft bleiben erhalten, einschliesslich einer bereits menschlich dokumentierten Terminzusage dieses Berichts. Ein wirklich neuer Bericht uebernimmt passende menschliche Eingaben, aber keine neue Zusage aus dem alten Folgetermin. Veraenderte Quellen werden nicht still ueber menschliche Angaben geschrieben. Jede neue Fassung braucht neue Medical-/RA-Freigaben; alte Signaturen und Originalfassungen bleiben Historie. Der UI-Dialog bewahrt auch bekannte, fuer diese Meldeart nicht eingeblendete Felder.

Eine automatisch nutzbare Kontaktquelle ist eine **bereits gebundene**, aktuell gueltige, freigegebene synthetische `Evidence` der Art `ManufacturerContact`. Ihr Originaltext ist ein strikt gelesenes JSON-Dokument `cohors-reporting-contact-1` mit `productId`, exakt passender `manufacturerSrn`, expliziten `jurisdictions`, `role: "ManufacturerReportingContact"` und `contact`. Letzteres enthaelt `contactId`, die einzeln belegten `firstName`, `lastName`, `street`, `city`, `postcode`, `phone`, `email` und `isSubmitter`; unbekannte Werte bleiben null. Gleichartige Nachweise muessen denselben vollstaendigen Kontakt beschreiben: keine Zusammensetzung verschiedener Personen, keine automatische Bevorzugung der letzten Quelle. Hash-/Originalintegritaet, Gueltigkeit, Freigabe, Produkt, Hersteller und Ziel werden geprueft. Doppelte JSON-Schluessel, falscher Scope, PRRC-Rolle oder unlesbarer Text werden sichtbar zurueckgehalten. Kein Kontakt aus Login, PRRC, Notified-Body-Adresse, Modellfantasie oder externer Recherche. Es werden keine neuen Kontakte in Bestandsdaten gesaet und keine Stammdatenplattform eingefuehrt. Ohne geeignete Quelle bleibt die Angabe offen.

Die separate UI-Sektion **Nicht automatisch befuellbar - Angaben oder Belege erforderlich** zeigt deutsche Feldbezeichnungen, fehlende Quelle, widerspruechliche Angaben, menschliche Entscheidung oder nicht unterstuetzte Konstellation, jeweils mit benoetigtem Beleg und Bearbeitungs-/Nachforderungsweg. Die Befunde entstehen aus denselben tatsaechlichen Meldeart-/Zielpruefungen wie das MIR-Artefakt, nicht aus einer zweiten Pflichtfeldliste. Quellenkonflikte bleiben auch beim blossen Speichern offen; eine explizite menschliche Klaerung mit Begruendung und ausgewaehlten Belegen ist gesondert aufzuzeichnen. Dies ist keine Freigabe. Technische Originalfehler bleiben in Details erhalten; historische Berichte erhalten beim Lesen keinen nachtraeglich behaupteten Vervollstaendigungsnachweis.

RA sieht eine Pruefansicht statt einer verpflichtenden Dateneingabemaske. Fehlende Pflichtangaben sperren die Freigabe; Medical Safety oder RA koennen das konkrete Paket begruendet zur Korrektur an PMS zurueckgeben. Die Rueckgabe ist versionierte Zusatzmetadaten eines Drafts, kein neuer persistenter Enumwert und keine Simulator-Ablehnung. Korrektur erzeugt eine neue Fassung und verlangt beide getrennten Signaturen erneut. Produktprofilwechsel blockiert neue Freigabe/Sendung, veraendert aber keinen idempotenten alten Command-Receipt. Optionale Nullfelder bleiben bei Altberichten ungeschrieben; deren urspruenglicher Hashvertrag, XML und FrozenJson werden nicht beim Lesen neu komponiert. Kein SQL-Schemawechsel und kein Seed/Migrationsauftrag.

### Gezielte Nachforderung: Kontakt, Versand und Ruecklauf getrennt

Unter **Informationsbedarf** werden Organisation, benannter synthetischer Kontakt/Empfangsstelle, Rolle, Kontaktreferenz, vorgesehener Kanal und synthetische Zielreferenz ausdruecklich eingegeben. Es gibt keine Adressableitung aus Reviewer-Konten und keinen realen E-Mail-, Telefon- oder Portalconnector. `InformationRequest.Lifecycle` (`cohors-information-request-1`) bindet Fall, Produkt, Empfaenger, Fragestellung, Minimaldatensatz, Rechtsgrundlagen, interne Antwortfrist und stabile Korrelation im Anfrage-Scope. Medical und RA genehmigen mit getrennten Konten genau diesen Scope.

Die zweite Signatur erzeugt **Approved**, nicht **SimulatedSent**. Ein eigener menschlicher Versandauftrag erzeugt **SendPending** und den atomaren vorhandenen Outbox-Eintrag. Erst dessen idempotente Verarbeitung durch denselben lokalen/Durable-Outbox-Pfad erzeugt einen tatsaechlichen internen Simulatorversuch mit **SimulatedSent**, **SendFailed** oder **TransmissionUnknown**. Es gibt keinen HTTP-/Mailzugriff. Eine Simulatorquittung beweist nur den internen technischen Empfang, keine echte Nachricht oder medizinische Kenntnisnahme. Ein unklarer Versuch kann nicht blind erneut versendet werden; der Abgleich bindet explizite Anfrage- und Versuch-ID sowie Empfaenger-, Fall-, Produkt-, Korrelations- und Scope-Hashes. Nach belegtem Fehlschlag ist ein neuer Versuch mit derselben Anfragekorrelation moeglich; der alte Versuch bleibt erhalten.

Ruecklaeufe werden zunaechst separat als `SafetyCase.InformationResponses` gespeichert: Originaltext unveraendert, inhaltsadressiertes Textoriginal, Originalumschlag mit Hash, Eingangszeit und erfassender Mensch. Die angegebene Originalreferenz ist innerhalb des Falles eindeutig; identischer Ruecklauf ist idempotent, geaenderter Inhalt unter derselben Referenz wird abgewiesen. Vollstaendige Anfrage-/Korrelations-/Fall-/Produktreferenzen und der exakt gebundene synthetische Absender liefern einen Vorschlag, noch keine Fall-Evidenz. Fehlende, mehrdeutige oder widerspruechliche Angaben bleiben sichtbar **Held**. Kein Fuzzy-Matching, keine letzte Anfrage und kein automatisches Umhaengen auf andere Faelle. Fehlende Referenzen koennen nur durch explizite Auswahl und begruendete menschliche Bestaetigung zugeordnet werden; widerspruechliche Referenzen oder fremder Absender bleiben gesperrt. Das Original wird dadurch nicht nachtraeglich ergaenzt.

Die begruendete Zuordnung durch PMS/Medical erzeugt eine **nicht freigegebene** Fall-Evidenz und macht das bisherige Assessment/Paket ueberholt; historische Signaturen und FrozenJson bleiben erhalten. Medical muss Herkunft, Relevanz und Grenzen danach separat als Bewertungsquelle pruefen. Erst dieser gesonderte Schritt erhoeht die Evidenzversion und setzt deren `Approved`, ohne Originalbytes zu ersetzen. Das ist weder eine klinische Wahrheitsbestaetigung noch eine neue Assessment-, Fall- oder Meldefreigabe. Ein KI-Auftrag wird durch Eingang, Zuordnung oder Quellenpruefung nicht erzeugt. Kenntnisdaten, LegalDue, SafetyDue und zugesagte Folgetermine bleiben unveraendert. Offene Zuordnungs-/Quellenpruefungen werden im Prozess und PSUR als offen sichtbar und verhindern einen unberechtigten Fallabschluss.

Neue Metadaten sind optional und bei null nicht serialisiert. Altanfragen bleiben byte-/hashkompatibel lesbar; ihr historisches `SimulatedSent` wird **nicht** nachtraeglich als eigenstaendiger Versandnachweis interpretiert. Fehlender Empfaenger-/Korrelationsnachweis wird sichtbar benannt; fuer einen neuen Sendeweg ist eine neue vollstaendige Anfrage erforderlich. Kein Seed, Schemawechsel oder Daten-Upgrade. Die bestehende Evidenz-/Audit-/Retention-Referenzsuche schliesst auch gespeicherte Originalumschlag- und Texthashes ein. Keine Live-Modellanalyse oder echte Klinikkommunikation wird dadurch aktiviert.

Mapping `cohors-mir-map-2026.4` trennt Original-XSD-Konformitaet, implementierte fachliche Pflicht-/Bedingungspruefungen und nationale Versandbereitschaft. Herstellerkontakt und die belegte Melderkonstellation sind explizit; andere Melder-/AR-Konstellationen bleiben gesperrt. FollowUp verlangt einen expliziten Massnahmen- und weiteren Untersuchungsstand, keine erfundene Durchfuehrung. FinalRep braucht bedingte Risikoreview-Ergebnisse beziehungsweise Nichtreview-Begruendung und einen strukturierten Aehnlichkeitsreview mit Methode, Zeitraeumen, Geografien und Nennerbasis. Fehlende Zaehler/Nenner bleiben null mit Datenlueckenbegruendung, niemals 0. MIR-Freitextgrenzen (4000 beziehungsweise feldspezifisch 2000 Zeichen) werden gemessen, nicht still gekuerzt oder auf PSUR uebertragen. FollowUp/Final brauchen eine echte gesonderte NCA-Referenz oder ausdruecklich begruendetes Unknown; eine Simulatorquittung ist keine Behoerdenreferenz. Historische Minimal-Mappings werden nicht als neu bestandene Pflichtfeldpruefung beschriftet. Exporte verwenden den authentifizierten API-Pfad, auch mit Bearer-Identitaet.

Das gebundene Zielprofil bezeichnet zustaendige Behoerde und technischen Simulator getrennt. Nationale Versandbereitschaft bleibt immer unbestaetigt. [EC MIR](https://health.ec.europa.eu/medical-devices-sector/new-regulations/guidance-mdcg-endorsed-documents-and-other-guidance/pmsv-reporting-forms_en): 7.3.1-Publikationsfamilie seit 01.05.2026 verpflichtend, PDF SB11154 und XSD SB11252; Labels beweisen keine Bytegleichheit. InitialFinal/FinalNonRep sind nicht implementiert. [MHRA GB-Schemata](https://www.gov.uk/government/publications/more-implementation/gb-mir-and-fsca-schema-implementation-guide) und [MORE](https://www.gov.uk/guidance/manufacturers-online-reporting-environment-more): GB 7.2.1GB ist nicht EU 7.3.1; GB-SIM-1 bleibt nur Pilotnarrativ, ohne GB-MIR-Pflichtfeldabnahme (UKRP, GMDN, IMDRF A-G, bedingte UKAB-Felder). Die 40-zeilige Helptext-Aenderungsuebersicht ist keine Pflichtfeldmatrix. NI folgt EU/MHRA MORE, konkrete Importannahme ungetestet. [ANSM](https://ansm.sante.fr/documents/reference/post-market-surveillance-and-vigilance) verlangt PDF UND XML; das Pilot-XML ist kein vollstaendiges franzoesisches Versandpaket. Keine neue PDF-/Portalintegration. BfArM403, HPRA-Verbindungsfehler und die italienische Sicherheitszwischenseite begrenzen den direkten nationalen Kanal-/Sprachabgleich; kein primaer belegter italienischer Portalweg behauptet. Dieser Quellenabgleich ersetzt keine Code-, klinische oder Behoerdenabnahme.

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

## B-10/E-02: abgegrenzte Untersuchungsquellen, noch ohne Laufnachweis

Der optionale Fallbaustein `Investigations` ergaenzt den bestehenden
CaseService, keine eigene Fachplattform. PMS/Medical dokumentieren eine
synthetische Asservatenreferenz, Eingang, Herkunft/Uebergabe und Luecken,
Ausgangszustand, Verwahrung, Beweissicherung, Land-/Chargen-/UDI-Zuordnung,
Fragestellung und moegliche Beeintraechtigung der Ursachenbewertung.
Unbekannte UDI/Charge bleibt unbekannt. Der eigene Sicherungsbeleg und alle
vorhandenen Quellen sind versioniert und objektgehasht gebunden.

Medical und eine getrennte RA-Identitaet pruefen das interne Paket.
Bei moeglich beeintraechtigender Untersuchung muss RA danach einen eigenen
vorherigen **simulierten Unterrichtungsschritt** dokumentieren; erst dann
darf PMS/Medical einen synthetischen Befund mit Restzustand, Gegenhypothesen
und Grenzen erfassen. Es gibt keine reale Materialpruefung, Behoerdenzustimmung
oder pauschale Genehmigungspflicht fuer nichtbeeintraechtigende Verfahren.
Fachliche Behoerde stammt aus dem vorhandenen Zielkatalog; der separate
`cohors:specimen-simulator:*` ist kein MIR-Empfang und erledigt keine Frist.

Quell-/Methodenaenderungen brauchen eine neue Planrevision mit neuen
internen Freigaben und erforderlichenfalls neuer Vorabunterrichtung.
Alte Revisionen, Quittungen, eingefrorene Pakete und Objektbytes bleiben in
Fallhistorie und Audit erhalten. Ein abgeschlossener Befund oder begruendeter
Verzicht wird nicht ueberschrieben. Neue Befunde invalidieren die bestehenden
offenen Berichtsfassungen, sind aber keine bestaetigte Ursache, Risikoakzeptanz
oder automatische Fallfreigabe. Offene Untersuchungsauftraege verhindern
Fallabschluss; Awareness, LegalDue und SafetyDue werden nicht geaendert.

Die API-Gruppe `/api/cases/{id}/investigations` bietet Erfassung/Revision,
`approve`, `inform`, `simulate`, `cancel` und einen lesenden `export`
mit optionaler konkreter `caseVersion`. Mutationen verwenden die bestehende
atomare Fall-/Audit-/Outbox-Transaktion, erwartete Version und Idempotenz.
Quellreferenzen binden den gespeicherten Audit-Payload-Hash statt einer
moeglicherweise anders serialisierten alten Fallfassung. Originaltexte
werden ueber die vorhandene Evidenzpruefung gelesen; der Produkt-Auditexport
nimmt die neuen eingebetteten Quellen und Bindungen mit.

**Backend-Nachweis, keine B10-Workbench-Abnahme:** Die sieben Regelfaelle und
drei SQL-Faelle in `SpecimenInvestigationTests.cs` sind in Build04/Tests04
bestanden und mit dem Cockpit-/Kalenderpaket bereitgestellt; siehe
[`validation.md`](validation.md#browser05-vollstaendiger-chromium-gesamtpass-und-kontrollierte-bereitstellung).
Eine eigene Workbench-Erfassung und ein B10-Browserfall bleiben offen.
Die frueheren unveraenderten Demopakete bleiben als Rueckfaelle erhalten.

## Cockpit-Grafiken und Fristenkalender

`GET /api/cockpit` projiziert einen berechtigten Fallzugangszeitraum
(einschliesslich, maximal 366 Tage) in der ausdruecklichen Anzeigezeitzone.
`SystemReceivedAt` zaehlt die Anlage einer Fallakte, nicht jeden Eingang,
kein Ereignisdatum und keine Inzidenz. Tages- bzw. Monatsabschnitte enthalten
exakte Zahlen und eine bedienbare Wertetabelle. Produktfilter, Grafik- und
KPI-Drilldowns liefern dieselbe serverseitig eingeschraenkte Auswahl mit
25 kompakten Fallzeilen pro Seite; Gesamtzahlen werden nicht auf 25 begrenzt.

Prozesszahlen verwenden `CaseService.ProjectProcess` und `CaseProcess.Read`,
genau wie die Fallleiste. Audit-Reihenfolge einer menschlichen Nachbearbeitung
und die originale Nichtmeldeentscheidung werden in einem SQL-Read gebunden,
nicht durch einen Process-/Historienrequest pro Fall. Stage ist kein Ersatz
fuer den aktuellen Schritt. Medical-/RA-KPI zaehlen den jeweiligen aktuellen
Pruefschritt, nicht alle spaeter irgendwann erforderlichen Freigaben.
Schluessel, Beschriftung und Rollen kommen aus der gemeinsamen
`CaseProcess.Definition`; auch Schritte ohne aktuelle Faelle werden mit
Null angezeigt. Ein bestehender Prozess-Drilldown bleibt bei einem
Leerwerden der Gruppe gueltig und liefert eine leere Liste, keinen
Filterfehler. Wirklich unbekannte Schluessel bleiben ungueltig.

**Risikogruppen sind derzeit nicht strukturiert gespeichert.** Das Cockpit
weist diesen Datenmangel aus: ohne Assessment bzw. Assessment ohne
Risikogruppierung. KI-/menschlicher Entwurf, aktuell medizinisch gepruefte
Fassung und veraltete/offene Analyse bleiben getrennt. Fristenanlass und
dokumentierter Patientenausgang sind keine Risikogruppen; Wahrscheinlichkeiten
und Freitext erzeugen keine neue medizinische Klassifikation.

Fallpflichten werden nach Fall und Verpflichtungs-ID genau einmal gezaehlt.
Ueberfaellig bedeutet ueberschrittene LegalDue bzw. bestaetigten Folgetermin.
Die 72-Stunden-Anzeige fuer bald faellige Pflichten bezieht sich auf die
interne SafetyDue, nur solange der Haupttermin nicht ueberfaellig ist.
Das ist ein Anzeigehorizont, keine neue regulatorische Schwelle.

`GET /api/deadline-calendar` liefert Monatszellen und eine paginierte Agenda,
unabhaengig vom Fallzugangsfilter. LegalDue, interne SafetyDue, bestaetigte
Folgepflichten, gespeicherte PSUR-Einreichungstage und BS-Antworttermine sind
getrennte Marker. Quellfeld, Version, Status, Fristgrundlage und Originalzone
sind sichtbar. PSUR-Daten ohne Uhrzeit/Zone bleiben `DateOnly`; der
Tagesvergleich in der benannten Anzeigezone behauptet keine neu berechnete
gesetzliche Frist. Monatsgrenzen werden fuer Zeitpunkte einschliesslich DST
in der Anzeigezone gelesen, nicht mit dem Browser- oder UTC-Tag verwechselt.

Aeltere PSUR-Fassungen derselben Serie/Periode bleiben als historische
Terminquellen sichtbar; nur die neueste Fassung zaehlt als aktuell offen.
Simulierte Eingangs-/Antwortquittung ist keine BS-Inhaltsakzeptanz.
Unbekannte Alt-PSUR-Termine werden gesondert als nicht verfuegbar ausgewiesen.
BS-Datensaetze und PSURs sind nur bei Berechtigung fuer ihre gesamte
gespeicherte Mitgliedschaft einschliesslich historischer BS-Quellen sichtbar.
Agenda-Links waehlen den konkreten PSUR bzw. BS-Vorgang und die Ursprungsrunde,
keinen beliebigen aktuellen Bericht.

Die Leseoberflaechen mutieren keine Fachobjekte. Der Browser laedt dort nicht
zusaetzlich `/cases` oder einzelne Prozessakten fuer Summen. Cockpit und
Kalender aktualisieren sequenziell alle 30 Sekunden bzw. explizit; keine
ueberlappende Pollschleife pro Ansicht. SQL liest fuer den Prozess die
ausgewaehlten aktuellen Faelle, fuer den Kalender kompakte Falltermine und
einmal die berechtigten PSUR-/BS-Originale fuer die vollstaendige Scopepruefung.
Erst danach entstehen die kompakten Antworten. Keine neue Migration oder
persistierte Summenquelle; insbesondere kein Nachweis fuer 250000 Faelle
oder 90 gleichzeitige Nutzer.

Die EUDAMED-Integrationsansicht prueft alle fuenf Sekunden, ob ein neuer
Lesezyklus moeglich ist. Solange eine Workspace-Antwort oder ein explizites
Nachladen laeuft, startet der Hintergrundtimer keinen weiteren Request.
Langsame Antworten werden dadurch nicht fortlaufend von juengeren
Pollgenerationen entwertet. Explizite Aenderungen sowie Produkt- und
Identitaetswechsel behalten ihre bisherigen Generations-/Lebenszyklusgrenzen.
Die Browserprobe verzoegert eine echte Workspace-Antwort ueber ein
Pollintervall und verlangt weiterhin genau einen Leseaufruf.

Grosse SQL-Originaltexte werden fuer Einzel-, Listen-, Historien- und
Produktaudit-Lesungen mit `SequentialAccess` und `GetTextReader` vollstaendig
eingelesen. Kanonisierung, Hashpruefungen, Produktscope und der transaktionale
Auditschnitt bleiben unveraendert. Innerhalb eines Workspace-Requests wird
dieselbe berechtigte Dossiermenge nicht nochmals fuer die Inbox gelesen;
identische Profil-/Pruefstand-Eingaben teilen die Bereitschaftsberechnung,
nicht jedoch den Deaktivierungsstatus des aktiven Kanals. Es gibt keinen
requestuebergreifenden Berechtigungs- oder Datenfallback.

Die Dossieraktionen uebernehmen nach bestaetigtem POST den typisierten
Serverstand direkt. Der Bestaetigungsdialog wartet nicht zusaetzlich auf
erneute Dossier- und Workspace-Lesungen. Die Uebersicht wird getrennt
aktualisiert; Lesefehler bleiben sichtbar. Ein eingereihter Versand wird
weiterhin nur als eingereiht angezeigt, nicht als fachlich angenommen.
Die Browserregression haelt eine echte nachgelagerte Workspace-Antwort
zurueck und verlangt trotzdem den Abschluss des bereits bestaetigten Dialogs.

## Unveraenderte Originale

`source\Konzept_PMS-System_Klasse-III-Implantate_MDR.docx`, die freigegebenen Eingangsunterlagen sowie Logo und Animation werden nicht veraendert. Die unveraenderte `source\PMS_Anforderungsmatrix_MDR_KlasseIII_Implantate.xlsx` hat SHA256 `6a70b6ca1d287f340abc43097b8538ba9b0b14b676e2063a4aa70245831785ca`.

Alle 118 Original-IDs (104 Muss, 14 Soll) wurden vom Koordinator einzeln zugeordnet; die uebernommenen MD-/XLSX-/JSON-Nachweise stehen unter `docs\requirements`. Jeder Originalstatus bleibt **Offen**. Beschaffung und Einzelzuordnung sind abgeschlossen, nicht die Erfuellung aller Anforderungen. P/S/D praezisieren den genehmigten Piloten; Z bleibt Zielbetrieb. Die Originalformulierungen C-13/C-14/L-06 ersetzen nicht die korrigierten Awareness-Regeln; F-11 ist keine automatische 15-Jahre-Loesch- oder PMS-Stoppfreigabe.
