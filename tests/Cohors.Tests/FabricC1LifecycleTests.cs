using System.Text.Json.Nodes;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed partial class FabricC1DeploymentTests
{
    [Theory]
    [InlineData("foundation")]
    [InlineData("private-endpoints")]
    public async Task PrivateApplyComposesOriginalNetworkEndpointsDnsAndSourceBootstrapAcrossLostStageReplies(string lostStage)
    {
        using var run = new Harness(sharedHub: true, delegated: true, maxRequests: 1000, cleanupReserve: 200);
        var hub = new HubFixture(run, registrationDns: true);
        var provider = new HubApplyProvider(run, hub) { LoseArmResponseStage = lostStage };
        run.Api.Responder = provider.Send;
        var dns = new HubDnsDouble((_, question) =>
        {
            var selection = hub.RegistrationSelections().Single(item =>
                FabricC1HubDns.CnameQuestion(item.Fqdn).AsSpan(12).SequenceEqual(question.AsSpan(12)));
            return HubCnameAnswer(question, selection.Name + "." + selection.Zone);
        });
        var boundClients = 0;
        void BindClients(IReadOnlyDictionary<string, string[]> routes)
        {
            Assert.Equal(3, routes.Count);
            foreach (var role in new[] { "agent", "released", "denied" })
                Assert.Single(routes[FabricC1Provisioner.WorkspaceHost(run.Store.Current!.Objects["workspace-" + role].Id)]);
            boundClients++;
        }
        await run.Provisioner.EnsureApplicationForRole("runtime", default);
        await run.Provisioner.EnsurePrincipalForRole("runtime", default);
        await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        Assert.Equal("AwaitingOwnerPeeredReceipt",
            (await run.Provisioner.AdvancePrivateDeployment(null, dns, BindClients, default)).Status);
        hub.SetState("peered", 2);
        await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        Assert.Equal("AwaitingOwnerConnectedReceipt",
            (await run.Provisioner.AdvancePrivateDeployment(null, dns, BindClients, default)).Status);
        hub.SetState("connected", 3);
        await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.AdvancePrivateDeployment(null, dns, BindClients, default));
        var originalStart = run.Store.Current!.StartedAtUtc;
        Assert.Contains("arm-" + lostStage, run.Store.Current.PendingCreates.Keys);
        run.Restart();
        Assert.Equal("AwaitingExactProviderDnsRegistration",
            (await run.Provisioner.AdvancePrivateDeployment(null, dns, BindClients, default)).Status);
        Assert.Equal(0, boundClients);
        var handoff = await run.Provisioner.AdvancePrivateDeployment(hub.RegistrationSelections(), dns, BindClients, default);
        Assert.Equal("AwaitingOwnerDataBoundReceipt", handoff.Status);
        var exported = run.Provisioner.RestoreSharedHubBindingsExport();
        Assert.True(JsonNode.DeepEquals(handoff.OwnerConsumerSlot, exported.Consumer));
        Assert.Equal(7, exported.EvidenceFiles.Count);
        Assert.Equal(3, dns.Calls.Count);
        provider.AcknowledgeData(exported.Consumer);
        await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        var sharedBefore = provider.SharedNetworkHash();
        for (var index = 0; index < 3; index++)
        {
            var progress = await run.Provisioner.AdvancePrivateDeployment(null, dns, BindClients, default);
            Assert.False(progress.CompleteC1Deployment);
            if (index < 2)
            {
                Assert.Equal("AwaitingOriginalSourceJob-" + (index == 0 ? "released" : "denied"), progress.Status);
                Assert.NotNull(progress.NextPollAtUtc);
                run.Clock.Advance(TimeSpan.FromSeconds(60));
                run.Restart();
            }
            else Assert.Equal("AwaitingEffectiveSourceAndHostedRuntimeBindings", progress.Status);
        }
        Assert.Equal(3, boundClients);
        Assert.Equal(originalStart, run.Store.Current!.StartedAtUtc);
        Assert.Contains("materialization", run.Store.Current.Objects.Keys);
        Assert.Contains("source-access-metadata", run.Store.Current.Objects.Keys);
        Assert.DoesNotContain("source-grants", run.Store.Current.Objects.Keys);
        foreach (var stage in new[] { "hub-spoke", "hub-peer", "hub-transit", "foundation", "private-endpoints", "hub-operator-access" })
            Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put &&
                request.Uri.AbsolutePath.EndsWith("/deployments/c1-" + stage, StringComparison.Ordinal));
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Post &&
            request.Uri.AbsolutePath.EndsWith("/jobs/execute/instances", StringComparison.Ordinal)));
        Assert.Equal(sharedBefore, provider.SharedNetworkHash());
        Assert.Equal(provider.OriginalForeignHash, Canonical.Hash(provider.ForeignResources));
        Assert.DoesNotContain(run.Api.Requests, request => request.Uri.IdnHost == "management.azure.com" &&
            request.Method != HttpMethod.Get && !request.Uri.AbsolutePath.StartsWith(hub.OwnGroup, StringComparison.Ordinal));
        Assert.Empty(run.Store.Current.PendingCreates);
        Assert.Empty(run.Store.Current.PendingOperations);
        var cleanupStart = run.Clock.GetUtcNow();
        var stopping = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingVerifiedWorkStop", stopping.Status);
        Assert.Equal(cleanupStart.AddSeconds(5), stopping.NextPollAtUtc);
        Assert.Null(stopping.OwnerHandoff);
        run.Clock.Advance(TimeSpan.FromSeconds(5));
        run.Restart();
        var stopped = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingEffectiveCredentialAndGrantRevocation", stopped.Status);
        Assert.Contains(stopped.Blockers, value => value.StartsWith("effective-revocation-not-proven:", StringComparison.Ordinal));
        foreach (var role in new[] { "released", "denied" })
            Assert.Equal("SourceWorkspaceAbsent", run.Store.Current!.CleanupReceipts["workspace-" + role].Kind);
        Assert.NotNull(run.Store.Current!.Objects.GetValueOrDefault("private-cleanup-work-stopped"));
        Assert.Null(stopped.OwnerHandoff);
        Assert.False(stopped.CompleteC1Cleanup);
        Assert.DoesNotContain("private-cleanup-credentials-revoked", run.Store.Current.Objects.Keys);
        Assert.Equal(cleanupStart, run.Store.Current.CleanupStartedAtUtc);
        Assert.Equal(originalStart, run.Store.Current.StartedAtUtc);
        run.Restart();
        Assert.Equal(stopped.Status, (await run.Provisioner.AdvancePrivateCleanup("operator-stop", default)).Status);
        Assert.Single(run.Api.Requests, request => request.Uri.AbsolutePath.EndsWith("/deallocate", StringComparison.Ordinal));
        Assert.Single(run.Api.Requests, request => request.Uri.AbsolutePath.EndsWith("/suspend", StringComparison.Ordinal));
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Delete));
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete &&
            request.Uri.AbsolutePath.Contains("virtualNetworkPeerings", StringComparison.Ordinal));
        Assert.Equal(sharedBefore, provider.SharedNetworkHash());
        Assert.Equal(provider.OriginalForeignHash, Canonical.Hash(provider.ForeignResources));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposedSpokeApplyResumesBetweenStagesWithoutRecreatingOrOwningTheHub(bool lostPeerResponse)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000, cleanupReserve: 200);
        var hub = new HubFixture(run);
        var provider = new HubApplyProvider(run, hub) { LosePeerResponse = lostPeerResponse, FailBeforePeer = !lostPeerResponse };
        run.Api.Responder = provider.Send;
        await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        var dns = new HubDnsDouble((_, _) => throw new InvalidOperationException("No DNS before endpoint creation."));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.AdvanceSharedHubNetwork(null, dns, default));
        Assert.Contains("arm-hub-spoke", run.Store.Current!.Objects.Keys);
        Assert.Equal(lostPeerResponse, run.Store.Current.PendingCreates.ContainsKey("arm-hub-peer"));
        var originalStart = run.Store.Current.StartedAtUtc;
        var count = run.Store.Current.Requests;
        run.Clock.Advance(TimeSpan.FromSeconds(2));
        run.Restart();
        var peered = await run.Provisioner.AdvanceSharedHubNetwork(null, dns, default);
        Assert.Equal("AwaitingOwnerPeeredReceipt", peered.Status);
        Assert.Equal("peered", peered.OwnerConsumerSlot!["state"]!.GetValue<string>());
        Assert.False(peered.CompleteC1Deployment);
        var requestCount = run.Api.Requests.Count;
        var originalHandoff = run.Provisioner.RestoreSharedHubHandshakeExport();
        Assert.True(JsonNode.DeepEquals(peered.OwnerConsumerSlot, originalHandoff.Consumer));
        Assert.Empty(originalHandoff.EvidenceFiles);
        Assert.Equal(requestCount, run.Api.Requests.Count);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put &&
            request.Uri.AbsolutePath.EndsWith("/deployments/c1-hub-spoke", StringComparison.Ordinal));
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put &&
            request.Uri.AbsolutePath.EndsWith("/deployments/c1-hub-peer", StringComparison.Ordinal));
        Assert.Equal(originalStart, run.Store.Current!.StartedAtUtc);
        Assert.True(run.Store.Current.Requests > count);
        var peerId = hub.SpokeVnet + "/virtualNetworkPeerings/to-shared-hub";
        Assert.False(hub.Resources[peerId]["properties"]!["useRemoteGateways"]!.GetValue<bool>());
        hub.SetState("peered", 2);
        await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        var connected = await run.Provisioner.AdvanceSharedHubNetwork(null, dns, default);
        Assert.Equal("AwaitingOwnerConnectedReceipt", connected.Status);
        Assert.True(hub.Resources[peerId]["properties"]!["useRemoteGateways"]!.GetValue<bool>());
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put &&
            request.Uri.AbsolutePath.EndsWith("/deployments/c1-hub-transit", StringComparison.Ordinal));
        hub.SetState("connected", 3);
        var accepted = await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        Assert.Equal("connected", accepted.State);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method != HttpMethod.Get &&
            !request.Uri.AbsolutePath.StartsWith(hub.OwnGroup, StringComparison.Ordinal));
        Assert.DoesNotContain(run.Api.Requests, request => request.Uri.AbsolutePath.Contains("primary", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(provider.OriginalForeignHash, Canonical.Hash(provider.ForeignResources));
        Assert.DoesNotContain(run.Store.Current.Objects.Keys, key => key.Contains("vpn", StringComparison.Ordinal));
        var sharedBefore = provider.SharedNetworkHash();
        provider.LosePeerDeleteResponse = lostPeerResponse;
        if (lostPeerResponse)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.AdvancePrivateCleanup("operator-stop", default));
            Assert.Contains("private-cleanup-spoke-peer-absent", run.Store.Current!.PendingDeletes.Keys);
            run.Restart();
        }
        var offboarding = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("AwaitingOwnerOffboardedReceipt", offboarding.Status);
        AssertPrivateRetirementProofs(run, offboarding.OwnerHandoff!);
        Assert.Equal(sharedBefore, provider.SharedNetworkHash());
        var requestsBeforeResume = run.Api.Requests.Count;
        run.Restart();
        var repeated = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal(requestsBeforeResume, run.Api.Requests.Count);
        foreach (var file in offboarding.OwnerHandoff!.EvidenceFiles)
            Assert.Equal(file.Value, repeated.OwnerHandoff!.EvidenceFiles[file.Key]);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
        SyntheticPrivateOwnerOffboard(hub, run);
        var acknowledged = await run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default);
        Assert.Equal("offboarded", acknowledged.State);
        var afterOwner = provider.SharedNetworkHash();
        var inventory = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("OwnInventoryCleanupBlocked", inventory.Status);
        Assert.Contains(inventory.Blockers, value => value.StartsWith("retained-unbound-or-unsupported-resource:", StringComparison.Ordinal));
        Assert.False(inventory.CompleteC1Cleanup);
        Assert.Equal(afterOwner, provider.SharedNetworkHash());
        Assert.Equal(provider.OriginalForeignHash, Canonical.Hash(provider.ForeignResources));
        Assert.DoesNotContain(run.Api.Requests, request => request.Method != HttpMethod.Get &&
            !request.Uri.AbsolutePath.StartsWith(hub.OwnGroup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EarlyIdentityOnlyStopNeedsNoInventedOwnerReceiptAndResumesWithoutNewMutations()
    {
        using var run = new Harness(sharedHub: true, delegated: true, maxRequests: 500, cleanupReserve: 100);
        var hub = new HubFixture(run);
        var provider = new HubApplyProvider(run, hub);
        run.Api.Responder = provider.Send;
        await run.Provisioner.EnsureApplicationForRole("runtime", default);
        await run.Provisioner.EnsurePrincipalForRole("runtime", default);
        var started = run.Store.Current!.StartedAtUtc;
        var stopped = await run.Provisioner.AdvancePrivateCleanup("operator-stop", default);
        Assert.Equal("OwnIdentityInventoryAbsentBeforeHubBinding", stopped.Status);
        Assert.Null(stopped.OwnerHandoff);
        Assert.False(stopped.CompleteC1Cleanup);
        Assert.DoesNotContain("shared-hub-receipt", run.Store.Current!.Objects.Keys);
        Assert.DoesNotContain("private-cleanup-offboarding-export", run.Store.Current.Objects.Keys);
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Delete));
        var cleanupStart = run.Store.Current.CleanupStartedAtUtc;
        run.Restart();
        Assert.Equal(stopped.Status, (await run.Provisioner.AdvancePrivateCleanup("operator-stop", default)).Status);
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Delete));
        Assert.Equal(started, run.Store.Current!.StartedAtUtc);
        Assert.Equal(cleanupStart, run.Store.Current.CleanupStartedAtUtc);
        Assert.All(run.Api.Requests, request => Assert.Equal("graph.microsoft.com", request.Uri.IdnHost));
        Assert.Equal(provider.OriginalForeignHash, Canonical.Hash(provider.ForeignResources));
    }

    [Fact]
    public async Task IndependentDeadlineWatcherReloadsTheOriginalClockRatherThanStartingAnotherWindow()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000, manualTimers: true);
        var start = run.Clock.GetUtcNow();
        run.Store.Save(new(Canonical.Hash(run.Input), new(), new()) { Requests = 1, StartedAtUtc = start });
        var watching = FabricC1Deployment.FenceAtOriginalDeadline(
            () => new(run.Input, run.Api, run.Store, run.Clock), run.Clock, default);
        Assert.False(watching.IsCompleted);
        run.Clock.Advance(TimeSpan.FromSeconds(run.Input.MaxControlDurationSeconds!.Value));
        var result = await watching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("StopFencedAwaitingOrderedCleanup", result.Status);
        Assert.Equal(start, run.Store.Current!.StartedAtUtc);
        Assert.Equal(start.AddSeconds(600), run.Store.Current.CleanupStartedAtUtc);
        Assert.Empty(run.Api.Requests);
        var stop = run.Store.Current.Objects["private-lifecycle-stop"].Response;
        Assert.Equal("original-deadline", stop["reason"]!.GetValue<string>());
        Assert.False(stop["workStopped"]!.GetValue<bool>());
        run.Restart();
        Assert.Equal("c1_deploy_cleanup_started",
            (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.AdvanceSharedHubNetwork(null,
                new HubDnsDouble((_, _) => throw new InvalidOperationException()), default))).Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedWorkspaceDenyPutIsReconciledWithoutRepeatingTheMutation(bool applied)
    {
        using var run = new Harness();
        run.Seed("workspace-agent", "Workspace", run.Workspace("agent"));
        var before = new JsonObject { ["future"] = new JsonObject { ["keep"] = true } };
        var after = FabricC1Deployment.LockCommunicationPolicy(before);
        run.Api.List(InboundSetting(true));
        run.Api.Reply(200, before, "\"original\"");
        run.Api.Fail(new HttpRequestException("Synthetic lost full-policy PUT response."));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.DenyWorkspacePublicAccess("agent", default));
        Assert.Contains("policy-agent", run.Store.Current!.PendingCreates.Keys);
        run.Restart();
        run.Api.List(InboundSetting(true));
        run.Api.Reply(200, applied ? after : before, "\"observed\"");
        if (applied)
        {
            await run.Provisioner.DenyWorkspacePublicAccess("agent", default);
            Assert.DoesNotContain("policy-agent", run.Store.Current!.PendingCreates.Keys);
        }
        else Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DenyWorkspacePublicAccess("agent", default))).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put);
    }

    private sealed class HubApplyProvider(Harness run, HubFixture hub)
    {
        private readonly Dictionary<string, JsonObject> deployments = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> workload = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> graph = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> fabric = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> policies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> jobs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> jobReadbacks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> deltaIds = new(StringComparer.Ordinal);
        private JsonObject? group;
        public bool LosePeerResponse { get; set; }
        public bool FailBeforePeer { get; set; }
        public string? LoseArmResponseStage { get; set; }
        public bool LosePeerDeleteResponse { get; set; }
        private bool probeDeallocated;
        public JsonObject ForeignResources { get; } = new()
        {
            ["otherConsumer"] = new JsonObject { ["identity"] = "synthetic-foreign-consumer", ["unchanged"] = true },
            ["hubAudience"] = new JsonObject { ["assignmentRequired"] = true, ["unchangedUsers"] = 4 }
        };
        public string OriginalForeignHash { get; } = Canonical.Hash(new JsonObject
        {
            ["otherConsumer"] = new JsonObject { ["identity"] = "synthetic-foreign-consumer", ["unchanged"] = true },
            ["hubAudience"] = new JsonObject { ["assignmentRequired"] = true, ["unchangedUsers"] = 4 }
        });

        public FabricC1DeploymentReply Send(Request request)
        {
            if (request.Uri.IdnHost == "graph.microsoft.com") return Graph(request);
            if (request.Uri.IdnHost != "management.azure.com") return Fabric(request);
            Assert.Equal("management.azure.com", request.Uri.IdnHost);
            var id = request.Uri.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (id == hub.OwnGroup) return Reply(group);
                if (id.EndsWith("/instanceView", StringComparison.Ordinal))
                {
                    Assert.True(workload.ContainsKey(id[..^"/instanceView".Length]));
                    return Reply(PrivateVmPower(probeDeallocated ? "deallocated" : "running"));
                }
                if (id == hub.OwnGroup + "/resources")
                    return List(hub.Resources.Concat(workload).Where(item => item.Key.StartsWith(hub.OwnGroup + "/", StringComparison.Ordinal))
                        .Select(item => item.Value));
                if (id.EndsWith("/deployments/c1-hub-peer", StringComparison.Ordinal) && FailBeforePeer)
                {
                    FailBeforePeer = false;
                    throw new HttpRequestException("Synthetic interruption between spoke and peer.");
                }
                if (id.Contains("/providers/Microsoft.Resources/deployments/", StringComparison.Ordinal))
                    return Reply(deployments.GetValueOrDefault(id));
                if (hub.Resources.TryGetValue(id, out var resource)) return Reply(resource);
                if (workload.TryGetValue(id, out var own)) return Reply(own);
                if (id == hub.OwnGroup + "/providers/Microsoft.Network/privateEndpoints")
                    return List(hub.Resources.Values.Where(item => item["type"]?.GetValue<string>() == "Microsoft.Network/privateEndpoints"));
                if (id.EndsWith("/ruleCollectionGroups", StringComparison.Ordinal) ||
                    id.EndsWith("/dnsSecurityRules", StringComparison.Ordinal) ||
                    id.EndsWith("/virtualNetworkLinks", StringComparison.Ordinal) ||
                    id.EndsWith("/virtualNetworkPeerings", StringComparison.Ordinal) ||
                    id.EndsWith("/A", StringComparison.Ordinal))
                    return List(hub.Resources.Where(item => item.Key.StartsWith(id + "/", StringComparison.Ordinal)).Select(item => item.Value));
                if (id == hub.SpokeVnet + "/virtualNetworkPeerings/to-shared-hub") return new(404, new());
                throw new InvalidOperationException("Unexpected synthetic GET: " + id);
            }
            if (request.Method == HttpMethod.Post)
            {
                Assert.StartsWith(hub.OwnGroup + "/", id, StringComparison.Ordinal);
                if (id.EndsWith("/deallocate", StringComparison.Ordinal))
                {
                    Assert.False(probeDeallocated);
                    probeDeallocated = true;
                }
                else
                {
                    Assert.EndsWith("/suspend", id, StringComparison.Ordinal);
                    workload[id[..^"/suspend".Length]]["properties"]!["state"] = "Suspended";
                }
                return new(202, new()) { RetryAfterSeconds = 5 };
            }
            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal(hub.SpokeVnet + "/virtualNetworkPeerings/to-shared-hub", id);
                Assert.NotNull(request.ETag);
                Assert.True(hub.Resources.Remove(id));
                if (LosePeerDeleteResponse)
                {
                    LosePeerDeleteResponse = false;
                    throw new HttpRequestException("Synthetic lost own peer DELETE acknowledgement.");
                }
                return new(204, new());
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.StartsWith(hub.OwnGroup, id, StringComparison.Ordinal);
            if (id == hub.OwnGroup)
            {
                Assert.Null(group);
                group = (JsonObject)request.Body!.DeepClone();
                group["id"] = id;
                return Reply(group, 201);
            }
            Assert.False(deployments.ContainsKey(id), "No ARM deployment PUT may be replayed.");
            var stage = id.Split('/')[^1]["c1-".Length..];
            var outputs = new JsonObject();
            if (stage == "hub-spoke")
            {
                hub.SeedSpoke(record: false); // Provider metadata only: no consumer journal is seeded.
                outputs["vnet"] = new JsonObject { ["value"] = hub.Resources[hub.SpokeVnet].DeepClone() };
            }
            else if (stage == "foundation") outputs = Foundation();
            else if (stage == "private-endpoints")
            {
                outputs["privateEndpoints"] = new JsonObject
                { ["value"] = hub.CreateEndpointResources(request.Body!["properties"]!["parameters"]!.AsObject(), workspaceDns: true) };
                hub.Resources[hub.SpokeVnet]["properties"]!["subnets"]![0]!["properties"]!["syntheticProviderAttachment"] = "original-created-endpoints";
            }
            else if (stage == "hub-operator-access")
            {
                var parameters = request.Body!["properties"]!["parameters"]!;
                var nsg = request.Body["properties"]!["template"]!["resources"]![0]!;
                var raw = nsg["properties"]!.ToJsonString();
                foreach (var (expression, replacement) in new Dictionary<string, JsonNode>
                {
                    ["[parameters('network').operatorEndpointPrefix]"] = parameters["network"]!["value"]!["operatorEndpointPrefix"]!,
                    ["[parameters('network').clientPrefix]"] = parameters["network"]!["value"]!["clientPrefix"]!,
                    ["[parameters('network').agentPrefix]"] = parameters["network"]!["value"]!["agentPrefix"]!,
                    ["[parameters('vpnClientPrefix')]"] = parameters["vpnClientPrefix"]!["value"]!,
                    ["[parameters('operatorAddresses')]"] = parameters["operatorAddresses"]!["value"]!
                })
                    raw = raw.Replace(System.Text.Json.JsonSerializer.Serialize(expression), replacement.ToJsonString(), StringComparison.Ordinal);
                var nsgId = hub.OwnGroup + "/providers/Microsoft.Network/networkSecurityGroups/" + hub.Name + "-operator-endpoint-nsg";
                hub.Resources[nsgId]["properties"] = JsonNode.Parse(raw);
                hub.Resources[nsgId]["properties"]!["provisioningState"] = "Succeeded";
                outputs["operatorNsg"] = new JsonObject { ["value"] = hub.Resources[nsgId].DeepClone() };
            }
            else
            {
                Assert.Contains(stage, new[] { "hub-peer", "hub-transit" });
                var remote = request.Body!["properties"]!["parameters"]!["useRemoteGateways"]!["value"]!.GetValue<bool>();
                Assert.Equal(stage == "hub-transit", remote);
                if (remote) Assert.True(hub.Resources.ContainsKey(hub.HubVnet + "/virtualNetworkPeerings/to-fabric"));
                var peerId = hub.SpokeVnet + "/virtualNetworkPeerings/to-shared-hub";
                var peer = new JsonObject
                {
                    ["id"] = peerId, ["type"] = "Microsoft.Network/virtualNetworks/virtualNetworkPeerings",
                    ["etag"] = "synthetic-peer-etag", ["properties"] = new JsonObject
                    {
                        ["provisioningState"] = "Succeeded", ["remoteVirtualNetwork"] = new JsonObject { ["id"] = hub.HubVnet },
                        ["allowVirtualNetworkAccess"] = true, ["allowForwardedTraffic"] = true,
                        ["allowGatewayTransit"] = false, ["useRemoteGateways"] = remote,
                        ["peeringState"] = remote ? "Connected" : "Initiated"
                    }
                };
                hub.Resources[peerId] = peer;
                outputs["peering"] = new JsonObject { ["value"] = peer.DeepClone() };
            }
            var deployment = new JsonObject
            {
                ["id"] = id, ["properties"] = new JsonObject
                {
                    ["mode"] = "Incremental", ["parameters"] = request.Body!["properties"]!["parameters"]!.DeepClone(),
                    ["correlationId"] = Guid.NewGuid().ToString("D"),
                    ["templateHash"] = Canonical.Hash(request.Body["properties"]!["template"]),
                    ["provisioningState"] = "Succeeded", ["outputs"] = outputs
                }
            };
            deployments[id] = deployment;
            if (stage == "hub-peer" && LosePeerResponse || stage == LoseArmResponseStage)
            {
                LosePeerResponse = false;
                LoseArmResponseStage = null;
                throw new HttpRequestException("Synthetic response loss after the provider created the original peer.");
            }
            return Reply(deployment, 201);
        }

        private JsonObject Foundation()
        {
            var vm = hub.OwnGroup + "/providers/Microsoft.Compute/virtualMachines/" + hub.Name + "-probe";
            var disk = hub.OwnGroup + "/providers/Microsoft.Compute/disks/synthetic-generated-os";
            AddWorkload(vm, "Microsoft.Compute/virtualMachines", new()
            {
                ["vmId"] = Guid.NewGuid().ToString("D"),
                ["storageProfile"] = new JsonObject { ["osDisk"] = new JsonObject
                { ["managedDisk"] = new JsonObject { ["id"] = disk }, ["deleteOption"] = "Delete" } }
            });
            AddWorkload(disk, "Microsoft.Compute/disks", new() { ["uniqueId"] = Guid.NewGuid().ToString("D") });
            workload[disk]["managedBy"] = vm;
            workload[disk]["tags"] = new JsonObject();
            var capacity = hub.OwnGroup + "/providers/Microsoft.Fabric/capacities/" + hub.Name + "cap";
            AddWorkload(capacity, "Microsoft.Fabric/capacities", new()
            {
                ["state"] = "Active",
                ["administration"] = new JsonObject
                { ["members"] = new JsonArray(run.Input.CapacityAdministrators!.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()) }
            });
            workload[capacity]["sku"] = new JsonObject { ["name"] = "F2", ["tier"] = "Fabric" };
            workload[capacity]["systemData"] = new JsonObject
            { ["createdAt"] = run.Clock.GetUtcNow().ToString("O"), ["createdBy"] = run.Input.BootstrapIdentity!.PrincipalObjectId };
            return new()
            {
                ["probe"] = new JsonObject { ["value"] = workload[vm].DeepClone() },
                ["capacity"] = new JsonObject { ["value"] = workload[capacity].DeepClone() }
            };
        }

        private void AddWorkload(string id, string type, JsonObject properties)
        {
            properties["provisioningState"] = "Succeeded";
            workload.Add(id, new()
            {
                ["id"] = id, ["type"] = type, ["location"] = run.Input.Location,
                ["tags"] = FabricC1Deployment.Tags(run.Input), ["etag"] = "\"synthetic-owned\"",
                ["properties"] = properties
            });
        }

        public void AcknowledgeData(JsonObject consumer)
        {
            hub.SetState("data-bound", 4);
            hub.Receipt["operatorEndpoints"] = consumer["operatorEndpoints"]!.DeepClone();
            var ids = new JsonArray();
            foreach (var record in consumer["dnsRecords"]!.AsArray())
            {
                var id = hub.HubGroup + "/providers/Microsoft.Network/privateDnsZones/" +
                    record!["zone"]!.GetValue<string>() + "/A/" + record["name"]!.GetValue<string>();
                ids.Add(id);
                hub.Resources.Add(id, new()
                {
                    ["id"] = id, ["type"] = "Microsoft.Network/privateDnsZones/A", ["location"] = "global",
                    ["etag"] = "synthetic-dns-etag", ["properties"] = new JsonObject
                    {
                        ["ttl"] = 300, ["metadata"] = new JsonObject
                        { ["cohorsBinding"] = run.Input.Network!.SharedHub!.HubBindingHash, ["cohorsConsumer"] = Canonical.Hash(run.Input) },
                        ["aRecords"] = new JsonArray(record["addresses"]!.AsArray().Select(address => (JsonNode)new JsonObject
                        { ["ipv4Address"] = address!.DeepClone() }).ToArray())
                    }
                });
            }
            hub.Receipt["dns"]!["recordSets"] = ids;
            hub.RefreshHashes();
        }

        public string SharedNetworkHash() => Canonical.Hash(new JsonObject(hub.Resources
            .Where(item => item.Key.StartsWith(hub.HubGroup + "/", StringComparison.Ordinal))
            .Select(item => KeyValuePair.Create(item.Key, (JsonNode?)item.Value.DeepClone()))));

        private FabricC1DeploymentReply Graph(Request request)
        {
            var path = request.Uri.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("/federatedIdentityCredentials", StringComparison.Ordinal) ||
                    path.EndsWith("/ownedObjects", StringComparison.Ordinal)) return List([]);
                if (path.EndsWith("/owners", StringComparison.Ordinal))
                    return List([new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId }]);
                if (path.Split('/').Length == 3)
                    return List(graph.Where(item => item.Key.StartsWith(path + "/", StringComparison.Ordinal)).Select(item => item.Value));
                return Reply(graph.GetValueOrDefault(path));
            }
            if (request.Method == HttpMethod.Delete)
            {
                Assert.True(graph.Remove(path));
                return new(204, new());
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains(path, new[] { "/v1.0/applications", "/v1.0/servicePrincipals" });
            var created = (JsonObject)request.Body!.DeepClone();
            created["id"] = Guid.NewGuid().ToString("D");
            if (path == "/v1.0/applications") created["appId"] = Guid.NewGuid().ToString("D");
            created["keyCredentials"] ??= new JsonArray();
            created["passwordCredentials"] ??= new JsonArray();
            graph.Add(path + "/" + created["id"]!.GetValue<string>(), created);
            return Reply(created, 201);
        }

        private FabricC1DeploymentReply Fabric(Request request)
        {
            var path = request.Uri.AbsolutePath;
            if (path == "/v1/admin/tenantsettings") return List([InboundSetting(true)]);
            if (path == "/v1/capacities") return List([new JsonObject
            {
                ["id"] = run.CapacityId, ["displayName"] = hub.Name + "cap", ["sku"] = "F2",
                ["region"] = run.Input.Location, ["state"] = "Active"
            }]);
            if (path.EndsWith("/networking/communicationPolicy", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Put) policies[path] = (JsonObject)request.Body!.DeepClone();
                else Assert.Equal(HttpMethod.Get, request.Method);
                return new(200, (JsonObject)(policies.GetValueOrDefault(path) ?? new JsonObject { ["preserve"] = "synthetic-extension" }).DeepClone(),
                    "\"synthetic-policy-etag\"");
            }
            if (path.EndsWith("/getDefinition", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                return Reply(SourceNotebookDefinition(definitions[path[..^"/getDefinition".Length]]));
            }
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Contains("/jobs/execute/instances", StringComparison.Ordinal))
            {
                var role = fabric["/v1/workspaces/" + parts[2]]["displayName"]!.GetValue<string>().Split('-')[^1];
                if (request.Method == HttpMethod.Post)
                {
                    Assert.False(jobs.ContainsKey(role), "An original source job is submitted only once.");
                    jobs[role] = Guid.NewGuid().ToString("D");
                    deltaIds[role] = Guid.NewGuid().ToString("D");
                    return new(202, new(), OperationUrl: request.Uri.GetLeftPart(UriPartial.Path) + "/" + jobs[role]) { RetryAfterSeconds = 60 };
                }
                Assert.Equal(HttpMethod.Get, request.Method);
                if (!jobReadbacks.ContainsKey(role))
                    jobReadbacks[role] = SourceNotebookJob(run, role, parts[4], jobs[role], deltaIds[role]);
                return Reply(jobReadbacks[role]);
            }
            if (request.Method == HttpMethod.Get)
            {
                if (fabric.TryGetValue(path, out var item)) return Reply(item);
                if (parts.Length == 3 && parts[1] == "workspaces") return new(404, new());
                if (parts.Length >= 4 && parts[3] == "items" && parts.Length == 4)
                    return List(fabric.Where(item => item.Key.StartsWith("/v1/workspaces/" + parts[2] + "/", StringComparison.Ordinal) &&
                        item.Value["type"]?.GetValue<string>() is "Lakehouse" or "Notebook" or "SQLEndpoint").Select(item => item.Value));
                if (parts.Length == 6 && parts[3] == "lakehouses" && parts[5] == "tables")
                    return new(200, new JsonObject { ["data"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "c1_sources", ["type"] = "Managed", ["format"] = "Delta",
                        ["location"] = "abfss://" + parts[2] + "@onelake.dfs.fabric.microsoft.com/" + parts[4] + "/Tables/c1_sources"
                    }) });
                if (path.EndsWith("/roleAssignments", StringComparison.Ordinal))
                    return List(fabric.Where(item => item.Key.StartsWith(path + "/", StringComparison.Ordinal)).Select(item => item.Value)
                        .Prepend(new JsonObject
                        {
                            ["id"] = parts[2], ["role"] = "Admin",
                            ["principal"] = new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId, ["type"] = "User" }
                        }));
                return List(fabric.Where(item => item.Key.StartsWith(path + "/", StringComparison.Ordinal) &&
                    !item.Key[(path.Length + 1)..].Contains('/')).Select(item => item.Value));
            }
            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal(3, parts.Length);
                Assert.Equal("workspaces", parts[1]);
                Assert.True(fabric.ContainsKey(path));
                foreach (var key in fabric.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
                    fabric.Remove(key);
                return new(200, new());
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            if (path.EndsWith("/dataAccessRoles", StringComparison.Ordinal))
            {
                var value = (JsonObject)request.Body!.DeepClone();
                value.Remove("kind");
                value["eTag"] = "synthetic-source-version-1";
                fabric.Add(path + "/" + value["name"]!.GetValue<string>(), value);
                return Reply(new JsonObject(), 201);
            }
            Assert.True(path == "/v1/workspaces" || path.EndsWith("/lakehouses", StringComparison.Ordinal) ||
                path.EndsWith("/notebooks", StringComparison.Ordinal) || path.EndsWith("/roleAssignments", StringComparison.Ordinal));
            var created = (JsonObject)request.Body!.DeepClone();
            created["id"] = Guid.NewGuid().ToString("D");
            var id = path + "/" + created["id"]!.GetValue<string>();
            if (path.EndsWith("/notebooks", StringComparison.Ordinal))
            {
                definitions[id] = created["definition"]!.AsObject();
                created.Remove("definition");
                created["type"] = "Notebook";
                created["workspaceId"] = parts[2];
            }
            if (path.EndsWith("/lakehouses", StringComparison.Ordinal))
            {
                created["type"] = "Lakehouse";
                created["workspaceId"] = parts[2];
                var sqlId = Guid.NewGuid().ToString("D");
                created["properties"] = new JsonObject
                { ["sqlEndpointProperties"] = new JsonObject { ["id"] = sqlId, ["provisioningStatus"] = "Success" } };
                fabric.Add("/v1/workspaces/" + parts[2] + "/sqlEndpoints/" + sqlId,
                    new() { ["id"] = sqlId, ["type"] = "SQLEndpoint", ["workspaceId"] = parts[2] });
            }
            fabric.Add(id, created);
            return Reply(created, 201);
        }

        private static FabricC1DeploymentReply Reply(JsonObject? value, int status = 200) =>
            value is null ? new(404, new()) : new(status, (JsonObject)value.DeepClone(), value["etag"]?.GetValue<string>());
        private static FabricC1DeploymentReply List(IEnumerable<JsonObject> values) =>
            new(200, new JsonObject { ["value"] = new JsonArray(values.Select(value => value.DeepClone()).ToArray()) });
    }
}
