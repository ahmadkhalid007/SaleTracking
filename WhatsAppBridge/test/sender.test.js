import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createSender } from '../sender.js';

function fixture(t, overrides = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'saletrack-sender-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const calls = [];
  let now = 1000;
  const client = {
    getNumberId: async phone => ({ _serialized: `${phone}@c.us` }),
    sendMessage: async (to, message, options) => {
      calls.push({ to, message, options, at: now });
      return { id: { _serialized: `message-${calls.length}` } };
    }
  };
  const options = {
    client, isReady: () => true, journalPath: join(dir, 'receipts.json'), delayMs: 3000,
    now: () => now, sleep: async ms => { now += ms; }, ...overrides
  };
  return { client, calls, options, sender: createSender(options) };
}
const alert = (key = 'TRK-TEST:1000') => ({ to: '+923001234567', message: '🎉 Target reached.', idempotencyKey: key });

test('sends text to a registered WhatsApp recipient and returns its ID', async t => {
  const f = fixture(t);
  assert.deepEqual(await f.sender.send(alert()), { success: true, messageId: 'message-1' });
  assert.equal(f.calls[0].to, '923001234567@c.us');
  assert.equal(f.calls[0].message, '🎉 Target reached.');
  assert.equal(f.calls[0].options.sendSeen, false);
});
test('duplicate simultaneous requests and restarted bridge return the same receipt', async t => {
  const f = fixture(t);
  const [first, second] = await Promise.all([f.sender.send(alert()), f.sender.send(alert())]);
  assert.deepEqual(first, second);
  assert.equal(f.calls.length, 1);
  const restarted = createSender(f.options);
  assert.deepEqual(await restarted.send({ ...alert(), message: 'A different current price' }), first);
  assert.equal(f.calls.length, 1);
});
test('separate recipients are queued and paced', async t => {
  const f = fixture(t);
  await Promise.all([f.sender.send(alert('track-one')), f.sender.send(alert('track-two')), f.sender.send(alert('track-three'))]);
  assert.deepEqual(f.calls.map(x => x.at), [1000, 4000, 7000]);
});
test('disconnected client never sends; reconnect allows a later attempt', async t => {
  let ready = false;
  const f = fixture(t, { isReady: () => ready });
  await assert.rejects(f.sender.send(alert()), { code: 'disconnected' });
  assert.equal(f.calls.length, 0);
  ready = true;
  assert.equal((await f.sender.send(alert())).success, true);
});
test('invalid phone and unregistered recipient do not send', async t => {
  const f = fixture(t);
  await assert.rejects(f.sender.send({ ...alert(), to: '123' }), { code: 'invalid_number' });
  f.client.getNumberId = async () => null;
  await assert.rejects(f.sender.send(alert()), { code: 'not_on_whatsapp' });
  assert.equal(f.calls.length, 0);
});
test('uncertain send is not automatically repeated, including after restart', async t => {
  const f = fixture(t);
  let sends = 0;
  f.client.sendMessage = async () => { sends++; throw new Error('connection lost after submission'); };
  await assert.rejects(f.sender.send(alert()), { code: 'send_uncertain' });
  await assert.rejects(createSender(f.options).send(alert()), { code: 'send_uncertain' });
  assert.equal(sends, 1);
});
test('a failed recipient lookup does not block other alerts or permanently suppress retries', async t => {
  const f = fixture(t);
  const lookup = f.client.getNumberId;
  f.client.getNumberId = async () => { throw new Error('offline'); };
  await assert.rejects(f.sender.send(alert()), { code: 'lookup_failed' });
  f.client.getNumberId = lookup;
  await f.sender.send(alert('another-track'));
  await f.sender.send(alert());
  assert.equal(f.calls.length, 2);
});
test('an existing alert cannot be redirected to a different recipient', async t => {
  const f = fixture(t);
  await f.sender.send(alert());
  await assert.rejects(f.sender.send({ ...alert(), to: '+14155552671' }), { code: 'recipient_changed' });
  assert.equal(f.calls.length, 1);
});
