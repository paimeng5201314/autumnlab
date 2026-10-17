'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const chemistry = require('../samples/chemleap/chemistry.js');
const model = require('../samples/chemleap/model.js');

function rng(seed) {
  return function () {
    seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
    return seed / 4294967296;
  };
}
function ascii(text) { return text.replace(/[₀₁₂₃₄₅₆₇₈₉]/g, digit => String('₀₁₂₃₄₅₆₇₈₉'.indexOf(digit))); }
function atoms(formula) {
  const tokens = formula.match(/[A-Z][a-z]?|\d+|[()]/g) || [];
  assert.equal(tokens.join(''), formula, 'Formula contains an unparsed token: ' + formula);
  const stack = [{}];
  for (let index = 0; index < tokens.length; index++) {
    const token = tokens[index];
    if (token === '(') stack.push({});
    else if (token === ')') {
      assert.ok(stack.length > 1, 'Unbalanced parentheses');
      const group = stack.pop();
      const multiplier = /^\d+$/.test(tokens[index + 1] || '') ? Number(tokens[++index]) : 1;
      for (const [element, count] of Object.entries(group)) stack.at(-1)[element] = (stack.at(-1)[element] || 0) + count * multiplier;
    } else {
      assert.match(token, /^[A-Z][a-z]?$/);
      const multiplier = /^\d+$/.test(tokens[index + 1] || '') ? Number(tokens[++index]) : 1;
      stack.at(-1)[token] = (stack.at(-1)[token] || 0) + multiplier;
    }
  }
  assert.equal(stack.length, 1);
  return stack[0];
}
function side(text) {
  const total = {}, formulas = [];
  for (const term of ascii(text).replace(/\s/g, '').split('+')) {
    const match = /^(\d+)?([A-Z].*)$/.exec(term);
    assert.ok(match, 'Cannot parse equation term: ' + term);
    const coefficient = Number(match[1] || 1);
    assert.ok(coefficient > 0);
    formulas.push(match[2]);
    for (const [element, count] of Object.entries(atoms(match[2]))) total[element] = (total[element] || 0) + coefficient * count;
  }
  return { formulas: formulas.sort(), atoms: Object.fromEntries(Object.entries(total).sort()) };
}
function safeIndex(state) { return state.platforms.findIndex(platform => chemistry.findReaction(state.current, platform.substance)); }
function badIndex(state) { return state.platforms.findIndex(platform => !chemistry.findReaction(state.current, platform.substance)); }
function succeed(state) {
  assert.ok(model.beginJump(state, safeIndex(state)));
  const outcome = model.land(state);
  assert.equal(outcome.success, true);
  return outcome;
}
function saveWith(change) {
  const value = model.serialize(model.create(7));
  change(value);
  return value;
}

test('chemistry browser globals and CommonJS expose the same dependency-free API', () => {
  const context = vm.createContext({});
  for (const file of ['chemistry.js', 'model.js']) vm.runInContext(fs.readFileSync(path.join(__dirname, '../samples/chemleap', file), 'utf8'), context);
  assert.equal(typeof context.ChemLeapChemistry.findReaction, 'function');
  assert.equal(context.ChemLeapModel.create(7).current, 'H2');
  assert.equal(context.ChemLeapChemistry.reactions.length, chemistry.reactions.length);
});

for (const reaction of chemistry.reactions) {
  test('balanced equation and real next product: ' + reaction.equation, () => {
    const parts = reaction.equation.split('→');
    assert.equal(parts.length, 2);
    const left = side(parts[0]), right = side(parts[1]);
    assert.deepEqual(left.atoms, right.atoms);
    assert.deepEqual(left.formulas, reaction.reactants.slice().sort());
    assert.deepEqual(right.formulas, reaction.products.slice().sort());
    assert.ok(reaction.products.includes(reaction.next));
    assert.ok(model.playableSpecies.includes(reaction.next));
    assert.equal(reaction.points, 10);
    assert.match(reaction.condition, /[\u4e00-\u9fff]/);
    assert.ok(Object.isFrozen(reaction) && Object.isFrozen(reaction.reactants));
  });
}

test('unordered reaction pairs are unique and symmetric', () => {
  const keys = new Set();
  for (const reaction of chemistry.reactions) {
    const [first, second] = reaction.reactants;
    const key = [first, second].sort().join('|');
    assert.equal(keys.has(key), false);
    keys.add(key);
    assert.equal(chemistry.findReaction(first, second), reaction);
    assert.equal(chemistry.findReaction(second, first), reaction);
  }
  assert.equal(chemistry.findReaction('H2', 'He'), null);
  assert.equal(chemistry.findReaction(null, 'O2'), null);
  assert.equal(chemistry.findReaction('__proto__', 'O2'), null);
  assert.deepEqual(chemistry.validReagents('unknown'), []);
});

test('every reachable current has two different continuations and no salt dead end', () => {
  const reached = new Set(['H2']), queue = ['H2'];
  while (queue.length) {
    const current = queue.shift();
    const reagents = chemistry.validReagents(current);
    assert.ok(new Set(reagents).size >= 2, 'Dead end: ' + current);
    assert.equal(reagents.length, new Set(reagents).size);
    for (const reagent of reagents) {
      const reaction = chemistry.findReaction(current, reagent);
      assert.ok(reaction && reaction.products.includes(reaction.next));
      if (!reached.has(reaction.next)) { reached.add(reaction.next); queue.push(reaction.next); }
    }
  }
  assert.deepEqual([...reached].sort(), [...model.playableSpecies].sort());
  assert.equal(chemistry.findReaction('HCl', 'NaOH').next, 'H2O');
  assert.equal(chemistry.findReaction('NaOH', 'CO2').next, 'Na2CO3');
});

test('species IDs are ASCII, display formulae agree, and metadata is frozen', () => {
  for (const [id, species] of Object.entries(chemistry.species)) {
    assert.match(id, /^[A-Za-z0-9()]+$/);
    assert.equal(ascii(species.formula), id);
    assert.match(species.name, /[\u4e00-\u9fff]/);
    assert.match(species.color, /^#[0-9a-f]{6}$/i);
    assert.ok(Object.isFrozen(species));
    atoms(id);
  }
});

test('48,000 seeded boards have a solution, unique substances and monotonic difficulty', () => {
  const scores = [0, 40, 50, 100, 200, 1000];
  for (const current of model.playableSpecies) {
    for (let seed = 0; seed < 1000; seed++) {
      let previousBadRatio = -1;
      for (const score of scores) {
        const platforms = model.generatePlatforms(current, score, rng(seed));
        const safe = platforms.filter(platform => chemistry.findReaction(current, platform.substance));
        assert.equal(platforms.length, score < 50 ? 3 : score < 100 ? 4 : score < 200 ? 5 : 6);
        assert.equal(safe.length, score < 50 ? 2 : 1);
        assert.equal(new Set(platforms.map(platform => platform.substance)).size, platforms.length);
        assert.equal(new Set(platforms.map(platform => platform.id)).size, platforms.length);
        const badRatio = 1 - safe.length / platforms.length;
        assert.ok(badRatio >= previousBadRatio);
        previousBadRatio = badRatio;
        for (let index = 0; index < platforms.length; index++) {
          const platform = platforms[index];
          assert.ok(Number.isFinite(platform.x) && Number.isFinite(platform.z));
          assert.ok(platform.z <= -2.6 && platform.z > -5.2);
          if (index) assert.ok(platform.x > platforms[index - 1].x);
        }
      }
    }
  }
});

test('constant random sources terminate with fair boards; invalid random outputs reject', () => {
  for (const current of model.playableSpecies) {
    for (const value of [0, 0.5, 0.9999999999]) {
      const board = model.generatePlatforms(current, 1000, () => value);
      assert.equal(board.length, 6);
      assert.equal(board.filter(platform => chemistry.findReaction(current, platform.substance)).length, 1);
    }
  }
  for (const value of [-1, 1, Infinity, NaN]) assert.throws(() => model.generatePlatforms('H2', 0, () => value), /CHEMLEAP_RANDOM_INVALID/);
  assert.throws(() => model.generatePlatforms('NaCl', 0, rng(1)), /CHEMLEAP_CURRENT_INVALID/);
});

test('seeded creation is deterministic, including zero and uint32 maximum', () => {
  for (const seed of [0, 1, 7, 0xffffffff]) assert.deepEqual(model.create(seed), model.create(seed));
  assert.notDeepEqual(model.create(1).platforms, model.create(2).platforms);
  for (const seed of [-1, 0x100000000, 0.5, '7', NaN, Infinity, null]) assert.throws(() => model.create(seed), /CHEMLEAP_SEED_INVALID/);
});

test('jumping changes phase only; landing alone grants points and the genuine next product', () => {
  const state = model.create(17), index = safeIndex(state);
  const reaction = chemistry.findReaction(state.current, state.platforms[index].substance);
  const jump = model.beginJump(state, index, 0.7);
  assert.equal(state.phase, 'jumping');
  assert.equal(state.score, 0);
  assert.equal(state.current, 'H2');
  assert.equal(state.round, 1);
  assert.deepEqual(jump.from, { x: 0, z: 2 });
  assert.deepEqual(jump.to, { x: jump.target.x, z: jump.target.z });
  assert.equal(jump.duration, 700);
  const result = model.land(state);
  assert.equal(result.success, true);
  assert.equal(result.reaction, reaction);
  assert.equal(state.current, reaction.next);
  assert.equal(state.score, 10);
  assert.equal(state.best, 10);
  assert.equal(state.round, 2);
  assert.equal(state.phase, 'ready');
  assert.ok(state.platforms.every(platform => platform.id.startsWith('r2-')));
});

test('wrong target ends the round without adding score, replacing chemistry or losing best', () => {
  const state = model.create(11);
  succeed(state);
  const before = model.serialize(state);
  model.beginJump(state, badIndex(state));
  const outcome = model.land(state);
  assert.equal(outcome.success, false);
  assert.equal(outcome.reason, 'CHEMLEAP_NO_REACTION');
  assert.equal(outcome.points, 0);
  assert.equal(state.phase, 'over');
  assert.equal(state.score, before.score);
  assert.equal(state.current, before.current);
  assert.equal(state.round, before.round);
  assert.equal(state.best, before.best);
  assert.deepEqual(state.history, before.history);
  const restarted = model.restart(state, 12);
  assert.equal(restarted.score, 0);
  assert.equal(restarted.best, 10);
  assert.equal(restarted.current, 'H2');
});

test('a jump cannot be submitted or settled twice and UI target mutation cannot retarget it', () => {
  const state = model.create(8), index = safeIndex(state);
  const jump = model.beginJump(state, index);
  assert.equal(model.beginJump(state, badIndex(state)), null);
  assert.equal(model.select(state, badIndex(state)), false);
  state.platforms[index].substance = 'He';
  assert.throws(() => { jump.target.substance = 'He'; }, TypeError);
  assert.equal(model.land(state).success, true);
  const before = model.serialize(state);
  assert.deepEqual(model.land(state), { success: false, reason: 'CHEMLEAP_NOT_JUMPING', points: 0 });
  assert.deepEqual(model.serialize(state), before);
});

test('invalid selection or jump power does not mutate state', () => {
  const state = model.create(4), before = model.serialize(state);
  for (const index of [-1, state.platforms.length, 0.5, NaN, '0']) {
    assert.equal(model.select(state, index), false);
    assert.equal(model.beginJump(state, index), null);
  }
  for (const power of [-0.01, 1.01, NaN, Infinity, '0.5']) assert.equal(model.beginJump(state, 0, power), null);
  assert.deepEqual(model.serialize(state), before);
  assert.equal(model.select(state, 1), true);
  assert.equal(model.beginJump(state).target.id, state.platforms[1].id);
});

test('five hundred consecutive successful jumps remain playable and bound history size', () => {
  const state = model.create(0);
  for (let round = 1; round <= 500; round++) {
    succeed(state);
    assert.equal(state.score, round * 10);
    assert.equal(state.best, round * 10);
    assert.equal(state.round, round + 1);
    assert.ok(safeIndex(state) >= 0);
    assert.equal(state.history.length, Math.min(32, round));
    if (round % 25 === 0) assert.deepEqual(model.restore(model.serialize(state)), state);
  }
});

test('save round trip is detached, supports JSON text, and preserves deterministic continuation', () => {
  const state = model.create(21);
  for (let index = 0; index < 9; index++) succeed(state);
  model.select(state, 2);
  const saved = model.serialize(state);
  assert.notEqual(saved.history, state.history);
  const restored = model.restore(JSON.stringify(saved));
  assert.deepEqual(restored, state);
  succeed(restored); succeed(state);
  assert.deepEqual(restored, state);
});

test('saving mid-jump restores the uncommitted board and does not award a premature score', () => {
  const state = model.create(91);
  model.beginJump(state, safeIndex(state));
  const restored = model.restore(model.serialize(state));
  assert.equal(state.phase, 'jumping');
  assert.equal(restored.phase, 'ready');
  assert.equal(restored.score, 0);
  assert.equal(restored.current, 'H2');
  assert.equal(model.land(restored).reason, 'CHEMLEAP_NOT_JUMPING');
  assert.equal(model.land(state).success, true);
});

test('a tampered unsolvable saved board is discarded and reconstructed from its seed', () => {
  const original = model.create(77), saved = model.serialize(original);
  saved.platforms = [{ id: '__proto__', substance: 'He', x: Infinity, z: 'bad' }];
  const restored = model.restore(saved);
  assert.deepEqual(restored.platforms, original.platforms);
  assert.ok(safeIndex(restored) >= 0);
  saved.platforms = [];
  assert.deepEqual(model.restore(saved).platforms, original.platforms);
});

const malformedSaves = [
  ['unknown current', value => { value.current = 'NaCl'; }],
  ['impossible initial current', value => { value.current = 'CO2'; }],
  ['negative score', value => { value.score = -10; }],
  ['fractional score', value => { value.score = 0.5; }],
  ['unearned score', value => { value.score = 10; value.best = 10; value.round = 2; }],
  ['best below score', value => { value.best = -10; }],
  ['wrong round', value => { value.round = 2; }],
  ['selected outside board', value => { value.selected = 100; }],
  ['in-flight phase', value => { value.phase = 'jumping'; }],
  ['unknown phase', value => { value.phase = 'win'; }],
  ['changed RNG tail', value => { value.seed = (value.seed + 1) >>> 0; }],
  ['negative seed', value => { value.boardSeed = -1; }],
  ['seed outside uint', value => { value.seed = 0x100000000; }],
  ['unexpected property', value => { value.admin = true; }],
  ['missing history', value => { delete value.history; }],
  ['nonarray history', value => { value.history = {}; }]
];
for (const [name, change] of malformedSaves) test('malformed save rejects: ' + name, () => {
  assert.throws(() => model.restore(saveWith(change)), /CHEMLEAP_SAVE_INVALID/);
});

test('future save versions reject, and malformed, oversized or nonobject text does not load', () => {
  assert.throws(() => model.restore(saveWith(value => { value.formatVersion = 2; })), /CHEMLEAP_SAVE_VERSION_UNSUPPORTED/);
  for (const value of [null, [], true, 1, '{', 'null', '[]', ' '.repeat(32769)]) assert.throws(() => model.restore(value), /CHEMLEAP_SAVE_INVALID/);
  assert.throws(() => model.restore(Object.create({ formatVersion: 1 })), /CHEMLEAP_SAVE_INVALID/);
});

test('history validation rejects fabricated chemistry, discontinuous chains and altered score rewards', () => {
  const state = model.create(19);
  for (let index = 0; index < 4; index++) succeed(state);
  for (const edit of [
    value => { value.history[0].reagent = 'He'; },
    value => { value.history[1].from = 'NaCl'; },
    value => { value.history[1].points = 1000; },
    value => { value.history[0].round = 99; },
    value => { value.history[0].equation = 'H₂ → NaCl'; },
    value => { value.history[0].next = 'NaCl'; },
    value => { value.history[0].extra = true; },
    value => { value.current = value.current === 'H2' ? 'CO2' : 'H2'; }
  ]) {
    const saved = model.serialize(state); edit(saved);
    assert.throws(() => model.restore(saved), /CHEMLEAP_SAVE_INVALID/);
  }
});

test('charge triangle is finite, periodic, and clamps negative or invalid elapsed time', () => {
  assert.equal(model.chargeAt(0), 0);
  assert.equal(model.chargeAt(600), 0.5);
  assert.equal(model.chargeAt(1200), 1);
  assert.equal(model.chargeAt(1800), 0.5);
  assert.equal(model.chargeAt(2400), 0);
  for (const ms of [-1, NaN, Infinity, -Infinity, '600']) assert.equal(model.chargeAt(ms), 0);
  for (let ms = 0; ms < 100000; ms += 13) assert.ok(model.chargeAt(ms) >= 0 && model.chargeAt(ms) <= 1);
});

test('every platform can be selected by charge and boundary inputs stay in range', () => {
  for (const score of [0, 50, 100, 200]) {
    const state = { platforms: model.generatePlatforms('H2', score, rng(5)) };
    const count = state.platforms.length;
    assert.equal(model.targetForCharge(state, 0), 0);
    assert.equal(model.targetForCharge(state, 1), count - 1);
    assert.equal(model.targetForCharge(state, Infinity), count - 1);
    assert.equal(model.targetForCharge(state, -Infinity), 0);
    assert.equal(model.targetForCharge(state, NaN), 0);
    for (let index = 0; index < count; index++) assert.equal(model.targetForCharge(state, (index + 0.5) / count), index);
  }
  assert.equal(model.targetForCharge({ platforms: [] }, 0.5), -1);
});
