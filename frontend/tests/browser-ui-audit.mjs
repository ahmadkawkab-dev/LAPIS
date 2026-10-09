import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { fixtures, id } from './ui-audit-fixtures.mjs';

// Run explicitly against a disposable Opera profile; not part of npm's unit suite.
const modulePath=process.env.WUKNA_PLAYWRIGHT_MODULE||'playwright';
const {chromium}=await import(path.isAbsolute(modulePath)?pathToFileURL(modulePath).href:modulePath);
export const browser=await chromium.connectOverCDP(process.env.WUKNA_OPERA_CDP||'http://127.0.0.1:9223');
const protocol=await browser.newBrowserCDPSession();
const browserInfo=await protocol.send('Browser.getVersion');
await protocol.detach();
if(!browserInfo.userAgent.includes('OPR/')){await browser.close();throw new Error('UI verification requires Opera; point WUKNA_OPERA_CDP at its disposable profile.');}
export const output=process.env.WUKNA_AUDIT_OUTPUT||'/tmp/wukna-ui-audit';
await fs.mkdir(output,{recursive:true});
export async function setup({theme='light',width=1440,height=1024,state='populated',authenticated=true,reducedMotion='reduce'}={}){
 const context=await browser.newContext({viewport:{width,height},colorScheme:theme,reducedMotion,timezoneId:'Asia/Beirut',serviceWorkers:'block'});
 const missing=[],errors=[];await fixtures(context,{state,authenticated,missing});
 await context.addInitScript(theme=>localStorage.setItem('wukna.theme.v1',theme),theme);
 const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 return {context,page,missing,errors};
}
export async function inspect(page){return page.evaluate(()=>{
 const visible=e=>{const s=getComputedStyle(e),r=e.getBoundingClientRect();return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'&&Number(s.opacity)>0;};
 const colorCanvas=document.createElement('canvas');colorCanvas.width=colorCanvas.height=1;const colorContext=colorCanvas.getContext('2d',{willReadFrequently:true});
 const rgba=s=>{if(!s)return null;if(s.startsWith('rgb')||s.startsWith('color(srgb')){const v=s.match(/[\d.]+/g)?.map(Number);if(!v||v.length<3)return null;if(s.startsWith('color(srgb'))for(let i=0;i<3;i++)v[i]*=255;return v;}colorContext.clearRect(0,0,1,1);colorContext.fillStyle=s;colorContext.fillRect(0,0,1,1);const v=[...colorContext.getImageData(0,0,1,1).data];v[3]/=255;return v;};
 const blend=(a,b)=>a.slice(0,3).map((v,i)=>v*(a[3]??1)+b[i]*(1-(a[3]??1))).concat(1);
 const background=e=>{const stack=[];for(let p=e;p;p=p.parentElement)stack.unshift(getComputedStyle(p).backgroundColor);return stack.reduce((bg,s)=>{const c=rgba(s);return c?blend(c,bg):bg;},[255,255,255,1]);};
 const luminance=c=>c.slice(0,3).map(v=>v/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4).reduce((s,v,i)=>s+v*[.2126,.7152,.0722][i],0);
 const ratio=(a,b)=>{const x=luminance(a),y=luminance(b);return(Math.max(x,y)+.05)/(Math.min(x,y)+.05);};
 const name=e=>(e.getAttribute('aria-label')||e.textContent||e.className).toString().trim().slice(0,90);
 const contrasts=[],outliers=[],icons=[],taskChecks=[],unnamed=[];
 for(const b of document.querySelectorAll('button,a,[role="button"]'))if(visible(b)){
  if(!b.getAttribute('aria-label')&&!b.textContent.trim()&&!b.title&&!b.getAttribute('aria-labelledby'))unnamed.push(b.className);
  const text=[b,...b.querySelectorAll('span,strong,small')].filter(e=>[...e.childNodes].some(n=>n.nodeType===3&&n.textContent.trim())&&visible(e));
  for(const n of text){const s=getComputedStyle(n),c=rgba(s.color),bg=background(n),r=c&&ratio(blend(c,bg),bg);if(r&&r<4.5)contrasts.push({button:name(b),text:name(n),ratio:+r.toFixed(2),color:s.color,background:bg,class:n.className});}
  for(const svg of b.querySelectorAll('svg'))if(visible(svg)){const s=getComputedStyle(svg),c=rgba(s.color);let bg=background(svg);if(b.matches('.wk-task-complete--done')){const box=rgba(getComputedStyle(b,'::before').backgroundColor);if(box)bg=blend(box,bg);}const r=c&&ratio(blend(c,bg),bg);if(r&&r<3)contrasts.push({button:name(b),kind:'icon',ratio:+r.toFixed(2),color:s.color,background:bg});}
 }
 for(const field of document.querySelectorAll('input,select,textarea'))if(visible(field)&&!['hidden','file','radio','checkbox'].includes(field.type)){
  const s=getComputedStyle(field,!field.value&&field.placeholder?'::placeholder':null),c=rgba(s.color),bg=background(field),r=c&&ratio(blend(c,bg),bg);
  if(r&&r<4.5)contrasts.push({button:field.getAttribute('aria-label')||field.placeholder||field.id||field.type,kind:'field',ratio:+r.toFixed(2),color:s.color,background:bg});
 }
 for(const b of document.querySelectorAll('.wk-sidebar--collapsed .wk-shell-link,.wk-sidebar--collapsed .wk-sidebar-new'))if(visible(b)){
  const svg=b.querySelector('.wk-button-content svg,svg');if(!svg)continue;const r=svg.getBoundingClientRect(),side=b.closest('.wk-sidebar').getBoundingClientRect();icons.push({label:name(b),offset:+(r.x+r.width/2-side.x-side.width/2).toFixed(2),width:r.width,height:r.height});
 }
 for(const b of document.querySelectorAll('.wk-task-complete'))if(visible(b)){
  const r=b.getBoundingClientRect(),s=getComputedStyle(b,'::before'),svg=b.querySelector('svg');
  const check=svg?.getBoundingClientRect();taskChecks.push({label:name(b),boxWidth:parseFloat(s.width),boxHeight:parseFloat(s.height),iconWidth:check?.width,iconHeight:check?.height,deltaX:check?+(check.x+check.width/2-r.x-r.width/2).toFixed(2):null,deltaY:check?+(check.y+check.height/2-r.y-r.height/2).toFixed(2):null});
 }
 for(const e of document.querySelectorAll('header,nav,button,input,select,textarea,.wk-feature-stage'))if(visible(e)&&!e.closest('.board-world')){
  const r=e.getBoundingClientRect();if(r.right>innerWidth+2||r.left<-2){let scrolling=false;for(let p=e.parentElement;p;p=p.parentElement)if(['auto','scroll','hidden','clip'].includes(getComputedStyle(p).overflowX)){scrolling=true;break;}if(!scrolling)outliers.push({name:name(e),class:e.className,left:r.left,right:r.right});}
 }
 return {width:innerWidth,height:innerHeight,theme:document.documentElement.dataset.theme,scrollWidth:document.documentElement.scrollWidth,contrasts,icons,taskChecks,outliers,unnamed};
});}
export async function visit(env,route){await env.page.goto(`http://localhost:5173${route}`);await env.page.waitForSelector('.wk-feature-stage');await env.page.evaluate(()=>document.fonts.ready);await env.page.waitForTimeout(450);}
export async function record(env,route,label){const scan=await inspect(env.page);const item={route,label,...scan,errors:[...env.errors],missing:[...env.missing]};await env.page.screenshot({path:`${output}/${label}.png`,fullPage:true});return item;}
if(process.argv[1]===new URL(import.meta.url).pathname){
 const routes=['/home','/boards','/tasks','/tasks/quick','/tasks/templates','/calendar','/notifications','/account/profile','/account/preferences',`/boards/${id(2)}`,`/boards/${id(3)}`];
 const results=[];
 try{
  for(const state of ['populated','empty'])for(const theme of ['light','dark'])for(const width of [375,768,1024,1440]){
   const env=await setup({state,theme,width});
   for(const route of routes){await visit(env,route);const scan=await inspect(env.page);const item={route,state,...scan,errors:[...env.errors],missing:[...env.missing]};results.push(item);if(scan.contrasts.length||scan.scrollWidth>width||scan.outliers.length||scan.unnamed.length||route==='/tasks')await env.page.screenshot({path:`${output}/${state}-${theme}-${width}-${route.split('/').filter(Boolean).join('-')}.png`,fullPage:true});}
   await env.context.close();console.log(JSON.stringify({state,theme,width,checked:routes.length,issues:results.filter(r=>r.state===state&&r.theme===theme&&r.width===width&&(r.contrasts.length||r.scrollWidth>width||r.outliers.length||r.errors.length||r.missing.length||r.unnamed.length)).map(r=>({route:r.route,contrast:r.contrasts,scrollWidth:r.scrollWidth,outliers:r.outliers,errors:r.errors,missing:r.missing,unnamed:r.unnamed}))}));
  }
  await fs.writeFile(`${output}/results.json`,JSON.stringify(results,null,2));
  if(results.some(r=>r.contrasts.length||r.scrollWidth>r.width||r.outliers.length||r.errors.length||r.missing.length||r.unnamed.length))process.exitCode=1;
 }finally{await browser.close();}
}
