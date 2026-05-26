import http from 'k6/http';
import { check } from 'k6';
import { Rate } from 'k6/metrics';

const apiBaseUrl = (__ENV.API_BASE_URL || 'http://localhost:5180').replace(/\/$/, '');
const blueprintKey = __ENV.BLUEPRINT_KEY || 'nft-ceiling-fanout-join';
const summaryPath = __ENV.K6_SUMMARY_PATH || 'k6-summary-staircase.json';
const startRate = Number(__ENV.K6_START_RATE || 2);
const stepRate = Number(__ENV.K6_STEP_RATE || 4);
const steps = Number(__ENV.K6_STEPS || 5);

export const processStartFailed = new Rate('process_start_failed');

export const options = {
  scenarios: {
    staircase: {
      executor: 'ramping-arrival-rate',
      timeUnit: '1s',
      preAllocatedVUs: Number(__ENV.K6_PREALLOCATED_VUS || 80),
      maxVUs: Number(__ENV.K6_MAX_VUS || 250),
      stages: buildStages(),
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    process_start_failed: ['rate<0.01'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const blueprints = http.get(`${apiBaseUrl}/api/blueprints`);
  check(blueprints, { 'blueprints loaded': (res) => res.status === 200 });

  const match = blueprints.json().find((blueprint) => blueprint.key === blueprintKey);
  if (!match) {
    throw new Error(`Blueprint '${blueprintKey}' was not found.`);
  }

  return { blueprintId: match.id };
}

export default function (data) {
  const response = http.post(
    `${apiBaseUrl}/api/blueprints/${data.blueprintId}/processes`,
    JSON.stringify(buildPayload()),
    { headers: { 'Content-Type': 'application/json' } },
  );

  const accepted = response.status === 202;
  processStartFailed.add(!accepted);
  check(response, { 'process accepted': () => accepted });
}

export function handleSummary(data) {
  return {
    [summaryPath]: JSON.stringify(data, null, 2),
  };
}

function buildStages() {
  const stages = [];
  for (let i = 0; i < steps; i += 1) {
    stages.push({ target: startRate + i * stepRate, duration: __ENV.K6_STEP_DURATION || '45s' });
  }
  stages.push({ target: 0, duration: '15s' });
  return stages;
}

function buildPayload() {
  const key = `k6-stair-${__VU}-${__ITER}-${Date.now()}`;
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
        Mode: 'runtime-k6-staircase',
      },
      '10000000-0000-0000-0000-000000000006': {
        ExpectedMinimumScore: 1,
      },
    },
  };
}
