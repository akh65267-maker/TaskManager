// LOAD: can the system handle the traffic we expect?
//
// A steady, realistic number of concurrent shoppers for long enough to reach steady
// state. The pass criteria are the "healthy" thresholds; anything that fails here
// means the system cannot even carry expected load.
//
//   VUS   concurrent shoppers (default 20)   HOLD   steady period (default 3m)
//   RAMP  ramp-up and ramp-down (default 1m)
import exec from 'k6/execution';
import { TREND_STATS, num } from './lib/config.js';
import { journey } from './lib/journeys.js';
import { observe } from './lib/phases.js';
import { report } from './lib/report.js';
import { prepare } from './lib/setup.js';
import { healthy, merge } from './lib/thresholds.js';

const VUS = num('VUS', 20);
const HOLD = __ENV.HOLD || '3m';
const RAMP = __ENV.RAMP || '1m';

export const options = {
  setupTimeout: '10m',
  summaryTrendStats: TREND_STATS,
  scenarios: {
    load: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: RAMP, target: VUS },
        { duration: HOLD, target: VUS },
        { duration: RAMP, target: 0 },
      ],
      gracefulRampDown: '30s',
    },
  },
  thresholds: merge(healthy(), observe([])),
};

export function setup() {
  return prepare();
}

export default function (data) {
  if (exec.vu.iterationInScenario === 0 && exec.vu.idInTest === 1) {
    console.log(`LOADTEST_TRAFFIC_STARTED ${Date.now()}`);
  }
  journey(data);
}

export function handleSummary(data) {
  return report(data, { title: `LOAD  (${VUS} VUs, ${HOLD} steady)` });
}
