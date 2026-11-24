/* KillerPDF site - shared chrome behavior (theme, accent, language, easter egg).
   Page-specific behavior (sidebar thumbnails, accordions) stays inline per page. */
(function () {
  var root = document.documentElement;
  var scriptBase = document.currentScript ? new URL('.', document.currentScript.src).href : '/';
  var THEMES = ['dark','light','hc','blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning','trashpolka'];
  // Themes that take an accent. Trash Polka keeps its own accent, stored apart from the others.
  var NEUTRAL = ['dark','light','hc','trashpolka'];
  var TP_ACCENT_KEY = 'kpdf-tp-accent';
  var THEMED = ['blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning'];  // fixed-color wordmark art
  // Per-family palette copied from the app: [ Accent (bright: text/links/logo/outlines), SelectionBg (darker fill: solid buttons, selected tab edges) ].
  var ACCENTS = {
    dark:  { red:['#FF7C77','#5E1C1C'], orange:['#E8962C','#F29A28'], yellow:['#EAD900','#F2E500'], green:['#22BB56','#1C5E38'], teal:['#1FB8A8','#1C5E5C'], blue:['#50AEE8','#1C3B5E'], purple:['#C08FE6','#411C5E'], magenta:['#FF6CD1','#FF2BBD'] },
    light: { red:['#901A1A','#901A1A'], orange:['#E8962C','#E8962C'], yellow:['#F2C500','#F2C500'], green:['#18541D','#18541D'], teal:['#085250','#085250'], blue:['#134D73','#134D73'], purple:['#5A1690','#5A1690'], magenta:['#8F0061','#8F0061'] },
    hc:    { red:['#FF3131','#FF3131'], orange:['#FF910A','#FF910A'], yellow:['#FFEB00','#FFEB00'], green:['#00FF66','#00FF66'], teal:['#0AFFE7','#0AFFE7'], blue:['#298DFF','#298DFF'], purple:['#C34AFF','#C34AFF'], magenta:['#FF2BBD','#FF2BBD'] },
    // Trash Polka: hover and pressed stay on the base color, like the app.
    trashpolka: { red:['#D7263D','#D7263D'], orange:['#EE7B22','#EE7B22'], yellow:['#EED500','#EED500'], green:['#7A9A3A','#7A9A3A'], teal:['#1E9A96','#1E9A96'], blue:['#3A6FD0','#3A6FD0'], purple:['#8A4CC2','#8A4CC2'], magenta:['#E4588E','#E4588E'] }
  };
  // SelectionBg (muted accent) for inactive tab/card edges; brightens to accent on hover. Mirrors KillerTools themes.ts.
  var SEL = {
    dark:  { red:'#5E1C1C', orange:'#5E3B16', yellow:'#2A2908', green:'#1C5E38', teal:'#1C5E5C', blue:'#1C3B5E', purple:'#411C5E', magenta:'#2A0D24' },
    light: { red:'#901A1A', orange:'#E8962C', yellow:'#F2C500', green:'#18541D', teal:'#085250', blue:'#134D73', purple:'#5A1690', magenta:'#8F0061' },
    hc:    { red:'#380000', orange:'#4E2900', yellow:'#2E2B00', green:'#003314', teal:'#003832', blue:'#0A2C50', purple:'#250038', magenta:'#2E0022' },
    trashpolka: { red:'#4A1218', orange:'#55300E', yellow:'#EED500', green:'#303A1D', teal:'#153A39', blue:'#1D2D4A', purple:'#352346', magenta:'#502636' }
  };
  function famFor(t) { return t === 'light' ? 'light' : t === 'hc' ? 'hc' : t === 'trashpolka' ? 'trashpolka' : 'dark'; }
  // Text on an accent fill uses white or near-black, whichever contrasts more with that fill.
  // Swatches and filled buttons use a deeper red than the dark red accent text, which stays light for contrast.
  var FILL = { '#FF7C77': '#EA524C' };
  function fillOf(hex) { return FILL[String(hex).toUpperCase()] || hex; }
  function readableOn(hex) {
    var lum = function (h) { var c = [1, 3, 5].map(function (i) { var v = parseInt(h.substr(i, 2), 16) / 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }); return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]; };
    var l = lum(hex), dark = lum('#1e1f22');
    return (1.05 / (l + 0.05)) >= ((l + 0.05) / (dark + 0.05)) ? '#ffffff' : '#1e1f22';
  }

  var swatches = [].slice.call(document.querySelectorAll('.swatch'));
  var accDots  = [].slice.call(document.querySelectorAll('.acc'));
  var accentSwitch = document.getElementById('accentSwitch');
  var accToggle = document.getElementById('accentToggle');
  var accPop = document.getElementById('accentPop');
  var curAccent = 'green';

  function buildThemeFlyout() {
    var group = document.querySelector('.topbar .tgrp');
    if (!group || !group.parentNode) return;
    var toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'theme-toggle';
    toggle.title = 'Theme';
    toggle.setAttribute('aria-label', 'Choose theme');
    toggle.setAttribute('aria-haspopup', 'true');
    toggle.setAttribute('aria-expanded', 'false');
    var preview = document.createElement('span');
    preview.setAttribute('aria-hidden', 'true');
    toggle.appendChild(preview);
    group.parentNode.insertBefore(toggle, group);

    function syncPreview(name) {
      var active = group.querySelector('.swatch[data-theme="' + name + '"]');
      if (!active) active = group.querySelector('.swatch');
      if (!active) return;
      preview.className = active.className;
      preview.removeAttribute('aria-pressed');
    }
    function closeFlyout(focusToggle) {
      group.classList.remove('open');
      toggle.setAttribute('aria-expanded', 'false');
      if (focusToggle) toggle.focus();
    }
    toggle.addEventListener('click', function (e) {
      e.stopPropagation();
      var opening = !group.classList.contains('open');
      group.classList.toggle('open', opening);
      toggle.setAttribute('aria-expanded', opening ? 'true' : 'false');
    });
    group.addEventListener('click', function (e) {
      var swatch = e.target.closest('.swatch[data-theme]');
      if (!swatch) return;
      syncPreview(swatch.getAttribute('data-theme'));
      closeFlyout(false);
    });
    document.addEventListener('click', function (e) {
      if (!group.contains(e.target) && !toggle.contains(e.target)) closeFlyout(false);
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && group.classList.contains('open')) closeFlyout(true);
    });
    syncPreview(root.getAttribute('data-theme') || 'dark');
  }
  buildThemeFlyout();


  function localPreviewSetting(name) {
    if (window.location.protocol !== 'file:') return null;
    try { return new URLSearchParams(window.location.search).get(name); } catch (e) { return null; }
  }

  function syncLocalPreviewLinks() {
    if (window.location.protocol !== 'file:') return;
    try {
      var current = new URL(window.location.href);
      current.searchParams.set('theme', root.getAttribute('data-theme'));
      current.searchParams.set('accent', curAccent);
      window.history.replaceState(null, '', current.href);
    } catch (e) {}
    document.querySelectorAll('a[href]').forEach(function (link) {
      var href = link.getAttribute('href');
      if (!href || href.charAt(0) === '#' || /^(?:https?:|mailto:|javascript:)/i.test(href)) return;
      try {
        var target = new URL(href, window.location.href);
        if (target.protocol !== 'file:' || !/\.html$/i.test(target.pathname)) return;
        target.searchParams.set('theme', root.getAttribute('data-theme'));
        target.searchParams.set('accent', curAccent);
        link.href = target.href;
      } catch (e) {}
    });
  }

  function applyAccent(name) {
    var theme = root.getAttribute('data-theme');
    var fam = famFor(theme);
    if (!ACCENTS[fam][name]) name = 'green';
    ['dark', 'light', 'hc', 'trashpolka'].forEach(function (neutralTheme) {
      if ((neutralTheme === 'trashpolka') !== (fam === 'trashpolka')) return;
      var preview = ACCENTS[neutralTheme][name];
      if (preview) document.querySelectorAll('.sw-' + neutralTheme).forEach(function (dot) {
        dot.style.setProperty('--sw-accent', neutralTheme === 'light' && name === 'yellow' ? 'linear-gradient(#FFF5A3, #FFD43B)' : fillOf(preview[0]));
      });
    });
    curAccent = name;
    var pair = ACCENTS[fam][name];
    var neutral = NEUTRAL.indexOf(theme) >= 0;
    root.toggleAttribute('data-yellow-outline', neutral && fam === 'light' && (name === 'yellow' || name === 'orange'));
    if (neutral) {
      root.style.setProperty('--accent', pair[0]);
      root.style.setProperty('--btn', pair[1]);
      root.style.setProperty('--sel', (SEL[fam] && SEL[fam][name]) || pair[1]);
      root.style.setProperty('--btn-text', readableOn(pair[1]));
      root.style.setProperty('--accent-text', readableOn(pair[0]));
      try { localStorage.setItem('kpdf-av', pair[0] + '|' + pair[1]); } catch (e) {}
    } else {
      root.style.removeProperty('--accent');
      root.style.removeProperty('--btn');
      root.style.removeProperty('--sel');
      root.style.removeProperty('--btn-text');
      root.style.removeProperty('--accent-text');
    }
    accDots.forEach(function (d) {
      var p = ACCENTS[fam][d.dataset.accent];
      if (p) {
        var previewColor = fam === 'light' && d.dataset.accent === 'yellow' ? '#EAD900' : fillOf(p[0]);
        d.style.background = previewColor;
        d.style.color = previewColor;
      }
      d.setAttribute('aria-pressed', d.dataset.accent === name ? 'true' : 'false');
    });
    if (accToggle) { accToggle.style.background = fam === 'light' && name === 'yellow' ? '#EAD900' : fillOf(pair[0]); accToggle.title = 'Accent color'; }
    try { localStorage.setItem(fam === 'trashpolka' ? TP_ACCENT_KEY : 'kpdf-accent', name); } catch (e) {}
    if (window.TrashPolka) { if (fam === 'trashpolka') window.TrashPolka.apply(pair[0]); else window.TrashPolka.clear(); }
    updateLogos();
    syncLocalPreviewLinks();
  }
  function updateLogos() {
    var theme = root.getAttribute('data-theme');
    var src;
    if (THEMED.indexOf(theme) >= 0) {
      // Fixed-color themes carry their own wordmark art, colored with the theme's in-app
      // AccentLogo resource (make-logo-svgs.py --themes).
      src = scriptBase + 'brand/killerpdf-logo-' + theme + '.svg';
    } else {
      // Trash Polka has no wordmark art of its own yet, so it uses the Black one in its accent.
      var variant = theme === 'light' ? 'light' : (theme === 'hc' || theme === 'trashpolka') ? 'black' : 'dark';
      var color = (NEUTRAL.indexOf(theme) >= 0) ? curAccent : 'green';
      src = scriptBase + 'brand/killerpdf-logo-' + variant + '-' + color + '.svg';
    }
    var imgs = document.querySelectorAll('img.wm-logo');
    for (var i = 0; i < imgs.length; i++) imgs[i].src = src;
    root.classList.remove('logo-pending');
  }

  function setTheme(name) {
    if (THEMES.indexOf(name) < 0) name = 'dark';
    var wasTp = root.getAttribute('data-theme') === 'trashpolka';
    if ((name === 'trashpolka') !== wasTp) {
      var stored = null;
      try { stored = localStorage.getItem(name === 'trashpolka' ? TP_ACCENT_KEY : 'kpdf-accent'); } catch (e) {}
      curAccent = stored || (name === 'trashpolka' ? 'red' : 'green');
    }
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
    var blbl = document.createElement('span'); blbl.className = 'lbl'; blbl.setAttribute('data-i18n', 'accent_label'); blbl.textContent = 'accent:';
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

  function normalizeCurrentFacts(key, value) {
    if (key === 'pa_31') {
      var version = (EN.pa_31 || '').match(/v\d+\.\d+\.\d+/);
      if (version) return value.replace(/v\d+\.\d+\.\d+/g, version[0]);
    }
    return value;
  }
  document.querySelectorAll('[data-engine-fact]').forEach(function (node) {
    var facts = window.KPDF_ENGINE_FACTS;
    var value = facts && facts[node.getAttribute('data-engine-fact')];
    if (typeof value === 'number') node.textContent = value.toLocaleString('en-US');
  });
  var LANGS = ['en','es','de','fr','ja','kk','ru','uk','nb','pt','tr','vi','zh','zh-cn','bn','cs','pl','hu','it','sl','sv','ko'];
  var FLAGS = {
    sv: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#006aa7"/><rect x="7" width="4" height="24" fill="#fecc00"/><rect y="10" width="24" height="4" fill="#fecc00"/></svg>',
    // Taegeuk in the middle, a trigram in each corner; solid bars are one rect, broken bars two.
    ko: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><g transform="rotate(34 12 12)"><circle cx="12" cy="12" r="5.2" fill="#0047a0"/><path d="M6.8 12a5.2 5.2 0 0 1 10.4 0a2.6 2.6 0 0 0-5.2 0a2.6 2.6 0 0 1-5.2 0Z" fill="#cd2e3a"/></g><g fill="#000"><g transform="translate(4.3 4.3) rotate(-45)"><rect x="-1.8" y="-1.75" width="3.6" height=".7"/><rect x="-1.8" y="-.35" width="3.6" height=".7"/><rect x="-1.8" y="1.05" width="3.6" height=".7"/></g><g transform="translate(4.3 19.7) rotate(45)"><rect x="-1.8" y="-1.75" width="3.6" height=".7"/><rect x="-1.8" y="-.35" width="1.5" height=".7"/><rect x=".3" y="-.35" width="1.5" height=".7"/><rect x="-1.8" y="1.05" width="3.6" height=".7"/></g><g transform="translate(19.7 4.3) rotate(45)"><rect x="-1.8" y="-1.75" width="1.5" height=".7"/><rect x=".3" y="-1.75" width="1.5" height=".7"/><rect x="-1.8" y="-.35" width="3.6" height=".7"/><rect x="-1.8" y="1.05" width="1.5" height=".7"/><rect x=".3" y="1.05" width="1.5" height=".7"/></g><g transform="translate(19.7 19.7) rotate(-45)"><rect x="-1.8" y="-1.75" width="1.5" height=".7"/><rect x=".3" y="-1.75" width="1.5" height=".7"/><rect x="-1.8" y="-.35" width="1.5" height=".7"/><rect x=".3" y="-.35" width="1.5" height=".7"/><rect x="-1.8" y="1.05" width="1.5" height=".7"/><rect x=".3" y="1.05" width="1.5" height=".7"/></g></g></svg>',
    en: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><g fill="#b22234"><rect width="24" height="1.85"/><rect y="3.7" width="24" height="1.85"/><rect y="7.4" width="24" height="1.85"/><rect y="11.1" width="24" height="1.85"/><rect y="14.8" width="24" height="1.85"/><rect y="18.5" width="24" height="1.85"/><rect y="22.2" width="24" height="1.8"/></g><rect width="11" height="12.95" fill="#3c3b6e"/></svg>',
    es: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#c60b1e"/><rect y="6" width="24" height="12" fill="#ffc400"/></svg>',
    de: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#000"/><rect y="8" width="24" height="8" fill="#dd0000"/><rect y="16" width="24" height="8" fill="#ffce00"/></svg>',
    fr: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#0055a4"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ef4135"/></svg>',
    ja: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><circle cx="12" cy="12" r="7" fill="#bc002d"/></svg>',
    kk: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#00afca"/><circle cx="12" cy="12" r="4.5" fill="#f6d34a"/><g stroke="#f6d34a" stroke-width="1"><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1"/></g></svg>',
    ru: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#fff"/><rect y="8" width="24" height="8" fill="#0039a6"/><rect y="16" width="24" height="8" fill="#d52b1e"/></svg>',
    uk: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#0057b7"/><rect y="12" width="24" height="12" fill="#ffd700"/></svg>',
    nb: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#ba0c2f"/><rect x="6" width="6" height="24" fill="#fff"/><rect y="9" width="24" height="6" fill="#fff"/><rect x="7.5" width="3" height="24" fill="#00205b"/><rect y="10.5" width="24" height="3" fill="#00205b"/></svg>',
    pt: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#009c3b"/><polygon points="12,3.5 21.5,12 12,20.5 2.5,12" fill="#ffdf00"/><circle cx="12" cy="12" r="4.6" fill="#002776"/></svg>',
    tr: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#e30a17"/><circle cx="9.5" cy="12" r="5" fill="#fff"/><circle cx="11" cy="12" r="4" fill="#e30a17"/><polygon points="15.5,9.4 16.12,11.15 17.97,11.2 16.5,12.32 17.03,14.1 15.5,13.05 13.97,14.1 14.5,12.32 13.03,11.2 14.88,11.15" fill="#fff"/></svg>',
    vi: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#da251d"/><polygon points="12,5 13.65,9.85 18.75,9.85 14.62,12.85 16.2,17.7 12,14.7 7.8,17.7 9.38,12.85 5.25,9.85 10.35,9.85" fill="#ff0"/></svg>',
    zh: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fe0000"/><rect width="12" height="12" fill="#000095"/><polygon points="6,3 7.2,6.6 11,6.6 7.9,8.8 9.1,12.4 6,10.2 2.9,12.4 4.1,8.8 1,6.6 4.8,6.6" fill="#fff"/></svg>',
    'zh-cn': '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#de2910"/><polygon points="4,3 4.9,5.6 7.6,5.6 5.4,7.3 6.2,9.9 4,8.3 1.8,9.9 2.6,7.3 0.4,5.6 3.1,5.6" fill="#ffde00"/></svg>',
    bn: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#006a4e"/><circle cx="10.5" cy="12" r="6" fill="#f42a41"/></svg>',
    cs: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#d7141a"/><polygon points="0,0 12,12 0,24" fill="#11457e"/></svg>',
    pl: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#dc143c"/></svg>',
    hu: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#ce2939"/><rect y="8" width="24" height="8" fill="#fff"/><rect y="16" width="24" height="8" fill="#477050"/></svg>',
    sl: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#fff"/><rect y="8" width="24" height="8" fill="#005da4"/><rect y="16" width="24" height="8" fill="#ed1c24"/><path d="M4 5h7v5c0 3-3.5 4-3.5 4S4 13 4 10z" fill="#005da4" stroke="#ed1c24" stroke-width=".6"/><path d="M4.8 10l1.5-2 1.2 1 1.2-1 1.5 2z" fill="#fff"/></svg>',
    it: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#009246"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ce2b37"/></svg>'
  };
  // Each menu row leads with the same round flag the toggle shows and ends with the locale
  // tag, matching killertools.net. Keys are lowercase: zh is Traditional, zh-cn Simplified.
  var LANG_CODES = { en: 'en-US', bn: 'bn-BD', cs: 'cs-CZ', de: 'de-DE', es: 'es-ES', fr: 'fr-FR', hu: 'hu-HU', it: 'it-IT', ja: 'ja-JP', kk: 'kk-KZ', ko: 'ko-KR', nb: 'nb-NO', pl: 'pl-PL', pt: 'pt-BR', ru: 'ru-RU', sl: 'sl-SI', sv: 'sv-SE', tr: 'tr-TR', uk: 'uk-UA', vi: 'vi-VN', zh: 'zh-TW', 'zh-tw': 'zh-TW', 'zh-cn': 'zh-CN' };
  [].forEach.call(document.querySelectorAll('.lang-item[data-lang]'), function (b) {
    var f = FLAGS[b.dataset.lang];
    if (f && !b.querySelector('.lang-flag')) b.insertAdjacentHTML('afterbegin', '<span class="lang-flag" aria-hidden="true">' + f + '</span>');
    var c = LANG_CODES[String(b.getAttribute('data-lang')).toLowerCase()];
    if (c && !b.querySelector('.lang-code')) b.insertAdjacentHTML('beforeend', '<span class="lang-code">' + c + '</span>');
  });
  // The language menu matches killertools.net row for row: the same native names in the
  // same order (English first, then by locale code). CSS adds the check on the active row.
  var LANG_NAMES = { en: 'English', bn: 'বাংলা', cs: 'Čeština', de: 'Deutsch', es: 'Español', fr: 'Français', hu: 'Magyar', it: 'Italiano', ja: '日本語', kk: 'Қазақша', ko: '한국어', nb: 'Norsk', pl: 'Polski', pt: 'Português', ru: 'Русский', sl: 'Slovenščina', sv: 'Svenska', tr: 'Türkçe', uk: 'Українська', vi: 'Tiếng Việt', 'zh-cn': '简体中文', zh: '繁體中文', 'zh-tw': '繁體中文' };
  var LANG_ORDER = ['en', 'bn', 'cs', 'de', 'es', 'fr', 'hu', 'it', 'ja', 'kk', 'ko', 'nb', 'pl', 'pt', 'ru', 'sl', 'sv', 'tr', 'uk', 'vi', 'zh-cn', 'zh', 'zh-tw'];
  (function () {
    var menu = document.getElementById('langMenu');
    if (!menu) return;
    var key = function (b) { return String(b.getAttribute('data-lang')).toLowerCase(); };
    var rows = [].slice.call(menu.querySelectorAll('.lang-item[data-lang]'));
    rows.forEach(function (b) {
      var name = LANG_NAMES[key(b)];
      if (!name || b.querySelector('.lang-name')) return;
      [].slice.call(b.childNodes).forEach(function (n) { if (n.nodeType === 3) b.removeChild(n); });
      var span = document.createElement('span');
      span.className = 'lang-name';
      span.textContent = name;
      var flag = b.querySelector('.lang-flag');
      b.insertBefore(span, flag ? flag.nextSibling : b.firstChild);
    });
    rows.sort(function (a, b) { return LANG_ORDER.indexOf(key(a)) - LANG_ORDER.indexOf(key(b)); });
    rows.forEach(function (b) { menu.appendChild(b); });
  })();
  var langItems = [].slice.call(document.querySelectorAll('.lang-item'));
  var langToggle = document.getElementById('langToggle');
  var langMenu = document.getElementById('langMenu');

  var labelKeys = {"Theme":"ui_Theme","Language":"ui_Language","Dark":"ui_Theme_Dark","Light":"ui_Theme_Light","Black":"ui_Theme_Black","Blood":"ui_Theme_Blood","Greed":"ui_Theme_Greed","Cyanotic":"ui_Theme_Cyanotic","98SE":"ui_Theme_98SE","Ectoplasm":"ui_Theme_Ectoplasm","Decay":"ui_Theme_Decay","Mourning":"ui_Theme_Mourning","Sepulchre":"ui_Theme_Sepulchre","Delirium":"ui_Theme_Delirium","Malaise":"ui_Theme_Malaise","Close":"ui_Lbl_Close","Ctrl":"ui_Key_Ctrl","Alt":"ui_Key_Alt","Shift":"ui_Key_Shift","Delete":"ui_Key_Delete","Enter":"ui_Key_Enter","Esc":"ui_Key_Esc","Menu":"ui_Key_Menu","Home":"ui_Key_Home","End":"ui_Key_End","PgUp":"ui_Key_PgUp","PgDn":"ui_Key_PgDn","Tab":"ui_Key_Tab","Scroll":"ui_Key_Scroll","Click":"ui_Key_Click","or":"ui_Key_Or","Wheel on view":"ui_Key_WheelView","Wheel on logo":"ui_Key_WheelLogo","Middle drag":"ui_Key_MiddleDrag","Space + drag":"ui_Key_SpaceDrag","Accent color":"ui_extra_0","Red":"ui_extra_1","Orange":"ui_extra_2","Green":"ui_extra_3","Teal":"ui_extra_4","Blue":"ui_extra_5","Purple":"ui_extra_6","Yellow":"ui_accent_yellow","Magenta":"ui_accent_magenta","click me":"ui_click_me","KillerMCP on GitHub":"mcp_gh","Previous feature":"ui_extra_7","Next feature":"ui_extra_8","Choose a feature":"ui_extra_9","Expanded KillerPDF screenshot":"ui_extra_10","version":"ui_extra_11","released":"ui_extra_12","size":"ui_extra_13","platform":"ui_extra_14","BASE":"ui_extra_15","Choose theme":"ui_Theme","High Contrast":"ui_Theme_Black","KillerPDF features":"features_h"};
  labelKeys["Get KillerPDF from the Microsoft Store"] = "ui_store";
  labelKeys["Get KillerMCP from the Microsoft Store"] = "ui_mcp_store";
  labelKeys["KillerPDF home"] = "ui_home";
  function translateLabels(dict) {
    labelKeys['Corpus figures'] = 'corpus_stats_aria';
    function lookup(text) {
      var key = labelKeys[text];
      return key && dict[key] != null ? dict[key] : text;
    }
    document.querySelectorAll('[title], [aria-label], [alt]').forEach(function (node) {
      ['title', 'aria-label', 'alt'].forEach(function (attribute) {
        var saved = 'data-en-' + attribute;
        var original = node.getAttribute(saved) || node.getAttribute(attribute);
        if (!original || !labelKeys[original]) return;
        node.setAttribute(saved, original);
        node.setAttribute(attribute, lookup(original));
      });
    });
    document.querySelectorAll('.kbd-row .k').forEach(function (node) {
      var original = node.getAttribute('data-en-keys') || node.textContent;
      node.setAttribute('data-en-keys', original);
      var direct = lookup(original);
      node.textContent = direct !== original ? direct : original.replace(
        /\b(?:Ctrl|Shift|Alt|Delete|Enter|Escape|Menu|Home|End|PgUp|PgDn|Tab|Scroll|Click|or|previous)\b/g,
        function (token) {
          if (token === 'previous') return dict.kb_previous_result || token;
          return lookup(token === 'Escape' ? 'Esc' : token);
        });
    });
  }
  var englishTitle = document.title;
  function applyLang(lang) {
    if (LANGS.indexOf(lang) < 0) lang = 'en';
    root.setAttribute('lang', lang === 'zh' ? 'zh-Hant' : (lang === 'zh-cn' ? 'zh-Hans' : lang));
    var dict = (lang === 'en') ? EN : (I18N[lang] || {});
    var pageName = window.location.pathname.split('/').pop();
    var titleKey = pageName === 'help.html' ? 'nav_help' :
      (pageName === 'technical.html' ? 'nav_tech' : (pageName === 'about.html' ? 'nav_about' : null));
    if (lang === 'en') document.title = englishTitle;
    else if (titleKey && dict[titleKey]) document.title = 'KillerPDF | ' + dict[titleKey];
    else if (!pageName || pageName === 'index.html') document.title = 'KillerPDF';
    document.querySelectorAll('[data-i18n]').forEach(function (n) {
      var k = n.getAttribute('data-i18n');
      n.innerHTML = normalizeCurrentFacts(k, (dict && dict[k] != null) ? dict[k] : EN[k]);
    });
    translateLabels(dict);
    var mcpVersion = document.querySelector('[data-mcp-version]');
    if (mcpVersion) mcpVersion.textContent = (dict.ui_extra_11 || 'Version') + ' ' + mcpVersion.dataset.mcpVersion;
    langItems.forEach(function (b) { b.setAttribute('aria-pressed', b.dataset.lang === lang ? 'true' : 'false'); });
    if (langToggle) langToggle.innerHTML = FLAGS[lang] || FLAGS.en;
    try { localStorage.setItem('kpdf-lang', lang); } catch (e) {}
    document.dispatchEvent(new CustomEvent('kpdf-languagechange', { detail: { lang: lang } }));
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

  function escapeCode(value) {
    return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  function codeToken(kind, value) {
    return '<span class="syn-' + kind + '">' + escapeCode(value) + '</span>';
  }

  function codeBlockText(block) {
    var copy = block.cloneNode(true);
    [].forEach.call(copy.querySelectorAll('br'), function (br) {
      br.parentNode.replaceChild(document.createTextNode('\n'), br);
    });
    return copy.textContent.replace(/^\s*\n|\n\s*$/g, '');
  }

  function highlightCommand(source) {
    var pattern = /#[^\n]*|\/\/[^\n]*|'(?:''|[^'])*'|"(?:`.|[^"`])*"|\{(?:input|output)\}|\$[A-Za-z_][\w:]*(?:\.[A-Za-z_]\w*)?|--?[A-Za-z][\w-]*|(?:[A-Za-z]:\\|\.\\|\\\\)[^\s"']+|\b\d+(?:\.\d+)?\b|\b(?:powershell|pwsh|dotnet|winget|irm|Invoke-RestMethod|Set-ExecutionPolicy|PdfTool\.exe|benchmark_corpus\.ps1)\b|[=+*/<>]+/gi;
    var output = '', last = 0, match;
    while ((match = pattern.exec(source))) {
      output += escapeCode(source.slice(last, match.index));
      var value = match[0], kind = 'command';
      if (/^(?:#|\/\/)/.test(value)) kind = 'comment';
      else if (/^(?:'|")/.test(value)) kind = 'string';
      else if (/^(?:\{|\$)/.test(value)) kind = 'variable';
      else if (/^--?[A-Za-z]/.test(value)) kind = 'option';
      else if (/^\d/.test(value)) kind = 'number';
      else if (/^[=+*/<>]+$/.test(value)) kind = 'operator';
      else if (/^(?:[A-Za-z]:\\|\.\\|\\\\)/.test(value)) kind = 'string';
      output += codeToken(kind, value);
      last = pattern.lastIndex;
    }
    return output + escapeCode(source.slice(last));
  }

  function highlightFormula(source) {
    var pattern = /\/\/[^\n]*|\b\d+(?:\.\d+)?\b|\b(?:max|min|round|int|newZoom|oldZoom|oldHOff|oldVOff|cursorX|cursorY|viewportW|pageWidthPt|pageHeightPt|renderW|renderH|pdfW|pdfH|canvasX|canvasY|dpiScaleX|dpiScaleY|zoom|ratio|scaledMax|GridZoomForN|rdW|rdH|sx|sy|newHOff|newVOff)\b|[=+*/<>-]+/g;
    var output = '', last = 0, match;
    while ((match = pattern.exec(source))) {
      output += escapeCode(source.slice(last, match.index));
      var value = match[0], kind;
      if (/^\/\//.test(value)) kind = 'comment';
      else if (/^\d/.test(value)) kind = 'number';
      else if (/^[=+*/<>-]+$/.test(value)) kind = 'operator';
      else if (/^(?:max|min|round|int|GridZoomForN)$/.test(value)) kind = 'command';
      else kind = 'variable';
      output += codeToken(kind, value);
      last = pattern.lastIndex;
    }
    return output + escapeCode(source.slice(last));
  }

  function highlightStaticCodeBlocks() {
    [].forEach.call(document.querySelectorAll('.codeblock, .code-block'), function (block) {
      if (block.querySelector('[class^="tok-"],[class*=" tok-"],[class^="syntax-"],[class*=" syntax-"]')) return;
      var keys = [].map.call(block.querySelectorAll('.cm[data-i18n]'), function (n) { return n.getAttribute('data-i18n'); });
      var source = codeBlockText(block);
      var formula = /\b(?:maxDim|scaledMax|newHOff|GridZoomForN|canvasX)\b/.test(source);
      block.innerHTML = formula ? highlightFormula(source) : highlightCommand(source);
      // Comments keep their translation key through the rewrite, so a later language change still reaches them.
      [].forEach.call(block.querySelectorAll('.syn-comment'), function (n, i) { if (keys[i]) n.setAttribute('data-i18n', keys[i]); });
    });
  }

  window.setupOutlineNavigation = function () {
    var nav = document.querySelector('.outline');
    var scroller = document.querySelector('.content-scroll');
    if (!nav || !scroller) return;
    var entries = [].slice.call(nav.querySelectorAll('a[href^="#"]')).map(function (link) {
      return { link: link, section: document.getElementById(link.getAttribute('href').slice(1)) };
    }).filter(function (entry) { return entry.section; });
    var pinned = null;
    function select(entry) {
      entries.forEach(function (item) { item.link.classList.toggle('on', item === entry); });
      if (entry && window.knOutlineReveal) window.knOutlineReveal(entry.link);
    }
    function sync() {
      if (pinned) { select(pinned); return; }
      var readingTop = scroller.getBoundingClientRect().top + 100;
      var best = entries[0], nearest = -Infinity;
      entries.forEach(function (entry) {
        var top = entry.section.getBoundingClientRect().top;
        if (top <= readingTop && top > nearest) { best = entry; nearest = top; }
      });
      select(best);
    }
    entries.forEach(function (entry) {
      entry.link.addEventListener('click', function () { pinned = entry; select(entry); });
    });
    function releasePin() { pinned = null; }
    scroller.addEventListener('wheel', releasePin, { passive: true });
    scroller.addEventListener('touchmove', releasePin, { passive: true });
    scroller.addEventListener('pointerdown', function (event) {
      if (event.clientX >= scroller.getBoundingClientRect().right - 18) releasePin();
    });
    window.addEventListener('keydown', function (event) {
      if (/^(ArrowUp|ArrowDown|PageUp|PageDown|Home|End| )$/.test(event.key)) releasePin();
    });
    function hashSelection() {
      pinned = entries.filter(function (entry) { return '#' + entry.section.id === location.hash; })[0] || null;
      sync();
    }
    window.addEventListener('hashchange', hashSelection);
    scroller.addEventListener('scroll', sync, { passive: true });
    hashSelection();
  };

  // ---- Init ----
  var savedTheme = localPreviewSetting('theme'), savedAccent = localPreviewSetting('accent'), savedLang = 'en';
  try { savedTheme = savedTheme || localStorage.getItem('kpdf-theme'); } catch (e) {}
  try { savedAccent = savedAccent || localStorage.getItem('kpdf-accent'); } catch (e) {}
  savedTheme = savedTheme || 'dark';
  if (savedTheme === 'trashpolka' && !localPreviewSetting('accent')) {
    try { savedAccent = localStorage.getItem(TP_ACCENT_KEY); } catch (e) { savedAccent = null; }
    savedAccent = savedAccent || 'red';
  }
  savedAccent = savedAccent || 'green';
  try { savedLang = localStorage.getItem('kpdf-lang') || 'en'; } catch (e) {}
  curAccent = savedAccent;
  setTheme(savedTheme);
  applyLang(savedLang);
  highlightStaticCodeBlocks();
})();
/* Top bar fit: the bar never wraps. When it runs out of room, the Microsoft Store and KillerMCP
   buttons shrink to their logos first, then the navigation links move into a More menu from the
   end, then the Download button drops its label, and last the Store and KillerMCP buttons join
   the menu. Re-measured on resize, font load and language changes. */
(function () {
  var bar = document.querySelector('.topbar');
  if (!bar || !window.MutationObserver) return;
  var nav = bar.querySelector('.tb-nav');
  var more = document.createElement('div');
  more.className = 'tb-more';
  more.hidden = true;
  var btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'tb-more-toggle';
  btn.setAttribute('aria-haspopup', 'true');
  btn.setAttribute('aria-expanded', 'false');
  function labelMore(lang) {
    lang = lang === 'zh-Hant' ? 'zh' : lang === 'zh-Hans' ? 'zh-cn' : lang;
    var dict = window.I18N && window.I18N[lang];
    btn.setAttribute('aria-label', dict && dict.ui_more ? dict.ui_more : 'More');
  }
  labelMore(document.documentElement.getAttribute('lang'));
  document.addEventListener('kpdf-languagechange', function (e) { labelMore(e.detail && e.detail.lang); });
  btn.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>';
  var menu = document.createElement('div');
  menu.className = 'lang-menu tb-more-menu';
  menu.hidden = true;
  more.appendChild(btn);
  more.appendChild(menu);
  if (nav && nav.parentNode === bar) bar.insertBefore(more, nav.nextSibling); else bar.appendChild(more);

  function mark(el) {
    if (!el) return null;
    var m = document.createComment('tb-slot');
    el.parentNode.insertBefore(m, el.nextSibling);
    return { el: el, slot: m };
  }
  var links = nav ? [].slice.call(nav.children).map(mark) : [];
  var extras = [bar.querySelector('.tb-mcp-link'), bar.querySelector('.tb-store')].filter(function (e) { return e && e.parentNode === bar; }).map(mark);
  var all = links.concat(extras);
  var busy = false;

  function over() { return bar.scrollWidth > bar.clientWidth + 1; }
  function close() { menu.hidden = true; btn.setAttribute('aria-expanded', 'false'); }
  function fit() {
    busy = true;
    close();
    all.forEach(function (m) { m.slot.parentNode.insertBefore(m.el, m.slot); });
    bar.classList.remove('tb-compact', 'tb-dl-icon');
    more.hidden = true;
    if (over()) bar.classList.add('tb-compact');
    if (over()) {
      more.hidden = false;
      for (var i = links.length - 1; i >= 0 && over(); i--) menu.insertBefore(links[i].el, menu.firstChild);
      if (over()) bar.classList.add('tb-dl-icon');
      for (var j = 0; j < extras.length && over(); j++) menu.appendChild(extras[j].el);
      if (!menu.children.length) more.hidden = true;
    }
    observer.takeRecords();
    busy = false;
  }
  var queued = false;
  function queue() {
    if (queued) return;
    queued = true;
    requestAnimationFrame(function () { queued = false; fit(); });
  }
  var observer = new MutationObserver(function () { if (!busy) queue(); });
  observer.observe(bar, { subtree: true, childList: true, characterData: true });
  window.addEventListener('resize', queue);
  // Logos, flags and fonts change widths without a DOM change, so watch every item's size too.
  if (window.ResizeObserver) {
    var sizes = new ResizeObserver(function () { if (!busy) queue(); });
    sizes.observe(bar);
    [].forEach.call(bar.children, function (c) { sizes.observe(c); });
  }
  window.addEventListener('load', queue);
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(queue);
  btn.addEventListener('click', function (e) {
    e.stopPropagation();
    var open = menu.hidden;
    menu.hidden = !open;
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
  });
  document.addEventListener('click', function (e) { if (!menu.hidden && !more.contains(e.target)) close(); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !menu.hidden) { close(); btn.focus(); } });
  fit();
})();
/* Film Grain: the same switch as the app's theme menu and KillerTools, on the family Alt+Shift+G chord.
   On by default; off sets data-grain="off", which empties --grain on every grained surface. */
(function () {
  'use strict';
  var root = document.documentElement;
  var KEY = 'film-grain';
  var LABELS = {
    en: 'Film Grain', bn: 'ফিল্ম গ্রেইন', cs: 'Filmové zrno', de: 'Filmkorn', es: 'Grano de película',
    fr: 'Grain de film', hu: 'Filmszemcse', it: 'Grana pellicola', ja: 'フィルムグレイン',
    kk: 'Пленка түйіршігі', nb: 'Filmkorn', no: 'Filmkorn', pl: 'Ziarno filmowe', pt: 'Granulação de filme',
    ru: 'Зернистость плёнки', sl: 'Filmska zrnatost', tr: 'Film greni', uk: 'Зернистість плівки',
    vi: 'Hạt phim', sv: 'Filmkorn', ko: '필름 그레인', 'zh-hant': '底片顆粒', 'zh-hans': '胶片颗粒'
  };
  var box = null, text = null;
  function apply(on) {
    root.setAttribute('data-grain', on ? 'on' : 'off');
    if (box) box.checked = on;
  }
  function set(on) {
    apply(on);
    try { localStorage.setItem(KEY, on ? 'on' : 'off'); } catch (e) {}
  }
  function label() {
    var lang = (root.getAttribute('lang') || 'en').toLowerCase();
    if (lang.indexOf('zh') === 0) lang = /hant|tw|hk/.test(lang) ? 'zh-hant' : 'zh-hans';
    else lang = lang.split('-')[0];
    return LABELS[lang] || LABELS.en;
  }
  function localize() {
    if (!text) return;
    text.textContent = label();
    text.parentNode.title = label() + ' (Alt+Shift+G)';
  }
  var saved = 'on';
  try { saved = localStorage.getItem(KEY) || 'on'; } catch (e) {}
  apply(saved !== 'off');

  var group = document.querySelector('.topbar .tgrp');
  if (group) {
    var sep = document.createElement('div');
    sep.className = 'tgrp-sep';
    sep.setAttribute('aria-hidden', 'true');
    var row = document.createElement('label');
    row.className = 'tgrp-grain';
    box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = saved !== 'off';
    box.addEventListener('change', function () { set(box.checked); });
    text = document.createElement('span');
    row.appendChild(box);
    row.appendChild(text);
    group.appendChild(sep);
    group.appendChild(row);
    localize();
    new MutationObserver(localize).observe(root, { attributes: true, attributeFilter: ['lang'] });
  }
  document.addEventListener('keydown', function (e) {
    if (e.altKey && e.shiftKey && !e.ctrlKey && !e.metaKey && e.code === 'KeyG') {
      e.preventDefault();
      if (!e.repeat) set(root.getAttribute('data-grain') === 'off');
    }
  });
})();
