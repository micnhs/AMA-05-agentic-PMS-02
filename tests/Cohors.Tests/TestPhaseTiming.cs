using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cohors.Tests;

internal enum CorrespondencePhase
{
    CaseStarted, DossierBuilt, OriginalTransportCompleted,
    FirstAcceptanceStarted, FirstAcceptanceCompleted,
    CorrespondenceProcessingStarted, CorrespondenceProcessingCompleted,
    OriginalChecksCompleted, ReplyDraftStarted, ReplyDraftCompleted,
    MedicalApprovalStarted, MedicalApprovalCompleted, RegulatoryApprovalStarted, RegulatoryApprovalCompleted,
    ReplyQueueTransportStarted, ReplyQueueTransportCompleted,
    ReplyAcceptanceStarted, ReplyAcceptanceCompleted, FinalAssertionsStarted,
    DuplicateReceiptStarted, DuplicateReceiptCompleted, ResubmissionGuardStarted, CaseCompleted
}

internal sealed class TestPhaseTiming
{
    private readonly string variant;
    private readonly string? path;
    private readonly long started = Stopwatch.GetTimestamp();
    private int sequence;

    internal TestPhaseTiming(string variant, string? path)
    {
        if (variant is not ("Comment" or "InformationRequest")) throw new ArgumentOutOfRangeException(nameof(variant));
        if (path is not null && !Path.IsPathFullyQualified(path)) throw new ArgumentException("Timing output requires an absolute path.", nameof(path));
        this.variant = variant;
        this.path = path;
    }

    internal static TestPhaseTiming FromEnvironment(string variant) =>
        new(variant, Environment.GetEnvironmentVariable("COHORS_TEST_PHASE_FILE"));

    internal void Record(CorrespondencePhase phase)
    {
        if (path is null) return;
        var timestamp = Stopwatch.GetTimestamp();
        var record = JsonSerializer.Serialize(new
        {
            variant, phase = phase.ToString(), sequence = ++sequence,
            timestampTicks = timestamp, frequency = Stopwatch.Frequency,
            elapsedTicks = timestamp - started, utc = DateTimeOffset.UtcNow
        });
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(record + "\n"));
        stream.Flush(flushToDisk: true);
    }
}
