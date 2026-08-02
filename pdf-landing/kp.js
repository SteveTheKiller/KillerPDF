/* KillerPDF site - shared chrome behavior (theme, accent, language, easter egg).
   Page-specific behavior (sidebar thumbnails, accordions) stays inline per page. */
(function () {
  var root = document.documentElement;
  var THEMES = ['dark','light','hc','blood','greed','cyanotic'];
  var NEUTRAL = ['dark','light','hc'];
  // Per-family palette copied from the app: [ Accent (bright: text/links/logo/outlines), SelectionBg (darker fill: solid buttons, selected tab edges) ].
  var ACCENTS = {
    dark:  { red:['#DD504B','#5E1C1C'], orange:['#E8962C','#F29A28'], green:['#1EA54C','#1C5E38'], teal:['#1FB8A8','#1C5E5C'], blue:['#50AEE8','#1C3B5E'], purple:['#B982E3','#411C5E'] },
    light: { red:['#931A1A','#931A1A'], orange:['#C7710F','#C7710F'], green:['#1B5E20','#1B5E20'], teal:['#0D827E','#0D827E'], blue:['#18608E','#18608E'], purple:['#5A1690','#5A1690'] },
    hc:    { red:['#FF2929','#FF2929'], orange:['#FF910A','#FF910A'], green:['#00FF66','#00FF66'], teal:['#0AFFE7','#0AFFE7'], blue:['#298DFF','#298DFF'], purple:['#B829FF','#B829FF'] }
  };
  // SelectionBg (muted accent) for inactive tab/card edges; brightens to accent on hover. Mirrors KillerTools themes.ts.
  var SEL = {
    dark:  { red:'#5E1C1C', orange:'#5E3B16', green:'#1C5E38', teal:'#1C5E5C', blue:'#1C3B5E', purple:'#411C5E' },
    light: { red:'#931A1A', orange:'#C7710F', green:'#1B5E20', teal:'#0D827E', blue:'#18608E', purple:'#5A1690' },
    hc:    { red:'#380000', orange:'#4E2900', green:'#003314', teal:'#003832', blue:'#0A2C50', purple:'#250038' }
  };
  function famFor(t) { return t === 'light' ? 'light' : t === 'hc' ? 'hc' : 'dark'; }

  var swatches = [].slice.call(document.querySelectorAll('.swatch'));
  var accDots  = [].slice.call(document.querySelectorAll('.acc'));
  var accentSwitch = document.getElementById('accentSwitch');
  var accToggle = document.getElementById('accentToggle');
  var accPop = document.getElementById('accentPop');
  var curAccent = 'green';

  function applyAccent(name) {
    var theme = root.getAttribute('data-theme');
    var fam = famFor(theme);
    if (!ACCENTS[fam][name]) name = 'green';
    curAccent = name;
    var pair = ACCENTS[fam][name];
    var neutral = NEUTRAL.indexOf(theme) >= 0;
    if (neutral) {
      root.style.setProperty('--accent', pair[0]);
      root.style.setProperty('--btn', pair[1]);
      root.style.setProperty('--sel', (SEL[fam] && SEL[fam][name]) || pair[1]);
      try { localStorage.setItem('kpdf-av', pair[0] + '|' + pair[1]); } catch (e) {}
    } else {
      root.style.removeProperty('--accent');
      root.style.removeProperty('--btn');
      root.style.removeProperty('--sel');
    }
    accDots.forEach(function (d) {
      var p = ACCENTS[fam][d.dataset.accent];
      if (p) { d.style.background = p[0]; d.style.color = p[0]; }
      d.setAttribute('aria-pressed', d.dataset.accent === name ? 'true' : 'false');
    });
    if (accToggle) { accToggle.style.background = pair[0]; accToggle.title = 'Accent color'; }
    try { localStorage.setItem('kpdf-accent', name); } catch (e) {}
    updateLogos();
  }
  function updateLogos() {
    var theme = root.getAttribute('data-theme');
    var variant = (theme === 'light') ? 'light' : 'dark';
    var color = (NEUTRAL.indexOf(theme) >= 0) ? curAccent : 'green';
    var src = 'brand/killerpdf-logo-' + variant + '-' + color + '.svg';
    var imgs = document.querySelectorAll('img.wm-logo');
    for (var i = 0; i < imgs.length; i++) imgs[i].src = src;
  }

  function setTheme(name) {
    if (THEMES.indexOf(name) < 0) name = 'dark';
    root.setAttribute('data-theme', name);
    try { localStorage.setItem('kpdf-theme', name); } catch (e) {}
    swatches.forEach(function (s) { s.setAttribute('aria-pressed', s.dataset.theme === name ? 'true' : 'false'); });
    if (accentSwitch) accentSwitch.hidden = NEUTRAL.indexOf(name) < 0;
    applyAccent(curAccent);
  }

  swatches.forEach(function (s) { s.addEventListener('click', function () { setTheme(s.dataset.theme); if (NEUTRAL.indexOf(s.dataset.theme) >= 0) showAccentBar(); else hideAccentBar(); }); });
  // Build a drop-down accent bar under the toolbar (moves the swatches out of the small header popup).
  var accentBar = null;
  var topbarEl = document.querySelector('.topbar');
  if (topbarEl && accDots.length) {
    accentBar = document.createElement('div');
    accentBar.className = 'accent-bar';
    var pill = document.createElement('div'); pill.className = 'pill';
    var grip = document.createElement('span'); grip.className = 'grip'; grip.setAttribute('aria-hidden', 'true');
    pill.appendChild(grip);
    var blbl = document.createElement('span'); blbl.className = 'lbl'; blbl.textContent = 'accent:';
    pill.appendChild(blbl);
    accDots.forEach(function (d) { pill.appendChild(d); });
    var bx = document.createElement('button'); bx.className = 'x'; bx.setAttribute('aria-label', 'Close'); bx.innerHTML = '&times;';
    bx.addEventListener('click', hideAccentBar);
    pill.appendChild(bx);
    accentBar.appendChild(pill);
    topbarEl.parentNode.insertBefore(accentBar, topbarEl.nextSibling);
    if (accPop) accPop.remove();

    // Drag the strip sideways by its grip, clamped so it stays inside the content pane (the frame).
    var dragDx = 0, dragging = false, dragStartX = 0, dragStartDx = 0;
    function dragClamp(v) {
      var vw = window.innerWidth, pw = pill.offsetWidth, pad = 6, left = 8, right = vw - 8;
      var f = document.querySelector('.content');
      // Extra inset on the right so the pill clears the content scrollbar at its max position.
      if (f) { var fr = f.getBoundingClientRect(); if (fr.width > 0) { left = fr.left + pad; right = fr.right - pad - 12; } }
      var centerLeft = vw / 2 - pw / 2, min = left - centerLeft, max = right - pw - centerLeft;
      if (min > max) return 0;
      return Math.max(min, Math.min(max, v));
    }
    grip.addEventListener('mousedown', function (e) {
      dragging = true; dragStartX = e.clientX; dragStartDx = dragDx;
      document.body.style.userSelect = 'none'; e.preventDefault();
    });
    window.addEventListener('mousemove', function (e) {
      if (!dragging) return;
      dragDx = dragClamp(dragStartDx + (e.clientX - dragStartX));
      pill.style.transform = 'translateX(' + dragDx + 'px)';
    });
    window.addEventListener('mouseup', function () {
      if (!dragging) return; dragging = false; document.body.style.userSelect = '';
    });
    window.addEventListener('resize', function () { dragDx = dragClamp(dragDx); pill.style.transform = 'translateX(' + dragDx + 'px)'; });
    // Default position: top-right corner, nearest the theme picker (still draggable from there).
    requestAnimationFrame(function () { dragDx = dragClamp(1e6); pill.style.transform = 'translateX(' + dragDx + 'px)'; });
  }
  function showAccentBar() { if (accentBar && NEUTRAL.indexOf(root.getAttribute('data-theme')) >= 0) { accentBar.classList.add('show'); if (accToggle) accToggle.setAttribute('aria-expanded', 'true'); } }
  function hideAccentBar() { if (accentBar) { accentBar.classList.remove('show'); if (accToggle) accToggle.setAttribute('aria-expanded', 'false'); } }
  accDots.forEach(function (d) { d.addEventListener('click', function () { applyAccent(d.dataset.accent); }); });
  if (accToggle) {
    accToggle.addEventListener('click', function (e) { e.stopPropagation(); if (accentBar && accentBar.classList.contains('show')) hideAccentBar(); else showAccentBar(); });
  }
  document.addEventListener('click', function (e) { if (accentBar && accentBar.classList.contains('show') && !e.target.closest('.accent-bar') && !e.target.closest('#accentToggle')) hideAccentBar(); });

  // ---- i18n (English complete; other languages fall back to English until translated) ----
  var I18N = (typeof window !== 'undefined' && window.I18N) ? window.I18N : {};
  var EN = {};
  document.querySelectorAll('[data-i18n]').forEach(function (n) { EN[n.getAttribute('data-i18n')] = n.innerHTML; });
  var LANGS = ['en','es','de','fr','ja','tr','zh','zh-cn','bn','cs'];
  var FLAGS = {
    en: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><g fill="#b22234"><rect width="24" height="1.85"/><rect y="3.7" width="24" height="1.85"/><rect y="7.4" width="24" height="1.85"/><rect y="11.1" width="24" height="1.85"/><rect y="14.8" width="24" height="1.85"/><rect y="18.5" width="24" height="1.85"/><rect y="22.2" width="24" height="1.8"/></g><rect width="11" height="12.95" fill="#3c3b6e"/></svg>',
    es: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#c60b1e"/><rect y="6" width="24" height="12" fill="#ffc400"/></svg>',
    de: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#000"/><rect y="8" width="24" height="8" fill="#dd0000"/><rect y="16" width="24" height="8" fill="#ffce00"/></svg>',
    fr: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#0055a4"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ef4135"/></svg>',
    ja: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><circle cx="12" cy="12" r="7" fill="#bc002d"/></svg>',
    tr: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#e30a17"/><circle cx="9.5" cy="12" r="5" fill="#fff"/><circle cx="11" cy="12" r="4" fill="#e30a17"/><polygon points="15.5,9.4 16.12,11.15 17.97,11.2 16.5,12.32 17.03,14.1 15.5,13.05 13.97,14.1 14.5,12.32 13.03,11.2 14.88,11.15" fill="#fff"/></svg>',
    zh: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fe0000"/><rect width="12" height="12" fill="#000095"/><polygon points="6,3 7.2,6.6 11,6.6 7.9,8.8 9.1,12.4 6,10.2 2.9,12.4 4.1,8.8 1,6.6 4.8,6.6" fill="#fff"/></svg>',
    'zh-cn': '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#de2910"/><polygon points="4,3 4.9,5.6 7.6,5.6 5.4,7.3 6.2,9.9 4,8.3 1.8,9.9 2.6,7.3 0.4,5.6 3.1,5.6" fill="#ffde00"/></svg>',
    bn: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#006a4e"/><circle cx="10.5" cy="12" r="6" fill="#f42a41"/></svg>',
    cs: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#d7141a"/><polygon points="0,0 12,12 0,24" fill="#11457e"/></svg>'
  };
  var langItems = [].slice.call(document.querySelectorAll('.lang-item'));
  var langToggle = document.getElementById('langToggle');
  var langMenu = document.getElementById('langMenu');

  function applyLang(lang) {
    if (LANGS.indexOf(lang) < 0) lang = 'en';
    root.setAttribute('lang', lang === 'zh' ? 'zh-Hant' : (lang === 'zh-cn' ? 'zh-Hans' : lang));
    var dict = (lang === 'en') ? EN : (I18N[lang] || {});
    document.querySelectorAll('[data-i18n]').forEach(function (n) {
      var k = n.getAttribute('data-i18n');
      n.innerHTML = (dict && dict[k] != null) ? dict[k] : EN[k];
    });
    langItems.forEach(function (b) { b.setAttribute('aria-pressed', b.dataset.lang === lang ? 'true' : 'false'); });
    if (langToggle) langToggle.innerHTML = FLAGS[lang] || FLAGS.en;
    try { localStorage.setItem('kpdf-lang', lang); } catch (e) {}
  }
  function closeLangMenu() { if (langMenu) { langMenu.hidden = true; langToggle.setAttribute('aria-expanded', 'false'); } }
  if (langToggle && langMenu) {
    langToggle.addEventListener('click', function (e) {
      e.stopPropagation();
      var willOpen = langMenu.hidden;
      langMenu.hidden = !willOpen;
      langToggle.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
    });
    langItems.forEach(function (b) { b.addEventListener('click', function () { applyLang(b.dataset.lang); closeLangMenu(); }); });
    document.addEventListener('click', function (e) { if (!langMenu.hidden && !e.target.closest('.lang-switch')) closeLangMenu(); });
  }

  // ---- Easter egg: click the version number ----
  var verEgg = document.getElementById('verEgg');
  var eggToast = document.getElementById('eggToast');
  if (verEgg) verEgg.addEventListener('click', function () {
    for (var i = 0; i < 18; i++) {
      var d = document.createElement('span');
      d.className = 'drip';
      d.style.left = (Math.random() * 100) + 'vw';
      d.style.height = (18 + Math.random() * 64) + 'px';
      d.style.opacity = (0.6 + Math.random() * 0.4).toFixed(2);
      var dur = 1.1 + Math.random() * 1.6;
      d.style.animation = 'dripfall ' + dur + 's linear forwards';
      d.style.animationDelay = (Math.random() * 0.5) + 's';
      document.body.appendChild(d);
      (function (el) { setTimeout(function () { el.remove(); }, (dur + 0.8) * 1000); })(d);
    }
    if (eggToast) {
      eggToast.textContent = 'No subscriptions were harmed in the making of this PDF editor.';
      eggToast.classList.add('show');
      clearTimeout(verEgg._t);
      verEgg._t = setTimeout(function () { eggToast.classList.remove('show'); }, 2800);
    }
  });

  // ---- Init ----
  var savedTheme = 'dark', savedAccent = 'green', savedLang = 'en';
  try { savedTheme = localStorage.getItem('kpdf-theme') || 'dark'; } catch (e) {}
  try { savedAccent = localStorage.getItem('kpdf-accent') || 'green'; } catch (e) {}
  try { savedLang = localStorage.getItem('kpdf-lang') || 'en'; } catch (e) {}
  curAccent = savedAccent;
  setTheme(savedTheme);
  applyLang(savedLang);
})();
