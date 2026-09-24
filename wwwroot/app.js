const $=id=>document.getElementById(id);
const escapeHtml=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const duration=s=>`${Math.floor(s/60)}:${String(Math.floor(s%60)).padStart(2,'0')}`;
let state={jobs:[],running:false,settings:{}},selected=null,queueFilter='all',queueKey='',detailKey='',uploading=false,timer;
function toast(text){$('toast').textContent=text;$('toast').className='visible';clearTimeout(timer);timer=setTimeout(()=>$('toast').className='',7000)}
async function api(path,options={}){const r=await fetch(path,options);const data=await r.json();if(!r.ok)throw Error(data.detail||'Request failed.');return data}
async function refresh(){try{state=await api('/api/state');render()}catch{$('queueNote').textContent='Server disconnected.';$('runStat').textContent='Offline';$('modelStatus').textContent='Server offline'}}
const isFlagged=j=>j.status==='completed'&&(j.suspicious==='yes'||j.weapon==='yes');
function setQueueFilter(filter){
 queueFilter=filter;render();
 $('videoQueue').scrollIntoView({block:'start'});
}
function render(){
 const visibleJobs=queueFilter==='flagged'?state.jobs.filter(isFlagged):state.jobs;
 $('showFlagged').setAttribute('aria-pressed',String(queueFilter==='flagged'));
 $('filterAll').setAttribute('aria-pressed',String(queueFilter==='all'));
 $('filterFlagged').setAttribute('aria-pressed',String(queueFilter==='flagged'));
 if(!visibleJobs.some(j=>j.id===selected))selected=visibleJobs[0]?.id??null;
 $('count').textContent=visibleJobs.length;
 $('totalStat').textContent=state.jobs.length;
 $('readyStat').textContent=state.jobs.filter(j=>j.status==='completed').length;
 $('flaggedStat').textContent=state.jobs.filter(isFlagged).length;
 $('runStat').textContent=state.running?'Processing':state.jobs.some(j=>j.status==='queued')?'Paused':'Idle';
 $('modelStatus').textContent=state.settings.mode==='local'?state.settings.model:'Notebook GPU';
 $('start').textContent=state.running?'Pause queue':'Generate descriptions';
 $('start').disabled=!state.running&&!state.jobs.some(j=>j.status==='queued');
 $('queueNote').textContent=state.running?'Running. Pause after current video.':'One video at a time.';
 const linked=state.linkedFolder||null;
 $('linkedPath').textContent=linked||'None';
 $('linkedPath').classList.toggle('none',!linked);
 $('linkFolder').textContent=linked?'Change':'Link a folder';
 $('refreshFolder').hidden=!linked;
 $('unlinkFolder').hidden=!linked;
 const key=JSON.stringify([selected,queueFilter,state.jobs.map(j=>[j.id,j.status,j.stage,j.suspicious,j.weapon])]);
 if(key!==queueKey){queueKey=key;$('queue').innerHTML=visibleJobs.length?visibleJobs.map(j=>`<div class="row ${selected===j.id?'selected':''}" role="button" tabindex="0" data-select="${j.id}" aria-label="View video ${state.jobs.indexOf(j)+1}: ${escapeHtml(j.name)}"><span class="queue-number" aria-hidden="true">${state.jobs.indexOf(j)+1}</span><img src="/api/videos/${j.id}/poster.jpg" alt=""><div class="row-info"><span class="row-name">${escapeHtml(j.name)}</span><div class="row-meta">${duration(j.duration)} · ${(j.bytes/1048576).toFixed(1)} MB</div></div><span class="badge ${isFlagged(j)?'flagged':j.status}">${isFlagged(j)?'Flagged':j.status==='completed'?'Ready':j.status==='processing'?'Processing':j.status==='queued'?'Queued':j.status==='failed'?'Failed':'Cancelled'}</span><button class="remove" data-id="${j.id}" data-action="${['queued','processing'].includes(j.status)?'cancel':'delete'}" aria-label="${['queued','processing'].includes(j.status)?'Cancel':'Remove'} ${escapeHtml(j.name)}">×</button></div>`).join(''):`<div class="empty">${queueFilter==='flagged'?'No flagged videos.':'No videos added.'}</div>`}
 const job=state.jobs.find(j=>j.id===selected);const detail=JSON.stringify(job??null);if(detail===detailKey)return;detailKey=detail;
 $('status').textContent=job?.stage??'';
 if(job&&$('detail').dataset.id===job.id){$('result').innerHTML=result(job);return}
 $('detail').dataset.id=job?.id??'';
 $('detail').innerHTML=job?`<div class="video-wrap"><video controls playsinline preload="metadata" src="/api/videos/${job.id}/video" poster="/api/videos/${job.id}/poster.jpg"></video><div class="video-meta">${escapeHtml(job.name)} · ${duration(job.duration)}</div></div><div class="result" id="result">${result(job)}</div>`:'<div class="placeholder">Select a video</div><div class="result"><h3>Description</h3><p class="waiting">No description yet.</p></div>';
}
function result(j){
 if(j.status==='completed')return `<h3>Description</h3><p>${escapeHtml(j.description)}</p><div class="tags"><span class="flag-${j.suspicious==='yes'?'yes':'no'}">Suspicious: ${escapeHtml(j.suspicious??'unknown')}</span><span class="flag-${j.weapon==='yes'?'yes':'no'}">Weapon: ${escapeHtml(j.weaponEnabled===false?'Disabled':j.weapon??'unknown')}</span><span>${j.timestamps.length} frames · ${j.elapsed}s${j.personLabels?' · Person labels':''}</span></div><div class="result-actions"><button data-copy="${j.id}">Copy</button><button data-download="${j.id}">Download</button></div>`;
 if(j.status==='failed')return `<h3>Generation failed</h3><p class="error">${escapeHtml(j.error)}</p><div class="result-actions"><button data-id="${j.id}" data-action="retry">Retry</button></div>`;
 if(j.status==='cancelled')return `<h3>Cancelled</h3><div class="result-actions"><button data-id="${j.id}" data-action="retry">Requeue</button></div>`;
 return `<h3>Description</h3><p class="waiting">${j.status==='processing'?escapeHtml(j.stage)+'…':'Queued. Click Generate descriptions to start.'}</p>`;
}
const isVideo=file=>file.type.startsWith('video/')||/\.(mp4|mov|webm|m4v|avi|mkv|mpeg|mpg|mts|m2ts|wmv|3gp|ogv)$/i.test(file.name);
async function upload(files,{skipped=0}={}){
 if(uploading){toast('Wait for the current upload to finish.');return}
 if(!files.length){toast('No videos found in this folder.');$('folder').value='';return}
 uploading=true;$('addFolder').disabled=true;$('files').disabled=true;
 $('uploadErrors').hidden=true;
 let added=0;const errors=[];
 try{
  for(let i=0;i<files.length;i++){
   const file=files[i],name=file.webkitRelativePath||file.name;
   $('uploadStatus').textContent=`Adding ${i+1}/${files.length}: ${name}`;
   if(file.size>500*1024*1024){errors.push(`${name}: maximum file size is 500 MB.`);continue}
   try{const j=await api('/api/videos',{method:'POST',headers:{'Content-Type':'application/octet-stream','X-Filename':encodeURIComponent(file.name)},body:file});selected=j.id;added++;await refresh()}catch(e){errors.push(`${name}: ${e.message}`)}
  }
 }finally{
  uploading=false;$('addFolder').disabled=false;$('files').disabled=false;
  $('files').value='';$('folder').value='';
  $('uploadStatus').textContent=`${added} added${skipped?' · '+skipped+' non-video files skipped':''}${errors.length?' · '+errors.length+' failed':''}`;
  $('uploadErrors').hidden=!errors.length;
  $('uploadErrorSummary').textContent=`${errors.length} videos could not be added`;
  $('uploadErrorList').innerHTML=errors.map(message=>`<li>${escapeHtml(message)}</li>`).join('');
 }
}
$('addFolder').onclick=()=>$('folder').click();
$('folder').onchange=e=>{
 const all=[...e.target.files];
 if(!all.length)return;
 const videos=all.filter(isVideo).sort((a,b)=>(a.webkitRelativePath||a.name).localeCompare(b.webkitRelativePath||b.name,undefined,{numeric:true}));
 upload(videos,{skipped:all.length-videos.length});
};
$('files').onchange=e=>upload([...e.target.files]);
$('dropzone').onkeydown=e=>{if(['Enter',' '].includes(e.key)){e.preventDefault();$('files').click()}};
for(const event of ['dragenter','dragover'])$('dropzone').addEventListener(event,e=>{e.preventDefault();$('dropzone').classList.add('drag')});
for(const event of ['dragleave','drop'])$('dropzone').addEventListener(event,e=>{e.preventDefault();$('dropzone').classList.remove('drag')});
$('dropzone').ondrop=e=>upload([...e.dataTransfer.files]);
$('start').onclick=async()=>{try{await api(`/api/queue/${state.running?'pause':'start'}`,{method:'POST'});await refresh()}catch(e){toast(e.message)}};
function scanResult(data){
 const bits=[`${data.added} added`];
 if(data.skipped)bits.push(`${data.skipped} already in the queue`);
 if(data.errors.length)bits.push(`${data.errors.length} skipped`);
 $('linkStatus').textContent=bits.join(' · ');
 $('uploadErrors').hidden=!data.errors.length;
 $('uploadErrorSummary').textContent=`${data.errors.length} videos could not be added`;
 $('uploadErrorList').innerHTML=data.errors.map(m=>`<li>${escapeHtml(m)}</li>`).join('');
}
async function postFolder(path){
 const busy=path===null?'unlinkFolder':'linkFolder';
 $(busy).disabled=true;$('linkStatus').textContent=path===null?'Unlinking…':'Scanning folder…';
 try{
  const data=await api('/api/folder',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({path})});
  $('linkDialog').close();scanResult(data);await refresh();
 }finally{$(busy).disabled=false}
}
$('linkFolder').onclick=()=>{$('linkPath').value=state.linkedFolder||'';$('linkError').textContent='';$('linkDialog').showModal()};
$('linkClose').onclick=()=>$('linkDialog').close();
$('linkForm').onsubmit=async e=>{e.preventDefault();$('linkError').textContent='';try{await postFolder($('linkPath').value)}catch(err){$('linkError').textContent=err.message}};
$('unlinkFolder').onclick=async()=>{try{await postFolder(null);$('linkStatus').textContent='Folder unlinked. Videos already added stay in the queue.'}catch(e){toast(e.message)}};
$('refreshFolder').onclick=async()=>{
 $('refreshFolder').disabled=true;$('linkStatus').textContent='Scanning folder…';
 try{scanResult(await api('/api/folder/refresh',{method:'POST'}));await refresh()}
 catch(e){$('linkStatus').textContent='';toast(e.message)}
 finally{$('refreshFolder').disabled=false}
};
function settingsNote(){const local=$('mode').value==='local';$('personLabelsLabel').hidden=false;$('modelLabel').style.display=local?'block':'none';$('model').required=local;$('settingsNote').textContent=local?`Custom prompt · ${state.frameLimit} frames · 600 output tokens. Smaller models may produce different results.`:'Qwen3 notebook backend · 150 frames · Screening settings included.'}
let modelListRequest=0;
function setModelOptions(models,current){
 const choices=[...new Set([...(current?[current]:[]),...models])];
 const chosen=current||choices[0]||'';
 $('model').value=chosen;
 $('modelChoices').innerHTML=choices.length?choices.map(id=>`<label class="model-choice"><input type="radio" name="modelChoice" value="${escapeHtml(id)}" ${id===chosen?'checked':''}><span>${escapeHtml(id)}${id===current&&!models.includes(id)?' (saved)':''}</span></label>`).join(''):'<p>No models installed</p>';
}
$('modelChoices').onchange=e=>{if(e.target.name==='modelChoice')$('model').value=e.target.value};
async function loadModels(){
 const request=++modelListRequest;
 if($('mode').value!=='local')return;
 const current=$('model').value||state.settings.model;
 $('refreshModels').disabled=true;$('settingsError').textContent='';
 try{
  const data=await api('/api/models?endpoint='+encodeURIComponent($('endpoint').value));
  if(request!==modelListRequest)return;
  setModelOptions(data.models,$('model').value||current);
  if(!data.models.length)$('settingsError').textContent='No models installed on this server.';
 }catch(e){if(request===modelListRequest)$('settingsError').textContent=e.message}
 finally{if(request===modelListRequest)$('refreshModels').disabled=false}
}
$('settingsButton').onclick=()=>{
 $('mode').value=state.settings.mode;$('endpoint').value=state.settings.endpoint;
 setModelOptions([],state.settings.model);$('personLabels').checked=state.settings.personLabels!==false;
 $('batchProcessing').checked=state.settings.batchProcessing===true;
 $('flagActivity').value=state.settings.flagActivity||'';
 $('siteContextEnabled').checked=state.settings.siteContextEnabled===true;
 $('siteContext').value=state.settings.siteContext||'';
 $('siteContext').disabled=!$('siteContextEnabled').checked;
 $('weaponEnabled').checked=state.settings.weaponEnabled!==false;
 $('settingsError').textContent='';settingsNote();$('settings').showModal();loadModels();
};
$('refreshModels').onclick=loadModels;
$('endpoint').onchange=loadModels;
$('mode').onchange=()=>{modelListRequest++;$('refreshModels').disabled=false;$('endpoint').value=$('mode').value==='local'?'http://127.0.0.1:11434/v1':'';settingsNote();loadModels()};
$('close').onclick=()=>$('settings').close();
$('settingsForm').onsubmit=async e=>{e.preventDefault();try{await api('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({mode:$('mode').value,endpoint:$('endpoint').value,model:$('model').value,personLabels:$('personLabels').checked,batchProcessing:$('batchProcessing').checked,flagActivity:$('flagActivity').value,siteContextEnabled:$('siteContextEnabled').checked,siteContext:$('siteContext').value,weaponEnabled:$('weaponEnabled').checked})});$('settings').close();await refresh();toast('Settings saved.')}catch(e){$('settingsError').textContent=e.message}};
document.addEventListener('click',async e=>{
 const action=e.target.closest('[data-action]');if(action){try{await api(`/api/videos/${action.dataset.id}/${action.dataset.action}`,{method:'POST'});await refresh()}catch(e){toast(e.message)}return}
 const row=e.target.closest('[data-select]');if(row){selected=row.dataset.select;render();return}
 const copy=e.target.closest('[data-copy]');if(copy){try{await navigator.clipboard.writeText(state.jobs.find(j=>j.id===copy.dataset.copy).description);toast('Copied.')}catch{toast('Select the description and copy it manually.')}return}
 const download=e.target.closest('[data-download]');if(download){const job=state.jobs.find(j=>j.id===download.dataset.download);const url=URL.createObjectURL(new Blob([job.description],{type:'text/plain'}));const a=document.createElement('a');a.href=url;a.download=job.name.replace(/\.[^.]+$/,'')+'-description.txt';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000)}
});
document.addEventListener('keydown',e=>{const row=e.target.closest('[data-select]');if(row&&e.target===row&&['Enter',' '].includes(e.key)){e.preventDefault();selected=row.dataset.select;render()}});
refresh();setInterval(refresh,1200);

$('navSettings').onclick=()=>$('settingsButton').click();

$('showFlagged').onclick=()=>setQueueFilter('flagged');
$('filterFlagged').onclick=()=>setQueueFilter('flagged');
$('filterAll').onclick=()=>setQueueFilter('all');

$('siteContextEnabled').onchange=()=>{$('siteContext').disabled=!$('siteContextEnabled').checked};
