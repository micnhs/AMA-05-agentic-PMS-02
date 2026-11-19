using System.Text;
using System.Text.Json;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class SignalDocumentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-19T15:00:00Z");
    private static readonly Actor Pms = new("document-pms", "Synthetic PMS", [Roles.Pms], ["ortho", "cardio"], true);
    private static readonly Actor Medical = new("document-medical", "Synthetic Medical", [Roles.Medical], ["ortho", "cardio"], true);
    private static readonly Actor Ra = new("document-ra", "Synthetic RA", [Roles.Regulatory], ["ortho", "cardio"], true);
    private const string Reason = "SYNTHETISCH: Vergleichsquelle und Grenzen ausdruecklich pruefen; keine Vorfallidentitaet, Kausalitaetsfreigabe oder Fristaenderung.";
    private const string Text = "SYNTHETISCH: Ein Hinweis ist nicht bestaetigt.\r\nKeine direkte Kausalitaet; fehlende Evidenz und \u00e4ltere Kenntnis gesondert pruefen.\r\n";

    internal static ImportSignalDocumentCommand Input(string key = "SYNTHETIC-DOCUMENT", bool bom = true) =>
        new("ortho", "synthetic-signal.txt", "text/plain",
            Convert.ToBase64String(Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + Text)),
            key, "Synthetisches Signaldokument", "Synthetic", null, Reason, Reason,
            "Known", Now.AddDays(-4), Reason, true, true, true, null, null, null, Reason);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_bytes_and_text_survive_import_download_and_review_without_invented_case_or_analysis(bool bom)
    {
        var fixture = new Fixture();
        var input = Input(bom: bom);
        var bytes = Convert.FromBase64String(input.ContentBase64);
        var result = await fixture.Service.ImportDocument(input, Pms, "import-original");
        var document = Assert.IsType<ImportedSignalDocument>(result.Source.Document);
        Assert.False(result.Reused);
        Assert.Equal(Text, Assert.Single(result.Source.Items).Summary);
        Assert.Equal(SignalDocumentRules.Hash(bytes), document.Original.Hash);
        Assert.Equal(Canonical.HashText(Text), document.Text.Hash);
        Assert.Equal(bom, document.Original.Hash != document.Text.Hash);
        Assert.Equal(bytes, (await fixture.Service.ReadDocument(result.Source.Id, 1, Medical)).Bytes);
        Assert.Equal(Now, result.Source.At);
        Assert.Equal(input.FirstKnownAt, document.FirstKnownAt);
        var signal = Assert.Single(await fixture.Signals.List("ReviewRequired", Medical));
        Assert.Equal(document, signal.Document);
        Assert.Empty(signal.History);
        Assert.Equal("ReviewRequired", signal.Status);
        Assert.Null(signal.CaseId);
        Assert.Null(signal.CaseReference);
        Assert.Equal(input.FirstKnownAt, signal.AwarenessAt);
        Assert.Empty(await fixture.Store.List<SafetyCase>("case", Pms.Products));
        Assert.Empty(await fixture.Store.List<AgentInvocation>("agentInvocation", Pms.Products));
        var write = Assert.Single(fixture.Store.Writes);
        Assert.Equal(("source", "SignalDocumentImported"), (write.Kind, write.Action));
        var request = Canonical.Serialize(write.Request);
        Assert.DoesNotContain("contentBase64", request);
        Assert.Contains(document.Original.Hash, request);
        Assert.Contains(document.Text.Hash, request);
        var references = RecoveryJson.ReferenceNodes(Canonical.Serialize(result.Source)).Select(Canonical.Read<JsonElement>).ToArray();
        Assert.Contains(references, node => node.GetProperty("hash").GetString() == document.Original.Hash);
        Assert.Contains(references, node => node.GetProperty("hash").GetString() == document.Text.Hash);
        Assert.All(references, node => Assert.False(node.TryGetProperty("objectKey", out _)));
        Assert.Equal(0, fixture.Reader.Calls);
    }

    [Fact]
    public async Task Unestablished_knowledge_remains_null_instead_of_using_file_name_or_upload_time()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.ImportDocument(Input() with
        { Name = "2001-01-01-synthetic-signal.txt", KnowledgeStatus = "NotEstablished", FirstKnownAt = null }, Pms, "unknown-knowledge");
        Assert.Null(result.Source.Document!.FirstKnownAt);
        Assert.Null(Assert.Single(await fixture.Signals.List("ReviewRequired", Medical)).AwarenessAt);
        Assert.Equal(Now, result.Source.At);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("bytes")]
    [InlineData("knowledge")]
    [InlineData("rights")]
    public async Task Retry_reuses_original_receipt_but_changed_bound_input_is_rejected(string field)
    {
        var fixture = new Fixture();
        var input = Input();
        var first = await fixture.Service.ImportDocument(input, Pms, "stable-document-key");
        var second = await fixture.Service.ImportDocument(input, Pms, "stable-document-key");
        Assert.True(second.Reused);
        Assert.Equal(Canonical.Hash(first.Source), Canonical.Hash(second.Source));
        Assert.Single(fixture.Store.Writes);
        Assert.Equal(2, fixture.Objects.Writes);
        var changed = field switch
        {
            "title" => input with { Title = "Anderer synthetischer Titel" },
            "bytes" => input with { ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Text + "Anderer Wortlaut.")) },
            "knowledge" => input with { FirstKnownAt = input.FirstKnownAt!.Value.AddHours(-1) },
            _ => input with { Rights = Reason + " Abweichender Nutzungsumfang." }
        };
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(changed, Pms, "stable-document-key"));
        Assert.Equal(("idempotency_conflict", 409), (error.Code, error.Status));
        Assert.Single(fixture.Store.Writes);
        Assert.Equal(2, fixture.Objects.Writes);
    }

    [Theory]
    [InlineData("pdf", "signal_document_type", 415)]
    [InlineData("path", "signal_document_type", 415)]
    [InlineData("mime", "signal_document_type", 415)]
    [InlineData("base64", "signal_document_encoding", 422)]
    [InlineData("utf8", "signal_document_encoding", 422)]
    [InlineData("binary", "signal_document_text", 422)]
    [InlineData("renamed-pdf", "signal_document_type", 415)]
    [InlineData("renamed-office", "signal_document_type", 415)]
    [InlineData("empty", "signal_document_size", 413)]
    [InlineData("oversize", "signal_document_size", 413)]
    [InlineData("too-many-characters", "signal_document_text", 422)]
    public async Task Unsupported_or_unreadable_files_never_create_placeholder_source_or_objects(string fault, string code, int status)
    {
        var fixture = new Fixture();
        var input = Input();
        input = fault switch
        {
            "pdf" => input with { Name = "signal.pdf" },
            "path" => input with { Name = @"..\signal.txt" },
            "mime" => input with { MediaType = "application/octet-stream" },
            "base64" => input with { ContentBase64 = "%%%" },
            "utf8" => input with { ContentBase64 = Convert.ToBase64String([0xff, 0xfe, 0x41, 0x42]) },
            "binary" => input with { ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Text + "\0")) },
            "renamed-pdf" => input with { ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.7\n" + Text)) },
            "renamed-office" => input with { ContentBase64 = Convert.ToBase64String([0x50, 0x4b, 3, 4, .. Encoding.UTF8.GetBytes(Text)]) },
            "empty" => input with { ContentBase64 = "" },
            "oversize" => input with { ContentBase64 = Convert.ToBase64String(new byte[SignalDocumentRules.MaximumBytes + 1]) },
            _ => input with { ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 30001))) }
        };
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(input, Pms, "invalid-document"));
        Assert.Equal((code, status), (error.Code, error.Status));
        Assert.Empty(fixture.Store.Writes);
        Assert.Equal(0, fixture.Objects.Writes);
        Assert.Equal(0, fixture.Reader.Calls);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("rights")]
    [InlineData("eligibility")]
    public async Task Every_human_intake_confirmation_is_required(string missing)
    {
        var fixture = new Fixture();
        var input = Input() with { MetadataConfirmed = missing != "metadata", RightsConfirmed = missing != "rights",
            ContentEligibilityConfirmed = missing != "eligibility" };
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(input, Pms, "missing-confirmation"));
        Assert.Equal("signal_document_confirmation", error.Code);
        Assert.Empty(fixture.Store.Writes);
        Assert.Equal(0, fixture.Objects.Writes);
    }

    [Theory]
    [InlineData("known-without-date")]
    [InlineData("unknown-with-date")]
    [InlineData("future-date")]
    [InlineData("unrecognized")]
    public async Task Knowledge_is_explicit_and_never_derived_from_upload(string fault)
    {
        var fixture = new Fixture();
        var input = fault switch
        {
            "known-without-date" => Input() with { FirstKnownAt = null },
            "unknown-with-date" => Input() with { KnowledgeStatus = "NotEstablished" },
            "future-date" => Input() with { FirstKnownAt = Now.AddMinutes(1) },
            _ => Input() with { KnowledgeStatus = "UseUploadDate" }
        };
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(input, Pms, "invalid-knowledge"));
        Assert.Equal("signal_document_knowledge", error.Code);
        Assert.Empty(fixture.Store.Writes);
        Assert.Equal(0, fixture.Objects.Writes);
    }

    [Theory]
    [InlineData("role", "forbidden")]
    [InlineData("machine", "human_required")]
    [InlineData("product", "product_scope")]
    public async Task Intake_checks_actual_human_role_and_product_scope(string fault, string code)
    {
        var fixture = new Fixture();
        var actor = fault switch { "role" => Medical, "machine" => Pms with { IsHuman = false }, _ => Pms with { Products = ["cardio"] } };
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(Input(), actor, "unauthorized-import"));
        Assert.Equal((code, 403), (error.Code, error.Status));
        Assert.Empty(fixture.Store.Writes);
        Assert.Equal(0, fixture.Objects.Writes);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("synthetic-origin")]
    [InlineData("public-http")]
    [InlineData("public-credentials")]
    [InlineData("public-query")]
    public async Task Undeclared_or_unsupported_origin_is_not_relabelled_as_admissible_content(string fault)
    {
        var fixture = new Fixture();
        var input = fault switch
        {
            "scope" => Input() with { Scope = "PatientRecord" },
            "synthetic-origin" => Input() with { Provenance = Reason.Replace("SYNTHETISCH:", "Ungeklaerte Herkunft:", StringComparison.Ordinal) },
            "public-http" => Input() with { Scope = "PublicAuthorized", OriginUri = "http://example.test/document.txt" },
            "public-credentials" => Input() with { Scope = "PublicAuthorized", OriginUri = "https://user:password@example.test/document.txt" },
            _ => Input() with { Scope = "PublicAuthorized", OriginUri = "https://example.test/document.txt?token=synthetic" }
        };
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(input, Pms, "origin-boundary"));
        Assert.Equal(fault == "scope" ? "signal_document_scope" : "signal_document_origin", error.Code);
        Assert.Empty(fixture.Store.Writes);
        Assert.Equal(0, fixture.Objects.Writes);
    }

    [Fact]
    public async Task Duplicate_hints_remain_visible_after_reload_and_never_merge_events_or_cross_product_scope()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.ImportDocument(Input(), Pms, "first-original");
        var same = await fixture.Service.ImportDocument(Input("ANOTHER-REFERENCE"), Pms, "same-bytes-new-intake");
        Assert.Equal("IdenticalBytes", Assert.Single(same.Matches).Match);
        var changed = await fixture.Service.ImportDocument(Input() with
        { ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Text + "Neue unabhaengige Beobachtung.")) }, Pms, "changed-same-reference");
        Assert.Equal("SameReferenceDifferentBytes", Assert.Single(changed.Matches).Match);
        var otherProduct = await fixture.Service.ImportDocument(Input() with { ProductId = "cardio" }, Pms, "other-product-original");
        Assert.Empty(otherProduct.Matches);
        var reloaded = await fixture.Signals.List("ReviewRequired", Medical with { Products = ["ortho"] });
        Assert.Equal(3, reloaded.Length);
        var original = Assert.Single(reloaded, value => value.OwnerId == first.Source.Id);
        Assert.Equal(2, original.DocumentMatches!.Length);
        Assert.All(reloaded, value => { Assert.Null(value.CaseId); Assert.Empty(value.History); });
        Assert.Empty(await fixture.Store.List<SafetyCase>("case", Pms.Products));
        Assert.Equal(4, fixture.Store.Writes.Count);
        Assert.Equal(0, fixture.Reader.Calls);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(2048)]
    public async Task Larger_no_match_requests_prepare_each_source_once_and_keep_immutable_lookup_data(int count)
    {
        var fixture = new Fixture();
        var runs = Enumerable.Range(0, count).Select(i => SyntheticSource($"source-{i:D6}",
            i % 2 == 0 ? "ortho" : "cardio", $"SYNTHETIC-REF-{i:D6}",
            $"SYNTHETISCH: Unabhaengige Beobachtung {i:D6}. " + new string('x', 4096), 1 + i % 7)).ToArray();
        var references = runs.Select(run => new VersionReference(run.Id, run.Version, Canonical.Hash(run))).ToArray();
        foreach (var run in runs) fixture.Store.Seed("source", run.Id, run.ProductId, run);
        var views = await fixture.Signals.List("ReviewRequired", Medical);
        Assert.Equal(count, views.Length);
        Assert.All(views, view => Assert.Empty(Assert.IsType<SignalDocumentMatch[]>(view.DocumentMatches)));

        var enumerations = 0;
        var visited = 0;
        IEnumerable<PublicSourceRun> Once()
        {
            Assert.Equal(1, ++enumerations);
            foreach (var run in runs) { visited++; yield return run; }
        }
        var lookup = new SignalDocumentLookup(Once());
        Assert.Equal(count, visited);
        foreach (var run in runs)
            run.Items[0] = run.Items[0] with { Summary = "Changed after preparation.", ExternalId = "COLLISION", Title = "Changed title" };
        for (var i = 0; i < runs.Length; i++)
        {
            var prepared = Assert.IsType<SignalDocumentLookup.Source>(lookup.Get(runs[i]));
            Assert.Equal(references[i], prepared.Reference);
            Assert.Equal($"SYNTHETIC-REF-{i:D6}", prepared.ExternalId);
            Assert.Equal("Synthetische Quelle " + runs[i].Id, prepared.Title);
            Assert.Empty(lookup.Matches(prepared));
        }
        Assert.Equal(1, enumerations);
        Assert.Equal(count, visited);
        var invalid = Assert.Throws<DomainException>(() => new SignalDocumentLookup(runs));
        Assert.Equal(("signal_document_integrity", 409), (invalid.Code, invalid.Status));
        Assert.Empty(fixture.Store.Writes);
        Assert.Equal(0, fixture.Reader.Calls);
    }

    [Fact]
    public void Dense_matches_are_not_truncated_merged_or_exposed_across_products()
    {
        const int countPerProduct = 128;
        var runs = new[] { "ortho", "cardio" }.SelectMany(product =>
            Enumerable.Range(0, countPerProduct).Select(i => SyntheticSource($"{product}-{i:D3}", product,
                "SYNTHETIC-SHARED-REFERENCE", Text, 1 + i % 4))).ToArray();
        var expected = runs.ToDictionary(run => run.Id,
            run => (run.ProductId, Reference: new VersionReference(run.Id, run.Version, Canonical.Hash(run))));
        var lookup = new SignalDocumentLookup(runs.Reverse());
        var total = 0;
        foreach (var run in runs)
        {
            var matches = lookup.Matches(Assert.IsType<SignalDocumentLookup.Source>(lookup.Get(run)));
            total += matches.Length;
            Assert.Equal(countPerProduct - 1, matches.Length);
            Assert.Equal(matches.Select(match => match.Source.Id).Order(StringComparer.Ordinal),
                matches.Select(match => match.Source.Id));
            Assert.Equal(matches.Length, matches.Select(match => match.Source.Id).Distinct().Count());
            Assert.All(matches, match =>
            {
                Assert.NotEqual(run.Id, match.Source.Id);
                Assert.Equal(run.ProductId, expected[match.Source.Id].ProductId);
                Assert.Equal(expected[match.Source.Id].Reference, match.Source);
                Assert.Equal("IdenticalBytes", match.Match);
            });
        }
        Assert.Equal(2 * countPerProduct * (countPerProduct - 1), total);
    }

    [Fact]
    public void Lookup_preserves_exact_versions_ordinal_reference_matching_and_byte_precedence()
    {
        var selected = SyntheticSource("selected", "ortho", "SYNTHETIC-REFERENCE", Text, 2);
        var sameBytes = SyntheticSource("B", "ortho", "ANOTHER-REFERENCE", Text, 9);
        var sameReference = SyntheticSource("a", "ortho", "SYNTHETIC-REFERENCE", Text + "Anderer Vorfall.", 5);
        var both = SyntheticSource("z", "ortho", "SYNTHETIC-REFERENCE", Text, 3);
        var caseSensitive = SyntheticSource("different-case", "ortho", "synthetic-reference", Text + "Weitere Beobachtung.");
        var foreign = SyntheticSource("foreign", "cardio", "SYNTHETIC-REFERENCE", Text);
        var metadata = SyntheticSource("metadata", "ortho", "SYNTHETIC-REFERENCE", Text) with
        { Source = "pubmed", Status = "ReadOnlyMetadata", Document = null };
        var lookup = SignalDocumentLookup.ForSource(selected,
            [both, foreign, selected with { Version = 12 }, sameReference, metadata, sameBytes, caseSensitive]);
        var matches = lookup.Matches(Assert.IsType<SignalDocumentLookup.Source>(lookup.Get(selected)));
        Assert.Equal(new[] { "B", "a", "z" }, matches.Select(match => match.Source.Id));
        Assert.Equal(new[] { "IdenticalBytes", "SameReferenceDifferentBytes", "IdenticalBytes" },
            matches.Select(match => match.Match));
        Assert.Equal(new[] { sameBytes, sameReference, both }.Select(run => new SignalDocumentMatch(
            new(run.Id, run.Version, Canonical.Hash(run)), run.Items[0].Title,
            run.Document!.Original.Hash == selected.Document!.Original.Hash ? "IdenticalBytes" : "SameReferenceDifferentBytes",
            run.Document.Original.Hash)), matches);
        Assert.Null(lookup.Get(selected with { Version = 12 }));
        Assert.Null(lookup.Get(foreign));
        Assert.Null(lookup.Get(metadata));
    }

    [Fact]
    public async Task Status_filtered_views_keep_matching_sources_from_other_review_states()
    {
        var fixture = new Fixture();
        var pending = (await fixture.Service.ImportDocument(Input(), Pms, "pending-original")).Source;
        var rejected = (await fixture.Service.ImportDocument(Input(), Pms, "rejected-original")).Source;
        var relevant = (await fixture.Service.ImportDocument(Input() with
        { ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Text + "Abweichende Beobachtung.")) }, Pms, "relevant-original")).Source;
        await fixture.Signals.Decide("source", rejected.Id, "Reject", new(1, Input().ExternalId, Reason), Ra, "reject-original");
        await fixture.Signals.Decide("source", relevant.Id, "ConfirmRelevance", new(1, Input().ExternalId, Reason), Medical, "confirm-original");
        var all = await fixture.Signals.List("All", Medical);
        Assert.Equal(3, all.Length);
        Assert.Equal(all.Select(view => view.OwnerId).Order(StringComparer.Ordinal), all.Select(view => view.OwnerId));
        foreach (var filter in new[] { "Rejected", "ReviewRequired" })
        {
            var view = Assert.Single(await fixture.Signals.List(filter, Medical));
            Assert.Equal(filter == "Rejected" ? rejected.Id : pending.Id, view.OwnerId);
            Assert.Equal(Canonical.Hash(Assert.Single(all, value => value.OwnerId == view.OwnerId)), Canonical.Hash(view));
            Assert.Equal(2, view.DocumentMatches!.Length);
            Assert.Contains(view.DocumentMatches, match => match.Source.Id == relevant.Id && match.Source.Version == 2);
            foreach (var match in view.DocumentMatches)
                Assert.Equal(Canonical.Hash(await fixture.Store.Get<PublicSourceRun>("source", match.Source.Id)), match.Source.Hash);
        }
        Assert.Empty(await fixture.Store.List<SafetyCase>("case", Pms.Products));
    }

    [Theory]
    [InlineData("text", "signal_document_integrity", 409)]
    [InlineData("missing-document", "signal_document_missing", 404)]
    [InlineData("source", "signal_document_integrity", 409)]
    [InlineData("status", "signal_document_integrity", 409)]
    public async Task Hidden_invalid_documents_still_fail_closed_before_status_filtered_projection(string fault, string code, int status)
    {
        var fixture = new Fixture();
        var rejected = SyntheticSource("visible-rejected", "ortho", "SYNTHETIC-VISIBLE", Text);
        rejected = rejected with { SignalReviews = SignalReviewRules.Decide(rejected.SignalReviews, "source", rejected.Id,
            rejected.Items[0].ExternalId, rejected.Items[0].ProvenanceHash,
            new("Reject", "Reviewer", Reason, Now, Medical.Id, Medical.Name, Roles.Medical, 1, rejected.Items[0].ProvenanceHash)) };
        var hidden = SyntheticSource("hidden-pending", "ortho", "SYNTHETIC-HIDDEN", Text + "Unabhaengiger Hinweis.");
        hidden = fault switch
        {
            "text" => hidden with { Items = [hidden.Items[0] with { Summary = Text }] },
            "missing-document" => hidden with { Document = null },
            "source" => hidden with { Source = "pubmed" },
            _ => hidden with { Status = "ReadOnlyMetadata" }
        };
        fixture.Store.Seed("source", rejected.Id, rejected.ProductId, rejected);
        fixture.Store.Seed("source", hidden.Id, hidden.ProductId, hidden);
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Signals.List("Rejected", Medical));
        Assert.Equal((code, status), (error.Code, error.Status));
        Assert.Empty(fixture.Store.Writes);
    }

    [Fact]
    public async Task Historical_import_and_review_receipts_keep_their_own_version_and_current_other_matches()
    {
        var fixture = new Fixture();
        var first = (await fixture.Service.ImportDocument(Input(), Pms, "historic-import")).Source;
        var other = (await fixture.Service.ImportDocument(Input("ANOTHER-REFERENCE"), Pms, "other-import")).Source;
        var decision = new SignalDecisionCommand(1, Input().ExternalId, Reason);
        var confirmed = await fixture.Signals.Decide("source", first.Id, "ConfirmRelevance", decision, Medical, "historic-decision");
        await fixture.Signals.Decide("source", first.Id, "Reject", new(2, Input().ExternalId, Reason), Ra, "later-rejection");
        await fixture.Signals.Decide("source", other.Id, "ConfirmRelevance", new(1, "ANOTHER-REFERENCE", Reason), Medical, "other-confirmation");
        var currentOther = (await fixture.Store.Get<PublicSourceRun>("source", other.Id))!;
        var count = fixture.Store.Writes.Count;
        var replay = await fixture.Signals.Decide("source", first.Id, "ConfirmRelevance", decision, Medical, "historic-decision");
        Assert.Equal(confirmed.OwnerVersion, replay.OwnerVersion);
        Assert.Equal(confirmed.Status, replay.Status);
        Assert.Equal(confirmed.History, replay.History);
        var match = Assert.Single(replay.DocumentMatches!);
        Assert.Equal(new VersionReference(other.Id, 2, Canonical.Hash(currentOther)), match.Source);
        var repeatedImport = await fixture.Service.ImportDocument(Input(), Pms, "historic-import");
        Assert.True(repeatedImport.Reused);
        Assert.Equal(Canonical.Hash(first), Canonical.Hash(repeatedImport.Source));
        Assert.Equal(match, Assert.Single(repeatedImport.Matches));
        Assert.Equal(count, fixture.Store.Writes.Count);
    }

    [Fact]
    public async Task Metadata_only_review_does_not_acquire_an_unrelated_document_lookup_dependency()
    {
        var fixture = new Fixture();
        var source = SyntheticSource("public-metadata", "ortho", "SYNTHETIC-METADATA", Text) with
        { Source = "pubmed", Status = "ReadOnlyMetadata", Document = null };
        var invalidDocument = SyntheticSource("invalid-document", "ortho", "SYNTHETIC-OTHER", Text) with { Document = null };
        fixture.Store.Seed("source", source.Id, source.ProductId, source);
        fixture.Store.Seed("source", invalidDocument.Id, invalidDocument.ProductId, invalidDocument);
        var input = new SignalDecisionCommand(1, source.Items[0].ExternalId, Reason);
        var reviewed = await fixture.Signals.Decide("source", source.Id, "Reject", input, Medical, "metadata-review");
        Assert.Equal("Rejected", reviewed.Status);
        Assert.Null(reviewed.Document);
        Assert.Null(reviewed.DocumentMatches);
        var replay = await fixture.Signals.Decide("source", source.Id, "Reject", input, Medical, "metadata-review");
        Assert.Equal(Canonical.Hash(reviewed), Canonical.Hash(replay));
        Assert.Single(fixture.Store.Writes);
    }

    [Fact]
    public async Task Comparison_is_version_bound_metadata_not_a_case_write_clock_reset_or_automatic_link()
    {
        var fixture = new Fixture();
        var value = Comparison();
        fixture.Store.Seed("case", value.Id, value.ProductId, value);
        var input = Input() with { ComparisonCaseId = value.Id, ComparisonCaseVersion = value.Version, ComparisonReason = Reason };
        var saved = await fixture.Service.ImportDocument(input, Pms, "comparison-intake");
        Assert.Equal(new VersionReference(value.Id, value.Version, Canonical.Hash(value)), saved.Source.Document!.ComparisonCase!.Source);
        Assert.Equal(Canonical.Hash(value), Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", value.Id)));
        Assert.Equal("source", Assert.Single(fixture.Store.Writes).Kind);
        var view = Assert.Single(await fixture.Signals.List("ReviewRequired", Medical));
        Assert.Null(view.CaseId);
        Assert.Equal(value.Reference, view.Document!.ComparisonCase!.Reference);
        fixture.Store.Seed("case", value.Id, value.ProductId, value with { Version = 2 });
        var replay = await fixture.Service.ImportDocument(input, Pms, "comparison-intake");
        Assert.True(replay.Reused);
        var stale = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(input, Pms, "stale-comparison"));
        Assert.Equal(("dependency_version", 409), (stale.Code, stale.Status));
        Assert.Single(fixture.Store.Writes);
        var foreign = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(input with { ProductId = "cardio" }, Pms, "foreign-comparison"));
        Assert.Equal("signal_document_comparison", foreign.Code);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("text")]
    [InlineData("scope")]
    public async Task Original_download_verifies_actual_content_and_authorized_scope(string fault)
    {
        var fixture = new Fixture();
        var saved = await fixture.Service.ImportDocument(Input(), Pms, "bound-original");
        var document = saved.Source.Document!;
        if (fault != "scope")
            fixture.Objects.Values[fault == "original" ? document.Original.Hash : document.Text.Hash] = Encoding.UTF8.GetBytes(Text + "Geaendert.");
        var actor = fault == "scope" ? Medical with { Products = ["cardio"] } : Medical;
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ReadDocument(saved.Source.Id, 1, actor));
        Assert.Equal(fault == "scope" ? ("product_scope", 403) : ("signal_document_integrity", 409), (error.Code, error.Status));
        Assert.Equal(2, fixture.Objects.Writes);
    }

    [Fact]
    public async Task Promotion_requires_relevance_and_separate_medical_evidence_action_without_case_mutation_or_network()
    {
        var fixture = new Fixture();
        var input = Input() with { Scope = "PublicAuthorized", OriginUri = "https://example.test/public-comparison.txt" };
        var source = (await fixture.Service.ImportDocument(input, Pms, "public-document")).Source;
        var pending = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.Approve(source.Id, new(1, input.ExternalId, Reason), Medical, "early-promotion", default));
        Assert.Equal("signal_document_review", pending.Code);
        var relevant = await fixture.Signals.Decide("source", source.Id, "ConfirmRelevance", new(1, input.ExternalId, Reason), Ra, "human-relevance");
        var unauthorized = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.Approve(source.Id, new(relevant.OwnerVersion, input.ExternalId, Reason), Ra, "ra-promotion", default));
        Assert.Equal(("forbidden", 403), (unauthorized.Code, unauthorized.Status));
        var evidence = await fixture.Service.Approve(source.Id, new(relevant.OwnerVersion, input.ExternalId, Reason), Medical, "medical-evidence", default);
        EvidenceRules.RequireIntegrity(evidence);
        Assert.Equal(Text, evidence.Content);
        Assert.False(evidence.Synthetic);
        Assert.True(evidence.Approved);
        Assert.Equal(source.Document!.Text.Hash, evidence.Hash);
        Assert.Equal(input.OriginUri, evidence.Origin);
        Assert.Contains(source.Document.Original.Hash, evidence.Locator);
        Assert.Equal("source", Assert.Single(fixture.Store.Writes.Last().Dependencies).Kind);
        Assert.Empty(await fixture.Store.List<SafetyCase>("case", Pms.Products));
        Assert.Empty(await fixture.Store.List<AgentInvocation>("agentInvocation", Pms.Products));
        Assert.Equal(0, fixture.Reader.Calls);
    }

    [Fact]
    public async Task Promotion_fails_when_the_bound_source_changes_after_text_review()
    {
        var fixture = new Fixture();
        var input = Input();
        var source = (await fixture.Service.ImportDocument(input, Pms, "promotion-race")).Source;
        var relevant = await fixture.Signals.Decide("source", source.Id, "ConfirmRelevance", new(1, input.ExternalId, Reason), Medical, "race-relevance");
        var current = (await fixture.Store.Get<PublicSourceRun>("source", source.Id))!;
        fixture.Store.BeforeExecute = command =>
        {
            Assert.Equal("evidence", command.Kind);
            fixture.Store.Seed("source", current.Id, current.ProductId, current with { Version = current.Version + 1 });
        };
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.Approve(source.Id,
            new(relevant.OwnerVersion, input.ExternalId, Reason), Medical, "race-promotion", default));
        Assert.Equal(("dependency_version", 409), (error.Code, error.Status));
        Assert.Empty(await fixture.Store.List<Evidence>("evidence", Pms.Products));
    }

    [Fact]
    public async Task Real_outbox_processor_rebuilds_only_after_explicit_medical_document_promotion()
    {
        var fixture = new Fixture();
        var source = (await fixture.Service.ImportDocument(Input(), Pms, "outbox-import")).Source;
        Assert.Equal("Handled", (await fixture.Processor.Process(fixture.Store.EventFor("SignalDocumentImported").Id)).State);
        Assert.Equal(0, fixture.Search.Rebuilds);
        Assert.Empty(fixture.Search.Projected);
        await fixture.Signals.Decide("source", source.Id, "ConfirmRelevance", new(1, Input().ExternalId, Reason), Ra, "outbox-relevance");
        Assert.Equal("Handled", (await fixture.Processor.Process(fixture.Store.EventFor("SignalRelevanceConfirmed").Id)).State);
        Assert.Equal(0, fixture.Search.Rebuilds);
        var evidence = await fixture.Service.Approve(source.Id, new(2, Input().ExternalId, Reason), Medical, "outbox-promotion", default);
        var message = fixture.Store.EventFor("SignalDocumentEvidenceApproved");
        Assert.Equal(Canonical.Hash(evidence), Canonical.HashText(message.Payload));
        Assert.Equal("Handled", (await fixture.Processor.Process(message.Id)).State);
        Assert.Equal(1, fixture.Search.Rebuilds);
        Assert.Equal(Canonical.Hash(evidence), Canonical.Hash(Assert.Single(fixture.Search.Projected)));
        Assert.True(await fixture.Store.HasReceipt(message.Id, "cohors-domain-events-v1"));
        Assert.Equal("Handled", (await fixture.Processor.Process(message.Id)).State);
        Assert.Equal(1, fixture.Search.Rebuilds);
        Assert.Single(fixture.Store.Completions, value => value.EventId == message.Id);
        Assert.Equal(0, fixture.Reader.Calls);
    }

    [Theory]
    [InlineData(true, "search_index_failed")]
    [InlineData(false, "IOException")]
    public async Task Search_failures_retry_and_poison_without_false_handled_receipt(bool domainError, string code)
    {
        var fixture = new Fixture();
        var message = await PromotionEvent(fixture);
        fixture.Search.Failure = domainError
            ? new DomainException("search_index_failed", "Synthetic index failure.", 503)
            : new IOException("Synthetic search transport failure.");
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var result = await fixture.Processor.Process(message.Id);
            Assert.Equal((attempt == 5 ? "Poisoned" : "Retry", code), (result.State, result.Error));
            Assert.Equal(attempt, fixture.Search.Rebuilds);
            Assert.False(await fixture.Store.HasReceipt(message.Id, "cohors-domain-events-v1"));
            Assert.Empty(fixture.Search.Projected);
        }
        Assert.Equal(5, fixture.Store.Completions.Count);
        Assert.All(fixture.Store.Completions, value => Assert.Equal(code, value.Error));
        fixture.Search.Failure = null;
        Assert.Equal("Unavailable", (await fixture.Processor.Process(message.Id)).State);
        Assert.Equal(5, fixture.Search.Rebuilds);
        Assert.False(await fixture.Store.HasReceipt(message.Id, "cohors-domain-events-v1"));
    }

    [Fact]
    public async Task Successful_search_retry_records_receipt_only_after_rebuild()
    {
        var fixture = new Fixture();
        var message = await PromotionEvent(fixture);
        fixture.Search.Failure = new DomainException("search_index_failed", "Synthetic index failure.", 503);
        Assert.Equal("Retry", (await fixture.Processor.Process(message.Id)).State);
        Assert.False(await fixture.Store.HasReceipt(message.Id, "cohors-domain-events-v1"));
        fixture.Search.Failure = null;
        Assert.Equal("Handled", (await fixture.Processor.Process(message.Id)).State);
        Assert.Equal(2, fixture.Search.Rebuilds);
        Assert.Single(fixture.Search.Projected);
        Assert.True(await fixture.Store.HasReceipt(message.Id, "cohors-domain-events-v1"));
        Assert.Collection(fixture.Store.Completions,
            value => Assert.Equal("search_index_failed", value.Error), value => Assert.Null(value.Error));
        Assert.Equal("Handled", (await fixture.Processor.Process(message.Id)).State);
        Assert.Equal(2, fixture.Search.Rebuilds);
        Assert.Equal(2, fixture.Store.Completions.Count);
    }

    [Fact]
    public async Task Storage_hash_mismatch_never_commits_a_source()
    {
        var fixture = new Fixture();
        fixture.Objects.WrongPutHash = true;
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ImportDocument(Input(), Pms, "storage-mismatch"));
        Assert.Equal(("signal_document_storage", 409), (error.Code, error.Status));
        Assert.Empty(fixture.Store.Writes);
    }

    [Fact]
    public void Historic_source_json_keeps_identical_bytes_and_hash_without_document_metadata()
    {
        const string historic = """{"at":"2026-11-16T10:00:00\u002B00:00","error":null,"executionId":null,"id":"read-historic","items":[],"productId":"ortho","queryContract":"public-metadata-readonly-1","source":"pubmed","status":"ReadOnlyMetadata","version":2}""";
        var restored = Canonical.Read<PublicSourceRun>(historic);
        Assert.Null(restored.Document);
        Assert.Equal(historic, Canonical.Serialize(restored));
        Assert.Equal(Canonical.HashText(historic), Canonical.Hash(restored));
    }

    private static PublicSourceRun SyntheticSource(string id, string productId, string externalId, string text, int version = 1)
    {
        var hash = Canonical.HashText(text);
        var length = Encoding.UTF8.GetByteCount(text);
        return new(id, version, "document", productId, SignalDocumentRules.Status, Now,
            [new(externalId, "Synthetische Quelle " + id, "cohors:signal-document:" + id, null, text, hash)], null,
            SignalDocumentRules.Contract,
            SignalReviews: [SignalReviewRules.Pending("source", id, externalId, hash)],
            Document: new(SignalDocumentRules.Contract, id + ".txt", "text/plain",
                new("cohors:signal-original:" + hash, hash, length), new("cohors:signal-text:" + hash, hash, length),
                SignalDocumentRules.ExtractionVersion, "Synthetic", Reason, Reason, "Known", Now.AddDays(-4),
                Reason, Pms.Id, null));
    }

    private static async Task<OutboxEvent> PromotionEvent(Fixture fixture)
    {
        var source = (await fixture.Service.ImportDocument(Input(), Pms, "retry-import")).Source;
        await fixture.Signals.Decide("source", source.Id, "ConfirmRelevance", new(1, Input().ExternalId, Reason), Medical, "retry-relevance");
        await fixture.Service.Approve(source.Id, new(2, Input().ExternalId, Reason), Medical, "retry-promotion", default);
        return fixture.Store.EventFor("SignalDocumentEvidenceApproved");
    }

    private static SafetyCase Comparison() => new(
        Id: "synthetic-comparison-case", Version: 1, Reference: "COH-SYNTHETIC-COMPARISON", ProductId: "ortho",
        Title: "Synthetischer Vergleichsfall", Description: Reason, Udi: null, Batch: null, Country: "IE", Jurisdiction: "EU-IE",
        Seriousness: Seriousness.OtherSerious, OccurredAt: Now.AddDays(-2), FirstAwarenessAt: Now.AddDays(-1),
        SystemReceivedAt: Now, ReportabilityAwarenessAt: null, Scenario: "synthetic", Synthetic: true, Stage: CaseStage.Intake,
        Disposition: Disposition.Undetermined, Sources: [], Awareness: [new("known", "Employee", Now.AddDays(-1), Now, "synthetic employee", Reason, Pms.Id)],
        Deadlines: [new("bound-deadline", "EU-IE", Seriousness.OtherSerious, Now.AddDays(-1), Now.AddDays(14), Now.AddDays(12),
            "synthetic-bound-rule", "synthetic-calendar", "Europe/Dublin", [Reason], "Open", false)],
        Evidence: [], Assessments: [], Reports: [], Approvals: [], Decisions: [], Submissions: [],
        InformationRequests: [], AgentRuns: [], Alerts: []);

    private sealed class Fixture
    {
        public readonly MemoryStore Store = new();
        public readonly MemoryObjects Objects = new();
        public readonly NoNetwork Reader = new();
        public PublicSourceService Service { get; }
        public SignalReviewService Signals { get; }
        public SearchFake Search { get; }
        public OutboxProcessor Processor { get; }
        public Fixture()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Cohors.slnx"))) root = root.Parent;
            var policies = DemoBaseline.LoadPolicies(Path.Combine(root?.FullName ?? throw new InvalidOperationException("Worktree root missing."), "policies"));
            Service = new(Store, Objects, Reader, new Clock(), policies);
            Signals = new(Store, Objects, new Clock());
            Search = new(Store);
            Processor = new(Store, new CaseService(Store, Objects, policies, new NoAgents(), Search, new Clock(), new MirComposer()), Search);
        }
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoAgents : IAgentRunner
    {
        public Task<AgentOutput> Run(AgentInput input, CancellationToken ct = default) =>
            throw new InvalidOperationException("Document import and index routing must not invoke an agent.");
    }
    private sealed class SearchFake(MemoryStore store) : IKnowledgeSearch
    {
        public int Rebuilds { get; private set; }
        public Exception? Failure { get; set; }
        public Evidence[] Projected { get; private set; } = [];
        public async Task<int> Rebuild(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Rebuilds++;
            if (Failure is not null) throw Failure;
            Projected = (await store.List<Evidence>("evidence", ["ortho", "cardio"], ct)).Where(value => value.Approved).ToArray();
            return Projected.Length;
        }
        public Task<Evidence[]> Retrieve(string productId, DateTimeOffset effectiveAt, string query, Actor actor, CancellationToken ct = default) =>
            throw new NotSupportedException("No Azure retrieval is executed by this offline routing fake.");
    }
    private sealed class NoNetwork : IPublicSourceReader
    {
        public int Calls { get; private set; }
        public Task<PublicSourceItem[]> Read(string source, string productId, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("File intake must not invoke an external reader.");
        }
    }
    private sealed class MemoryObjects : IObjectStore
    {
        public readonly Dictionary<string, byte[]> Values = [];
        public int Writes { get; private set; }
        public bool WrongPutHash { get; set; }
        public Task<string> Put(string content, CancellationToken ct = default) => PutBytes(Encoding.UTF8.GetBytes(content), ct);
        public async Task<string> Read(string hash, CancellationToken ct = default) => Encoding.UTF8.GetString(await ReadBytes(hash, ct));
        public Task<string> PutBytes(ReadOnlyMemory<byte> content, CancellationToken ct = default)
        {
            Writes++;
            var hash = SignalDocumentRules.Hash(content.Span);
            Values[hash] = content.ToArray();
            return Task.FromResult(WrongPutHash ? new string('0', 64) : hash);
        }
        public Task<byte[]> ReadBytes(string hash, CancellationToken ct = default) =>
            Task.FromResult(Values.TryGetValue(hash, out var value) ? value.ToArray() : throw new InvalidDataException("Object missing."));
    }
    private sealed class MemoryStore : IStateStore
    {
        private sealed record Entry(string Product, int Version, string Json);
        private readonly Dictionary<(string Kind, string Id), Entry> entries = [];
        private readonly Dictionary<(string Kind, string Id, int Version), string> history = [];
        private readonly Dictionary<string, (string Hash, string Json)> receipts = [];
        private readonly Dictionary<string, OutboxEvent> outbox = [];
        private readonly Dictionary<string, string> owners = [];
        private readonly HashSet<(string EventId, string Consumer)> consumerReceipts = [];
        public readonly List<CommandContext> Writes = [];
        public readonly List<(string EventId, string? Error)> Completions = [];
        public Action<CommandContext>? BeforeExecute { get; set; }
        public OutboxEvent EventFor(string action) => outbox.Values.Single(value => value.Type == action);
        public void Seed<T>(string kind, string id, string product, T value)
        {
            var json = Canonical.Serialize(value);
            using var parsed = JsonDocument.Parse(json);
            var version = parsed.RootElement.GetProperty("version").GetInt32();
            entries[(kind, id)] = new(product, version, json);
            history[(kind, id, version)] = json;
        }
        public Task<T?> Get<T>(string kind, string id, CancellationToken ct = default) =>
            Task.FromResult(entries.TryGetValue((kind, id), out var value) ? Canonical.Read<T>(value.Json) : default);
        public Task<T?> GetVersion<T>(string kind, string id, int version, CancellationToken ct = default) =>
            Task.FromResult(history.TryGetValue((kind, id, version), out var json) ? Canonical.Read<T>(json) : default);
        public Task<T[]> List<T>(string kind, string[] products, CancellationToken ct = default) =>
            Task.FromResult(entries.Where(pair => pair.Key.Kind == kind && products.Contains(pair.Value.Product))
                .Select(pair => Canonical.Read<T>(pair.Value.Json)).ToArray());
        private static string Key(CommandContext c) => Canonical.Hash(new { c.Actor.Id, c.Kind, c.Action, c.IdempotencyKey });
        private static string Hash(CommandContext c) => Canonical.Hash(new { c.Id, c.ProductId, c.ExpectedVersion, c.Request });
        public Task<T?> Receipt<T>(CommandContext command, CancellationToken ct = default)
        {
            command.Actor.RequireProduct(command.ProductId);
            Guard.Text(command.IdempotencyKey, "Idempotency-Key", 8, 120);
            if (!receipts.TryGetValue(Key(command), out var receipt)) return Task.FromResult(default(T));
            Guard.Against(receipt.Hash != Hash(command), "idempotency_conflict", "Changed command.", 409);
            return Task.FromResult<T?>(Canonical.Read<T>(receipt.Json));
        }
        public async Task<T> Execute<T>(CommandContext command, Func<T?, T> change, CancellationToken ct = default)
        {
            if (await Receipt<T>(command, ct) is { } receipt) return receipt;
            BeforeExecute?.Invoke(command);
            BeforeExecute = null;
            foreach (var dependency in command.Dependencies)
                Guard.Against(!entries.TryGetValue((dependency.Kind, dependency.Source.Id), out var source) ||
                    source.Version != dependency.Source.Version || Canonical.HashText(source.Json) != dependency.Source.Hash,
                    "dependency_version", "Changed dependency.", 409);
            entries.TryGetValue((command.Kind, command.Id), out var current);
            Guard.Against((current?.Version ?? 0) != command.ExpectedVersion, "version_conflict", "Changed aggregate.", 409);
            var next = change(current is null ? default : Canonical.Read<T>(current.Json));
            Seed(command.Kind, command.Id, command.ProductId, next);
            Writes.Add(command);
            receipts[Key(command)] = (Hash(command), Canonical.Serialize(next));
            var eventId = "offline-event-" + Writes.Count;
            outbox[eventId] = new(eventId, command.Id, entries[(command.Kind, command.Id)].Version, command.Action,
                Canonical.Serialize(next), command.At, 0, null);
            return next;
        }
        public Task<T[]> History<T>(string kind, string[] products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PortfolioState> Capture(string[] products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Psur> FreezePsur(CommandContext c, PsurCaptureScope s, Func<PortfolioState, Psur> create, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuditEvent[]> Audit(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OutboxEvent[]> PendingEvents(int limit, CancellationToken ct = default) =>
            Task.FromResult(outbox.Values.Where(value => !owners.ContainsKey(value.Id) && value.Attempts < 5 &&
                !consumerReceipts.Any(receipt => receipt.EventId == value.Id)).Take(limit).ToArray());
        public Task<OutboxEvent?> ClaimEvent(string eventId, string owner, CancellationToken ct = default)
        {
            if (!outbox.TryGetValue(eventId, out var value) || value.Attempts >= 5 || owners.ContainsKey(eventId) ||
                consumerReceipts.Any(receipt => receipt.EventId == eventId)) return Task.FromResult<OutboxEvent?>(null);
            owners.Add(eventId, owner);
            outbox[eventId] = value with { Attempts = value.Attempts + 1 };
            return Task.FromResult<OutboxEvent?>(outbox[eventId]);
        }
        public Task CompleteEvent(string eventId, string consumer, string owner, string? error, CancellationToken ct = default)
        {
            if (!owners.TryGetValue(eventId, out var claimedBy) || owner != claimedBy)
                throw new InvalidOperationException("The controlled event lease belongs to another owner.");
            owners.Remove(eventId);
            Completions.Add((eventId, error));
            outbox[eventId] = outbox[eventId] with { Error = error };
            if (error is null) consumerReceipts.Add((eventId, consumer));
            return Task.CompletedTask;
        }
        public Task<OutboxIssue[]> OutboxIssues(string[] products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasReceipt(string eventId, string consumer, CancellationToken ct = default) =>
            Task.FromResult(consumerReceipts.Contains((eventId, consumer)));
    }
}
