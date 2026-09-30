import { LIMITS } from './config.js';

// Combines threshold sets, concatenating the criteria when two sets name the same
// metric. Plain object spread would let a later set REPLACE an earlier one, and the
// always-passing observation thresholds from phases.js share metric names with the
// real ones - spreading them would silently switch a real check off.
export function merge(...sets) {
  const merged = {};
  for (const set of sets) {
    for (const [metric, criteria] of Object.entries(set)) {
      merged[metric] = [...(merged[metric] || []), ...criteria];
    }
  }
  return merged;
}

// The pass/fail criteria for a healthy system. Scenarios that expect degradation
// (stress, the fault phase of a chaos run) apply them only to the phases where the
// system is supposed to be healthy.

export function healthy(filter = '') {
  const tag = filter ? `,${filter}` : '';
  return {
    [`http_req_failed${filter ? `{${filter}}` : ''}`]: [`rate<${LIMITS.maxFailedRate}`],
    [`http_req_duration{name:products_list${tag}}`]: [`p(95)<${LIMITS.browseP95}`],
    [`http_req_duration{name:product_detail${tag}}`]: [`p(95)<${LIMITS.detailP95}`],
    [`http_req_duration{name:order_create${tag}}`]: [`p(95)<${LIMITS.orderCreateP95}`],
    [`checkout_resolve_seconds${filter ? `{${filter}}` : ''}`]: [`p(95)<${LIMITS.checkoutP95}`],
    [`checkout_confirmed${filter ? `{${filter}}` : ''}`]: [`rate>${LIMITS.minConfirmedRate}`],
  };
}
