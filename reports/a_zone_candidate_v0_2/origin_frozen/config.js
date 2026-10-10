// 晴天小医院 v2.1 轻量原型 — 配置数据
// 数据来源：hospital_lite_v21/config/runtime.json (content_sha256 07c447c...)
// 仅在游戏逻辑之外，所有价格/时长/奖励/布局均从此对象读取，不写死在 game.js 中。

window.HOSP_CONFIG = {
  version: "2.1.0-lite-prototype",
  inputs: {
    tick_hz: 10,
    gp_base_seconds: 12,
    coins_per_base_work_second: 1.5,
    reward_round: 5,
    price_round: 50,
    initial_coins: 250,
    opening_grant: 150,
    speed_gain_per_level: 0.15,
    max_level: 5,
    patience_seconds: 120,
    tutorial_grace_seconds: 120,
    max_patients: 40,
    walk_units_per_second: 160,
    registration_seconds: 2,
    registration_slots: 4,
    settle_seconds: 1,
    autosave_sim_seconds: 30,
    admission_gp_wait_budget_seconds: 48
  },

  // A 区 4 科室为本期可玩；B/C 标记为 locked，仅作占位提示，不可投币
  rooms: {
    GP: { id: "GP", area: "A", initial: true,  initial_slots: 1, max_slots: 3, time_ratio: 1.000, base_ticks: 120, unlock_cost: 0,
          upgrade_costs: [300, 550, 1050, 2050], slot_costs: [700, 1400], name: "全科诊室", desc: "初诊分诊，判断后续路线" },
    PH: { id: "PH", area: "A", initial: true,  initial_slots: 1, max_slots: 2, time_ratio: 0.667, base_ticks: 80,  unlock_cost: 0,
          upgrade_costs: [300, 550, 1050, 2050], slot_costs: [700],        name: "药房",   desc: "取药结算后离院" },
    TR: { id: "TR", area: "A", initial: false, initial_slots: 1, max_slots: 3, time_ratio: 1.667, base_ticks: 200, unlock_cost: 400,
          upgrade_costs: [300, 600, 1200, 2350], slot_costs: [800, 1600], name: "处置室", desc: "接待需要处置治疗的患者" },
    LB: { id: "LB", area: "A", initial: false, initial_slots: 1, max_slots: 2, time_ratio: 1.333, base_ticks: 160, unlock_cost: 1400,
          upgrade_costs: [300, 600, 1200, 2350], slot_costs: [1200], name: "检验室", desc: "接待需要检查的患者" },
    // B/C 仅占位，本期不可开放
    IM: { id: "IM", area: "B", initial: false, initial_slots: 1, max_slots: 2, time_ratio: 1.833, base_ticks: 220, unlock_cost: 3100, name: "影像室", desc: "B区后续内容", locked: true },
    OB: { id: "OB", area: "B", initial: false, initial_slots: 2, max_slots: 3, time_ratio: 3.750, base_ticks: 450, unlock_cost: 3750, name: "留观室", desc: "B区后续内容", locked: true },
    RH: { id: "RH", area: "C", initial: false, initial_slots: 1, max_slots: 2, time_ratio: 2.333, base_ticks: 280, unlock_cost: 6900, name: "康复室", desc: "C区后续内容", locked: true }
  },

  // A 区可生成的病例（路线全部在 GP/PH/TR/LB 内），共 9 个
  cases: [
    { id: "C01", group: "base",     route: ["GP", "PH"],    last_multiplier: 1.00, reward: 30 },
    { id: "C02", group: "base",     route: ["GP", "PH"],    last_multiplier: 1.15, reward: 35 },
    { id: "C03", group: "base",     route: ["GP", "PH"],    last_multiplier: 1.30, reward: 35 },
    { id: "C04", group: "treatment",route: ["GP", "TR"],    last_multiplier: 1.00, reward: 50 },
    { id: "C05", group: "treatment",route: ["GP", "TR"],    last_multiplier: 1.15, reward: 55 },
    { id: "C06", group: "treatment",route: ["GP", "TR"],    last_multiplier: 1.30, reward: 60 },
    { id: "C07", group: "lab",      route: ["GP", "LB", "PH"], last_multiplier: 1.00, reward: 55 },
    { id: "C08", group: "lab",      route: ["GP", "LB", "PH"], last_multiplier: 1.20, reward: 60 },
    { id: "C09", group: "lab",      route: ["GP", "LB", "TR"], last_multiplier: 1.10, reward: 75 }
  ],

  // 病例生成权重（A 区阶段，对应 stage 1 weights 子集）
  case_weights: { base: 0.4, treatment: 0.3, lab: 0.3 },

  // 到院节奏：stage 1 normal = 4 人/分钟 → 平均 15 秒一人
  arrival_per_minute: 4,

  // A 区布局坐标（1280×720 设计坐标系）
  layout: {
    area_width: 1280,
    area_height: 720,
    corridor_y: 360,
    nodes: {
      ENTRY: [80, 360],
      RG:    [200, 360],
      GP:    [430, 250],
      PH:    [930, 250],
      TR:    [430, 490],
      LB:    [930, 490],
      EXIT:  [1200, 360]
    },
    room_size: { w: 220, h: 150 }
  },

  // 主线任务（A 区相关子集，自动发奖）
  tasks: [
    { id: "T01", name: "完成第一位患者的服务", condition: { completed: 1 }, reward: 150, main: true },
    { id: "T02", name: "开放处置室",            condition: { room: "TR" },     reward: 100, main: true },
    { id: "T03", name: "累计完成10位患者",      condition: { completed: 10 },  reward: 100, main: true },
    { id: "T04", name: "完成一次提速或扩容",    condition: { investments: 1 },reward: 100, main: true },
    { id: "T05", name: "累计完成10次检验",      condition: { service: { LB: 10 } }, reward: 200, main: true }
  ]
};
