import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { request as httpRequest } from 'node:http';
import { createBridgeServer } from '../http-server.js';

async function fixture(t, overrides = {}) {
  let sends = 0;
  let reconnects = 0;
  let disconnects = 0;
  const key = 'a'.repeat(64);
  const server = createBridgeServer({
    apiKey: key, port: 3210,
    getStatus: () => ({ state: 'qr', connected: false }), getQr: () => 'private-qr',
    reconnect: async () => { reconnects++; },
    disconnect: async () => { disconnects++; },
    send: async () => { sends++; return { success: true, messageId: 'confirmed-id' }; },
    ...overrides
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(() => { server.closeAllConnections(); server.close(); });
  const request = (path, options = {}) => new Promise((resolve, reject) => {
    const req = httpRequest(`http://127.0.0.1:${server.address().port}${path}`, {
      method: options.method || 'GET', headers: { Host: '127.0.0.1:3210', ...options.headers }
    }, res => {
      const chunks = [];
      res.on('data', chunk => chunks.push(chunk));
      res.on('end', () => resolve({ status: res.statusCode, json: async () => JSON.parse(Buffer.concat(chunks).toString()) }));
    });
    req.on('error', reject);
    req.end(options.body);
  });
  return { key, request, sends: () => sends, reconnects: () => reconnects, disconnects: () => disconnects };
}
test('sending requires the local API key', async t => {
  const f = await fixture(t);
  const response = await f.request('/api/messages', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
  assert.equal(response.status, 401);
  assert.equal(f.sends(), 0);
});
test('QR requires a server token; the former local page cannot bypass admin access', async t => {
  const f = await fixture(t);
  assert.equal((await f.request('/ui/status')).status, 401);
  const local = await f.request('/ui/status', { headers: { 'X-Local-Setup': '1' } });
  assert.equal(local.status, 401);
  assert.equal((await f.request('/')).status, 401);
  assert.equal((await f.request('/api/connection')).status, 401);
  const connection = await f.request('/api/connection', { headers: { Authorization: `Bearer ${f.key}` } });
  assert.equal((await connection.json()).qr, 'private-qr');
  const api = await f.request('/api/status', { headers: { Authorization: `Bearer ${f.key}` } });
  assert.equal((await api.json()).qr, undefined);
});
test('reconnect is a protected POST and the old UI route is disabled', async t => {
  const f = await fixture(t);
  assert.equal((await f.request('/api/reconnect', { method: 'POST' })).status, 401);
  assert.equal((await f.request('/ui/reconnect', { method: 'POST', headers: { 'X-Local-Setup': '1' } })).status, 401);
  const headers = { Authorization: `Bearer ${f.key}` };
  assert.equal((await f.request('/api/reconnect', { headers })).status, 404);
  assert.equal(f.reconnects(), 0);
  assert.equal((await f.request('/api/reconnect', { method: 'POST', headers })).status, 202);
  assert.equal(f.reconnects(), 1);
});
test('disconnect requires an authenticated POST from the local application', async t => {
  const f = await fixture(t);
  assert.equal((await f.request('/api/disconnect', { method: 'POST' })).status, 401);
  const headers = { Authorization: `Bearer ${f.key}` };
  assert.equal((await f.request('/api/disconnect', { headers })).status, 404);
  assert.equal((await f.request('/api/disconnect', {
    method: 'POST', headers: { ...headers, Origin: 'https://example.com' }
  })).status, 403);
  assert.equal(f.disconnects(), 0);
  const result = await f.request('/api/disconnect', { method: 'POST', headers });
  assert.equal(result.status, 200);
  assert.equal((await result.json()).success, true);
  assert.equal(f.disconnects(), 1);
});

test('disconnect waits for logout to finish and reports failures', async t => {
  let finishLogout;
  const logout = new Promise(resolve => { finishLogout = resolve; });
  let started;
  const starting = new Promise(resolve => { started = resolve; });
  let completed = false;
  const f = await fixture(t, { disconnect: async () => { started(); await logout; } });
  const pending = f.request('/api/disconnect', {
    method: 'POST', headers: { Authorization: `Bearer ${f.key}` }
  }).then(response => { completed = true; return response; });
  await starting;
  assert.equal(completed, false);
  finishLogout();
  assert.equal((await pending).status, 200);

  const failing = await fixture(t, { disconnect: async () => { throw new Error('logout failed'); } });
  const response = await failing.request('/api/disconnect', {
    method: 'POST', headers: { Authorization: `Bearer ${failing.key}` }
  });
  assert.equal(response.status, 503);
  assert.equal((await response.json()).success, false);
});

test('cross-site requests and unknown hosts are rejected', async t => {
  const f = await fixture(t);
  assert.equal((await f.request('/ui/status', { headers: { 'X-Local-Setup': '1', Origin: 'https://example.com' } })).status, 403);
  assert.equal((await f.request('/', { headers: { Host: 'attacker.example:3210' } })).status, 403);
});
test('authorized requests return sender results; malformed JSON never sends', async t => {
  const f = await fixture(t);
  const headers = { Authorization: `Bearer ${f.key}`, 'Content-Type': 'application/json' };
  const response = await f.request('/api/messages', { method: 'POST', headers, body: '{"message":"🎉"}' });
  assert.equal(response.status, 200);
  assert.equal((await response.json()).messageId, 'confirmed-id');
  assert.equal((await f.request('/api/messages', { method: 'POST', headers, body: 'invalid' })).status, 400);
  assert.equal(f.sends(), 1);
});
