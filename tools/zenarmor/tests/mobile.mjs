import {spawn} from 'node:child_process';
import {readFile, mkdtemp, rm, access} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
const [origin] = process.argv.slice(2);
const candidates = [process.env.CHROME_PATH, process.env.PROGRAMFILES && path.join(process.env.PROGRAMFILES,'Google/Chrome/Application/chrome.exe'), '/usr/bin/google-chrome'].filter(Boolean);
let chrome;
for (const candidate of candidates) { try { await access(candidate); chrome=candidate; break; } catch {} }
assert.ok(chrome, 'Chrome is required for the mobile dashboard tests');
const profile = await mkdtemp(path.join(tmpdir(),'hw-browser-'));
const proc = spawn(chrome,['--headless=new','--disable-gpu','--disable-extensions','--no-first-run','--no-default-browser-check',
  '--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'],{stdio:'ignore',windowsHide:true});
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(check, message) {
  for(let i=0;i<100;i++){try{const value=await check();if(value)return value;}catch{} await pause(100);}
  throw Error(message);
}
let socket;
try {
  const port=await until(async()=>(await readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0],'Chrome did not start');
  const tabs=await (await fetch('http://127.0.0.1:'+port+'/json/list')).json();
  socket=new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);
  await new Promise((ok,fail)=>{socket.onopen=ok;socket.onerror=fail;});
  const pending=new Map();let serial=0;
  socket.onmessage=event=>{const m=JSON.parse(event.data);if(pending.has(m.id)){pending.get(m.id)(m);pending.delete(m.id);}};
  async function send(method,params={}){const id=++serial;const result=new Promise(ok=>pending.set(id,ok));socket.send(JSON.stringify({id,method,params}));const m=await result;if(m.error)throw Error(m.error.message);return m.result;}
  async function evaluate(expression){const r=await send('Runtime.evaluate',{expression,returnByValue:true});if(r.exceptionDetails)throw Error(r.exceptionDetails.text);return r.result.value;}
  await send('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  for(const [page,selector] of [['zenarmor.html','#events details'],['live.html','#liveSource'],['devices.html','main'],['history.html','main'],['adult-analysis.html','main'],['tls-inspection.html','main'],['security.html','main'],['notifications.html','main'],['device.html?id=11','#identityProfile, main']]){
    await send('Page.navigate',{url:origin+'/'+page});
    await until(()=>evaluate('location.pathname==='+JSON.stringify('/'+page.split('?')[0])+' && document.readyState==="complete" && !!document.querySelector('+JSON.stringify(selector)+')'),'Missing '+page+' '+selector);
    assert.ok(await evaluate('document.documentElement.scrollWidth<=window.innerWidth+1'), 'Horizontal overflow on '+page);
    if(page==='zenarmor.html'){
      assert.ok(await evaluate('document.querySelector("#events").textContent.includes("Example.TEST.")'));
      await evaluate('document.querySelector("[name=domain]").value="next.test";document.querySelector("#filters").requestSubmit()');
      await until(()=>evaluate('document.querySelector("#events").textContent.includes("next.test") && !document.querySelector("#events").textContent.includes("Example.TEST.")'),'Zenarmor filter');
    }
  }
  console.log('PASS: mobile navigation, layout, fixture evidence and Zenarmor filtering');

} finally {
  socket?.close();proc.kill();
  await rm(profile,{recursive:true,force:true,maxRetries:3,retryDelay:200}).catch(()=>{});
}
