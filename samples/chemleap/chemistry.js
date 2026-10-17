(function (root, factory) {
  'use strict';
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.ChemLeapChemistry = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  // Equations are curriculum abstractions, not practical experiment instructions.
  // Source references support the chemistry; every displayed equation is also
  // independently checked for atom conservation by chemleap-model-tests.cjs.
  const sources = Object.freeze([
    'https://openstax.org/books/chemistry-2e/pages/4-1-writing-and-balancing-chemical-equations',
    'https://openstax.org/books/chemistry-2e/pages/4-2-classifying-chemical-reactions',
    'https://openstax.org/books/chemistry-atoms-first/pages/7-4-reaction-yields',
    'https://chem.libretexts.org/Courses/University_of_British_Columbia/CHEM_100:_Foundations_of_Chemistry/07:_Chemical_Reactions/7.08:_AcidBase_and_Gas_Evolution_Reactions',
    'https://pmc.ncbi.nlm.nih.gov/articles/PMC8696962/',
    'https://chem.libretexts.org/Courses/University_of_Kentucky/UK%3A_General_Chemistry/18%3A_Representative_Metals_Metalloids_and_Nonmetals/18.05%3A_Occurrence_Preparation_and_Compounds_of_Hydrogen',
    'https://vuir.vu.edu.au/45866/1/HABY_Carl-Thesis_nosignature.pdf',
    'https://repository.gatech.edu/server/api/core/bitstreams/6d3a3002-1f7b-4d19-890e-1c055e7d5bcc/content'
  ]);
  const species = Object.freeze(Object.fromEntries([
    ['H2', 'H₂', '氢气', '#b7e8ff'],
    ['O2', 'O₂', '氧气', '#74c9f3'],
    ['Cl2', 'Cl₂', '氯气', '#dae77c'],
    ['CuO', 'CuO', '氧化铜', '#7c7793'],
    ['Cu', 'Cu', '铜', '#d99b74'],
    ['H2O', 'H₂O', '水', '#89e4e4'],
    ['HCl', 'HCl', '氯化氢 / 盐酸', '#ffc18e'],
    ['Na', 'Na', '钠', '#d3d7ed'],
    ['NaOH', 'NaOH', '氢氧化钠', '#c7a8ff'],
    ['CaO', 'CaO', '氧化钙', '#f0dfb7'],
    ['Ca(OH)2', 'Ca(OH)₂', '氢氧化钙', '#b3e5b8'],
    ['CaCO3', 'CaCO₃', '碳酸钙', '#f1ddbc'],
    ['CO2', 'CO₂', '二氧化碳', '#b4ccf2'],
    ['Na2CO3', 'Na₂CO₃', '碳酸钠', '#f6baee'],
    ['HNO3', 'HNO₃', '硝酸', '#ffb5a8'],
    ['NaCl', 'NaCl', '氯化钠', '#e2e5f0'],
    ['CaCl2', 'CaCl₂', '氯化钙', '#e8d0b6'],
    ['Ca(NO3)2', 'Ca(NO₃)₂', '硝酸钙', '#e1cbb9'],
    ['NaNO3', 'NaNO₃', '硝酸钠', '#edbcd5'],
    ['He', 'He', '氦气', '#c5b8e6'],
    ['Ne', 'Ne', '氖气', '#efb5ce'],
    ['Ar', 'Ar', '氩气', '#aec8d7'],
    ['Kr', 'Kr', '氪气', '#b9d1b7']
  ].map(function (row) {
    return [row[0], Object.freeze({ formula: row[1], name: row[2], color: row[3] })];
  })));

  function reaction(reactants, products, next, equation, condition) {
    return Object.freeze({ reactants: Object.freeze(reactants), products: Object.freeze(products), next: next,
      equation: equation, condition: condition, points: 10 });
  }
  const reactions = Object.freeze([
    reaction(['H2', 'O2'], ['H2O'], 'H2O', '2H₂ + O₂ → 2H₂O', '满足反应所需的能量条件'),
    reaction(['H2', 'Cl2'], ['HCl'], 'HCl', 'H₂ + Cl₂ → 2HCl', '满足反应所需的能量条件'),
    reaction(['H2', 'CuO'], ['Cu', 'H2O'], 'H2O', 'H₂ + CuO → Cu + H₂O', '还原反应需要相应的能量条件'),
    reaction(['H2O', 'Na'], ['NaOH', 'H2'], 'NaOH', '2H₂O + 2Na → 2NaOH + H₂', '金属与水的反应'),
    reaction(['H2O', 'CaO'], ['Ca(OH)2'], 'Ca(OH)2', 'H₂O + CaO → Ca(OH)₂', '碱性氧化物与水的反应'),
    reaction(['HCl', 'NaOH'], ['NaCl', 'H2O'], 'H2O', 'HCl + NaOH → NaCl + H₂O', '水溶液中的酸碱中和'),
    reaction(['HCl', 'Ca(OH)2'], ['CaCl2', 'H2O'], 'H2O', '2HCl + Ca(OH)₂ → CaCl₂ + 2H₂O', '水溶液中的酸碱中和'),
    reaction(['CaCO3', 'HCl'], ['CaCl2', 'CO2', 'H2O'], 'CO2', 'CaCO₃ + 2HCl → CaCl₂ + CO₂ + H₂O', '酸与碳酸盐的反应'),
    reaction(['CaCO3', 'HNO3'], ['Ca(NO3)2', 'CO2', 'H2O'], 'CO2', 'CaCO₃ + 2HNO₃ → Ca(NO₃)₂ + CO₂ + H₂O', '酸与碳酸盐的反应'),
    reaction(['NaOH', 'CO2'], ['Na2CO3', 'H2O'], 'Na2CO3', '2NaOH + CO₂ → Na₂CO₃ + H₂O', '水溶液中碱相对充足，生成碳酸盐'),
    reaction(['Ca(OH)2', 'CO2'], ['CaCO3', 'H2O'], 'CaCO3', 'Ca(OH)₂ + CO₂ → CaCO₃ + H₂O', '水溶液中碱相对充足，形成碳酸盐沉淀'),
    reaction(['Na2CO3', 'HCl'], ['NaCl', 'CO2', 'H2O'], 'CO2', 'Na₂CO₃ + 2HCl → 2NaCl + CO₂ + H₂O', '酸与碳酸盐的反应'),
    reaction(['Na2CO3', 'HNO3'], ['NaNO3', 'CO2', 'H2O'], 'CO2', 'Na₂CO₃ + 2HNO₃ → 2NaNO₃ + CO₂ + H₂O', '酸与碳酸盐的反应'),
    reaction(['Na2CO3', 'Ca(OH)2'], ['CaCO3', 'NaOH'], 'CaCO3', 'Na₂CO₃ + Ca(OH)₂ → CaCO₃ + 2NaOH', '水溶液中形成碳酸钙沉淀'),
    reaction(['NaOH', 'HNO3'], ['NaNO3', 'H2O'], 'H2O', 'NaOH + HNO₃ → NaNO₃ + H₂O', '水溶液中的酸碱中和')
  ]);

  const byPair = new Map();
  function key(a, b) { return [a, b].sort().join('|'); }
  reactions.forEach(function (entry) {
    const pair = key(entry.reactants[0], entry.reactants[1]);
    if (byPair.has(pair)) throw new Error('CHEMLEAP_REACTION_AMBIGUOUS');
    entry.reactants.concat(entry.products).forEach(function (id) {
      if (!Object.hasOwn(species, id)) throw new Error('CHEMLEAP_SPECIES_UNKNOWN');
    });
    if (!entry.products.includes(entry.next)) throw new Error('CHEMLEAP_NEXT_NOT_PRODUCT');
    byPair.set(pair, entry);
  });
  function findReaction(current, reagent) {
    if (typeof current !== 'string' || typeof reagent !== 'string') return null;
    return byPair.get(key(current, reagent)) || null;
  }
  function validReagents(current) {
    if (typeof current !== 'string') return [];
    const result = [];
    reactions.forEach(function (entry) {
      const index = entry.reactants.indexOf(current);
      if (index !== -1) result.push(entry.reactants[1 - index]);
    });
    return Array.from(new Set(result));
  }
  return Object.freeze({ species: species, reactions: reactions, findReaction: findReaction, validReagents: validReagents, sources: sources });
});
