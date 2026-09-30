// STRESS: what happens when we exceed capacity?
//
// The arrival rate steps up until something gives. An arrival-rate executor is used
// on purpose: it keeps starting iterations at the target rate whether or not the
// system keeps up, which is what real overload looks like. A VU-based test slows
// down together with the system and hides the point where it breaks.
//
// The interesting outputs are the highest step that still held, whether failures were
// clean (429/503/timeouts) or messy, and - checked afterwards by run.sh - whether the
// data is still consistent and the system comes back once the pressure stops.
//
//   STEPS   iterations/second per step (default 5,10,20,40,80,160)
//   STEP_S  seconds held per step (default 60)   MAX_VUS  ceiling (default 300)
import exec from 'k6/execution';
import { TREND_STATS, num } from './lib/config.js';
import { journey } from './lib/journeys.js';
import { observe, phaseAt } from './lib/phases.js';
import { report } from './lib/report.js';
import { prepare } from './lib/setup.js';

const STEPS = (__ENV.STEPS || '5,10,20,40,80,160').split(',').map(Number);
const STEP_S = num('STEP_S', 60);
const RAMP_S = 10;
const MAX_VUS = num('MAX_VUS', 300);

// One phase per step, plus a cool-down at the lowest rate to see whether it recovers.
const phases = [
  ...STEPS.map((rate) => ({ name: `rate-${rate}`, seconds: RAMP_S + STEP_S })),
  { name: 'recovery', seconds: RAMP_S + 60 },
];

export const options = {
  setupTimeout: '10m',
  summaryTrendStats: TREND_STATS,
  scenarios: {
    stress: {
      executor: 'ramping-arrival-rate',
      startRate: STEPS[0],
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: MAX_VUS,
      stages: [
        ...STEPS.flatMap((rate) => [
          { duration: `${RAMP_S}s`, target: rate },
          { duration: `${STEP_S}s`, target: rate },
        ]),
        { duration: `${RAMP_S}s`, target: STEPS[0] },
        { duration: '60s', target: STEPS[0] },
      ],
    },
  },
  thresholds: {
    // Stop early once the system is plainly broken: pushing further only proves it.
    http_req_failed: [{ threshold: 'rate<0.5', abortOnFail: true, delayAbortEval: '60s' }],
    ...observe(phases),
  },
};

export function setup() {
  return prepare();
}

export default function (data) {
  if (exec.vu.iterationInScenario === 0 && exec.vu.idInTest === 1) {
    console.log(`LOADTEST_TRAFFIC_STARTED ${Date.now()}`);
  }
  journey(data, { phase: phaseAt(phases) });
}

export function handleSummary(data) {
  const values = (name) => {
    const m = data.metrics[name];
    return m ? m.values || m : {};
  };

  // A step "held" if it stayed fast and clean. The last step that held before the
  // first that did not is the capacity of this stack, on this machine.
  let held = null;
  let brokeAt = null;
  for (const { name } of phases) {
    if (name === 'recovery') continue;
    const duration = values(`http_req_duration{phase:${name}}`);
    const failed = values(`http_req_failed{phase:${name}}`);
    if (duration['p(95)'] === undefined) continue;
    const ok = (failed.rate ?? 0) < 0.02 && duration['p(95)'] < 1000;
    if (ok && brokeAt === null) held = name;
    if (!ok && brokeAt === null) brokeAt = name;
  }

  const extra = [
    '',
    held ? `Highest step that held (<2% failed, p95 <1s): ${held}  (iterations/second)` : 'No step held.',
    brokeAt ? `First step that did not hold: ${brokeAt}` : 'Every step held: raise STEPS to find the limit.',
    'These are numbers for this machine running the whole stack and the load generator together.',
  ];

  return report(data, { title: 'STRESS', phases, extra });
}
