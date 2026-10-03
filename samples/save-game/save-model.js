/* Reuses element-pairs' finite game model; migration is application-owned, never an implicit host schema rename. */
(function (root) {
  "use strict";
  const symbols = ["H", "He", "C", "N", "O", "Ne"];
  function fail() { throw Object.assign(new Error("Unsupported or invalid save content."), { code: "SAVE_FORMAT_UNSUPPORTED" }); }
  function exact(value, fields) { return value && typeof value === "object" && !Array.isArray(value) && Object.keys(value).sort().join(",") === [...fields].sort().join(","); }
  function validate(value) {
    let normalized;
    if (exact(value, ["formatVersion", "nickname", "deck", "matched", "moves"]) && value.formatVersion === 1) normalized = value;
    else if (exact(value, ["formatVersion", "player", "deck", "matched", "turns"]) && value.formatVersion === 2 && exact(value.player, ["nickname"]))
      normalized = { formatVersion: 1, nickname: value.player.nickname, deck: value.deck, matched: value.matched, moves: value.turns };
    else return fail();
    if (typeof normalized.nickname !== "string" || normalized.nickname.length > 24
      || !Number.isSafeInteger(normalized.moves) || normalized.moves < 0
      || !Array.isArray(normalized.deck) || normalized.deck.length !== 12
      || symbols.some(symbol => normalized.deck.filter(card => card === symbol).length !== 2)
      || !Array.isArray(normalized.matched) || normalized.matched.length % 2 !== 0
      || new Set(normalized.matched).size !== normalized.matched.length
      || normalized.matched.some(index => !Number.isInteger(index) || index < 0 || index > 11)
      || normalized.moves < normalized.matched.length / 2
      || symbols.some(symbol => { const count = normalized.matched.filter(index => normalized.deck[index] === symbol).length; return count !== 0 && count !== 2; })) return fail();
    return { formatVersion: 1, nickname: normalized.nickname, deck: [...normalized.deck], matched: [...normalized.matched], moves: normalized.moves };
  }
  function toV2(value) { const checked = validate(value); return { formatVersion: 2, player: { nickname: checked.nickname }, deck: checked.deck, matched: checked.matched, turns: checked.moves }; }
  const api = Object.freeze({ validate, toV2 });
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.SaveModel = api;
})(typeof window === "object" ? window : globalThis);
