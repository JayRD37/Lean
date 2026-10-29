const $ = (id) => document.getElementById(id);
const escapeHtml = (value) => String(value ?? '—').replace(/[&<>"']/g, (c) => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let latestStatus = null;
let allActivity = [];
let strategies = [];
let latestResearch = null;

const labels = {
  optimization_started: 'Strategy search started',
  strategy_candidate_approved_for_paper: 'Candidate approved for paper',
  paper_process_started: 'Paper process started',
  paper_process_exited: 'Paper process exited',
  paper_process_restart_scheduled: 'Paper restart scheduled',
  promotion_unchanged_keep_current_paper_strategy: 'Current candidate retained',
  optimization_failed_keep_current_paper_strategy: 'Search failed; current strategy retained',
  external_emergency_stop_detected: 'Emergency stop detected',
  external_emergency_stop_honored: 'Emergency stop honored',
  dashboard_emergency_stop_requested: 'Dashboard shutdown requested'
};
const stamp = (value) => value ? new Date(value).toLocaleString([], {month:'short',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit'}) : '—';
const eventCategory = (name) => /stop|shutdown/i.test(name) ? 'stop' : /paper|process/i.test(name) ? 'paper' : 'strategy';
const prettyJson = (obj) => {
  if (!obj || typeof obj !== 'object') return {};
  return obj;
};
const enumLabel = (value, mapping) => typeof value === 'number' || /^\d+$/.test(String(value)) ? (mapping[Number(value)] ?? String(value)) : String(value ?? '—');
const familyLabel = value => enumLabel(value, {0:'Trend following',1:'Mean reversion',2:'Breakout'});
const timeframeLabel = value => enumLabel(value, {0:'Minute',1:'Hour',2:'Daily'});

function renderGenome(genome) {
  const box = $('genome-table');
  if (!genome || typeof genome !== 'object') {
    $('genome-name').textContent = 'No candidate loaded';
    box.innerHTML = '<div class="empty">No candidate metadata is recorded in the active session.</div>';
    return;
  }
  const family = familyLabel(genome.Family ?? genome.family ?? 'Unknown family');
  const tf = timeframeLabel(genome.Timeframe ?? genome.timeframe ?? '—');
  $('genome-name').textContent = `${family} · ${tf}`;
  const fields = Object.entries(genome)
    .filter(([key]) => !['Family','family','Timeframe','timeframe','EntryThresholdTenths','ExitThresholdTenths','EntryThreshold','ExitThreshold'].includes(key))
    .slice(0, 6);
  if (genome.EntryThreshold !== undefined) fields.push(['EntryThreshold', Number(genome.EntryThreshold).toFixed(1)]);
  if (genome.ExitThreshold !== undefined) fields.push(['ExitThreshold', Number(genome.ExitThreshold).toFixed(1)]);
  box.innerHTML = fields.map(([key,value]) => {
    const display = key.toLowerCase().includes('timeframe') ? timeframeLabel(value) : value ?? '—';
    return `<div class="gene"><span>${escapeHtml(key.replace(/([a-z])([A-Z])/g,'$1 $2').replaceAll('_',' '))}</span><b title="${escapeHtml(display)}">${escapeHtml(display)}</b></div>`;
  }).join('') || '<div class="empty">No genome parameters recorded.</div>';
}

function renderEvents(target, events, limit = 8) {
  const subset = events.slice(0, limit);
  target.innerHTML = subset.length ? subset.map(item => {
    const event = item.Event ?? item.event ?? 'unknown';
    const detail = prettyJson(item.Details ?? item.details);
    const text = Object.entries(detail).map(([key,value]) => `${key}: ${typeof value === 'object' ? JSON.stringify(value) : value}`).join(' · ');
    const category = eventCategory(event);
    return `<div class="activity-item"><span class="event-time">${escapeHtml(stamp(item.Utc ?? item.utc))}</span><span class="event-dot ${category}"></span><span class="event-name" title="${escapeHtml(event)}">${escapeHtml(labels[event] || event.replaceAll('_',' '))}</span><span class="event-detail" title="${escapeHtml(text)}">${escapeHtml(text || 'Recorded by local supervisor')}</span></div>`;
  }).join('') : '<div class="empty">No supervisor audit events have been recorded yet.</div>';
}

function renderStrategies(items) {
  const unique = new Map();
  for (const item of items) {
    const details = item.details || {};
    const genome = details.Genome || details.genome || {};
    const key = JSON.stringify(genome);
    if (!unique.has(key)) unique.set(key, item);
  }
  const ranked = [...unique.values()].sort((a,b) => Number((b.details?.Fitness ?? b.details?.fitness) || 0) - Number((a.details?.Fitness ?? a.details?.fitness) || 0));
  const cards = ranked.map(item => {
    const details = item.details || {};
    const genome = details.Genome || details.genome || {};
    const name = `${familyLabel(genome.Family ?? genome.family ?? 'Strategy')} · ${timeframeLabel(genome.Timeframe ?? genome.timeframe ?? 'Unknown timeframe')}`;
    const fields = [
      ['Fitness', details.Fitness ?? details.fitness ?? '—'],
      ['Population', details.Population ?? details.population ?? '—'],
      ['Validation windows', details.ValidationWindowCount ?? details.validationWindowCount ?? '—']
    ];
    const term = `${name} ${JSON.stringify(genome)} ${JSON.stringify(details)}`.toLowerCase();
    return `<article class="vault-card" data-search="${escapeHtml(term)}"><div class="vault-title"><div><h2>${escapeHtml(name)}</h2><p>Promoted ${escapeHtml(stamp(item.Utc ?? item.utc))}</p></div><span class="badge">PAPER GATE</span></div><div class="vault-meta">${fields.map(([label,value])=>`<span>${escapeHtml(label.toUpperCase())}<b>${escapeHtml(value)}</b></span>`).join('')}</div></article>`;
  }).join('');
  $('vault-list').innerHTML = cards || '<div class="panel empty">No promoted strategy records were found. This is an empty state from the supervisor audit log.</div>';
  $('campaign-history').innerHTML = items.length ? items.map(item => {
    const d = item.details || {}; const g = d.Genome || d.genome || {};
    const event = item.Event || item.event || 'strategy_candidate_approved_for_paper';
    return `<div class="activity-item"><span class="event-time">${escapeHtml(stamp(item.Utc ?? item.utc))}</span><span class="event-dot"></span><span class="event-name">${escapeHtml(familyLabel(g.Family ?? g.family ?? 'Candidate'))} · ${escapeHtml(timeframeLabel(g.Timeframe ?? g.timeframe ?? '—'))}</span><span class="event-detail">Fitness ${escapeHtml(d.Fitness ?? d.fitness ?? '—')} · ${escapeHtml(labels[event] || event.replaceAll('_',' '))}</span></div>`;
  }).join('') : '<div class="empty">No strategy promotion history.</div>';
}

function renderCampaign(config) {
  const metrics = [
    ['GENERATIONS', config.generationCount, 'per search run'],
    ['POPULATION SIZE', config.populationSize, 'per genetic population'],
    ['RANDOM BASELINE', config.randomBaselineSize, 'control candidates'],
    ['ELITES / TOURNAMENT', `${config.eliteCount} / ${config.tournamentSize}`, 'elite count / selection size'],
    ['MUTATION RATE', `${(Number(config.mutationRate) * 100).toFixed(0)}%`, 'configured probability'],
    ['VALIDATION WINDOWS', config.validationWindowCount, 'held-out windows'],
    ['MONTE CARLO RUNS', config.monteCarloSimulationCount, 'robustness simulations'],
    ['PARALLEL BACKTESTS', config.maximumConcurrentBacktests, 'maximum concurrent']
  ];
  $('campaign-metrics').innerHTML = metrics.map(([label,value,foot]) => `<article class="metric-card"><div class="metric-label">${escapeHtml(label)}</div><div class="metric-value">${escapeHtml(value)}</div><div class="metric-foot">${escapeHtml(foot)}</div></article>`).join('');
  const windows = [
    ['TRAINING',config.trainingStartDate,config.trainingEndDate],
    ['VALIDATION',config.validationStartDate,config.validationEndDate],
    ['CONFIRMATION',config.confirmationStartDate,config.confirmationEndDate]
  ];
  $('campaign-windows').innerHTML = windows.map(([name,start,end],index) => `<article class="window-card"><div class="window-index">0${index+1}</div><div><div class="panel-kicker">${name}</div><b>${escapeHtml(start)} <span>→</span> ${escapeHtml(end)}</b></div></article>`).join('');
  $('campaign-config-note').textContent = `Objective ${config.objective} · Symbols ${config.symbols} · Timeframes ${config.allowedTimeframes} · Reoptimization every ${config.reoptimizationIntervalHours} hours · Source ${config.source}`;
}

function renderStatus(s) {
  latestStatus = s;
  $('updated').textContent = stamp(s.generatedAtUtc);
  $('connection-dot').className = `pulse-dot ${s.supervisor === 'connected' ? 'ok' : 'warn'}`;
  $('connection-text').textContent = s.supervisor === 'connected' ? 'Supervisor connected' : 'Supervisor not found';
  $('equity').textContent = s.equity;
  $('net-profit').textContent = s.netProfit;
  $('return-value').textContent = `Return ${s.returnValue}`;
  $('holdings').textContent = s.holdings;
  $('order-count').textContent = `${s.orderCount} order${s.orderCount === 1 ? '' : 's'} reported by latest run`;
  $('data-state').textContent = s.dataState;
  $('bar-count').textContent = `${s.equityPointCount} equity chart points`;
  $('lean-status').textContent = `LEAN ${s.leanStatus || '—'}`;
  $('supervisor-state').textContent = s.supervisor;
  $('supervisor-state').className = s.supervisor === 'connected' ? 'ok' : 'warn';
  $('process-state').textContent = s.paperProcessRunning ? `Running · PID ${s.paperProcessId}` : 'Not running';
  $('process-state').className = s.paperProcessRunning ? 'ok' : 'warn';
  $('stream-state').textContent = s.dataState;
  $('stream-state').className = s.equityPointCount > 0 ? 'ok' : 'warn';
  $('kill-state').textContent = s.emergencyStopPresent ? 'Stop file present' : 'Armed';
  $('kill-state').className = s.emergencyStopPresent ? 'warn' : 'ok';
  $('health-indicator').style.color = s.paperProcessRunning && s.supervisor === 'connected' ? 'var(--green)' : 'var(--amber)';
  $('stop-button').disabled = !s.paperProcessRunning || s.emergencyStopPresent;
  $('session-id').textContent = s.paperProcessRunning ? `PID ${s.paperProcessId}` : 'NO ACTIVE PROCESS';
  $('session-banner').className = `status-banner ${s.paperProcessRunning ? 'good' : 'stopped'}`;
  $('session-title').textContent = s.paperProcessRunning ? 'Paper session active' : 'No active paper process';
  $('session-subtitle').textContent = s.emergencyStopPresent ? 'Emergency stop file is present; supervisor shutdown is expected.' : s.paperProcessRunning ? `${s.dataState}. The interface refreshes automatically.` : 'Supervisor audit history remains available below.';
  $('run-folder').textContent = s.latestRunDirectory || 'No run result';
  $('portfolio-count').textContent = s.holdings;
  $('portfolio-equity').textContent = s.equity;
  $('insight-windows').textContent = s.validationWindowCount ?? '—';
  $('insight-fitness').textContent = Number.isFinite(Number(s.fitness)) ? Number(s.fitness).toFixed(4) : '—';
  $('insight-data-state').textContent = s.dataState;
  $('insight-explanation').textContent = s.equityPointCount > 0 ? `The latest LEAN result contains ${s.equityPointCount} strategy equity points.` : 'The latest LEAN result contains no strategy equity points yet. The result file alone does not establish whether market bars have arrived.';
  $('equity-point-tag').textContent = `${s.equityPointCount} POINT${s.equityPointCount === 1 ? '' : 'S'}`;
  $('fitness').textContent = `FITNESS ${Number.isFinite(Number(s.fitness)) ? Number(s.fitness).toFixed(4) : '—'}`;
  $('validation-count').textContent = `${s.validationWindowCount ?? '—'} validation windows`;
  renderGenome(s.genome);
  renderEquity(s.equityPoints || []);
  loadHoldings();
}

function renderEquity(points) {
  const host = $('equity-chart');
  renderLineChart(host, points, 'LEAN strategy equity curve');
}

function renderFitnessHistory(items) {
  const points = items.map(item => {
    const details = item.details || {};
    const score = Number(details.Fitness ?? details.fitness);
    return Number.isFinite(score) ? {x:item.Utc ?? item.utc, y:score} : null;
  }).filter(Boolean).reverse();
  $('fitness-point-tag').textContent = `${points.length} APPROVAL${points.length === 1 ? '' : 'S'}`;
  renderLineChart($('fitness-chart'), points, 'Approved candidate fitness history');
}

function renderLineChart(host, points, name) {
  if (!points.length) {
    host.className = 'chart-empty';
    host.textContent = name === 'LEAN strategy equity curve' ? 'No equity curve has been written to the latest result file.' : 'No approved candidate history has been recorded.';
    return;
  }
  const width = 1000, height = 160, pad = 16;
  const ys = points.map(point => Number(point.y)).filter(Number.isFinite);
  if (!ys.length) { host.className = 'chart-empty'; host.textContent = 'The source contains no usable values.'; return; }
  const low = Math.min(...ys), high = Math.max(...ys), span = Math.max(high - low, Math.abs(high) * .0001, 1);
  const coords = ys.map((y,index) => [pad + index * (width - pad*2) / Math.max(ys.length-1,1), height-pad - (y-low) / span * (height-pad*2)]);
  const line = coords.map(([x,y],i)=>`${i?'L':'M'}${x.toFixed(1)},${y.toFixed(1)}`).join(' ');
  const area = `${line} L${coords.at(-1)[0].toFixed(1)},${height-pad} L${coords[0][0].toFixed(1)},${height-pad} Z`;
  const xLabel = value => { const parsed = Date.parse(value); return Number.isNaN(parsed) ? String(value ?? '') : new Date(parsed).toLocaleString([], {month:'short',day:'2-digit',hour:'2-digit',minute:'2-digit'}); };
  const markers = coords.map(([x,y],index)=>`<circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="3" fill="#a4e46e" stroke="#0b0d12" stroke-width="2"><title>${escapeHtml(xLabel(points[index]?.x))} · ${escapeHtml(ys[index].toFixed(4))}</title></circle>`).join('');
  const fillId = `${name.replace(/[^a-z0-9]/gi,'').toLowerCase()}Fill`;
  host.className = '';
  host.innerHTML = `<svg class="chart-svg" viewBox="0 0 ${width} ${height}" role="img" aria-label="${escapeHtml(name)} with ${ys.length} points" preserveAspectRatio="none"><defs><linearGradient id="${fillId}" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#a4e46e" stop-opacity=".2"/><stop offset="1" stop-color="#a4e46e" stop-opacity="0"/></linearGradient></defs><path class="chart-grid" d="M${pad},${pad}H${width-pad} M${pad},${height/2}H${width-pad} M${pad},${height-pad}H${width-pad}"/><path class="chart-area" style="fill:url(#${fillId})" d="${area}"/><path class="chart-line" d="${line}"/>${markers}<text class="chart-label" x="${pad}" y="${height-2}">${escapeHtml(xLabel(points[0]?.x))}</text><text class="chart-label" x="${width-pad}" y="${height-2}" text-anchor="end">${escapeHtml(xLabel(points.at(-1)?.x))}</text><text class="chart-label" x="${width-pad}" y="13" text-anchor="end">${escapeHtml(Math.max(...ys).toFixed(4))}</text></svg>`;
}

const populationColors = {Growth:'#a4e46e',Consistency:'#82b8f5',RandomBaseline:'#c29aff'};
const populationNames = {Growth:'Growth',Consistency:'Consistency',RandomBaseline:'Random baseline'};
const number = (value, digits=3) => value == null || value === '' || !Number.isFinite(Number(value)) ? '—' : Number(value).toFixed(digits);

function renderResearch(data) {
  latestResearch = data;
  const rows = Array.isArray(data?.generations) ? data.generations : [];
  const landscape = Array.isArray(data?.landscape) ? data.landscape : [];
  const recent = Array.isArray(data?.recentEvaluations) ? data.recentEvaluations : [];
  const populations = [...new Set(rows.map(row=>row.population))];
  $('research-status').textContent = data?.available ? `${data.evaluationCount} EVALUATIONS` : 'NO HISTORY FILE';
  $('research-legend').innerHTML = populations.map(pop=>`<span class="legend-item"><i style="--series:${populationColors[pop] || '#abb2bd'}"></i>${escapeHtml(populationNames[pop] || pop)}</span>`).join('') + (populations.length ? '<span class="legend-hint"><i></i>solid best · dashed median</span>' : '');
  renderEvolution(rows, populations);
  const generations = [...new Set(landscape.map(item=>item.generation))].sort((a,b)=>a-b);
  const select = $('landscape-generation');
  const current = select.value === '' ? Number.NaN : Number(select.value);
  select.innerHTML = generations.map(g=>`<option value="${g}">Generation ${g}</option>`).join('');
  if (generations.length) select.value = generations.includes(current) ? String(current) : String(generations.at(-1));
  select.disabled = generations.length === 0;
  const selected = Number(select.value);
  const points = landscape.filter(item=>item.generation === selected);
  renderLandscape(points);
  $('landscape-count').textContent = `${points.length} GENOMES`;
  $('evaluation-count').textContent = `${recent.length} RECENT`;
  $('evaluation-rows').innerHTML = recent.length ? recent.map(item=>`<tr><td>${escapeHtml(stamp(item.utc))}</td><td>${escapeHtml(item.generation)}</td><td>${escapeHtml(populationNames[item.population] || item.population)}</td><td>${escapeHtml(item.phase)}</td><td>${escapeHtml(Number(item.fitness) <= -1e6 ? 'Rejected' : number(item.fitness,4))}</td><td>${escapeHtml(number(item.sharpeRatio,2))}</td><td>${escapeHtml(number(item.drawdown*100,2))}${item.drawdown == null?'':'%'}</td><td>${escapeHtml(item.tradeCount ?? '—')}</td></tr>`).join('') : '<tr><td colspan="8" class="empty">No evaluation history found for the configured results folder.</td></tr>';
  const leaders = data?.latestGenerationProfiles || [];
  $('leader-cards').innerHTML = leaders.length ? leaders.map(item=>{
    const m=item.metrics||{}; const g=item.genome||{};
    return `<article class="vault-card"><div class="vault-title"><div><h2>${escapeHtml(populationNames[item.population]||item.population)} leader</h2><p>Generation ${escapeHtml(item.generation)} · candidate ${escapeHtml(item.candidateId)}</p></div><span class="badge">FITNESS ${escapeHtml(number(item.fitness,4))}</span></div><div class="vault-meta"><span>SHARPE<b>${escapeHtml(number(m.sharpeRatio,2))}</b></span><span>RETURN<b>${escapeHtml(number(m.annualReturn*100,1))}${m.annualReturn==null?'':'%'}</b></span><span>DRAWDOWN<b>${escapeHtml(number(m.drawdown*100,1))}${m.drawdown==null?'':'%'}</b></span></div><div class="leader-genome">${escapeHtml(familyLabel(g.Family??g.family??'Strategy'))} · Fast ${escapeHtml(g.FastPeriod??'—')} / Slow ${escapeHtml(g.SlowPeriod??'—')} · ${escapeHtml(timeframeLabel(g.Timeframe??'—'))}</div></article>`;
  }).join('') : '<div class="panel empty">No scored training candidates found in the latest generation.</div>';
  const runDate = data?.campaignStartedAtUtc ? stamp(data.campaignStartedAtUtc) : 'all available history';
  $('research-foot').textContent = data?.available ? `Source: ${data.resultSource} · campaign started ${runDate} · ${data.generationCount} generations · ${data.malformedRows || 0} malformed rows skipped` : 'Optimizer history has not been recorded at the configured results path.';
}

function renderEvolution(rows, populations) {
  const host=$('evolution-chart');
  if (!rows.length) {host.className='research-chart chart-empty';host.textContent='No optimizer training evaluations were found for this campaign.';return;}
  const gens=[...new Set(rows.map(r=>r.generation))].sort((a,b)=>a-b);
  const values=rows.flatMap(r=>[r.bestFitness,r.medianFitness]).map(Number).filter(Number.isFinite);
  if (!values.length) {host.className='research-chart chart-empty';host.textContent='Training rows exist, but no finite fitness scores were recorded.';return;}
  const W=1000,H=290,L=66,R=18,T=20,B=42,lo=Math.min(...values),hi=Math.max(...values),pad=Math.max((hi-lo)*.12,.001),min=lo-pad,max=hi+pad;
  const x=g=>L+(gens.indexOf(g)/Math.max(1,gens.length-1))*(W-L-R), y=v=>H-B-(v-min)/(max-min)*(H-T-B);
  let svg=`<svg class="research-svg" viewBox="0 0 ${W} ${H}" role="img" aria-label="Best and median training fitness by generation">`;
  for(let i=0;i<5;i++){const value=min+(max-min)*i/4, yy=y(value);svg+=`<path class="chart-grid" d="M${L},${yy}H${W-R}"/><text class="chart-label" x="${L-9}" y="${yy+4}" text-anchor="end">${escapeHtml(number(value,3))}</text>`;}
  gens.forEach(g=>{const xx=x(g);svg+=`<path class="chart-grid subtle-grid" d="M${xx},${T}V${H-B}"/><text class="chart-label" x="${xx}" y="${H-12}" text-anchor="middle">Gen ${g}</text>`;});
  for(const pop of populations){const color=populationColors[pop]||'#abb2bd';for(const field of ['bestFitness','medianFitness']){const series=gens.map(g=>rows.find(r=>r.generation===g&&r.population===pop)).filter(r=>Number.isFinite(Number(r?.[field])));if(!series.length)continue;const d=series.map((r,i)=>`${i?'L':'M'}${x(r.generation)},${y(r[field])}`).join(' ');svg+=`<path d="${d}" fill="none" stroke="${color}" stroke-width="${field==='bestFitness'?2.4:1.5}" ${field==='medianFitness'?'stroke-dasharray="4 5"':''} vector-effect="non-scaling-stroke"/>`;for(const r of series)svg+=`<circle cx="${x(r.generation)}" cy="${y(r[field])}" r="3.5" fill="${color}" stroke="#0b0d12" stroke-width="1.5"><title>${escapeHtml(populationNames[pop]||pop)} · Gen ${r.generation} · ${field==='bestFitness'?'best':'median'} ${escapeHtml(number(r[field],4))} · n=${r.scoredCandidateCount}</title></circle>`;}}
  svg+=`<text class="chart-label" x="${L}" y="14">FITNESS</text><text class="chart-label" x="${W-R}" y="${H-12}" text-anchor="end">GENERATION</text></svg>`;
  host.className='research-chart';host.innerHTML=svg;
}

function renderLandscape(points) {
  const host=$('landscape-chart');
  const usable=points.filter(p=>Number.isFinite(Number(p.fastPeriod))&&Number.isFinite(Number(p.slowPeriod))&&Number.isFinite(Number(p.fitness)));
  if(!usable.length){host.className='research-chart chart-empty';host.textContent='No fast/slow period genome values were recorded for this generation.';return;}
  const W=1000,H=310,L=58,R=20,T=18,B=42;
  const xs=usable.map(p=>Number(p.fastPeriod)),ys=usable.map(p=>Number(p.slowPeriod));
  const xmin=Math.min(...xs),xmax=Math.max(...xs),ymin=Math.min(...ys),ymax=Math.max(...ys);
  const x=v=>L+((v-xmin)/Math.max(1,xmax-xmin))*(W-L-R), y=v=>H-B-((v-ymin)/Math.max(1,ymax-ymin))*(H-T-B);
  let svg=`<svg class="research-svg" viewBox="0 0 ${W} ${H}" role="img" aria-label="Candidate genome landscape: fast period versus slow period">`;
  for(let i=0;i<5;i++){const xv=xmin+(xmax-xmin)*i/4,yv=ymin+(ymax-ymin)*i/4,xx=x(xv),yy=y(yv);svg+=`<path class="chart-grid" d="M${xx},${T}V${H-B}"/><path class="chart-grid" d="M${L},${yy}H${W-R}"/><text class="chart-label" x="${xx}" y="${H-14}" text-anchor="middle">${escapeHtml(number(xv,0))}</text><text class="chart-label" x="${L-9}" y="${yy+4}" text-anchor="end">${escapeHtml(number(yv,0))}</text>`;}
  for(const p of usable){const color=populationColors[p.population]||'#abb2bd',radius=Math.min(11,4+Math.sqrt(Number(p.candidateCount||1))*1.8),family=familyLabel(p.genome?.Family??'Strategy');svg+=`<circle class="landscape-point" cx="${x(p.fastPeriod)}" cy="${y(p.slowPeriod)}" r="${radius}" fill="${color}" fill-opacity=".72" stroke="${color}" stroke-width="1.3"><title>${escapeHtml(populationNames[p.population]||p.population)} · ${escapeHtml(family)} · Fast ${p.fastPeriod} / Slow ${p.slowPeriod} · Fitness ${escapeHtml(number(p.fitness,4))} · repeated ${p.candidateCount} · candidate ${p.bestCandidateId}</title></circle>`;}
  svg+=`<text class="chart-label" x="${L}" y="14">SLOW PERIOD</text><text class="chart-label" x="${W-R}" y="${H-14}" text-anchor="end">FAST PERIOD</text></svg>`;
  host.className='research-chart';host.innerHTML=svg;
}

async function loadHoldings() {
  try {
    const payload = await fetch('/api/portfolio',{cache:'no-store'}).then(r => r.json());
    const rows = payload.holdings || [];
    $('holdings-table').innerHTML = rows.length ? rows.map(item => `<tr><td>${escapeHtml(item.symbol)}</td><td>${escapeHtml(item.quantity)}</td><td>${escapeHtml(item.mark)}</td><td>${escapeHtml(item.value)}</td></tr>`).join('') : '<tr><td colspan="4" class="empty">No holdings reported by latest run.</td></tr>';
  } catch {
    $('holdings-table').innerHTML = '<tr><td colspan="4" class="empty">Could not read portfolio details from the latest run.</td></tr>';
  }
}

async function refreshAll() {
  try {
    const [status, activity, vault, campaign, research] = await Promise.all([
      fetch('/api/status',{cache:'no-store'}).then(r => { if(!r.ok) throw new Error(`status ${r.status}`); return r.json(); }),
      fetch('/api/activity?limit=100',{cache:'no-store'}).then(r => r.json()),
      fetch('/api/strategies',{cache:'no-store'}).then(r => r.json()),
      fetch('/api/campaign',{cache:'no-store'}).then(r => r.json()),
      fetch('/api/research',{cache:'no-store'}).then(r => { if(!r.ok) throw new Error(`research ${r.status}`); return r.json(); })
    ]);
    allActivity = activity;
    strategies = vault;
    renderStatus(status);
    renderEvents($('recent-activity'), activity, 6);
    renderEvents($('all-activity'), activity, 100);
    renderStrategies(vault);
    renderFitnessHistory(vault);
    renderCampaign(campaign);
    renderResearch(research);
    applyActivityFilter();
    applyVaultFilter();
  } catch (error) {
    $('connection-dot').className = 'pulse-dot warn';
    $('connection-text').textContent = 'Dashboard API unavailable';
    $('session-title').textContent = 'Could not read local engine state';
    $('session-subtitle').textContent = String(error.message || error);
    $('session-banner').className = 'status-banner stopped';
  }
}

function applyActivityFilter() {
  const filter = $('activity-filter').value;
  const filtered = filter === 'all' ? allActivity : allActivity.filter(event => eventCategory(event.Event ?? event.event) === filter);
  renderEvents($('all-activity'), filtered, 100);
}
function applyVaultFilter() {
  const query = $('vault-search').value.trim().toLowerCase();
  const cards = [...document.querySelectorAll('.vault-card')];
  let visible = 0;
  cards.forEach(card => { card.hidden = !!query && !card.dataset.search.includes(query); if (!card.hidden) visible++; });
  $('vault-no-results').hidden = !(query && cards.length && visible === 0);
}
function navigate() {
  const key = location.hash.replace('#','') || 'overview';
  const valid = ['overview','campaign','vault','portfolio','insights','activity'];
  const view = valid.includes(key) ? key : 'overview';
  document.querySelectorAll('.view').forEach(section => section.classList.toggle('active', section.id === `view-${view}`));
  document.querySelectorAll('.nav-item').forEach(link => link.classList.toggle('active', link.dataset.view === view));
  $('view-title').textContent = ({overview:'COMMAND DECK',campaign:'CAMPAIGN',vault:'STRATEGY VAULT',portfolio:'PORTFOLIO',insights:'INSIGHTS',activity:'ACTIVITY'})[view];
}

$('navigation').addEventListener('click', e => { if (e.target.closest('a')) navigate(); });
window.addEventListener('hashchange', navigate);
$('refresh').addEventListener('click', refreshAll);
$('activity-filter').addEventListener('change', applyActivityFilter);
$('vault-search').addEventListener('input', applyVaultFilter);
$('landscape-generation').addEventListener('change', () => { if (latestResearch) renderResearch(latestResearch); });
$('stop-button').addEventListener('click', () => $('stop-dialog').showModal());
$('confirm-stop').addEventListener('click', async (event) => {
  event.preventDefault();
  $('stop-dialog').close();
  try {
    const response = await fetch('/api/emergency-stop', {method:'POST'});
    const payload = await response.json();
    if (!response.ok) throw new Error(payload.error || `Request failed (${response.status})`);
    $('toast').textContent = 'Emergency shutdown requested. Waiting for supervisor…';
    $('toast').classList.add('show');
    setTimeout(()=>$('toast').classList.remove('show'),4200);
    setTimeout(refreshAll,800);
  } catch (error) {
    $('toast').textContent = `Shutdown request failed: ${error.message}`;
    $('toast').classList.add('show');
    setTimeout(()=>$('toast').classList.remove('show'),5000);
  }
});
window.addEventListener('hashchange',navigate);
navigate();
refreshAll();
setInterval(refreshAll, 5000);

