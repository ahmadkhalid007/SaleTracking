import { createServer } from 'node:http';
import { timingSafeEqual } from 'node:crypto';
import { SendError } from './sender.js';

export function createBridgeServer({ apiKey, port, getStatus, getQr, reconnect, disconnect, send }) {
  const allowedHosts = new Set([`127.0.0.1:${port}`, `localhost:${port}`]);
  const validToken = value => {
    const actual = Buffer.from(value || '');
    const expected = Buffer.from(`Bearer ${apiKey}`);
    return actual.length === expected.length && timingSafeEqual(actual, expected);
  };
  const json = (res, status, value) => {
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(value));
  };

  return createServer(async (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('Content-Security-Policy', "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");
    if (!allowedHosts.has(req.headers.host)) return json(res, 403, { error: 'Invalid host.' });
    const origin = req.headers.origin;
    if ((origin && origin !== `http://${req.headers.host}`) || req.headers['sec-fetch-site'] === 'cross-site')
      return json(res, 403, { error: 'Local access only.' });
    const path = new URL(req.url, `http://${req.headers.host}`).pathname;
    // Connection management is available only through SaleTrack's admin controller.
    const isApi = path.startsWith('/api/') && validToken(req.headers.authorization);
    if (!isApi) return json(res, 401, { error: 'Authorization required.' });

    try {
      if (req.method === 'GET' && path === '/api/status')
        return json(res, 200, getStatus());
      if (req.method === 'GET' && path === '/api/connection')
        return json(res, 200, { ...getStatus(), qr: getQr() });
      if (req.method === 'POST' && path === '/api/reconnect') {
        await reconnect();
        return json(res, 202, { success: true });
      }
      if (req.method === 'POST' && path === '/api/disconnect') {
        await disconnect();
        return json(res, 200, { success: true });
      }
      if (req.method === 'POST' && path === '/api/messages') {
        if (!(req.headers['content-type'] || '').startsWith('application/json'))
          return json(res, 415, { error: 'Expected JSON.' });
        const chunks = [];
        let size = 0;
        for await (const chunk of req) {
          size += chunk.length;
          if (size > 16384) return json(res, 413, { error: 'Message too large.' });
          chunks.push(chunk);
        }
        let input;
        try { input = JSON.parse(Buffer.concat(chunks).toString('utf8')); }
        catch { return json(res, 400, { error: 'Invalid JSON.' }); }
        return json(res, 200, await send(input));
      }
      return json(res, 404, { error: 'Not found.' });
    } catch (error) {
      if (error instanceof SendError)
        return json(res, error.status, { success: false, code: error.code, error: error.message });
      console.error('WhatsApp request failed; no message content or credentials are logged.');
      return json(res, 503, { success: false, code: 'bridge_error', error: 'The WhatsApp connection needs attention.' });
    }
  });
}
