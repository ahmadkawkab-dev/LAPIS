import assert from 'node:assert/strict';
import test from 'node:test';
import { boardZoom, clampZoom, nextZoomStep, contentBounds, zoomAtPoint, snapCameraAtDefault,
  fitBoardContent, moveWorldPoint, screenDeltaToWorld, screenToWorld, worldToScreen } from '../src/features/boards/boardZoom.ts';
import { clientPointToBoard, revealBoardNode } from '../src/features/boards/boardViewport.ts';
import { clampDimension, noteDimensionBounds } from '../src/features/boards/noteDimensions.ts';
const viewport = { width: 1000, height: 700 };
const zooms = [0.25, 0.5, 1, 1.5, 1.75];
const close = (actual, expected) => assert.ok(Math.abs(actual - expected) < 1e-8, `${actual} != ${expected}`);

test('manual range is fixed at 25–175% with 100% default, independent of content', () => {
  assert.equal(clampZoom(0.01), 0.25);
  assert.equal(clampZoom(100), 1.75);
  assert.equal(clampZoom(boardZoom.default), 1);
  assert.equal(clampZoom(NaN), 1);
  for (const content of [null, { left: 0, top: 0, right: 100, bottom: 100 }, { left: -100000, top: -100000, right: 100000, bottom: 100000 }]) {
    fitBoardContent(content, viewport);
    assert.equal(clampZoom(0.25), 0.25);
  }
});
test('toolbar uses explicit understandable steps and handles continuous wheel zoom', () => {
  for (let i = 0; i < boardZoom.steps.length - 1; i++) {
    assert.equal(nextZoomStep(boardZoom.steps[i], 1), boardZoom.steps[i+1]);
    assert.equal(nextZoomStep(boardZoom.steps[i+1], -1), boardZoom.steps[i]);
  }
  assert.equal(nextZoomStep(1.17, 1), 1.25);
  assert.equal(nextZoomStep(1.17, -1), 1);
  assert.equal(nextZoomStep(1.75, 1), 1.75);
  assert.equal(nextZoomStep(0.25, -1), 0.25);
});
test('fit includes negative bounds, padding, center, a comfortable single-card cap and empty reset', () => {
  const bounds = { left: -1500, top: -1200, right: 1800, bottom: 900 };
  const fit = fitBoardContent(bounds, viewport);
  assert.ok((bounds.right-bounds.left)*fit.zoom <= viewport.width - boardZoom.padding*2 + 0.01);
  assert.ok((bounds.bottom-bounds.top)*fit.zoom <= viewport.height - boardZoom.padding*2 + 0.01);
  const center = worldToScreen({ x:150, y:-150 }, fit);
  close(center.x,500);close(center.y,350);
  assert.equal(fitBoardContent({left:-600,top:-800,right:-320,bottom:-580},viewport).zoom,1);
  assert.deepEqual(fitBoardContent(null,viewport),{zoom:1,x:0,y:0});
  assert.equal(fitBoardContent({left:-100000,top:-100000,right:100000,bottom:100000},viewport).zoom,0.25);
});
test('screen/world conversion supports every quadrant and requested zoom level', () => {
  for (const zoom of zooms) for (const x of [-600,600]) for (const y of [-800,800]) {
    const camera={x:220,y:-110,zoom},world={x,y};
    const screen=worldToScreen(world,camera);
    assert.deepEqual(screenToWorld(screen,camera),world);
    assert.deepEqual(clientPointToBoard({left:20,top:30},camera,{x:screen.x+20,y:screen.y+30}),world);
  }
});
test('wheel and center zoom preserve the world point beneath the anchor', () => {
  for(const zoom of zooms) for(const next of zooms) for(const anchor of [{x:120,y:80},{x:500,y:350}]) {
    const camera={x:-350,y:425,zoom};const world=screenToWorld(anchor,camera);
    const result=screenToWorld(anchor,zoomAtPoint(camera,next,anchor));
    close(result.x,world.x);close(result.y,world.y);
  }
});
test('notes and tasks drag freely through zero into negative X/Y at every zoom', () => {
  for(const zoom of zooms) for(const kind of [0,1]) {
    const note=Object.freeze({kind,positionX:40,positionY:20,width:280,height:220});
    const point=moveWorldPoint({x:note.positionX,y:note.positionY},{x:-640*zoom,y:-820*zoom},zoom);
    assert.deepEqual(point,{x:-600,y:-800});
    assert.deepEqual(moveWorldPoint(point,{x:0,y:-400*zoom},zoom),{x:-600,y:-1200});
    assert.deepEqual(note,{kind,positionX:40,positionY:20,width:280,height:220});
  }
});
test('resize deltas and dimension limits remain in world units at every zoom', () => {
  const note={kind:0,title:'Plan',content:'Body',width:280};
  for(const zoom of zooms) {
    const delta=screenDeltaToWorld({x:40*zoom,y:20*zoom},zoom);
    assert.deepEqual(delta,{x:40,y:20});
    const bounds=noteDimensionBounds(note,[],note.width+delta.x);
    assert.equal(clampDimension(note.width+delta.x,bounds.minWidth,bounds.maxWidth),320);
  }
});
test('camera movement and fit never rewrite entity coordinates', () => {
  const notes=Object.freeze([Object.freeze({positionX:-500,positionY:-1200,width:280,height:220})]);
  assert.deepEqual(contentBounds(notes),{left:-500,top:-1200,right:-220,bottom:-980});
  const fit=fitBoardContent(contentBounds(notes),viewport);
  for(const zoom of zooms) zoomAtPoint(fit,zoom,{x:500,y:350});
  assert.equal(notes[0].positionX,-500);assert.equal(notes[0].positionY,-1200);
});
test('default camera is device-pixel aligned without snapping zoomed geometry', () => {
  assert.deepEqual(snapCameraAtDefault({x:123.5,y:-45.4,zoom:1},1),{x:124,y:-45,zoom:1});
  assert.deepEqual(snapCameraAtDefault({x:123.25,y:-45.2,zoom:1},2),{x:123.5,y:-45,zoom:1});
  const zoomed={x:123.5,y:-45.4,zoom:0.25};assert.equal(snapCameraAtDefault(zoomed,1),zoomed);
});
test('inspector reveal translates only camera and supports negative/offscreen cards', () => {
  let pan;
  const viewport={getBoundingClientRect:()=>({left:100,top:50,right:900,bottom:650})};
  const node={getBoundingClientRect:()=>({left:-300,top:-100,right:-20,bottom:120})};
  assert.equal(revealBoardNode(viewport,node,(x,y)=>{pan={x,y};}),true);
  assert.deepEqual(pan,{x:424,y:174});
});
test('new empty tasks are compact, grow with items, then use bounded internal scrolling', () => {
  const task={kind:1,title:'Task',content:'',width:300};
  assert.equal(noteDimensionBounds(task,[]).minHeight,144);
  const rows=Array.from({length:100},()=>({title:'An actionable item'}));
  assert.equal(noteDimensionBounds(task,rows).minHeight,360);
  assert.equal(clampDimension(10000,360,720),720);
});
