// Uses the same bundled Deque axe-core as the backend. Raw axe nodes never leave this closure.
(function (root) {
  'use strict';
  function summarize(result, evidenceVersion) {
    const outcomes = { violations: 'Fail', incomplete: 'ManualReviewRequired', passes: 'Pass', inapplicable: 'NotApplicable' };
    const rules = [];
    for (const [kind, outcome] of Object.entries(outcomes)) {
      for (const rule of result[kind] || []) {
        const criterionIds = [...new Set((rule.tags || []).map(tag => /^wcag(\d)(\d)(\d{1,2})$/.exec(tag))
          .filter(Boolean).map(m => `${m[1]}.${m[2]}.${m[3]}`))];
        if (criterionIds.length && /^[a-z0-9-]{1,80}$/.test(rule.id))
          rules.push({ ruleId: rule.id, outcome, criterionIds, count: Math.min(100000, (rule.nodes || []).length) });
      }
    }
    return { state: 'Completed', version: result.testEngine?.version, evidenceVersion, rules: rules.slice(0, 300) };
  }
  function collector(axe, hooks = {}) {
    let key = null, pending = null;
    return async function collect(doc, evidenceVersion) {
      if (key === evidenceVersion && pending) return pending;
      key = evidenceVersion;
      const previous = pending;
      pending = (async () => {
        if (previous) await previous;
        if (!axe?.run) return { state: 'Unavailable', evidenceVersion, rules: [] };
        try {
          hooks.beforeRun?.();
          const result = await axe.run(doc, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
            resultTypes: ['violations', 'incomplete', 'passes', 'inapplicable'] });
          return summarize(result, evidenceVersion);
        } catch { return { state: 'Unavailable', evidenceVersion, rules: [] }; }
        finally { hooks.afterRun?.(); }
      })();
      return pending;
    };
  }
  const api = { summarize, collector };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { axeEvidence: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
