import http from 'node:http';
import { readFileSync } from 'node:fs';
import { createHmac, createHash, randomUUID } from 'node:crypto';

const config = JSON.parse(readFileSync('/t12/private.json', 'utf8'));
const origin = 'http://127.0.0.1:8080';
const control = 'http://127.0.0.1:8090';
const cookies = new Map();
let csrf;
let caseId;
let runId;
const gate = { armed: false, eventId: null, instanceId: null, held: false, closed: false, calls: 0 };
const agentCalls = [];
const held = [];
const workflowTraffic = { ready: {}, outbox: {}, process: {} };

function requireValue(value, message) {
  if (!value) throw new Error(message);
}

async function bytes(stream) {
  const chunks = [];
  let length = 0;
  for await (const chunk of stream) {
    length += chunk.length;
    requireValue(length <= 2 * 1024 * 1024, 'Local transport payload exceeded its bound.');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

function respond(response, status, body) {
  response.writeHead(status, { 'content-type': 'application/json' });
  response.end(JSON.stringify(body));
}

async function api(path, method = 'GET', body) {
  const headers = { cookie: [...cookies].map(([key, value]) => `${key}=${value}`).join('; ') };
  if (body !== undefined) {
    headers['content-type'] = 'application/json';
    headers['X-CSRF-Token'] = csrf;
    headers['Idempotency-Key'] = randomUUID();
  }
  const response = await fetch(origin + path, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body),
    redirect: 'error', signal: AbortSignal.timeout(30000),
  });
  for (const cookie of response.headers.getSetCookie()) {
    const pair = cookie.split(';', 1)[0];
    cookies.set(pair.slice(0, pair.indexOf('=')), pair.slice(pair.indexOf('=') + 1));
  }
  requireValue(response.ok, `Local API returned HTTP ${response.status}.`);
  return response.json();
}

async function session() {
  if (csrf && cookies.has('Cohors.Session')) return;
  csrf = (await api('/api/auth/session')).csrfToken;
  await api('/api/auth/login', 'POST', { username: config.username, password: config.password });
  const identity = await api('/api/auth/session');
  requireValue(identity.authenticated && identity.actor.isHuman, 'A real local human session is required.');
  csrf = identity.csrfToken;
}

function transport() {
  return { ...gate, agentCalls: [...agentCalls], workflowTraffic };
}

async function table(name, instance, select) {
  requireValue(/^[A-Za-z][A-Za-z0-9]+$/.test(name), 'Invalid local table name.');
  requireValue(/^outbox-[a-f0-9]{32}-r\d+-a\d+$/.test(instance), 'No observed Durable instance identity.');
  const url = new URL(`http://127.0.0.1:10002/${config.storageAccount}/${name}`);
  url.searchParams.set('$filter', `PartitionKey eq '${instance}'`);
  url.searchParams.set('$select', select);
  const date = new Date().toUTCString();
  // The emulator's canonical resource includes its account twice, per Storage REST.
  const signature = createHmac('sha256', Buffer.from(config.storageKey, 'base64'))
    .update(`${date}\n/${config.storageAccount}${url.pathname}`).digest('base64');
  const response = await fetch(url, {
    headers: { 'x-ms-date': date, 'x-ms-version': '2019-02-02',
      Authorization: `SharedKeyLite ${config.storageAccount}:${signature}`,
      Accept: 'application/json;odata=nometadata', DataServiceVersion: '3.0', MaxDataServiceVersion: '3.0' },
    redirect: 'error', signal: AbortSignal.timeout(10000),
  });
  requireValue(response.ok, `Local Durable table read returned HTTP ${response.status}.`);
  requireValue(!response.headers.has('x-ms-continuation-nextpartitionkey') &&
    !response.headers.has('x-ms-continuation-nextrowkey'), 'Unexpected truncated Durable history.');
  return (await response.json()).value;
}

async function action(input) {
  if (input.action === 'probe') {
    const endpoints = { api: `${origin}/health/ready`, agents: 'http://127.0.0.1:8081/health/ready',
      workflows: 'http://127.0.0.1:8082/api/health/ready', operations: 'http://127.0.0.1:8083/api/health/ready' };
    requireValue(Object.hasOwn(endpoints, input.role), 'Unknown local host.');
    try {
      const response = await fetch(endpoints[input.role], { signal: AbortSignal.timeout(5000), redirect: 'error' });
      if (!response.ok) return { code: response.status, status: 'not-ready' };
      const body = await response.json();
      return { code: response.status, status: body.status };
    } catch (error) {
      if (error instanceof TypeError || error.name === 'TimeoutError') return { code: 0, status: 'unreachable' };
      throw error;
    }
  }
  if (input.action === 'transport') return transport();
  if (input.action === 'release') {
    gate.armed = false;
    for (const item of held.splice(0)) {
      if (!item.response.destroyed) {
        item.response.writeHead(item.status, item.headers);
        item.response.end(item.body);
      }
    }
    return transport();
  }
  if (input.action === 'durable') {
    requireValue(gate.instanceId, 'No instance observed through the real timer dispatch.');
    const instances = await table(`${config.hub}Instances`, gate.instanceId,
      'PartitionKey,RowKey,RuntimeStatus,ExecutionId,CreatedTime,LastUpdatedTime,Name');
    const history = await table(`${config.hub}History`, gate.instanceId,
      'PartitionKey,RowKey,EventType,Name,TaskScheduledId,ExecutionId');
    return { instanceId: gate.instanceId, instances, history };
  }
  await session();
  if (input.action === 'status') {
    const status = await api('/api/status');
    return { heartbeat: status.heartbeat, heartbeatMissing: status.heartbeatMissing,
      orchestration: status.orchestration, agentRuntime: status.agentRuntime,
      foundryConfigured: status.foundryConfigured };
  }
  if (input.action === 'request-assessment') {
    requireValue(!caseId && !gate.armed, 'This run already has its single test case.');
    const now = Date.now();
    const registered = await api('/api/cases', 'POST', {
      incidentKey: `T12-${config.run}`, sourceId: `T12-${config.run}`,
      title: 'Synthetic T12 separated-host restart exercise',
      description: 'SYNTHETISCH: Isolierter technischer Wiederanlaufnachweis. Keine reale Person und keine externe Kommunikation.',
      productId: 'ortho', udi: null, batch: 'T12-SYNTHETIC', country: 'IE', jurisdiction: 'EU-IE',
      seriousness: 'OtherSerious', occurredAt: new Date(now - 86400000).toISOString(),
      awarenessAt: new Date(now - 60000).toISOString(), scenario: 't12-restart', synthetic: true,
    });
    caseId = registered.id;
    gate.armed = true;
    const requested = await api(`/api/cases/${caseId}/assessment`, 'POST', {
      expectedVersion: registered.version, mode: 'Replay',
      reason: 'Synthetischen Replay-Auftrag fuer die isolierte getrennte Host-Wiederanlaufpruefung anfordern.',
    });
    runId = requested.agentRuns.at(-1).id;
    return { caseId, runId, mode: requested.agentRuns.at(-1).mode, status: requested.agentRuns.at(-1).status };
  }
  throw new Error('Unknown local driver action.');
}

function proxy(port, targetPort, agent) {
  http.createServer(async (request, response) => {
    try {
      requireValue(request.headers.host === `127.0.0.1:${port}`, 'Only the fixed local origin is accepted.');
      requireValue(agent
        ? request.method === 'POST' && request.url === '/internal/run'
        : (request.method === 'GET' && ['/internal/outbox', '/internal/ready'].includes(request.url)) ||
          (request.method === 'POST' && /^\/internal\/outbox\/[a-f0-9]{32}\/process$/.test(request.url)),
      'This local transport cannot trigger timers, approvals or external operations.');
      const body = await bytes(request);
      let envelope;
      if (agent) {
        envelope = JSON.parse(body);
        requireValue(envelope.input.mode === 'Replay', 'Only synthetic Replay transport is allowed.');
      }
      const target = new URL(`http://127.0.0.1:${targetPort}${request.url}`);
      const headers = { ...request.headers, host: target.host };
      const upstream = await new Promise((resolve, reject) => {
        const outgoing = http.request(target, { method: request.method, headers }, resolve);
        outgoing.on('error', reject);
        outgoing.end(body);
      });
      const result = await bytes(upstream);
      if (!agent) {
        const counts = workflowTraffic[request.url === '/internal/ready' ? 'ready' :
          request.url === '/internal/outbox' ? 'outbox' : 'process'];
        counts[upstream.statusCode] = (counts[upstream.statusCode] ?? 0) + 1;
      }
      if (agent) {
        requireValue(upstream.statusCode === 200, `Real AgentHost returned HTTP ${upstream.statusCode}.`);
        const output = JSON.parse(result);
        agentCalls.push({ inputHash: envelope.inputHash, role: envelope.input.role, mode: envelope.input.mode,
          model: output.output.model, outputHash: createHash('sha256').update(result).digest('hex'),
          at: new Date().toISOString() });
      } else {
        if (gate.armed && !gate.eventId && request.url === '/internal/outbox' && upstream.statusCode === 200) {
          const assessment = JSON.parse(result).filter(item => item.type === 'AssessmentRequested');
          requireValue(assessment.length <= 1, 'Ambiguous assessment event at the test transport.');
          if (assessment.length) {
            const event = assessment[0];
            gate.eventId = event.id;
            gate.instanceId = `outbox-${event.id}-r${event.recoveryVersion}-a${event.attempts}`;
          }
        }
        if (request.url === `/internal/outbox/${gate.eventId}/process`) {
          gate.calls++;
          if (gate.armed) {
            const completed = JSON.parse(result);
            requireValue(upstream.statusCode === 200 && completed.id === gate.eventId && completed.state === 'Handled',
              'The real API has not committed the expected outbox success.');
            gate.held = true;
            held.push({ response, status: upstream.statusCode, headers: upstream.headers, body: result });
            response.once('close', () => { if (!response.writableEnded) gate.closed = true; });
            return;
          }
        }
      }
      response.writeHead(upstream.statusCode, upstream.headers);
      response.end(result);
    } catch (error) {
      if (!response.headersSent && !response.destroyed) respond(response, 502, { error: error.message });
    }
  }).listen(port, '127.0.0.1');
}

if (process.argv[2] === 'serve') {
  proxy(8084, 8080, false);
  proxy(8086, 8081, true);
  http.createServer(async (request, response) => {
    if (request.method !== 'POST' || request.url !== '/' || request.headers['x-t12-control'] !== config.controlKey) {
      respond(response, 403, { error: 'Local test control denied.' });
      return;
    }
    try { respond(response, 200, await action(JSON.parse(await bytes(request)))); }
    catch (error) { respond(response, 500, { error: error.message }); }
  }).listen(8090, '127.0.0.1');
} else if (process.argv[2] === 'client') {
  const response = await fetch(control, { method: 'POST', body: await bytes(process.stdin),
    headers: { 'content-type': 'application/json', 'x-t12-control': config.controlKey },
    redirect: 'error', signal: AbortSignal.timeout(45000) });
  const result = await response.text();
  if (!response.ok) {
    process.stderr.write(`Local runtime action failed: HTTP ${response.status}; ${result}\n`);
    process.exitCode = 1;
  } else process.stdout.write(result);
} else throw new Error('Use serve or client inside the owned local network namespace.');
