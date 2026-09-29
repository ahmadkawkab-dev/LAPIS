import assert from 'node:assert/strict';
import test from 'node:test';
let moduleNumber=0;
const NativeRequest=globalThis.Request;
const origin='https://wukna.test';
const user={id:'user-id',email:'returning@wukna.test',username:'returning',displayName:null,profileImageUrl:null,profileImageVersion:null};
const session=(expired=false,accessToken='restored-access')=>({accessToken,expiresAt:new Date(Date.now()+(expired?-60000:3600000)).toISOString(),user});
const json=(body,status=200)=>Response.json(body,{status});
async function fixture(handler){
  globalThis.window={location:{origin}};
  globalThis.Request=class extends NativeRequest {constructor(input,init){super(typeof input==='string'?new URL(input,origin):input,init);}};
  const calls=[];
  globalThis.fetch=async(input,init)=>{
    const request=input instanceof NativeRequest?input:new Request(input,init);
    const call={path:new URL(request.url).pathname,method:request.method,headers:request.headers,credentials:request.credentials,cache:request.cache,body:await request.text()};
    calls.push(call);return handler(call,calls);
  };
  const auth=await import(`../src/auth.ts?session-test=${++moduleNumber}`);
  return {auth,calls};
}

test('reopening with only a valid HttpOnly refresh cookie acquires CSRF then restores identity',async()=>{
  const {auth,calls}=await fixture(call=>{
    assert.equal(call.credentials,'include');
    if(call.path.endsWith('/csrf')){assert.equal(call.cache,'no-store');assert.equal(call.headers.get('Authorization'),null);return json({token:'fresh'});}
    assert.equal(call.path,'/api/auth/refresh');assert.equal(call.headers.get('X-CSRF-TOKEN'),'fresh');return json(session());
  });
  assert.equal(auth.currentSession(),null);
  assert.equal((await auth.bootstrapSession()).user.id,user.id);
  assert.deepEqual(calls.map(c=>c.path),['/api/auth/csrf','/api/auth/refresh']);
  assert.equal(auth.currentSession().user.id,user.id);
});

test('startup replaces a cached CSRF token before refreshing an expired in-memory access token',async()=>{
  let token='old';
  const {auth,calls}=await fixture(call=>{
    if(call.path.endsWith('/csrf'))return json({token});
    if(call.path.endsWith('/login'))return json(session(true));
    assert.equal(call.headers.get('X-CSRF-TOKEN'),'fresh');return json(session());
  });
  await auth.login(user.email,'password');token='fresh';
  await auth.bootstrapSession();
  assert.deepEqual(calls.map(c=>c.path),['/api/auth/csrf','/api/auth/login','/api/auth/csrf','/api/auth/refresh']);
});

test('startup fetches fresh CSRF even with a valid in-memory access token',async()=>{
  const {auth,calls}=await fixture(call=>call.path.endsWith('/csrf')?json({token:'fresh'}):json(session()));
  await auth.login(user.email,'password');await auth.bootstrapSession();
  assert.equal(calls.filter(c=>c.path.endsWith('/csrf')).length,2);
  assert.equal(calls.filter(c=>c.path.endsWith('/refresh')).length,0);
});

test('idle refresh reacquires rejected CSRF and rotates the session once',async()=>{
  let token='old',rotations=0;
  const {auth,calls}=await fixture(call=>{
    if(call.path.endsWith('/csrf'))return json({token});
    if(call.path.endsWith('/login'))return json(session(true));
    if(call.headers.get('X-CSRF-TOKEN')!==token)return json({error:'Invalid CSRF token.'},400);
    rotations++;return json(session());
  });
  await auth.login(user.email,'password');token='replacement';
  assert.equal((await auth.restoreSession()).user.id,user.id);
  assert.equal(calls.filter(c=>c.path.endsWith('/refresh')).length,2);assert.equal(rotations,1);
});

for(const reason of ['missing','expired','revoked','invalid'])test(`${reason} refresh credentials require login only after fresh CSRF succeeds`,async()=>{
  const {auth,calls}=await fixture(call=>call.path.endsWith('/csrf')?json({token:'fresh'}):json({},401));
  assert.equal(await auth.bootstrapSession(),null);assert.equal(auth.currentSession(),null);
  assert.equal(calls.length,2);
  let expired=0;auth.setSessionExpiredHandler(()=>expired++);
  await assert.rejects(auth.apiFetch('/api/profile'),e=>e.code==='unauthenticated');
  assert.equal(expired,1);assert.equal(calls.filter(c=>c.path==='/api/profile').length,0);
});

test('persistent CSRF rejection retries once and never expires a valid refresh session',async()=>{
  const {auth,calls}=await fixture(call=>call.path.endsWith('/csrf')?json({token:'fresh'}):json({code:'invalid_csrf',error:'Invalid CSRF token.'},400));
  let expired=0;auth.setSessionExpiredHandler(()=>expired++);
  await assert.rejects(auth.bootstrapSession(),e=>e.code==='invalid_csrf');
  assert.equal(calls.filter(c=>c.path.endsWith('/refresh')).length,2);assert.equal(expired,0);
  await assert.rejects(auth.bootstrapSession(),e=>e.code==='invalid_csrf');
  assert.equal(calls.filter(c=>c.path.endsWith('/refresh')).length,4);
});

test('temporary refresh failures do not retry uncertain operations or invoke logout',async()=>{
  const {auth,calls}=await fixture(call=>call.path.endsWith('/csrf')?json({token:'fresh'}):json({code:'server_error'},500));
  let expired=0;auth.setSessionExpiredHandler(()=>expired++);
  await assert.rejects(auth.bootstrapSession(),e=>e.status===500);
  assert.equal(calls.length,2);assert.equal(expired,0);
});

test('concurrent startup and protected-data calls share acquisition and refresh',async()=>{
  const {auth,calls}=await fixture(async call=>{
    await new Promise(resolve=>setTimeout(resolve,5));
    if(call.path.endsWith('/csrf'))return json({token:'fresh'});
    if(call.path.endsWith('/refresh'))return json(session());
    assert.equal(call.headers.get('Authorization'),'Bearer restored-access');return json(user);
  });
  await Promise.all(Array.from({length:10},()=>auth.bootstrapSession()));
  assert.equal(calls.filter(c=>c.path.endsWith('/csrf')).length,1);assert.equal(calls.filter(c=>c.path.endsWith('/refresh')).length,1);
  await Promise.all(Array.from({length:10},()=>auth.apiFetch('/api/profile')));
  assert.equal(calls.filter(c=>c.path.endsWith('/refresh')).length,1);
});

test('protected profile loading waits until refresh has restored access and identity',async()=>{
  let release;
  const {auth,calls}=await fixture(call=>{
    if(call.path.endsWith('/csrf'))return json({token:'fresh'});
    if(call.path.endsWith('/refresh'))return new Promise(resolve=>{release=()=>resolve(json(session()));});
    assert.equal(auth.currentSession().user.id,user.id);assert.equal(call.headers.get('Authorization'),'Bearer restored-access');return json(user);
  });
  const response=auth.apiFetch('/api/profile');await new Promise(resolve=>setTimeout(resolve,5));
  assert.deepEqual(calls.map(c=>c.path),['/api/auth/csrf','/api/auth/refresh']);release();
  assert.deepEqual(await (await response).json(),user);
});

test('bearer API CSRF retry reacquires identity-bound credentials and replays the original body once',async()=>{
  let attempts=0,mutations=0;
  const {auth,calls}=await fixture(call=>{
    if(call.path.endsWith('/csrf'))return json({token:call.headers.get('Authorization')?'identity-csrf':'anonymous-csrf'});
    if(call.path.endsWith('/refresh'))return json(session());
    attempts++;assert.equal(call.body,'{"displayName":"Restored"}');
    if(attempts===1)return json({code:'invalid_csrf'},400);
    assert.equal(call.headers.get('X-CSRF-TOKEN'),'identity-csrf');assert.equal(call.headers.get('Authorization'),'Bearer restored-access');mutations++;return json(user);
  });
  await auth.bootstrapSession();
  assert.equal((await auth.apiFetch('/api/profile',{method:'PATCH',body:JSON.stringify({displayName:'Restored'})})).status,200);
  assert.equal(attempts,2);assert.equal(mutations,1);
  assert.equal(calls.filter(c=>c.path.endsWith('/csrf'))[1].headers.get('Authorization'),'Bearer restored-access');
});

test('normal permission failures are returned unchanged without a CSRF retry',async()=>{
  const {auth,calls}=await fixture(call=>call.path.endsWith('/csrf')?json({token:'fresh'}):call.path.endsWith('/refresh')?json(session()):json({error:'forbidden'},403));
  const response=await auth.apiFetch('/api/profile',{method:'PATCH'});
  assert.equal(response.status,403);assert.deepEqual(await response.json(),{error:'forbidden'});assert.equal(calls.length,3);
});

test('CSRF retry budget stays bounded across an access-token renewal',async()=>{
  let attempts=0;
  const {auth,calls}=await fixture(call=>{
    if(call.path.endsWith('/csrf'))return json({token:'fresh'});
    if(call.path.endsWith('/refresh'))return json(session(false,`access-${calls.length}`));
    attempts++;return attempts===2?json({},401):json({code:'invalid_csrf'},400);
  });
  assert.equal((await auth.apiFetch('/api/profile',{method:'PATCH'})).status,400);
  assert.equal(attempts,3);assert.equal(calls.filter(c=>c.path.endsWith('/csrf')).length,2);
});

test('authenticated requests never send credentials outside this application',async()=>{
  const {auth,calls}=await fixture(()=>{throw Error('must not fetch');});
  await assert.rejects(auth.apiFetch('https://other.test/api/profile'),/this app/);assert.equal(calls.length,0);
});

test('a missing CSRF response token blocks startup without attempting refresh',async()=>{
  const {auth,calls}=await fixture(()=>json({}));
  await assert.rejects(auth.bootstrapSession(),e=>e.code==='csrf_unavailable');assert.equal(calls.length,1);
});

test('concurrent stale CSRF failures share one replacement acquisition',async()=>{
  let acquisition=0;
  const {auth,calls}=await fixture(async call=>{
    if(call.path.endsWith('/csrf')){acquisition++;await new Promise(resolve=>setTimeout(resolve,10));return json({token:`csrf-${acquisition}`});}
    if(call.headers.get('X-CSRF-TOKEN')==='csrf-1')return json({error:'Invalid CSRF token.'},400);
    return json(session());
  });
  await Promise.all(Array.from({length:10},()=>auth.login(user.email,'password')));
  assert.equal(calls.filter(c=>c.path.endsWith('/csrf')).length,2);
  assert.equal(calls.filter(c=>c.path.endsWith('/login')).length,20);
});
test('legacy CSRF errors have the same recoverable error code as current API responses',()=>{
  const code='Invalid CSRF token.';
  // The constructor is shared by the auth client and the frontend API client.
  return import('../src/auth.ts').then(({AuthApiError})=>assert.equal(new AuthApiError(code,400).code,'invalid_csrf'));
});
