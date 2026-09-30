import { createHash } from 'node:crypto';
import { existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';

export class SendError extends Error {
  constructor(code, message, status = 503) {
    super(message);
    this.code = code;
    this.status = status;
  }
}

// The receipt journal prevents a timed-out HTTP request from causing another send.
// An interrupted/ambiguous send stays uncertain until the operator checks WhatsApp.
export function createSender({ client, isReady, journalPath, delayMs = 3000, sleep = delay, now = Date.now }) {
  const receipts = existsSync(journalPath)
    ? JSON.parse(readFileSync(journalPath, 'utf8')) : {};
  const pending = new Map();
  let tail = Promise.resolve();
  let lastSendAt = null;

  function save() {
    writeFileSync(`${journalPath}.tmp`, JSON.stringify(receipts), { mode: 0o600 });
    renameSync(`${journalPath}.tmp`, journalPath);
  }

  function send(input) {
    const { to, message, idempotencyKey } = input ?? {};
    if (typeof to !== 'string' || !/^\+[1-9][0-9]{7,14}$/.test(to))
      return Promise.reject(new SendError('invalid_number', 'Use a WhatsApp number with + and country code.', 400));
    if (typeof message !== 'string' || !message.trim() || message.length > 4096)
      return Promise.reject(new SendError('invalid_message', 'The alert must contain 1 to 4096 characters.', 400));
    if (typeof idempotencyKey !== 'string' || !/^[a-zA-Z0-9:_-]{1,120}$/.test(idempotencyKey))
      return Promise.reject(new SendError('invalid_key', 'A valid alert reference is required.', 400));

    const key = createHash('sha256').update(idempotencyKey).digest('hex');
    const recipientHash = createHash('sha256').update(to).digest('hex');
    const previous = receipts[key];
    if (previous && previous.recipientHash !== recipientHash)
      return Promise.reject(new SendError('recipient_changed', 'The recipient changed for an existing alert. Check its send history.', 409));
    if (pending.has(key)) {
      const queued = pending.get(key);
      return queued.recipientHash === recipientHash ? queued.promise
        : Promise.reject(new SendError('recipient_changed', 'This alert is already queued for another recipient.', 409));
    }
    if (previous?.state === 'submitted')
      return Promise.resolve({ success: true, messageId: previous.messageId });
    if (previous)
      return Promise.reject(new SendError('send_uncertain', 'A previous send may have succeeded. Check WhatsApp before creating another alert.', 409));
    if (!isReady())
      return Promise.reject(new SendError('disconnected', 'Ask the administrator to link the WhatsApp sender in SaleTrack Settings.'));
    if (pending.size >= 100)
      return Promise.reject(new SendError('queue_full', 'The WhatsApp queue is busy. Retry at the next price check.', 429));

    const promise = tail.then(async () => {
      if (!isReady()) throw new SendError('disconnected', 'WhatsApp disconnected before this alert could send.');
      if (lastSendAt !== null) await sleep(Math.max(0, delayMs - (now() - lastSendAt)));
      if (!isReady()) throw new SendError('disconnected', 'WhatsApp disconnected before this alert could send.');
      let recipient;
      try { recipient = await client.getNumberId(to.slice(1)); }
      catch { throw new SendError('lookup_failed', 'Could not check the recipient. Retry at the next price check.'); }
      if (!recipient?._serialized)
        throw new SendError('not_on_whatsapp', 'The recipient number is not registered with WhatsApp.', 422);

      receipts[key] = { state: 'sending', recipientHash, attemptedAt: new Date(now()).toISOString() };
      save(); // Never start a send if its receipt cannot first be persisted.
      lastSendAt = now();
      try {
        const result = await client.sendMessage(recipient._serialized, message, { sendSeen: false });
        const messageId = result?.id?._serialized;
        if (!messageId) throw new Error('No submission ID');
        receipts[key] = { ...receipts[key], state: 'submitted', messageId };
        save();
        return { success: true, messageId };
      } catch (sendError) {
        console.error('client.sendMessage error details:', sendError?.stack || sendError?.message || sendError);
        // Do not automatically repeat a send whose result is ambiguous.
        if (receipts[key].state !== 'submitted') receipts[key].state = 'uncertain';
        save();
        throw new SendError('send_uncertain', 'WhatsApp did not confirm the result. Check the business account before sending this alert again.', 409);
      }
    });
    pending.set(key, { promise, recipientHash });
    tail = promise.catch(() => {}).finally(() => pending.delete(key));
    return promise;
  }
  return { send };
}
