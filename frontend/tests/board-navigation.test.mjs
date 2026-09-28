import assert from 'node:assert/strict';
import test from 'node:test';
import { beginWorldDrag, worldDragPosition, shouldPanBoard, panCamera, wheelPanDelta,
  edgeAutoPan, edgePanVelocity, createEdgeAutoPan } from '../src/features/boards/boardNavigation.ts';
import { screenToWorld, worldToScreen } from '../src/features/boards/boardZoom.ts';

const bounds={left:100,top:50,right:1100,bottom:850};
const center={x:600,y:450};
const edges={left:{x:108,y:450},right:{x:1092,y:450},top:{x:600,y:58},bottom:{x:600,y:842}};
const close=(a,b)=>assert.ok(Math.abs(a-b)<1e-8,`${a} != ${b}`);
function rig(snap=false) {
  let time=0,id=0,camera={x:0,y:0,zoom:1},applied=[];
  const frames=new Map();
  const auto=createEdgeAutoPan({getViewport:()=>bounds,panBy:delta=>{
    const before=camera,next=panCamera(camera,delta);
    camera=snap?{...next,x:Math.round(next.x),y:Math.round(next.y)}:next;
    const actual={x:camera.x-before.x,y:camera.y-before.y};applied.push(actual);return actual;
  },scheduler:{request:cb=>{frames.set(++id,cb);return id;},cancel:frame=>frames.delete(frame),now:()=>time}});
  return {auto,get camera(){return camera;},applied,frames,step(ms=16){time+=ms;const queued=[...frames.values()];frames.clear();queued.forEach(cb=>cb(time));}};
}

test('primary empty-space drag pans, while normal object and control clicks stay independent',()=>{
  assert.equal(shouldPanBoard(0,false,false,false),true);
  assert.equal(shouldPanBoard(0,false,true,false),false);
  assert.equal(shouldPanBoard(0,false,false,true),false);
  assert.equal(shouldPanBoard(0,false,true,true),false);
  assert.equal(shouldPanBoard(2,false,false,false),false);
  assert.equal(shouldPanBoard(0,true,true,false),true);
  assert.equal(shouldPanBoard(0,true,true,true),false);
  assert.equal(shouldPanBoard(1,false,true,false),true);
});
test('camera click-drag moves freely in all directions without touching entity positions or zoom',()=>{
  const note=Object.freeze({positionX:-500,positionY:-1200});
  for(const zoom of [.25,.5,1,1.5,1.75])for(const x of [-12000,12000])for(const y of [-12000,12000]) {
    assert.deepEqual(panCamera({x:15,y:30,zoom},{x,y}),{x:x+15,y:y+30,zoom});
    assert.deepEqual(note,{positionX:-500,positionY:-1200});
  }
});
test('wheel scrolling supports vertical, horizontal, diagonal and Shift-horizontal movement',()=>{
  const wheel={deltaX:0,deltaY:120,deltaMode:0,shiftKey:false};
  assert.deepEqual(wheelPanDelta(wheel,800),{x:0,y:-120});
  assert.deepEqual(wheelPanDelta({...wheel,shiftKey:true},800),{x:-120,y:0});
  assert.deepEqual(wheelPanDelta({...wheel,deltaX:80,deltaY:0},800),{x:-80,y:0});
  assert.deepEqual(wheelPanDelta({...wheel,deltaX:80,deltaY:0,shiftKey:true},800),{x:-80,y:0});
  assert.deepEqual(wheelPanDelta({...wheel,deltaX:-80,deltaY:-120},800),{x:80,y:120});
  assert.deepEqual(wheelPanDelta({...wheel,deltaY:2,deltaMode:1},800),{x:0,y:-32});
  assert.deepEqual(wheelPanDelta({...wheel,deltaY:1,deltaMode:2},800),{x:0,y:-800});
});
test('edge auto-pan translates camera in the correct direction at every edge and at corners',()=>{
  for(const [edge,point]of Object.entries(edges)) {
    const v=edgePanVelocity(point,bounds);
    if(edge==='left')assert.ok(v.x>0&&v.y===0);
    if(edge==='right')assert.ok(v.x<0&&v.y===0);
    if(edge==='top')assert.ok(v.y>0&&v.x===0);
    if(edge==='bottom')assert.ok(v.y<0&&v.x===0);
  }
  const corner=edgePanVelocity({x:108,y:58},bounds);assert.ok(corner.x>0&&corner.y>0);
  assert.deepEqual(edgePanVelocity(center,bounds),{x:0,y:0});
});
test('auto-pan speed grows smoothly toward each edge and remains bounded outside the viewport',()=>{
  for(const [edge,point]of Object.entries(edges)) {
    const speeds=[79,60,40,20,0,-200].map(distance=>{
      const p={...point};if(edge==='left')p.x=bounds.left+distance;if(edge==='right')p.x=bounds.right-distance;
      if(edge==='top')p.y=bounds.top+distance;if(edge==='bottom')p.y=bounds.bottom-distance;
      const v=edgePanVelocity(p,bounds);return Math.abs(v.x||v.y);
    });
    assert.ok(speeds.every((s,i)=>i===0||s>=speeds[i-1]));
    assert.equal(speeds.at(-1),edgeAutoPan.maxSpeed);
    assert.ok(speeds[0]<1);
  }
  assert.deepEqual(edgePanVelocity(center,{left:0,right:0,top:0,bottom:0}),{x:0,y:0});
});
test('auto-pan runs continuously with a stationary pointer at all four edges',()=>{
  for(const point of Object.values(edges)) {
    const r=rig();let previews=0;r.auto.start(point,()=>previews++);
    r.step();const first=r.camera;r.step();r.step();
    assert.ok(Math.abs(r.camera.x)>Math.abs(first.x)||Math.abs(r.camera.y)>Math.abs(first.y));
    assert.equal(previews,3);assert.equal(r.frames.size,1);r.auto.stop();
  }
});
test('moving back into the interior stops auto-pan; updating back to an edge resumes it',()=>{
  const r=rig();r.auto.start(edges.left,()=>{});r.step();r.auto.update(center);
  assert.equal(r.frames.size,0);const stopped={...r.camera};r.step();assert.deepEqual(r.camera,stopped);
  r.auto.update(edges.right);r.step();assert.ok(r.camera.x<stopped.x);r.auto.stop();
});
test('release/cancel/unmount stop the frame loop and its preview callback',()=>{
  const r=rig();let calls=0;r.auto.start(edges.top,()=>calls++);r.step();r.auto.stop();
  const stopped={...r.camera};for(let i=0;i<10;i++)r.step();
  assert.deepEqual(r.camera,stopped);assert.equal(calls,1);assert.equal(r.frames.size,0);
  r.auto.stop();r.auto.update(edges.bottom);assert.equal(r.frames.size,0);
});
test('auto-pan uses elapsed time with a cap so background pauses cannot cause a jump',()=>{
  const fast=rig(),slow=rig();fast.auto.start(edges.left,()=>{});slow.auto.start(edges.left,()=>{});
  for(let i=0;i<10;i++)fast.step(16);for(let i=0;i<5;i++)slow.step(32);close(fast.camera.x,slow.camera.x);
  const before=fast.camera.x;fast.step(10000);assert.ok(fast.camera.x-before<=edgeAutoPan.maxSpeed*edgeAutoPan.maxFrameSeconds);
});
test('slow edge movement accumulates across device-pixel rounding rather than sticking',()=>{
  const r=rig(true);r.auto.start({x:bounds.left+70,y:center.y},()=>{});
  for(let i=0;i<60;i++)r.step();assert.ok(r.camera.x>=9);r.auto.stop();
});
test('live-camera drag keeps the same grab point attached to the pointer while auto-panning at every zoom',()=>{
  for(const zoom of [.25,.5,1,1.5,1.75])for(const point of Object.values(edges)) {
    const startCamera={x:125,y:-90,zoom},position={x:-500,y:-1200},grab={x:30,y:16};
    const initial=worldToScreen({x:position.x+grab.x,y:position.y+grab.y},startCamera);
    const anchor=beginWorldDrag(position,screenToWorld(initial,startCamera));
    for(let frame=1;frame<=20;frame++) {
      const speed=edgePanVelocity(point,bounds),camera=panCamera(startCamera,{x:speed.x*.016*frame,y:speed.y*.016*frame});
      const dragged=worldDragPosition(anchor,screenToWorld(point,camera));
      const attached=worldToScreen({x:dragged.x+grab.x,y:dragged.y+grab.y},camera);
      close(attached.x,point.x);close(attached.y,point.y);
    }
    assert.deepEqual(position,{x:-500,y:-1200});
  }
});
test('notes and tasks can cross zero into negative coordinates while dragging at non-100% zoom',()=>{
  for(const kind of [0,1])for(const zoom of [.25,.5,1,1.5,1.75]) {
    const position={x:20,y:30},camera={x:500,y:500,zoom},initial=worldToScreen(position,camera);
    const anchor=beginWorldDrag(position,screenToWorld(initial,camera));
    const next=worldDragPosition(anchor,screenToWorld({x:initial.x-520*zoom,y:initial.y-1230*zoom},camera));
    assert.deepEqual({kind,...next},{kind,x:-500,y:-1200});
  }
});
