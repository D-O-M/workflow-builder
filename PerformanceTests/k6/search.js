import http from 'k6/http';
import { check, sleep } from 'k6';

const apiBaseUrl = (__ENV.API_BASE_URL || 'http://localhost:5180').replace(/\/$/, '');
const blueprintKey = __ENV.BLUEPRINT_KEY || 'nft-ceiling-fanout-join';
const summaryPath = __ENV.K6_SUMMARY_PATH || 'k6-summary-search.json';
const headers = buildHeaders();

export const options = {
  scenarios: {
    search: {
      executor: 'constant-vus',
      vus: Number(__ENV.K6_VUS || 4),
      duration: __ENV.K6_DURATION || '1m',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<750'],
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
  const now = new Date();
  const createdFrom = new Date(now.getTime() - Number(__ENV.SEARCH_WINDOW_MINUTES || 15) * 60 * 1000);
  const body = {
    blueprintDefinitionIds: [data.blueprintId],
    processStatuses: ['Completed', 'Failed', 'InProgress', 'Pending'],
    createdFrom: createdFrom.toISOString(),
    createdTo: now.toISOString(),
    nodeFilters: [],
    propertyFilters: [],
    limit: Number(__ENV.SEARCH_LIMIT || 25),
    cursor: null,
  };

  const response = http.post(
    `${apiBaseUrl}/api/processes/search`,
    JSON.stringify(body),
    { headers },
  );

  check(response, {
    'search returned page': (res) => res.status === 200 && Array.isArray(res.json('items')),
  });
  sleep(Number(__ENV.K6_THINK_TIME_SECONDS || 0.1));
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
