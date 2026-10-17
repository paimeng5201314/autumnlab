(function (root, factory) {
  'use strict';
  if (typeof module === 'object' && module.exports) module.exports = factory(require('./chemistry.js'));
  else root.ChemLeapModel = factory(root.ChemLeapChemistry);
})(typeof globalThis !== 'undefined' ? globalThis : this, function (chemistry) {
  'use strict';
  if (!chemistry) throw new Error('CHEMLEAP_CHEMISTRY_REQUIRED');

  const playableSpecies = Object.freeze(['H2', 'H2O', 'HCl', 'NaOH', 'Ca(OH)2', 'CaCO3', 'CO2', 'Na2CO3']);
  const distractors = Object.freeze(['He', 'Ne', 'Ar', 'NaCl', 'Cu', 'Kr']);
  const maximumScore = 1000000000;
  const pending = new WeakMap();
  const ownedFields = ['formatVersion', 'current', 'score', 'best', 'round', 'platforms', 'phase', 'history', 'selected', 'seed', 'boardSeed'];

  function integer(value, min, max) { return Number.isInteger(value) && value >= min && value <= max; }
  function validSeed(value) { return integer(value, 0, 0xffffffff); }
  function rngFor(seed) {
    return { value: seed, next: function () {
      this.value = (Math.imul(this.value, 1664525) + 1013904223) >>> 0;
      return this.value / 4294967296;
    } };
  }
  function shuffled(values, rng) {
    const copy = values.slice();
    for (let index = copy.length - 1; index > 0; index--) {
      const sample = rng();
      if (!Number.isFinite(sample) || sample < 0 || sample >= 1) throw new Error('CHEMLEAP_RANDOM_INVALID');
      const swap = Math.floor(sample * (index + 1));
      const previous = copy[index]; copy[index] = copy[swap]; copy[swap] = previous;
    }
    return copy;
  }
  function generatePlatforms(current, score, rng) {
    if (!playableSpecies.includes(current)) throw new Error('CHEMLEAP_CURRENT_INVALID');
    if (!integer(score, 0, maximumScore) || typeof rng !== 'function') throw new Error('CHEMLEAP_GENERATION_INVALID');
    const count = score < 50 ? 3 : score < 100 ? 4 : score < 200 ? 5 : 6;
    const safeCount = score < 50 ? 2 : 1;
    const safe = shuffled(chemistry.validReagents(current), rng).slice(0, safeCount);
    const bad = shuffled(distractors.filter(function (id) { return !chemistry.findReaction(current, id); }), rng).slice(0, count - safe.length);
    if (safe.length !== safeCount || bad.length + safe.length !== count) throw new Error('CHEMLEAP_NO_SOLUTION');
    return shuffled(safe.concat(bad), rng).map(function (id, index) {
      const sample = rng();
      if (!Number.isFinite(sample) || sample < 0 || sample >= 1) throw new Error('CHEMLEAP_RANDOM_INVALID');
      return { id: 'p' + (index + 1), substance: id, x: (index - (count - 1) / 2) * 2.3, z: -2.6 - sample * 2.6 };
    });
  }
  function board(state, seed) {
    const rng = rngFor(seed);
    state.boardSeed = seed;
    state.platforms = generatePlatforms(state.current, state.score, function () { return rng.next(); }).map(function (platform, index) {
      platform.id = 'r' + state.round + '-p' + (index + 1);
      return platform;
    });
    state.seed = rng.value;
    state.selected = 0;
  }
  function create(seed) {
    if (seed === undefined) seed = 0xc0ffee;
    if (!validSeed(seed)) throw new Error('CHEMLEAP_SEED_INVALID');
    const state = { formatVersion: 1, current: 'H2', score: 0, best: 0, round: 1, platforms: [], phase: 'ready', history: [], selected: 0, seed: seed, boardSeed: seed };
    board(state, seed);
    return state;
  }
  function select(state, index) {
    if (!state || state.phase !== 'ready' || !integer(index, 0, state.platforms.length - 1)) return false;
    state.selected = index;
    return true;
  }
  function beginJump(state, index, power) {
    if (!state || state.phase !== 'ready' || pending.has(state)) return null;
    if (index === undefined) index = state.selected;
    if (power === undefined) power = 0.5;
    if (!integer(index, 0, state.platforms.length - 1) || !Number.isFinite(power) || power < 0 || power > 1) return null;
    const platform = state.platforms[index];
    const target = Object.freeze({ id: platform.id, substance: platform.substance, x: platform.x, z: platform.z });
    pending.set(state, { target: target, current: state.current, round: state.round, score: state.score,
      reaction: chemistry.findReaction(state.current, target.substance) });
    state.selected = index;
    state.phase = 'jumping';
    return { target: target, from: { x: 0, z: 2 }, to: { x: target.x, z: target.z }, duration: 700, height: 2.2 + power * 1.1 };
  }
  function land(state) {
    const jump = state && pending.get(state);
    if (!jump || state.phase !== 'jumping') return { success: false, reason: 'CHEMLEAP_NOT_JUMPING', points: 0 };
    pending.delete(state);
    if (!jump.reaction || jump.score >= maximumScore) {
      state.phase = 'over';
      return { success: false, reason: jump.score >= maximumScore ? 'CHEMLEAP_SCORE_LIMIT' : 'CHEMLEAP_NO_REACTION', points: 0, target: jump.target };
    }
    const reaction = jump.reaction;
    state.history.push({ round: jump.round, from: jump.current, reagent: jump.target.substance, next: reaction.next, equation: reaction.equation, points: reaction.points });
    state.history = state.history.slice(-32);
    state.current = reaction.next;
    state.score = jump.score + reaction.points;
    state.best = Math.max(state.best, state.score);
    state.round = jump.round + 1;
    state.phase = 'ready';
    board(state, state.seed);
    return { success: true, reaction: reaction, points: reaction.points, target: jump.target };
  }
  function chargeAt(ms) {
    if (!Number.isFinite(ms) || ms <= 0) return 0;
    const progress = (ms % 2400) / 1200;
    return progress <= 1 ? progress : 2 - progress;
  }
  function targetForCharge(state, power) {
    if (!state || !Array.isArray(state.platforms) || state.platforms.length === 0) return -1;
    const clamped = typeof power !== 'number' || Number.isNaN(power) ? 0 : Math.max(0, Math.min(1, power));
    return Math.min(state.platforms.length - 1, Math.floor(clamped * state.platforms.length));
  }
  function restore(value) {
    try {
      if (typeof value === 'string') {
        if (value.length > 32768) throw new Error('CHEMLEAP_SAVE_INVALID');
        value = JSON.parse(value);
      }
      if (!value || typeof value !== 'object' || Array.isArray(value) || ![Object.prototype, null].includes(Object.getPrototypeOf(value))) throw new Error('CHEMLEAP_SAVE_INVALID');
      if (value.formatVersion !== 1) throw new Error('CHEMLEAP_SAVE_VERSION_UNSUPPORTED');
      if (Object.keys(value).some(function (field) { return !ownedFields.includes(field); }) ||
          !playableSpecies.includes(value.current) || !integer(value.score, 0, maximumScore) || value.score % 10 !== 0 ||
          !integer(value.best, value.score, maximumScore) || value.best % 10 !== 0 || value.round !== value.score / 10 + 1 ||
          !['ready', 'over'].includes(value.phase) || !validSeed(value.seed) || !validSeed(value.boardSeed) ||
          !Array.isArray(value.history) || value.history.length !== Math.min(32, value.score / 10)) throw new Error('CHEMLEAP_SAVE_INVALID');
      if (value.score === 0 && value.current !== 'H2') throw new Error('CHEMLEAP_SAVE_INVALID');
      const history = value.history.map(function (entry, index) {
        if (!entry || typeof entry !== 'object' || Array.isArray(entry) || Object.keys(entry).sort().join(',') !== 'equation,from,next,points,reagent,round' ||
            !playableSpecies.includes(entry.from) || entry.round !== value.round - value.history.length + index ||
            index === 0 && entry.round === 1 && entry.from !== 'H2' || index > 0 && entry.from !== value.history[index - 1].next) throw new Error('CHEMLEAP_SAVE_INVALID');
        const reaction = chemistry.findReaction(entry.from, entry.reagent);
        if (!reaction || entry.next !== reaction.next || entry.equation !== reaction.equation || entry.points !== reaction.points) throw new Error('CHEMLEAP_SAVE_INVALID');
        return { round: entry.round, from: entry.from, reagent: entry.reagent, next: entry.next, equation: entry.equation, points: entry.points };
      });
      if (history.length && history[history.length - 1].next !== value.current) throw new Error('CHEMLEAP_SAVE_INVALID');
      const state = { formatVersion: 1, current: value.current, score: value.score, best: value.best, round: value.round,
        platforms: [], phase: value.phase, history: history, selected: 0, seed: value.seed, boardSeed: value.boardSeed };
      // Platform data in a save is never trusted: deterministically rebuild it.
      board(state, value.boardSeed);
      if (state.seed !== value.seed || !integer(value.selected, 0, state.platforms.length - 1)) throw new Error('CHEMLEAP_SAVE_INVALID');
      state.selected = value.selected;
      return state;
    } catch (error) {
      if (error && error.message === 'CHEMLEAP_SAVE_VERSION_UNSUPPORTED') throw error;
      throw new Error('CHEMLEAP_SAVE_INVALID');
    }
  }
  function serialize(state) {
    const value = {};
    ownedFields.forEach(function (field) { value[field] = state[field]; });
    if (value.phase === 'jumping') value.phase = 'ready';
    return JSON.parse(JSON.stringify(restore(value)));
  }
  function restart(state, seed) {
    const next = create(seed === undefined && state ? state.seed : seed);
    next.best = state && integer(state.best, 0, maximumScore) && state.best % 10 === 0 ? Math.max(state.best, state.score || 0) : 0;
    return next;
  }
  return Object.freeze({ create: create, generatePlatforms: generatePlatforms, select: select, beginJump: beginJump, land: land,
    chargeAt: chargeAt, targetForCharge: targetForCharge, serialize: serialize, restore: restore, restart: restart, playableSpecies: playableSpecies });
});
