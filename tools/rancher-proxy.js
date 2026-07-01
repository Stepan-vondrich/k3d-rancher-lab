// Docasny reverzni proxy: http://localhost:8900 -> https://localhost:8443 (Rancher, self-signed)
// Umoznuje preview-browseru (ktery odmita self-signed cert) zobrazit Rancher UI pro screenshot.
const http = require('http');
const https = require('https');

const THOST = 'localhost', TPORT = 8443, LPORT = 8900;
const BACKEND = 'https://' + THOST + ':' + TPORT;

// Nechavame Host/Origin na localhost:8900, aby Rancher generoval UI URL na proxy (ne na backend).
// X-Forwarded-* rekne Rancheru spravny vnejsi endpoint.
function reqHeaders(req) {
  const h = { ...req.headers };
  h['x-forwarded-host'] = 'localhost:' + LPORT;
  h['x-forwarded-proto'] = 'http';
  h['x-forwarded-port'] = '' + LPORT;
  return h;
}

function fixHeaders(h) {
  const out = { ...h };
  delete out['strict-transport-security'];
  if (out['set-cookie']) {
    out['set-cookie'] = out['set-cookie'].map(c =>
      c.replace(/;\s*Secure/gi, '').replace(/;\s*SameSite=None/gi, '; SameSite=Lax'));
  }
  if (out['location']) {
    out['location'] = out['location']
      .replace('https://localhost:8443', 'http://localhost:' + LPORT)
      .replace('https://localhost', 'http://localhost:' + LPORT);
  }
  return out;
}

const server = http.createServer((req, res) => {
  // UI plugins nejsou nainstalovane a backend na to obcas resetuje spojeni -> vratime prazdny seznam
  if (req.url.startsWith('/v1/uiplugins')) {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ type: 'collection', resourceType: 'uiplugins', data: [] }));
    return;
  }
  const opts = {
    host: THOST, port: TPORT, path: req.url, method: req.method,
    headers: reqHeaders(req), rejectUnauthorized: false,
  };
  const preq = https.request(opts, pres => {
    res.writeHead(pres.statusCode, fixHeaders(pres.headers));
    pres.pipe(res);
  });
  preq.on('error', e => { res.writeHead(502); res.end('proxy error: ' + e.message); });
  req.pipe(preq);
});

server.on('upgrade', (req, socket, head) => {
  const opts = {
    host: THOST, port: TPORT, path: req.url, method: req.method,
    headers: reqHeaders(req), rejectUnauthorized: false,
  };
  const preq = https.request(opts);
  preq.on('upgrade', (pres, psocket) => {
    const lines = ['HTTP/1.1 101 Switching Protocols'];
    for (const [k, v] of Object.entries(pres.headers)) lines.push(k + ': ' + v);
    socket.write(lines.join('\r\n') + '\r\n\r\n');
    psocket.pipe(socket); socket.pipe(psocket);
    psocket.on('error', () => socket.destroy());
    socket.on('error', () => psocket.destroy());
  });
  preq.on('error', () => socket.destroy());
  preq.end();
});

server.listen(LPORT, () => console.log('rancher-proxy on http://localhost:' + LPORT));
