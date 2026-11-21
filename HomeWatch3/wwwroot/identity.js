(()=>{
  const $=id=>document.getElementById(id),id=new URLSearchParams(location.search).get('id');
  if(!id)return;
  const url='/api/devices/'+encodeURIComponent(id)+'/identity';
  async function load(){
    const response=await fetch(url);if(!response.ok)throw Error('Identity history unavailable');
    const data=await response.json(),form=$('ownership').elements;
    form.owner.value=data.profile?.owner||'';form.labels.value=data.profile?.labels||'';
    $('splitDevice').hidden=!data.group.some(x=>String(x.device)===id);
    const box=$('identityRecords');box.replaceChildren();
    const lines=[...(data.locallyAdministeredMac?['Locally administered MAC. This can include randomized addresses.']:[]),
      ...data.group.map(g=>'Device '+g.device+' grouped under '+g.canonical),
      ...data.observations.map(x=>[HomeWatchTime.format(x.stamp),x.source,x.name,x.mac,x.ip].filter(Boolean).join(' · ')),
      ...data.audit.map(x=>[HomeWatchTime.format(x.stamp),x.actor,x.action,x.detail].join(' · '))];
    for(const line of lines){const p=document.createElement('p');p.textContent=line;box.append(p);}
  }
  async function save(change){
    const response=await fetch(url,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(change)});
    if(!response.ok)throw Error('Identity change failed ('+response.status+')');
    await load();$('identityOutcome').textContent='Saved';
  }
  function run(p){p.catch(e=>$('identityOutcome').textContent=e.message);}
  $('ownership').onsubmit=e=>{e.preventDefault();run(save({action:'profile',...Object.fromEntries(new FormData(e.target))}));};
  $('deviceGroup').onsubmit=e=>{e.preventDefault();run(save({action:'merge',target:Number(e.target.elements.target.value)}));};
  $('splitDevice').onclick=()=>run(save({action:'split'}));run(load());
})();
