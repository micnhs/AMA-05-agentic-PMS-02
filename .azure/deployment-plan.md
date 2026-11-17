# Cohors Fabric deployment preparation

Status: Planning; isolated source implementation approved, deployment target
and resource/cost approval outstanding. NOT Ready for Validation or Validated.

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

Subscription(s), regions, Fabric tenant/workspaces/capacities, identities,
resource groups, SKUs, budgets and actual processing geography are UNCONFIRMED.
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
privately by the coordinator, but no resource target/cost/region is approved;
identity/subscription/host details must not enter this public repository.

## Hard gates and fail-closed boundary

G02 remains a production conflict: B alone cannot finally acknowledge normal
commands after loss of A. A second complete protection path including historical
evidence within 60 minutes is unproven. Region C is an option, not an approved
topology. No single-region exception or reduced RPO/RTO.

Specification, local tests, real-service evidence and functional/operational
acceptance are separate evidence levels. Missing credentials/capacity or product
limitations block only dependent work, but always block production cutover.
Pending/unknown commits never become success through retries or timeouts.
Awareness starts deadlines immediately; a local observation is not an RPO0 ACK.

## Deployment preparation and rollback

Recipe decision pending inspection of the inherited Bicep deployment contracts.
Do not invoke inherited deployment scripts against their old defaults.
Use synthetic pilot data only. Real authority communication and medical
production operation are not authorized. Resource/cost/retention approvals
remain separate from the approval to implement source changes.

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
- No .NET/Node build, Fabric service, recovery or deployment validation claimed
  yet. Hosted build evidence will be separate from platform/acceptance evidence.
