// Synthetic responses for presentation checks. Never send real mutations to a server.
export const id = n => `00000000-0000-4000-8000-${String(n).padStart(12,'0')}`;
export const today = new Date().toISOString().slice(0,10), stamp = `${today}T09:00:00Z`;
export const user = {id:id(1),email:'test@example.test',username:'test.user',displayName:'A Very Long Display Name For Responsive Testing',profileImageUrl:null,profileImageVersion:null};
const board = (n,title,role=1) => ({id:id(n),title,createdAt:stamp,updatedAt:stamp,role,canEdit:role===1,noteCount:6,taskListCount:1,taskItemCount:2,completedTaskItemCount:1,memberCount:2,previewNodes:[],previewConnections:[]});
export const boards = [board(2,'A collaborative board title that should fit comfortably on every screen'),board(3,'UnbrokenBoardTitle'.repeat(10),0)];
export const tasks = [10,11,12].map((n,i)=>({id:id(n),title:i===0?'A long task title that wraps across several lines without displacing its actions':i===1?'UnbrokenTaskTitle'.repeat(12):'Completed task',description:'A detailed description '.repeat(20),plannedDate:today,plannedTime:i===1?'09:30:00':null,timeZoneId:'Asia/Beirut',plannedAtUtc:i===1?`${today}T06:30:00Z`:null,completedAt:i===2?stamp:null,createdAt:stamp,updatedAt:stamp,listId:null}));
export const notes = ['#EEE8DB','#DEE5D4','#EFDDD1','#EEE2BF','#DCE7EB','#EADCE1'].map((color,n)=>({id:id(20+n),boardId:id(2),kind:n===1?1:0,parentNoteId:null,title:n===1?'Checklist with a long title':`Note ${n+1}`,content:'Readable note body',positionX:n%3*320,positionY:Math.floor(n/3)*250,width:280,height:210,zIndex:n+1,color,isCompleted:false,createdAt:stamp,version:1}));
notes.push({...notes[1],id:id(29),kind:2,parentNoteId:id(21),title:'Long checklist item '.repeat(6),positionX:null,positionY:null,isCompleted:true});
export const templates = Array.from({length:6},(_,n)=>({id:id(40+n),name:n?`Reusable template ${n}`:'UnbrokenTemplateTitle'.repeat(8),items:tasks.map(t=>({title:t.title,description:t.description,plannedTime:t.plannedTime,timeZoneId:t.timeZoneId})),createdAt:stamp,updatedAt:stamp}));
const items = tasks.map(t=>({source:'task',id:t.id,title:t.title,scheduleKind:t.plannedTime?'timedTask':'dateOnlyTask',startDate:t.plannedTime?null:today,endDateExclusive:null,startAtUtc:t.plannedAtUtc,endAtUtc:null,timeZoneId:t.timeZoneId,isCompleted:!!t.completedAt}));
items.push({source:'event',id:id(50),title:'Calendar event with a long label '.repeat(4),scheduleKind:'timedEvent',startDate:null,endDateExclusive:null,startAtUtc:`${today}T10:00:00Z`,endAtUtc:`${today}T11:00:00Z`,timeZoneId:'Asia/Beirut',isCompleted:null});
const event = {id:id(50),title:items.at(-1).title,description:'Event description',location:'Meeting room',isAllDay:false,allDayStartDate:null,allDayEndDateExclusive:null,localStart:`${today}T13:00:00`,localEnd:`${today}T14:00:00`,timeZoneId:'Asia/Beirut',startAtUtc:`${today}T10:00:00Z`,endAtUtc:`${today}T11:00:00Z`,createdAt:stamp,updatedAt:stamp};
const notification = {id:id(60),type:'taskReminder',title:'Long task reminder title '.repeat(4),activityKind:null,actorUserId:null,boardId:null,resourceKind:'task',resourceId:id(10),issuedAt:stamp,updatedAt:stamp,readAt:null,dismissedAt:null,revision:1,readRevision:0,isUnread:true,activityCount:1,taskId:id(10),taskTitle:tasks[0].title};
const settings = {inAppEnabled:true,pushEnabled:false,soundsMuted:false,soundVolume:.5,chatNotificationsEnabled:true,taskReminderNotificationsEnabled:true,scheduledTaskReminderNotificationsEnabled:true,sharedBoardNotificationsEnabled:true,boardInvitationNotificationsEnabled:true,taskActivityNotificationsEnabled:true,chatSoundEnabled:true,taskReminderSoundEnabled:true,scheduledTaskPostedSoundEnabled:true,boardInvitationSoundEnabled:true,taskCompletedSoundEnabled:true,privatePreviewsEnabled:false};
const sender = {userId:user.id,username:user.username,displayName:user.displayName,avatarUrl:null,avatarVersion:null};
const message = {id:id(70),boardId:id(2),sequence:'1',cursor:'1',type:'text',body:'A long message '.repeat(30),createdAt:stamp,clientMessageId:id(71),sender,attachment:null,scheduledTask:null};
export async function fixtures(context,{state='populated',authenticated=true,missing=[]}={}) {
 await context.route('**/api/**',async route=>{
  const url=new URL(route.request().url()),p=url.pathname,method=route.request().method();
  const send=json=>route.fulfill({json}),empty=state==='empty',list=x=>empty?[]:x;
  if(p==='/api/auth/csrf')return send({token:'synthetic-csrf-token'});
  if(p==='/api/auth/refresh')return authenticated?send({accessToken:'synthetic-not-a-real-token',expiresAt:'2099-01-01T00:00:00Z',user}):route.fulfill({status:401,json:{code:'unauthenticated'}});
  if(state==='error'&&!p.startsWith('/api/auth')&&!p.startsWith('/api/profile')&&!p.includes('preferences')&&!p.endsWith('/settings'))return route.fulfill({status:503,json:{code:'service_unavailable'}});
  if(state==='loading'&&['/api/tasks','/api/calendar','/api/boards','/api/task-templates/page','/api/notifications/page','/api/notifications/upcoming/page'].includes(p))await new Promise(r=>setTimeout(r,2500));
  if(p==='/api/profile')return send({...user,userId:user.id});
  if(p==='/api/profile/onboarding')return send({status:'Completed',version:1});
  if(p==='/api/auth/account')return send({id:user.id,email:user.email,hasPassword:true,externalLogins:[]});
  if(p==='/api/tasks/settings')return send({timeZoneId:'Asia/Beirut'});
  if(p==='/api/boards')return send(list(boards));
  if(/^\/api\/boards\/[^/]+$/.test(p))return send(boards.find(b=>p.endsWith(b.id))??boards[0]);
  if(p.endsWith('/members')&&!p.includes('/chat/'))return send([{...user,userId:user.id,role:1,canEdit:true},{...user,userId:id(90),username:'guest.test',displayName:'Guest with a long name',role:0,canEdit:false}]);
  if(p.endsWith('/guest-limit'))return send({maxGuests:10,guestCount:1});
  if(p.endsWith('/notes'))return send(list(notes.map(n=>({...n,boardId:p.split('/')[3]}))));
  if(p.endsWith('/connections'))return send([]);
  if(p==='/api/tasks'){
   const view=url.searchParams.get('view'),date=url.searchParams.get('date')||today;
   const rows=view==='week'?tasks.concat(Array.from({length:6},(_,n)=>({...tasks[0],id:id(100+n),title:`Another task on day ${n+1}`,plannedDate:new Date(Date.parse(`${date}T12:00:00Z`)+n*86400000).toISOString().slice(0,10)}))):tasks;
   return send({items:list(rows.map(t=>view==='inbox'?{...t,plannedDate:null,plannedTime:null,plannedAtUtc:null}:t)),hasMore:false});
  }
  if(/^\/api\/tasks\/[^/]+(?:\/complete|\/reopen)?$/.test(p)){const t=tasks.find(t=>p.includes(t.id))??tasks[0];return send({...t,completedAt:p.endsWith('/complete')?stamp:p.endsWith('/reopen')?null:t.completedAt});}
  if(p.endsWith('/reminder'))return route.fulfill({status:204});
  if(p==='/api/task-lists')return send(list([{id:id(80),name:'A task list with a long name',createdAt:stamp,updatedAt:stamp}]));
  if(p==='/api/task-templates')return send(list(templates));
  if(p==='/api/task-templates/page')return send({items:list(templates),hasMore:false,totalCount:empty?0:templates.length});
  if(p.startsWith('/api/task-templates/'))return send(templates.find(t=>p.endsWith(t.id))??templates[0]);
  if(p==='/api/calendar')return send({items:list(items),hasMore:false});
  if(p.startsWith('/api/calendar/events/'))return send(event);
  if(p==='/api/notifications/preferences')return send({settings,revision:1,updatedAt:stamp});
  if(p==='/api/notifications/chat-unread')return send([]);
  if(p==='/api/notifications/unread-count')return send({unreadCount:empty?0:1});
  if(p==='/api/notifications/page')return send({items:list([notification]),nextCursor:null,totalCount:empty?0:1,unreadCount:empty?0:1});
  if(p==='/api/notifications/upcoming/page')return send({items:list([{taskId:id(10),taskTitle:tasks[0].title,dueAtUtc:`${today}T18:00:00Z`,minutesBefore:10}]),nextCursor:null,totalCount:empty?0:1});
  if(p==='/api/notifications/push/presence')return route.fulfill({status:204});
  if(p==='/api/notifications/push/configuration')return send({enabled:false,publicKey:null});
  if(p==='/api/notifications/push/subscriptions')return send([]);
  if(p.endsWith('/notification-preferences'))return send({mode:'allActivity',effectiveMode:'allActivity',soundsMuted:false,mutedUntil:null,revision:1,updatedAt:stamp});
  if(p.endsWith('/chat/state'))return send({boardId:id(2),membershipInstanceId:id(92),latestCursor:'1',slowModeSeconds:0,settingsRevision:1,isMuted:false,mutedUntil:null,serverTime:new Date().toISOString(),moderationRevision:1,nextSendAllowedAt:null,isOwner:true,lastReadSequence:'0',unreadCount:0});
  if(p.endsWith('/chat/read'))return send({lastReadSequence:'1',unreadCount:0});
  if(p.endsWith('/chat/messages'))return send({items:list([message]),olderCursor:null,newerCursor:'1',hasMore:false,catchUpThrough:'1',serverTime:new Date().toISOString()});
  if(p.includes('/chat/messages/'))return send(message);
  if(p.endsWith('/chat/members'))return send({items:[],nextUserId:null});
  missing.push({path:p,method});return route.fulfill({status:501,json:{code:'audit_unhandled_fixture'}});
 });
 await context.route('**/hubs/**',route=>route.fulfill({status:503,json:{code:'audit_realtime_offline'}}));
}
