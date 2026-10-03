import http from 'k6/http';
import { check, sleep } from 'k6';
import exec from 'k6/execution';
import { Counter, Rate, Trend } from 'k6/metrics';
import { BASE_URL, CHECKOUT_WAIT_S, MIX_BASKET, MIX_BROWSE } from './config.js';
import { login } from './setup.js';

// How long from placing an order until it left Pending, in ms. The saga makes this
// asynchronous, so the HTTP latency of POST /orders alone says nothing about it.
export const checkoutResolve = new Trend('checkout_resolve_seconds', true);
export const checkoutConfirmed = new Rate('checkout_confirmed');
export const checkoutOutcome = new Counter('checkout_outcome');

const JSON_HEADERS = { 'Content-Type': 'application/json' };

// Tokens last one hour. A soak test outlives that, so each VU re-logs-in shortly
// before expiry - staggered by VU id so they do not all hit the 10/minute login
// limit in the same minute.
const REFRESH_AFTER_MS = 45 * 60 * 1000;
let me = null;

function currentUser(data) {
  if (me === null) {
    const base = data.users[(exec.vu.idInTest - 1) % data.users.length];
    me = { ...base };
  }
  const jitter = (exec.vu.idInTest % 30) * 20 * 1000;
  if (Date.now() - me.issuedAt > REFRESH_AFTER_MS + jitter) {
    me.token = login(me.email, me.password).token;
    me.issuedAt = Date.now();
  }
  return me;
}

const authed = (user) => ({ ...JSON_HEADERS, Authorization: `Bearer ${user.token}` });

// `tagsNow` returns the tags for THIS moment (the phase the run is in right now).
// It is a function, not a value, on purpose: a single visit can span a phase boundary
// (a checkout waits up to 30 s), and tagging the whole visit with the phase it started
// in would count requests made after a fault began as if they were made before it.
function get(url, name, tagsNow, headers) {
  return http.get(`${BASE_URL}${url}`, { headers, tags: { name, ...tagsNow() } });
}

function post(url, body, name, tagsNow, headers) {
  return http.post(`${BASE_URL}${url}`, JSON.stringify(body), { headers, tags: { name, ...tagsNow() } });
}

function browse(data, tagsNow) {
  const list = get('/products?page=1&pageSize=12&sort=newest', 'products_list', tagsNow);
  check(list, { 'product list 200': (r) => r.status === 200 }, tagsNow());

  const detail = get(`/products/${data.productId}`, 'product_detail', tagsNow);
  check(detail, { 'product detail 200': (r) => r.status === 200 }, tagsNow());

  const stock = get(`/inventory/${data.productId}`, 'inventory', tagsNow);
  check(stock, { 'inventory 200': (r) => r.status === 200 }, tagsNow());
}

function basket(data, user, tagsNow) {
  const headers = authed(user);
  const add = post('/basket/items', { productId: data.productId, quantity: 1 }, 'basket_add', tagsNow, headers);
  check(add, { 'basket add 200': (r) => r.status === 200 }, tagsNow());

  const view = get('/basket', 'basket_get', tagsNow, headers);
  check(view, { 'basket get 200': (r) => r.status === 200 }, tagsNow());

  // Leave nothing behind: baskets have no TTL, so a long run would otherwise
  // grow Redis for as long as it goes.
  http.del(`${BASE_URL}/basket`, null, { headers, tags: { name: 'basket_clear', ...tagsNow() } });
}

function checkout(data, user, tagsNow) {
  const headers = authed(user);
  const startedAt = Date.now();
  const phaseAtStart = tagsNow().phase;

  const created = post(
    '/orders',
    { items: [{ productId: data.productId, quantity: 1, unitPrice: data.price }] },
    'order_create',
    tagsNow,
    headers,
  );
  if (!check(created, { 'order created 201': (r) => r.status === 201 }, tagsNow())) return;

  const orderId = created.json('id');
  let status = 'Pending';
  const giveUpAt = startedAt + CHECKOUT_WAIT_S * 1000;

  while (status === 'Pending' && Date.now() < giveUpAt) {
    sleep(0.25);
    const poll = get(`/orders/${orderId}`, 'order_status', tagsNow, headers);
    if (poll.status === 200) status = poll.json('status');
  }

  // A checkout belongs to a phase only if it began and ended inside it. One that
  // straddles a boundary says nothing clean about either side - a checkout begun just
  // before a fault is a victim of the fault, not evidence about the baseline - so it
  // gets its own tag and no phase threshold sees it.
  const phaseAtEnd = tagsNow().phase;
  const tags = phaseAtStart === phaseAtEnd ? (phaseAtStart ? { phase: phaseAtStart } : {}) : { phase: 'straddle' };

  checkoutResolve.add(Date.now() - startedAt, tags);
  checkoutConfirmed.add(status === 'Confirmed', tags);
  checkoutOutcome.add(1, { status, ...tags });
}

// One simulated shopper visit. The mix is configurable (config.js); the default is
// read-heavy, like a storefront. `phaseOf` (optional) names the phase the run is in
// at the moment it is called.
export function journey(data, phaseOf = null) {
  const tagsNow = () => (phaseOf ? { phase: phaseOf() } : {});
  const user = currentUser(data);
  const roll = Math.random();

  // Each visit is about one product, picked at random from the ones under test.
  const product = data.products[Math.floor(Math.random() * data.products.length)];
  const view = { productId: product.productId, price: product.price };

  if (roll < MIX_BROWSE) browse(view, tagsNow);
  else if (roll < MIX_BROWSE + MIX_BASKET) basket(view, user, tagsNow);
  else checkout(view, user, tagsNow);

  // Think time. Without it a VU is a tight loop and "20 users" means far more load
  // than 20 people would generate.
  sleep(0.5 + Math.random());
}
