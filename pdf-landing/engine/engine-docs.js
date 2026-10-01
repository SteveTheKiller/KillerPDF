(function () {
  'use strict';

  var root = document.documentElement;
  var docId = document.body.getAttribute('data-doc') || 'overview';
  var lang = 'en';
  try { lang = localStorage.getItem('kpdf-lang') || 'en'; } catch (e) {}
  var L10N = null;
  var pages = [
    { id: 'overview', title: 'Overview', file: 'index.html', group: 'Engine' },
    { id: 'getting-started', title: 'Getting started', file: 'getting-started.html', group: 'Start here' },
    { id: 'reading-pdfs', title: 'Reading PDFs', file: 'reading-pdfs.html', group: 'Work with documents' },
    { id: 'creating-pdfs', title: 'Creating PDFs', file: 'creating-pdfs.html', group: 'Work with documents' },
    { id: 'editing-pdfs', title: 'Editing PDFs', file: 'editing-pdfs.html', group: 'Work with documents' },
    { id: 'validation', title: 'Validation', file: 'validation.html', group: 'Standards and security' },
    { id: 'signing-encryption', title: 'Signing and encryption', file: 'signing-encryption.html', group: 'Standards and security' },
    { id: 'api', title: 'API guide', file: 'api.html', group: 'Reference' },
    { id: 'architecture', title: 'Architecture', file: 'architecture.html', group: 'Reference' }
  ];

  function esc(value) {
    return String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  function token(kind, value) {
    return '<span class="syntax-' + kind + '">' + esc(value) + '</span>';
  }

  function highlightCode(source, language) {
    var lang = String(language || '').toLowerCase();
    if (lang === 'xml' || lang === 'html') {
      return esc(source).replace(/(&lt;!--[\s\S]*?--&gt;)|(&lt;\/?)([A-Za-z_:][\w:.-]*)([\s\S]*?)(\/?&gt;)/g, function (_, comment, open, name, attributes, close) {
        if (comment) return '<span class="syntax-comment">' + comment + '</span>';
        attributes = attributes.replace(/([A-Za-z_:][\w:.-]*)(\s*=\s*)(&quot;[^&]*?&quot;|'[^']*')/g, '<span class="syntax-attr">$1</span>$2<span class="syntax-string">$3</span>');
        return '<span class="syntax-punctuation">' + open + '</span><span class="syntax-tag">' + name + '</span>' + attributes + '<span class="syntax-punctuation">' + close + '</span>';
      });
    }

    var csharpKeywords = /^(?:abstract|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|from|get|global|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|partial|private|protected|public|readonly|record|ref|required|return|sbyte|sealed|set|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|where|while|with|yield)$/;
    var powershellKeywords = /^(?:begin|break|catch|class|continue|data|do|dynamicparam|else|elseif|end|enum|exit|filter|finally|for|foreach|from|function|if|in|param|process|return|switch|throw|trap|try|until|using|while)$/i;
    var pattern = lang === 'powershell'
      ? /#[^\n]*|'(?:''|[^'])*'|"(?:`.|[^"`])*"|\$[A-Za-z_][\w:]*(?:\.[A-Za-z_]\w*)?|\b\d+(?:\.\d+)?\b|\b[A-Za-z_][\w-]*\b|[^\s]/g
      : /\/\*[\s\S]*?\*\/|\/\/[^\n]*|@?\$?"(?:""|\\.|[^"\\])*"|'(?:\\.|[^'\\])'|\b\d+(?:\.\d+)?(?:[fFdDmMuUlL]*)\b|\b[A-Za-z_]\w*\b|[^\s]/g;
    var output = '', last = 0, match;
    while ((match = pattern.exec(source))) {
      output += esc(source.slice(last, match.index));
      var value = match[0], kind = '';
      if (/^(?:\/\/|\/\*|#)/.test(value)) kind = 'comment';
      else if (/^(?:@?\$?"|')/.test(value)) kind = 'string';
      else if (lang === 'powershell' && /^\$/.test(value)) kind = 'variable';
      else if (/^\d/.test(value)) kind = 'number';
      else if ((lang === 'powershell' ? powershellKeywords : csharpKeywords).test(value)) kind = 'keyword';
      else if (lang === 'powershell' && /^[A-Za-z]+-[A-Za-z]/.test(value)) kind = 'function';
      else if (/^[A-Z][A-Za-z0-9_]*$/.test(value)) kind = 'type';
      output += kind ? token(kind, value) : esc(value);
      last = pattern.lastIndex;
    }
    return output + esc(source.slice(last));
  }

  function inline(value) {
    var code = [];
    var text = esc(value).replace(/`([^`]+)`/g, function (_, body) {
      code.push('<code>' + body + '</code>');
      return '\u0000' + (code.length - 1) + '\u0000';
    });
    text = text.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+|\/[^\s)]+|[^\s)]+\.html(?:#[^\s)]*)?)\)/g, '<a href="$2">$1</a>');
    text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    text = text.replace(/\*([^*]+)\*/g, '<em>$1</em>');
    return text.replace(/\u0000(\d+)\u0000/g, function (_, index) { return code[Number(index)]; });
  }

  function slug(value) {
    return value.toLowerCase().replace(/<[^>]*>/g, '').replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '');
  }

  function splitCells(line) {
    return line.trim().replace(/^\||\|$/g, '').split('|').map(function (cell) { return cell.trim(); });
  }

  function renderMarkdown(markdown) {
    var lines = markdown.replace(/\r\n?/g, '\n').split('\n');
    var html = [], i = 0, paragraph = [];
    function flushParagraph() {
      if (!paragraph.length) return;
      var cls = html.length === 0 ? ' class="doc-lede"' : '';
      html.push('<p' + cls + '>' + inline(paragraph.join(' ')) + '</p>');
      paragraph = [];
    }
    while (i < lines.length) {
      var line = lines[i];
      if (/^```/.test(line)) {
        flushParagraph();
        var language = line.slice(3).trim(), body = [];
        i++;
        while (i < lines.length && !/^```/.test(lines[i])) { body.push(lines[i]); i++; }
        html.push('<pre data-language="' + esc(language) + '"><code class="language-' + esc(language) + '">' + highlightCode(body.join('\n'), language) + '</code></pre>');
        i++;
        continue;
      }
      var heading = /^(#{1,3})\s+(.+)$/.exec(line);
      if (heading) {
        flushParagraph();
        var level = heading[1].length, label = heading[2], id = slug(label);
        html.push('<h' + level + ' id="' + id + '">' + inline(label) + '</h' + level + '>');
        i++;
        continue;
      }
      if (line.indexOf('|') >= 0 && i + 1 < lines.length && /^\s*\|?\s*:?-+/.test(lines[i + 1])) {
        flushParagraph();
        var headers = splitCells(line), rows = [];
        i += 2;
        while (i < lines.length && lines[i].indexOf('|') >= 0 && lines[i].trim()) { rows.push(splitCells(lines[i])); i++; }
        html.push('<table><thead><tr>' + headers.map(function (cell) { return '<th>' + inline(cell) + '</th>'; }).join('') + '</tr></thead><tbody>' + rows.map(function (row) { return '<tr>' + row.map(function (cell) { return '<td>' + inline(cell) + '</td>'; }).join('') + '</tr>'; }).join('') + '</tbody></table>');
        continue;
      }
      if (/^>\s?/.test(line)) {
        flushParagraph();
        var quote = [];
        while (i < lines.length && /^>\s?/.test(lines[i])) { quote.push(lines[i].replace(/^>\s?/, '')); i++; }
        html.push('<blockquote><p>' + inline(quote.join(' ')) + '</p></blockquote>');
        continue;
      }
      var unordered = /^\s*[-+]\s+(.+)$/.exec(line);
      var ordered = /^\s*\d+\.\s+(.+)$/.exec(line);
      if (unordered || ordered) {
        flushParagraph();
        var tag = unordered ? 'ul' : 'ol', items = [];
        while (i < lines.length) {
          var match = tag === 'ul' ? /^\s*[-+]\s+(.+)$/.exec(lines[i]) : /^\s*\d+\.\s+(.+)$/.exec(lines[i]);
          if (!match) break;
          items.push('<li>' + inline(match[1]) + '</li>');
          i++;
        }
        html.push('<' + tag + '>' + items.join('') + '</' + tag + '>');
        continue;
      }
      if (!line.trim()) flushParagraph(); else paragraph.push(line.trim());
      i++;
    }
    flushParagraph();
    return html.join('\n');
  }

  function themeButtons() {
    return ['dark','light','hc','blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning'].map(function (name) {
      return '<button class="swatch sw-' + name + '" data-theme="' + name + '" title="' + name.charAt(0).toUpperCase() + name.slice(1) + '"></button>';
    }).join('');
  }

  function navigation() {
    var groups = [], names = [];
    pages.forEach(function (page) {
      if (names.indexOf(page.group) < 0) { names.push(page.group); groups.push({ name: page.group, pages: [] }); }
      groups[names.indexOf(page.group)].pages.push(page);
    });
    return groups.map(function (group) {
      return '<div class="docs-nav-group"><span class="docs-nav-label">' + group.name + '</span>' + group.pages.map(function (page) {
        var active = page.id === docId ? ' class="on" aria-current="page"' : '';
        return '<a href="' + page.file + '" data-page="' + page.id + '"' + active + '>' + page.title + '</a>' + (page.id === docId ? '<div class="page-outline" id="pageOutline"></div>' : '');
      }).join('') + '</div>';
    }).join('');
  }

  function shell() {
    document.body.innerHTML = '<div class="topbar">' +
      '<a href="../index.html" class="tb-home" title="KillerPDF home"><img class="tb-icon" src="../kp-icon.png" alt="KillerPDF" width="44" height="44"><img class="wm-logo tb-wm" src="../brand/killerpdf-logo-dark-green.svg" alt="KillerPDF"></a>' +
      '<a class="tb-dl" href="https://github.com/SteveTheKiller/KillerPDF/releases/latest/download/KillerPDF.exe"><svg viewBox="0 0 24 24"><path d="M11 4h2v7h3l-4 5-4-5h3V4zM5 18h14v2H5z"></path></svg><span data-i18n="nav_dl">Download</span></a>' +
      '<span class="tb-spacer"></span><nav class="tb-nav"><a href="../help.html">Help</a><a href="../technical.html">Technical</a><a href="./index.html" class="on">Engine</a><a href="../corpus.html">Corpus</a><a href="../about.html">About</a></nav>' +
      '<div class="tgrp" role="group" aria-label="Theme">' + themeButtons() + '</div>' +
      '<div class="acc-fly accent-switch" id="accentSwitch"><button class="acc-toggle" id="accentToggle" aria-haspopup="true" aria-expanded="false" title="Accent color"></button><div class="acc-pop" id="accentPop" hidden role="group" aria-label="Accent color"><span class="acc-pop-label">accent:</span>' +
      '<button class="acc" data-accent="red" style="background:#DD504B;color:#DD504B" title="Red"></button><button class="acc" data-accent="orange" style="background:#E8962C;color:#E8962C" title="Orange"></button><button class="acc" data-accent="yellow" style="background:#EAD900;color:#EAD900" title="Yellow"></button><button class="acc" data-accent="green" style="background:#1EA54C;color:#1EA54C" title="Green"></button><button class="acc" data-accent="teal" style="background:#1FB8A8;color:#1FB8A8" title="Teal"></button><button class="acc" data-accent="blue" style="background:#50AEE8;color:#50AEE8" title="Blue"></button><button class="acc" data-accent="purple" style="background:#B982E3;color:#B982E3" title="Purple"></button><button class="acc" data-accent="magenta" style="background:#FF52C9;color:#FF52C9" title="Magenta"></button></div></div></div>' +
      '<div class="shell engine-shell"><aside class="sidebar engine-sidebar"><div class="sb-tabs"><span class="on">Developer guide</span></div><nav class="docs-nav" aria-label="Engine documentation">' + navigation() + '</nav></aside>' +
      '<main class="content"><div class="content-scroll"><div class="engine-doc-wrap"><div id="engineHero"></div><article class="engine-doc" id="engineDoc"><p>Loading documentation...</p></article><nav class="doc-pager" id="docPager" aria-label="Documentation pages"></nav></div></div></main></div>' +
      '<footer class="statusbar"><span class="left"><a href="https://github.com/SteveTheKiller/KillerPDF/tree/main/engine">Engine source</a> &middot; <a href="https://www.nuget.org/packages/KillerPdf.Engine">NuGet</a> &middot; GPLv3</span><span class="right"><span id="verEgg">v1.8.2</span> &middot; &copy; 2026 <b><a href="https://thekiller.net">Steve the Killer</a></b></span></footer>';
  }

  function hero() {
    if (docId !== 'overview') return;
    document.getElementById('engineHero').innerHTML = '<div class="engine-doc-hero"><img class="engine-doc-mark" src="../killerpdf-engine-icon.png" alt=""><h1 class="engine-doc-brand">The KillerPDF<span class="accent">.Engine</span></h1></div>';
  }

  function pager() {
    var labels = (L10N && L10N.ui) || {};
    var index = pages.findIndex(function (page) { return page.id === docId; });
    var previous = index > 0 ? pages[index - 1] : null;
    var next = index >= 0 && index < pages.length - 1 ? pages[index + 1] : null;
    var html = previous ? '<a href="' + previous.file + '"><small>' + (labels.previous || 'Previous') + '</small><span>' + previous.title + '</span></a>' : '<span></span>';
    html += next ? '<a href="' + next.file + '"><small>' + (labels.next || 'Next') + '</small><span>' + next.title + '</span></a>' : '<span></span>';
    document.getElementById('docPager').innerHTML = html;
  }

  function outline() {
    var container = document.getElementById('pageOutline');
    if (!container) return;
    var headings = [].slice.call(document.querySelectorAll('#engineDoc h2'));
    container.innerHTML = headings.map(function (heading) { return '<a href="#' + heading.id + '">' + heading.textContent + '</a>'; }).join('');
  }

  function loadSharedScript(source) {
    var script = document.createElement('script');
    script.async = false;
    script.src = source;
    document.body.appendChild(script);
  }

  function normalizeCurrentFacts(markdown) {
    if (docId === 'overview') {
      return markdown
        .replace(/1\.8\.0/g, '1.8.2')
        .replace(/47([\s.,\u00A0]?)725/g, function (_, sep) { return '47' + sep + '792'; })
        .replace(/\b1([\s.,\u00A0]?)436\b/g, function (_, sep) { return '1' + sep + '437'; })
        .replace(/\b2([\s.,\u00A0]?)907\b/g, function (_, sep) { return '47' + sep + '024'; });
    }
    if (docId === 'getting-started') return markdown.replace(/1\.8\.0/g, '1.8.2');
    if (docId === 'validation') return markdown.replace(/\b1([\s.,\u00A0]?)436\b/g,
      function (_, sep) { return '1' + sep + '437'; });
    return markdown;
  }

  function showMarkdown(markdown) {
    markdown = normalizeCurrentFacts(markdown);
    document.getElementById('engineDoc').innerHTML = renderMarkdown(markdown);
    if (docId === 'overview') {
      var repeatedTitle = document.querySelector('#engineDoc > h1:first-child');
      if (repeatedTitle) repeatedTitle.remove();
    }
    outline();
  }

  function loadLocalMarkdown() {
    var frame = document.createElement('iframe');
    frame.hidden = true;
    frame.src = './docs/' + docId + '.md';
    frame.onload = function () {
      try {
        showMarkdown(frame.contentDocument.body.textContent || '');
      } catch (_) {
        showLoadError();
      }
      frame.remove();
    };
    frame.onerror = showLoadError;
    document.body.appendChild(frame);
  }

  function showLoadError() {
    var ui = (L10N && L10N.ui) || {};
    document.getElementById('engineDoc').innerHTML = '<div class="doc-error"><strong>' + (ui.loadErrorTitle || 'The guide could not be loaded.') + '</strong><p>' + (ui.loadErrorBody || 'Open this page through the KillerPDF website or a local web server.') + '</p></div>';
  }

  function applyDocsL10n() {
    var l10n = window.KPDF_ENGINE_DOCS_L10N;
    if (!l10n || l10n.lang !== lang) return;
    L10N = l10n;
    var ui = l10n.ui || {};
    pages.forEach(function (page) {
      if (ui.titles && ui.titles[page.id]) page.title = ui.titles[page.id];
      if (ui.groups && ui.groups[page.group]) page.group = ui.groups[page.group];
    });
    var docsNav = document.querySelector('.docs-nav');
    if (docsNav) docsNav.innerHTML = navigation();
    var sbTab = document.querySelector('.sb-tabs .on');
    if (sbTab && ui.devguide) sbTab.textContent = ui.devguide;
    if (ui.nav) {
      var navLinks = document.querySelectorAll('.tb-nav a');
      var navKeys = ['help', 'technical', 'engine', 'about'];
      [].forEach.call(navLinks, function (link, index) {
        if (ui.nav[navKeys[index]]) link.textContent = ui.nav[navKeys[index]];
      });
    }
    var sourceLink = document.querySelector('.statusbar .left a');
    if (sourceLink && ui.engineSource) sourceLink.textContent = ui.engineSource;
    if (l10n.docs && l10n.docs[docId]) showMarkdown(l10n.docs[docId]);
    pager();
  }

  function loadDocsL10n() {
    if (lang === 'en' || !/^[a-z]{2}(-[a-z]{2})?$/.test(lang)) return;
    var script = document.createElement('script');
    script.async = false;
    script.src = './docs-i18n/' + lang + '.js?v=1';
    script.onload = applyDocsL10n;
    document.body.appendChild(script);
  }

  shell();
  hero();
  pager();
  if (window.KPDF_ENGINE_DOCS && window.KPDF_ENGINE_DOCS[docId]) {
    showMarkdown(window.KPDF_ENGINE_DOCS[docId]);
  } else {
    fetch('./docs/' + docId + '.md', { credentials: 'same-origin' })
      .then(function (response) { if (!response.ok) throw new Error('HTTP ' + response.status); return response.text(); })
      .then(showMarkdown)
      .catch(function () {
        if (window.location.protocol === 'file:') loadLocalMarkdown(); else showLoadError();
      });
  }
  loadDocsL10n();
  loadSharedScript('../kp-i18n.js?v=9');
  loadSharedScript('../kp.js?v=12');
})(window);
