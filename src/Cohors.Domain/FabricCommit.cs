using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cohors.Domain.Fabric;

public enum CommitPhase
{
    Reserved, InputsProtected, Prepared, Decided, BlockClosed, GeoProtected, ProofsProtected,
    Certified, CertificateProtected, Activated, Aborted
}

public enum ProtectionClass { AcknowledgedBusiness, Awareness, RepeatableDraft }
public sealed record AggregateKey(string Kind, string Id);
public sealed record RevisionHead(AggregateKey Key, int Revision, string Hash);
public sealed record RevisionWrite(RevisionHead Before, string StateJson)
{
    public RevisionHead After() => new(Before.Key, checked(Before.Revision + 1), Canonical.HashText(StateJson));
}
public sealed record EffectIntent(string Id, string Kind, string PayloadJson);
public sealed record ProtectedObject(string Key, string Hash, long Length, DateTimeOffset RetainThrough);
public sealed record JournalTarget(string Server, string Database, string Region);
public sealed record ArchiveTarget(string Id, string Region, string Subscription, string Administration);
public sealed record TrustedCertifier(string PrincipalId, string KeyVersion, string SubjectPublicKeyInfo);
public sealed record ProtectionTopology(string Environment, string WriterId, long WriterEpoch,
    string JournalIncarnation, JournalTarget Primary, JournalTarget Secondary,
    ImmutableArray<ArchiveTarget> Archives, TrustedCertifier Certifier);
public sealed record WriterFence(string Environment, string WriterId, long Epoch, string JournalIncarnation, string TopologyHash);
public sealed record ProtectionInventory(long RequiredHighWatermark, long JournalHighWatermark,
    ImmutableDictionary<string, long> ArchiveHighWatermarks, string InventoryHash);
public sealed record JournalAnchor(long Sequence, string Hash);
public sealed record Reservation(string CommandId, string Fingerprint, ImmutableArray<AggregateKey> LockedKeys, bool Activated);
public sealed record CommitRequest(string Environment, string CommandId, string Action, string ActorId,
    string AuthorizationSnapshotHash, string RuleVersion, string SchemaVersion, string CanonicalVersion,
    string AuditDraftJson, ProtectionClass Protection,
    DateTimeOffset ObservedAt, DateTimeOffset RetainThrough,
    ImmutableArray<RevisionHead> ReadSet, ImmutableArray<RevisionWrite> Writes,
    ImmutableArray<ProtectedObject> Originals, ImmutableArray<EffectIntent> Effects);
public sealed record ArchiveReadback(string ArchiveId, string ObjectKey, string Version,
    string Hash, long Length, string PolicyETag, bool Locked, bool ProtectedAppend,
    DateTimeOffset RetainThrough, DateTimeOffset ReadAt)
{
    public static ArchiveReadback FromBytes(string archiveId, string objectKey, string version,
        ReadOnlySpan<byte> bytes, string policyETag, bool locked, bool protectedAppend,
        DateTimeOffset retainThrough, DateTimeOffset readAt) =>
        new(archiveId, objectKey, version, Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length, policyETag, locked, protectedAppend, retainThrough, readAt);
}
public sealed record PendingRevisionReceipt(string CommandId, string EnvelopeHash,
    string ReadSetHash, string WriteSetHash, string EffectSetHash, bool OutboxBlocked);
public sealed record JournalDecision(string Environment, string CommandId, string Fingerprint,
    string JournalIncarnation, long WriterEpoch, long TransactionId, long Sequence,
    string PredecessorHash, string ReadSetHash, string WriteSetHash, string PendingHash);
public sealed record LedgerBlockProof(string JournalIncarnation, long DecisionTransactionId,
    string DecisionHash, long BlockId, string DigestJson, DateTimeOffset ClosedAt)
{
    public string DigestHash() => Canonical.HashText(DigestJson);
}
public sealed record GeoBarrierProof(string JournalIncarnation, long WriterEpoch,
    JournalTarget Primary, JournalTarget Secondary, long ClosedBlockId, string DigestHash,
    bool ExecutedOnPrimary, bool LogHardened, string SecondaryDecisionHash,
    string SecondaryDigestHash, DateTimeOffset BarrierCompletedAt, DateTimeOffset SecondaryReadAt);
public sealed record CertificateBody(string Environment, string CommandId, string Fingerprint,
    string TopologyHash, string JournalIncarnation, long WriterEpoch, long Sequence,
    string DecisionHash, long BlockId, string DigestHash, string GeoProofHash,
    string InputProofHash, ImmutableArray<ArchiveReadback> ProofArchives, string InventoryHash,
    string PendingHash, string AuthorizationSnapshotHash,
    string RuleVersion, string SchemaVersion, string CertifierId, string KeyVersion,
    DateTimeOffset VerifiedAt, ImmutableArray<string> VerifiedChecks);
public sealed record CommitCertificate(CertificateBody Body, string Signature);
public sealed record ActivationReceipt(string CommandId, string EnvelopeHash,
    string CertificateHash, ImmutableArray<RevisionHead> CurrentSnapshot,
    ImmutableArray<string> ReleasedEffects, DateTimeOffset ActivatedAt);
public sealed record TerminalReservation(string Environment, string CommandId, string Fingerprint,
    string JournalIncarnation, long WriterEpoch, bool RejectedWithoutDecision);

/// <summary>
/// In-process contract reducer, not a storage adapter or service attestation.
/// Observations must come from independently authorised adapters before runtime integration.
/// </summary>
public sealed record FabricCommit
{
    public const string EmptyHash = "0000000000000000000000000000000000000000000000000000000000000000";
    public static readonly ImmutableArray<string> RequiredChecks =
        ["authorization", "fencing", "head-chain", "history-completeness", "ledger-native-verification", "originals"];

    public CommitPhase Phase { get; private init; }
    public CommitRequest Request { get; private init; }
    public ProtectionTopology Topology { get; private init; }
    public JournalAnchor Predecessor { get; private init; }
    public string Fingerprint { get; private init; }
    public string InputProofHash { get; private init; } = "";
    public ImmutableArray<ArchiveReadback> ProofReadbacks { get; private init; } = [];
    public ProtectionInventory Inventory { get; private init; }
    public ImmutableArray<ArchiveReadback> InputReadbacks { get; private init; } = [];
    public string PendingHash { get; private init; } = "";
    public PendingRevisionReceipt? Pending { get; private init; }
    public JournalDecision? Decision { get; private init; }
    public LedgerBlockProof? Block { get; private init; }
    public GeoBarrierProof? Geo { get; private init; }
    public CommitCertificate? Certificate { get; private init; }
    public DateTimeOffset? CertificateStoredAt { get; private init; }
    public bool CanAcknowledge => Phase == CommitPhase.Activated;
    public bool BlocksFollowingCommand => Phase is not (CommitPhase.Activated or CommitPhase.Aborted);
    public string EnvelopeJson() => Canonical.Serialize(Request);
    public WriterFence Fence() => new(Request.Environment, Topology.WriterId, Topology.WriterEpoch,
        Topology.JournalIncarnation, Canonical.Hash(Topology));

    private FabricCommit(CommitRequest request, ProtectionTopology topology, ProtectionInventory inventory, JournalAnchor predecessor)
    {
        Request = request;
        Topology = topology;
        Inventory = inventory;
        Predecessor = predecessor;
        Fingerprint = Canonical.Hash(request);
        Phase = CommitPhase.Reserved;
    }

    public static FabricCommit Reserve(CommitRequest request, ProtectionTopology topology,
        ProtectionInventory inventory, JournalAnchor predecessor, ImmutableArray<RevisionHead> certifiedSnapshot,
        ImmutableArray<Reservation> reservations)
    {
        ValidateTopology(topology, inventory);
        Guard.Against(request.Environment != topology.Environment, "fabric_environment", "Command and topology environments differ.");
        Text(request.CommandId, "CommandId");
        Text(request.Action, "Action");
        Text(request.ActorId, "ActorId");
        Text(request.RuleVersion, "RuleVersion");
        Text(request.SchemaVersion, "SchemaVersion");
        Guard.Against(request.CanonicalVersion != "cohors-canonical-json-v1",
            "fabric_canonical_version", "An explicitly supported canonicalization version is required.");
        CanonicalJson(request.AuditDraftJson);
        Hash(request.AuthorizationSnapshotHash);
        Hash(predecessor.Hash);
        Guard.Against(predecessor.Sequence < inventory.RequiredHighWatermark || predecessor.Sequence < 0 ||
            predecessor.Sequence == long.MaxValue, "fabric_journal_anchor", "Journal predecessor does not cover the protected history.");
        Guard.Against(!Enum.IsDefined(request.Protection) || request.RetainThrough <= request.ObservedAt,
            "fabric_protection", "Explicit protection class and effective retention are required.");
        ValidateHeads(request.ReadSet);
        ValidateHeads(certifiedSnapshot);
        Guard.Against(!SameHeads(request.ReadSet, certifiedSnapshot), "fabric_snapshot_conflict",
            "The complete certified read set must match the requested snapshot.", 409);
        Guard.Against(request.Writes.IsDefaultOrEmpty || request.Originals.IsDefault || request.Effects.IsDefault ||
            reservations.IsDefault, "fabric_manifest", "Explicit write, original, effect and reservation sets are required.");
        Guard.Against(request.Writes.Select(w => w.Before.Key).Distinct().Count() != request.Writes.Length,
            "fabric_duplicate_write", "Each aggregate may be written only once per command.");
        foreach (var write in request.Writes)
        {
            Guard.Against(!request.ReadSet.Contains(write.Before) || write.Before.Revision == int.MaxValue,
                "fabric_write_predecessor", "Every write needs its exact predecessor in the read set.");
            CanonicalJson(write.StateJson);
        }
        Guard.Against(request.Originals.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != request.Originals.Length ||
            request.Effects.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != request.Effects.Length,
            "fabric_duplicate_manifest", "Duplicate original or effect IDs are forbidden.");
        foreach (var original in request.Originals)
        {
            Text(original.Key, "Original key");
            Hash(original.Hash);
            Guard.Against(new[] { "replay-", "proofs-", "certificate-" }.Any(prefix => original.Key == prefix + request.CommandId) ||
                original.Length < 0 ||
                original.RetainThrough < request.RetainThrough,
                "fabric_original_manifest", "Originals need distinct immutable keys, byte lengths and sufficient retention.");
        }
        foreach (var effect in request.Effects)
        {
            Text(effect.Id, "Effect ID");
            Text(effect.Kind, "Effect kind");
            CanonicalJson(effect.PayloadJson);
        }
        var fingerprint = Canonical.Hash(request);
        foreach (var reservation in reservations)
        {
            Guard.Against(reservation.LockedKeys.IsDefault, "fabric_reservation", "Reservation lock set is missing.");
            if (reservation.CommandId == request.CommandId)
                throw new DomainException(reservation.Fingerprint == fingerprint ? "fabric_resolve_receipt" : "fabric_command_conflict",
                    "Resolve the original command receipt; never create a second reservation.", 409);
            Guard.Against(!reservation.Activated && reservation.LockedKeys.Intersect(request.ReadSet.Select(h => h.Key)).Any(),
                "fabric_predecessor_pending", "A preceding dependent command is not yet certified and activated.", 409);
        }
        return new(request, topology, inventory, predecessor);
    }

    public FabricCommit ResolveRetry(CommitRequest request)
    {
        Guard.Against(request.Environment != Request.Environment || request.CommandId != Request.CommandId ||
            Canonical.Hash(request) != Fingerprint, "fabric_command_conflict",
            "The same CommandId cannot be reused with different content.", 409);
        return this;
    }

    public ImmutableArray<ProtectedObject> RequiredInputs() =>
        [.. Request.Originals, new("replay-" + Request.CommandId, Fingerprint,
            Encoding.UTF8.GetByteCount(EnvelopeJson()), Request.RetainThrough)];

    public FabricCommit ProtectInputs(ImmutableArray<ArchiveReadback> readbacks, WriterFence fence)
    {
        Require(CommitPhase.Reserved, fence);
        ValidateReadbacks(RequiredInputs(), readbacks, Request.ObservedAt);
        return this with { Phase = CommitPhase.InputsProtected, InputProofHash = ReadbacksHash(readbacks), InputReadbacks = readbacks };
    }

    public FabricCommit Prepare(PendingRevisionReceipt receipt, WriterFence fence)
    {
        Require(CommitPhase.InputsProtected, fence);
        Guard.Against(receipt.CommandId != Request.CommandId || receipt.EnvelopeHash != Fingerprint ||
            receipt.ReadSetHash != HeadsHash(Request.ReadSet) || receipt.WriteSetHash != WritesHash() ||
            receipt.EffectSetHash != Canonical.Hash(Request.Effects) || !receipt.OutboxBlocked,
            "fabric_pending_binding", "Pending state and blocked outbox must atomically bind the complete envelope.");
        return this with { Phase = CommitPhase.Prepared, PendingHash = Canonical.Hash(receipt), Pending = receipt };
    }

    public FabricCommit Decide(JournalDecision decision, WriterFence fence)
    {
        Require(CommitPhase.Prepared, fence);
        Guard.Against(decision.Environment != Request.Environment || decision.CommandId != Request.CommandId ||
            decision.Fingerprint != Fingerprint || decision.JournalIncarnation != Topology.JournalIncarnation ||
            decision.WriterEpoch != Topology.WriterEpoch || decision.TransactionId <= 0 ||
            decision.Sequence != Predecessor.Sequence + 1 || decision.PredecessorHash != Predecessor.Hash ||
            decision.ReadSetHash != HeadsHash(Request.ReadSet) || decision.WriteSetHash != WritesHash() ||
            decision.PendingHash != PendingHash,
            "fabric_decision_binding", "Journal decision must bind the predecessor, epoch, pending state and all read/write dependencies.");
        return this with { Phase = CommitPhase.Decided, Decision = decision };
    }

    public FabricCommit CloseBlock(LedgerBlockProof block, WriterFence fence)
    {
        Require(CommitPhase.Decided, fence);
        ValidateDigest(block);
        Guard.Against(block.JournalIncarnation != Topology.JournalIncarnation ||
            block.DecisionTransactionId != Decision!.TransactionId || block.DecisionHash != Canonical.Hash(Decision) ||
            block.BlockId < 0 || block.ClosedAt < Request.ObservedAt,
            "fabric_block_binding", "Closed ledger block must cover this exact journal decision.");
        return this with { Phase = CommitPhase.BlockClosed, Block = block };
    }

    public FabricCommit ProtectGeo(GeoBarrierProof proof, WriterFence fence)
    {
        Require(CommitPhase.BlockClosed, fence);
        Guard.Against(proof.JournalIncarnation != Topology.JournalIncarnation ||
            proof.WriterEpoch != Topology.WriterEpoch || proof.Primary != Topology.Primary || proof.Secondary != Topology.Secondary ||
            !proof.ExecutedOnPrimary || !proof.LogHardened || proof.ClosedBlockId != Block!.BlockId ||
            proof.DigestHash != Block.DigestHash() || proof.SecondaryDecisionHash != Canonical.Hash(Decision) ||
            proof.SecondaryDigestHash != Block.DigestHash() || proof.BarrierCompletedAt < Block.ClosedAt ||
            proof.SecondaryReadAt < proof.BarrierCompletedAt,
            "fabric_geo_binding", "Post-digest log durability and separate matching secondary readback are both required.");
        return this with { Phase = CommitPhase.GeoProtected, Geo = proof };
    }

    public string ProofBundleJson()
    {
        Guard.Against(Geo is null, "fabric_geo_missing", "Geo protection must precede the proof bundle.");
        return Canonical.Serialize(new { Fingerprint, Topology, Inventory, InputReadbacks, Pending, Decision, Block, Geo });
    }

    public ProtectedObject ProofBundleObject()
    {
        var json = ProofBundleJson();
        return new("proofs-" + Request.CommandId, Canonical.HashText(json), Encoding.UTF8.GetByteCount(json), Request.RetainThrough);
    }

    public FabricCommit ProtectProofs(ImmutableArray<ArchiveReadback> readbacks, WriterFence fence)
    {
        Require(CommitPhase.GeoProtected, fence);
        ValidateReadbacks([ProofBundleObject()], readbacks, Geo!.SecondaryReadAt);
        return this with { Phase = CommitPhase.ProofsProtected, ProofReadbacks = readbacks };
    }

    public CertificateBody CertificateScope(DateTimeOffset verifiedAt)
    {
        Guard.Against(Phase != CommitPhase.ProofsProtected, "fabric_phase", "Certificate scope requires all preceding protection stages.");
        Guard.Against(verifiedAt < ProofReadbacks.Max(r => r.ReadAt) || verifiedAt >= Request.RetainThrough,
            "fabric_verification_time", "Independent verification must follow its evidence within effective retention.");
        return new(Request.Environment, Request.CommandId, Fingerprint, Canonical.Hash(Topology), Topology.JournalIncarnation,
            Topology.WriterEpoch, Decision!.Sequence, Canonical.Hash(Decision), Block!.BlockId, Block.DigestHash(),
            Canonical.Hash(Geo), InputProofHash, ProofReadbacks, Inventory.InventoryHash, PendingHash,
            Request.AuthorizationSnapshotHash, Request.RuleVersion,
            Request.SchemaVersion, Topology.Certifier.PrincipalId, Topology.Certifier.KeyVersion, verifiedAt, RequiredChecks);
    }

    public FabricCommit Certify(CommitCertificate certificate, WriterFence fence)
    {
        Require(CommitPhase.ProofsProtected, fence);
        var expected = CertificateScope(certificate.Body.VerifiedAt);
        Guard.Against(Canonical.Hash(certificate.Body) != Canonical.Hash(expected), "fabric_certificate_scope",
            "The independent certificate does not cover the exact decision, checks and protection chain.");
        try
        {
            using var key = RSA.Create();
            var encodedKey = Convert.FromBase64String(Topology.Certifier.SubjectPublicKeyInfo);
            key.ImportSubjectPublicKeyInfo(encodedKey, out var read);
            Guard.Against(read != encodedKey.Length || key.KeySize < 2048 ||
                !key.VerifyData(Encoding.UTF8.GetBytes(Canonical.Serialize(certificate.Body)),
                    Convert.FromBase64String(certificate.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
                "fabric_certificate_signature", "Independent certificate signature is invalid.");
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        {
            throw new DomainException("fabric_certificate_signature", "Independent certificate key or signature is invalid.", 422, error);
        }
        return this with { Phase = CommitPhase.Certified, Certificate = certificate };
    }

    public ProtectedObject CertificateObject()
    {
        Guard.Against(Certificate is null, "fabric_certificate_missing", "A signed certificate is required.");
        var json = Canonical.Serialize(Certificate);
        return new("certificate-" + Request.CommandId, Canonical.HashText(json), Encoding.UTF8.GetByteCount(json), Request.RetainThrough);
    }

    public FabricCommit ProtectCertificate(ImmutableArray<ArchiveReadback> readbacks, WriterFence fence)
    {
        Require(CommitPhase.Certified, fence);
        ValidateReadbacks([CertificateObject()], readbacks, Certificate!.Body.VerifiedAt);
        return this with { Phase = CommitPhase.CertificateProtected, CertificateStoredAt = readbacks.Max(r => r.ReadAt) };
    }

    public ImmutableArray<RevisionHead> ExpectedActivationSnapshot() => Request.ReadSet.Select(head =>
        Request.Writes.SingleOrDefault(write => write.Before.Key == head.Key)?.After() ?? head).ToImmutableArray();

    public ImmutableArray<RevisionHead> ReadActivatedSnapshot(ImmutableArray<RevisionHead> currentHeads, WriterFence fence)
    {
        Require(CommitPhase.Activated, fence);
        ValidateHeads(currentHeads);
        Guard.Against(!SameHeads(currentHeads, ExpectedActivationSnapshot()), "fabric_historical_receipt",
            "This command is historical; its snapshot must not replace the current certified heads.", 409);
        return currentHeads;
    }

    public FabricCommit Activate(ActivationReceipt receipt, WriterFence fence)
    {
        Require(CommitPhase.CertificateProtected, fence);
        ValidateHeads(receipt.CurrentSnapshot);
        Guard.Against(receipt.CommandId != Request.CommandId || receipt.EnvelopeHash != Fingerprint ||
            receipt.CertificateHash != CertificateObject().Hash || !SameHeads(receipt.CurrentSnapshot, ExpectedActivationSnapshot()) ||
            receipt.ActivatedAt < CertificateStoredAt || receipt.ActivatedAt >= Request.RetainThrough ||
            receipt.ReleasedEffects.IsDefault || receipt.ReleasedEffects.Length != Request.Effects.Length ||
            !receipt.ReleasedEffects.Order(StringComparer.Ordinal).SequenceEqual(Request.Effects.Select(e => e.Id).Order(StringComparer.Ordinal)),
            "fabric_activation_binding", "Activation and released outbox must match the complete certified revision vector.");
        return this with { Phase = CommitPhase.Activated };
    }

    public FabricCommit Abort(TerminalReservation terminal, WriterFence fence)
    {
        RequireFence(fence);
        Guard.Against(Phase is not (CommitPhase.Reserved or CommitPhase.InputsProtected or CommitPhase.Prepared),
            "fabric_roll_forward", "A decided command cannot be rolled back, including after a timeout.", 409);
        Guard.Against(terminal.Environment != Request.Environment || terminal.CommandId != Request.CommandId ||
            terminal.Fingerprint != Fingerprint || terminal.JournalIncarnation != Topology.JournalIncarnation ||
            terminal.WriterEpoch != Topology.WriterEpoch || !terminal.RejectedWithoutDecision,
            "fabric_abort_unproven", "Abort requires an authoritative terminal reservation with no decision.");
        return this with { Phase = CommitPhase.Aborted };
    }

    public string WritesHash() => Canonical.Hash(Request.Writes.OrderBy(w => w.Before.Key.Kind, StringComparer.Ordinal)
        .ThenBy(w => w.Before.Key.Id, StringComparer.Ordinal).ToArray());
    public static string HeadsHash(ImmutableArray<RevisionHead> heads) => Canonical.Hash(heads
        .OrderBy(h => h.Key.Kind, StringComparer.Ordinal).ThenBy(h => h.Key.Id, StringComparer.Ordinal).ToArray());

    private static bool SameHeads(ImmutableArray<RevisionHead> left, ImmutableArray<RevisionHead> right) =>
        HeadsHash(left) == HeadsHash(right);

    private static string ReadbacksHash(ImmutableArray<ArchiveReadback> readbacks) => Canonical.Hash(readbacks
        .OrderBy(r => r.ArchiveId, StringComparer.Ordinal).ThenBy(r => r.ObjectKey, StringComparer.Ordinal).ToArray());

    private void Require(CommitPhase phase, WriterFence fence)
    {
        RequireFence(fence);
        Guard.Against(Phase != phase, "fabric_phase", $"Expected {phase}; actual {Phase}.", 409);
    }

    private void RequireFence(WriterFence fence) =>
        Guard.Against(fence != Fence(), "fabric_writer_fenced",
            "Writer epoch, journal incarnation, identity or topology is stale; explicit recovery continuity is required.", 409);

    private void ValidateReadbacks(ImmutableArray<ProtectedObject> objects,
        ImmutableArray<ArchiveReadback> readbacks, DateTimeOffset notBefore)
    {
        Guard.Against(readbacks.IsDefault || readbacks.Length != checked(objects.Length * Topology.Archives.Length) ||
            readbacks.Select(r => (r.ArchiveId, r.ObjectKey)).Distinct().Count() != readbacks.Length,
            "fabric_archive_coverage", "Every protected object needs one independent readback from each archive.");
        foreach (var target in Topology.Archives)
            foreach (var expected in objects)
            {
                var proof = readbacks.SingleOrDefault(r => r.ArchiveId == target.Id && r.ObjectKey == expected.Key);
                Guard.Against(proof is null || proof.Hash != expected.Hash || proof.Length != expected.Length ||
                    !proof.Locked || proof.ProtectedAppend || proof.RetainThrough < expected.RetainThrough ||
                    proof.ReadAt < notBefore || proof.ReadAt >= proof.RetainThrough ||
                    string.IsNullOrWhiteSpace(proof.Version) || string.IsNullOrWhiteSpace(proof.PolicyETag),
                    "fabric_archive_readback", "Original bytes, version, length and locked non-append retention must match in both regions.");
            }
    }

    private static void ValidateTopology(ProtectionTopology topology, ProtectionInventory inventory)
    {
        Text(topology.Environment, "Environment");
        Text(topology.WriterId, "Writer");
        Text(topology.JournalIncarnation, "Journal incarnation");
        Text(topology.Certifier.PrincipalId, "Certifier");
        Text(topology.Certifier.KeyVersion, "Signing key version");
        Text(topology.Certifier.SubjectPublicKeyInfo, "Public signing key");
        foreach (var target in new[] { topology.Primary, topology.Secondary })
        {
            Text(target.Server, "Journal server");
            Text(target.Database, "Journal database");
            Text(target.Region, "Journal region");
        }
        Guard.Against(topology.WriterEpoch <= 0 || topology.Primary.Region == topology.Secondary.Region ||
            topology.Primary.Server == topology.Secondary.Server || topology.Certifier.PrincipalId == topology.WriterId ||
            topology.Archives.IsDefault || topology.Archives.Length != 2,
            "fabric_protection_topology", "Two protection regions, one writer and an independent certifier are mandatory.");
        foreach (var archive in topology.Archives)
        {
            Text(archive.Id, "Archive ID");
            Text(archive.Region, "Archive region");
            Text(archive.Subscription, "Archive subscription");
            Text(archive.Administration, "Archive administration");
            Guard.Against(archive.Administration == topology.WriterId || archive.Administration == topology.Certifier.PrincipalId,
                "fabric_separation_of_duties", "Archive administration must be independent of writer and certifier.");
        }
        Guard.Against(topology.Archives.Select(a => a.Id).Distinct().Count() != 2 ||
            topology.Archives.Select(a => a.Region).Distinct().Count() != 2 ||
            topology.Archives.Select(a => a.Subscription).Distinct().Count() != 2 ||
            topology.Archives.Select(a => a.Administration).Distinct().Count() != 2,
            "fabric_protection_topology", "Both archives require distinct regions, identities and administration boundaries.");
        Hash(inventory.InventoryHash);
        Guard.Against(inventory.RequiredHighWatermark < 0 || inventory.JournalHighWatermark < inventory.RequiredHighWatermark ||
            inventory.ArchiveHighWatermarks.Count != 2 ||
            topology.Archives.Any(a => !inventory.ArchiveHighWatermarks.TryGetValue(a.Id, out var high) ||
                high < inventory.RequiredHighWatermark),
            "fabric_history_unprotected", "A replacement topology must protect the complete acknowledged historical inventory before normal writes.");
    }

    private static void ValidateHeads(ImmutableArray<RevisionHead> heads)
    {
        Guard.Against(heads.IsDefaultOrEmpty || heads.Select(h => h.Key).Distinct().Count() != heads.Length,
            "fabric_snapshot", "A complete, unique revision vector is required.");
        foreach (var head in heads)
        {
            Text(head.Key.Kind, "Aggregate kind");
            Text(head.Key.Id, "Aggregate ID");
            Hash(head.Hash);
            Guard.Against(head.Revision < 0 || head.Revision == 0 && head.Hash != EmptyHash ||
                head.Revision > 0 && head.Hash == EmptyHash,
                "fabric_revision", "Absent and existing aggregate revisions must be explicit.");
        }
    }

    private void ValidateDigest(LedgerBlockProof block)
    {
        try
        {
            using var document = JsonDocument.Parse(block.DigestJson);
            var digest = document.RootElement;
            Guard.Against(digest.ValueKind != JsonValueKind.Object ||
                !digest.TryGetProperty("block_id", out var id) || !id.TryGetInt64(out var number) || number != block.BlockId ||
                !digest.TryGetProperty("database_name", out var database) || database.GetString() != Topology.Primary.Database ||
                !digest.TryGetProperty("hash", out var hash) || string.IsNullOrWhiteSpace(hash.GetString()) ||
                !digest.TryGetProperty("digest_time", out var time) || time.GetDateTimeOffset() != block.ClosedAt ||
                !digest.TryGetProperty("last_transaction_commit_time", out var committed) || committed.GetDateTimeOffset() > block.ClosedAt,
                "fabric_native_digest", "The complete native digest must identify the expected closed block and journal database.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new DomainException("fabric_native_digest", "Native ledger digest is invalid.", 422, error);
        }
    }

    private static void Hash(string? value) => Guard.Against(value is null || value.Length != 64 ||
        value.Any(c => !char.IsAsciiHexDigit(c)) || value != value.ToLowerInvariant(),
        "fabric_hash", "A lowercase SHA256 digest is required.");

    private static void Text(string value, string name) =>
        Guard.Against(string.IsNullOrWhiteSpace(value), "fabric_field", $"{name} is required.");

    private static void CanonicalJson(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            Guard.Against(parsed.RootElement.ValueKind != JsonValueKind.Object || Canonical.Serialize(parsed.RootElement) != json,
                "fabric_payload", "Replay state and effects must be complete canonical JSON objects.");
        }
        catch (JsonException error)
        {
            throw new DomainException("fabric_payload", "Replay state or effect JSON is invalid.", 422, error);
        }
    }
}
