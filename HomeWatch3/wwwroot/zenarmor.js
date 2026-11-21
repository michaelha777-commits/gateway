const el=id=>document.getElementById(id);
const localTime=new Intl.DateTimeFormat('en-CA',{timeZone:'America/Toronto',dateStyle:'short',timeStyle:'long'});
let next=null, sequence=0;
async function json(url,options){
  const response=await fetch(url,{cache:'no-store',...options});
  if(!response.ok)throw new Error('Request failed ('+response.status+')');
  return response.json();
}
async function health(){
  const s=await json('/api/zenarmor/status');
  el('health').textContent=[s.state,'Last sync '+(s.lastSuccessUtc?localTime.format(new Date(s.lastSuccessUtc)):'never'),
    s.retained+' retained',s.imported+' DNS records imported','DNS cursor '+s.cursor,'Poll '+s.pollSeconds+'s',
    (s.storageBytes/1048576).toFixed(1)+' MiB',s.latestSourceMs?'Latest source '+localTime.format(s.latestSourceMs):null,
    s.nextRetryUtc?'Retry '+localTime.format(new Date(s.nextRetryUtc)):null].filter(Boolean).join(' · ');
  const sources=await json('/api/zenarmor/sources');el('sources').replaceChildren();
  for(const row of sources){const p=document.createElement('p');p.textContent=row.source+' · '+row.imported+' imported · cursor '+row.cursor+' · '+(row.error||(row.synced?localTime.format(new Date(row.synced)):'Awaiting collection'));el('sources').append(p);}
  el('sync').disabled=!s.enabled;
  el('toggle').disabled=false;el('toggle').textContent=s.enabled?'Disable collection':'Enable collection';
  el('toggle').onclick=()=>run(json('/api/zenarmor/enabled/'+!s.enabled,{method:'PUT'}).then(health));
}
async function events(append=false){
  const request=++sequence, params=new URLSearchParams(new FormData(el('filters')));
  for(const key of [...params.keys()])if(!params.get(key))params.delete(key);
  if(append&&next)for(const [key,value] of Object.entries(next))params.set(key,value);
  el('more').disabled=true;
  const source=params.get('source'); params.delete('source');
  const page=await json('/api/zenarmor/evidence/'+encodeURIComponent(source)+'?'+params);
  if(request!==sequence)return;
  if(!append)el('events').replaceChildren();
  for(const envelope of page.rows){
    const row=envelope.evidence;
    const details=document.createElement('details'),title=document.createElement('summary'),body=document.createElement('p');
    title.textContent=localTime.format(Number(row.start_time))+' · '+(row.query||row.host||row.sni||row.dst_hostname||row.ip_dst_saddr||source)+' · '+(row.domain_categories||row.app_category||row.category||'Unclassified');
    body.textContent=[row.device_name,row.ip_src_saddr,row.src_hwaddr,source+' #'+row.id,row.app_name,row.uri,row.signature,
      'Connection '+(row.conn_uuid||'unknown'),'Community ID '+(row.community_id||'unknown')].filter(Boolean).join(' · ');
    const raw=document.createElement('pre'); raw.style.whiteSpace='pre-wrap';raw.style.overflowWrap='anywhere';raw.textContent=JSON.stringify(row,null,2);
    const related=document.createElement('button');related.type='button';related.textContent='Related evidence';
    related.onclick=()=>run(json('/api/zenarmor/related/'+source+'/'+envelope.epoch+'/'+row.id).then(matches=>{
      const list=document.createElement('ul');for(const m of matches){const li=document.createElement('li');li.textContent=m.source+' #'+m.id+' · '+(m.domain||m.category||'Observation');list.append(li);}
      if(!matches.length)list.textContent='No records share sufficient identifiers.';related.replaceWith(list);
    }));
    details.append(title,body,raw,related);
    if(['alerts','security-tags'].includes(source)){
      const url='/api/zenarmor/investigation/'+source+'/'+envelope.epoch+'/'+row.id;
      const notes=document.createElement('div'),note=document.createElement('input'),ack=document.createElement('button');
      note.maxLength=1000;note.placeholder='Investigation note';ack.type='button';ack.textContent='Acknowledge';
      async function refresh(){notes.replaceChildren();for(const entry of await json(url)){
        const p=document.createElement('p');p.textContent=[localTime.format(new Date(entry.time)),entry.actor,entry.note,entry.acknowledged?'Acknowledged':''].join(' · ');notes.append(p);}}
      details.addEventListener('toggle',()=>{if(details.open)run(refresh());},{once:true});
      ack.onclick=()=>run(json(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({note:note.value,acknowledged:true})}).then(()=>{note.value='';return refresh();}));
      details.append(note,ack,notes);
    }el('events').append(details);
  }
  if(!append&&!page.rows.length)el('events').textContent='No retained observations match these filters.';
  next=page.next;el('more').hidden=!next;el('more').disabled=false;
}
async function categories(){
  for(const row of await json('/api/zenarmor/categories')){
    const button=document.createElement('button');
    button.className='button secondary-button';
    button.textContent=row.day+' · '+(row.category||'Unclassified')+' · '+row.observations+' queries · '+row.blocked+' blocked';
    button.onclick=()=>{el('filters').elements.source.value='dns';el('filters').elements.category.value=row.category;run(events());};
    el('categories').append(button);
  }
}
function run(promise){promise.catch(e=>{el('health').textContent=e.message;el('more').disabled=false;});}
el('filters').onsubmit=e=>{e.preventDefault();run(events());};
el('more').onclick=()=>run(events(true));
el('sync').onclick=()=>run(fetch('/api/zenarmor/sync',{method:'POST'}).then(response=>{if(!response.ok)throw Error('Sync request failed');return health();}));
async function overview(){
  const data=await json('/api/zenarmor/overview');
  for(const [key,title] of [['domains','Top domains'],['devices','Top devices'],['blocked','Blocked domains']]){
    const h=document.createElement('h3');h.textContent=title;el('overview').append(h);
    const list=document.createElement('ol');
    for(const row of data[key]){
      const item=document.createElement('li');item.textContent=row.label+' · '+row.count;list.append(item);
    }
    el('overview').append(list);
  }
}
const requested=new URLSearchParams(location.search).get('source');
if([...el('filters').elements.source.options].some(x=>x.value===requested))el('filters').elements.source.value=requested;
run(Promise.all([health(),events(),categories(),overview()]));
