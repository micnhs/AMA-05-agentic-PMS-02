using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public interface IFabricC1HubDnsTransport
{
    Task<byte[]> Exchange(IPAddress resolver, byte[] query, CancellationToken ct);
}

public sealed class FabricC1HubDnsTransport : IFabricC1HubDnsTransport
{
    public async Task<byte[]> Exchange(IPAddress resolver, byte[] query, CancellationToken ct)
    {
        Guard.Against(resolver.AddressFamily != AddressFamily.InterNetwork || query.Length is < 12 or > 512,
            "c1_hub_dns_transport", "An exact bound IPv4 resolver and bounded DNS question are required.", 409);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(resolver, 53, ct);
        using var stream = client.GetStream();
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, checked((ushort)query.Length));
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(query, ct);
        await stream.ReadExactlyAsync(prefix, ct);
        var size = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        Guard.Against(size < 12, "c1_hub_dns_transport", "The DNS reply is truncated.", 409);
        var response = new byte[size];
        await stream.ReadExactlyAsync(response, ct);
        return response;
    }
}

public static class FabricC1HubDns
{
    public static byte[] CnameQuestion(string host)
    {
        RequireHost(host);
        using var bytes = new MemoryStream();
        var header = new byte[12];
        RandomNumberGenerator.Fill(header.AsSpan(0, 2));
        header[2] = 1;
        header[5] = 1;
        bytes.Write(header);
        foreach (var label in host.Split('.'))
        {
            bytes.WriteByte(checked((byte)label.Length));
            bytes.Write(Encoding.ASCII.GetBytes(label));
        }
        bytes.Write([0, 0, 5, 0, 1]);
        return bytes.ToArray();
    }

    public static string ReadCname(byte[] question, byte[] response, string host)
    {
        RequireHost(host);
        if (question is null || response is null)
            throw new DomainException("c1_hub_dns_response", "The original DNS question and response are required.", 409);
        Guard.Against(question.Length is < 17 or > 512 || response.Length is < 12 or > 65535 ||
            question[2] != 1 || question[3] != 0 || U16(question, 4) != 1 ||
            U16(question, 6) != 0 || U16(question, 8) != 0 || U16(question, 10) != 0 ||
            U16(response, 0) != U16(question, 0) || (response[2] & 0xFA) != 0x80 ||
            (response[3] & 15) is not (0 or 3) || (response[3] & 0x40) != 0 || U16(response, 4) != 1,
            "c1_hub_dns_response", "The complete DNS response must match this non-truncated CNAME question.", 409);
        var queryPosition = 12;
        var responsePosition = 12;
        Guard.Against(Name(question, ref queryPosition) != host || Name(response, ref responsePosition) != host ||
            U16(question, queryPosition) != 5 || U16(question, queryPosition + 2) != 1 ||
            queryPosition + 4 != question.Length ||
            U16(response, responsePosition) != 5 || U16(response, responsePosition + 2) != 1,
            "c1_hub_dns_response", "A DNS reply cannot substitute a different query name, type or class.", 409);
        responsePosition += 4;
        var aliases = new List<string>();
        var count = U16(response, 6);
        var authority = U16(response, 8);
        var additional = U16(response, 10);
        Guard.Against(count > 128 || authority > 128 || additional > 128, "c1_hub_dns_response", "The DNS section inventory exceeds its bound.", 409);
        for (var index = 0; index < count + authority + additional; index++)
        {
            var owner = Name(response, ref responsePosition);
            var type = U16(response, responsePosition);
            var recordClass = U16(response, responsePosition + 2);
            var length = U16(response, responsePosition + 8);
            responsePosition += 10;
            Guard.Against(responsePosition + length > response.Length, "c1_hub_dns_response", "DNS record data is truncated.", 409);
            if (index < count && owner == host && type == 5 && recordClass == 1)
            {
                var position = responsePosition;
                var alias = Name(response, ref position);
                Guard.Against(position != responsePosition + length, "c1_hub_dns_response", "CNAME data has trailing or missing bytes.", 409);
                RequireHost(alias);
                aliases.Add(alias);
            }
            responsePosition += length;
        }
        Guard.Against(responsePosition != response.Length, "c1_hub_dns_response", "The DNS response has undeclared trailing data.", 409);
        Guard.Against(aliases.Count != 1 || aliases[0] == host, "c1_hub_dns_chain",
            "The original response must contain one unambiguous next CNAME; NXDOMAIN/A-only is not an alias proof.", 409);
        return aliases[0];
    }

    private static ushort U16(byte[] bytes, int position)
    {
        Guard.Against(position < 0 || position + 2 > bytes.Length, "c1_hub_dns_response", "DNS fields are truncated.", 409);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position, 2));
    }

    private static string Name(byte[] bytes, ref int position)
    {
        var current = position;
        var continuation = -1;
        var visited = new HashSet<int>();
        var labels = new List<string>();
        while (true)
        {
            Guard.Against(current >= bytes.Length || !visited.Add(current) || visited.Count > 128,
                "c1_hub_dns_response", "A DNS name is truncated, cyclic or oversized.", 409);
            var size = bytes[current++];
            if ((size & 0xC0) == 0xC0)
            {
                Guard.Against(current >= bytes.Length, "c1_hub_dns_response", "A DNS compression pointer is truncated.", 409);
                var next = ((size & 0x3F) << 8) | bytes[current++];
                Guard.Against(next >= current - 2, "c1_hub_dns_response", "A DNS pointer must reference an earlier complete name.", 409);
                if (continuation < 0) continuation = current;
                current = next;
                continue;
            }
            Guard.Against(size > 63 || current + size > bytes.Length, "c1_hub_dns_response", "A DNS label is invalid.", 409);
            if (size == 0) break;
            var label = bytes.AsSpan(current, size);
            Guard.Against(label.ContainsAnyExceptInRange((byte)0x21, (byte)0x7E), "c1_hub_dns_response", "Non-ASCII DNS labels are not accepted.", 409);
            labels.Add(Encoding.ASCII.GetString(label).ToLowerInvariant());
            current += size;
        }
        position = continuation < 0 ? current : continuation;
        var name = string.Join(".", labels);
        Guard.Against(name.Length > 253, "c1_hub_dns_response", "A DNS name exceeds the wire limit.", 409);
        return name;
    }

    private static void RequireHost(string host) =>
        Guard.Against(string.IsNullOrWhiteSpace(host) || host.Length is < 1 or > 253 || host != host.ToLowerInvariant() || host.Contains('*') ||
            Uri.CheckHostName(host) != UriHostNameType.Dns || host.Split('.').Any(label => label.Length is < 1 or > 63) ||
            host.Any(character => character > 127), "c1_hub_dns_name", "An exact lower-case ASCII provider name is required.", 409);
}

public sealed partial class FabricC1Provisioner
{
    public async Task<byte[][]> CollectSharedHubDnsEvidence(IFabricC1HubDnsTransport transport, CancellationToken ct)
    {
        var (_, deadline) = PlanNormalRequests(0);
        var receipt = RequireStoredHubReceipt();
        var candidate = Owned(HubBindingsCandidateKey, "SharedHubBindingsCandidate");
        var observations = HubObject(candidate, "observations");
        var consumer = HubObject(candidate, "consumer");
        var selections = JsonSerializer.Deserialize<FabricC1HubDnsSelection[]>(observations["selections"], Canonical.Json) ??
            throw new DomainException("c1_hub_dns_selection", "The original DNS selection is missing.", 409);
        Guard.Against(Text(receipt, "state") != "connected" ||
            time.GetUtcNow() >= SourceUtc(observations, "observedAtUtc").AddHours(24),
            "c1_hub_dns_evidence", "The own unregistered candidate must have current original observations.", 409);
        if (state.Objects.ContainsKey(HubBindingsEvidenceKey)) return ReadHubEvidenceBytes();
        var resolver = IPAddress.Parse(Text(HubObject(receipt, "hub"), "dnsServerIp"));
        var files = new List<byte[]>();
        foreach (var selected in selections)
        {
            var binding = Canonical.Hash(new { Subject = Text(candidate, "subjectSha256"), Selection = selected, Resolver = resolver.ToString() });
            var key = "shared-hub-dns-proof-" + Canonical.Hash(selected);
            if (state.Objects.TryGetValue(key, out var saved))
            {
                Guard.Against(saved.Kind != "SharedHubDnsEvidence" || saved.RequestHash != binding,
                    "c1_hub_dns_evidence", "The durable DNS evidence belongs to another original selection or subject.", 409);
                var bytes = DecodeHubEvidence(saved.Response["base64"]?.GetValue<string>());
                var verified = VerifyHubEvidenceFiles([bytes], consumer, "dns-chain", retirement: false);
                Guard.Against(verified.Keys.Single() != saved.Response["sha256"]?.GetValue<string>(),
                    "c1_hub_dns_evidence", "The original DNS evidence file digest changed.", 409);
                VerifyHubDnsProof(HubObject(verified.Values.Single().Value, "dns"), selected, observations);
                files.Add(bytes);
                continue;
            }
            var target = selected.Name + "." + selected.Zone;
            var current = selected.Fqdn;
            var chain = new List<string> { current };
            var exchanges = new JsonArray();
            while (current != target)
            {
                Guard.Against(chain.Count >= 17, "c1_hub_dns_chain", "The canonical-name chain exceeded the original bounded registration.", 409);
                var query = FabricC1HubDns.CnameQuestion(current);
                var (next, operationDeadline) = PlanNormalRequests(1);
                SaveJournal(next);
                state = next;
                var remaining = operationDeadline - time.GetUtcNow();
                Guard.Against(remaining <= TimeSpan.Zero, "c1_deploy_budget", "The original deadline elapsed before DNS I/O.", 409);
                using var budget = new CancellationTokenSource(remaining, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
                var response = await transport.Exchange(resolver, query, linked.Token);
                var alias = FabricC1HubDns.ReadCname(query, response, current);
                Guard.Against(chain.Contains(alias, StringComparer.Ordinal), "c1_hub_dns_chain", "A cyclic alias cannot establish a private registration target.", 409);
                exchanges.Add(new JsonObject
                {
                    ["queryBase64"] = Convert.ToBase64String(query), ["responseBase64"] = Convert.ToBase64String(response)
                });
                chain.Add(alias);
                current = alias;
            }
            PlanNormalRequests(0);
            var resources = HubObject(observations, "resources");
            var endpoint = resources[selected.PrivateEndpointId]!.AsObject();
            var nicId = endpoint["properties"]!["networkInterfaces"]![0]!["id"]!.GetValue<string>();
            var proof = new JsonObject
            {
                ["privateEndpointId"] = selected.PrivateEndpointId, ["fqdn"] = selected.Fqdn, ["zone"] = selected.Zone, ["name"] = selected.Name,
                ["resolverIp"] = resolver.ToString(), ["cnameChain"] = Strings(chain), ["dnsExchanges"] = exchanges,
                ["endpointObservationSha256"] = FabricC1Deployment.HubWireHash(endpoint),
                ["nicObservationSha256"] = FabricC1Deployment.HubWireHash(resources[nicId]),
                ["endpointReadback"] = endpoint.DeepClone(), ["nicReadback"] = resources[nicId]!.DeepClone(),
                ["privateAddressResolutionAccepted"] = false, ["trafficAccepted"] = false
            };
            VerifyHubDnsProof(proof, selected, observations);
            var evidence = HubPrerequisiteEvidence(consumer, "dns-chain", deadline);
            evidence["dns"] = proof;
            var original = JsonSerializer.SerializeToUtf8Bytes(evidence, Canonical.Json);
            Guard.Against(original.Length > 262144, "c1_hub_evidence", "A DNS proof exceeds its original file bound.", 409);
            SaveObject(key, "SharedHubDnsEvidence", selected.PrivateEndpointId, binding, new JsonObject
            {
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(original)),
                ["base64"] = Convert.ToBase64String(original)
            });
            files.Add(original);
        }
        return files.ToArray();
    }

    private JsonObject HubPrerequisiteEvidence(JsonObject consumer, string purpose, DateTimeOffset expires) => new()
    {
        ["contract"] = "cohors-hub-prerequisite-evidence-1", ["generation"] = input.Network!.SharedHub!.Generation,
        ["consumerBindingHash"] = state.BindingHash, ["sourceCommit"] = input.SourceCommit,
        ["purpose"] = purpose, ["subjectSha256"] = FabricC1Deployment.HubPrerequisiteSubject(consumer),
        ["observedAtUtc"] = time.GetUtcNow().ToString("O"), ["expiresAtUtc"] = expires.ToString("O"), ["passed"] = true
    };
}
