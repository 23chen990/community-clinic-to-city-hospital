export const DispatchPlan = Object.freeze({
  RISK_PRIORITY: 'risk-priority',
  TEMPORARY_SUPPORT: 'temporary-support',
  OUTSOURCE: 'outsource',
});

const SERVICE_ORDER = [
  ['reception', '接待'],
  ['triage', '分诊'],
  ['consult', '全科初诊'],
  ['sampling', '采样'],
  ['analysis', '分析'],
  ['followUp', '结果复诊'],
  ['treatment', '治疗'],
  ['pharmacy', '药房'],
];

const CASES = [
  { id: 'URTI', name: '上呼吸道样症状', risk: 2800, lab: true, confidence: 4700 },
  { id: 'FLU', name: '流感样疾病', risk: 4600, lab: true, confidence: 4300 },
  { id: 'THROAT', name: '咽部感染样症状', risk: 5400, lab: true, confidence: 4400 },
  { id: 'ALLERGY', name: '轻度过敏样反应', risk: 3800, lab: true, confidence: 4900 },
  { id: 'DEHYDRATION', name: '脱水', risk: 2200, lab: false, confidence: 8200 },
];

const label = {
  waitingReception: '等待接待', inReception: '接待登记',
  waitingTriage: '等待分诊', inTriage: '分诊中',
  waitingConsult: '等待全科', inConsult: '初诊中',
  waitingSampling: '等待采样', inSampling: '采样中',
  waitingAnalysis: '等待分析', inAnalysis: '分析中',
  resultPending: '待发布结果', waitingFollowUp: '等待复诊', inFollowUp: '结果复诊',
  waitingTreatment: '等待治疗', inTreatment: '治疗中',
  waitingPharmacy: '等待药房', inPharmacy: '药房处理中',
  completed: '已离院', transferred: '已转院',
};

export class ClinicGame {
  constructor(seed = 7) {
    this.seed = seed >>> 0 || 0x9e3779b9;
    this.tick = 0;
    this.paused = false;
    this.speed = 1;
    this.nextTaskId = 1;
    this.events = [];
    this.ledger = [];
    this.seenLedgerKeys = new Set();
    this.queues = Object.fromEntries(SERVICE_ORDER.map(([key]) => [key, []]));
    this.active = Object.fromEntries(SERVICE_ORDER.map(([key]) => [key, null]));
    this.results = [];
    this.outsourced = [];
    this.dispatchPlan = null;
    this.supportUntil = -1;
    this.nextPublish = 1500;
    this.fatigue = 0;
    this.patients = CASES.map((caseData, index) => ({
      id: `患者${String(index + 1).padStart(3, '0')}`,
      caseId: caseData.id,
      caseName: caseData.name,
      risk: Math.min(9999, caseData.risk + this.random(0, 350)),
      patience: 10000,
      confidence: caseData.confidence,
      needsLab: caseData.lab,
      stage: 'scheduled',
      arrivalTick: index * 12,
      waitTicks: 0,
      taskTicks: 0,
      outsourceReturn: -1,
      revenue: 120 + index * 10,
    }));
  }

  random(min, max) {
    this.seed = (Math.imul(this.seed, 1664525) + 1013904223) >>> 0;
    return min + (this.seed % (max - min));
  }

  get snapshot() {
    const patients = this.patients.map((patient) => ({
      id: patient.id,
      caseId: patient.caseId,
      caseName: patient.caseName,
      stage: patient.stage,
      stageLabel: label[patient.stage] || patient.stage,
      risk: patient.risk,
      riskLabel: patient.risk >= 8500 ? '危急' : patient.risk >= 6500 ? '高' : patient.risk >= 3500 ? '中' : '低',
      patience: patient.patience,
      waitTicks: patient.waitTicks,
    }));
    return {
      tick: this.tick,
      clock: `${String(8 + Math.floor(this.tick / 36000)).padStart(2, '0')}:${String(Math.floor(this.tick / 600) % 60).padStart(2, '0')}`,
      arrived: this.patients.filter((patient) => patient.stage !== 'scheduled').length,
      discharged: this.patients.filter((patient) => patient.stage === 'completed').length,
      transferred: this.patients.filter((patient) => patient.stage === 'transferred').length,
      patients,
      lab: {
        samplingQueue: this.queues.sampling.length + Number(this.active.sampling !== null),
        analysisQueue: this.queues.analysis.length + Number(this.active.analysis !== null),
        resultBacklog: this.results.length,
        outsourced: this.outsourced.length,
        technicianFatigue: this.fatigue,
        supportActive: this.dispatchPlan === DispatchPlan.TEMPORARY_SUPPORT && this.tick < this.supportUntil,
        dispatchPlan: this.dispatchPlan,
        bottleneck: this.results.length ? '结果发布' : this.queues.analysis.length ? '分析' : this.queues.sampling.length ? '采样' : '无',
      },
      shiftComplete: this.patients.every((patient) => ['completed', 'transferred'].includes(patient.stage)),
      revenue: this.ledger.reduce((sum, entry) => sum + entry.amount, 0),
    };
  }

  enqueue(service, patient) {
    if (!this.queues[service].includes(patient)) {
      patient.queuedAt = this.tick;
      this.queues[service].push(patient);
    }
  }

  addEvent(type, patient, message) {
    this.events.push({ tick: this.tick, type, patient, message });
  }

  submit(command) {
    if (command.type === 'pause') this.paused = true;
    if (command.type === 'resume') this.paused = false;
    if (command.type === 'speed') this.speed = command.speed;
  }

  dispatch(plan) {
    if (!Object.values(DispatchPlan).includes(plan) || this.results.length === 0 || this.dispatchPlan) return false;
    this.dispatchPlan = plan;
    if (plan === DispatchPlan.TEMPORARY_SUPPORT) {
      this.supportUntil = this.tick + 600;
      this.nextPublish = this.tick + 1;
      this.fatigue = Math.min(100, this.fatigue + 15);
      this.addEvent('support-start', '医院', '临时支援开始');
    } else if (plan === DispatchPlan.RISK_PRIORITY) {
      this.nextPublish = this.tick + 100;
      this.addEvent('risk-priority', '医院', '风险优先已启用');
    } else {
      const count = Math.max(1, Math.floor(this.results.length / 2));
      const chosen = [...this.results].sort((a, b) => a.risk - b.risk || a.id.localeCompare(b.id)).slice(0, count);
      for (const patient of chosen) {
        this.results.splice(this.results.indexOf(patient), 1);
        patient.stage = 'outsourced';
        patient.outsourceReturn = this.tick + 300;
        this.outsourced.push(patient);
        this.addEvent('outsourced', patient.id, '普通样本已外送');
      }
      if (!this.results.length) this.dispatchPlan = null;
    }
    return true;
  }

  advanceUntil(predicate, maxTicks) {
    let count = 0;
    while (!predicate() && count < maxTicks) {
      this.advanceTick();
      count++;
    }
    return predicate();
  }

  advanceTicks(count) {
    for (let index = 0; index < count; index++) this.advanceTick();
  }

  advanceTick() {
    if (this.paused || this.snapshot.shiftComplete) return;
    this.tick++;
    for (const patient of this.patients.filter((item) => item.stage === 'scheduled' && item.arrivalTick <= this.tick)) {
      patient.stage = 'waitingReception';
      this.enqueue('reception', patient);
      this.addEvent('arrival', patient.id, '患者进入接待与分诊');
    }

    for (const [service, task] of Object.entries(this.active)) {
      if (task && task.endsAt <= this.tick) {
        this.active[service] = null;
        this.finishService(service, task.patient);
      }
    }

    for (const patient of this.outsourced.filter((item) => item.outsourceReturn <= this.tick)) {
      this.outsourced.splice(this.outsourced.indexOf(patient), 1);
      patient.stage = 'waitingFollowUp';
      this.enqueue('followUp', patient);
      this.addEvent('result-returned', patient.id, '外送结果返回门诊');
    }

    for (const patient of this.results) {
      patient.waitTicks++;
      patient.patience = Math.max(0, patient.patience - 1);
      patient.risk = Math.min(10000, patient.risk + 1);
    }
    this.publishResultIfReady();
    this.scheduleServices();
  }

  finishService(service, patient) {
    switch (service) {
      case 'reception': patient.stage = 'waitingTriage'; this.enqueue('triage', patient); break;
      case 'triage': patient.stage = 'waitingConsult'; this.enqueue('consult', patient); this.addEvent('triage', patient.id, '分诊完成'); break;
      case 'consult':
        if (patient.needsLab && patient.confidence < 7000) {
          patient.stage = 'waitingSampling';
          this.enqueue('sampling', patient);
          this.addEvent('lab-ordered', patient.id, '诊断信心不足，申请基础化验');
        } else { patient.stage = 'waitingTreatment'; this.enqueue('treatment', patient); }
        break;
      case 'sampling': patient.stage = 'waitingAnalysis'; this.enqueue('analysis', patient); this.addEvent('sampling', patient.id, '采样完成'); break;
      case 'analysis': patient.stage = 'resultPending'; this.results.push(patient); this.addEvent('result-pending', patient.id, '分析完成，等待结果发布'); break;
      case 'followUp': patient.stage = 'waitingTreatment'; this.enqueue('treatment', patient); this.addEvent('follow-up', patient.id, '结果复诊完成'); break;
      case 'treatment': patient.stage = 'waitingPharmacy'; this.enqueue('pharmacy', patient); this.addEvent('treatment', patient.id, '治疗完成'); break;
      case 'pharmacy':
        patient.stage = 'completed';
        this.addEvent('discharged', patient.id, '患者离院');
        if (!this.seenLedgerKeys.has(`revenue:${patient.id}`)) {
          this.seenLedgerKeys.add(`revenue:${patient.id}`);
          this.ledger.push({ key: `revenue:${patient.id}`, amount: patient.revenue });
        }
        break;
      default: break;
    }
  }

  publishResultIfReady() {
    if (!this.results.length) {
      if (this.dispatchPlan === DispatchPlan.TEMPORARY_SUPPORT && this.tick >= this.supportUntil) {
        this.dispatchPlan = null;
        this.addEvent('support-end', '医院', '临时支援结束');
      } else if (this.dispatchPlan === DispatchPlan.RISK_PRIORITY) this.dispatchPlan = null;
      return;
    }

    if (this.tick >= this.nextPublish) {
      const patient = this.dispatchPlan === DispatchPlan.RISK_PRIORITY
        ? [...this.results].sort((a, b) => b.risk - a.risk || a.id.localeCompare(b.id))[0]
        : this.results[0];
      this.results.splice(this.results.indexOf(patient), 1);
      patient.stage = 'waitingFollowUp';
      this.enqueue('followUp', patient);
      this.addEvent('result-published', patient.id, '结果发布，返回全科复诊');
      this.addEvent('result-returned', patient.id, '结果返回门诊');
      const delay = this.dispatchPlan === DispatchPlan.TEMPORARY_SUPPORT ? 200 : this.dispatchPlan === DispatchPlan.RISK_PRIORITY ? 100 : 1500;
      this.nextPublish = this.tick + delay;
    }

    if (this.dispatchPlan === DispatchPlan.TEMPORARY_SUPPORT && this.tick >= this.supportUntil) {
      this.dispatchPlan = null;
      this.addEvent('support-end', '医院', '临时支援结束');
      this.nextPublish = this.tick + 1500;
    }
  }

  scheduleServices() {
    for (const [service] of SERVICE_ORDER) {
      if (this.active[service] || this.queues[service].length === 0) continue;
      const patient = service === 'followUp' && this.dispatchPlan === DispatchPlan.RISK_PRIORITY
        ? [...this.queues[service]].sort((a, b) => b.risk - a.risk || a.queuedAt - b.queuedAt)[0]
        : this.queues[service][0];
      this.queues[service].splice(this.queues[service].indexOf(patient), 1);
      patient.stage = ({ reception: 'inReception', triage: 'inTriage', consult: 'inConsult', sampling: 'inSampling', analysis: 'inAnalysis', followUp: 'inFollowUp', treatment: 'inTreatment', pharmacy: 'inPharmacy' })[service];
      patient.waitTicks = 0;
      const duration = service === 'treatment' ? 20 : service === 'analysis' ? 30 : service === 'sampling' ? 20 : 15;
      this.active[service] = { id: this.nextTaskId++, patient, endsAt: this.tick + duration };
    }
  }
}
