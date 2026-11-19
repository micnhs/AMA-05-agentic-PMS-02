using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed partial class FabricC1DeploymentTests
{
    [Fact]
    public async Task PrivateRetirementPersistsOriginalProofBytesWaitsForOwnerAndNeverReactivates()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500, cleanupReserve: 200);
        var hub = new HubFixture(run);
        await hub.Import();
        var started = run.Store.Current!.StartedAtUtc;
        var originals = run.Store.Current!.Objects.ToDictionary(pair => pair.Key, pair => Canonical.Hash(pair.Value));
        run.Api.Reply(404, new());
        run.Api.Reply(404, new());
        var pending = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingOwnerOffboardedReceipt", pending.Status);
        Assert.False(pending.CompleteC1Cleanup);
        var exported = Assert.IsType<FabricC1HubBindingsExport>(pending.OwnerHandoff);
        AssertPrivateRetirementProofs(run, exported);
        Assert.False(exported.OwnerRevisionCreated);
        Assert.Empty(exported.Consumer["privateEndpoints"]!.AsArray());
        Assert.Empty(exported.Consumer["dnsRecords"]!.AsArray());
        Assert.Empty(exported.Consumer["operatorEndpoints"]!.AsArray());
        Assert.Null(exported.Consumer["web"]);

        var requests = run.Api.Requests.Count;
        run.Restart();
        run.Clock.Advance(TimeSpan.FromSeconds(1));
        var resumed = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal(requests, run.Api.Requests.Count);
        Assert.Equal(started, run.Store.Current!.StartedAtUtc);
        Assert.Equal(exported.EvidenceFiles.Keys.Order(), resumed.OwnerHandoff!.EvidenceFiles.Keys.Order());
        foreach (var file in exported.EvidenceFiles) Assert.Equal(file.Value, resumed.OwnerHandoff!.EvidenceFiles[file.Key]);
        Assert.Equal("c1_deploy_cleanup_started", Assert.Throws<DomainException>(() =>
            run.Provisioner.PrepareSharedHubStage("hub-spoke")).Code);

        SyntheticPrivateOwnerOffboard(hub, run);
        QueuePrivateOwnerReadback(hub, run);
        var acknowledged = await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        Assert.Equal("offboarded", acknowledged.State);
        Assert.All(run.Api.Requests.Skip(requests), request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith(hub.HubGroup + "/", request.Uri.AbsolutePath, StringComparison.Ordinal);
        });
        foreach (var original in originals.Where(pair => pair.Key != "shared-hub-receipt"))
            Assert.Equal(original.Value, Canonical.Hash(run.Store.Current!.Objects[original.Key]));
        run.Api.Reply(404, new());
        Assert.Equal("OwnActiveInventoryAbsent", (await run.Provisioner.AdvancePrivateCleanup("operator-stop", default)).Status);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete || request.Method == HttpMethod.Post);
        Assert.False(run.Store.Current!.Objects["private-lifecycle-stop"].Response["workStopped"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OwnerTerminalReceiptCannotSubstituteForConsumerPrerequisiteProofs()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        SyntheticPrivateOwnerOffboard(hub, run);
        var before = run.Api.Requests.Count;
        await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default));
        Assert.Equal(before, run.Api.Requests.Count);
        Assert.Equal("reserved", run.Store.Current!.Objects["shared-hub-receipt"].Response["state"]!.GetValue<string>());
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-owner-acknowledged"));
    }

    [Fact]
    public async Task LostExportSaveAcknowledgementReplaysExactlyThePersistedBytesWithoutNewRequests()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        run.Api.Reply(404, new());
        run.Api.Reply(404, new());
        run.Store.AfterSave = value =>
        {
            if (value.Objects.ContainsKey("private-cleanup-offboarding-export"))
                throw new IOException("synthetic lost durable-export acknowledgement");
        };
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.AdvancePrivateCleanup("operator-stop", default));
        var original = run.Store.Current!.Objects["private-cleanup-offboarding-export"];
        var requests = run.Api.Requests.Count;
        var counters = (run.Store.Current!.Requests, run.Store.Current!.CleanupRequests, run.Store.Current!.CleanupStartedAtUtc);
        run.Store.AfterSave = null;
        run.Restart();
        var result = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingOwnerOffboardedReceipt", result.Status);
        Assert.Equal(original.RequestHash, run.Store.Current!.Objects["private-cleanup-offboarding-export"].RequestHash);
        Assert.Equal(requests, run.Api.Requests.Count);
        Assert.Equal(counters, (run.Store.Current!.Requests, run.Store.Current!.CleanupRequests, run.Store.Current!.CleanupStartedAtUtc));
        foreach (var pair in result.OwnerHandoff!.EvidenceFiles)
            Assert.Equal(Convert.FromBase64String(original.Response["evidenceFiles"]![pair.Key]!.GetValue<string>()), pair.Value);
    }

    [Fact]
    public async Task FailureBetweenWorkAndAccessResumesTheOriginalProofWithoutResettingTheFence()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        run.Api.Reply(404, new());
        run.Store.AfterSave = value =>
        {
            if (value.Objects.ContainsKey("private-cleanup-work-stopped"))
                throw new IOException("synthetic interruption between ordered stages");
        };
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.AdvancePrivateCleanup("operator-stop", default));
        var proof = Canonical.Hash(run.Store.Current!.Objects["private-cleanup-work-stopped"]);
        var cleanupStart = run.Store.Current!.CleanupStartedAtUtc;
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-credentials-revoked"));
        run.Store.AfterSave = null;
        run.Restart();
        run.Clock.Advance(TimeSpan.FromSeconds(1));
        var before = run.Api.Requests.Count;
        run.Api.Reply(404, new());
        var result = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingOwnerOffboardedReceipt", result.Status);
        Assert.Equal(proof, Canonical.Hash(run.Store.Current!.Objects["private-cleanup-work-stopped"]));
        Assert.Equal(cleanupStart, run.Store.Current!.CleanupStartedAtUtc);
        Assert.Single(run.Api.Requests.Skip(before));
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("http-request")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("request-cancelled")]
    [InlineData("http-403")]
    [InlineData("http-503")]
    [InlineData("response-shape")]
    [InlineData("response-size")]
    [InlineData("response-limit")]
    public async Task LostOrAcceptedPeerDeletionIsReadOnlyReconciledAndNeverReplayed(string response)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var original = run.Store.Current!.Objects["arm-hub-transit"];
        var before = run.Api.Requests.Count;
        run.Api.List(PrivateGroupFixture(hub));
        QueuePrivatePeerReadback(hub, run);
        if (response == "accepted") run.Api.Reply(202, new(), retryAfterSeconds: 30);
        else QueuePrivateProviderFailure(run.Api, response);
        var waitingForDelete = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingOriginalSpokePeeringDeletion", waitingForDelete.Status);
        if (response != "accepted")
            Assert.Contains(PrivateProviderFailureBlocker(response, "private-cleanup-spoke-peer-absent"), waitingForDelete.Blockers);
        Assert.True(run.Store.Current!.PendingDeletes.ContainsKey("private-cleanup-spoke-peer-absent"));
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-offboarding-export"));
        Assert.Equal("\"synthetic-etag\"", Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete).ETag);
        run.Restart();
        var delay = response == "accepted" ? 30 : 5;
        var requests = run.Api.Requests.Count;
        var waiting = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal(run.Clock.GetUtcNow().AddSeconds(delay), waiting.NextPollAtUtc);
        Assert.Equal(requests, run.Api.Requests.Count);
        run.Clock.Advance(TimeSpan.FromSeconds(delay));
        run.Api.Reply(404, new());
        var result = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingOwnerOffboardedReceipt", result.Status);
        Assert.Equal(Canonical.Hash(original), Canonical.Hash(run.Store.Current!.Objects["arm-hub-transit"]));
        Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete);
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Uri.AbsolutePath.StartsWith(hub.HubGroup, StringComparison.Ordinal));
        AssertPrivateRetirementProofs(run, result.OwnerHandoff!);
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("vnet-instance")]
    [InlineData("foreign-peer")]
    public async Task OriginalPeerDeletionRejectsReplacedOrForeignTransitBeforeAnyDelete(string mutation)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var before = run.Api.Requests.Count;
        run.Api.List(PrivateGroupFixture(hub));
        var peer = (JsonObject)hub.Resources[hub.SpokeVnet + "/virtualNetworkPeerings/to-shared-hub"].DeepClone();
        if (mutation == "etag") peer["etag"] = "replacement";
        run.Api.Reply(200, peer);
        if (mutation != "etag")
        {
            var vnet = (JsonObject)hub.Resources[hub.SpokeVnet].DeepClone();
            if (mutation == "vnet-instance") vnet["properties"]!["resourceGuid"] = Guid.NewGuid().ToString("D");
            run.Api.Reply(200, vnet);
            if (mutation == "foreign-peer")
            {
                var foreign = (JsonObject)peer.DeepClone();
                foreign["id"] = hub.SpokeVnet + "/virtualNetworkPeerings/foreign";
                run.Api.List(peer, foreign);
            }
        }
        Assert.Equal("c1_private_cleanup_peer_replaced", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvancePrivateCleanup("operator-stop", default))).Code);
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Method != HttpMethod.Get);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-offboarding-export"));
    }

    [Fact]
    public async Task OriginalPeerDeletionPreservesTheLastAdmittedWeakETag()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        hub.Resources[hub.SpokeVnet + "/virtualNetworkPeerings/to-shared-hub"]["etag"] = "W/\"synthetic-generation\"";
        hub.Receipt["revision"] = 4;
        hub.RefreshHashes();
        await hub.Import();
        var before = run.Api.Requests.Count;
        run.Api.List(PrivateGroupFixture(hub));
        QueuePrivatePeerReadback(hub, run);
        run.Api.Reply(204, new());
        run.Api.Reply(404, new());
        Assert.Equal("AwaitingOwnerOffboardedReceipt", (await run.Provisioner.AdvancePrivateCleanup("operator-stop", default)).Status);
        Assert.Equal("W/\"synthetic-generation\"",
            Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete).ETag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnVmAndF2StopAreReadBackBeforeExportAndTheManualOwnerWait(bool lostProbeStopReply)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var (vm, capacity) = SeedPrivatePaidFoundation(run, hub);
        var before = run.Api.Requests.Count;
        run.Api.Reply(200, vm);
        run.Api.Reply(200, PrivateVmPower("running"));
        if (lostProbeStopReply) run.Api.Fail(new HttpRequestException("synthetic lost original probe-stop response"));
        else run.Api.Reply(202, new(), retryAfterSeconds: 5);
        run.Api.Reply(200, capacity);
        run.Api.Reply(202, new(), retryAfterSeconds: 5);
        var stopping = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", stopping.Status);
        Assert.Contains(lostProbeStopReply ? "provider-response-unresolved:private-cleanup-probe-stop" :
            "probe-deallocation-not-observed", stopping.Blockers);
        Assert.Contains("capacity-suspension-not-observed", stopping.Blockers);
        Assert.Equal(run.Clock.GetUtcNow().AddSeconds(5), stopping.NextPollAtUtc);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-work-stopped"));
        Assert.Equal(2, run.Api.Requests.Skip(before).Count(request => request.Method == HttpMethod.Post));
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete);

        run.Restart();
        var requests = run.Api.Requests.Count;
        var waiting = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal(stopping.NextPollAtUtc, waiting.NextPollAtUtc);
        Assert.Equal(requests, run.Api.Requests.Count);
        run.Clock.Advance(TimeSpan.FromSeconds(5));
        capacity["properties"]!["state"] = "Suspended";
        run.Api.Reply(200, vm);
        run.Api.Reply(200, PrivateVmPower("deallocated"));
        run.Api.Reply(200, capacity);
        run.Api.List(PrivateGroupFixture(hub).Concat([vm, capacity]).ToArray());
        QueuePrivatePeerReadback(hub, run);
        run.Api.Reply(204, new());
        run.Api.Reply(404, new());
        var stopped = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingOwnerOffboardedReceipt", stopped.Status);
        AssertPrivateRetirementProofs(run, stopped.OwnerHandoff!);
        Assert.Equal("Suspended", run.Store.Current!.Objects["private-cleanup-capacity-stop-observed"].Response["capacity"]!["properties"]!["state"]!.GetValue<string>());
        Assert.Single(run.Api.Requests.Skip(before), request => request.Uri.AbsolutePath.EndsWith("/deallocate", StringComparison.Ordinal));
        Assert.Single(run.Api.Requests.Skip(before), request => request.Uri.AbsolutePath.EndsWith("/suspend", StringComparison.Ordinal));
        Assert.All(run.Api.Requests.Skip(before).Where(request => request.Method != HttpMethod.Get),
            request => Assert.StartsWith(hub.OwnGroup + "/", request.Uri.AbsolutePath, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http-request")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("request-cancelled")]
    [InlineData("http-403")]
    [InlineData("legacy-http-403")]
    [InlineData("http-503")]
    [InlineData("response-shape")]
    [InlineData("response-size")]
    [InlineData("response-limit")]
    public async Task NotebookCancellationKeepsOriginalPrivateJobIdentityAndUnknownOutcomeIsNotReplayed(string response)
    {
        using var run = new Harness(sharedHub: true, delegated: true, maxRequests: 1000, cleanupReserve: 200);
        var hub = await HubFixture.Connected(run);
        var (vm, capacity) = SeedPrivatePaidFoundation(run, hub);
        var source = new SourceNotebookHarness(run);
        await source.Submit("released");
        await source.Submit("denied");
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        var before = run.Api.Requests.Count;
        run.Api.Reply(200, vm);
        run.Api.Reply(200, PrivateVmPower("deallocated"));
        run.Api.Reply(200, source.Job("released", "InProgress"));
        QueuePrivateProviderFailure(run.Api, response);
        run.Api.Reply(200, source.Job("denied", "InProgress"));
        run.Api.Reply(202, new(), retryAfterSeconds: 5);
        run.Api.Reply(200, capacity);
        run.Api.Reply(202, new(), retryAfterSeconds: 5);
        var initial = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", initial.Status);
        Assert.Contains(PrivateProviderFailureBlocker(response, "private-cleanup-notebook-stop-released"), initial.Blockers);
        Assert.Contains("notebook-cancellation-not-observed:denied", initial.Blockers);
        Assert.Contains("capacity-suspension-not-observed", initial.Blockers);
        Assert.Null(initial.OwnerHandoff);
        Assert.False(initial.CompleteC1Cleanup);
        var posts = run.Api.Requests.Skip(before).Where(request => request.Method == HttpMethod.Post).ToArray();
        Assert.Equal(3, posts.Length);
        var cancel = Assert.Single(posts, request => request.Uri.IdnHost == FabricC1Provisioner.WorkspaceHost(source.WorkspaceId("released")));
        Assert.EndsWith("/cancel", cancel.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Null(cancel.Body);
        Assert.Contains(posts, request => request.Uri.IdnHost == FabricC1Provisioner.WorkspaceHost(source.WorkspaceId("denied")) &&
            request.Uri.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal));
        Assert.Contains(posts, request => request.Uri.AbsolutePath.EndsWith("/suspend", StringComparison.Ordinal));
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-work-stopped"));
        var definitiveDenial = response is "http-403" or "legacy-http-403";
        Assert.Equal(!definitiveDenial, run.Store.Current.PendingDeletes.ContainsKey("private-cleanup-notebook-stop-released"));
        var originalIntent = Canonical.Hash(run.Store.Current.Objects["private-cleanup-notebook-stop-released-intent"]);
        var originalClock = (run.Store.Current.StartedAtUtc, run.Store.Current.CleanupStartedAtUtc);
        var expectedPosts = 3;
        if (definitiveDenial)
        {
            if (response == "legacy-http-403") run.Restart();
            var notDueStart = run.Api.Requests.Count;
            run.Api.Reply(200, vm);
            run.Api.Reply(200, PrivateVmPower("deallocated"));
            var notDue = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
            Assert.Contains("notebook-stop-poll-not-due:released", notDue.Blockers);
            Assert.DoesNotContain(run.Api.Requests.Skip(notDueStart), request => request.Method == HttpMethod.Post);
            Assert.Equal(403, run.Store.Current!.DeniedRequests["private-cleanup-notebook-stop-released"].Status);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                run.Clock.Advance(TimeSpan.FromSeconds(5));
                var attemptStart = run.Api.Requests.Count;
                run.Api.Reply(200, vm);
                run.Api.Reply(200, PrivateVmPower("deallocated"));
                run.Api.Reply(200, source.Job("released", "InProgress"));
                run.Api.Reply(attempt == 0 ? 403 : 202, new(), retryAfterSeconds: 5);
                run.Api.Reply(200, source.Job("denied", "InProgress"));
                capacity["properties"]!["state"] = "Suspending";
                run.Api.Reply(200, capacity);
                var retry = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
                Assert.Equal("AwaitingVerifiedWorkStop", retry.Status);
                var repeated = Assert.Single(run.Api.Requests.Skip(attemptStart), request => request.Method == HttpMethod.Post);
                Assert.Equal(cancel.Uri, repeated.Uri);
                Assert.Equal(cancel.ETag, repeated.ETag);
                Assert.Null(repeated.Body);
                Assert.Equal(originalIntent, Canonical.Hash(run.Store.Current!.Objects["private-cleanup-notebook-stop-released-intent"]));
                Assert.Equal(attempt != 0, run.Store.Current.PendingDeletes.ContainsKey("private-cleanup-notebook-stop-released"));
                var denied = run.Store.Current.DeniedRequests["private-cleanup-notebook-stop-released"];
                Assert.Equal(403, denied.Status);
                Assert.Equal(run.Store.Current.Objects["private-cleanup-notebook-stop-released-intent"].RequestHash, denied.RequestHash);
                if (attempt == 0)
                    Assert.Contains(PrivateProviderFailureBlocker(response, "private-cleanup-notebook-stop-released"), retry.Blockers);
                Assert.Null(retry.OwnerHandoff);
                Assert.False(run.Store.Current.Objects.ContainsKey("private-cleanup-work-stopped"));
                expectedPosts++;
            }
        }
        run.Restart();
        run.Clock.Advance(TimeSpan.FromSeconds(5));
        run.Api.Reply(200, vm);
        run.Api.Reply(200, PrivateVmPower("deallocated"));
        run.Api.Reply(200, source.Job("released", "InProgress"));
        run.Api.Reply(200, source.Job("denied", "InProgress"));
        capacity["properties"]!["state"] = "Suspending";
        run.Api.Reply(200, capacity);
        Assert.Equal("AwaitingVerifiedWorkStop", (await run.Provisioner.AdvancePrivateCleanup("operator-stop", default)).Status);
        Assert.Equal(originalIntent, Canonical.Hash(run.Store.Current!.Objects["private-cleanup-notebook-stop-released-intent"]));
        Assert.Equal(expectedPosts, run.Api.Requests.Skip(before).Count(request => request.Method == HttpMethod.Post));
        run.Clock.Advance(TimeSpan.FromSeconds(5));
        run.Api.Reply(200, vm);
        run.Api.Reply(200, PrivateVmPower("deallocated"));
        run.Api.Reply(200, source.Job("released", "Cancelled"));
        run.Api.Reply(200, source.Job("denied", "InProgress"));
        capacity["properties"]!["state"] = "Suspended";
        run.Api.Reply(200, capacity);
        var result = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", result.Status);
        Assert.Contains("notebook-cancellation-not-observed:denied", result.Blockers);
        Assert.Equal("Cancelled", run.Store.Current!.Objects["private-cleanup-notebook-stop-released-observed"].Response["job"]!["status"]!.GetValue<string>());
        Assert.Equal("Suspended", run.Store.Current.Objects["private-cleanup-capacity-stop-observed"].Response["capacity"]!["properties"]!["state"]!.GetValue<string>());
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-offboarding-export"));
        Assert.Equal(expectedPosts, run.Api.Requests.Skip(before).Count(request => request.Method == HttpMethod.Post));
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete);
        Assert.All(run.Api.Requests.Skip(before).Where(request => request.Uri.IdnHost != "management.azure.com"),
            request => Assert.Contains(request.Uri.IdnHost, new[] { "released", "denied" }
                .Select(role => FabricC1Provisioner.WorkspaceHost(source.WorkspaceId(role)))));
        Assert.True(run.Store.Current!.Objects.ContainsKey("source-notebook-job-intent-released"));
        Assert.True(run.Store.Current!.Objects.ContainsKey("source-notebook-job-released"));
        Assert.False(run.Store.Current!.Objects.ContainsKey("source-materialization-released"));
        Assert.Equal(originalClock, (run.Store.Current.StartedAtUtc, run.Store.Current.CleanupStartedAtUtc));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task DeniedProbeStopRetriesOnlyWhenDueWithItsOriginalIfMatchAndDoesNotReplay412(int status)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var (vm, capacity) = SeedPrivatePaidFoundation(run, hub);
        const string key = "private-cleanup-probe-stop";
        var before = run.Api.Requests.Count;
        run.Api.Reply(200, vm);
        run.Api.Reply(200, PrivateVmPower("running"));
        run.Api.Reply(status, new(), retryAfterSeconds: 7);
        run.Api.Reply(200, capacity);
        run.Api.Reply(202, new(), retryAfterSeconds: 5);
        var first = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", first.Status);
        Assert.Contains("provider-response-rejected:" + key + ":c1_deploy_http_" + status, first.Blockers);
        Assert.False(run.Store.Current!.PendingDeletes.ContainsKey(key));
        var intent = Canonical.Hash(run.Store.Current.Objects[key + "-intent"]);
        var originalClock = (run.Store.Current.StartedAtUtc, run.Store.Current.CleanupStartedAtUtc);
        var originalPost = Assert.Single(run.Api.Requests.Skip(before), request =>
            request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/deallocate", StringComparison.Ordinal));
        run.Restart();
        var notDueStart = run.Api.Requests.Count;
        var notDue = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Contains("probe-stop-poll-not-due", notDue.Blockers);
        Assert.Equal(notDueStart, run.Api.Requests.Count);
        run.Clock.Advance(TimeSpan.FromSeconds(7));
        var retryStart = run.Api.Requests.Count;
        var later = (JsonObject)vm.DeepClone();
        later["etag"] = "later-readback-etag";
        run.Api.Reply(200, later);
        run.Api.Reply(200, PrivateVmPower("running"));
        run.Api.Reply(412, new());
        capacity["properties"]!["state"] = "Suspending";
        run.Api.Reply(200, capacity);
        var retry = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", retry.Status);
        Assert.Contains("provider-response-rejected:" + key + ":c1_deploy_http_412", retry.Blockers);
        var repeated = Assert.Single(run.Api.Requests.Skip(retryStart), request => request.Method == HttpMethod.Post);
        Assert.Equal(originalPost.Uri, repeated.Uri);
        Assert.Equal(originalPost.ETag, repeated.ETag);
        Assert.Equal("\"synthetic-vm-etag\"", repeated.ETag);
        Assert.NotEqual("\"later-readback-etag\"", repeated.ETag);
        Assert.Null(repeated.Body);
        Assert.Equal(intent, Canonical.Hash(run.Store.Current!.Objects[key + "-intent"]));
        Assert.True(run.Store.Current.PendingDeletes.ContainsKey(key));
        Assert.Equal(status, run.Store.Current.DeniedRequests[key].Status);
        run.Restart();
        run.Clock.Advance(TimeSpan.FromSeconds(5));
        var readOnlyStart = run.Api.Requests.Count;
        run.Api.Reply(200, later);
        run.Api.Reply(200, PrivateVmPower("running"));
        run.Api.Reply(200, capacity);
        var unresolved = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", unresolved.Status);
        Assert.DoesNotContain(run.Api.Requests.Skip(readOnlyStart), request => request.Method != HttpMethod.Get);
        Assert.Equal(intent, Canonical.Hash(run.Store.Current!.Objects[key + "-intent"]));
        Assert.True(run.Store.Current.PendingDeletes.ContainsKey(key));
        Assert.Equal(originalClock, (run.Store.Current.StartedAtUtc, run.Store.Current.CleanupStartedAtUtc));
        Assert.Null(unresolved.OwnerHandoff);
        Assert.False(run.Store.Current.Objects.ContainsKey("private-cleanup-work-stopped"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OuterCancellationNeverContinuesToAnotherPaidStop(bool alreadyCancelled)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var (vm, _) = SeedPrivatePaidFoundation(run, hub);
        using var outer = new CancellationTokenSource();
        var before = run.Api.Requests.Count;
        if (alreadyCancelled) outer.Cancel();
        else
        {
            run.Api.Reply(200, vm);
            run.Api.Reply(200, PrivateVmPower("running"));
            run.Api.ReplyComputed(202, () =>
            {
                outer.Cancel();
                throw new TaskCanceledException("synthetic outer cancellation", null, outer.Token);
            });
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.Provisioner.AdvancePrivateCleanup("operator-stop", outer.Token));
        Assert.Equal(alreadyCancelled ? 0 : 1, run.Api.Requests.Skip(before).Count(request => request.Method == HttpMethod.Post));
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Uri.AbsolutePath.Contains("/capacities/", StringComparison.Ordinal));
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-work-stopped"));
        Assert.False(run.Store.Current.Objects.ContainsKey("private-cleanup-offboarding-export"));
    }

    [Fact]
    public async Task AnExpiredOriginalTailCannotBeRenewedAfterRequestBudgetCancellation()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000, manualTimers: true);
        var hub = await HubFixture.Connected(run);
        var (vm, _) = SeedPrivatePaidFoundation(run, hub);
        var duration = run.Input.Cleanup!.MaxDurationSeconds!.Value;
        using var outer = new CancellationTokenSource();
        run.Api.Reply(200, vm);
        run.Api.Reply(200, PrivateVmPower("running"));
        run.Api.ReplyComputed(202, () =>
        {
            run.Clock.Advance(TimeSpan.FromSeconds(duration));
            throw new OperationCanceledException("synthetic request reaching its original cleanup deadline");
        });
        var before = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvancePrivateCleanup("operator-stop", outer.Token))).Code);
        Assert.False(outer.IsCancellationRequested);
        Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Uri.AbsolutePath.Contains("/capacities/", StringComparison.Ordinal));
        Assert.True(run.Store.Current!.PendingDeletes.ContainsKey("private-cleanup-probe-stop"));
        Assert.False(run.Store.Current.Objects.ContainsKey("private-cleanup-capacity-stop-intent"));
        var deadline = run.Store.Current.CleanupStartedAtUtc!.Value.AddSeconds(duration);
        Assert.Equal(deadline, run.Clock.GetUtcNow());
        run.Restart();
        before = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvancePrivateCleanup("operator-stop", outer.Token))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
        Assert.Equal(deadline, run.Store.Current!.CleanupStartedAtUtc!.Value.AddSeconds(duration));
    }

    [Theory]
    [InlineData("conflict-before-stop")]
    [InlineData("io-before-stop")]
    [InlineData("io-after-stop")]
    public async Task LocalJournalFailureNeverBecomesAProviderLossOrContinuesStops(string failure)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var (vm, _) = SeedPrivatePaidFoundation(run, hub);
        var before = run.Api.Requests.Count;
        run.Api.Reply(200, vm);
        run.Api.Reply(200, PrivateVmPower("running"));
        run.Api.Reply(202, new(), retryAfterSeconds: 5);
        run.Store.AfterSave = value =>
        {
            if (failure == "io-after-stop" ? value.Objects.ContainsKey("private-cleanup-probe-stop-poll") :
                value.PendingDeletes.ContainsKey("private-cleanup-probe-stop"))
            {
                if (failure == "conflict-before-stop")
                    throw new DomainException("c1_deploy_journal_conflict", "synthetic original journal CAS conflict", 409);
                throw new IOException("synthetic original journal write uncertainty");
            }
        };
        if (failure == "conflict-before-stop")
            Assert.Equal("c1_deploy_journal_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
                run.Provisioner.AdvancePrivateCleanup("operator-stop", default))).Code);
        else
            await Assert.ThrowsAsync<IOException>(() => run.Provisioner.AdvancePrivateCleanup("operator-stop", default));
        Assert.Equal(failure == "io-after-stop" ? 1 : 0, run.Api.Requests.Skip(before).Count(request => request.Method == HttpMethod.Post));
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Uri.AbsolutePath.Contains("/capacities/", StringComparison.Ordinal));
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-work-stopped"));
        var failedAt = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvancePrivateCleanup("operator-stop", default))).Code);
        Assert.Equal(failedAt, run.Api.Requests.Count);
    }

    [Theory]
    [InlineData("missing-stage")]
    [InlineData("different-request")]
    [InlineData("different-id")]
    [InlineData("incomplete-stage")]
    public async Task IntermediateArmObservationNeedsItsCompletedOriginalStage(string mutation)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var state = run.Store.Load()!;
        var original = state.Objects["arm-hub-spoke"];
        state.Objects["arm-hub-spoke-observation"] = original with
        {
            Kind = "ArmDeploymentObservation",
            Id = mutation == "different-id" ? original.Id + "-foreign" : original.Id,
            RequestHash = mutation == "different-request" ? new string('0', 64) : original.RequestHash
        };
        if (mutation == "missing-stage") state.Objects.Remove("arm-hub-spoke");
        if (mutation == "incomplete-stage") state.Objects["arm-hub-spoke"].Response["properties"]!["provisioningState"] = "Running";
        run.Store.Save(state);
        run.Restart();
        var before = run.Api.Requests.Count;
        var result = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", result.Status);
        Assert.Contains("unresolved-original-arm-observation:arm-hub-spoke-observation", result.Blockers);
        Assert.Equal(before, run.Api.Requests.Count);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-work-stopped"));
        Assert.Null(result.OwnerHandoff);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("http-503")]
    public async Task FinalCapacityDeleteUncertaintyRetainsForeignResourcesAndOnlyReconcilesTheOriginalIntent(string response)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000, cleanupReserve: 200);
        var hub = await HubFixture.Connected(run);
        var (_, capacity) = SeedPrivatePaidFoundation(run, hub);
        capacity["properties"]!["state"] = "Suspended";
        run.Api.Reply(404, new());
        run.Api.Reply(200, capacity);
        run.Api.List(capacity);
        run.Api.Reply(404, new());
        var offboarding = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingOwnerOffboardedReceipt", offboarding.Status);
        var exportHash = run.Store.Current!.Objects["private-cleanup-offboarding-export"].RequestHash;
        SyntheticPrivateOwnerOffboard(hub, run);
        QueuePrivateOwnerReadback(hub, run);
        await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        var foreign = new JsonObject
        {
            ["id"] = hub.OwnGroup + "/providers/Microsoft.Storage/storageAccounts/foreign-retained",
            ["type"] = "Microsoft.Storage/storageAccounts",
            ["tags"] = new JsonObject { ["deploymentId"] = "foreign" }
        };
        var foreignHash = Canonical.Hash(foreign);
        var capacityId = capacity["id"]!.GetValue<string>();
        var key = "private-cleanup-resource-" + Canonical.HashText(capacityId.ToLowerInvariant());
        var before = run.Api.Requests.Count;
        run.Api.List(capacity, foreign);
        run.Api.Reply(404, new());
        run.Api.Reply(200, capacity);
        QueuePrivateProviderFailure(run.Api, response);
        var pending = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("OwnInventoryCleanupBlocked", pending.Status);
        Assert.Contains(PrivateProviderFailureBlocker(response, key), pending.Blockers);
        Assert.Contains(pending.Blockers, value => value.StartsWith("network-dependents-not-proven-absent:", StringComparison.Ordinal));
        Assert.True(run.Store.Current!.PendingDeletes.ContainsKey(key));
        var intentHash = Canonical.Hash(run.Store.Current.Objects[key + "-intent"]);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            run.Restart();
            run.Clock.Advance(TimeSpan.FromSeconds(5));
            run.Api.List(capacity, foreign);
            run.Api.Reply(404, new());
            run.Api.Reply(attempt == 0 ? 200 : 404, attempt == 0 ? capacity : new());
            var resumed = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
            Assert.Equal("OwnInventoryCleanupBlocked", resumed.Status);
            Assert.Contains("retained-unbound-or-unsupported-resource:" + foreign["id"]!.GetValue<string>(), resumed.Blockers);
            Assert.False(resumed.CompleteC1Cleanup);
            Assert.Equal(intentHash, Canonical.Hash(run.Store.Current!.Objects[key + "-intent"]));
        }
        Assert.True(run.Store.Current!.CleanupReceipts.ContainsKey(key));
        Assert.False(run.Store.Current.PendingDeletes.ContainsKey(key));
        var delete = Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete);
        Assert.Equal(capacityId, delete.Uri.AbsolutePath);
        Assert.Equal("\"synthetic-capacity-etag\"", delete.ETag);
        Assert.All(run.Api.Requests.Skip(before), request =>
            Assert.True(request.Method == HttpMethod.Get || request.Uri.AbsolutePath == capacityId));
        Assert.Equal(exportHash, run.Store.Current.Objects["private-cleanup-offboarding-export"].RequestHash);
        Assert.Equal(foreignHash, Canonical.Hash(foreign));
        Assert.DoesNotContain(run.Store.Current.Objects.Values, value => value.Kind == "PrivateInventoryAbsent");
    }

    [Fact]
    public async Task SourceInventoryAbsenceDoesNotBecomeEffectiveGrantRevocationEvidence()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        run.Seed("runtime-source-grant", "WorkspaceGrant", new() { ["id"] = Guid.NewGuid().ToString("D") });
        var state = run.Store.Load()!;
        state.CleanupReceipts["workspace-released"] = new("SourceWorkspaceAbsent", Guid.NewGuid().ToString("D"),
            "synthetic-inventory-not-revocation", new()
            { ["effectiveRevocationProven"] = false, ["workersStopped"] = false, ["retentionPurged"] = false });
        run.Store.Save(state);
        run.Restart();
        var before = run.Api.Requests.Count;
        run.Api.Reply(404, new());
        var result = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingEffectiveCredentialAndGrantRevocation", result.Status);
        Assert.Contains("effective-revocation-not-proven:runtime-source-grant", result.Blockers);
        Assert.True(run.Store.Current!.Objects.ContainsKey("private-cleanup-work-stopped"));
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-credentials-revoked"));
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-offboarding-export"));
        Assert.Single(run.Api.Requests.Skip(before));
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Uri.AbsolutePath.Contains("virtualNetworkPeerings", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hosted-session", "HostedSession")]
    [InlineData("unexpected-worker", "UnimplementedWorker")]
    public async Task UnsupportedRuntimeIsFencedButNeverCountedAsStopped(string key, string kind)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        run.Seed(key, kind, new() { ["id"] = Guid.NewGuid().ToString("D") });
        var before = run.Api.Requests.Count;
        var result = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", result.Status);
        Assert.Contains("unsupported-runtime-or-history:" + key, result.Blockers);
        Assert.NotNull(run.Store.Current!.CleanupStartedAtUtc);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-work-stopped"));
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-offboarding-export"));
        Assert.Equal(before, run.Api.Requests.Count);
        Assert.Equal("c1_deploy_cleanup_dependents", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupIdentityPhase(default))).Code);
    }

    [Fact]
    public async Task OriginalDeadlineCleanupCanImportTerminalAckPastExpiryButCannotRenewItsTail()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500, cleanupReserve: 200);
        var hub = new HubFixture(run);
        await hub.Import();
        var started = run.Store.Current!.StartedAtUtc;
        run.Clock.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1));
        run.Api.Reply(404, new());
        run.Api.Reply(404, new());
        var cleanup = await run.Provisioner.AdvancePrivateCleanup("original-deadline", default);
        Assert.Equal("AwaitingOwnerOffboardedReceipt", cleanup.Status);
        SyntheticPrivateOwnerOffboard(hub, run);
        QueuePrivateOwnerReadback(hub, run);
        Assert.Equal("offboarded", (await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default)).State);
        Assert.Equal(started, run.Store.Current!.StartedAtUtc);
        Assert.Equal(run.Input.ExpiresAtUtc, DateTimeOffset.Parse(cleanup.OwnerHandoff!.Consumer["expiresAtUtc"]!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture));
        var counters = (run.Store.Current!.Requests, run.Store.Current!.CleanupRequests);
        var before = run.Api.Requests.Count;
        run.Clock.Advance(TimeSpan.FromMinutes(9));
        run.Restart();
        Assert.Equal("c1_deploy_cleanup_budget", Assert.Throws<DomainException>(() => run.Provisioner.RestorePrivateOffboardingExport()).Code);
        Assert.Equal(counters, (run.Store.Current!.Requests, run.Store.Current!.CleanupRequests));
        Assert.Equal(before, run.Api.Requests.Count);
    }

    [Theory]
    [InlineData("unknown-submission")]
    [InlineData("404")]
    [InlineData("Deduped")]
    public async Task UncertainNotebookOutcomesCannotProduceAStoppedWorkProof(string outcome)
    {
        using var run = new Harness(sharedHub: true, delegated: true, maxRequests: 1000);
        var hub = new HubFixture(run);
        await hub.Import();
        var source = new SourceNotebookHarness(run);
        if (outcome == "unknown-submission")
        {
            source.QueueNotebook("released");
            run.Api.Fail(new IOException("synthetic lost job submission"));
            await Assert.ThrowsAsync<IOException>(() => run.Provisioner.AdvanceSourceMaterialization("released", default));
        }
        else await source.Submit("released");
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        var before = run.Api.Requests.Count;
        if (outcome == "404") run.Api.Reply(404, new());
        if (outcome == "Deduped") run.Api.Reply(200, source.Job("released", "Deduped"));
        var result = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", result.Status);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-work-stopped"));
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-offboarding-export"));
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Method != HttpMethod.Get);
        if (outcome == "unknown-submission")
        {
            Assert.Equal(before, run.Api.Requests.Count);
            Assert.True(run.Store.Current!.PendingCreates.ContainsKey("source-notebook-job-released"));
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("operation")]
    [InlineData("delete")]
    [InlineData("generated-child")]
    public async Task PruningOriginalUnresolvedOrGeneratedChildHistoryNeverUnlocksCleanup(string kind)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        var state = run.Store.Load()!;
        if (kind == "create") state.PendingCreates["arm-foundation"] = new string('a', 64);
        if (kind == "operation") state.PendingOperations["arm-foundation"] = "https://management.azure.com/synthetic-operation";
        if (kind == "delete") state.PendingDeletes["unhandled-original-delete"] = new string('a', 64);
        if (kind == "generated-child")
        {
            state.Objects["arm-child-synthetic"] = new("ArmGeneratedChild",
                hub.OwnGroup + "/providers/Microsoft.Compute/disks/synthetic", new string('a', 64), new()
                { ["parentId"] = hub.OwnGroup + "/providers/Microsoft.Compute/virtualMachines/synthetic" });
            state.Objects["unhandled-worker"] = new("UnimplementedWorker", "synthetic", "synthetic", new());
        }
        run.Store.Save(state);
        run.Restart();
        Assert.Equal("AwaitingVerifiedWorkStop", (await run.Provisioner.AdvancePrivateCleanup("operator-stop", default)).Status);
        state = run.Store.Load()!;
        if (kind == "create") state.PendingCreates.Remove("arm-foundation");
        if (kind == "operation") state.PendingOperations.Remove("arm-foundation");
        if (kind == "delete") state.PendingDeletes.Remove("unhandled-original-delete");
        if (kind == "generated-child") state.Objects.Remove("arm-child-synthetic");
        run.Store.Save(state);
        run.Restart();
        var before = run.Api.Requests.Count;
        Assert.Equal("c1_private_cleanup_history", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvancePrivateCleanup("operator-stop", default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-offboarding-export"));
    }

    [Fact]
    public async Task CleanupRequestExhaustionSurvivesRestartWithoutReissuingUnknownDeletes()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var before = run.Api.Requests.Count;
        run.Api.List(PrivateGroupFixture(hub));
        QueuePrivatePeerReadback(hub, run);
        run.Api.Reply(202, new());
        await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        var started = run.Store.Current!.CleanupStartedAtUtc;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            run.Restart();
            run.Clock.Advance(TimeSpan.FromSeconds(5));
            QueuePrivatePeerReadback(hub, run);
            Assert.Equal("AwaitingOriginalSpokePeeringDeletion", (await run.Provisioner.AdvancePrivateCleanup("operator-stop", default)).Status);
        }
        run.Restart();
        run.Clock.Advance(TimeSpan.FromSeconds(5));
        QueuePrivatePeerReadback(hub, run);
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvancePrivateCleanup("operator-stop", default))).Code);
        Assert.Equal(run.Input.Cleanup!.ReservedRequests, run.Store.Current!.CleanupRequests);
        Assert.Equal(started, run.Store.Current!.CleanupStartedAtUtc);
        var exhausted = run.Api.Requests.Count;
        run.Restart();
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvancePrivateCleanup("operator-stop", default))).Code);
        Assert.Equal(exhausted, run.Api.Requests.Count);
        Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete);
        Assert.True(run.Store.Current!.Objects.ContainsKey("arm-hub-spoke"));
        Assert.True(run.Store.Current!.Objects.ContainsKey("arm-hub-peer"));
        Assert.True(run.Store.Current!.Objects.ContainsKey("arm-hub-transit"));
    }

    [Fact]
    public async Task OwnerAckAndTerminalReceiptPersistAtomicallyAcrossLostSaveAcknowledgement()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500, cleanupReserve: 200);
        var hub = new HubFixture(run);
        await hub.Import();
        run.Api.Reply(404, new());
        run.Api.Reply(404, new());
        await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        SyntheticPrivateOwnerOffboard(hub, run);
        QueuePrivateOwnerReadback(hub, run);
        run.Store.AfterSave = state =>
        {
            if (state.Objects["shared-hub-receipt"].Response["state"]!.GetValue<string>() == "offboarded")
                throw new IOException("synthetic lost atomic owner-ack acknowledgement");
        };
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default));
        Assert.True(run.Store.Current!.Objects.ContainsKey("private-cleanup-owner-acknowledged"));
        run.Store.AfterSave = null;
        run.Restart();
        var before = run.Api.Requests.Count;
        run.Api.Reply(404, new());
        Assert.Equal("OwnActiveInventoryAbsent", (await run.Provisioner.AdvancePrivateCleanup("operator-stop", default)).Status);
        var only = Assert.Single(run.Api.Requests.Skip(before));
        Assert.Equal(hub.OwnGroup + "/resources", only.Uri.AbsolutePath);
    }

    [Fact]
    public async Task OffboardingRetainsForeignHubPeersDnsAndSharedDomainsWithoutForeignWorkloadReads()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000, cleanupReserve: 200);
        // This reserve belongs to a fresh journal, before its first request, not to a resumed deployment.
        var input = run.Input with { Cleanup = run.Input.Cleanup! with { ReservedRequests = 80 } };
        var provisioner = new FabricC1Provisioner(input, run.Api, run.Store, run.Clock);
        var hub = new HubFixture(run);
        hub.Receipt["consumerBindingHash"] = Canonical.Hash(input);
        hub.Sign();
        hub.Queue();
        await provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        run.Api.Reply(404, new());
        run.Api.Reply(404, new());
        var export = (await provisioner.AdvancePrivateCleanup("operator-stop", default)).OwnerHandoff!;
        Assert.Equal(3, export.EvidenceFiles.Count);
        SyntheticPrivateOwnerOffboard(hub, run);
        var foreignVnet = "/subscriptions/" + input.SubscriptionId + "/resourceGroups/primary-workload/providers/Microsoft.Network/virtualNetworks/primary";
        var peerId = hub.HubVnet + "/virtualNetworkPeerings/to-primary";
        var zone = hub.HubGroup + "/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net";
        var link = zone + "/virtualNetworkLinks/consumer-primary";
        var record = zone + "/A/primary";
        JsonObject Resource(string id, JsonObject properties) => new()
        {
            ["id"] = id, ["etag"] = "synthetic-retained-primary", ["location"] = input.Location,
            ["properties"] = properties
        };
        hub.Resources[peerId] = Resource(peerId, new()
        { ["remoteVirtualNetwork"] = new JsonObject { ["id"] = foreignVnet }, ["provisioningState"] = "Succeeded" });
        hub.Resources[zone] = Resource(zone, new() { ["provisioningState"] = "Succeeded" });
        hub.Resources[link] = Resource(link, new()
        { ["virtualNetwork"] = new JsonObject { ["id"] = foreignVnet }, ["provisioningState"] = "Succeeded", ["registrationEnabled"] = false });
        hub.Resources[record] = Resource(record, new()
        {
            ["metadata"] = new JsonObject { ["cohorsConsumer"] = new string('c', 64) },
            ["aRecords"] = new JsonArray(new JsonObject { ["ipv4Address"] = "10.244.6.10" })
        });
        hub.Receipt["dns"]!["zones"] = new JsonArray(new JsonObject { ["name"] = "privatelink.blob.core.windows.net", ["id"] = zone });
        hub.Resources[hub.HubGroup + "/providers/Microsoft.Network/dnsResolverDomainLists/allow"]["properties"]!["domains"] =
            new JsonArray("login.microsoftonline.com");
        hub.Resources[hub.FirewallPolicy + "/ruleCollectionGroups/https"]["properties"]!["ruleCollections"] =
            PrivateForeignFirewallCollection("ApplicationRule", "10.244.1.0/24");
        hub.RefreshHashes();
        var retained = hub.Resources.ToDictionary(pair => pair.Key, pair => Canonical.Hash(pair.Value));
        var before = run.Api.Requests.Count;
        QueuePrivateOwnerReadback(hub, run);
        Assert.Equal("offboarded", (await provisioner.ImportSharedHubReceipt(hub.Receipt, default)).State);
        Assert.All(run.Api.Requests.Skip(before), request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith(hub.HubGroup + "/", request.Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.DoesNotContain("/resourceGroups/primary-workload/", request.Uri.AbsolutePath, StringComparison.Ordinal);
        });
        foreach (var original in retained) Assert.Equal(original.Value, Canonical.Hash(hub.Resources[original.Key]));
        var foreignOwnGroup = new JsonObject
        {
            ["id"] = hub.OwnGroup + "/providers/Microsoft.Storage/storageAccounts/foreign",
            ["type"] = "Microsoft.Storage/storageAccounts",
            ["tags"] = new JsonObject { ["deploymentId"] = "foreign" }
        };
        run.Api.List(foreignOwnGroup);
        var final = await provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("OwnInventoryCleanupBlocked", final.Status);
        Assert.Contains("retained-unbound-or-unsupported-resource:" + foreignOwnGroup["id"]!.GetValue<string>(), final.Blockers);
        Assert.False(final.CompleteC1Cleanup);
        Assert.DoesNotContain(run.Api.Requests.Skip(before), request => request.Method != HttpMethod.Get);
        Assert.Equal(Canonical.Hash(input), run.Store.Current!.BindingHash);
    }

    [Fact]
    public async Task ARelabelledOperatorFirewallDestinationCannotSurviveTheTerminalAck()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        run.Api.Reply(404, new());
        run.Api.Reply(404, new());
        await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        SyntheticPrivateOwnerOffboard(hub, run);
        var collections = PrivateForeignFirewallCollection("NetworkRule", "10.243.0.0/24");
        collections[0]!["rules"]![0]!["destinationAddresses"] = new JsonArray("10.241.6.10");
        hub.Resources[hub.FirewallPolicy + "/ruleCollectionGroups/https"]["properties"]!["ruleCollections"] = collections;
        hub.RefreshHashes();
        QueuePrivateOwnerReadback(hub, run);
        Assert.Equal("c1_hub_egress", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-cleanup-owner-acknowledged"));
    }

    [Fact]
    public async Task OperatorEndpointReceiptArraysAreComparedAsSetsAgainstSortedProviderObservations()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        hub.SeedEndpoints();
        hub.SetState("data-bound", 4);
        var endpoint = hub.Resources[hub.OwnGroup + "/providers/Microsoft.Network/privateEndpoints/" + hub.Name + "-vault-pe"];
        var nic = hub.Resources[endpoint["properties"]!["networkInterfaces"]![0]!["id"]!.GetValue<string>()];
        var first = nic["properties"]!["ipConfigurations"]![0]!.AsObject();
        var originalIp = first["properties"]!["privateIPAddress"]!.GetValue<string>();
        first["properties"]!["privateLinkConnectionProperties"]!["fqdns"] = new JsonArray("a-vault.synthetic.example.invalid");
        var second = (JsonObject)first.DeepClone();
        second["name"] = "second";
        second["properties"]!["privateIPAddress"] = "10.241.6.20";
        second["properties"]!["privateLinkConnectionProperties"]!["fqdns"] = new JsonArray("z-vault.synthetic.example.invalid");
        nic["properties"]!["ipConfigurations"]!.AsArray().Add(second);
        endpoint["properties"]!["customDnsConfigs"] = new JsonArray(
            new JsonObject { ["fqdn"] = "z-vault.synthetic.example.invalid", ["ipAddresses"] = new JsonArray("10.241.6.20") },
            new JsonObject { ["fqdn"] = "a-vault.synthetic.example.invalid", ["ipAddresses"] = new JsonArray(originalIp) });
        hub.Receipt["operatorEndpoints"]![0]!["addresses"] = new JsonArray(originalIp, "10.241.6.20");
        hub.Receipt["operatorEndpoints"]![0]!["fqdns"] = new JsonArray("z-vault.synthetic.example.invalid", "a-vault.synthetic.example.invalid");
        hub.RefreshHashes();
        Assert.Equal("data-bound", (await hub.Import()).State);
    }

    private static void AssertPrivateRetirementProofs(Harness run, FabricC1HubBindingsExport export)
    {
        Assert.Equal(3, export.EvidenceFiles.Count);
        Assert.Equal("offboarded", export.Consumer["state"]!.GetValue<string>());
        foreach (var purpose in new[]
        { "stoppedWorkEvidenceSha256", "credentialsAndGrantsRevokedEvidenceSha256", "spokePeeringAbsentEvidenceSha256" })
        {
            var digest = export.Consumer["offboarding"]![purpose]!.GetValue<string>();
            var bytes = export.EvidenceFiles[digest];
            Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var value = JsonNode.Parse(bytes)!.AsObject();
            Assert.Equal(purpose, value["purpose"]!.GetValue<string>());
            Assert.Equal(FabricC1Deployment.HubPrerequisiteSubject(export.Consumer), value["subjectSha256"]!.GetValue<string>());
            Assert.Equal(Canonical.Hash(run.Input), value["consumerBindingHash"]!.GetValue<string>());
            Assert.Equal(run.Input.SourceCommit, value["sourceCommit"]!.GetValue<string>());
            Assert.Equal(run.Input.Network!.SharedHub!.Generation, value["generation"]!.GetValue<string>());
            Assert.True(value["passed"]!.GetValue<bool>());
            Assert.NotEmpty(value["proof"]!.AsObject());
            Assert.False(value["stopFence"]!["workStopped"]!.GetValue<bool>());
            Assert.True(DateTimeOffset.Parse(value["expiresAtUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) <=
                run.Store.Current!.CleanupStartedAtUtc!.Value.AddSeconds(run.Input.Cleanup!.MaxDurationSeconds!.Value));
        }
    }

    private static JsonObject[] PrivateGroupFixture(HubFixture hub) => hub.Resources
        .Where(pair => pair.Key.StartsWith(hub.OwnGroup + "/", StringComparison.Ordinal))
        .Select(pair =>
        {
            var resource = (JsonObject)pair.Value.DeepClone();
            if (pair.Key.Contains("/virtualNetworkPeerings/", StringComparison.Ordinal))
                resource["type"] = "Microsoft.Network/virtualNetworks/virtualNetworkPeerings";
            return resource;
        }).ToArray();

    private static void QueuePrivatePeerReadback(HubFixture hub, Harness run)
    {
        var peer = hub.Resources[hub.SpokeVnet + "/virtualNetworkPeerings/to-shared-hub"];
        run.Api.Reply(200, peer);
        run.Api.Reply(200, hub.Resources[hub.SpokeVnet]);
        run.Api.List(peer);
    }

    private static void SyntheticPrivateOwnerOffboard(HubFixture hub, Harness run)
    {
        hub.Receipt["state"] = "offboarded";
        hub.Receipt["revision"] = hub.Receipt["revision"]!.GetValue<int>() + 1;
        hub.Receipt["peering"] = null;
        hub.Receipt["dns"]!["recordSets"] = new JsonArray();
        hub.Receipt["egress"]!["httpsRules"] = new JsonArray();
        hub.Receipt["operatorEndpoints"] = new JsonArray();
        hub.Receipt["web"] = null;
        hub.Receipt["readback"]!["observedAtUtc"] = run.Clock.GetUtcNow().ToString("O");
        hub.Resources.Remove(hub.SpokeVnet + "/virtualNetworkPeerings/to-shared-hub");
        hub.Resources.Remove(hub.HubVnet + "/virtualNetworkPeerings/to-fabric");
        foreach (var suffix in new[] { "https", "private" })
            hub.Resources[hub.FirewallPolicy + "/ruleCollectionGroups/" + suffix]["properties"]!["ruleCollections"] = new JsonArray();
        hub.RefreshHashes();
    }

    private static void QueuePrivateOwnerReadback(HubFixture hub, Harness run)
    {
        foreach (var observation in hub.Receipt["readback"]!["resources"]!.AsArray())
            run.Api.Reply(200, hub.Resources[observation!["id"]!.GetValue<string>()]);
        var dns = hub.Receipt["hub"]!["dnsPolicyId"]!.GetValue<string>();
        foreach (var prefix in new[]
        { hub.FirewallPolicy + "/ruleCollectionGroups/", dns + "/dnsSecurityRules/", dns + "/virtualNetworkLinks/" })
            run.Api.List(hub.Resources.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(pair => pair.Value).ToArray());
        run.Api.List(hub.Resources.Where(pair => pair.Key.StartsWith(hub.HubVnet + "/virtualNetworkPeerings/", StringComparison.Ordinal))
            .Select(pair => pair.Value).ToArray());
        foreach (var zone in hub.Receipt["dns"]!["zones"]!.AsArray())
            foreach (var collection in new[] { "virtualNetworkLinks", "A" })
            {
                var prefix = zone!["id"]!.GetValue<string>() + "/" + collection + "/";
                run.Api.List(hub.Resources.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(pair => pair.Value).ToArray());
            }
    }

    private static JsonArray PrivateForeignFirewallCollection(string type, string source) => new(new JsonObject
    {
        ["priority"] = 200, ["ruleCollectionType"] = "FirewallPolicyFilterRuleCollection",
        ["action"] = new JsonObject { ["type"] = "Allow" }, ["rules"] = new JsonArray(new JsonObject
        {
            ["name"] = "retained-primary", ["ruleType"] = type, ["sourceAddresses"] = new JsonArray(source),
            ["targetFqdns"] = new JsonArray("login.microsoftonline.com"),
            ["protocols"] = new JsonArray(new JsonObject { ["protocolType"] = "Https", ["port"] = 443 }), ["terminateTLS"] = false
        })
    });

    private static JsonObject PrivateVmPower(string power) => new()
    { ["statuses"] = new JsonArray(new JsonObject { ["code"] = "PowerState/" + power }) };

    private static void QueuePrivateProviderFailure(FakeApi api, string response)
    {
        if (response is "http-403" or "legacy-http-403" or "http-503")
        {
            api.Reply(response == "http-503" ? 503 : 403, new());
            return;
        }
        api.Fail(response switch
        {
            "http-request" => new HttpRequestException("synthetic lost provider response"),
            "io" => new IOException("synthetic incomplete provider response body"),
            "timeout" => new TaskCanceledException("synthetic HttpClient timeout"),
            "request-cancelled" => new OperationCanceledException("synthetic request budget cancellation", new CancellationToken(canceled: true)),
            "response-shape" => new DomainException("c1_deploy_response_shape", "synthetic malformed provider body", 409),
            "response-size" => new DomainException("c1_deploy_response_size", "synthetic oversized provider response", 409),
            "response-limit" => new DomainException("c1_response_limit", "synthetic chunked response limit", 409),
            _ => throw new InvalidOperationException("Unknown synthetic provider failure.")
        });
    }

    private static string PrivateProviderFailureBlocker(string response, string key) =>
        response is "http-403" or "legacy-http-403" or "http-503"
            ? "provider-response-rejected:" + key + ":c1_deploy_http_" + (response == "http-503" ? "503" : "403")
            : "provider-response-unresolved:" + key;

    private static (JsonObject Vm, JsonObject Capacity) SeedPrivatePaidFoundation(Harness run, HubFixture hub)
    {
        var request = run.Provisioner.PrepareSharedHubStage("foundation");
        var vm = new JsonObject
        {
            ["id"] = hub.OwnGroup + "/providers/Microsoft.Compute/virtualMachines/" + hub.Name + "-probe",
            ["type"] = "Microsoft.Compute/virtualMachines", ["location"] = run.Input.Location,
            ["tags"] = FabricC1Deployment.Tags(run.Input), ["etag"] = "synthetic-vm-etag",
            ["identity"] = new JsonObject { ["type"] = "SystemAssigned", ["principalId"] = Guid.NewGuid().ToString("D"), ["tenantId"] = run.Input.TenantId },
            ["properties"] = new JsonObject { ["vmId"] = Guid.NewGuid().ToString("D"), ["provisioningState"] = "Succeeded" }
        };
        var capacity = new JsonObject
        {
            ["id"] = hub.OwnGroup + "/providers/Microsoft.Fabric/capacities/" + hub.Name + "cap",
            ["type"] = "Microsoft.Fabric/capacities", ["location"] = run.Input.Location,
            ["tags"] = FabricC1Deployment.Tags(run.Input), ["etag"] = "synthetic-capacity-etag",
            ["systemData"] = new JsonObject { ["createdAt"] = run.Clock.GetUtcNow().ToString("O"), ["createdBy"] = run.Input.BootstrapIdentity!.PrincipalObjectId },
            ["sku"] = new JsonObject { ["name"] = "F2", ["tier"] = "Fabric" },
            ["properties"] = new JsonObject
            {
                ["state"] = "Active", ["provisioningState"] = "Succeeded",
                ["administration"] = new JsonObject { ["members"] = new JsonArray("synthetic-admin@example.invalid") }
            }
        };
        var body = new JsonObject { ["properties"] = new JsonObject
        { ["mode"] = "Incremental", ["template"] = request.Template.DeepClone(), ["parameters"] = request.Parameters.DeepClone() } };
        run.Seed("arm-foundation", "ArmDeployment", new()
        {
            ["id"] = hub.OwnGroup + "/providers/Microsoft.Resources/deployments/c1-foundation",
            ["properties"] = new JsonObject
            {
                ["provisioningState"] = "Succeeded", ["outputs"] = new JsonObject
                {
                    ["probe"] = new JsonObject { ["value"] = vm.DeepClone() },
                    ["capacity"] = new JsonObject { ["value"] = capacity.DeepClone() }
                }
            }
        }, Canonical.Hash(body));
        return (vm, capacity);
    }
}
