// 最小 UPnP 客户端：SSDP 发现音响的 MediaRenderer，然后打 SOAP 动作。
// 只读动作（默认）: GetVersion / GetProtocolInfo / GetCurrentTransportActions / GetVolume / GetMute
// 用法: node upnp.mjs            # 发现 + 只读探测
//       node upnp.mjs url <http://...>   # 让音响播这个 URL（SetAVTransportURI + Play）
//       node upnp.mjs stop               # 停
import dgram from 'node:dgram';
import http from 'node:http';

const CONTROL = 'urn:schemas-upnp-org:service:AVTransport:1';
const RCTRL = 'urn:schemas-upnp-org:service:RenderingControl:1';

function discover(timeoutMs = 2500) {
  return new Promise(res => {
    const s = dgram.createSocket('udp4');
    const hits = new Map();
    s.on('message', m => {
      const t = m.toString('latin1');
      const loc = (/LOCATION: (\S+)/i.exec(t) || [])[1];
      if (loc) hits.set(loc, { st: (/ST: (\S+)/i.exec(t) || [])[1], from: s.address() });
    });
    s.on('error', () => { });
    s.bind(0, () => {
      const q = ['M-SEARCH * HTTP/1.1', 'HOST: 239.255.255.250:1900', 'MAN: "ssdp:discover"', 'MX: 1', 'ST: ssdp:all', '', ''].join('\r\n');
      s.send(q, 0, q.length, 1900, '239.255.255.250');
    });
    setTimeout(() => { try { s.close(); } catch { } res([...hits.keys()]); }, timeoutMs);
  });
}

const get = url => new Promise(res => {
  http.get(url, r => { let b = ''; r.on('data', d => b += d); r.on('end', () => res(b)); }).on('error', e => res('<' + e.code + '>'));
  setTimeout(() => res('<timeout>'), 4000);
});

function soap(url, svc, action, body) {
  return new Promise(res => {
    const u = new URL(url);
    const xml = `<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><u:${action} xmlns:u="${svc}">${body}</u:${action}></s:Body></s:Envelope>`;
    const req = http.request({ host: u.hostname, port: u.port || 80, path: u.pathname, method: 'POST',
      headers: { 'Content-Type': 'text/xml; charset="utf-8"', SOAPAction: `"${svc}#${action}"`, 'Content-Length': Buffer.byteLength(xml) }, timeout: 5000 },
      r => { let b = ''; r.on('data', d => b += d); r.on('end', () => res({ code: r.statusCode, body: b })); });
    req.on('error', e => res({ err: e.code }));
    req.on('timeout', () => { req.destroy(); res({ err: 'timeout' }); });
    req.write(xml); req.end();
  });
}
const clean = b => (b || '').replace(/<\?xml[^>]*\?>/g, '').replace(/<\/?(s|s:Envelope|s:Body|u:[A-Za-z]+)[^>]*>/g, '').replace(/\s+/g, ' ').trim();

(async () => {
  const locs = await discover();
  const devLoc = locs.find(l => /dev\/.*desc|.*\.xml|devdb/i.test(l)) || locs[0];
  if (!devLoc) { console.log('SSDP 没发现任何设备'); process.exit(1); }
  console.log(`LOCATION: ${devLoc}`);
  const desc = await get(devLoc);
  const base = new URL(devLoc);
  const abs = p => new URL(p, base).href;
  const av = /<serviceType>urn:schemas-upnp-org:service:AVTransport:1<\/serviceType>[\s\S]*?<controlURL>([^<]+)<\/controlURL>/.exec(desc);
  const rc = /<serviceType>urn:schemas-upnp-org:service:RenderingControl:1<\/serviceType>[\s\S]*?<controlURL>([^<]+)<\/controlURL>/.exec(desc);
  console.log(`AVTransport 控制: ${av ? abs(av[1]) : '(描述里没有 AVTransport)'}`);
  console.log(`RenderingControl: ${rc ? abs(rc[1]) : '(无)'}`);
  if (!av) { console.log(desc.slice(0, 700)); process.exit(1); }
  const avUrl = abs(av[1]), rcUrl = rc ? abs(rc[1]) : null;

  const mode = process.argv[2];
  if (mode === 'url') {
    const u = process.argv[3];
    const meta = `<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"><item id="0" parent="0" restricted="1"><dc:title>Gravity++</dc:title><upnp:class>object.item.audioItem.musicTrack</upnp:class><res protocolInfo="http-get:*:audio/mpeg:*">${u}</res></item></DIDL-Lite>`;
    const r1 = await soap(avUrl, CONTROL, 'SetAVTransportURI', `<InstanceID>0</InstanceID><CurrentURI>${u}</CurrentURI><CurrentURIMetaData>${meta.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')}</CurrentURIMetaData>`);
    console.log('SetAVTransportURI →', r1.code || r1.err, clean(r1.body).slice(0, 160));
    const r2 = await soap(avUrl, CONTROL, 'Play', '<InstanceID>0</InstanceID><Speed>1</Speed>');
    console.log('Play →', r2.code || r2.err, clean(r2.body).slice(0, 160));
    process.exit(0);
  }
  if (mode === 'stop') {
    const r = await soap(avUrl, CONTROL, 'Stop', '<InstanceID>0</InstanceID>');
    console.log('Stop →', r.code || r.err, clean(r.body).slice(0, 160));
    process.exit(0);
  }
  console.log('\n--- 只读动作探测 ---');
  for (const a of ['GetVersion', 'GetProtocolInfo', 'GetCurrentTransportActions']) {
    const r = await soap(avUrl, CONTROL, a, a === 'GetVersion' ? '' : '<InstanceID>0</InstanceID>');
    console.log(`AVTransport.${a}: ${r.code || r.err}  ${(r.body ? clean(r.body).slice(0, 220) : '')}`);
  }
  if (rcUrl) for (const a of ['GetVolume', 'GetMute']) {
    const r = await soap(rcUrl, RCTRL, a, '<InstanceID>0</InstanceID><Channel>Master</Channel>');
    console.log(`RenderingControl.${a}: ${r.code || r.err}  ${(r.body ? clean(r.body).slice(0, 120) : '')}`);
  }
  process.exit(0);
})();
