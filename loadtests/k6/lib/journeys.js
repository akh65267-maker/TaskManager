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

function get(url, name, tags, headers) {
  return http.get(`${BASE_URL}${url}`, { headers, tags: { name, ...tags } });
}

function post(url, body, name, tags, headers) {
  return http.post(`${BASE_URL}${url}`, JSON.stringify(body), { headers, tags: { name, ...tags } });
}

function browse(data, tags) {
  const list = get('/products?page=1&pageSize=12&sort=newest', 'products_list', tags);
  check(list, { 'product list 200': (r) => r.status === 200 }, tags);

  const detail = get(`/products/${data.productId}`, 'product_detail', tags);
  check(detail, { 'product detail 200': (r) => r.status === 200 }, tags);

  const stock = get(`/inventory/${data.productId}`, 'inventory', tags);
  check(stock, { 'inventory 200': (r) => r.status === 200 }, tags);
}

function basket(data, user, tags) {
  const headers = authed(user);
  const add = post('/basket/items', { productId: data.productId, quantity: 1 }, 'basket_add', tags, headers);
  check(add, { 'basket add 200': (r) => r.status === 200 }, tags);

  const view = get('/basket', 'basket_get', tags, headers);
  check(view, { 'basket get 200': (r) => r.status === 200 }, tags);

  // Leave nothing behind: baskets have no TTL, so a long run would otherwise
  // grow Redis for as long as it goes.
  http.del(`${BASE_URL}/basket`, null, { headers, tags: { name: 'basket_clear', ...tags } });
}

function checkout(data, user, tags) {
  const headers = authed(user);
  const startedAt = Date.now();

  const created = post(
    '/orders',
    { items: [{ productId: data.productId, quantity: 1, unitPrice: data.price }] },
    'order_create',
    tags,
    headers,
  );
  if (!check(created, { 'order created 201': (r) => r.status === 201 }, tags)) return;

  const orderId = created.json('id');
  let status = 'Pending';
  const giveUpAt = startedAt + CHECKOUT_WAIT_S * 1000;

  while (status === 'Pending' && Date.now() < giveUpAt) {
    sleep(0.25);
    const poll = get(`/orders/${orderId}`, 'order_status', tags, headers);
    if (poll.status === 200) status = poll.json('status');
  }

  checkoutResolve.add(Date.now() - startedAt, tags);
  checkoutConfirmed.add(status === 'Confirmed', tags);
  checkoutOutcome.add(1, { status, ...tags });
}

// One simulated shopper visit. The mix is configurable (config.js); the default is
// read-heavy, like a storefront.
export function journey(data, tags = {}) {
  const user = currentUser(data);
  const roll = Math.random();

  // Each visit is about one product, picked at random from the ones under test.
  const product = data.products[Math.floor(Math.random() * data.products.length)];
  const view = { productId: product.productId, price: product.price };

  if (roll < MIX_BROWSE) browse(view, tags);
  else if (roll < MIX_BROWSE + MIX_BASKET) basket(view, user, tags);
  else checkout(view, user, tags);

  // Think time. Without it a VU is a tight loop and "20 users" means far more load
  // than 20 people would generate.
  sleep(0.5 + Math.random());
}
