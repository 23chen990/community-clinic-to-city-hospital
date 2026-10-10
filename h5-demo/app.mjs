import { ClinicGame, DispatchPlan } from './simulation.mjs';

const game = new ClinicGame(11);
const $ = (id) => document.getElementById(id);
const serviceStage = {
  reception: ['waitingReception', 'inReception'], triage: ['waitingTriage', 'inTriage'],
  consult: ['waitingConsult', 'inConsult'], sampling: ['waitingSampling', 'inSampling'],
  analysis: ['waitingAnalysis', 'inAnalysis'], followUp: ['waitingFollowUp', 'inFollowUp'],
  treatment: ['waitingTreatment', 'inTreatment'], pharmacy: ['waitingPharmacy', 'inPharmacy'],
};
let lastFrame = 0;
let carry = 0;
let selectedPlan = DispatchPlan.TEMPORARY_SUPPORT;
let panelOpen = false;
let priorPaused = false;
let lastEventCount = 0;

const shortId = (id) => id.replace('患者', '患');
const stageLabels = { waitingSampling: '等待采样', inSampling: '采样中', waitingAnalysis: '等待分析', inAnalysis: '分析中', resultPending: '待发布结果' };
const running = (service) => {
  const active = game.active[service];
  if (!active) return '空闲';
  const title = { reception: '登记中', triage: '分诊中', consult: '诊疗中', sampling: '采样中', analysis: '分析中', followUp: '复诊中', treatment: '治疗中', pharmacy: '配药中' }[service];
  return `${title} · ${shortId(active.patient.id)}`;
};

function dotsFor(service, activeStage) {
  const queued = game.queues[service].map((patient) => patient.id);
  const active = game.active[service]?.patient.id;
  return [...(active ? [active] : []), ...queued.slice(0, 4)].map((id) => {
    const patient = game.patients.find((entry) => entry.id === id);
    const tone = patient?.risk >= 6000 ? 'high' : patient?.risk < 3500 ? 'low' : '';
    return `<span class="person-dot ${tone}" title="${id}">${shortId(id).slice(-1)}</span>`;
  }).join('');
}

function renderMap() {
  const groups = {
    waitingPeople: ['reception'], triagePeople: ['triage'], gpPeople: ['consult', 'followUp'],
    carePeople: ['treatment'], pharmacyPeople: ['pharmacy'],
  };
  for (const [id, services] of Object.entries(groups)) $(id).innerHTML = services.map((service) => dotsFor(service)).join('');
  $('waitingCount').textContent = `等待 ${game.queues.reception.length} 人`;
  $('triageCount').textContent = running('triage') === '空闲' ? running('reception') : running('triage');
  $('gpCount').textContent = game.active.consult ? running('consult') : running('followUp');
  $('careCount').textContent = running('treatment');
  $('pharmacyCount').textContent = running('pharmacy');
  const backlog = game.snapshot.lab.resultBacklog;
  $('backlog').textContent = backlog;
  $('fabBacklog').textContent = backlog;
  $('fabBacklog').classList.toggle('visible', backlog > 0);
  $('labAlert').classList.toggle('visible', backlog > 0);
  $('arrived').textContent = game.snapshot.arrived;
  $('discharged').textContent = game.snapshot.discharged;
  $('clock').textContent = game.snapshot.clock;
  $('goalCount').textContent = game.snapshot.arrived;
  $('goalCheck').classList.toggle('done', game.snapshot.arrived === 5);
  $('goalCheck').textContent = game.snapshot.arrived === 5 ? '✓' : '○';
  const doneLabs = game.events.filter((event) => event.type === 'result-published').length;
  $('labDone').textContent = doneLabs;
  $('labCheck').classList.toggle('done', doneLabs >= 4);
  $('labCheck').textContent = doneLabs >= 4 ? '✓' : '○';
  $('doctorState').textContent = running('consult') !== '空闲' ? '接诊中' : running('followUp') !== '空闲' ? '复诊中' : '待命';
  $('nurseState').textContent = running('triage') !== '空闲' ? '分诊中' : running('sampling') !== '空闲' ? '采样中' : '待命';
  $('statusText').textContent = game.snapshot.lab.supportActive ? '临时检验支援中' : backlog ? '化验结果等待发布' : '患者正在按流程就诊';
}

function renderLab() {
  const state = game.snapshot.lab;
  $('samplingQueue').innerHTML = `${state.samplingQueue} <small>人</small>`;
  $('analysisQueue').innerHTML = `${state.analysisQueue} <small>份</small>`;
  $('resultQueue').innerHTML = `${state.resultBacklog} <small>份</small>`;
  $('bottleneck').textContent = state.bottleneck;
  $('bottleneckCount').textContent = state.resultBacklog || state.analysisQueue || state.samplingQueue;
  $('samplingBar').style.width = `${Math.min(100, state.samplingQueue * 26)}%`;
  $('analysisBar').style.width = `${Math.min(100, state.analysisQueue * 26)}%`;
  $('resultBar').style.width = `${Math.min(100, state.resultBacklog * 25)}%`;
  $('fatigue').textContent = `${state.technicianFatigue}%`;
  $('fatigueBar').style.width = `${state.technicianFatigue}%`;
  const waiting = game.patients.filter((patient) => ['waitingSampling', 'inSampling', 'waitingAnalysis', 'inAnalysis', 'resultPending'].includes(patient.stage));
  $('patientQueueCount').textContent = `${waiting.length} 人`;
  $('labPatients').innerHTML = waiting.length ? waiting.map((patient) => `<div class="patient-row"><span class="patient-avatar">${shortId(patient.id).slice(-1)}</span><span class="patient-info"><b>${patient.id}</b><small>${stageLabels[patient.stage] || patient.stage}</small></span><span class="risk-pill ${patient.risk >= 6000 ? 'high' : ''}">${patient.risk >= 6500 ? '高' : patient.risk >= 3500 ? '中' : '低'}风险</span><span class="wait-pill">${Math.floor(patient.waitTicks / 10)}秒</span></div>`).join('') : '<div class="empty-queue">暂时没有排队患者</div>';
  $('executeBtn').disabled = !state.resultBacklog || Boolean(state.dispatchPlan);
  if (state.supportActive) $('executeBtn').textContent = `临时支援中 · ${Math.ceil((game.supportUntil - game.tick) / 10)} 秒`;
  else $('executeBtn').innerHTML = `执行${selectedPlan === DispatchPlan.TEMPORARY_SUPPORT ? '临时支援' : selectedPlan === DispatchPlan.RISK_PRIORITY ? '风险优先' : '外送样本'} <span>→</span>`;
  $('dispatchImpact').innerHTML = selectedPlan === DispatchPlan.TEMPORARY_SUPPORT
    ? '<span class="impact-mark">✦</span><span><b>预计 60 秒内发布 3 份结果</b><small>检验人员疲劳 +15%</small></span>'
    : selectedPlan === DispatchPlan.RISK_PRIORITY
      ? '<span class="impact-mark">✦</span><span><b>优先发布风险较高的结果</b><small>队列等待仍会继续累积</small></span>'
      : '<span class="impact-mark">✦</span><span><b>普通样本外送，结果稍后返回</b><small>外送结果将在 30 秒后返回门诊</small></span>';
}

function renderEvents() {
  const fresh = game.events.slice(-5).reverse();
  if (fresh.length === lastEventCount) return;
  lastEventCount = fresh.length;
  $('eventList').innerHTML = fresh.map((event) => `<p>${event.message}${event.patient && event.patient !== '医院' ? ` · ${event.patient}` : ''}</p>`).join('');
}

function render() {
  renderMap(); renderLab(); renderEvents();
  const complete = game.snapshot.shiftComplete;
  $('settleBtn').disabled = !complete;
  $('settleBtn').textContent = complete ? '查看班次结算　→' : '班次进行中　→';
  $('settleBtn').innerHTML = complete ? '查看班次结算 <span>→</span>' : '班次进行中 <span>→</span>';
  $('pauseBtn').textContent = game.paused ? '▶' : 'Ⅱ';
  $('pauseBtn').setAttribute('aria-label', game.paused ? '继续' : '暂停');
}

function frame(time) {
  if (lastFrame) carry += Math.min(120, time - lastFrame) * 10 * game.speed;
  lastFrame = time;
  const steps = Math.floor(carry);
  if (steps) { game.advanceTicks(steps); carry -= steps; render(); }
  requestAnimationFrame(frame);
}

function openPanel() {
  if (panelOpen) return;
  panelOpen = true;
  priorPaused = game.paused;
  game.submit({ type: 'pause' });
  $('labPanel').classList.add('open'); $('scrim').classList.add('show');
  render();
}
function closePanel() {
  if (!panelOpen) return;
  panelOpen = false;
  $('labPanel').classList.remove('open'); $('scrim').classList.remove('show');
  if (!priorPaused) game.submit({ type: 'resume' });
  render();
}

$('openLab').addEventListener('click', openPanel);
$('labRoom').addEventListener('click', openPanel);
$('closeLab').addEventListener('click', closePanel);
$('scrim').addEventListener('click', closePanel);
$('pauseBtn').addEventListener('click', () => { game.submit({ type: game.paused ? 'resume' : 'pause' }); render(); });
document.querySelectorAll('.speed-btn').forEach((button) => button.addEventListener('click', () => {
  game.submit({ type: 'speed', speed: Number(button.dataset.speed) });
  document.querySelectorAll('.speed-btn').forEach((item) => item.classList.toggle('active', item === button));
}));
$('goalToggle').addEventListener('click', () => {
  $('goalBody').classList.toggle('collapsed');
  $('goalChevron').textContent = $('goalBody').classList.contains('collapsed') ? '›' : '⌄';
});
document.querySelectorAll('.dispatch-option').forEach((button) => button.addEventListener('click', () => {
  selectedPlan = button.dataset.plan;
  document.querySelectorAll('.dispatch-option').forEach((item) => item.classList.toggle('selected', item === button));
  renderLab();
}));
$('executeBtn').addEventListener('click', () => {
  const success = game.dispatch(selectedPlan);
  if (success) closePanel();
  render();
});
$('settleBtn').addEventListener('click', () => {
  if (!game.snapshot.shiftComplete) return;
  game.submit({ type: 'pause' });
  $('settledLabs').textContent = game.events.filter((event) => event.type === 'result-published').length;
  $('revenue').textContent = game.snapshot.revenue;
  $('settlement').classList.add('show');
});
$('settlementClose').addEventListener('click', () => $('settlement').classList.remove('show'));
document.addEventListener('keydown', (event) => { if (event.key === 'Escape') closePanel(); });

render();
requestAnimationFrame(frame);
