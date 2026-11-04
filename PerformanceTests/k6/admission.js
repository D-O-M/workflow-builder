import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const apiBaseUrl = (__ENV.API_BASE_URL || 'http://localhost:5180').replace(/\/$/, '');
const blueprintKey = __ENV.BLUEPRINT_KEY || 'nft-ceiling-fanout-join';
const payloadTemplate = __ENV.PROCESS_PAYLOAD_JSON ? JSON.parse(open(__ENV.PROCESS_PAYLOAD_JSON)) : null;
const summaryPath = __ENV.K6_SUMMARY_PATH || 'k6-summary-admission.json';
const scenarioProfile = __ENV.NFT_PROFILE || 'smoke';
const headers = buildHeaders();

export const processStartAccepted = new Counter('process_start_accepted');
export const processStartFailed = new Rate('process_start_failed');

export const options = {
  scenarios: {
    admission: scenarioProfile === 'standard'
      ? {
          executor: 'ramping-arrival-rate',
          timeUnit: '1s',
          preAllocatedVUs: Number(__ENV.K6_PREALLOCATED_VUS || 50),
          maxVUs: Number(__ENV.K6_MAX_VUS || 150),
          stages: [
            { target: Number(__ENV.K6_START_RATE || 2), duration: '30s' },
            { target: Number(__ENV.K6_TARGET_RATE || 10), duration: '2m' },
            { target: 0, duration: '15s' },
          ],
        }
      : {
          executor: 'constant-vus',
          vus: Number(__ENV.K6_VUS || 2),
          duration: __ENV.K6_DURATION || '20s',
        },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<750'],
    process_start_failed: ['rate<0.01'],
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
  const payload = buildPayload();
  const response = http.post(
    `${apiBaseUrl}/api/blueprints/${data.blueprintId}/processes`,
    JSON.stringify(payload),
    { headers },
  );

  const accepted = response.status === 202;
  processStartAccepted.add(accepted ? 1 : 0);
  processStartFailed.add(!accepted);
  check(response, { 'process accepted': () => accepted });
  sleep(Number(__ENV.K6_THINK_TIME_SECONDS || 0));
}

function buildHeaders() {
  const result = { 'Content-Type': 'application/json' };
  if (__ENV.ACCESS_TOKEN) {
    result.Authorization = `Bearer ${__ENV.ACCESS_TOKEN}`;
  }
  return result;
}

export function handleSummary(data) {
  return {
    [summaryPath]: JSON.stringify(data, null, 2),
  };
}

function buildPayload() {
  if (payloadTemplate) {
    return payloadTemplate;
  }

  const key = `k6-${__VU}-${__ITER}-${Date.now()}`;
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
        Mode: 'runtime-k6',
      },
      '10000000-0000-0000-0000-000000000006': {
        ExpectedMinimumScore: 1,
      },
    },
  };
}
