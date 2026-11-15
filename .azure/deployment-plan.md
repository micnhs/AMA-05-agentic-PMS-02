# Cohors pilot deployment preparation

Status: Approved for synthetic implementation and local preparation only.
Azure validation is incomplete; deployment is NOT authorized.

## Approved scope

The coordinator's approved implementation plan is the source for the functional
scope. Build a synthetic PMS pilot with React/TypeScript, .NET 10, Container Apps,
.NET isolated Durable Functions, SQL, Blob, AI Search and Microsoft Foundry.
Authority transmissions and medical information requests remain simulated.
Separate medical and regulatory human identities must approve immutable packages.

## Deployment boundary

Preparation only. Do not provision Azure resources or choose a paid subscription,
region, model deployment, SKU or irreversible retention policy. These are separate
approval gates. Report local emulation, compiled integrations and live-tested
integrations separately. No production credentials or patient data.

## Implementation sequence

1. Baseline, domain, identity contracts and transactional SQL audit/outbox.
2. Complaint, awareness, deadline, assessment, two approvals, frozen package,
   simulator receipt and audit; UI grows with this slice.
3. Versioned evidence, deduplication, product matching and public read adapters.
4. Six bounded logical agents; explicit Foundry live and deterministic replay.
5. Assurance, simulated information requests, preliminary reports and follow-ups.
6. Denominator-aware trends, CAPA effectiveness, change requests and PSUR review.
7. Integrated workbench, regulatory desk, deadlines and audit.
8. Bicep, gated CI/CD, managed identities, private paths and operating guide.
9. Twenty-scenario acceptance checks and a verified reachable local preview.

## Known gates

- The original source DOCX is read-only and will remain unchanged.
- The unchanged 118-requirement spreadsheet and completed individual mapping are
  integrated under `source` and `docs/requirements`. All original statuses remain
  Open; assignment is not fulfillment of all pilot/target-operation requirements.
- Medical, regulatory, privacy and quality validation are not software tests.
- Live Azure authentication, network integration, model quality and disaster
  recovery require an approved deployed environment.
- Local .NET SDK 10.0.404 is installed in the worktree tooling area.

## Recipe

Bicep preparation for the approved architecture; no deployment execution.
The existing approved storage-backed Durable Functions design is retained.

## Host contract and coordination

`docs/runtime-contract.md` is the integration contract. Web/API and ONE internal
agent host are separate processes/images/identities in Azure; the in-process
agent library is a labelled local-only alternative. Workflows and Operations are
separate .NET isolated Flex Functions code packages deployed through OneDeploy.
Functions Dockerfiles are local Linux validation aids, not a change of cloud plan.
Their backend is the explicitly approved Azure Storage task hub;
a different paid scheduler is not implicitly authorized by a generic template.

The separate infrastructure session's isolated commits `35a29bc0` and `ae692f2b`
are integrated here as `1073a40` and `56ca20e`. Their offline tests pass, but the
security gate remains blocked. This worktree retains Dockerfiles, host code,
local scripts, application packages and integration. No cloud provisioning is
authorized.

Live Foundry remains gated on a configuration-bound provider review for actual
EU-member-state processing countries. A DataZone label is not sufficient and is
rejected; a regional SKU still requires geography verification. The optional
Foundry CLI setup is blocked: azd is absent and the authenticated GitHub release
lookup returned HTTP 403 / organization SAML enforcement. No authentication
bypass, CLI deployment or live model invocation was performed.

## All validation checks pass

- [ ] Bicep recipe core validation (CLI, authentication, build, validate, what-if).
  Offline compilation is allowed; subscription-scoped authentication, deployment
  validation and what-if require a separately approved real target. They have not
  been run or inferred from examples.
- [ ] Optional Bicep lint and the pinned offline security gate.
- [ ] Azure Policy validation for the actually approved subscription.
- [ ] Runtime/managed-identity/private-network and provider-geography proof.
- [ ] Initial AAD telemetry ingestion/schema/query and three-minute watchdog
  activation proof. Local SQL/query-model tests are not Kusto ingestion evidence.

## 7. Validation Proof

- Current Windows local SQL/domain acceptance: 64 passed, 0 failed, 0 skipped.
  MIR fixed follow-up obligations and effective PSUR document revisions included.
- Current React/TypeScript build passed; Web/API image builds use Node 24 as a
  build-only stage, not a Node Azure runtime. Application hosts are .NET.
- Integrated infrastructure offline tests: 25 passed, including the narrow
  SQLite model of the productive three-minute KQL predicates. Not Kusto/Azure.
- Infrastructure owner's recorded security result remains BLOCKED: 184 checks
  passed, 79 finding occurrences across 17 controls; no automatic waivers.
- True Linux loopback reachability was probed without credentials or database
  mutation. Isolated current Linux application/browser acceptance is in progress.
- The Actions-only harness guard remains intact. No `GITHUB_ACTIONS=true`
  spoofing, no workflow dispatch, no push, and no Azure deployment were performed.
- Plan status is not `Validated`. The full azure-validate recipe is intentionally
  blocked at target-dependent/cloud checks and unresolved security controls.
