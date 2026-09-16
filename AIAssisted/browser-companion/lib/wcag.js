// Passive, bounded WCAG evidence. No text, form values, event payloads or raw DOM leave this module.
(function (root) {
  'use strict';
  const MAX_ELEMENTS = 3000;
  function channel(v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }
  function luminance(c) { return 0.2126 * channel(c[0]) + 0.7152 * channel(c[1]) + 0.0722 * channel(c[2]); }
  function composite(fg, bg) {
    const a = fg[3] + bg[3] * (1 - fg[3]);
    return a === 0 ? [0, 0, 0, 0] : [0, 1, 2].map(i => (fg[i] * fg[3] + bg[i] * bg[3] * (1 - fg[3])) / a).concat(a);
  }
  function ratio(fg, bg) {
    if (!fg || !bg || bg[3] !== 1) return null;
    const f = luminance(composite(fg, bg)), b = luminance(bg);
    return (Math.max(f, b) + 0.05) / (Math.min(f, b) + 0.05);
  }
  function color(value) {
    const m = /^rgba?\(\s*([\d.]+)[, ]+([\d.]+)[, ]+([\d.]+)(?:\s*[,/]\s*([\d.]+))?\s*\)$/.exec(value || '');
    if (!m) return null;
    const c = [+m[1], +m[2], +m[3], m[4] === undefined ? 1 : +m[4]];
    return c.slice(0, 3).every(v => v >= 0 && v <= 255) && c[3] >= 0 && c[3] <= 1 ? c : null;
  }
  function threshold(sizePx, weight) { return sizePx >= 24 || (sizePx >= 18.6666666667 && weight >= 700) ? 3 : 4.5; }
  function background(el, win) {
    let bg = [0, 0, 0, 0];
    for (let p = el; p; p = p.parentElement) {
      const s = win.getComputedStyle(p);
      // Compositing, images, overlays and nonstandard color spaces cannot be proven by this sampler.
      if (s.backgroundImage !== 'none' || +s.opacity !== 1 || s.filter !== 'none' || s.mixBlendMode !== 'normal') return null;
      const c = color(s.backgroundColor);
      if (!c) return null;
      bg = composite(bg, c);
      // Still inspect ancestors for opacity/filter, even with an opaque local background.
    }
    // A transparent canvas can depend on browser/user settings; do not assume white.
    return bg[3] === 1 ? bg : null;
  }
  function collect(doc, win, findings, dependencies) {
    const { sanitize, dom, hasAccessibleName } = dependencies;
    const checks = [];
    const all = Array.from(doc.querySelectorAll('*'));
    const bounded = all.slice(0, MAX_ELEMENTS);
    const truncated = all.length > MAX_ELEMENTS;
    function record(id, tested, failed = [], uncertain = 0, na = false) {
      const count = typeof failed === 'number' ? failed : failed.length;
      checks.push({ checkId: id, outcome: count ? 'Fail' : uncertain ? 'ManualReviewRequired' : na ? 'NotApplicable' : 'Pass',
        tested, failed: count, uncertain, selectors: Array.isArray(failed) ? failed.slice(0, 5).map(sanitize.selectorFor).filter(Boolean) : [] });
    }
    // These are explicit executions of the existing check catalogue, not criterion-level passes.
    for (const id of Object.keys(dependencies.rules)) {
      const finding = findings.find(f => f.ruleId === id);
      checks.push({ checkId: id, outcome: finding ? 'Fail' : 'Pass', tested: 1, failed: finding?.count || 0, uncertain: 0, selectors: finding?.selectors || [] });
    }
    const images = bounded.filter(el => el.matches('[role=img], input[type=image], svg[role=img]'));
    record('a11y-image-name', images.length, images.filter(el => el.getAttribute('aria-hidden') !== 'true' &&
      !(el.tagName.toLowerCase() === 'svg' && el.querySelector('title')?.textContent.trim()) && !hasAccessibleName(doc, el)), truncated ? 1 : 0);
    const videos = doc.querySelectorAll('video'), audios = doc.querySelectorAll('audio');
    record('media-presence', videos.length + audios.length, [], videos.length + audios.length);
    // A missing native track is a candidate only: captions may be open, external or live.
    record('media-captions', videos.length, [], Array.from(videos).filter(v => !v.querySelector('track[kind=captions], track[kind=subtitles]')).length);
    record('autoplay-audio', videos.length + audios.length, [], Array.from(doc.querySelectorAll('audio[autoplay], video[autoplay]')).filter(m => !m.muted).length);
    const inputs = bounded.filter(el => el.matches('input[autocomplete], select[autocomplete], textarea[autocomplete]'));
    record('input-purpose', inputs.length, [], inputs.filter(el => /^(off|on)$/.test(el.autocomplete)).length + (truncated ? 1 : 0));
    const text = bounded.filter(el => !el.matches('script, style, option, noscript') && !dom.isHidden(el, win) &&
      Array.from(el.childNodes).some(n => n.nodeType === 3 && n.textContent.trim()));
    const poor = []; let measured = 0, uncertain = truncated ? 1 : 0;
    if (win?.getComputedStyle) for (const el of text) {
      if (el.matches(':disabled') || el.closest('[inert], [aria-disabled=true]')) continue;
      const s = win.getComputedStyle(el);
      if (s.textShadow !== 'none' || s.backgroundClip === 'text' || el.getClientRects().length === 0 || el.closest('svg, canvas')) { uncertain++; continue; }
      const r = ratio(color(s.color), background(el, win));
      if (r === null) { uncertain++; continue; }
      measured++;
      if (r < threshold(parseFloat(s.fontSize), parseInt(s.fontWeight, 10))) poor.push(el);
    }
    record('text-contrast', measured, poor, uncertain);
    const controls = bounded.filter(el => el.matches('button, a[href], [role=button], [role=link]'));
    const mismatch = [];
    for (const el of controls) {
      if (dom.isHidden(el, win)) continue;
      // Only simple text-only controls with explicit aria-label are deterministic here.
      if (el.children.length || !el.hasAttribute('aria-label') || el.hasAttribute('aria-labelledby')) continue;
      const label = el.textContent.trim().replace(/\s+/g, ' ').toLocaleLowerCase();
      const name = el.getAttribute('aria-label').trim().replace(/\s+/g, ' ').toLocaleLowerCase();
      if (label && !name.includes(label)) mismatch.push(el);
    }
    record('label-in-name', controls.length, mismatch, truncated ? 1 : 0);
    record('mouse-only', controls.length, [], bounded.filter(el => el.hasAttribute('onclick') && !el.matches('button, a[href], input, select, textarea, [tabindex]')).length);
    record('invalid-association', doc.querySelectorAll('[aria-invalid=true]').length, [],
      Array.from(doc.querySelectorAll('[aria-invalid=true]')).filter(el => !el.hasAttribute('aria-describedby') && !el.hasAttribute('aria-errormessage')).length);
    record('status-presence', doc.querySelectorAll('[aria-live], [role=status], [role=alert]').length, [], 1);
    record('language-parts', doc.querySelectorAll('[lang]').length, [], 1);
    record('sensory-instructions', text.length, [], text.filter(el => /\b(to the right|below|red button|round icon)\b/i.test(el.textContent)).length);
    record('gesture-presence', bounded.length, [], doc.querySelectorAll('[draggable=true], [ontouchmove]').length);
    record('pointerdown-presence', bounded.length, [], doc.querySelectorAll('[onpointerdown], [onmousedown]').length);
    record('timing-presence', bounded.length, [], doc.querySelectorAll('[role=timer], meta[http-equiv=refresh]').length);
    record('moving-content', bounded.length, [], doc.querySelectorAll('marquee, [aria-roledescription=carousel]').length);
    record('bypass-structure', doc.querySelectorAll('main, [role=main], nav, [role=navigation]').length, [], 1);
    const nav = Array.from(doc.querySelectorAll('nav, [role=navigation]')).slice(0, 100).map(n => n.querySelectorAll('a[href], [role=link]').length);
    const components = ['button', 'input', 'select', 'textarea', '[role=dialog]'].map(s => doc.querySelectorAll(s).length);
    record('navigation-structure', nav.length, [], 1);
    record('component-structure', components.length, [], 1);
    const width = win?.innerWidth;
    checks.push({ checkId: 'reflow-snapshot', outcome: width === 320 ? 'ManualReviewRequired' : 'NotTested', tested: width === 320 ? 1 : 0,
      failed: 0, uncertain: width === 320 && doc.documentElement.scrollWidth > 320 ? 1 : 0, selectors: [] });
    const mediaScopeComplete = !truncated && !doc.querySelector('iframe, object, embed, canvas') && !bounded.some(el => el.shadowRoot || el.tagName.includes('-'));
    return { checks, videoCount: videos.length, audioCount: audios.length, mediaScopeComplete, navigationStructure: nav, componentStructure: components };
  }
  const api = { collect, ratio, color, composite, luminance, threshold, MAX_ELEMENTS };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { wcag: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
