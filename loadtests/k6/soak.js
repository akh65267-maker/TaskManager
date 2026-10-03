// SOAK: what happens after running under load for hours?
//
// Steady moderate load for a long time. Nothing here is about peak capacity; it looks
// for things that only appear with time: memory that never comes back, latency that
// creeps up, a table or queue that only grows, a connection pool that slowly leaks.
//
// Latency drift is judged here (first tenth of the run against the last tenth).
// Memory growth and leftover rows are judged by run.sh, which samples container
// memory throughout and checks the databases at the end.
//
//   SOAK_MINUTES  length of the run (default 30; use 120+ for a real soak)
//   VUS           steady concurrency (default 10)
import exec from 'k6/execution';
import { TREND_STATS, num } from './lib/config.js';
import { journey } from './lib/journeys.js';
import { observe, phaseAt } from './lib/phases.js';
import { report } from './lib/report.js';
import { prepare } from './lib/setup.js';
import { healthy, merge } from './lib/thresholds.js';

const MINUTES = num('SOAK_MINUTES', 30);
const VUS = num('VUS', 10);
const total = MINUTES * 60;

// First and last tenth are compared; the middle is just there to be survived.
const phases = [
  { name: 'first', seconds: total * 0.1 },
  { name: 'middle', seconds: total * 0.8 },
  { name: 'last', seconds: total * 0.1 },
];

export const options = {
  setupTimeout: '10m',
  summaryTrendStats: TREND_STATS,
  scenarios: {
    soak: { executor: 'constant-vus', vus: VUS, duration: `${MINUTES}m`, gracefulStop: '60s' },
  },
  thresholds: merge(healthy(), observe(phases)),
};

export function setup() {
  return prepare();
}

export default function (data) {
  if (exec.vu.iterationInScenario === 0 && exec.vu.idInTest === 1) {
    console.log(`LOADTEST_TRAFFIC_STARTED ${Date.now()}`);
  }
  journey(data, () => phaseAt(phases));
}

export function handleSummary(data) {
  const p95 = (phase) => {
    const m = data.metrics[`http_req_duration{phase:${phase}}`];
    return m ? (m.values || m)['p(95)'] : undefined;
  };
  const first = p95('first');
  const last = p95('last');

  // Latency that has grown by half (and by a margin that is not just noise on a fast
  // system) is drift. Judged on the whole request mix, dominated by browsing.
  let verdict = 'UNKNOWN';
  let detail = 'not enough samples to compare';
  if (first !== undefined && last !== undefined) {
    const grew = last > first * 1.5 && last - first > 50;
    verdict = grew ? 'FAIL' : 'PASS';
    detail = `p95 first tenth ${first.toFixed(0)}ms, last tenth ${last.toFixed(0)}ms (${(((last - first) / first) * 100).toFixed(0)}%)`;
  }

  return report(data, {
    title: `SOAK  (${VUS} VUs for ${MINUTES} min)`,
    phases,
    extra: ['', `Latency drift: ${detail}`, `LOADTEST_VERDICT soak-latency-drift=${verdict}`],
  });
}
