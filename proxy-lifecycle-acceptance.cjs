const { chromium } = require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');
const assert = require('node:assert/strict');
const net = require('node:net');
const fs = require('node:fs');
const origin = 'http://localhost:5173';
const api = 'http://localhost:5000/api/local-https-proxy';
const snapshots = [];
async function state(label) {
  const response = await fetch(api + '/runtime', { headers: { Origin: origin } });
  assert.equal(response.status, 200);
  const s = await response.json();
  snapshots.push({ label, runtimeId:s.runtimeId, port:s.port, edgeProcessId:s.edgeProcessId, edgeRunning:s.edgeRunning, proxyListening:s.proxyListening, status:s.runtimeStatus });
  return s;
}
async function tunnel(port) {
  const echo = net.createServer(socket => socket.on('data', b => socket.write(b)));
  await new Promise(resolve => echo.listen(0, '127.0.0.1', resolve));
  const peer = net.connect(port, '127.0.0.1');
  try {
    await new Promise((resolve, reject) => {
      let established = false, text = '';
      peer.setTimeout(5000, () => reject(new Error('Proxy tunnel timed out')));
      peer.on('error', reject);
      peer.on('connect', () => peer.write(`CONNECT 127.0.0.1:${echo.address().port} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n`));
      peer.on('data', b => {
        text += b.toString();
        if (!established && text.includes('\r\n\r\n')) {
          assert.match(text, /200 Connection Established/);
          established = true; text = ''; peer.write('lifecycle-echo');
        } else if (established && text.includes('lifecycle-echo')) resolve();
      });
    });
  } finally { peer.destroy(); await new Promise(resolve => echo.close(resolve)); }
}
async function authentication(page) {
  await page.getByText('Target Environments', {exact:true}).first().click();
  await page.getByRole('tab', {name:'Authentication',exact:true}).click();
  const disclosure = page.getByRole('button', {name:/View setup details/});
  if (await disclosure.count()) await disclosure.click();
  await page.getByRole('button', {name:'Start authenticated proxy',exact:true}).waitFor();
}
(async () => {
  const browser = await chromium.launch({headless:true});
  const context = await browser.newContext();
  let stopCommands = 0, startCommands = 0;
  await context.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/local-https-proxy/')) {
      if (url.endsWith('/stop')) stopCommands++;
      if (url.endsWith('/start')) startCommands++;
      return route.continue();
    }
    return route.fulfill({status:200,contentType:'application/json',body:'{}'});
  });
  await context.addInitScript(() => {
    if (!localStorage.getItem('birknext:frontend-analysis-settings'))
      localStorage.setItem('birknext:frontend-analysis-settings',JSON.stringify({activeProfileId:'proxy-lifecycle-dev',profiles:[{
        id:'proxy-lifecycle-dev',name:'Proxy lifecycle DEV',environmentType:'Development',targetUrl:'https://example.com/',
        authentication:{authenticatedTestingMethod:'LocalHttpsProxy'}
      }]}));
  });
  const page = await context.newPage();
  let ownedRuntimeId;
  try {
    await page.goto(origin + '/admin/system-settings');
    await authentication(page);
    const before = await state('initial');
    assert.ok(!before.sessionId, 'Acceptance requires no pre-existing proxy; never replace someone else\'s runtime');
    await page.getByRole('button',{name:'Start authenticated proxy',exact:true}).click();
    await page.locator('[data-testid=proxy-state]').filter({hasText:'Running'}).waitFor();
    ownedRuntimeId = (await state('listener started')).runtimeId;
    await page.getByRole('button',{name:'Open browser',exact:true}).click();
    await page.locator('[data-testid=proxy-browser-state]').filter({hasText:'Running'}).waitFor();
    const initial = await state('started');
    assert.equal(initial.edgeRunning,true);
    await tunnel(initial.port);
    async function unchanged(label) {
      const s=await state(label);
      assert.equal(s.runtimeId,initial.runtimeId); assert.equal(s.port,initial.port);
      assert.equal(s.edgeProcessId,initial.edgeProcessId); assert.equal(s.edgeRunning,true); assert.equal(s.proxyListening,true);
      assert.equal(stopCommands,0);
      return s;
    }
    for (let i=1;i<=2;i++) {
      await page.reload(); await authentication(page);
      await page.locator('[data-testid=proxy-state]').filter({hasText:'Running'}).waitFor();
      await unchanged('refresh '+i);
      assert.equal(startCommands,1);
    }
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.clearBrowserCache');
    await page.reload(); await authentication(page);
    await unchanged('hard refresh');
    await page.getByRole('tab',{name:'Browser Discovery',exact:true}).click();
    await page.getByRole('tab',{name:'Authentication',exact:true}).click();
    await unchanged('tab navigation');
    await page.goto(origin + '/dashboard');
    await page.goto(origin + '/admin/system-settings'); await authentication(page);
    await unchanged('route navigation');
    const second = await context.newPage();
    await second.goto(origin + '/admin/system-settings'); await authentication(second);
    await second.locator('[data-testid=proxy-state]').filter({hasText:'Running'}).waitFor();
    await unchanged('second BirkNext tab');
    await second.getByRole('button',{name:'Start authenticated proxy',exact:true}).click();
    await second.locator('[data-testid=proxy-state]').filter({hasText:'Running'}).waitFor();
    await unchanged('repeated Start');
    await tunnel(initial.port);
    await second.getByRole('button',{name:'Stop proxy',exact:true}).click();
    await second.locator('[data-testid=proxy-state]').filter({hasText:'Stopped'}).waitFor();
    await page.locator('[data-testid=proxy-state]').filter({hasText:'Stopped'}).waitFor();
    const stopped = await state('explicit Stop');
    assert.equal(stopped.proxyListening,false); assert.equal(stopped.edgeRunning,false);
    assert.equal(stopCommands,1);
    await second.reload(); await authentication(second);
    await state('refresh after Stop');
    assert.equal(stopCommands,1);
    fs.writeFileSync('proxy-lifecycle-acceptance.json',JSON.stringify({startCommands,stopCommands,snapshots},null,2));
    console.log(JSON.stringify({startCommands,stopCommands,snapshots},null,2));
  } finally {
    fs.writeFileSync('proxy-lifecycle-acceptance.json',JSON.stringify({startCommands,stopCommands,snapshots},null,2));
    await browser.close();
    // Explicit cleanup of only this test's runtime if an assertion failed.
    const current = await state('cleanup check');
    if (ownedRuntimeId && current.runtimeId===ownedRuntimeId && current.sessionId)
      await fetch(api+'/stop',{method:'POST',headers:{Origin:origin,'Content-Type':'application/json'},body:JSON.stringify({sessionId:current.sessionId,profileId:current.profileId,contextFingerprint:current.contextFingerprint})});
  }
})().catch(e=>{console.error(e);process.exitCode=1});
