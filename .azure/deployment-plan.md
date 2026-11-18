# Cohors Fabric deployment preparation

Status: C1 provisioning components implemented, complete bootstrap blocked (source-only). General cost approval
and bounded C1 execution approval already exist. The current assignment is to
automate resource creation, not to require manually created targets. Concrete
start parameters and authorized deployment identity are supplied by the existing
infrastructure owner. No cloud mutation is authorized by this source task.
NOT Ready for Validation or Validated.

## Current C1 automation increment

- Inspect existing deployment patterns and authoritative ARM/Graph/Fabric/
  Foundry contracts before generating resource or API operations.
- Implement the isolated minimal C1 resource graph and non-ARM bootstrap;
  derive IDs and endpoints from actual responses with ownership verification.
- Require explicit tenant/subscription, start-rights checks, bounded execution,
  resumable ownership state, secret-safe credential references, private data
  paths and public access disabled. No silent policy or privilege expansion.
- Stop before allocating resources if a mandatory platform operation is
  unsupported; do not emit success-shaped placeholders or reuse primary assets.
- Validate creation/resume/conflict/rights/output cases only on the existing
  credential-free hosted lane. Keep all seven native C1 cases NotRun.
- Finalize the supported executable scope and precise API blockers below after
  research. Target/IAM/policy effects require a separate visible deployment
  review; no second planning session and no repeat general approval request.

The first source increment now supplies variant-only ARM JSON, Graph/Fabric
provider operations, a durable ownership/request journal and an offline
`--fabric-c1-deploy plan --manifest FILE` entry. `apply` is deliberately blocked
before credential acquisition and mutations. It is not complete automation.
See `docs\fabric\README.md` for the remaining private-bootstrap integrations.
They are implementation gaps, not unsupported-provider claims. No template-only
or paid partial apply is exposed. Source regression verification uses only the
existing standard-hosted job; no cloud validation/deployment has occurred.

R4.0 from the existing infrastructure owner was read and hash-checked:
contract `ee2307d6b4371fdf260230825d43599be8355afbf127f332cce1ff968cb6aaee`,
binding map `f0301868704a269b58d654230f5695ffbabb2c4f2f727f822ffc79a77dca2da7`.
Private identities, paths and tenant configuration are not copied here.
The R4 package still requires specific egress/operator/credential/model
choices and effective start scopes; it does not require manually created
runtime resources or repeat the general C1/cost approval.

## Approval and immutable baseline

The approved ALT-FAB-01 v0.2 and FAB-BASELINE-01 v0.2 govern this independent
alternative. Mode: MODIFY the existing Azure application without changing its
primary branch, branding or functional scope.

- Fork: `445efc75c0b79dcfa9f23489593df0ddb8be02dc`.
- Branch: `micnhs-cohors-fabric-umsetzung`.
- Delivery identity: `cohors-fabric`; never reuse the primary delivery targets.
- The 118 original IDs (104 Muss, 14 Soll), 45 additions, 53 acceptance scenarios
  and nine scope clarifications remain mandatory within their agreed boundaries.
- Inherited tests, local tooling claims and deployment evidence are historical,
  NOT evidence for this variant.

## Approved sequence

1. Bind source, configuration and rule versions; establish delivery isolation.
2. Version the complete requirement register with ID-specific evidence and gaps.
3. Address G01 commit/audit/original protection and G02 recovery/fencing first;
   investigate actual app-only Fabric IQ (G03) and private end-to-end paths (G04).
4. Replace persistence/evidence, knowledge/retrieval and agent hosting by adapters
   only after their prerequisite gates permit it. Preserve deterministic rules,
   independent human approvals, deadlines, UI and Domibus/simulator semantics.
5. Validate and deploy to separately approved targets; prove independent rollback.

## Target architecture

- Fabric Core SQL: operational state, hidden pending revisions and gated outbox.
- OneLake: operational evidence/knowledge copies, NOT the original WORM authority.
- Fabric IQ: controlled, scoped, version-bound queries; no approval authority.
- Foundry Hosted Agents: own .NET agents; no direct business-state mutation.
- Separate Web/API, deterministic workflow/deadline/watchdog and Domibus services.
- Narrow external Azure SQL Ledger journal: ordering/commit authority, NOT a
  second editable case database.
- Two directly written regional Blob WORM archives: complete original bytes,
  replay envelopes, digests and certificates; independently administered.
- Independent certifier; explicit regional fencing and recovery.

## Azure context and execution isolation

A read-only Azure subscription/tenant context was observed privately; it is not
a chosen deployment destination or a grant of write access. Protection
subscriptions, regions, Fabric workspaces/capacities, identities, resource
groups, SKUs, budgets and actual processing geography remain UNAPPROVED.
Existing primary resources, credentials, containers, SQL instances, volumes,
ports, fixtures, processes and local directories must not be used.
The source worktree is isolated; host/compute/quota isolation is not yet proven.
No native build, service, browser or load run until a separate execution path
is allocated. No global tool, SDK, Docker or Azure configuration changes.

A single standard GitHub-hosted Ubuntu job is now allocated for this exact
Fabric branch: .NET 10.0.404, Node 24.10.0, own temporary package/cache paths,
no SQL/Docker/browser, credentials, Azure login, OIDC, shared caches or uploads.
The branch-only workflow does not modify the primary/default branch. Inherited
primary workflows are disabled in this checkout. Azure context was observed
privately by the coordinator, but no concrete resource target/budget/region is approved;
identity/subscription/host details must not enter this public repository.

## Hard gates and fail-closed boundary

G02 remains a production conflict: B alone cannot finally acknowledge normal
commands after loss of A. A second complete protection path including historical
evidence within 60 minutes is unproven. Region C is an option, not an approved
topology. No single-region exception or reduced RPO/RTO.

The current [capability register](../docs/fabric/capability-gates.json) and
[native probe preparation](../docs/fabric/capability-probes.md) preserve all
G01-G11 blockers. G03 distinguishes direct ontology MCP (SP access currently
unsupported), direct data-agent MCP (documented SP candidate), and delegated
Foundry connections. G04 records conflicting path-specific private-link
documentation; actual end-to-end app-only/private execution is still required.

The conceptual resource inventory requires separately administered regional
protection subscriptions for two WORM accounts, an Azure SQL Ledger primary
and named geo-secondary, separate Core/Evidence and knowledge Fabric capacities,
Core SQL/OneLake/IQ workspaces, own Foundry account/project and .NET agents,
own Web/API/workflow/watchdog/Domibus, signer/key stores, private endpoints/DNS
and independent monitoring/emergency resources. These are requirements, not
approved resource names, quantities, SKUs or prices.

Region-specific quota, price and capacity checks cannot be completed without
selected destinations. Do not raise quotas or install Azure extensions to fill
these gaps. This preparation is not a finalized deployment plan for approval.
The dedicated network execution path for actual service probes is also not
allocated; the credential-free standard hosted job is not that path.

Specification, local tests, real-service evidence and functional/operational
acceptance are separate evidence levels. Missing credentials/capacity or product
limitations block only dependent work, but always block production cutover.
Pending/unknown commits never become success through retries or timeouts.
Awareness starts deadlines immediately; a local observation is not an RPO0 ACK.

## Deployment preparation and rollback

Recipe decision pending inspection of the inherited Bicep deployment contracts.
Do not invoke inherited deployment scripts against their old defaults.
Use synthetic pilot data only. Real authority communication and medical
production operation are not authorized. Concrete resource/budget/retention
approvals remain separate from general cost approval and source implementation.

The [C1 preparation contract](../docs/fabric/c1-preparation.md) and
[null-bound input template](../fabric/c1/inputs.template.json) record the exact
missing IDs, private execution path, credential/audience/source grants, bounded
cost/runtime limits and cleanup evidence. Its historical preparation kernel
remains offline; a separate opt-in C1 execution envelope now binds the executable
HTTP/SSE, explicit app credential and Hosted Invocations modes. Normal API and
adapter registrations are unchanged. Only the existing credential-free hosted
build/in-process path is used for source validation, not a private cloud runner.
The existing infrastructure owner supplies the reviewed start-parameter and
rights package. The C1 automation must create its own resources and derive the
private bindings, rather than asking for pre-existing resource IDs or portal
copy steps. No second resource plan or cloud allocation is created by this
source-only assignment.

Before any deployment: finalize the isolated target plan and obtain target/cost
approval; invoke azure-validate, then azure-deploy. Do not claim readiness until
the application, infrastructure and platform gates have actual evidence.
Rollback must affect this delivery only; decided commands roll forward under
the protocol and must not be rolled back by an application/image rollback.

## Validation proof

- Exact fork, concept/workbook/config hashes and manifest distribution transform
  checked. Only private source-path labels changed in the distributed manifest.
- 118+45 IDs, 104/14 priorities, 326 distinct test specifications, 53 scenarios
  and nine scope clarifications checked; four local Python contract tests pass.
- Hosted run 44117767152 on e6ba11f proves Web/Solution builds and 24 selected
  in-process .NET tests. Run 44122134291 on 4e8d638 adds 27 commit-model tests
  and seven Python checks. These exact commits remain distinct from later work.
- Run 44123472283 on 4a5d38d passes the extended 31-case model suite
  (55 selected .NET tests total) with native digest/JSON edge cases.
- No Fabric/IQ/Hosted Agent, WORM/Ledger, recovery, functional/operational
  acceptance or deployment validation has run. The state remains Planning.
