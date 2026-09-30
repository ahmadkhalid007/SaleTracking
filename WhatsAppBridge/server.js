import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { randomBytes } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import qrcode from 'qrcode';
import { createSender, SendError } from './sender.js';
import { createBridgeServer } from './http-server.js';

const root = dirname(fileURLToPath(import.meta.url));
const dataDir = resolve(process.env.WHATSAPP_BRIDGE_DATA_DIR || resolve(root, '../.local/whatsapp'));
mkdirSync(dataDir, { recursive: true });
process.env.PUPPETEER_CACHE_DIR ||= resolve(dataDir, 'browser');
const { default: whatsapp } = await import('whatsapp-web.js');
const { Client, LocalAuth } = whatsapp;
const tokenFile = resolve(process.env.WHATSAPP_BRIDGE_TOKEN_FILE || resolve(dataDir, 'bridge-token'));
mkdirSync(dirname(tokenFile), { recursive: true });
if (!existsSync(tokenFile)) writeFileSync(tokenFile, randomBytes(32).toString('hex'), { mode: 0o600, flag: 'wx' });
const apiKey = process.env.WHATSAPP_BRIDGE_API_KEY || readFileSync(tokenFile, 'utf8').trim();
if (apiKey.length < 32) throw new Error('The bridge API key must have at least 32 characters.');
const port = Number(process.env.WHATSAPP_BRIDGE_PORT || 3210);
const delayMs = Number(process.env.WHATSAPP_SEND_DELAY_MS || 3000);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Invalid bridge port.');
if (!Number.isFinite(delayMs) || delayMs < 1000 || delayMs > 60000) throw new Error('Send delay must be 1000 to 60000 milliseconds.');

let state = 'starting';
let qrImage = null;
let senderNumber = null;
let client;
let initializing = false;
let generation = 0;
let readyTimer;
let disconnecting;
const getStatus = () => ({ state, connected: state === 'ready', senderNumber });

async function startClient(force = false) {
  if (disconnecting || initializing || (!force && state === 'ready')) return;
  initializing = true;
  const current = ++generation;
  clearInterval(readyTimer);
  state = 'starting';
  qrImage = null;
  senderNumber = null;
  try {
    if (client) {
      await client.destroy().catch(() => {});
      client = null;
    }
    client = new Client({
      authStrategy: new LocalAuth({ dataPath: resolve(dataDir, 'session'), clientId: 'saletrack' }),
      puppeteer: {
        headless: true,
        protocolTimeout: 180000,
        timeout: 120000,
        args: [
          '--no-sandbox',
          '--disable-setuid-sandbox',
          '--disable-dev-shm-usage',
          '--disable-accelerated-2d-canvas',
          '--no-first-run',
          '--disable-gpu'
        ],
        ...(process.env.WHATSAPP_CHROME_PATH ? { executablePath: process.env.WHATSAPP_CHROME_PATH } : {})
      },
      deviceName: 'SaleTrack Alerts',
      takeoverOnConflict: false,
      webVersionCache: { type: 'local', path: resolve(dataDir, 'web-cache') }
    });
    client.on('qr', async value => {
      try {
        const data = await qrcode.toDataURL(value, { width: 300, margin: 2 });
        if (current !== generation || !['starting', 'qr'].includes(state)) return;
        qrImage = data;
        state = 'qr';
        console.log('Scan the QR code in SaleTrack Settings.');
      } catch { if (current === generation) state = 'error'; }
    });
    client.on('loading_screen', (percent, message) => {
      console.log(`WhatsApp loading: ${percent}% - ${message}`);
    });
    client.on('authenticated', () => {
      if (current !== generation) return;
      state = 'connecting';
      qrImage = null;
      console.log('WhatsApp authenticated.');
      clearInterval(readyTimer);
      readyTimer = setInterval(async () => {
        if (current !== generation || state !== 'connecting') { clearInterval(readyTimer); return; }
        if (state !== 'connecting' || !client?.pupPage) return;
        try {
          const userWid = await client.pupPage.evaluate(() => {
            try {
              const u = window.require?.('WAWebUserPrefsMeUser')?.getMaybeMePnUser?.()
                || window.require?.('WAWebConnModel')?.Conn?.wid;
              return u?.user || null;
            } catch { return null; }
          });
          if (userWid && current === generation && state === 'connecting') {
            clearInterval(readyTimer);
            state = 'ready';
            qrImage = null;
            senderNumber = `+${userWid}`;
            console.log(`WhatsApp is connected and ready for price alerts (${senderNumber}).`);
          }
        } catch {}
      }, 2000);
    });
    client.on('ready', () => {
      if (current !== generation) return;
      clearInterval(readyTimer);
      state = 'ready';
      qrImage = null;
      senderNumber = client.info?.wid?.user ? `+${client.info.wid.user}` : (senderNumber || null);
      console.log('WhatsApp is connected and ready for price alerts.');
    });
    client.on('auth_failure', () => {
      if (current !== generation) return;
      clearInterval(readyTimer);
      state = 'auth_failure'; qrImage = null; senderNumber = null;
    });
    client.on('disconnected', () => {
      if (current !== generation) return;
      clearInterval(readyTimer);
      state = 'disconnected'; qrImage = null; senderNumber = null;
      console.log('WhatsApp disconnected. Reconnect in SaleTrack Settings.');
    });
    await client.initialize();
  } catch (error) {
    if (current !== generation) return;
    state = 'error';
    qrImage = null;
    console.error(`WhatsApp could not start (${error?.name || 'Error'}): ${error?.message || String(error)}. See WHATSAPP_SETUP.md for browser setup.`);
    await client?.destroy().catch(() => {});
  } finally { if (current === generation) initializing = false; }
}

function disconnectClient() {
  if (disconnecting) return disconnecting;
  if (state === 'disconnected' && !client) return Promise.resolve();
  if (state !== 'ready' || !client)
    throw new SendError('not_connected', 'Refresh the connection status before disconnecting.', 409);

  const linkedClient = client;
  // Invalidate callbacks and stop queued alerts before logging out the linked device.
  ++generation;
  clearInterval(readyTimer);
  client = null;
  state = 'disconnecting';
  qrImage = null;
  senderNumber = null;
  disconnecting = (async () => {
    try {
      await linkedClient.logout();
      await linkedClient.destroy();
      state = 'disconnected';
      console.log('WhatsApp was disconnected by an administrator.');
    } catch (error) {
      await linkedClient.destroy().catch(() => {});
      state = 'error';
      throw error;
    } finally {
      initializing = false;
      disconnecting = null;
    }
  })();
  return disconnecting;
}

const sender = createSender({
  client: {
    getNumberId: async (number) => {
      try {
        const id = await client.getNumberId(number);
        if (id?._serialized) return id;
      } catch (err) {
        console.warn(`WhatsApp getNumberId check failed for ${number}: ${err?.message || err}. Falling back to formatted ID.`);
      }
      const clean = String(number).replace(/[^0-9]/g, '');
      return clean ? { _serialized: `${clean}@c.us`, user: clean } : null;
    },
    sendMessage: async (chatId, message, options = {}) => {
      console.log(`sendMessage attempting to send to: ${chatId}`);
      if (!client) {
        throw new Error('WhatsApp client is not initialized');
      }
      let result;
      let sendError = null;
      try {
        result = await client.sendMessage(chatId, message, options);
      } catch (err) {
        console.warn(`client.sendMessage threw: ${err?.message || err}`);
        sendError = err;
      }

      if (result?.id?._serialized) {
        return result;
      }

      // If client.sendMessage returned undefined (common in modern WhatsApp Web where Msg.get is async),
      // check the active chat in the page to find the outgoing message ID:
      if (client?.pupPage && !client.pupPage.isClosed?.()) {
        try {
          const fallbackId = await client.pupPage.evaluate(async (id) => {
            try {
              const wid = window.require('WAWebWidFactory').createWid(id);
              const chat = window.require('WAWebCollections').Chat.get(wid);
              const msgs = chat?.msgs?.getModelsArray();
              if (msgs && msgs.length > 0) {
                for (let i = msgs.length - 1; i >= Math.max(0, msgs.length - 5); i--) {
                  const m = msgs[i];
                  if (m?.id?._serialized && (m.self === 'out' || m.id?.fromMe)) {
                    return m.id._serialized;
                  }
                }
              }
            } catch {}
            return null;
          }, chatId);

          if (fallbackId) {
            console.log(`Resolved submitted message ID from chat: ${fallbackId}`);
            return { id: { _serialized: fallbackId } };
          }
        } catch (e) {
          console.warn('Page evaluation error while reading message ID:', e.message);
        }
      }

      if (sendError) {
        throw sendError;
      }

      throw new Error(`Failed to confirm message delivery to ${chatId}`);
    }
  },
  isReady: () => state === 'ready',
  journalPath: resolve(dataDir, 'receipts.json'),
  delayMs
});
const server = createBridgeServer({
  apiKey, port, getStatus, getQr: () => qrImage,
  reconnect: async () => {
    if (disconnecting) throw new SendError('connection_busy', 'Wait for WhatsApp to finish disconnecting.', 409);
    void startClient(true);
  },
  disconnect: disconnectClient, send: sender.send
});
server.on('error', error => {
  console.error(`Could not start the internal WhatsApp service (${error.code}).`);
  process.exit(1);
});
server.listen(port, '127.0.0.1', () => {
  console.log('WhatsApp service is running. Open SaleTrack Settings to connect the sender.');
  void startClient();
});
async function shutdown() {
  ++generation;
  clearInterval(readyTimer);
  state = 'stopping';
  server.close();
  await client?.destroy().catch(() => {});
  process.exit(0);
}
process.once('SIGINT', shutdown);
process.once('SIGTERM', shutdown);
// Stop the browser when the application host disappears, including a debugger stop.
if (process.env.WHATSAPP_PARENT_PID) {
  const parentPid = Number(process.env.WHATSAPP_PARENT_PID);
  setInterval(() => {
    try { process.kill(parentPid, 0); }
    catch { void shutdown(); }
  }, 3000).unref();
}
