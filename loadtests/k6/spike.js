// SPIKE: what happens when traffic suddenly jumps?
//
// A calm baseline, then a near-instant multiple of it, then back. Stress asks where
// the system breaks under a gradual climb; this asks whether it survives a sudden
// one and, above all, whether it goes back to normal afterwards. The pass criteria
// therefore apply to the baseline and to the recovery, not to the spike itself.
//
//   BASE_VUS  calm level (default 5)   SPIKE_FACTOR  multiple (default 10)
//   HOLD_S    seconds at the peak (default 120)
import exec from 'k6/execution';
import { TREND_STATS, num } from './lib/config.js';
import { journey } from './lib/journeys.js';
import { observe, phaseAt } from './lib/phases.js';
import { report } from './lib/report.js';
import { prepare } from './lib/setup.js';
import { healthy, merge } from './lib/thresholds.js';

const BASE = num('BASE_VUS', 5);
const FACTOR = num('SPIKE_FACTOR', 10);
const HOLD_S = num('HOLD_S', 120);

const phases = [
  { name: 'baseline', seconds: 60 },
  { name: 'spike-up', seconds: 10 },
  { name: 'spike', seconds: HOLD_S },
  { name: 'spike-down', seconds: 10 },
  { name: 'recovery', seconds: 120 },
];

export const options = {
  setupTimeout: '10m',
  summaryTrendStats: TREND_STATS,
  scenarios: {
    spike: {
      executor: 'ramping-vus',
      startVUs: BASE,
      stages: [
        { duration: '60s', target: BASE },
        { duration: '10s', target: BASE * FACTOR },
        { duration: `${HOLD_S}s`, target: BASE * FACTOR },
        { duration: '10s', target: BASE },
        { duration: '120s', target: BASE },
      ],
      gracefulRampDown: '30s',
    },
  },
  thresholds: merge(healthy('phase:baseline'), healthy('phase:recovery'), observe(phases)),
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
  return report(data, { title: `SPIKE  (${BASE} -> ${BASE * FACTOR} VUs for ${HOLD_S}s, then back)`, phases });
}
