import test from 'node:test';
import assert from 'node:assert/strict';
import { ClinicGame, DispatchPlan } from './simulation.mjs';

test('baseline fills four published lab results before intervention', () => {
  const game = new ClinicGame(11);
  game.advanceUntil(() => game.snapshot.lab.resultBacklog === 4, 500);

  assert.equal(game.snapshot.arrived, 5);
  assert.equal(game.snapshot.lab.resultBacklog, 4);
  assert.equal(game.snapshot.lab.samplingQueue, 0);
  assert.equal(game.snapshot.lab.analysisQueue, 0);
});

test('temporary support lasts sixty simulation seconds and clears three of four results', () => {
  const game = new ClinicGame(11);
  game.advanceUntil(() => game.snapshot.lab.resultBacklog === 4, 500);
  game.dispatch(DispatchPlan.TEMPORARY_SUPPORT);
  game.advanceTicks(599);
  assert.equal(game.snapshot.lab.resultBacklog, 1);
  assert.equal(game.snapshot.lab.supportActive, true);
  assert.equal(game.snapshot.lab.technicianFatigue, 15);

  game.advanceTicks(1);
  assert.equal(game.snapshot.lab.resultBacklog, 1);
  assert.equal(game.snapshot.lab.supportActive, false);
});

test('publishing a result routes the same patient back through follow-up and discharge', () => {
  const game = new ClinicGame(19);
  game.advanceUntil(() => game.snapshot.lab.resultBacklog === 4, 500);
  game.dispatch(DispatchPlan.TEMPORARY_SUPPORT);
  game.advanceTicks(600);
  game.advanceUntil(() => game.snapshot.shiftComplete, 3_000);

  assert.equal(game.snapshot.discharged, 5);
  assert.equal(game.snapshot.lab.resultBacklog, 0);
  assert.ok(game.events.some(event => event.type === 'result-returned'));
  assert.equal(new Set(game.ledger.map(entry => entry.key)).size, game.ledger.length);
});
