import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const apiBaseUrl = (__ENV.API_BASE_URL || 'http://localhost:5180').replace(/\/$/, '');
const blueprintKey = __ENV.BLUEPRINT_KEY || 'nft-ceiling-fanout-join';
const summaryPath = __ENV.K6_SUMMARY_PATH || 'k6-summary-completion.json';
const headers = buildHeaders();

export const processCompletionLatency = new Trend('process_completion_latency', true);
export const processCompletionFailed = new Rate('process_completion_failed');

export const options = {
  scenarios: {
    completion: {
      executor: 'constant-vus',
      vus: Number(__ENV.K6_VUS || 4),
      duration: __ENV.K6_DURATION || '1m',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    process_completion_failed: ['rate<0.01'],
    process_completion_latency: ['p(95)<30000'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const blueprints = http.get(`${apiBaseUrl}/api/blueprints`, { headers });
  check(blueprints, { 'blueprints loaded': (res) => res.status === 200 });

  const match = blueprints.json().find((blueprint) => blueprint.key === blueprintKey);
  if (!match) {
    throw new Error(`Blueprint '${blueprintKey}' was not found.`);
  }

  return { blueprintId: match.id };
}

export default function (data) {
  const started = Date.now();
  const startResponse = http.post(
    `${apiBaseUrl}/api/blueprints/${data.blueprintId}/processes`,
    JSON.stringify(buildPayload()),
    { headers },
  );

  if (startResponse.status !== 202) {
    processCompletionFailed.add(1);
    check(startResponse, { 'process accepted': () => false });
    return;
  }

  const processId = startResponse.json('processId');
  const terminal = waitForTerminalProcess(data.blueprintId, processId);
  const latency = Date.now() - started;

  processCompletionLatency.add(latency);
  processCompletionFailed.add(terminal.status !== 'Completed');
  check(terminal, { 'process completed': (process) => process.status === 'Completed' });
}

export function handleSummary(data) {
  return {
    [summaryPath]: JSON.stringify(data, null, 2),
  };
}

function waitForTerminalProcess(blueprintId, processId) {
  const timeoutMs = Number(__ENV.PROCESS_TIMEOUT_MS || 60000);
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    const response = http.get(`${apiBaseUrl}/api/blueprints/${blueprintId}/processes/${processId}`, { headers });
    if (response.status === 200) {
      const detail = response.json();
      if (detail.status === 'Completed' || detail.status === 'Failed') {
        return detail;
      }
    }

    sleep(Number(__ENV.PROCESS_POLL_SECONDS || 0.5));
  }

  return { status: 'TimedOut' };
}

function buildHeaders() {
  const result = { 'Content-Type': 'application/json' };
  if (__ENV.ACCESS_TOKEN) {
    result.Authorization = `Bearer ${__ENV.ACCESS_TOKEN}`;
  }
  return result;
}

function buildPayload() {
  const key = `k6-completion-${__VU}-${__ITER}-${Date.now()}`;
  return {
initialTaskInput: {
      correlationKey: key,
      payloadSize: 256 + (__ITER % 512),
      baseScore: 10 + (__ITER % 7),
      region: 'local',
      delayMs: 0,
    },
    nodeOverrides: {
      '10000000-0000-0000-0000-000000000005': {
        Mode: 'runtime-k6-completion',
      },
      '10000000-0000-0000-0000-000000000006': {
        ExpectedMinimumScore: 1,
      },
    },
  };
}
