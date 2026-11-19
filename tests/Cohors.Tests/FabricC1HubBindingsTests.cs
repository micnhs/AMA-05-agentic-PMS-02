using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed partial class FabricC1DeploymentTests
{
    [Fact]
    public async Task OriginalEndpointRegistrationNullStepIsDurableButNotPrivateDnsOrTrafficAcceptance()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run, registrationDns: true);
        hub.SeedEndpoints();
        hub.QueueRegistration();
        var candidate = await run.Provisioner.PrepareSharedHubBindings(hub.RegistrationSelections(), default);
        var dns = new HubDnsDouble((_, _) => throw new InvalidOperationException("The confirmed null step must not query an unregistered A record."));
        var evidence = await run.Provisioner.CollectSharedHubDnsEvidence(dns, default);
        var exported = run.Provisioner.ExportSharedHubBindings(evidence);
        Assert.Empty(dns.Calls);
        Assert.Equal(7, exported.Consumer["privateEndpoints"]!.AsArray().Count);
        Assert.Equal(7, exported.Consumer["dnsRecords"]!.AsArray().Count);
        Assert.Equal(5, exported.Consumer["operatorEndpoints"]!.AsArray().Count);
        Assert.False(exported.TrafficAccepted);
        Assert.False(exported.OwnerRevisionCreated);
        Assert.Equal(Canonical.Hash(run.Input), exported.Consumer["bindingHash"]!.GetValue<string>());
        Assert.Equal(candidate.SubjectSha256, FabricC1Deployment.HubPrerequisiteSubject(exported.Consumer));
        foreach (var (digest, bytes) in exported.EvidenceFiles)
        {
            Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var proof = JsonNode.Parse(bytes)!;
            Assert.Equal(candidate.SubjectSha256, proof["subjectSha256"]!.GetValue<string>());
            Assert.Single(proof["dns"]!["cnameChain"]!.AsArray());
            Assert.Empty(proof["dns"]!["dnsExchanges"]!.AsArray());
            Assert.False(proof["dns"]!["privateAddressResolutionAccepted"]!.GetValue<bool>());
            Assert.False(proof["dns"]!["trafficAccepted"]!.GetValue<bool>());
        }
        run.Clock.Advance(TimeSpan.FromSeconds(1));
        run.Restart();
        hub.QueueRegistration();
        await run.Provisioner.PrepareSharedHubBindings(hub.RegistrationSelections(), default);
        var restored = run.Provisioner.ExportSharedHubBindings(await run.Provisioner.CollectSharedHubDnsEvidence(dns, default));
        Assert.True(JsonNode.DeepEquals(exported.Consumer, restored.Consumer));
        foreach (var (digest, bytes) in exported.EvidenceFiles) Assert.Equal(bytes, restored.EvidenceFiles[digest]);
        var directory = Directory.CreateTempSubdirectory("cohors-hub-handoff-").FullName;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var destination = Path.Combine(directory, candidate.SubjectSha256);
        var names = exported.EvidenceFiles.Keys.Select(hash => hash + ".json")
            .Concat(["consumer-slot.json", "hub-evidence-files.json"]).ToArray();
        try
        {
            FabricC1Deployment.WriteHubConsumerHandoff(exported, directory);
            var original = names.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(destination, name)));
            File.Delete(Path.Combine(destination, names[0]));
            File.Delete(Path.Combine(destination, "consumer-slot.json"));
            FabricC1Deployment.WriteHubConsumerHandoff(restored, directory);
            foreach (var (name, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, name)));
            File.WriteAllText(Path.Combine(destination, names[0]), "synthetic-conflict");
            Assert.Equal("c1_hub_handoff_drift", Assert.Throws<DomainException>(() =>
                FabricC1Deployment.WriteHubConsumerHandoff(restored, directory)).Code);
            Assert.Equal("synthetic-conflict", File.ReadAllText(Path.Combine(destination, names[0])));
            var missing = restored.EvidenceFiles.ToDictionary(pair => pair.Key, pair => pair.Value);
            missing.Remove(missing.Keys.First());
            Assert.Equal("c1_hub_handoff", Assert.Throws<DomainException>(() =>
                FabricC1Deployment.WriteHubConsumerHandoff(new(restored.Consumer, missing), directory)).Code);
        }
        finally
        {
            foreach (var name in names) File.Delete(Path.Combine(destination, name));
            if (Directory.Exists(destination)) Directory.Delete(destination);
            Directory.Delete(directory);
        }
        Assert.DoesNotContain(run.Api.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task FailedDnsHopResumesOnlyItsUnfinishedReadAndKeepsOriginalBytesAndBudget()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run, registrationDns: true);
        hub.SeedEndpoints(cnameForVault: true);
        hub.QueueRegistration();
        await run.Provisioner.PrepareSharedHubBindings(hub.RegistrationSelections(), default);
        var started = run.Store.Current!.StartedAtUtc;
        var before = run.Store.Current.Requests;
        var lost = new HubDnsDouble((_, _) => throw new IOException("Synthetic DNS transport interruption."));
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.CollectSharedHubDnsEvidence(lost, default));
        var saved = run.Store.Current!.Objects.Where(item => item.Value.Kind == "SharedHubDnsEvidence")
            .ToDictionary(item => item.Key, item => Canonical.Hash(item.Value));
        Assert.Equal(6, saved.Count);
        Assert.Equal(before + 1, run.Store.Current.Requests);
        run.Restart();
        var dns = new HubDnsDouble((resolver, question) =>
        {
            Assert.Equal(IPAddress.Parse("10.242.0.4"), resolver);
            return HubCnameAnswer(question, "vault.privatelink.synthetic.example.invalid");
        });
        var export = run.Provisioner.ExportSharedHubBindings(await run.Provisioner.CollectSharedHubDnsEvidence(dns, default));
        Assert.Single(dns.Calls);
        Assert.Equal(before + 2, run.Store.Current!.Requests);
        Assert.Equal(started, run.Store.Current.StartedAtUtc);
        foreach (var (key, hash) in saved) Assert.Equal(hash, Canonical.Hash(run.Store.Current.Objects[key]));
        var proof = export.EvidenceFiles.Values.Select(bytes => JsonNode.Parse(bytes)!)
            .Single(item => item["dns"]!["name"]!.GetValue<string>() == "vault");
        Assert.Equal(2, proof["dns"]!["cnameChain"]!.AsArray().Count);
        var exchange = Assert.Single(proof["dns"]!["dnsExchanges"]!.AsArray())!;
        Assert.Equal(dns.Calls[0], Convert.FromBase64String(exchange["queryBase64"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("resolver")]
    [InlineData("endpoint")]
    [InlineData("subject")]
    [InlineData("positive-wrapper")]
    [InlineData("before-endpoint")]
    [InlineData("expiry-extension")]
    public async Task OwnerExportRejectsPositiveWrappersOrUnboundOriginalDnsEvidence(string drift)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run, registrationDns: true);
        hub.SeedEndpoints();
        hub.QueueRegistration();
        await run.Provisioner.PrepareSharedHubBindings(hub.RegistrationSelections(), default);
        var files = await run.Provisioner.CollectSharedHubDnsEvidence(new HubDnsDouble((_, _) => throw new InvalidOperationException()), default);
        var tampered = JsonNode.Parse(files[0])!.AsObject();
        if (drift == "resolver") tampered["dns"]!["resolverIp"] = "8.8.8.8";
        else if (drift == "endpoint") tampered["dns"]!["endpointReadback"]!["properties"]!["customDnsConfigs"]![0]!["ipAddresses"]![0] = "10.241.4.99";
        else if (drift == "subject") tampered["subjectSha256"] = new string('f', 64);
        else if (drift == "before-endpoint") tampered["observedAtUtc"] = run.Clock.GetUtcNow().AddTicks(-1).ToString("O");
        else if (drift == "expiry-extension") tampered["expiresAtUtc"] = run.Clock.GetUtcNow().AddSeconds(601).ToString("O");
        else tampered.Remove("dns");
        files[0] = JsonSerializer.SerializeToUtf8Bytes(tampered, Canonical.Json);
        Assert.StartsWith("c1_hub_", Assert.Throws<DomainException>(() => run.Provisioner.ExportSharedHubBindings(files)).Code, StringComparison.Ordinal);
        Assert.DoesNotContain("shared-hub-data-export", run.Store.Current!.Objects.Keys);
    }

    [Fact]
    public async Task DnsExportRecoveryAfterJournalledCommitRetainsExactOriginalFiles()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run, registrationDns: true);
        hub.SeedEndpoints();
        hub.QueueRegistration();
        await run.Provisioner.PrepareSharedHubBindings(hub.RegistrationSelections(), default);
        var files = await run.Provisioner.CollectSharedHubDnsEvidence(new HubDnsDouble((_, _) => throw new InvalidOperationException()), default);
        run.Store.AfterSave = value =>
        {
            if (value.Objects.ContainsKey("shared-hub-data-export")) throw new IOException("Synthetic crash after durable export, before file delivery.");
        };
        Assert.Throws<IOException>(() => run.Provisioner.ExportSharedHubBindings(files));
        run.Store.AfterSave = null;
        run.Restart();
        var recovered = run.Provisioner.RestoreSharedHubBindingsExport();
        foreach (var original in files)
            Assert.Equal(original, recovered.EvidenceFiles[Convert.ToHexStringLower(SHA256.HashData(original))]);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("cycle")]
    [InlineData("type")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("undeclared-authority")]
    public void DnsRegistrationReplaysOriginalWireNotJustAClaimedAlias(string drift)
    {
        const string host = "service.public.example.invalid";
        var question = FabricC1HubDns.CnameQuestion(host);
        var response = HubCnameAnswer(question, "service.privatelink.example.invalid");
        if (drift == "id") response[0] ^= 0x7f;
        else if (drift == "cycle") response = HubCnameAnswer(question, host);
        else if (drift == "type") response[question.Length + 3] = 1;
        else if (drift == "trailing") response = [.. response, 0];
        else if (drift == "undeclared-authority") response[9] = 1;
        else response = response[..^1];
        Assert.Throws<DomainException>(() => FabricC1HubDns.ReadCname(question, response, host));
    }

    [Fact]
    public async Task IndependentStopCanFenceAnOpenApplyWithoutLosingItsOriginalPendingRequest()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var directory = Directory.CreateTempSubdirectory("cohors-stop-cas-").FullName;
        var path = Path.Combine(directory, "state.json");
        try
        {
            using var applyFile = new FabricC1DeploymentFileJournal(path);
            applyFile.Save(new(Canonical.Hash(run.Input), new(), new() { ["runtime-app"] = new string('a', 64) })
            { Requests = 3, StartedAtUtc = run.Clock.GetUtcNow() });
            var apply = new FabricC1Provisioner(run.Input, run.Api, applyFile, run.Clock);
            using var stopFile = new FabricC1DeploymentFileJournal(path);
            var stop = new FabricC1Provisioner(run.Input, run.Api, stopFile, run.Clock);
            var fenced = stop.FencePrivateDeployment("operator-stop");
            Assert.Equal("StopFencedAwaitingOrderedCleanup", fenced.Status);
            Assert.Equal("c1_deploy_journal_conflict",
                (await Assert.ThrowsAsync<DomainException>(() => apply.Preflight(default))).Code);
            Assert.Empty(run.Api.Requests);
            var original = Assert.IsType<FabricC1DeploymentJournal>(stopFile.Load());
            Assert.Equal(3, original.Requests);
            Assert.Contains("runtime-app", original.PendingCreates.Keys);
            Assert.False(original.Objects["private-lifecycle-stop"].Response["workStopped"]!.GetValue<bool>());
            run.Clock.Advance(TimeSpan.FromSeconds(5));
            var resumed = new FabricC1Provisioner(run.Input, run.Api, stopFile, run.Clock);
            resumed.FencePrivateDeployment("operator-stop");
            Assert.Equal(original.CleanupStartedAtUtc, resumed.State.CleanupStartedAtUtc);
            Assert.Equal(original.StartedAtUtc, resumed.State.StartedAtUtc);
            Assert.Equal("c1_deploy_cleanup_started",
                (await Assert.ThrowsAsync<DomainException>(() => resumed.Preflight(default))).Code);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
            Directory.Delete(directory);
        }
    }

    private sealed class HubDnsDouble(Func<IPAddress, byte[], byte[]> exchange) : IFabricC1HubDnsTransport
    {
        public List<byte[]> Calls { get; } = [];
        public Task<byte[]> Exchange(IPAddress resolver, byte[] query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(query.ToArray());
            return Task.FromResult(exchange(resolver, query));
        }
    }

    private static byte[] HubCnameAnswer(byte[] question, string alias)
    {
        using var name = new MemoryStream();
        foreach (var label in alias.Split('.'))
        {
            name.WriteByte(checked((byte)label.Length));
            name.Write(Encoding.ASCII.GetBytes(label));
        }
        name.WriteByte(0);
        var response = new byte[checked(question.Length + 12 + (int)name.Length)];
        question.CopyTo(response, 0);
        response[2] = 0x81;
        response[3] = 0x83; // A CNAME can precede NXDOMAIN for the still-unregistered private target.
        response[7] = 1;
        var answer = response.AsSpan(question.Length);
        answer[0] = 0xc0; answer[1] = 0x0c;
        BinaryPrimitives.WriteUInt16BigEndian(answer[2..], 5);
        BinaryPrimitives.WriteUInt16BigEndian(answer[4..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(answer[6..], 30);
        BinaryPrimitives.WriteUInt16BigEndian(answer[10..], checked((ushort)name.Length));
        name.ToArray().CopyTo(answer[12..]);
        return response;
    }
}
