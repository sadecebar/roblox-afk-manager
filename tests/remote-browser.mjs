import {spawn} from 'node:child_process';
import {readFile, writeFile, mkdir, unlink, rm} from 'node:fs/promises';
import {existsSync} from 'node:fs';
import {resolve, sep} from 'node:path';
import assert from 'node:assert/strict';

const root = resolve(import.meta.dirname, '..');
const artifacts = resolve(root, 'artifacts');
const configuration = process.argv.find(arg => arg.startsWith('--configuration='))?.split('=')[1] ?? 'Release';
assert(['Debug', 'Release'].includes(configuration), 'Use --configuration=Debug or --configuration=Release');
const localDotnet = resolve(root, '.dotnet/dotnet.exe');
const dotnet = process.env.DOTNET_EXE || (existsSync(localDotnet) ? localDotnet : 'dotnet');
const runner = resolve(root, `tests/RobloxAfkManager.Tests/bin/${configuration}/net10.0-windows/RobloxAfkManager.Tests.dll`);
assert(existsSync(runner), 'Build the selected configuration before running browser checks.');
assert(existsSync(resolve(artifacts, 'remote-synthetic-preview.jpg')), 'Run the native fixture checks first.');
const edge = process.env.EDGE_PATH || [process.env['ProgramFiles(x86)'], process.env.ProgramFiles]
  .filter(Boolean).map(dir => resolve(dir, 'Microsoft/Edge/Application/msedge.exe')).find(existsSync);
assert(edge && existsSync(edge), 'Install Microsoft Edge or set EDGE_PATH to its executable.');
const profile = resolve(artifacts, 'remote-browser-profile-' + process.pid);
assert(profile.startsWith(artifacts + sep) && profile !== artifacts, 'Browser profile must remain inside test artifacts.');
const delay = ms => new Promise(r => setTimeout(r, ms));
const fixturePath = resolve(artifacts, 'remote-browser-fixture.json');
const stopPath = resolve(artifacts, 'remote-browser-stop');
await mkdir(artifacts, {recursive:true});
for (const path of [fixturePath, stopPath]) await unlink(path).catch(e => {if (e.code !== 'ENOENT') throw e;});
const fixture = spawn(dotnet, [runner, '--remote-demo'], {cwd:root,windowsHide:true,stdio:['ignore','pipe','pipe']});
let fixtureError;
fixture.on('error', error => { fixtureError = error; });
fixture.stdout.resume(); fixture.stderr.resume();
let browser, socket;
const requests = new Map(); let serial = 0;
const errors = [];
function command(method, params = {}) {
  const id = ++serial;
  return new Promise((resolve, reject) => { requests.set(id,{resolve,reject}); socket.send(JSON.stringify({id,method,params})); });
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', {expression,returnByValue:true,awaitPromise:true});
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
async function until(expression, timeout = 15000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await evaluate(expression)) return; await delay(100); }
  throw new Error('Timed out: ' + expression);
}
async function screenshot(name) {
  const result = await command('Page.captureScreenshot',{format:'png'});
  await writeFile(resolve(artifacts,name),Buffer.from(result.data,'base64'));
}
try {
  let info;
  for(let i=0;i<100;i++) { if(fixtureError) throw fixtureError; try {info=JSON.parse(await readFile(fixturePath,'utf8'));break;} catch {await delay(100);} }
  assert(info,'Fixture failed to start');
  browser = spawn(edge, ['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-port=0','--remote-debugging-address=127.0.0.1','--user-data-dir='+profile,'about:blank'], {windowsHide:true,stdio:'ignore'});
  let browserError;
  browser.on('error', error => { browserError = error; });
  let port;
  for(let i=0;i<150;i++) { if(browserError) throw browserError; try {port=Number((await readFile(resolve(profile,'DevToolsActivePort'),'utf8')).split('\n')[0]);break;} catch {await delay(100);} }
  assert(port,'Headless Edge failed to start');
  const tabs = await (await fetch('http://127.0.0.1:'+port+'/json/list')).json();
  socket = new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);
  await new Promise((r,j)=>{socket.onopen=r;socket.onerror=j;});
  socket.onmessage = event => {
    const message=JSON.parse(event.data);
    if (message.id && requests.has(message.id)) {const p=requests.get(message.id);requests.delete(message.id);message.error?p.reject(message.error):p.resolve(message.result);}
    if(message.method==='Runtime.exceptionThrown') errors.push(message.params.exceptionDetails);
  };
  await command('Page.enable'); await command('Runtime.enable'); await command('Network.enable');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await command('Page.navigate',{url:info.Address});
  await until('document.readyState === "complete" && !!document.getElementById("code")');
  await screenshot('remote-mobile-login.png');
  await evaluate('document.getElementById("code").value="wrong";document.getElementById("login").requestSubmit()');
  await until('document.getElementById("login-error").textContent.includes("incorrect")');
  // This secret belongs to the temporary synthetic fixture, not a real manager session.
  await evaluate('document.getElementById("code").value='+JSON.stringify(info.AccessCode)+';document.getElementById("login").requestSubmit()');
  await until('document.querySelectorAll(".client img:not([hidden])").length === 16');
  assert(await evaluate('document.documentElement.scrollWidth <= innerWidth'),'Mobile overflow');
  assert(await evaluate('document.querySelector(".session").textContent.includes("Fixture <game> & friends")'), 'Game metadata must display as literal text');
  assert(await evaluate('document.querySelector(".session").textContent.includes("1h 15m")'), 'Session duration missing');
  assert(await evaluate('document.querySelectorAll(".state")[1].textContent === "Disconnected"'), 'Disconnect status missing');
  assert.equal(await evaluate('getComputedStyle(document.getElementById("grid")).gridTemplateColumns.split(" ").length'),2);
  await screenshot('remote-mobile-grid.png');
  await evaluate('document.querySelector(".client").click()');
  await until('document.getElementById("detail").open && !document.getElementById("detail-image").hidden');
  await screenshot('remote-mobile-enlarged.png');
  await evaluate('document.getElementById("close-detail").click()');
  await command('Emulation.setDeviceMetricsOverride',{width:1280,height:900,deviceScaleFactor:1,mobile:false});
  await screenshot('remote-web-desktop.png');
  await command('Network.emulateNetworkConditions',{offline:true,latency:0,downloadThroughput:0,uploadThroughput:0});
  await until('document.getElementById("connection").textContent.includes("Disconnected")');
  assert(await evaluate('!document.getElementById("notice").hidden'));
  await command('Network.emulateNetworkConditions',{offline:false,latency:0,downloadThroughput:-1,uploadThroughput:-1});
  await until('document.getElementById("connection").textContent.includes("Connected")');
  await evaluate('document.getElementById("sign-out").click()');
  assert(await evaluate('document.getElementById("dashboard").hidden && sessionStorage.getItem("afk-code") === null'));
  assert.equal(errors.length,0,JSON.stringify(errors));
  console.log('PASS mobile 390x844 login, invalid code, 16 images, two columns, enlargement, desktop layout, offline/reconnect, disconnect; no JS exceptions.');
} finally {
  await writeFile(stopPath,'stop');
  if(socket?.readyState===1) {command('Browser.close').catch(()=>{});await delay(500);socket.close();}
  if(browser && browser.exitCode===null) browser.kill();
  await Promise.race([new Promise(r=>fixture.once('exit',r)),delay(5000)]);
  if(fixture.exitCode===null) fixture.kill();
  await unlink(fixturePath).catch(()=>{}); await unlink(stopPath).catch(()=>{});
  // Only this run's validated profile path is removed; never use a personal browser profile.
  await rm(profile,{recursive:true,force:true,maxRetries:10,retryDelay:200});
}
