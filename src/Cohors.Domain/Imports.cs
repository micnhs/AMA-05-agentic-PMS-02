using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cohors.Domain;

public enum IntakeImportChannel { SyntheticDistributorFeedback, SyntheticClinicFeedback, SyntheticCallCenterLog }
public enum IntakeImportStatus { Received, Processed }
public enum IntakeRowOutcome { Pending, Registered, Duplicate, Rejected, CorrectionRequired }
public enum IntakeKnown { None, Source, Case }
public enum DistributionImportStatus { Validated, Rejected }

public sealed record CsvRecord(int Line, string Raw, string[] Fields, string? Error = null);
public sealed record ImportOriginal(string FileName, string Uri, string Hash, int Bytes);
public sealed record ImportFinding(int Line, string RowHash, string Code, string Message);

public sealed record IntakeImportRow(int Line, string RowHash, IntakeRowOutcome Outcome, string Code, string Reason,
    string? ExternalRef, string? IncidentKey, string? SourceId, IntakeKnown Known, int? DuplicateOf, VersionReference? Case);
public sealed record IntakeImportCounts(int Rows, int Registered, int Duplicates, int Rejected, int Pending,
    int CorrectionRequired);
public sealed record IntakeImport(string Id, int Version, string ProductId, IntakeImportChannel Channel,
    ImportOriginal Original, DateTimeOffset ReceivedAt, string ImportedBy, string Reason, string Contract,
    string Boundary, IntakeImportStatus Status, IntakeImportRow[] Rows, IntakeImportCounts Counts,
    DateTimeOffset? ProcessedAt);
public sealed record IntakeCandidate(int Line, string RowHash, string ExternalRef, string IncidentKey, string SourceId,
    string? Udi, string? Batch, string Country, string Jurisdiction, Seriousness Seriousness,
    DateTimeOffset? OccurredAt, DateTimeOffset AwarenessAt, string Title, string Description);
public sealed record IntakeEvaluation(IntakeImportRow[] Rows, IntakeCandidate[] Candidates);

public sealed record DistributionRow(int Line, string RowHash, string RowRef, string UdiDi, string Batch, string SiteRef,
    string Country, string Jurisdiction, DateOnly DeliveredOn, int UnitsDelivered, int UnitsImplanted, int Recipients,
    int AdditionalImplants);
public sealed record DistributionTotals(int Rows, int ValidRows, int Findings, int UnitsDelivered, int UnitsImplanted,
    int Recipients, int AdditionalImplants);
public sealed record DistributionImport(string Id, int Version, string ProductId, ImportOriginal Original,
    DateTimeOffset ReceivedAt, string ImportedBy, string Reason, string Contract, string Boundary,
    DistributionImportStatus Status, DistributionRow[] Rows, ImportFinding[] Findings, DistributionTotals Totals);
public sealed record DistributionEvaluation(DistributionRow[] Rows, ImportFinding[] Findings);

public sealed record ExposureSource(string Contract, VersionReference Import, string DateBasis, string ProposalHash,
    int[] Lines, int Recipients, int AdditionalImplants, string ApprovedBy, DateTimeOffset ApprovedAt, string Reason);
public sealed record DenominatorProposal(string ProductId, VersionReference Import, string Region, DateOnly Start,
    DateOnly End, string Unit, int Count, int Recipients, int AdditionalImplants, int[] Lines, DateOnly CoveredFrom,
    DateOnly CoveredThrough, string DateBasis, string Boundary, string Hash);
public sealed record DenominatorPreview(DenominatorProposal Proposal, string TargetExposureId, int TargetVersion,
    int? CurrentCount, bool Unchanged);
public sealed record AffectedBatch(string Batch, string UdiDi, int Rows, int UnitsDelivered, int UnitsImplanted,
    int Recipients, string[] Jurisdictions, DateOnly FirstDelivery, DateOnly LastDelivery);
public sealed record AffectedMarket(string Jurisdiction, string Country, string Scope, int Rows, int UnitsDelivered,
    int UnitsImplanted, int Recipients);
public sealed record DistributionAffected(string ProductId, VersionReference Import, string[] Batches,
    string[] Jurisdictions, string[] UnknownBatches, AffectedBatch[] Matched, AffectedMarket[] Markets, int[] Lines,
    string Method, string Boundary, string Hash);

public static class CaseIdentity
{
    // Mirrors CaseService.Register and the demo seed, so import rows resolve to the same incident.
    public static string For(string productId, string incidentKey) =>
        Canonical.Hash(new { manufacturer = "cohors-synthetic", ProductId = productId, IncidentKey = incidentKey })[..32];
}

public static class SyntheticCsv
{
    public static CsvRecord[] Parse(string content, string header, int maximumRows)
    {
        Guard.Against(content.Any(c => char.IsControl(c) && c is not '\t' and not '\r' and not '\n'), "csv_encoding",
            "Steuerzeichen sind in synthetischen CSV-Importen nicht zulaessig.");
        var text = content.StartsWith('\uFEFF') ? content[1..] : content;
        Guard.Against(text.Length == 0, "csv_empty", "Leere Datei; nichts importiert.");
        var lines = text.Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        for (var index = 0; index < lines.Length; index++)
        {
            var carriage = lines[index].IndexOf('\r');
            Guard.Against(carriage >= 0 && carriage != lines[index].Length - 1, "csv_line_ending",
                "Wagenruecklauf ist nur als Zeilenende (CRLF) zulaessig; mehrzeilige Felder werden nicht unterstuetzt.");
            if (carriage >= 0) lines[index] = lines[index][..^1];
        }
        Guard.Against(lines[0] != header, "csv_header", $"Kopfzeile muss exakt lauten: {header}");
        Guard.Against(lines.Length == 1, "csv_empty", "Keine Datenzeilen; nichts importiert.");
        Guard.Against(lines.Length - 1 > maximumRows, "csv_rows",
            $"Hoechstens {maximumRows} Datenzeilen je Import; Datei bitte aufteilen.");
        return lines.Skip(1).Select((raw, index) =>
        {
            var (fields, error) = Split(raw);
            return new CsvRecord(index + 2, raw, fields, error);
        }).ToArray();
    }

    private static (string[] Fields, string? Error) Split(string raw)
    {
        var fields = new List<string>();
        var index = 0;
        while (true)
        {
            if (index < raw.Length && raw[index] == '"')
            {
                var value = new StringBuilder();
                index++;
                while (true)
                {
                    if (index >= raw.Length) return ([], "Anfuehrungszeichen ist nicht geschlossen.");
                    if (raw[index] == '"')
                    {
                        if (index + 1 < raw.Length && raw[index + 1] == '"')
                        {
                            value.Append('"');
                            index += 2;
                            continue;
                        }
                        index++;
                        break;
                    }
                    value.Append(raw[index++]);
                }
                fields.Add(value.ToString());
                if (index == raw.Length) return (fields.ToArray(), null);
                if (raw[index] != ',') return ([], "Nach einem geschlossenen Anfuehrungszeichen muss ein Komma oder das Zeilenende folgen.");
                index++;
            }
            else
            {
                var end = raw.IndexOf(',', index);
                var value = end < 0 ? raw[index..] : raw[index..end];
                if (value.Contains('"')) return ([], "Anfuehrungszeichen sind nur am Feldanfang zulaessig.");
                fields.Add(value);
                if (end < 0) return (fields.ToArray(), null);
                index = end + 1;
            }
        }
    }
}

public static partial class ImportRules
{
    public const string IntakeContract = "cohors-channel-intake-import-1";
    public const string DistributionContract = "cohors-distribution-import-1";
    public const string SourceContract = "cohors-exposure-source-1";
    public const string IntakeHeader =
        "externalRef,incidentKey,productId,udi,batch,country,jurisdiction,seriousness,occurredAt,awarenessAt,title,description";
    public const string DistributionHeader =
        "rowRef,productId,udiDi,batch,siteRef,country,jurisdiction,deliveredOn,unitsDelivered,unitsImplanted,recipients";
    public const int IntakeColumns = 12;
    public const int DistributionColumns = 11;
    public const int MaximumBytes = 256 * 1024;
    public const int MaximumIntakeRows = 100;
    public const int MaximumDistributionRows = 500;
    public const string Unit = "Implants";
    public const string DateBasis = "Lieferdatum (keine patientenbezogenen Implantationsdaten)";
    public const string IntakeBoundary =
        "Synthetischer strukturierter Kanal-Batchimport. Jede Zeile ist ein Eingangssignal ohne bestaetigte Sicherheitsbewertung; " +
        "jede Zeile wird registriert, als Dublette verknuepft, mit Grund abgewiesen oder zur menschlichen Korrektur zurueckgestellt. " +
        "Keine Dokument- oder KI-Extraktion, kein Echtkanal.";
    public const string DistributionBoundary =
        "Synthetische aggregierte Liefer-/Implantationsdaten ohne direkten Personenbezug; kein ERP, kein Patientenregister. " +
        "Absatz, implantierte Einheiten und Empfaenger bleiben getrennt; ein Nenner wird erst durch menschliche Freigabe wirksam.";
    public const string ProposalBoundary =
        "Vorschlag, keine Freigabe: Vollstaendigkeit der Lieferdaten und die Zuordnung Lieferdatum zu Implantationszeitraum " +
        "sind menschlich zu bestaetigen. Gebundene Trend-/PSUR-Ergebnisse behalten ihre Nennerversion.";
    public const string AffectedMethod =
        "Exakter Abgleich von Charge und Jurisdiktion gegen validierte Lieferzeilen; keine Textaehnlichkeit, keine Personenzuordnung.";
    public const string AffectedBoundary =
        "Lieferbezug zeigt moegliche Exposition, keine festgestellte Betroffenheit einzelner Personen. Fehlende Lieferzeilen sind " +
        "kein Nachweis fehlender Betroffenheit. Keine Field-Safety-Massnahme und keine Meldung.";
    private static readonly UTF8Encoding Strict = new(false, true);

    public static ImportOriginal Original(string kind, string fileName, string content)
    {
        Guard.Against(fileName is null || !FilePattern().IsMatch(fileName), "import_file_name",
            "Dateiname: hoechstens 120 Zeichen aus A-Z, a-z, 0-9, Punkt, Unterstrich, Bindestrich mit Endung .csv.");
        Guard.Against(content is null, "csv_empty", "Leere Datei; nichts importiert.");
        int bytes;
        try { bytes = Strict.GetByteCount(content!); }
        catch (EncoderFallbackException error)
        {
            throw new DomainException("csv_encoding", "Datei ist kein gueltiges UTF-8.", 422, error);
        }
        Guard.Against(bytes > MaximumBytes, "csv_size", $"Hoechstens {MaximumBytes} Byte je synthetischem Import.");
        return new(fileName!, $"cohors:synthetic-import:{kind}:{fileName}", Canonical.HashText(content!), bytes);
    }

    public static string IntakeId(string productId, IntakeImportChannel channel, string contentHash) =>
        "intake-" + Canonical.Hash(new { productId, channel = channel.ToString(), contentHash })[..32];

    public static string DistributionId(string productId, string contentHash) =>
        "distribution-" + Canonical.Hash(new { productId, contentHash })[..32];

    public static string SourceId(IntakeImportChannel channel, string externalRef) => $"{channel}:{externalRef}";

    public static string ExposureId(string productId, string region, DateOnly start, DateOnly end) =>
        $"exposure-{productId}-{region}-{start.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{end.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    public static string? ExpectedCountry(string jurisdiction) => jurisdiction switch
    {
        "GB" or "NI" => "GB",
        _ when jurisdiction.Length == 5 && jurisdiction.StartsWith("EU-", StringComparison.Ordinal) => jurisdiction[3..],
        _ => null
    };

    public static IntakeEvaluation EvaluateIntake(CsvRecord[] records, Device device, IntakeImportChannel channel,
        DateTimeOffset referenceTime)
    {
        var checks = records.Select(r => CheckIntake(r, device, channel, referenceTime)).ToArray();
        var conflicts = new HashSet<int>();
        var copies = new Dictionary<int, int>();
        foreach (var group in checks.Where(c => c.ExternalRef is not null)
            .GroupBy(c => c.ExternalRef!, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var members = group.OrderBy(c => c.Line).ToArray();
            if (members.Select(c => c.RowHash).Distinct(StringComparer.Ordinal).Count() > 1)
                foreach (var member in members.Where(m => m.Code is null)) conflicts.Add(member.Line);
            else if (members[0].Code is null)
                foreach (var copy in members.Skip(1)) copies.Add(copy.Line, members[0].Line);
        }
        var rows = checks.Select(check =>
        {
            var candidate = check.Candidate;
            if (conflicts.Contains(check.Line))
                return Row(check, IntakeRowOutcome.Rejected, "row_ref_conflict",
                    "Gleiche Kanalreferenz mit abweichendem Inhalt in derselben Datei; keine stille Auswahl.");
            if (copies.TryGetValue(check.Line, out var first))
                return Row(check, IntakeRowOutcome.Duplicate, "row_duplicate",
                    $"Identische Zeile {first} in derselben Datei; einmalig uebernommen.") with { DuplicateOf = first };
            return candidate is null
                ? Row(check, IntakeRowOutcome.Rejected, check.Code!, check.Message!)
                : Row(check, IntakeRowOutcome.Pending, "pending", "Validiert; Uebernahme folgt im selben Importlauf.");
        }).ToArray();
        var pending = rows.Where(r => r.Outcome == IntakeRowOutcome.Pending).Select(r => r.Line).ToHashSet();
        return new(rows, checks.Where(c => pending.Contains(c.Line)).Select(c => c.Candidate!).ToArray());

        static IntakeImportRow Row(IntakeCheck check, IntakeRowOutcome outcome, string code, string reason) =>
            new(check.Line, check.RowHash, outcome, code, reason, check.ExternalRef, check.Candidate?.IncidentKey,
                check.Candidate?.SourceId, IntakeKnown.None, null, null);
    }

    // `others` holds the other channel imports of the product; the import being processed must not be included.
    // `registrations` holds the first stored version (version 1) of target cases that changed since; see Conflict.
    public static IntakeImportRow Classify(IntakeImportRow row, IntakeCandidate candidate, string productId,
        IReadOnlyCollection<SafetyCase> cases, IReadOnlyCollection<IntakeImport> others,
        IReadOnlyCollection<SafetyCase>? registrations = null)
    {
        Guard.Against(row.Outcome != IntakeRowOutcome.Pending || row.Line != candidate.Line || row.RowHash != candidate.RowHash,
            "import_state", "Importzeile und validierter Kandidat stimmen nicht ueberein.", 409);
        var id = CaseIdentity.For(productId, candidate.IncidentKey);
        if (cases.Any(c => c.ProductId == productId && c.Id != id && c.Sources.Any(s => s.ExternalId == candidate.SourceId)))
            return Refuse(row, "source_incident_conflict",
                "Kanalreferenz ist bereits einem anderen Vorfall zugeordnet; keine Umhaengung durch Import.");
        var target = cases.SingleOrDefault(c => c.Id == id);
        var source = target?.Sources.FirstOrDefault(s => s.ExternalId == candidate.SourceId);
        if (source is not null && source.Hash != Canonical.HashText(candidate.Description))
            return Refuse(row, "source_revision", "Gleiche Kanalreferenz mit geaendertem Inhalt; als neue Quellversion erfassen.");
        var registration = registrations?.FirstOrDefault(c => c.Id == id && c.Version == 1);
        if (Conflict(target, registration, candidate, productId, others) is { } finding) return Hold(row, target, finding);
        if (target is null) return row;
        return row with { Known = source is null ? IntakeKnown.Case : IntakeKnown.Source };
    }

    // Recorded facts of one channel source: the rows of other imports taken over and bound to a case (identical copies follow
    // their primary row), otherwise the first registration of the case. Rows still open stop a differing row but confirm
    // nothing. Differing or unconfirmable facts never pass as an unchanged duplicate.
    // The first registration is the case's version 1: a later human change (for example a medical reclassification)
    // changes the case, not the facts recorded for its first source. A changed case without that version is unconfirmed.
    private static string? Conflict(SafetyCase? target, SafetyCase? registration, IntakeCandidate candidate, string productId,
        IReadOnlyCollection<IntakeImport> others)
    {
        var source = target?.Sources.FirstOrDefault(s => s.ExternalId == candidate.SourceId);
        if (source is not null && SourceChanges(target!, candidate) is { Length: > 0 } changes)
            return $"Gleiche Kanalquelle mit abweichenden Angaben: {string.Join("; ", changes)}.";
        var rows = Provenance(productId, candidate.SourceId, others);
        if (rows.FirstOrDefault(r => r.RowHash != candidate.RowHash) is { } differing)
            return $"Gleiche Kanalquelle, aber die Zeile weicht von der {(differing.Open ? "noch nicht abgeschlossenen" : "bereits uebernommenen")} " +
                $"Zeile {differing.Line} aus {differing.FileName} ab (Schweregrad, Rechtsraum, Land, UDI, Charge, Vorfallreferenz oder Schreibweise).";
        if (source is null || rows.Any(r => !r.Open)) return null;
        var first = target!.Version == 1 ? target : registration;
        if (target.Sources[0].ExternalId != candidate.SourceId || first is null || first.Id != target.Id || first.Version != 1 ||
            first.Sources.FirstOrDefault()?.ExternalId != candidate.SourceId)
            return "Gleiche Kanalquelle bereits erfasst, aber ihre Zeilenangaben (Schweregrad, Rechtsraum, Land, UDI, Charge) sind weder " +
                "durch eine abgeschlossene Importzeile noch durch die Erstregistrierung des Vorfalls belegt.";
        var facts = CaseChanges(first, candidate);
        return facts.Length == 0 ? null
            : $"Gleiche Kanalquelle mit abweichenden Angaben gegenueber der Erstregistrierung des Vorfalls: {string.Join("; ", facts)}.";
    }

    private sealed record ProvenanceRow(string FileName, int Line, string RowHash, bool Open);

    private static ProvenanceRow[] Provenance(string productId, string sourceId, IReadOnlyCollection<IntakeImport> others) =>
        others.Where(i => i.ProductId == productId)
            .OrderBy(i => i.ReceivedAt).ThenBy(i => i.Id, StringComparer.Ordinal)
            .SelectMany(i => i.Rows.Where(r => r.SourceId == sourceId &&
                    r.Outcome is IntakeRowOutcome.Pending or IntakeRowOutcome.Registered or IntakeRowOutcome.Duplicate)
                .Select(r => new ProvenanceRow(i.Original.FileName, r.Line, r.RowHash, !Accepted(r, i.Rows))))
            .ToArray();

    // Only a row taken over and bound to a case, or an identical copy bound with such a primary row, confirms recorded facts.
    // A copy of a primary row that is still pending stays open: the outcome Duplicate alone confirms nothing.
    private static bool Accepted(IntakeImportRow row, IEnumerable<IntakeImportRow> rows) =>
        Bound(row) && (row.Code != "row_duplicate" ||
            (rows.FirstOrDefault(p => p.Line == row.DuplicateOf) is { Code: not "row_duplicate" } primary &&
                Bound(primary) && primary.Case == row.Case && primary.RowHash == row.RowHash));

    private static bool Bound(IntakeImportRow row) =>
        row.Outcome is IntakeRowOutcome.Registered or IntakeRowOutcome.Duplicate && row.Case is not null;

    private static string[] CaseChanges(SafetyCase target, IntakeCandidate candidate)
    {
        var changes = new List<string>();
        if (candidate.Seriousness != target.Seriousness)
            changes.Add($"Schweregrad {candidate.Seriousness} statt erfasst {target.Seriousness}");
        if (candidate.Country != target.Country || candidate.Jurisdiction != target.Jurisdiction)
            changes.Add($"Land/Rechtsraum {candidate.Country}/{candidate.Jurisdiction} statt erfasst {target.Country}/{target.Jurisdiction}");
        if (candidate.Udi != target.Udi) changes.Add("abweichende UDI");
        if (candidate.Batch != target.Batch) changes.Add("abweichende Charge");
        return [.. changes];
    }

    public static string[] SourceChanges(SafetyCase target, IntakeCandidate candidate)
    {
        var source = target.Sources.First(s => s.ExternalId == candidate.SourceId);
        var changes = new List<string>();
        if (candidate.AwarenessAt != source.AwarenessAt)
            changes.Add($"erste Kenntnis {Utc(candidate.AwarenessAt)} statt erfasst {Utc(source.AwarenessAt)}");
        var occurred = candidate.OccurredAt ?? candidate.AwarenessAt;
        if (target.Evidence.Where(e => e.Id == source.Id).OrderBy(e => e.Version).FirstOrDefault() is { } evidence &&
            occurred != evidence.ValidFrom)
            changes.Add($"Ereigniszeitpunkt {Utc(occurred)} statt erfasst {Utc(evidence.ValidFrom)}");
        if (!string.Equals(candidate.Title, source.Title, StringComparison.Ordinal)) changes.Add("abweichender Titel");
        return [.. changes];
    }

    public static IntakeImportRow Reclassify(IntakeImportRow received, IntakeCandidate candidate, string productId,
        IReadOnlyCollection<SafetyCase> cases, IReadOnlyCollection<IntakeImport> others,
        IReadOnlyCollection<SafetyCase>? registrations = null)
    {
        var current = Classify(received with { Known = IntakeKnown.None }, candidate, productId, cases, others, registrations);
        if (current.Outcome != IntakeRowOutcome.Pending) return current;
        // Conflicts use the current state; the outcome uses the state at receipt, so a case that another import created
        // during this run is reported as linked. A resumed run's own registration is settled before this point.
        var known = received.Known == IntakeKnown.None || current.Known == IntakeKnown.None ? IntakeKnown.None
            : received.Known == IntakeKnown.Case || current.Known == IntakeKnown.Case ? IntakeKnown.Case : IntakeKnown.Source;
        return current with { Known = known };
    }

    // `own` is true only when this row's registration call recorded the source (its key is in the case audit trail).
    // Otherwise registration returned an already recorded source unchanged; `others` must then be read after that call
    // and the row is an unchanged duplicate only when every recorded fact of the source matches it. `registration` is the
    // case's version 1 when `registered` has changed since (see Conflict).
    public static IntakeImportRow Settle(IntakeImportRow row, IntakeCandidate candidate, SafetyCase registered,
        IReadOnlyCollection<IntakeImport> others, bool own, SafetyCase? registration = null)
    {
        Guard.Against(row.Line != candidate.Line || row.RowHash != candidate.RowHash || row.SourceId != candidate.SourceId,
            "import_state", "Importzeile und validierter Kandidat stimmen nicht ueberein.", 409);
        Guard.Against(row.Outcome != IntakeRowOutcome.Pending || row.SourceId is null ||
            registered.Sources.All(s => s.ExternalId != row.SourceId), "import_state",
            "Registrierter Vorfall enthaelt die Kanalquelle der Importzeile nicht.", 409);
        var reference = PortfolioRules.Reference(registered);
        if (!own)
            return Conflict(registered, registration, candidate, registered.ProductId, others) is { } finding
                ? Hold(row, registered, finding)
                : row with { Outcome = IntakeRowOutcome.Duplicate, Code = "source_known", Case = reference, Known = IntakeKnown.Source,
                    Reason = "Kanalquelle mit identischer Zeile (Titel, Text, Ereignis- und Kenntniszeitpunkt, Schweregrad, Rechtsraum, " +
                        "Land, UDI, Charge) bereits erfasst; Vorfall unveraendert." };
        return row.Known switch
        {
            IntakeKnown.Case => row with { Outcome = IntakeRowOutcome.Duplicate, Code = "case_known", Case = reference,
                Reason = "Vorfall existierte bereits; Originalquelle verknuepft, neue Information menschlich triagieren." },
            _ when registered.Sources[0].ExternalId == row.SourceId => row with { Outcome = IntakeRowOutcome.Registered,
                Code = "registered", Case = reference, Known = IntakeKnown.None,
                Reason = "Als Eingangssignal registriert (Eingang, Bewertung offen); kein bestaetigter Sicherheitsfall." },
            _ => row with { Outcome = IntakeRowOutcome.Duplicate, Code = "case_linked", Case = reference, Known = IntakeKnown.None,
                Reason = "Vorfall entstand waehrend des Imports; Originalquelle verknuepft, neue Information menschlich triagieren." }
        };
    }

    public static IntakeImportRow Refuse(IntakeImportRow row, DomainException error) => Refuse(row, error.Code, error.Message);

    private static IntakeImportRow Refuse(IntakeImportRow row, string code, string reason) =>
        row with { Outcome = IntakeRowOutcome.Rejected, Code = code, Reason = reason, Known = IntakeKnown.None };

    private static IntakeImportRow Hold(IntakeImportRow row, SafetyCase? target, string finding) =>
        row with
        {
            Outcome = IntakeRowOutcome.CorrectionRequired, Code = "source_changed",
            Known = target is null ? IntakeKnown.None
                : target.Sources.Any(s => s.ExternalId == row.SourceId) ? IntakeKnown.Source : IntakeKnown.Case,
            Case = target is null ? null : PortfolioRules.Reference(target),
            Reason = finding + " Keine identische Dublette: zur menschlichen Korrektur zurueckgestellt. " +
                (target is null ? "Kein Vorfall angelegt und keine Quelle uebernommen" : "Fall, Quellen und Fristen bleiben unveraendert") +
                "; der Import nimmt keine Fristkorrektur vor. Abweichungen prueft ein Mensch ueber die bestehenden " +
                "Fallwege; eine fruehere Kenntnis korrigieren nur Medical oder RA ueber die Kenntniskorrektur des Falls."
        };

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static IntakeImportRow[] Propagate(IntakeImportRow[] rows)
    {
        var byLine = rows.ToDictionary(r => r.Line);
        return rows.Select(row =>
        {
            if (row.Code != "row_duplicate" || row.DuplicateOf is not { } line) return row;
            var primary = byLine[line];
            return primary.Outcome switch
            {
                IntakeRowOutcome.Rejected => row with { Outcome = IntakeRowOutcome.Rejected, Code = primary.Code,
                    Reason = $"Identische Kopie von Zeile {line}: {primary.Reason}" },
                IntakeRowOutcome.CorrectionRequired => row with { Outcome = IntakeRowOutcome.CorrectionRequired, Code = primary.Code,
                    Case = primary.Case, Reason = $"Identische Kopie von Zeile {line}: {primary.Reason}" },
                _ => row with { Case = primary.Case }
            };
        }).ToArray();
    }

    public static IntakeImportCounts Count(IntakeImportRow[] rows) => new(rows.Length,
        rows.Count(r => r.Outcome == IntakeRowOutcome.Registered), rows.Count(r => r.Outcome == IntakeRowOutcome.Duplicate),
        rows.Count(r => r.Outcome == IntakeRowOutcome.Rejected), rows.Count(r => r.Outcome == IntakeRowOutcome.Pending),
        rows.Count(r => r.Outcome == IntakeRowOutcome.CorrectionRequired));

    public static DistributionEvaluation EvaluateDistribution(CsvRecord[] records, Device device, DateOnly today)
    {
        var findings = new List<ImportFinding>();
        var valid = new List<DistributionRow>();
        foreach (var record in records)
        {
            var (row, finding) = CheckDistribution(record, device, today);
            if (row is not null) valid.Add(row);
            else findings.Add(finding!);
        }
        foreach (var row in valid.GroupBy(r => r.RowRef, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .SelectMany(g => g).ToArray())
        {
            findings.Add(new(row.Line, row.RowHash, "row_ref_duplicate", "Zeilenreferenz mehrfach vergeben; keine stille Zusammenfuehrung."));
            valid.Remove(row);
        }
        foreach (var row in valid.GroupBy(r => r.Batch, StringComparer.Ordinal)
            .Where(g => g.Select(r => r.UdiDi).Distinct(StringComparer.Ordinal).Count() > 1).SelectMany(g => g).ToArray())
        {
            findings.Add(new(row.Line, row.RowHash, "batch_udi_conflict", "Charge ist mehreren UDI-DI zugeordnet; Chargenkonsistenz verletzt."));
            valid.Remove(row);
        }
        return new(valid.OrderBy(r => r.Line).ToArray(), findings.OrderBy(f => f.Line).ToArray());
    }

    public static DistributionTotals Totals(int records, DistributionEvaluation evaluation) => new(records,
        evaluation.Rows.Length, evaluation.Findings.Length, evaluation.Rows.Sum(r => r.UnitsDelivered),
        evaluation.Rows.Sum(r => r.UnitsImplanted), evaluation.Rows.Sum(r => r.Recipients),
        evaluation.Rows.Sum(r => r.AdditionalImplants));

    public static DenominatorProposal Propose(DistributionImport import, VersionReference reference, string region,
        DateOnly start, DateOnly end, DateOnly today)
    {
        RequireUsable(import, reference);
        PortfolioRules.RequireRegion(region);
        Guard.Against(end < start, "denominator_period", "Periodenende liegt vor dem Beginn.");
        Guard.Against(end >= today, "denominator_period_open", "Nur abgeschlossene Perioden; das Ende muss vor dem heutigen UTC-Tag liegen.");
        var rows = import.Rows.Where(r => r.DeliveredOn >= start && r.DeliveredOn <= end &&
            PortfolioRules.InRegion(r.Country, r.Jurisdiction, region)).ToArray();
        var count = rows.Sum(r => r.UnitsImplanted);
        Guard.Against(count == 0, "denominator_empty", "Keine implantierten Einheiten in Periode und Region; kein Nullnenner.");
        var proposal = new DenominatorProposal(import.ProductId, reference, region, start, end, Unit, count,
            rows.Sum(r => r.Recipients), rows.Sum(r => r.AdditionalImplants), rows.Select(r => r.Line).ToArray(),
            import.Rows.Min(r => r.DeliveredOn), import.Rows.Max(r => r.DeliveredOn), DateBasis, ProposalBoundary, "");
        return proposal with { Hash = Canonical.Hash(proposal) };
    }

    public static DistributionAffected Affected(DistributionImport import, VersionReference reference, Device device,
        IEnumerable<string> batches, IEnumerable<string> jurisdictions)
    {
        RequireUsable(import, reference);
        Guard.Against(device.Id != import.ProductId, "device_unknown", "Produktfamilie passt nicht zum Import.");
        var wanted = batches.Select(b => b.Trim()).Where(b => b.Length > 0).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        Guard.Against(wanted.Length is < 1 or > 20 || wanted.Any(b => !BatchPattern().IsMatch(b)), "affected_batches",
            "1 bis 20 Chargen im synthetischen Format DEMO-... angeben.");
        var markets = jurisdictions.Select(j => j.Trim()).Where(j => j.Length > 0).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        Guard.Against(markets.Any(m => !device.Markets.Contains(m, StringComparer.Ordinal)), "affected_jurisdiction",
            "Jurisdiktion ist fuer diese Produktfamilie nicht freigegeben.");
        var scope = markets.Length == 0 ? device.Markets.Order(StringComparer.Ordinal).ToArray() : markets;
        var rows = import.Rows.Where(r => wanted.Contains(r.Batch, StringComparer.Ordinal) &&
            scope.Contains(r.Jurisdiction, StringComparer.Ordinal)).OrderBy(r => r.Line).ToArray();
        var matched = rows.GroupBy(r => r.Batch, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AffectedBatch(g.Key, g.First().UdiDi, g.Count(), g.Sum(r => r.UnitsDelivered),
                g.Sum(r => r.UnitsImplanted), g.Sum(r => r.Recipients),
                g.Select(r => r.Jurisdiction).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                g.Min(r => r.DeliveredOn), g.Max(r => r.DeliveredOn))).ToArray();
        var affectedMarkets = rows.GroupBy(r => r.Jurisdiction, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AffectedMarket(g.Key, g.First().Country, MarketScope(g.Key), g.Count(),
                g.Sum(r => r.UnitsDelivered), g.Sum(r => r.UnitsImplanted), g.Sum(r => r.Recipients))).ToArray();
        var result = new DistributionAffected(import.ProductId, reference, wanted, scope,
            wanted.Where(b => matched.All(m => m.Batch != b)).ToArray(), matched, affectedMarkets,
            rows.Select(r => r.Line).ToArray(), AffectedMethod, AffectedBoundary, "");
        return result with { Hash = Canonical.Hash(result) };
    }

    public static Exposure Approve(Exposure? current, string id, DenominatorProposal proposal, int expectedCount,
        IEnumerable<Exposure> others, string approvedBy, DateTimeOffset at, string reason)
    {
        Guard.Against(proposal.Count != expectedCount, "denominator_changed",
            "Nennervorschlag hat sich seit der Anzeige geaendert; bitte erneut pruefen.", 409);
        Guard.Against(current is not null && (current.Id != id || current.ProductId != proposal.ProductId ||
            current.Region != proposal.Region || current.Start != proposal.Start || current.End != proposal.End),
            "denominator_target", "Zielnenner passt nicht zu Produkt, Region und Periode des Vorschlags.", 409);
        Guard.Against(current?.Source?.ProposalHash == proposal.Hash, "denominator_unchanged",
            "Genau dieser Nennervorschlag ist bereits freigegeben.", 409);
        foreach (var other in others.Where(e => e.Approved && e.ProductId == proposal.ProductId && e.Id != id &&
            e.Region != proposal.Region && e.Start == proposal.Start && e.End == proposal.End))
            Guard.Against(other.Unit != proposal.Unit || (proposal.Region == "Worldwide"
                ? other.Count > proposal.Count : other.Region == "Worldwide" && proposal.Count > other.Count),
                "exposure_subset", "Teilregion darf die Worldwide-Basis derselben Periode nicht uebersteigen; Einheiten muessen gleich sein.", 409);
        return new Exposure(id, (current?.Version ?? 0) + 1, proposal.ProductId, proposal.Region, proposal.Start, proposal.End,
            proposal.Unit, proposal.Count, "Synthetische implantierte Einheiten laut freigegebenem Lieferimport (Lieferdatum im Fenster)",
            $"Menschlich freigegebener synthetischer Lieferimport {proposal.Import.Id} v{proposal.Import.Version}; kein ERP, keine Patientendaten.",
            true, new ExposureSource(SourceContract, proposal.Import, proposal.DateBasis, proposal.Hash, proposal.Lines,
                proposal.Recipients, proposal.AdditionalImplants, approvedBy, at, reason));
    }

    // The other denominators of the same product and period that Approve compares against, bound into the commit:
    // present ones by version and hash, absent regions as empty reservations of their deterministic id. A parallel
    // approval of another region then fails the store's dependency check instead of passing against a stale snapshot.
    public static VersionReference[] PeriodDependencies(IEnumerable<Exposure> exposures, DenominatorProposal proposal, string targetId)
    {
        var period = exposures.Where(e => e.ProductId == proposal.ProductId && e.Start == proposal.Start &&
            e.End == proposal.End && e.Id != targetId).ToArray();
        var absent = PortfolioRules.Regions.Where(r => r != proposal.Region && period.All(e => e.Region != r))
            .Select(r => new VersionReference(ExposureId(proposal.ProductId, r, proposal.Start, proposal.End), 0, ""));
        return [.. period.Select(e => new VersionReference(e.Id, e.Version, Canonical.Hash(e))).Concat(absent)
            .OrderBy(d => d.Id, StringComparer.Ordinal)];
    }

    private static void RequireUsable(DistributionImport import, VersionReference reference)
    {
        Guard.Against(reference.Id != import.Id || reference.Version != import.Version || reference.Hash != Canonical.Hash(import),
            "import_version", "Importreferenz ist nicht an diese Importfassung gebunden.", 409);
        Guard.Against(import.Status != DistributionImportStatus.Validated || import.Rows.Length == 0, "import_rejected",
            "Abgewiesener Import ist keine Nenner- oder Betroffenheitsquelle.", 409);
    }

    private static string MarketScope(string jurisdiction) => jurisdiction switch
    {
        "GB" => "GB: Drittland mit eigenem Meldeprofil",
        "NI" => "NI: eigenes Meldeprofil, getrennt von GB",
        _ => "EU-Mitgliedstaat: MDR-Meldeprofil"
    };

    private sealed record IntakeCheck(int Line, string RowHash, string? ExternalRef, IntakeCandidate? Candidate,
        string? Code, string? Message);

    private static IntakeCheck CheckIntake(CsvRecord record, Device device, IntakeImportChannel channel, DateTimeOffset referenceTime)
    {
        var hash = Canonical.HashText(record.Raw);
        IntakeCheck Fail(string code, string message, string? reference = null) => new(record.Line, hash, reference, null, code, message);
        if (record.Error is not null) return Fail("row_csv", record.Error);
        if (string.IsNullOrWhiteSpace(record.Raw)) return Fail("row_empty", "Leere Zeile; nichts uebernommen.");
        if (record.Fields.Length != IntakeColumns)
            return Fail("row_columns", $"Erwartet {IntakeColumns} Spalten, gefunden {record.Fields.Length}.");
        var f = record.Fields.Select(v => v.Trim()).ToArray();
        var reference = f[0];
        if (!ExternalRefPattern().IsMatch(reference))
            return Fail("row_external_ref", "Kanalreferenz: 1-60 Zeichen aus A-Z, a-z, 0-9, Punkt, Unterstrich, Bindestrich.");
        if (!IncidentKeyPattern().IsMatch(f[1]))
            return Fail("row_incident_key", "Vorfallreferenz: 3-60 Zeichen aus A-Z, a-z, 0-9, Punkt, Unterstrich, Bindestrich.", reference);
        if (f[2] != device.Id) return Fail("row_product", "Produktfamilie der Zeile passt nicht zum gewaehlten Importscope.", reference);
        var udi = f[3].Length == 0 ? null : f[3];
        if (udi is not null && !Regex.IsMatch(udi, $"^DEMO-{Regex.Escape(device.Model)}-[A-Z0-9]{{1,20}}\\z", RegexOptions.CultureInvariant))
            return Fail("row_udi", "UDI leer lassen oder synthetisch mit Modellpraefix angeben; keine Ersatzannahme.", reference);
        var batch = f[4].Length == 0 ? null : f[4];
        if (batch is not null && !BatchPattern().IsMatch(batch))
            return Fail("row_batch", "Charge leer lassen oder im synthetischen Format DEMO-... angeben.", reference);
        if (!CountryPattern().IsMatch(f[5])) return Fail("row_country", "Land: ISO-3166-Alpha-2 in Grossbuchstaben.", reference);
        if (!device.Markets.Contains(f[6], StringComparer.Ordinal))
            return Fail("row_market", "Jurisdiktion ist fuer diese Produktfamilie nicht freigegeben.", reference);
        if (ExpectedCountry(f[6]) != f[5]) return Fail("row_jurisdiction_country", "Land und Jurisdiktion widersprechen sich.", reference);
        if (!Enum.GetNames<Seriousness>().Contains(f[7], StringComparer.Ordinal))
            return Fail("row_seriousness", "Schweregrad: exakter Katalogwert erforderlich.", reference);
        DateTimeOffset? occurred = null;
        if (f[8].Length > 0 && (occurred = Instant(f[8])) is null)
            return Fail("row_date", "Ereigniszeitpunkt: ISO-8601 mit Sekunden und Zeitzone.", reference);
        if (f[9].Length == 0) return Fail("row_awareness_missing", "Erste Kenntnis fehlt; keine Ersatzannahme.", reference);
        if (Instant(f[9]) is not { } awareness)
            return Fail("row_date", "Erste Kenntnis: ISO-8601 mit Sekunden und Zeitzone.", reference);
        if (awareness > referenceTime) return Fail("row_awareness_future", "Erste Kenntnis liegt nach dem Importeingang.", reference);
        if (occurred > awareness) return Fail("row_awareness_order", "Ereignis liegt nach der ersten Kenntnis.", reference);
        if (f[10].Length is < 5 or > 200 || f[11].Length is < 20 or > 12000)
            return Fail("row_text", "Titel 5-200 und Beschreibung 20-12000 Zeichen.", reference);
        if (!f[11].StartsWith("SYNTHETISCH", StringComparison.Ordinal))
            return Fail("row_synthetic", "Beschreibung muss mit SYNTHETISCH beginnen; nur synthetische Pilotdaten.", reference);
        return new(record.Line, hash, reference, new IntakeCandidate(record.Line, hash, reference, f[1], SourceId(channel, reference),
            udi, batch, f[5], f[6], Enum.Parse<Seriousness>(f[7]), occurred, awareness, f[10], f[11]), null, null);
    }

    private static (DistributionRow? Row, ImportFinding? Finding) CheckDistribution(CsvRecord record, Device device, DateOnly today)
    {
        var hash = Canonical.HashText(record.Raw);
        (DistributionRow?, ImportFinding?) Fail(string code, string message) => (null, new(record.Line, hash, code, message));
        if (record.Error is not null) return Fail("row_csv", record.Error);
        if (string.IsNullOrWhiteSpace(record.Raw)) return Fail("row_empty", "Leere Zeile; nichts uebernommen.");
        if (record.Fields.Length != DistributionColumns)
            return Fail("row_columns", $"Erwartet {DistributionColumns} Spalten, gefunden {record.Fields.Length}.");
        var f = record.Fields.Select(v => v.Trim()).ToArray();
        if (!RowRefPattern().IsMatch(f[0])) return Fail("row_ref", "Zeilenreferenz: 1-40 Zeichen aus A-Z, a-z, 0-9, Punkt, Unterstrich, Bindestrich.");
        if (f[1] != device.Id) return Fail("row_product", "Produktfamilie der Zeile passt nicht zum gewaehlten Importscope.");
        if (!Regex.IsMatch(f[2], $"^DEMO-{Regex.Escape(device.Model)}-[A-Z0-9-]{{1,30}}\\z", RegexOptions.CultureInvariant))
            return Fail("row_udi_di", "UDI-DI muss synthetisch sein und zum Modell der Produktfamilie passen.");
        if (!BatchPattern().IsMatch(f[3])) return Fail("row_batch", "Charge im synthetischen Format DEMO-... erforderlich.");
        var site = SitePattern().Match(f[4]);
        if (!site.Success) return Fail("row_site", "Standort nur pseudonym im Format DEMO-SITE-LAND-000.");
        if (!CountryPattern().IsMatch(f[5])) return Fail("row_country", "Land: ISO-3166-Alpha-2 in Grossbuchstaben.");
        if (!device.Markets.Contains(f[6], StringComparer.Ordinal))
            return Fail("row_market", "Jurisdiktion ist fuer diese Produktfamilie nicht freigegeben.");
        if (ExpectedCountry(f[6]) != f[5]) return Fail("row_jurisdiction_country", "Land und Jurisdiktion widersprechen sich.");
        if (site.Groups[1].Value != f[5]) return Fail("row_site_country", "Standortland und Lieferland widersprechen sich.");
        if (!DateOnly.TryParseExact(f[7], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var delivered))
            return Fail("row_date", "Lieferdatum im Format JJJJ-MM-TT erforderlich.");
        if (delivered > today) return Fail("row_delivery_future", "Lieferdatum liegt in der Zukunft.");
        if (!Number(f[8], out var units) || units is < 1 or > 100000)
            return Fail("row_units_delivered", "Gelieferte Einheiten: ganze Zahl von 1 bis 100000.");
        if (!Number(f[9], out var implanted) || implanted > units)
            return Fail("row_units_implanted", "Implantierte Einheiten: ganze Zahl von 0 bis zur Liefermenge.");
        if (!Number(f[10], out var recipients) || (implanted == 0 ? recipients != 0 : recipients < 1 || recipients > implanted))
            return Fail("row_recipients", "Empfaenger: 0 ohne Implantation, sonst 1 bis Anzahl implantierter Einheiten.");
        return (new(record.Line, hash, f[0], f[2], f[3], f[4], f[5], f[6], delivered, units, implanted, recipients,
            implanted - recipients), null);
    }

    private static bool Number(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static DateTimeOffset? Instant(string value) =>
        InstantPattern().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToUniversalTime() : null;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,116}\.csv\z", RegexOptions.CultureInvariant)]
    private static partial Regex FilePattern();
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,60}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalRefPattern();
    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,60}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IncidentKeyPattern();
    [GeneratedRegex(@"^DEMO-[A-Z0-9-]{1,35}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BatchPattern();
    [GeneratedRegex(@"^[A-Z]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CountryPattern();
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RowRefPattern();
    [GeneratedRegex(@"^DEMO-SITE-([A-Z]{2})-[0-9]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SitePattern();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstantPattern();
}
