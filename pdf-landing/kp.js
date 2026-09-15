/* KillerPDF site - shared chrome behavior (theme, accent, language, easter egg).
   Page-specific behavior (sidebar thumbnails, accordions) stays inline per page. */
(function () {
  var root = document.documentElement;
  var scriptBase = document.currentScript ? new URL('.', document.currentScript.src).href : '/';
  var THEMES = ['dark','light','hc','blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning'];
  var NEUTRAL = ['dark','light','hc'];
  var THEMED = ['blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning'];  // fixed-color wordmark art
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
    ['dark', 'light', 'hc'].forEach(function (neutralTheme) {
      var preview = ACCENTS[neutralTheme][name];
      if (preview) document.querySelectorAll('.sw-' + neutralTheme).forEach(function (dot) {
        dot.style.setProperty('--sw-accent', preview[0]);
      });
    });
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
      var variant = (theme === 'light') ? 'light' : 'dark';
      var color = (NEUTRAL.indexOf(theme) >= 0) ? curAccent : 'green';
      src = scriptBase + 'brand/killerpdf-logo-' + variant + '-' + color + '.svg';
    }
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

  function normalizeCurrentFacts(key, value) {
    if (key === 'pa_31') return value.replace(/v1\.8\.1/g, 'v1.8.2');
    if (key !== 'pt_220') return value;
    return value
      .replace(/1\.8\.0/g, '1.8.2')
      .replace(/138([\s.,\u00A0]?)691/g, function (_, sep) { return '139' + sep + '519'; })
      .replace(/100([\s.,\u00A0]?)012/g, function (_, sep) { return '100' + sep + '609'; })
      .replace(/49([\s.,\u00A0]?)271/g, function (_, sep) { return '49' + sep + '711'; })
      .replace(/47([\s.,\u00A0]?)725/g, function (_, sep) { return '47' + sep + '792'; })
      .replace(/\b816\b/g, '906')
      .replace(/38([\s.,\u00A0]?)679/g, function (_, sep) { return '38' + sep + '910'; });
  }
  var LANGS = ['en','es','de','fr','ja','kk','ru','tr','zh','zh-cn','bn','cs','pl','hu','it'];
  var FLAGS = {
    en: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><g fill="#b22234"><rect width="24" height="1.85"/><rect y="3.7" width="24" height="1.85"/><rect y="7.4" width="24" height="1.85"/><rect y="11.1" width="24" height="1.85"/><rect y="14.8" width="24" height="1.85"/><rect y="18.5" width="24" height="1.85"/><rect y="22.2" width="24" height="1.8"/></g><rect width="11" height="12.95" fill="#3c3b6e"/></svg>',
    es: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#c60b1e"/><rect y="6" width="24" height="12" fill="#ffc400"/></svg>',
    de: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#000"/><rect y="8" width="24" height="8" fill="#dd0000"/><rect y="16" width="24" height="8" fill="#ffce00"/></svg>',
    fr: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#0055a4"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ef4135"/></svg>',
    ja: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><circle cx="12" cy="12" r="7" fill="#bc002d"/></svg>',
    kk: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#00afca"/><circle cx="12" cy="12" r="4.5" fill="#f6d34a"/><g stroke="#f6d34a" stroke-width="1"><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1"/></g></svg>',
    ru: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#fff"/><rect y="8" width="24" height="8" fill="#0039a6"/><rect y="16" width="24" height="8" fill="#d52b1e"/></svg>',
    tr: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#e30a17"/><circle cx="9.5" cy="12" r="5" fill="#fff"/><circle cx="11" cy="12" r="4" fill="#e30a17"/><polygon points="15.5,9.4 16.12,11.15 17.97,11.2 16.5,12.32 17.03,14.1 15.5,13.05 13.97,14.1 14.5,12.32 13.03,11.2 14.88,11.15" fill="#fff"/></svg>',
    zh: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fe0000"/><rect width="12" height="12" fill="#000095"/><polygon points="6,3 7.2,6.6 11,6.6 7.9,8.8 9.1,12.4 6,10.2 2.9,12.4 4.1,8.8 1,6.6 4.8,6.6" fill="#fff"/></svg>',
    'zh-cn': '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#de2910"/><polygon points="4,3 4.9,5.6 7.6,5.6 5.4,7.3 6.2,9.9 4,8.3 1.8,9.9 2.6,7.3 0.4,5.6 3.1,5.6" fill="#ffde00"/></svg>',
    bn: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#006a4e"/><circle cx="10.5" cy="12" r="6" fill="#f42a41"/></svg>',
    cs: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#d7141a"/><polygon points="0,0 12,12 0,24" fill="#11457e"/></svg>',
    pl: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#dc143c"/></svg>',
    hu: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#ce2939"/><rect y="8" width="24" height="8" fill="#fff"/><rect y="16" width="24" height="8" fill="#477050"/></svg>',
    it: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#009246"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ce2b37"/></svg>'
  };
  var langItems = [].slice.call(document.querySelectorAll('.lang-item'));
  var langToggle = document.getElementById('langToggle');
  var langMenu = document.getElementById('langMenu');

  var labelKeys = {"Theme":"ui_Theme","Language":"ui_Language","Dark":"ui_Theme_Dark","Light":"ui_Theme_Light","Black":"ui_Theme_Black","Blood":"ui_Theme_Blood","Greed":"ui_Theme_Greed","Cyanotic":"ui_Theme_Cyanotic","98SE":"ui_Theme_98SE","Ectoplasm":"ui_Theme_Ectoplasm","Decay":"ui_Theme_Decay","Mourning":"ui_Theme_Mourning","Sepulchre":"ui_Theme_Sepulchre","Delirium":"ui_Theme_Delirium","Malaise":"ui_Theme_Malaise","Close":"ui_Lbl_Close","Ctrl":"ui_Key_Ctrl","Alt":"ui_Key_Alt","Shift":"ui_Key_Shift","Delete":"ui_Key_Delete","Enter":"ui_Key_Enter","Esc":"ui_Key_Esc","Menu":"ui_Key_Menu","Home":"ui_Key_Home","End":"ui_Key_End","PgUp":"ui_Key_PgUp","PgDn":"ui_Key_PgDn","Tab":"ui_Key_Tab","Scroll":"ui_Key_Scroll","Click":"ui_Key_Click","or":"ui_Key_Or","Wheel on view":"ui_Key_WheelView","Wheel on logo":"ui_Key_WheelLogo","Middle drag":"ui_Key_MiddleDrag","Space + drag":"ui_Key_SpaceDrag","Accent color":"ui_extra_0","Red":"ui_extra_1","Orange":"ui_extra_2","Green":"ui_extra_3","Teal":"ui_extra_4","Blue":"ui_extra_5","Purple":"ui_extra_6","Previous feature":"ui_extra_7","Next feature":"ui_extra_8","Choose a feature":"ui_extra_9","Expanded KillerPDF screenshot":"ui_extra_10","version":"ui_extra_11","released":"ui_extra_12","size":"ui_extra_13","platform":"ui_extra_14","BASE":"ui_extra_15","Choose theme":"ui_Theme","High Contrast":"ui_Theme_Black","KillerPDF features":"features_h"};
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
      (pageName === 'technical.html' ? 'nav_tech' : null);
    if (lang === 'en') document.title = englishTitle;
    else if (titleKey && dict[titleKey]) document.title = 'KillerPDF | ' + dict[titleKey];
    else if (!pageName || pageName === 'index.html') document.title = 'KillerPDF';
    document.querySelectorAll('[data-i18n]').forEach(function (n) {
      var k = n.getAttribute('data-i18n');
      n.innerHTML = normalizeCurrentFacts(k, (dict && dict[k] != null) ? dict[k] : EN[k]);
    });
    translateLabels(dict);
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
      var source = codeBlockText(block);
      var formula = /\b(?:maxDim|scaledMax|newHOff|GridZoomForN|canvasX)\b/.test(source);
      block.innerHTML = formula ? highlightFormula(source) : highlightCommand(source);
    });
  }

  // ---- Init ----
  var savedTheme = localPreviewSetting('theme'), savedAccent = localPreviewSetting('accent'), savedLang = 'en';
  try { savedTheme = savedTheme || localStorage.getItem('kpdf-theme'); } catch (e) {}
  try { savedAccent = savedAccent || localStorage.getItem('kpdf-accent'); } catch (e) {}
  savedTheme = savedTheme || 'dark';
  savedAccent = savedAccent || 'green';
  try { savedLang = localStorage.getItem('kpdf-lang') || 'en'; } catch (e) {}
  curAccent = savedAccent;
  setTheme(savedTheme);
  applyLang(savedLang);
  highlightStaticCodeBlocks();
})();
