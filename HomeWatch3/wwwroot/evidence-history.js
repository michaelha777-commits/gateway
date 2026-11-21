(()=>{
  const el=id=>document.getElementById(id);
  let next=null,query=null,sequence=0;
  async function load(append){
    const generation=++sequence;
    const params=append?new URLSearchParams(query):new URLSearchParams(new FormData(el('rawFilters')));
    for(const key of [...params.keys()])if(!params.get(key))params.delete(key);
    if(!append){
      query=params.toString();
      el('rawCsv').href='/api/history/evidence/export?format=csv&'+query;
      el('rawJson').href='/api/history/evidence/export?format=json&'+query;
    }else for(const [key,value] of Object.entries(next))params.set(key,value);
    el('rawNext').disabled=true;
    try{
      const resp=await fetch('/api/history/evidence?'+params,{cache:'no-store'});
      if(!resp.ok)throw Error('History request failed ('+resp.status+')');
      const page=await resp.json();
      if(generation!==sequence)return;
      if(!append)el('rawRows').replaceChildren();
      for(const row of page.rows){
        const details=document.createElement('details'),summary=document.createElement('summary'),pre=document.createElement('pre');
        summary.textContent=[fmt(row.timestampUtc),row.source,row.domain||row.destinationIp||row.application||'Observation'].join(' · ');
        pre.textContent=JSON.stringify(row,null,2);pre.style.whiteSpace='pre-wrap';pre.style.overflowWrap='anywhere';
        details.append(summary,pre);el('rawRows').append(details);
      }
      if(!append&&!page.rows.length)el('rawRows').textContent='No matching evidence.';
      next=page.next;el('rawNext').hidden=!next;
    }catch(e){if(generation===sequence)el('rawRows').textContent=e.message;}
    finally{if(generation===sequence)el('rawNext').disabled=false;}
  }
  el('rawHistory').ontoggle=()=>{if(el('rawHistory').open&&query===null)load(false);};
  el('rawFilters').onsubmit=e=>{e.preventDefault();load(false);};
  el('rawNext').onclick=()=>load(true);
})();
