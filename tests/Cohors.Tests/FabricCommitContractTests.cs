using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohors.Domain;
using Cohors.Domain.Fabric;

namespace Cohors.Tests;

// Synthetic in-process observations: no SQL, WORM, Fabric or regional availability evidence.
public sealed class FabricCommitContractTests
{
    [Theory]
    [InlineData(ProtectionClass.AcknowledgedBusiness)]
    [InlineData(ProtectionClass.Awareness)]
    [InlineData(ProtectionClass.RepeatableDraft)]
    public void EveryInterruptionBeforeExactActivationBlocksSuccessAndPendingReads(ProtectionClass protection)
    {
        using var sample = new SyntheticCommit();
        var state = sample.Start(sample.Request with { Protection = protection });
        while (!state.CanAcknowledge)
        {
            Assert.True(state.BlocksFollowingCommand);
            Assert.Same(state, state.ResolveRetry(state.Request));
            Assert.Equal(sample.Request.ObservedAt, state.Request.ObservedAt);
            Reject("fabric_phase", () => state.ReadActivatedSnapshot(state.ExpectedActivationSnapshot(), state.Fence()));
            state = sample.Advance(state);
        }
        Assert.False(state.BlocksFollowingCommand);
        Assert.Equal(2, state.ReadActivatedSnapshot(state.ExpectedActivationSnapshot(), state.Fence())[0].Revision);
        Assert.Equal(4, state.ExpectedActivationSnapshot()[2].Revision);
    }

    [Fact]
    public void CommandIdentityBindsAuditOriginalsRulesActorAndAllDependentRevisions()
    {
        using var sample = new SyntheticCommit();
        var state = sample.Start();
        var request = sample.Request;
        var changed = new[]
        {
            request with { Action = "DifferentAction" },
            request with { ActorId = "different-human" },
            request with { AuditDraftJson = """{"reason":"different"}""" },
            request with { AuthorizationSnapshotHash = Canonical.HashText("other-scope") },
            request with { RuleVersion = "rules-next" },
            request with { ObservedAt = request.ObservedAt.AddSeconds(1) },
            request with { Originals = [request.Originals[0] with { Hash = Canonical.HashText("different-bytes") }] },
            request with { ReadSet = request.ReadSet.SetItem(2, request.ReadSet[2] with { Revision = 5 }) },
            request with { Writes = request.Writes.SetItem(0, request.Writes[0] with { StateJson = """{"value":"changed"}""" }) },
            request with { Effects = [request.Effects[0] with { PayloadJson = """{"intent":"changed"}""" }] }
        };
        foreach (var alternative in changed)
            Reject("fabric_command_conflict", () => state.ResolveRetry(alternative));
    }

    [Theory]
    [InlineData("""{"value":{"x":1,"x":2}}""")]
    [InlineData("""{"x": 1}""")]
    [InlineData("null")]
    public void AmbiguousOrNoncanonicalReplayStatesAreRejected(string json)
    {
        using var sample = new SyntheticCommit();
        Reject("fabric_payload", () => sample.Start(sample.Request with
        {
            Writes = sample.Request.Writes.SetItem(0, sample.Request.Writes[0] with { StateJson = json })
        }));
    }

    [Fact]
    public void MultiaggregateReservationRequiresCompleteCurrentSnapshotAndNoPendingDependency()
    {
        using var sample = new SyntheticCommit();
        var request = sample.Request;
        foreach (var heads in new[]
        {
            request.ReadSet.RemoveAt(2),
            request.ReadSet.SetItem(2, request.ReadSet[2] with { Revision = 5 }),
            request.ReadSet.SetItem(1, request.ReadSet[1] with { Hash = Canonical.HashText("same-version-other-bytes") })
        })
            Reject("fabric_snapshot_conflict", () => sample.Start(snapshot: heads));
        Reject("fabric_snapshot", () => sample.Start(snapshot: request.ReadSet.Add(request.ReadSet[0])));
        var pending = new Reservation("preceding-command", Canonical.HashText("previous"), [request.ReadSet[2].Key], false);
        Reject("fabric_predecessor_pending", () => sample.Start(reservations: [pending]));
        Assert.Equal(CommitPhase.Reserved, sample.Start(reservations: [pending with { Activated = true }]).Phase);
        var same = pending with { CommandId = request.CommandId, Fingerprint = Canonical.Hash(request), Activated = true };
        Reject("fabric_resolve_receipt", () => sample.Start(reservations: [same]));
        Reject("fabric_command_conflict", () => sample.Start(reservations: [same with { Fingerprint = Canonical.HashText("other") }]));
        Reject("fabric_write_predecessor", () => sample.Start(request with
        {
            Writes = [request.Writes[0] with { Before = request.Writes[0].Before with { Revision = 9 } }]
        }));
    }

    [Theory]
    [InlineData("missing-region")]
    [InlineData("same-region")]
    [InlineData("same-subscription")]
    [InlineData("same-administrator")]
    [InlineData("writer-certifier")]
    [InlineData("same-journal-region")]
    public void OneRegionOrSharedProtectionAuthorityCannotConfirmNormalCommands(string fault)
    {
        using var sample = new SyntheticCommit();
        var topology = sample.Topology;
        topology = fault switch
        {
            "missing-region" => topology with { Archives = [topology.Archives[1]] },
            "same-region" => topology with { Archives = topology.Archives.SetItem(1, topology.Archives[1] with { Region = topology.Archives[0].Region }) },
            "same-subscription" => topology with { Archives = topology.Archives.SetItem(1, topology.Archives[1] with { Subscription = topology.Archives[0].Subscription }) },
            "same-administrator" => topology with { Archives = topology.Archives.SetItem(1, topology.Archives[1] with { Administration = topology.Archives[0].Administration }) },
            "writer-certifier" => topology with { Certifier = topology.Certifier with { PrincipalId = topology.WriterId } },
            _ => topology with { Secondary = topology.Secondary with { Region = topology.Primary.Region } }
        };
        Reject("fabric_protection_topology", () => sample.Start(topology: topology));
    }

    [Fact]
    public void AzureIdentityCaseAliasesCannotCreateIndependentProtectionDomains()
    {
        using var sample = new SyntheticCommit();
        var topology = sample.Topology;
        const string subscription = "11111111-2222-4333-8444-abcdefabcdef";
        foreach (var duplicate in new[]
        {
            topology with { Certifier = topology.Certifier with { PrincipalId = topology.WriterId.ToUpperInvariant() } },
            topology with { Secondary = topology.Secondary with { Server = topology.Primary.Server.ToUpperInvariant() } },
            topology with { Secondary = topology.Secondary with { Region = topology.Primary.Region.ToUpperInvariant() } },
            topology with { Archives = topology.Archives.SetItem(1, topology.Archives[1] with { Id = topology.Archives[0].Id.ToLowerInvariant() }) },
            topology with { Archives = topology.Archives.SetItem(1, topology.Archives[1] with { Region = topology.Archives[0].Region.ToUpperInvariant() }) },
            topology with { Archives = topology.Archives.SetItem(1, topology.Archives[1] with { Administration = topology.Archives[0].Administration.ToUpperInvariant() }) },
            topology with { Archives = [topology.Archives[0] with { Subscription = subscription },
                topology.Archives[1] with { Subscription = subscription.ToUpperInvariant() }] }
        })
            Reject("fabric_protection_topology", () => sample.Start(topology: duplicate));
        foreach (var administrator in new[] { topology.WriterId.ToUpperInvariant(), topology.Certifier.PrincipalId.ToUpperInvariant() })
            Reject("fabric_separation_of_duties", () => sample.Start(topology: topology with
            {
                Archives = topology.Archives.SetItem(0, topology.Archives[0] with { Administration = administrator })
            }));
    }

    [Fact]
    public void GuidFormattingAndDnsRootNotationDoNotCreateNewServiceIdentities()
    {
        using var sample = new SyntheticCommit();
        var id = Guid.Parse("11111111-2222-4333-8444-abcdefabcdef");
        var topology = sample.Topology;
        Reject("fabric_protection_topology", () => sample.Start(topology: topology with
        {
            WriterId = id.ToString("D"), Certifier = topology.Certifier with { PrincipalId = id.ToString("B").ToUpperInvariant() }
        }));
        Reject("fabric_protection_topology", () => sample.Start(topology: topology with
        {
            Archives = [topology.Archives[0] with { Subscription = id.ToString("D") },
                topology.Archives[1] with { Subscription = id.ToString("N") }]
        }));
        Reject("fabric_protection_topology", () => sample.Start(topology: topology with
        {
            Primary = topology.Primary with { Server = "journal.example.test" },
            Secondary = topology.Secondary with { Server = "JOURNAL.EXAMPLE.TEST." }
        }));
    }

    [Theory]
    [InlineData("region-a", "region-c")]
    [InlineData("region-c", "region-d")]
    public void ArchivesMustCoverExactlyTheTwoJournalProtectionRegions(string first, string second)
    {
        using var sample = new SyntheticCommit();
        Reject("fabric_protection_regions", () => sample.Start(topology: sample.Topology with
        {
            Archives = [sample.Topology.Archives[0] with { Region = first }, sample.Topology.Archives[1] with { Region = second }]
        }));
    }

    [Fact]
    public void ServiceRegionComparisonDoesNotNormalizeOriginalKeysOrCommandBytes()
    {
        using var sample = new SyntheticCommit();
        var request = sample.Request with
        {
            Originals = [sample.Request.Originals[0] with { Key = "Source.PDF" }, sample.Request.Originals[0] with { Key = "source.pdf" }]
        };
        var state = sample.Start(request, topology: sample.Topology with
        {
            Archives = [sample.Topology.Archives[0] with { Region = "REGION-B" }, sample.Topology.Archives[1] with { Region = "REGION-A" }]
        });
        Assert.Equal(CommitPhase.Reserved, state.Phase);
        Reject("fabric_command_conflict", () => state.ResolveRetry(request with
        {
            Originals = request.Originals.SetItem(0, request.Originals[0] with { Key = "SOURCE.PDF" })
        }));
    }

    [Fact]
    public void ReplacementProtectionMustCoverAllAcknowledgedHistoricalInventory()
    {
        using var sample = new SyntheticCommit();
        foreach (var incomplete in new[]
        {
            sample.Inventory with { JournalHighWatermark = 39 },
            sample.Inventory with { ArchiveHighWatermarks = sample.Inventory.ArchiveHighWatermarks.SetItem("B", 39) },
            sample.Inventory with { ArchiveHighWatermarks = sample.Inventory.ArchiveHighWatermarks.Remove("A") }
        })
            Reject("fabric_history_unprotected", () => sample.Start(inventory: incomplete));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("bytes")]
    [InlineData("length")]
    [InlineData("unlocked")]
    [InlineData("append")]
    [InlineData("retention")]
    [InlineData("version")]
    public void BothArchivesMustReadBackTheExactOriginalAndReplayBytesUnderLockedRetention(string fault)
    {
        using var sample = new SyntheticCommit();
        var state = sample.Start();
        var proofs = sample.Readbacks(state, state.RequiredInputs(), 2);
        proofs = fault switch
        {
            "missing" => proofs.RemoveAt(1),
            "duplicate" => proofs.SetItem(1, proofs[0]),
            "bytes" => proofs.SetItem(0, proofs[0] with { Hash = Canonical.HashText("corrupted") }),
            "length" => proofs.SetItem(0, proofs[0] with { Length = proofs[0].Length + 1 }),
            "unlocked" => proofs.SetItem(0, proofs[0] with { Locked = false }),
            "append" => proofs.SetItem(0, proofs[0] with { ProtectedAppend = true }),
            "retention" => proofs.SetItem(0, proofs[0] with { RetainThrough = state.Request.RetainThrough.AddDays(-1) }),
            _ => proofs.SetItem(0, proofs[0] with { Version = "" })
        };
        Reject(fault is "missing" or "duplicate" ? "fabric_archive_coverage" : "fabric_archive_readback",
            () => state.ProtectInputs(proofs, state.Fence()));
    }

    [Fact]
    public void EveryTransitionChecksWriterEpochIncarnationIdentityAndTopology()
    {
        using var sample = new SyntheticCommit();
        var state = sample.Start();
        while (!state.CanAcknowledge)
        {
            foreach (var stale in new[]
            {
                state.Fence() with { Epoch = state.Fence().Epoch - 1 },
                state.Fence() with { WriterId = "former-writer" },
                state.Fence() with { JournalIncarnation = "other-incarnation" },
                state.Fence() with { TopologyHash = Canonical.HashText("other-topology") }
            })
                Reject("fabric_writer_fenced", () => sample.Advance(state, stale));
            state = sample.Advance(state);
        }
    }

    [Fact]
    public void PendingOutboxAndJournalDecisionBindTheWholeMultiaggregateCommand()
    {
        using var sample = new SyntheticCommit();
        var state = sample.At(CommitPhase.InputsProtected);
        var pending = sample.Pending(state);
        foreach (var wrong in new[]
        {
            pending with { OutboxBlocked = false },
            pending with { WriteSetHash = Canonical.HashText("one-write-only") },
            pending with { EffectSetHash = Canonical.HashText("missing-intent") },
            pending with { EnvelopeHash = Canonical.HashText("other-envelope") }
        })
            Reject("fabric_pending_binding", () => state.Prepare(wrong, state.Fence()));
        state = state.Prepare(pending, state.Fence());
        var decision = sample.Decision(state);
        foreach (var wrong in new[]
        {
            decision with { PredecessorHash = Canonical.HashText("other-head") },
            decision with { Sequence = decision.Sequence + 1 },
            decision with { ReadSetHash = Canonical.HashText("one-dependency-only") },
            decision with { WriterEpoch = decision.WriterEpoch - 1 },
            decision with { PendingHash = Canonical.HashText("other-pending") }
        })
            Reject("fabric_decision_binding", () => state.Decide(wrong, state.Fence()));
    }

    [Fact]
    public void DigestMustCloseTheExactDecisionBeforeGeoBarrierAndSecondaryReadiness()
    {
        using var sample = new SyntheticCommit();
        var decided = sample.At(CommitPhase.Decided);
        var block = sample.Block(decided);
        Reject("fabric_block_binding", () => decided.CloseBlock(block with { DecisionTransactionId = 99 }, decided.Fence()));
        Reject("fabric_native_digest", () => decided.CloseBlock(block with { DigestJson = "{}" }, decided.Fence()));
        var closed = decided.CloseBlock(block, decided.Fence());
        var geo = sample.Geo(closed);
        Reject("fabric_phase", () => decided.ProtectGeo(geo, decided.Fence()));
        foreach (var wrong in new[]
        {
            geo with { ExecutedOnPrimary = false },
            geo with { LogHardened = false },
            geo with { ClosedBlockId = geo.ClosedBlockId - 1 },
            geo with { BarrierCompletedAt = block.ClosedAt.AddSeconds(-1) },
            geo with { SecondaryReadAt = geo.BarrierCompletedAt.AddSeconds(-1) },
            geo with { Secondary = geo.Primary },
            geo with { SecondaryDecisionHash = Canonical.HashText("old-replica") },
            geo with { SecondaryDigestHash = Canonical.HashText("old-block") }
        })
            Reject("fabric_geo_binding", () => closed.ProtectGeo(wrong, closed.Fence()));
    }

    [Fact]
    public void NativeSqlDigestShapePreservesBytesAndAcceptsOffsetlessUtcTimestamps()
    {
        using var sample = new SyntheticCommit();
        var state = sample.At(CommitPhase.Decided);
        var block = sample.Block(state) with
        {
            DigestJson = Canonical.Serialize(new
            {
                database_name = state.Topology.Primary.Database, block_id = 7, hash = "0x" + new string('A', 64),
                last_transaction_commit_time = SyntheticCommit.Time.AddSeconds(3).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                digest_time = SyntheticCommit.Time.AddSeconds(4).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
            })
        };
        Assert.Equal(block.DigestJson, state.CloseBlock(block, state.Fence()).Block!.DigestJson);
        Reject("fabric_native_digest", () => state.CloseBlock(
            block with { DigestJson = block.DigestJson.Replace(new string('A', 64), "malformed-hash", StringComparison.Ordinal) },
            state.Fence()));
    }

    [Fact]
    public void NativeDigestAndAllEarlierProofsMustBeArchivedBeforeCertificateIssuance()
    {
        using var sample = new SyntheticCommit();
        var state = sample.At(CommitPhase.GeoProtected);
        Reject("fabric_phase", () => state.CertificateScope(SyntheticCommit.Time.AddSeconds(8)));
        var incomplete = sample.Readbacks(state, [state.ProofBundleObject()], 7).RemoveAt(1);
        Reject("fabric_archive_coverage", () => state.ProtectProofs(incomplete, state.Fence()));
        var ready = sample.Advance(state);
        Reject("fabric_verification_time", () => ready.CertificateScope(SyntheticCommit.Time.AddSeconds(6)));
        using var bundle = JsonDocument.Parse(state.ProofBundleJson());
        Assert.Equal(state.Block!.DigestJson, bundle.RootElement.GetProperty("block").GetProperty("digestJson").GetString());
    }

    [Fact]
    public void IndependentSignatureMustBindExactScopeChecksAndVersionedKey()
    {
        using var sample = new SyntheticCommit();
        var state = sample.At(CommitPhase.ProofsProtected);
        var body = state.CertificateScope(SyntheticCommit.Time.AddSeconds(8));
        foreach (var wrong in new[]
        {
            body with { AuthorizationSnapshotHash = Canonical.HashText("unapproved-scope") },
            body with { VerifiedChecks = body.VerifiedChecks.Remove("authorization") },
            body with { KeyVersion = "untrusted-key-version" },
            body with { InputProofHash = Canonical.HashText("unprotected-inputs") }
        })
            Reject("fabric_certificate_scope", () => state.Certify(sample.Sign(wrong), state.Fence()));
        using var otherKey = RSA.Create(2048);
        var invalid = new CommitCertificate(body, Convert.ToBase64String(otherKey.SignData(
            Encoding.UTF8.GetBytes(Canonical.Serialize(body)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        Reject("fabric_certificate_signature", () => state.Certify(invalid, state.Fence()));
        Reject("fabric_certificate_signature", () => state.Certify(new(body, "not-base64"), state.Fence()));
        Assert.False(state.Certify(sample.Sign(body), state.Fence()).CanAcknowledge);
    }

    [Fact]
    public void CertificateNeedsItsOwnLaterDualReadbackAndExactActivation()
    {
        using var sample = new SyntheticCommit();
        var state = sample.At(CommitPhase.Certified);
        var certificateProofs = sample.Readbacks(state, [state.CertificateObject()], 9);
        Reject("fabric_archive_coverage", () => state.ProtectCertificate(certificateProofs.RemoveAt(0), state.Fence()));
        Reject("fabric_archive_readback", () => state.ProtectCertificate(
            certificateProofs.SetItem(0, certificateProofs[0] with { ReadAt = SyntheticCommit.Time.AddSeconds(7) }), state.Fence()));
        state = state.ProtectCertificate(certificateProofs, state.Fence());
        var activation = sample.Activation(state);
        foreach (var wrong in new[]
        {
            activation with { CertificateHash = Canonical.HashText("other-certificate") },
            activation with { CurrentSnapshot = state.Request.ReadSet },
            activation with { CurrentSnapshot = state.ExpectedActivationSnapshot().RemoveAt(1) },
            activation with { ReleasedEffects = [] },
            activation with { ActivatedAt = state.Request.RetainThrough },
            activation with { ActivatedAt = SyntheticCommit.Time.AddSeconds(8) }
        })
            Reject("fabric_activation_binding", () => state.Activate(wrong, state.Fence()));
        Assert.True(state.Activate(activation, state.Fence()).CanAcknowledge);
    }

    [Fact]
    public void TimeoutNeverAuthorizesBlindRollbackOrReactivationOfHistoricalHeads()
    {
        using var sample = new SyntheticCommit();
        var state = sample.At(CommitPhase.Prepared);
        var terminal = new TerminalReservation(state.Request.Environment, state.Request.CommandId, state.Fingerprint,
            state.Topology.JournalIncarnation, state.Topology.WriterEpoch, true);
        Reject("fabric_abort_unproven", () => state.Abort(terminal with { RejectedWithoutDecision = false }, state.Fence()));
        var rejected = state.Abort(terminal, state.Fence());
        Assert.False(rejected.CanAcknowledge);
        Assert.False(rejected.BlocksFollowingCommand);
        state = sample.Advance(state);
        while (true)
        {
            Reject("fabric_roll_forward", () => state.Abort(terminal, state.Fence()));
            Assert.Same(state, state.ResolveRetry(state.Request));
            if (state.CanAcknowledge) break;
            state = sample.Advance(state);
        }
        var laterHeads = state.ExpectedActivationSnapshot().SetItem(0,
            state.ExpectedActivationSnapshot()[0] with { Revision = 3, Hash = Canonical.HashText("later-certified-revision") });
        Reject("fabric_historical_receipt", () => state.ReadActivatedSnapshot(laterHeads, state.Fence()));
        Reject("fabric_phase", () => state.Activate(sample.Activation(state), state.Fence()));
    }

    private static void Reject(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<DomainException>(action).Code);

    private sealed class SyntheticCommit : IDisposable
    {
        public static readonly DateTimeOffset Time = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly RSA signer = RSA.Create(2048);
        private readonly byte[] original = Encoding.UTF8.GetBytes("SYNTHETIC original bytes, not patient data.");
        public ProtectionTopology Topology { get; }
        public ProtectionInventory Inventory { get; }
        public CommitRequest Request { get; }

        public SyntheticCommit()
        {
            Topology = new("synthetic-fabric", "synthetic-writer", 3, "synthetic-incarnation",
                new("journal-a", "journal", "region-a"), new("journal-b", "journal", "region-b"),
                [new("A", "region-a", "subscription-a", "admin-a"), new("B", "region-b", "subscription-b", "admin-b")],
                new("independent-certifier", "synthetic-key-version", Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo())));
            Inventory = new(40, 40, ImmutableDictionary<string, long>.Empty.Add("A", 40).Add("B", 40),
                Canonical.HashText("synthetic-complete-inventory"));
            ImmutableArray<RevisionHead> reads =
            [
                new(new("Case", "synthetic-case"), 1, Canonical.HashText("case-before")),
                new(new("Psur", "synthetic-psur"), 2, Canonical.HashText("psur-before")),
                new(new("Product", "synthetic-product"), 4, Canonical.HashText("product-read-only"))
            ];
            Request = new("synthetic-fabric", "synthetic-command", "SyntheticUpdate", "synthetic-human",
                Canonical.HashText("synthetic-authorization-snapshot"), "synthetic-rules-v1", "synthetic-schema-v1",
                "cohors-canonical-json-v1", """{"actor":"synthetic-human","reason":"synthetic-test"}""",
                ProtectionClass.AcknowledgedBusiness, Time, Time.AddYears(15), reads,
                [new(reads[0], """{"id":"synthetic-case","revision":2}"""), new(reads[1], """{"id":"synthetic-psur","revision":3}""")],
                [new("original-synthetic", Convert.ToHexStringLower(SHA256.HashData(original)), original.Length, Time.AddYears(15))],
                [new("synthetic-effect", "SyntheticIntent", """{"action":"do-not-send","synthetic":true}""")]);
        }

        public FabricCommit Start(CommitRequest? request = null, ProtectionTopology? topology = null,
            ProtectionInventory? inventory = null, ImmutableArray<RevisionHead> snapshot = default,
            ImmutableArray<Reservation> reservations = default) =>
            FabricCommit.Reserve(request ?? Request, topology ?? Topology, inventory ?? Inventory,
                new(40, Canonical.HashText("synthetic-predecessor")),
                snapshot.IsDefault ? Request.ReadSet : snapshot, reservations.IsDefault ? [] : reservations);

        public FabricCommit At(CommitPhase phase)
        {
            var state = Start();
            while (state.Phase != phase) state = Advance(state);
            return state;
        }

        public PendingRevisionReceipt Pending(FabricCommit state) => new(state.Request.CommandId, state.Fingerprint,
            FabricCommit.HeadsHash(state.Request.ReadSet), state.WritesHash(), Canonical.Hash(state.Request.Effects), true);

        public JournalDecision Decision(FabricCommit state) => new(state.Request.Environment, state.Request.CommandId,
            state.Fingerprint, state.Topology.JournalIncarnation, state.Topology.WriterEpoch, 11,
            state.Predecessor.Sequence + 1, state.Predecessor.Hash, FabricCommit.HeadsHash(state.Request.ReadSet),
            state.WritesHash(), state.PendingHash);

        public LedgerBlockProof Block(FabricCommit state) => new(state.Topology.JournalIncarnation,
            state.Decision!.TransactionId, Canonical.Hash(state.Decision), 7,
            Canonical.Serialize(new
            {
                database_name = state.Topology.Primary.Database, block_id = 7, hash = "0x" + new string('a', 64),
                last_transaction_commit_time = Time.AddSeconds(3), digest_time = Time.AddSeconds(4)
            }), Time.AddSeconds(4));

        public GeoBarrierProof Geo(FabricCommit state) => new(state.Topology.JournalIncarnation, state.Topology.WriterEpoch,
            state.Topology.Primary, state.Topology.Secondary, state.Block!.BlockId, state.Block.DigestHash(),
            true, true, Canonical.Hash(state.Decision), state.Block.DigestHash(), Time.AddSeconds(5), Time.AddSeconds(6));

        public CommitCertificate Sign(CertificateBody body) => new(body, Convert.ToBase64String(signer.SignData(
            Encoding.UTF8.GetBytes(Canonical.Serialize(body)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));

        public ActivationReceipt Activation(FabricCommit state) => new(state.Request.CommandId, state.Fingerprint,
            state.CertificateObject().Hash, state.ExpectedActivationSnapshot(),
            state.Request.Effects.Select(e => e.Id).ToImmutableArray(), Time.AddSeconds(10));

        public ImmutableArray<ArchiveReadback> Readbacks(FabricCommit state, ImmutableArray<ProtectedObject> objects, int seconds)
        {
            var result = ImmutableArray.CreateBuilder<ArchiveReadback>();
            foreach (var archive in state.Topology.Archives)
                foreach (var item in objects)
                {
                    var bytes = item.Key == "original-synthetic" ? original : Encoding.UTF8.GetBytes(
                        item.Key.StartsWith("replay-", StringComparison.Ordinal) ? state.EnvelopeJson() :
                        item.Key.StartsWith("proofs-", StringComparison.Ordinal) ? state.ProofBundleJson() :
                        Canonical.Serialize(state.Certificate));
                    result.Add(ArchiveReadback.FromBytes(archive.Id, item.Key, "synthetic-object-version", bytes,
                        "synthetic-locked-policy", true, false, item.RetainThrough, Time.AddSeconds(seconds)));
                }
            return result.ToImmutable();
        }

        public FabricCommit Advance(FabricCommit state, WriterFence? fence = null)
        {
            fence ??= state.Fence();
            return state.Phase switch
            {
                CommitPhase.Reserved => state.ProtectInputs(Readbacks(state, state.RequiredInputs(), 2), fence),
                CommitPhase.InputsProtected => state.Prepare(Pending(state), fence),
                CommitPhase.Prepared => state.Decide(Decision(state), fence),
                CommitPhase.Decided => state.CloseBlock(Block(state), fence),
                CommitPhase.BlockClosed => state.ProtectGeo(Geo(state), fence),
                CommitPhase.GeoProtected => state.ProtectProofs(Readbacks(state, [state.ProofBundleObject()], 7), fence),
                CommitPhase.ProofsProtected => state.Certify(Sign(state.CertificateScope(Time.AddSeconds(8))), fence),
                CommitPhase.Certified => state.ProtectCertificate(Readbacks(state, [state.CertificateObject()], 9), fence),
                CommitPhase.CertificateProtected => state.Activate(Activation(state), fence),
                _ => throw new InvalidOperationException("Synthetic command is terminal.")
            };
        }

        public void Dispose() => signer.Dispose();
    }
}
