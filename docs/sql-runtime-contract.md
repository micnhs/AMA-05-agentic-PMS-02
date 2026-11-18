# SQL-Runtimevertrag: eingeschraenkter Operations-Watchdog

Quellvertrag `cohors-operations-write-v1`, SQL-Schemaversion3.
Dieser Vertrag ersetzt **nicht rueckwirkend** b7/Build42: dort besass
Operations generische Schreibrechte auf dasselbe Aggregat-JSON wie die
Medical-/RA-Prozesse. Ein neuer Build, die echte SQL-Principal-Pruefung
und ein gesonderter Release-/Runtimebezug sind erforderlich. Keine
Azure-, Entra-, Preview- oder Livefreigabe folgt allein aus dieser Datei.

## Einzige Schemaquelle und Einstieg

`Cohors.Bootstrap sql-migrate` verwendet weiterhin
`SqlStateStore.Initialize` und die eingebetteten Migrationen. Nach001/002
folgen `003a_operations_quote.sql`, `003b_operations_write.sql` und
`003c_operations_permissions.sql`. Quote-Funktion und Schreibprozedur
sind einzeln wiederholbare SQL-DDL-Batches; erst die letzte transaktionale
Migration setzt Version3 und den Runtime-Rollenvertrag. Ein Abbruch davor
ist kein bereites Schema. Es gibt keine zweite DDL im Infrastrukturcode.

`OperationsService.Tick` verwendet `IOperationsStore`, im SQL-Adapter
ausschliesslich `dbo.CohorsOperationsWrite`. Es braucht weder API-HTTP
noch Foundry, Search, Blob oder einen Modellaufruf. Die generischen
Anwendungsdienste bleiben fuer API/Bootstrap erhalten; ihre DML-Rechte
werden **nicht** an die Operations-Identitaet weitergegeben.

Die Prozedur akzeptiert Zieltyp/ID, erwartete Version, den kanonischen
Ergebnisvorschlag, fachlichen Tickzeitpunkt und genaue
Korrespondenzabhaengigkeiten. Sie rekonstruiert selbst die einzige erlaubte
Aenderung und vergleicht alle Bytes. Zieltyp und ID sind exakt gebunden;
SQLs nachgestellte Leerzeichen oder Gross-/Kleinschreibungsaliase sind
kein anderer erlaubter Audit-/Outboxzielname. Zulaessig sind neue quellgebundene
Fristenhinweise in `case`/`psurReview`, neue originalgebundene
Korrespondenzhinweise in `eudamedDelivery` sowie der typisierte
`operations/heartbeat`. Vorhandene Hinweise bleiben unveraendert.
Andere Typen, Freigaben, Personen, Paketdaten, Empfaenger, Nachweise,
Fristwerte und Statusuebergaenge sind nicht schreibbar. Auch zukuenftige,
dem Adapter noch unbekannte Felder duerfen nicht verschwinden.

Aktion, Maschinenidentitaet `cohors-operations`/Rolle `Workflow`,
Auditbegruendung, kanonischer Hash, Outbox-Typ/-Payload und Commandbeleg
werden in SQL bestimmt. Es gibt keine frei waehlbaren Actor-/Approval-/
Audit-/Outboxparameter. Der Tickzeitpunkt ist wie bisher die fachliche
`TimeProvider`-Zeit; der Azure-Host verwendet `TimeProvider.System`,
synthetische Tests eine ausdrueckliche Testuhr. Er ersetzt keine
urspruengliche Awareness, LegalDue oder menschliche Freigabezeit.

Bestehende Objekt-/Abhaengigkeits-, Command- und Aggregatlocks werden
beibehalten. Erwartete Version, Originalhashes, aktuelle Quellen,
Aufbewahrungssperren und Auditkopf werden in derselben Transaktion
geprueft. Aggregat, Historie, Audit, Outbox, Commandbeleg und reine
Fristen-Projektionsversion werden gemeinsam geschrieben oder gemeinsam
zurueckgerollt. Ein identischer bereits verbuchter Auftrag gibt nur den
Originalbeleg zurueck; Versions-/Abhaengigkeitskonflikte bleiben explizit.

## Rollen und genaue Objektberechtigungen

`S` = SELECT, `I` = INSERT, `U` = UPDATE. Laufzeit jeweils mit CONNECT,
verschluesselter Verbindung und eigenem Principal; keine gemeinsame
API-/Operations-/Bootstrapidentitaet.

| dbo-Objekt | API | Operations |
| --- | --- | --- |
| Aggregates | S,I,U | S |
| AggregateVersions | S,I | S |
| Commands | S,I | keine direkte Berechtigung |
| AuditEvents | S,I | keine direkte Berechtigung |
| Outbox | S,I,U | keine direkte Berechtigung |
| Inbox | S,I | keine direkte Berechtigung |
| Deadlines | S,I,U | keine direkte Berechtigung |
| SearchProjection | S,I,U | keine direkte Berechtigung |
| SchemaVersions | S | S |
| CohorsOperationsWrite | nur lokaler integrierter Watchdog; Azure-API benoetigt es nicht | EXECUTE |
| CohorsOperationsQuote | keine direkte Berechtigung | keine direkte Berechtigung |

Der eigene Operations-Principal wird Mitglied von
`cohors_operations_runtime`, nicht von `db_datawriter`, `db_ddladmin`
oder `db_owner`. Die Migration erteilt genau die drei SELECT-Rechte und
EXECUTE auf der einen Prozedur; sie verweigert direktes INSERT/UPDATE/
DELETE auf allen neun Anwendungstabellen sowie die aufgefuehrten
DDL-/Benutzer-/Rollenrechte und ALTER/TAKE OWNERSHIP auf `dbo`.
UPDATE wird zusaetzlich auf allen Spalten derselben neun Tabellen
verweigert: SQLs Vorrang eines Spalten-GRANT vor einem blossen
Tabellen-DENY darf keine Hintertuer oeffnen.
Die ununterbrochene `dbo`-Ownershipkette der
Prozedur erlaubt trotzdem ihre eingeschraenkte atomare Operation.
Das ist **keine Kind-RLS** und kein Verzicht auf den Watchdog.

Der eigene Operations-Functions-Host prueft diese SQL-Rollentrennung
vor jedem Tick und im Readinesspfad. Ein Owner-, DDL-, Securityadmin-
oder direkt schreibfaehiger Principal scheitert explizit; es gibt keinen
Fallback auf generisches DML. Die lokale API-Watchdogemulation ist davon
getrennt und bleibt ein synthetischer API-Prozess, keine eingeschraenkte
Azure-Operationsidentitaet.

Der Infrastrukturbootstrap muss den exakten, separaten Entra-Principal
zuordnen, Altmitgliedschaften/-grants ausschliessen und darf keine
zusatzliche Prozedur-/Schema-EXECUTE-Vollmacht erteilen. `dbo`, Datenbank-
oder Serveradministratoren sind kein eingeschraenkter Runtimeprincipal.
Direktes `sp_getapplock` im bestehenden Public-Principal-Namensraum
verleiht keine Tabellenrechte.

Der getrennte Migrationsprincipal braucht fuer den tatsaechlichen
Migrationslauf Tabellen-/Index-DDL aus001/002, CREATE/ALTER FUNCTION und
PROCEDURE in `dbo`, CREATE/ALTER ROLE sowie die expliziten Objekt-GRANT/
DENY-Berechtigungen aus003c und Schreibzugriff auf SchemaVersions.
Das ist eine administrativ kontrollierte Migration, kein dauerhafter
Runtime-Grant und kein Anlass, Operations `db_owner` zu geben.
Der getrennte synthetische Seed verwendet danach die bestehenden
API-aehnlichen Fachschreibrechte und den vorgesehenen Blobzugang;
Operations fuehrt weder Migration noch Seed aus.

DELETE auf SearchProjection bleibt ausschliesslich dem separat
freigegebenen `SyntheticLocal`-Retentionpfad vorbehalten, nicht dem
Azure-Runtimevertrag. Inbox-/Outboxverarbeitung, Searchprojektion,
kontrollierter manueller Wiederanlauf und Fachfreigaben bleiben API-
Aufgaben; der Operations-Timer fuehrt nur den Watchdog aus.

## Nachweisgrenze

Die fokussierte Kategorie `OperationsSqlBoundary` umfasst den echten
Case-/PSUR-/Korrespondenztick unter einem datenbankseitig
eingeschraenkten Principal, negative direkte und Prozedurzugriffe,
einen Fehler nach dem Aggregatschreibbeginn sowie parallelen identischen
Retry und veraltete Versionen. Die Principal-Tests verwenden je eigene
`WITHOUT LOGIN`-Benutzer innerhalb der bereits journalgebundenen lokalen
Testdatenbank und echte `EXECUTE AS USER`-Verbindungen ohne Pooling.
Es entstehen keine Serverlogins oder fremden Datenbankaenderungen.
Ergebnisse und genaue Build-/Paketbindung werden in der
[Validierungsdokumentation](validation.md) getrennt von Quellabsichten
festgehalten. Ein nativer SQL-Test ist kein Entra-/Azure-Betriebsnachweis.
