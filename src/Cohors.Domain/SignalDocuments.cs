using System.Security.Cryptography;
using System.Text;

namespace Cohors.Domain;

public sealed record SignalDocumentBlob(string Uri, string Hash, int Length);
public sealed record SignalDocumentCaseReference(VersionReference Source, string Reference, string Reason);
public sealed record ImportedSignalDocument(string Contract, string Name, string MediaType,
    SignalDocumentBlob Original, SignalDocumentBlob Text, string ExtractionVersion,
    string Scope, string Provenance, string Rights, string KnowledgeStatus,
    DateTimeOffset? FirstKnownAt, string KnowledgeReason, string ConfirmedBy,
    SignalDocumentCaseReference? ComparisonCase);

public static class SignalDocumentRules
{
    public const string Contract = "cohors-signal-document-1";
    public const string ExtractionVersion = "utf8-text-1";
    public const string Status = "ReadOnlyDocument";
    public const int MaximumBytes = 128 * 1024;
    public const int MaximumTextCharacters = 30000;
    public const string Boundary = "Tatsaechliche Datei und deterministisch gelesener UTF-8-Text, keine KI-Analyse oder Replay-Ausgabe. " +
        "Ein Signaldokument ist kein eigener Herstellerfall. Vergleichsbezug und Bytegleichheit beweisen keinen identischen Vorfall. " +
        "Keine Fallanlage, Zusammenfuehrung, Kenntnis-/Fristaenderung oder Freigabe durch den Import. " +
        "Konkrete Vorkommnisse und fruehere Fallkenntnis ueber die bestehenden menschlichen Fall-/Awarenesspfade pruefen.";

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Extract(string name, string mediaType, ReadOnlySpan<byte> bytes)
    {
        Guard.Text(name, "Originaldateiname", 5, 150);
        Guard.Against(name != name.Trim() || name.Any(c => c is '/' or '\\' or ':' || char.IsControl(c)) ||
            !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || mediaType != "text/plain",
            "signal_document_type", "Nur eine UTF-8-.txt-Datei mit text/plain ist unterstuetzt. Keine PDF-/Office-Extraktion oder OCR.", 415);
        Guard.Against(bytes.Length is 0 or > MaximumBytes, "signal_document_size", "Textdatei bis 128 KiB erforderlich.", 413);
        var textBytes = bytes.StartsWith("\uFEFF"u8) ? bytes[3..] : bytes;
        Guard.Against(textBytes.StartsWith("%PDF-"u8) || textBytes.StartsWith("PK\x03\x04"u8),
            "signal_document_type", "PDF-/Office-/ZIP-Bytes werden nicht durch Umbenennen zu lesbarem Klartext.", 415);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(textBytes); }
        catch (DecoderFallbackException)
        {
            throw new DomainException("signal_document_encoding", "Ungueltiges UTF-8; kein Ersatztext oder OCR-Erfolg erzeugt.");
        }
        Guard.Against(text.Length > MaximumTextCharacters ||
            text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')),
            "signal_document_text", "Maximal 30000 Textzeichen ohne binaere Steuerzeichen erforderlich.");
        Guard.Text(text, "Tatsaechlicher Dokumenttext", 20, MaximumTextCharacters);
        return text;
    }

    public static ImportedSignalDocument RequireIntegrity(PublicSourceRun run)
    {
        var document = run.Document ?? throw new DomainException("signal_document_missing", "Kein importiertes Signaldokument.", 404);
        Guard.Against(run.Source != "document" || run.Status != Status || document.Contract != Contract ||
            document.ExtractionVersion != ExtractionVersion || run.Items is not { Length: 1 } ||
            document.Original is null || document.Text is null,
            "signal_document_integrity", "Dokument-, Extraktions- oder Quellenbindung ist unvollstaendig.", 409);
        var original = document.Original ?? throw new DomainException("signal_document_integrity", "Originalbindung fehlt.", 409);
        var text = document.Text ?? throw new DomainException("signal_document_integrity", "Textbindung fehlt.", 409);
        Guard.Against(original.Uri != "cohors:signal-original:" + original.Hash ||
            text.Uri != "cohors:signal-text:" + text.Hash ||
            original.Hash is not { Length: 64 } || original.Hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            original.Length is <= 0 or > MaximumBytes ||
            document.Scope is not ("Synthetic" or "PublicAuthorized") ||
            document.KnowledgeStatus is not ("Known" or "NotEstablished") ||
            (document.KnowledgeStatus == "Known") != (document.FirstKnownAt is not null) ||
            document.FirstKnownAt > run.At ||
            original.Hash != run.Items[0].ProvenanceHash ||
            text.Hash != Canonical.HashText(run.Items[0].Summary) ||
            text.Length != Encoding.UTF8.GetByteCount(run.Items[0].Summary),
            "signal_document_integrity", "Originalhash, unveraenderter gelesener Text und Quellenakte stimmen nicht ueberein.", 409);
        return document;
    }
}
