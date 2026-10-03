// RESILIENCE: what happens when a dependency fails?
//
// Moderate steady traffic while run.sh breaks something and later puts it back. k6
// cannot stop a container, so run.sh does that on a timeline, and this script only
// generates traffic and labels each request with the phase it fell in:
//
//   baseline -> fault -> settling -> recovery
//
// Pass criteria apply to baseline (the test is fair) and to recovery (the system came
// back). The fault phase is deliberately unjudged: errors while a dependency is down
// are expected, and the useful facts are which endpoints failed and how - read them
// from the report. What must hold afterwards - no order stuck, stock conserved, the
// outbox drained - is checked by run.sh against the databases.
//
//   BASELINE_S / FAULT_S / RECOVERY_S   seconds per phase (defaults 45 / 60 / 120)
//   SETTLE_S  seconds after the fault ends that are not judged (default 30)
//   VUS       steady concurrency (default 10)
import exec from 'k6/execution';
import { TREND_STATS, num } from './lib/config.js';
import { journey } from './lib/journeys.js';
import { observe, phaseAt } from './lib/phases.js';
import { report } from './lib/report.js';
import { prepare } from './lib/setup.js';
import { healthy, merge } from './lib/thresholds.js';

const VUS = num('VUS', 10);
const SETTLE_S = num('SETTLE_S', 30);
const RECOVERY_S = num('RECOVERY_S', 120);

const phases = [
  { name: 'baseline', seconds: num('BASELINE_S', 45) },
  { name: 'fault', seconds: num('FAULT_S', 60) },
  { name: 'settling', seconds: SETTLE_S },
  { name: 'recovery', seconds: Math.max(RECOVERY_S - SETTLE_S, 30) },
];
const total = phases.reduce((sum, p) => sum + p.seconds, 0);

export const options = {
  setupTimeout: '10m',
  summaryTrendStats: TREND_STATS,
  scenarios: {
    chaos: { executor: 'constant-vus', vus: VUS, duration: `${total}s`, gracefulStop: '45s' },
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
  return report(data, { title: `RESILIENCE  (${__ENV.FAULT || 'fault'})`, phases });
}
