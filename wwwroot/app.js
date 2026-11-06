const $ = id => document.getElementById(id);
let lastRequest;
const node = (tag, text, cls) => { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; if (cls) e.className = cls; return e; };
function link(text, href) { const a = node('a', text, 'source-link'); a.href = href; a.target = '_blank'; a.rel = 'noopener noreferrer'; return a; }
async function readResponse(response) {
  if (!response.ok) { let body; try { body = await response.json(); } catch {} throw new Error(body?.error || `Request failed (${response.status}).`); }
  return response.json();
}
async function loadCorpus() {
  try {
    const c = await readResponse(await fetch('/api/corpus'));
    $('coverage').textContent = c.coverage;
    $('corpus-count').textContent = `${c.documents.length} records · ${c.interventions} interventions · ${c.retrieval.passages} passages`;
    for (const d of c.delegations) { const o = node('option', d); o.value = d; $('delegation').append(o); }
    for (const {document:d, ingestion:s} of c.documents) {
      const row = node('div', undefined, 'document');
      row.append(link(d.symbol, `/api/documents/${d.id}/pdf`), node('span',d.date), node('span',d.notes), node('span',s.status === 'indexed' ? `${s.pages} pages · indexed` : `Failed: ${s.error}`));
      $('documents').append(row);
    }
    $('generate').disabled = !c.generation.enabled;
    $('generate').checked = c.generation.enabled;
    const retrieval = c.retrieval.ready ? `Semantic index ready (${c.retrieval.model}); Hybrid combines it with keyword search.` : (c.retrieval.warning || 'Semantic retrieval is unavailable; keyword search remains available.');
    $('model-status').textContent = `${retrieval} ${c.generation.enabled ? `Optional answers: local ${c.generation.model}.` : 'Answer generation is disabled.'}`;
  } catch (e) { showError(e.message); $('model-status').textContent = 'Corpus could not be loaded.'; }
}
function showError(message) { $('error').textContent = message; $('error').hidden = false; }
function request() { return { question: $('question').value.trim(), delegation: $('delegation').value || null, year: Number($('year').value) || null, groupsOnly: $('groups').checked, includeProcedural: $('procedural').checked, generate: $('generate').checked, searchMode: $('search-mode').value }; }
function renderAnswer(result) {
  $('answer').replaceChildren();
  const ids = new Set(result.evidence.map(e => e.id));
  const pattern = /\[([A-Za-z0-9_.-]+)\]/g; let position = 0;
  for (const match of result.answer.matchAll(pattern)) {
    $('answer').append(document.createTextNode(result.answer.slice(position, match.index)));
    if (ids.has(match[1])) { const a = node('a',match[0],'citation'); a.href = '#' + encodeURIComponent(match[1]); $('answer').append(a); }
    else $('answer').append(document.createTextNode(match[0]));
    position = match.index + match[0].length;
  }
  $('answer').append(document.createTextNode(result.answer.slice(position)));
}
async function ask(event) {
  event?.preventDefault();
  if (!$('question-form').reportValidity()) return;
  const pending = request();
  $('error').hidden = true; $('ask').disabled = true; $('ask').textContent = pending.generate ? 'Reading evidence…' : 'Searching…';
  $('export').disabled = true;
  try {
    const result = await readResponse(await fetch('/api/ask', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(pending) }));
    lastRequest = pending;
    $('results').hidden = false; $('mode').textContent = result.mode;
    renderAnswer(result);
    $('scope').textContent = `Retrieval: ${result.retrieval.mode}. ${result.retrieval.scope} Passages within scope: ${result.retrieval.passagesInScope}. Ranked candidates; not an exhaustive topic assessment.`;
    $('retrieval-warning').hidden = !result.retrieval.warning; $('retrieval-warning').textContent = result.retrieval.warning || '';
    $('warning').hidden = !result.generationWarning; $('warning').textContent = result.generationWarning || '';
    $('result-count').textContent = `Retrieved interventions: ${result.matchingInterventions} · Delegations / organizations: ${result.matchingDelegations} · Passage excerpts shown: ${result.returnedEvidence}${result.evidenceTruncated ? ' (display capped; export includes all retrieved candidates)' : ''}. Keyword terms: ${result.searchTerms.join(', ') || 'browse within filters'}.`;
    $('evidence').replaceChildren();
    for (const e of result.evidence) {
      const row = node('tr'); row.id = e.id;
      const who = node('td'); who.append(node('strong',e.delegation), node('small',e.speaker));
      if (e.representedGroup) who.append(node('div',`On behalf of ${e.representedGroup}`, 'group'));
      const passage = node('td'); passage.append(node('div',e.excerpt));
      const details = node('button','Read passage and context','passage-button'); details.type = 'button'; details.onclick = () => openContext(e.id); passage.append(details);
      const source = node('td'); source.append(node('strong',e.symbol), node('small',`${e.date} · PDF p. ${e.page}`),link('Open saved PDF',e.localPdfUrl),link('Official source',e.officialPdfUrl),node('small',`Evidence ID: ${e.id}`));
      source.append(node('small', `Keyword rank: ${e.keywordRank ?? '—'} · Semantic rank: ${e.semanticRank ?? '—'}${e.semanticSimilarity != null ? ` · Cosine: ${e.semanticSimilarity.toFixed(3)}` : ''}`));
      row.append(who,passage,source); $('evidence').append(row);
    }
    $('export').disabled = result.evidence.length === 0;
  } catch (e) { showError(e.message); }
  finally { $('ask').disabled = false; $('ask').textContent = 'Find evidence ↗'; }
}
async function openContext(id) {
  try {
    const p = await readResponse(await fetch(`/api/passages/${encodeURIComponent(id)}`));
    $('passage-title').textContent = `${p.intervention.delegation} · ${p.intervention.symbol}`; $('passage-text').replaceChildren();
    $('passage-text').append(node('p', `${p.intervention.speaker}. ${p.intervention.capacity}${p.intervention.representedGroup ? `: ${p.intervention.representedGroup}` : ''}.`, 'hint'));
    for (const s of p.context) {
      $('passage-text').append(node('h3',`${s.id === id ? 'Retrieved passage' : 'Adjacent context'} · PDF page ${s.page}`), node('p',s.text));
    }
    const full = node('button','Read full intervention','secondary'); full.type = 'button';
    full.onclick = () => openPassage(p.intervention.id, true); $('passage-text').append(full);
    $('passage').showModal();
  } catch(e) { showError(e.message); }
}
async function openPassage(id, alreadyOpen = false) {
  try {
    const p = await readResponse(await fetch(`/api/interventions/${encodeURIComponent(id)}`));
    $('passage-title').textContent = `${p.delegation} · ${p.symbol}`; $('passage-text').replaceChildren();
    $('passage-text').append(node('p', `${p.speaker}. ${p.capacity}${p.representedGroup ? `: ${p.representedGroup}` : ''}.`,'hint'));
    for (const s of p.segments) { $('passage-text').append(node('h3',`PDF page ${s.page}`),node('p',s.text)); }
    if (!alreadyOpen) $('passage').showModal();
  } catch (e) { showError(e.message); }
}
$('question-form').addEventListener('submit',ask);
document.querySelectorAll('[data-question]').forEach(b => b.addEventListener('click',()=>{ $('question').value=b.dataset.question; ask(); }));
$('close-dialog').addEventListener('click',()=>$('passage').close());
$('export').addEventListener('click',async()=>{
  if (!lastRequest) return;
  try {
    const response = await fetch('/api/export',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(lastRequest)});
    if (!response.ok) throw new Error('Export failed.');
    const url = URL.createObjectURL(await response.blob()); const a = node('a'); a.href=url; a.download='meeting-evidence.csv'; a.click(); setTimeout(()=>URL.revokeObjectURL(url),1000);
  } catch (e) { showError(e.message); }
});
loadCorpus();
