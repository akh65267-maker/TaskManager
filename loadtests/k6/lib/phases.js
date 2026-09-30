import exec from 'k6/execution';

// A run is divided into named phases (baseline, spike, recovery, ...). Every request
// is tagged with the phase it fell in, so the report can say "p95 during the spike"
// and "p95 after it" instead of one average that hides both.

export function elapsedMs() {
  return Date.now() - exec.scenario.startTime;
}

// phases: [{ name, seconds }] in order. Past the end, the last phase applies.
export function phaseAt(phases, elapsed = elapsedMs()) {
  let end = 0;
  for (const phase of phases) {
    end += phase.seconds * 1000;
    if (elapsed < end) return phase.name;
  }
  return phases[phases.length - 1].name;
}

export function totalSeconds(phases) {
  return phases.reduce((sum, p) => sum + p.seconds, 0);
}

// k6 only records a tagged sub-metric (like http_req_duration{phase:spike}) when a
// threshold mentions it. These thresholds always pass; they exist so the report
// has per-phase numbers to show. Real pass/fail thresholds are added separately.
export function observe(phases) {
  const thresholds = {};
  for (const { name } of phases) {
    thresholds[`http_req_duration{phase:${name}}`] = ['max>=0'];
    thresholds[`http_req_failed{phase:${name}}`] = ['rate>=0'];
    thresholds[`http_reqs{phase:${name}}`] = ['count>=0'];
    thresholds[`checkout_confirmed{phase:${name}}`] = ['rate>=0'];
  }
  for (const status of ['Confirmed', 'Cancelled', 'Pending']) {
    thresholds[`checkout_outcome{status:${status}}`] = ['count>=0'];
  }
  return thresholds;
}
