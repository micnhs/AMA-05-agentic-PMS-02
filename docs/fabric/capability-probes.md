# Fabric capability probes and unresolved platform boundaries

Documentation reviewed on 2026-11-17. This is a blocked capability preparation
record, not an approved provisioning plan or a real-service test report.
The exact sources and their published revision identifiers are recorded in
[capability-gates.json](capability-gates.json).

## G01: native commit, audit and original protection

Required isolated targets: Fabric Core SQL, a narrow Azure SQL Ledger primary
and named geo-secondary, two regional WORM accounts in separately administered
protection subscriptions, and an independent certifier identity/key. None is
selected or provisioned here.

Use synthetic commands with at least two writes and one read-only dependency.
Persist the complete envelope, originals, audit draft and blocked outbox.
Collect exact source/config/rule hashes, command fingerprint, epoch/incarnation,
transaction/block IDs, predecessor and revision vectors, object versions,
policy references, digest bytes, certificate bytes and activation binding.
Full bytes remain in approved private evidence stores; public CI logs must not
contain endpoints, tokens, identities or domain data.

| Probe | Positive evidence | Negative/interruption evidence |
|---|---|---|
| G01-01 original protection | Direct write and byte-for-byte readback in A and B; effective locked retention | Missing B, changed bytes/length, wrong version, unlocked/shortened policy, append or overwrite denied |
| G01-02 multiaggregate decision | Atomic command/receipt/head decision against all expected versions | Two racing commands; stale read-only dependency; same ID/different bytes; successor before activation |
| G01-03 closed block and geo | Native digest closes covering block before barrier to named secondary | Barrier-before-digest, wrong secondary/incarnation, timeout, log durable but replica not read-ready |
| G01-04 independent verification | Native ledger verification and independent authorization/completeness checks | Privileged valid-but-unauthorized insert, truncated history, altered digest location, untrusted key |
| G01-05 certificate and activation | Proof bundle A/B, signed certificate, its separate later A/B storage, exact current activation | Crash after each write, lost response, certificate only in A, wrong revision/effect vector |
| G01-06 effect release | Certified intent plus current fenced dispatch claim | Old writer, expired claim, unknown attempt, duplicate message/dossier response |

Microsoft documents that `sp_generate_database_ledger_digest` closes an open
last block and that `sp_wait_for_database_copy_sync` covers transactions
committed before its call on the primary. Neither function supplies the complete
Cohors certificate. Native ledger verification needs `VIEW LEDGER CONTENT` and
snapshot isolation; its cost must be measured, including on the critical path.
No native proof may be replaced by a synthetic DTO or a hash comparison alone.

## G02: recovery, RPO and the second protection path

Before a fault, create an independently retained inventory of all acknowledged
ingress/awareness, originals, human approvals, audit, reports, released PSURs,
submissions and receipts, including old objects and deletion/hold journals.
Record acknowledgement instants and highwatermarks outside the failed region.

| Probe | Required result |
|---|---|
| G02-01 component/AZ loss | Core restored within 5 minutes, without losing acknowledged protected data |
| G02-02 region A loss | Restore all previously acknowledged bytes and vectors from B; no normal final ACK from B alone |
| G02-03 protection repair | Within 60 minutes restore the second journal durability target, full historical archive, identities, private network and certifier; independently verify completeness |
| G02-04 old writers | Fence old writer, signer and dispatcher in the actual services; stale epochs cannot commit, activate or transmit |
| G02-05 emergency operation | Independent emergency route within 15 minutes; original awareness/deadlines continue; locally recorded ingress is explicitly not an RPO0 ACK |
| G02-06 corrupt/lost history | Verify native ledger history or open an explicit new incarnation with signed continuity; never impersonate old native transaction/block proofs |

The 60-minute full protection repair is an unresolved hard production conflict.
A prepared region C is an option to evaluate with costs and recovery volume,
not an approved or existing topology. Promotion of B is not sufficient. No
single-region exception, lower RPO or redefinition of recovery completion is
permitted. Interactive p95 <2s / p99 <5s must include the complete protection
path, not only an HTTP 202 response.

## G03: distinguish the actual app-only endpoint

| Path | Current documented authentication | Consequence |
|---|---|---|
| Direct ontology MCP | Known issue prevents service-principal access | Not a proven unattended path; a user/OBO session is not a substitute |
| Direct published data-agent MCP | User or service-principal bearer token; scope `https://api.fabric.microsoft.com/.default` | Candidate app-only path, including an ontology-backed source only if the real controlled semantics and access tests pass |
| Data-agent query interface described in the SP guide | Client credentials, Fabric/Power BI resource `https://analysis.windows.net/powerbi/api/.default`; preview | Endpoint/audience must not be interchanged with the MCP interface |
| Foundry-managed Fabric IQ connection/toolbox | Documented user delegation or separate OAuth user session | Does not itself prove the required app-only path |

The data-agent SP guide explicitly excludes managed identities and SP-backed KQL
sources. An identity automatically supplied to a Hosted Agent is therefore not
assumed to be an accepted data-agent credential. No default credential chain may
silently fall back to a developer's login.

G03-01 must obtain an actual application-only credential on an approved isolated
identity, perform MCP `initialize`, `tools/list` and `tools/call` without any
interactive user session, and bind the chosen tool/schema, source release,
ontology/bindings and structured query/result provenance. A successful answer
with no verifiable version/scope binding does not pass.

G03-02 must deny wrong audience, missing source grant, wrong product/scope,
withdrawn grant, revoked release and pending/unpublished revisions. G03-03 must
repeat a long-running task by its original task ID after interruption without
inventing a new business command. Only synthetic data may leave Fabric, and only
after the cross-geo/client-handling terms are approved under G05.

Missing inputs: paid capacity/workspace/item and source release, dedicated SP
credential method, tenant setting scoped to an approved group, workspace/source
grants, approved geography and private execution location. A subscription read
does not supply these permissions.

## G04: private path is not a single endpoint checkbox

The public documents contain a concrete unresolved path discrepancy:

- Fabric's workspace private-link support page, published revision
  `4118631219d16472cec761dfbfa6255429575523`, says external integrations such as
  AI Foundry or Microsoft 365 do not support private links for data agents.
- Foundry's Fabric IQ page, revision
  `d1fe4e30fa69d0203f9a71721f295c980727485a`, documents tenant/workspace private
  links for data agents and a workspace-specific host with an OBO connection.

These are not interchangeable app-only paths. The selected direct client,
runtime, auth flow and service versions need vendor clarification and an actual
end-to-end test. Do not silently choose the more favorable page as a pass.
The Foundry page lists ontology only for tenant-level private link and Power BI
semantic-model integration as public-only.

G04-01 requires private DNS/address and connection evidence for every hop:
client to Web/API; API to Core SQL, OneLake, journal primary/secondary, both
archives and signer; workflow/watchdog and Domibus; Hosted Agent to model,
the chosen IQ endpoint and Cohors API; regional recovery destinations.
Private inbound access does not prove private egress.

G04-02 disables public data-plane access and blocks outbound public fallback,
then executes the same positive operations. Repeat from outside the approved
network and with wrong DNS to prove rejection. Inventory unavoidable Entra/
control-plane dependencies explicitly rather than calling all internet traffic
private.

G04-03 tests the region-loss/rebuild path with the same restrictions. SQL
cross-region private-link access from data agents is currently documented as
unsupported. A healthy local-region query does not validate the recovery route.

Other deployment-order restrictions: workspace private links require an F SKU,
not trial/P capacity; Core SQL must exist before its workspace private link is
enabled, and SQL creation after that point is currently unsupported. Tenant
private-link settings affect shared tenant behavior and are not authorized by
this branch's isolation. Do not toggle them against the primary environment.

Foundry defaults to public inbound and egress. Private ingress alone leaves
egress public; BYO/managed VNet planning must precede creation. The current guide
says network injection cannot be added to an existing account. These are
planning constraints, not permission to provision or select a region.

## G05-G11 and admission

G05 requires a reviewed data-location/retention register for Fabric, Foundry
sessions, responses, model calls, archives and transport. G06 requires scoped
read/write denial, revision-bound independent human approvals and prompt
injection/permission-revocation checks. G07 requires versioned retrieval,
citations, negative filters and independent evaluation. G08 requires real
.NET Hosted Agent execution, reentry and isolation. The hosting service is GA;
the documented .NET integration install still uses `--prerelease`. Pin and test
the actual SDK instead of assigning the whole service a blanket preview label.

G09 requires measured capacity/costs and complete SLO/RTO/latency/alert results.
G10 requires the agreed EUDAMED/Domibus pilot/production boundaries and actual
transport reconciliation, without real authority communication in this scope.
G11 requires ID-exact completion of the 163 requirements and all 53 scenarios;
the current register is a specification, not 326 passing functional tests.

General cost approval has now been received. No concrete
Azure/Fabric target, capacity, role, region, retention, quantified budget or
actual C1 run has been approved. The bounded [C1 preparation](c1-preparation.md)
records the input/identity/source/private-path and abort/cleanup contract.
No cloud writes, actual probes, SDK installation or repository settings changes
are performed by this document. The existing credential-free standard hosted
job remains only the build/in-process execution path.
