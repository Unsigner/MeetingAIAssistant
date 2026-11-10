const $ = id => document.getElementById(id);
const node = (tag, text, cls) => { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; if (cls) e.className = cls; return e; };
let corpus, lastContext = null, sessionScope = null, busy = false, sequence = 0;
const snapshots = [];
const emptyTemplate = $('empty-state').cloneNode(true);
function link(text, href) { const a = node('a', text, 'source-link'); a.href = href; a.target = '_blank'; a.rel = 'noopener noreferrer'; return a; }
async function readResponse(response) { if (!response.ok) { let body; try { body = await response.json(); } catch {} throw new Error(body?.error || `Request failed (${response.status}).`); } return response.json(); }
function showError(text) { $('error').textContent = text; $('error').hidden = false; }
function request(question) { return { question, delegation: $('delegation').value || null, year: Number($('year').value) || null, session: sessionScope, groupsOnly: $('groups').checked, includeProcedural: $('procedural').checked, generate: $('generate').checked, searchMode: $('search-mode').value }; }
function setBusy(value) {
  busy = value;
  $('ask').disabled = $('new-conversation').disabled = $('question').disabled = value;
  $('ask').textContent = value ? 'Reading…' : 'Send ↑';
  document.querySelectorAll('.scope-panel input,.scope-panel select').forEach(e => e.disabled = value || e.dataset.fixed === 'true' || (e.id === 'generate' && !corpus?.generation.enabled));
  document.querySelectorAll('[data-research-action]').forEach(e => e.disabled = value || e.dataset.unavailable === 'true');
}
function applyScope(r) {
  if (r.delegation && ![...$('delegation').options].some(o => o.value === r.delegation)) { const o = node('option', `${r.delegation} (no indexed statements)`); o.value = r.delegation; $('delegation').append(o); }
  $('delegation').value = r.delegation || '';
  if (r.year && ![...$('year').options].some(o => o.value === String(r.year))) { const o = node('option', `${r.year} (no records)`); o.value = r.year; $('year').append(o); }
  $('year').value = r.year ? String(r.year) : '';
  $('groups').checked = r.groupsOnly; $('procedural').checked = r.includeProcedural; sessionScope = r.session || null;
  $('session-scope').hidden = !sessionScope;
  $('session-scope').textContent = sessionScope ? `General Assembly session ${sessionScope}. Changing the year clears this session restriction.` : '';
}
function openCoverage() { $('corpus-details').open = true; $('corpus-details').scrollIntoView({ behavior:'smooth', block:'start' }); }
async function loadCorpus() {
  try {
    corpus = await readResponse(await fetch('/api/corpus'));
    const years = [...new Set(corpus.documents.map(x => x.document.date.slice(0,4)))].sort();
    const indexed = corpus.documents.filter(x => x.ingestion.status === 'indexed').length;
    const pages = corpus.documents.reduce((n,x) => n + x.ingestion.pages, 0);
    $('corpus-summary').textContent = `${corpus.documents.length} records · ${years[0]}–${years.at(-1)} · Selected HRC agenda sections · English`;
    $('index-status').textContent = `${indexed} of ${corpus.documents.length} records indexed · ${pages} PDF pages`;
    $('coverage').textContent = corpus.coverage;
    $('corpus-count').textContent = `${corpus.interventions} interventions · ${corpus.retrieval.passages} passages`;
    for (const year of years) for (const id of ['year','compare-from','compare-to']) { const o = node('option',year); o.value=year; $(id).append(o); }
    $('compare-from').value = years.at(-2) || years[0]; $('compare-to').value = years.at(-1);
    for (const name of corpus.delegations) { const o = node('option',name); o.value=name; $('delegation').append(o); }
    for (const {document:d,ingestion:s} of corpus.documents) { const row = node('div',undefined,'document'); row.append(link(d.symbol,`/api/documents/${encodeURIComponent(d.id)}/pdf`),node('span',d.date),node('span',d.notes),node('span',s.status === 'indexed' ? `${s.pages} pages · indexed` : `Failed: ${s.error}`)); $('documents').append(row); }
    $('generate').checked = corpus.generation.enabled;
    $('model-status').textContent = `${corpus.retrieval.ready ? `Semantic index ready: ${corpus.retrieval.model}.` : corpus.retrieval.warning} ${corpus.generation.enabled ? `Optional answers: ${corpus.generation.model}.` : 'AI generation is disabled.'}`;
    setBusy(false);
  } catch(e) { showError(e.message); $('request-status').textContent = 'The corpus could not be loaded. Restart the app or refresh after correcting the error.'; $('ask').disabled=true; }
}
function snippet(text, size=260) { if (text.length <= size) return text; const end=text.lastIndexOf(' ',size); return text.slice(0,end>size/2?end:size)+'…'; }
function scopeNote(result) { const p=node('div',undefined,'scope-note'); p.append(node('strong','SCOPE AND LIMITATION'),node('span',`${result.retrieval?.scope || ''} Retrieval: ${result.retrieval?.mode || 'keyword'}. ${result.retrieval?.passagesInScope ?? 0} passages within scope. Selected records; not exhaustive.`)); return p; }
function renderText(target, text, citations) {
  let position=0;
  for (const match of text.matchAll(/\[([A-Za-z0-9_.-]+)\]/g)) {
    target.append(document.createTextNode(text.slice(position,match.index)));
    if (citations.has(match[1])) { const a=node('a',match[0],'citation'); a.href='#'+citations.get(match[1]); a.addEventListener('click',event=>{ event.preventDefault(); const e=document.getElementById(citations.get(match[1])); if (!e) return; let ancestor=e; while (ancestor) { if (ancestor.tagName==='DETAILS') ancestor.open=true; if(ancestor.hidden) ancestor.hidden=false; ancestor=ancestor.parentElement; } e.classList.add('matched'); e.scrollIntoView({behavior:'smooth',block:'center'}); }); target.append(a); }
    else target.append(document.createTextNode(match[0]));
    position=match.index+match[0].length;
  }
  target.append(document.createTextNode(text.slice(position)));
}
function contextButton(e) { const b=node('button','Read passage and context','context-button'); b.type='button'; b.addEventListener('click',()=>openContext(e.id)); return b; }
function extraPassage(e, target) {
  const div=node('div',undefined,'passage-item'); div.id=target;
  div.append(node('small',`${e.symbol} · ${e.date} · ${e.speaker} · PDF p. ${e.page}`),node('p',e.excerpt),contextButton(e),link('Open source page',e.localPdfUrl),link('Official document',e.officialPdfUrl));
  return div;
}
function renderEvidence(result, prefix, citations) {
  const container=node('div');
  const groups=new Map();
  for (const e of result.evidence) { const key=JSON.stringify([e.delegation,e.capacity,e.representedGroup]); if(!groups.has(key))groups.set(key,[]); groups.get(key).push(e); citations.set(e.id,`${prefix}-${e.id}`); }
  if (!groups.size) return container;
  const scroll=node('div',undefined,'table-scroll'), table=node('table'), head=node('thead'), hr=node('tr');
  for (const text of ['Delegation / capacity','Meeting / speaker','Evidence','Source']) hr.append(node('th',text));
  head.append(hr);table.append(head);const body=node('tbody');table.append(body);scroll.append(table);container.append(scroll);
  let ordinal=0;
  for (const evidence of groups.values()) {
    const e=evidence[0], row=node('tr');row.id=citations.get(e.id);row.hidden=ordinal++>=5;
    const who=node('td');who.append(node('strong',e.delegation)); if(e.representedGroup)who.append(node('div',`Speaking on behalf of ${e.representedGroup}`,'group-label'));else who.append(node('small',e.capacity));
    const meeting=node('td');meeting.append(node('strong',e.symbol),node('small',e.speaker),node('small',e.date));
    const text=node('td');text.append(node('div',snippet(e.excerpt)),contextButton(e));
    if(evidence.length>1){const more=node('details',undefined,'extra-passages');more.append(node('summary',`${evidence.length-1} more retrieved passage${evidence.length>2?'s':''}`));for(const item of evidence.slice(1))more.append(extraPassage(item,citations.get(item.id)));text.append(more);}
    const source=node('td');source.append(link(`Open p. ${e.page} ↗`,e.localPdfUrl),link('Official source',e.officialPdfUrl));
    const ranks=node('details');ranks.append(node('summary','Ranking details'),node('p',`Keyword: ${e.keywordRank??'—'} · Semantic: ${e.semanticRank??'—'}${e.semanticSimilarity!=null?` · Cosine ${e.semanticSimilarity.toFixed(3)} (not confidence)`:''}`));ranks.className='hint';source.append(ranks);
    row.append(who,meeting,text,source);body.append(row);
  }
  if(groups.size>5){const more=node('button',`Show all ${groups.size} retrieved speaking-capacity rows`,'text-button');more.addEventListener('click',()=>{body.querySelectorAll('tr').forEach(r=>r.hidden=false);more.hidden=true;});container.append(more);}
  return container;
}
function renderResult(result, target, prefix) {
  const refs=new Map(), evidence=renderEvidence(result,prefix,refs);
  const unique=new Set(result.evidence.map(e=>e.delegation)).size;
  const title=result.evidence.length ? `Retrieved evidence from ${unique} delegation${unique===1?'':'s'}, organization${unique===1?'':'s'} or institutional speaker${unique===1?'':'s'}.` : 'No evidence returned for this scope.';
  target.append(node('p',title,'answer-title'),scopeNote(result));
  const answer=node('div',undefined,'answer');
  if(result.mode.startsWith('Local RAG') || result.evidence.length===0)renderText(answer,result.answer,refs);
  else answer.append(node('p','These are ranked matches from the selected records, not a complete count of statements on this issue.'));
  target.append(answer);
  if(result.retrieval?.warning)target.append(node('p',result.retrieval.warning,'warning'));
  if(result.generationWarning)target.append(node('p',result.generationWarning,'warning'));
  target.append(node('p',`${result.mode} · ${result.returnedEvidence} returned passages · ${result.matchingInterventions} retrieved interventions.${result.evidenceTruncated?' Return limit reached; this answer and its export contain only these returned passages.':''}`,'result-meta'),evidence);
}
function renderComparison(reply,target,prefix) {
  target.append(node('p',reply.message,'warning'));
  const grid=node('div',undefined,'compare-grid');
  for(const item of reply.comparison){const col=node('section',undefined,'year-result');col.append(node('h3',String(item.year)),node('p',`${item.corpusRecords} source record${item.corpusRecords===1?'':'s'} in the corpus for this year. This is not a matched-document count.`,'hint'));
    const result=item.result;col.append(scopeNote(result),node('p',result.answer,'hint'));
    if(result.retrieval.warning)col.append(node('p',result.retrieval.warning,'warning'));
    const details=node('details'),moreSummary=node('summary');details.append(moreSummary);const displayed=new Set();let leadCount=0;
    result.evidence.forEach(e=>{const block=node('div',undefined,'compact-evidence');block.id=`${prefix}-${item.year}-${e.id}`;block.append(node('strong',e.delegation));if(e.representedGroup)block.append(node('div',`Speaking on behalf of ${e.representedGroup}`,'group-label'));else if(/President|Chair|Secretariat/.test(e.capacity))block.append(node('small',e.capacity));block.append(node('small',`${e.speaker} · ${e.date} · ${e.symbol} · PDF p. ${e.page}`),node('p',snippet(e.excerpt,230)),contextButton(e),link('Open source page',e.localPdfUrl));if(leadCount<2&&!displayed.has(e.delegation)){displayed.add(e.delegation);leadCount++;col.append(block);}else details.append(block);});
    moreSummary.textContent=`${result.evidence.length-leadCount} more returned passages`;
    if(result.evidence.length>leadCount)col.append(details);grid.append(col);
  }
  target.append(grid);
}
function actionButton(text,action,reply) {
  const b=node('button',text,'action-chip');b.dataset.researchAction=action;
  if(action==='groups' && reply.resolvedRequest.groupsOnly){b.dataset.unavailable='true';b.disabled=true;}
  b.addEventListener('click',()=>{
    const labels={groups:'Show group statements',compare:'Compare by year','previous-year':'What about the previous year?','previous-session':'What about the previous session?'};
    const source=reply.resolvedRequest;
    const pending={...source,question:labels[action],generate:$('generate').checked,searchMode:$('search-mode').value};
    send(pending,source,action,action==='compare'?[Number($('compare-from').value),Number($('compare-to').value)]:null);
  });return b;
}
function exportSnapshot(snapshot) {
  const header=['Comparison year','Question','Resolved question','Delegation','Speaker','Capacity','Represented group','Meeting date','Document','PDF page','Excerpt','Official source','Passage ID','Retrieval mode','Scope','Corpus coverage'];
  const rows=[header];const reply=snapshot.reply;
  const results=reply.comparison?.map(x=>({year:x.year,result:x.result})) || [{year:'',result:reply.result}];
  for(const {year,result} of results) for(const e of result.evidence)rows.push([year,snapshot.message,reply.resolvedRequest.question,e.delegation,e.speaker,e.capacity,e.representedGroup||'',e.date,e.symbol,e.page,e.excerpt,e.officialPdfUrl,e.id,result.retrieval.mode,result.retrieval.scope,result.coverage]);
  const csv=value=>{let s=String(value??'');if(/^[=+@-]/.test(s.trimStart()))s="'"+s;return '"'+s.replaceAll('"','""')+'"';};
  const url=URL.createObjectURL(new Blob(['\uFEFF'+rows.map(r=>r.map(csv).join(',')).join('\r\n')],{type:'text/csv;charset=utf-8'}));
  const a=node('a');a.href=url;a.download=`meeting-evidence-answer-${snapshot.id}.csv`;document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
async function send(pending,previous=lastContext,action='ask',compareYears=null) {
  if(busy)return;
  $('error').hidden=true;$('empty-state')?.remove();
  const turn=node('article',undefined,'turn'),id=++sequence;turn.id=`turn-${id}`;
  turn.append(node('p',pending.question,'user-message'),node('div','RESEARCH ASSISTANT','assistant-label'));
  const content=node('div');content.append(node('p','Reading the selected records…','hint'));turn.append(content);$('messages').append(turn);
  setBusy(true);$('request-status').textContent=action==='compare'?'Comparing two evidence sets…':'Resolving scope and retrieving evidence…';
  try {
    const reply=await readResponse(await fetch('/api/research',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({request:pending,previousRequest:previous,action,compareYears})}));
    content.replaceChildren();
    if(reply.kind==='clarification'){
      content.append(node('p',reply.message,'warning'));
      const retry=node('button','Edit question or scope and retry','secondary');retry.addEventListener('click',()=>{$('question').value=pending.question;$('question').focus();});content.append(retry);
    }else{
      const snapshot={id,message:pending.question,reply:structuredClone(reply)};snapshots.push(snapshot);
      content.append(node('p',reply.kind==='comparison'?'Continued the previous topic for a two-year evidence comparison.':reply.message,'hint resolution'));
      if(reply.kind==='comparison')renderComparison(reply,content,`answer-${id}`);else renderResult(reply.result,content,`answer-${id}`);
      const actions=node('div',undefined,'actions');
      for(const [label,act] of [['Compare by year','compare'],['Show group statements','groups'],['Previous year','previous-year'],['Previous session','previous-session']])actions.append(actionButton(label,act,reply));
      const exportButton=node('button','Export returned evidence','action-chip');exportButton.disabled=!(reply.result?.evidence.length || reply.comparison?.some(x=>x.result.evidence.length));exportButton.addEventListener('click',()=>exportSnapshot(snapshot));actions.append(exportButton);content.append(actions);
      lastContext=structuredClone(reply.resolvedRequest);applyScope(lastContext);
    }
    turn.scrollIntoView({behavior:'smooth',block:'start'});
  }catch(e){content.replaceChildren(node('p',e.message,'warning'));showError(e.message);$('question').value=pending.question;}
  finally{setBusy(false);$('request-status').textContent='Follow-ups: same question in 2023 · show group statements · previous session · compare with 2024. Conversation stays in this tab.';}
}
async function openContext(id) {
  try { const p=await readResponse(await fetch(`/api/passages/${encodeURIComponent(id)}`));$('passage-title').textContent=`${p.intervention.delegation} · ${p.intervention.symbol}`;$('passage-text').replaceChildren(node('p',`${p.intervention.speaker}. ${p.intervention.capacity}${p.intervention.representedGroup?`: speaking on behalf of ${p.intervention.representedGroup}`:''}.`,'hint'));
    for(const s of p.context)$('passage-text').append(node('h3',`${s.id===id?'Retrieved passage':'Adjacent context'} · PDF page ${s.page}`),node('p',s.text));
    const full=node('button','Read full intervention','secondary');full.addEventListener('click',()=>openIntervention(p.intervention.id));$('passage-text').append(full);if(!$('passage').open)$('passage').showModal();
  }catch(e){showError(e.message);}
}
async function openIntervention(id) {
  try {const p=await readResponse(await fetch(`/api/interventions/${encodeURIComponent(id)}`));$('passage-title').textContent=`${p.delegation} · ${p.symbol}`;$('passage-text').replaceChildren(node('p',`${p.speaker}. ${p.capacity}${p.representedGroup?`: speaking on behalf of ${p.representedGroup}`:''}.`,'hint'));for(const s of p.segments)$('passage-text').append(node('h3',`PDF page ${s.page}`),node('p',s.text));}catch(e){showError(e.message);}
}
$('question-form').addEventListener('submit',event=>{event.preventDefault();if(!$('question-form').reportValidity())return;const pending=request($('question').value.trim());$('question').value='';send(pending);});
$('question').addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.shiftKey){event.preventDefault();$('question-form').requestSubmit();}});
function attachExamples() { document.querySelectorAll('[data-question]').forEach(b=>b.addEventListener('click',()=>{const pending=request(b.dataset.question);send(pending,null);})); }
attachExamples();
$('new-conversation').addEventListener('click',()=>{if(busy)return;lastContext=null;sessionScope=null;snapshots.length=0;$('messages').replaceChildren(emptyTemplate.cloneNode(true));attachExamples();$('question').value='';$('error').hidden=true;applyScope({delegation:null,year:null,session:null,groupsOnly:false,includeProcedural:true});$('request-status').textContent='Conversation cleared. No conversation history is stored on the server.';$('question').focus();});
$('year').addEventListener('change',()=>{sessionScope=null;$('session-scope').hidden=true;});
$('view-coverage').addEventListener('click',openCoverage);$('browse-records').addEventListener('click',openCoverage);
$('close-dialog').addEventListener('click',()=>$('passage').close());
loadCorpus();
