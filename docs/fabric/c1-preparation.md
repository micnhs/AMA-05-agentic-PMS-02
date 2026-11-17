# Fabric C1: bounded MCP probe preparation

Status: **PREPARATION ONLY / cloud execution blocked**. General cost approval
was received; no amount, capacity, workspace, region, model version, role/tenant
change or actual C1 run is selected/authorized by that approval. No resources,
credentials, model calls or live service evidence were created here.

## Deliverable and exact boundary

| File | Use |
| --- | --- |
| [`inputs.template.json`](../../fabric/c1/inputs.template.json) | Machine-readable binding; every `null` is a required missing input, not a default to discover or provision |
| [`synthetic-source.json`](../../fabric/c1/synthetic-source.json) | Five synthetic rows, three fixed questions and an independent expected/forbidden-row oracle |
| `src/Cohors.Infrastructure/FabricC1ProbeClient.cs` | Network-free .NET preparation kernel: preflight, discovery/request recipes, exact tool/schema checks and bound task resume/cancel intents |
| `tests/Cohors.Tests/FabricC1PreparationTests.cs` | In-process checks in the existing single credential-free hosted build job |

`Discovery` prepares the MCP `initialize`, `notifications/initialized` and
`tools/list` sequence. These are **not a batch to send without replies**: the
future transport must first validate the negotiated protocol/server response.
`Call` accepts only the reviewed single-tool name/schema and a question from the
hash-bound fixture. It never picks the first available tool or argument.
`TaskAction` retains the original identity/configuration, query hash and task ID;
it returns an intent, not an implemented task wire protocol or cancellation proof.

There is deliberately no HTTP/SSE transport, credential acquisition, DNS/socket
check, Foundry host, CLI entry point or DI registration in this preparation.
Use a pinned supported MCP SDK/transport for the later approved runtime rather
than hand-writing an unverified SSE/auth layer. No package/tool installation is
needed for these in-process checks. The skill's automatic dependency setup was
not executed because it can install global `azd` extensions on the shared host.
`PreparedOffline` means complete *input shape*, never authentic approval or
service evidence; `CloudExecutionAllowed` remains false.

## Missing inputs: infrastructure owner supplies private bindings

Keep the filled file and approval evidence in the approved private evidence
location, not in this public checkout, CI secrets, logs or uploaded artifacts.
The existing infrastructure owner continues the one joint target plan; do not
open another planning session or invent resource IDs.

| Block in the input file | Required missing values and evidence |
| --- | --- |
| `identity` | Exact Entra tenant, application/client ID, service-principal object ID; approved certificate/federation/secret method and a secret-store reference **without secret bytes**; expected token issuer and actual resource audience |
| `target` | Own capacity resource ID/SKU and region; workspace, published data-agent, source-item and ontology IDs; copied exact private MCP URL; observed supported protocol; tool name, question argument, canonical input-schema hash; reviewed task protocol/SDK revision |
| `source` | Materialized item/release hash, ontology definition/binding hash and effective source-grant review hash. The fixed `ortho` scope, `C1-RELEASE-1` label and fixture hash describe synthetic intent only, not a deployed release |
| `runtime` | Allocated private direct-runner resource; Foundry project endpoint, own Hosted Agent version/image digest; approved model deployment/version and processing geography |
| `controls` | Hashes of the isolated resource/region plan, exact app-only private-path review, DNS/egress and public-denial plans, data-handling/retention review, quantified budget/expiry plan and named cleanup plan; separate C1 run approval reference |
| `limits` | Approved maximum request count, total duration, response size and cleanup timeout; no unlimited/implicit retry or progress-based deadline extension |

The resource plan must bind subscription/RG, capacity SKU/quotas, billing owner,
cost amount/currency/expiry, model price/version, private endpoint IDs and exact
source grants. The data-handling plan must cover Fabric AI cross-geo settings,
Foundry/model responses, client evidence, diagnostics and deletion/retention.
Tenant settings may only use the explicitly approved SP security group; no
tenant-wide enabling, role expansion or other primary-environment change.

## Identity, source and protocol contract

**App-only is authenticated service access, not anonymous Cohors operation.**
The Entra user login and separate authorized Medical/RA human approvals through
the Cohors API remain unchanged. The probe has no approval or business-write
tool. A Hosted runtime identity is not assumed to be a supported Fabric caller.

For the direct **published data-agent MCP** candidate, request
`https://api.fabric.microsoft.com/.default`. The issued token's `aud` is a
separately bound resource value, not that scope string. Do not substitute the
Power BI audience from the different query-interface/OBO documentation.
Use an explicit approved SP credential, not `DefaultAzureCredential`, Azure CLI
login, interactive/device-code/OBO or a managed-identity fallback. Decode-only
JWT claims are not identity proof: collect matching Entra/service-side caller
evidence, audience/issuer checks and request correlation without retaining tokens.

Direct ontology MCP still has the documented SP limitation. An ontology-backed
data agent remains a candidate, not equivalent evidence by name. Bind the native
published definition, attached source IDs, query traces and source/ontology
release hashes independently of answer text. A model repeating requested IDs or
hashes does not prove which data it read.

The fixture has two allowed revisions (`C1-A@1`, `C1-B@2`), a pending newer
`C1-A@2`, a revoked `C1-C@1` and an unauthorized `cardio` row `C1-D@1`. The owner
must materialize allowed and denied source scopes with **actual enforceable
source permissions and release visibility**. Prompt instructions and filtering
the answer afterwards are not access control. Broad workspace Member/Contributor
rights can invalidate the negative cases; if necessary isolation cannot be
enforced with supported item/source grants, record a blocker, not a pass.
No operational case database or existing primary source is used.

The offline discovery recipe is pinned to MCP `2025-11-25`; the target has not
been observed to support it. The current tasks-extension documentation describes
per-request capabilities and `server/discover`, while the pinned lifecycle uses
`initialize`. Do not mix task layouts or assume `tasks/result` vs. a final result
inside `tasks/get`. Bind the exact supported server/SDK dialect before implementing
the transport or running the long-task probe. Discovery-only preparation can
omit tool/schema/task bindings; any query preparation requires their review.

## Native cases to run only after separate authorization

Run the same immutable client/fixture first from the allocated private direct
runtime, then from the own Foundry Hosted Agent. The standard GitHub runner is
**not** a private service-test runner.

| Case | Required observation; failure/stop condition |
| --- | --- |
| C1-01 identity/discovery | No user session; exact SP, workspace/item, audience, issuer and protocol; complete expected tool/schema. Stop on drift, missing source grant or undocumented protocol |
| C1-02 private positive | Same approved query in direct and Hosted runtime with public data-plane access disabled; exact allowed rows plus independent source/ontology/version provenance |
| C1-03 source negatives | Wrong product, pending/newer and revoked release, missing/withdrawn source grant deny data under the actual caller. An empty answer/model refusal alone is not denial proof; pair with native authorization/query evidence |
| C1-04 identity negatives | Approved negative identity and wrong-audience token fail at the same isolated endpoint. Never send a token to a redirected/unreviewed host; never broaden grants to make the probe green |
| C1-05 network negatives | From an approved outside-network runner and with wrong DNS, no successful data-plane path. Capture resolved addresses, private endpoint, connection/egress and denial evidence; no public fallback |
| C1-06 interruption | Persist original request/config hash and task ID in approved private storage; resume that task after interruption. Unknown response without a durable ID stays Unknown, not a new `tools/call` |
| C1-07 cancellation/cleanup | Cancel the bound task, observe its terminal outcome, close session/transport, verify exact run-owned resource cleanup and cost stop. Cancellation acknowledgement alone does not prove work stopped |

A `401/403`, timeout or empty result without a healthy positive control can be a
broken setup, not a passed denial. Task `input_required` must stop unattended
execution; no user/OBO authentication or business approval fallback. Enforce all
approved limits, bounded private response retention and no automatic query retry.
Redirection, public resolution, scope/release drift, unclear provenance or
excess budget/time/data immediately stop new requests and start recorded cleanup.

The private path includes Hosted -> model, Hosted -> Fabric MCP, Fabric ->
ontology/source, necessary evidence/credential services and the relevant Cohors
API path; private ingress alone is insufficient. Enumerate unavoidable Entra/
control-plane traffic separately. Firewall/VPN products are not mandated here;
the owner must choose approved controls that enforce the required path.

## Evidence, abort and cleanup record

Retain privately: source commit/image/configuration and fixture hashes; UTC
start/end; direct/Hosted execution identity; service request/task IDs; tool/schema
and native query/source-release evidence; per-hop private and denial evidence;
each case's expected/observed result, response digest, failure code and cost.
Each observation is `NotRun`, `Blocked`, `Observed`, `Denied`, `Unknown` or
`Failed`; specification/in-process/native observation/acceptance stay distinct.
The preparation has **zero native observations**, no G03/G04 pass.

Before provisioning, the owner supplies an exact run-owned cleanup inventory,
responsible operator, deadline and approved retention/deletion policy. On success
or abort: stop scheduling; request task cancellation when its ID is known;
reconcile final/Unknown state; close sessions and revoke only dedicated temporary
credentials/grants; remove or suspend only approved named C1 resources; verify
deletion/suspension and remaining billable items. Unknown tasks, failed deletion,
shared-resource dependencies or retained diagnostics produce `CleanupIncomplete`
with owner/action, never a success-shaped cleanup report. Never delete primary
resources, whole subscriptions or evidence whose retention is still required.

Only small sanitized status/hash summaries belong in public hosted logs.
Raw responses, private endpoints, tenant/principal IDs, tokens, source ACLs and
private configuration do not. This does not amend G01/G02, their two protection
subscriptions, any adapter gate, full private-path/recovery proof or pilot
acceptance. No Public/OBO/Direct-SQL substitute is authorized.

## Sources and unresolved platform boundary

Reviewed 2026-11-17; documentation is not native execution:

- [Data-agent MCP](https://learn.microsoft.com/fabric/data-science/data-agent-mcp-server): streamable HTTP, handshake, published endpoint, SP token and Fabric scope.
- [Data-agent SP](https://learn.microsoft.com/fabric/data-science/data-agent-service-principal): preview, source grants, no managed-identity or SP-backed KQL support.
- [Workspace private-link support](https://learn.microsoft.com/fabric/security/security-workspace-level-private-links-support#data-agent) still excludes external Foundry/M365 integrations.
- [Foundry Fabric IQ](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/fabric-iq) describes workspace-private data agents but its private connection example forwards a user token. This is **not** proof for the chosen direct SP path; explicit path review/clarification and a separately approved experiment are required.
- [MCP lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle) and [tasks extension](https://modelcontextprotocol.io/extensions/tasks/overview): pin the actual negotiated dialect; no guessed compatibility.

The existing [capability probes](capability-probes.md) and
[gate register](capability-gates.json) remain authoritative and open.
