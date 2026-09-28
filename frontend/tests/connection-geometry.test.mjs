import assert from 'node:assert/strict';
import test from 'node:test';
import {connectionSides,connectionAnchor,connectionHandleReach,connectionEndpoints,nearestConnectionSide,nearestConnectionPath,
  connectionPreviewPath,notesAreConnected} from '../src/features/boards/connectionGeometry.ts';
import {screenToWorld,worldToScreen} from '../src/features/boards/boardZoom.ts';
import {mergeVersionedConnection} from '../src/realtime/reconcile.ts';
const source={positionX:-500,positionY:-1200,width:280,height:220};
const target={positionX:200,positionY:100,width:300,height:144};
const close=(a,b)=>assert.ok(Math.abs(a-b)<1e-7,`${a} != ${b}`);
const endOfPath=path=>path.split(',').at(-1).trim().split(' ').map(Number);

test('four anchors are centered exactly on each world-space card edge',()=>{
  const expected={top:[-360,-1200,0,-1],right:[-220,-1090,1,0],bottom:[-360,-980,0,1],left:[-500,-1090,-1,0]};
  for(const side of connectionSides){const p=connectionAnchor(source,side);assert.deepEqual([p.x,p.y,p.dx,p.dy],expected[side]);}
});
test('connectors render all sixteen explicit source/target side combinations',()=>{
  for(const sourceSide of connectionSides)for(const targetSide of connectionSides){
    const pair=connectionEndpoints(source,target,sourceSide,targetSide);
    assert.equal(pair.start.side,sourceSide);assert.equal(pair.end.side,targetSide);
    const path=nearestConnectionPath(source,target,sourceSide,targetSide);
    assert.ok(path.startsWith(`M ${pair.start.x} ${pair.start.y} C `));assert.deepEqual(endOfPath(path),[pair.end.x,pair.end.y]);
    assert.ok(!/NaN|Infinity/.test(path));
  }
});
test('drops choose the closest perimeter side; explicit handle choices remain fixed',()=>{
  for(const side of connectionSides){const p=connectionAnchor(target,side);assert.equal(nearestConnectionSide(target,p),side);assert.equal(connectionEndpoints(source,target,'top',side).end.side,side);}
  assert.equal(nearestConnectionSide(target,{x:275,y:105}),'top');
  assert.equal(nearestConnectionSide(target,{x:497,y:170}),'right');
  assert.equal(nearestConnectionSide(target,{x:350,y:241}),'bottom');
  assert.equal(nearestConnectionSide(target,{x:202,y:180}),'left');
});
test('moving and resizing either card preserves the chosen side attachment without rewriting the edge',()=>{
  const edge=Object.freeze({sourceHandle:'top',targetHandle:'left'});
  for(const side of connectionSides){
    const moved={...source,positionX:source.positionX-800,positionY:source.positionY+400,width:400,height:300};
    const pair=connectionEndpoints(moved,{...target,positionX:-900,positionY:-500},side,side);
    assert.deepEqual(pair.start,connectionAnchor(moved,side));
    assert.deepEqual(pair.end,connectionAnchor({...target,positionX:-900,positionY:-500},side));
  }
  assert.deepEqual(edge,{sourceHandle:'top',targetHandle:'left'});
});
test('creation and reconnection previews snap to every side with correct source/target direction',()=>{
  for(const fixedSide of connectionSides)for(const targetSide of connectionSides){
    const fixed=connectionAnchor(source,fixedSide),moving=connectionAnchor(target,targetSide);
    const forward=connectionPreviewPath(source,fixedSide,{x:999,y:999},target,targetSide);
    assert.ok(forward.startsWith(`M ${fixed.x} ${fixed.y} C `));assert.deepEqual(endOfPath(forward),[moving.x,moving.y]);
    const reverse=connectionPreviewPath(source,fixedSide,{x:999,y:999},target,targetSide,true);
    assert.ok(reverse.startsWith(`M ${moving.x} ${moving.y} C `));assert.deepEqual(endOfPath(reverse),[fixed.x,fixed.y]);
  }
  assert.deepEqual(endOfPath(connectionPreviewPath(source,'bottom',{x:-900,y:-1500})),[-900,-1500]);
});
test('pan/zoom conversions retain the same world-side endpoints at every zoom and negative position',()=>{
  for(const zoom of [.25,.5,1,1.5,1.75])for(const side of connectionSides){
    const anchor=connectionAnchor(source,side),camera={x:350,y:-125,zoom};
    const screen=worldToScreen(anchor,camera),point=screenToWorld(screen,camera);
    close(point.x,anchor.x);close(point.y,anchor.y);assert.equal(nearestConnectionSide(source,point),side);
  }
});
test('duplicate relationship checks include reversed connections while reconnection excludes itself',()=>{
  const edges=[{id:'edge-1',sourceNoteId:'a',targetNoteId:'b'},{id:'edge-2',sourceNoteId:'b',targetNoteId:'c'}];
  assert.equal(notesAreConnected('a','b',edges),true);assert.equal(notesAreConnected('b','a',edges),true);
  assert.equal(notesAreConnected('a','b',edges,'edge-1'),false);assert.equal(notesAreConnected('c','b',edges,'edge-1'),true);
});
test('versioned reconnection replaces one edge and rejects late creation/update/delete resurrection',()=>{
  const original={id:'edge',sourceNoteId:'a',targetNoteId:'b',sourceHandle:'right',targetHandle:'left',version:12};
  const reconnected={...original,targetNoteId:'c',sourceHandle:'top',targetHandle:'bottom',version:15};
  const current=mergeVersionedConnection([original],reconnected);assert.deepEqual(current,[reconnected]);
  assert.equal(mergeVersionedConnection(current,original),current);
  assert.equal(mergeVersionedConnection(current,reconnected),current);
  assert.deepEqual(mergeVersionedConnection([],reconnected,15),[]);
});

test('enlarged handle hit areas stop before neighboring cards in all four directions',()=>{
  const note={id:'center',positionX:0,positionY:0,width:280,height:220};
  const obstacles=[{id:'north',positionX:0,positionY:-260,width:280,height:220},
    {id:'east',positionX:320,positionY:0,width:280,height:220},
    {id:'south',positionX:0,positionY:260,width:280,height:220},
    {id:'west',positionX:-320,positionY:0,width:280,height:220}];
  const reach=connectionHandleReach(note,[note,...obstacles]);
  for(const side of connectionSides){assert.equal(reach[side],38);for(const zoom of [.25,.5,1,1.5,1.75])assert.ok(Math.min(44/zoom,reach[side])<40);}
  assert.deepEqual(connectionHandleReach(note,[note]),{top:176,right:176,bottom:176,left:176});
  assert.equal(connectionHandleReach(note,[note,{...obstacles[0],positionY:-220}]).top,0);
  assert.equal(connectionHandleReach(note,[note,{...obstacles[0],positionY:-400}]).top,176);
});
