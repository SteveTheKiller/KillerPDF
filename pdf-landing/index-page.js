(function () {
  // Feature carousel. The original translated headings and descriptions remain in the document,
  // so language changes continue to use the shared I18N dictionary without duplicating content.
  var featureCarousel = document.querySelector('.feature-carousel');
  if (featureCarousel) {
    var featureCards = Array.prototype.slice.call(featureCarousel.querySelectorAll('.feature-card'));
    var featureRail = featureCarousel.querySelector('.feature-rail');
    var featureCount = featureCarousel.querySelector('.feature-count');
    var featureIndex = 0;
    var featureTabs = [];

    featureCards.forEach(function (card, index) {
      var heading = card.querySelector('h3');
      var copy = document.createElement('div');
      var number = document.createElement('div');
      var tab = document.createElement('button');
      while (card.firstChild) copy.appendChild(card.firstChild);
      number.className = 'feature-number';
      number.setAttribute('aria-hidden', 'true');
      number.textContent = String(index + 1).padStart(2, '0');
      copy.className = 'feature-copy';
      card.appendChild(number);
      card.appendChild(copy);
      card.id = 'feature-slide-' + index;
      card.setAttribute('role', 'tabpanel');
      card.setAttribute('aria-roledescription', 'slide');
      card.setAttribute('aria-labelledby', 'feature-tab-' + index);

      tab.className = 'feature-tab';
      tab.type = 'button';
      tab.id = 'feature-tab-' + index;
      tab.setAttribute('role', 'tab');
      tab.setAttribute('aria-controls', card.id);
      tab.textContent = heading ? heading.textContent : ('Feature ' + (index + 1));
      tab.title = tab.textContent;
      tab.setAttribute('aria-label', tab.textContent);
      tab.addEventListener('click', function () { showFeature(index, true); });
      featureRail.appendChild(tab);
      featureTabs.push(tab);
    });

    function refreshFeatureLabels() {
      featureCards.forEach(function (card, index) {
        var heading = card.querySelector('h3');
        if (heading) {
          featureTabs[index].textContent = heading.textContent;
          featureTabs[index].title = heading.textContent;
          featureTabs[index].setAttribute('aria-label', heading.textContent);
        }
      });
    }
    function showFeature(index, focusTab) {
      featureIndex = (index + featureCards.length) % featureCards.length;
      featureCards.forEach(function (card, i) {
        var active = i === featureIndex;
        card.classList.toggle('is-active', active);
        card.hidden = !active;
        featureTabs[i].setAttribute('aria-selected', active ? 'true' : 'false');
        featureTabs[i].tabIndex = active ? 0 : -1;
      });
      featureCount.textContent = String(featureIndex + 1).padStart(2, '0') + ' / ' + String(featureCards.length).padStart(2, '0');
      if (focusTab) featureTabs[featureIndex].focus({ preventScroll: true });
    }
    featureCarousel.querySelector('.feature-prev').addEventListener('click', function () { showFeature(featureIndex - 1, false); });
    featureCarousel.querySelector('.feature-next').addEventListener('click', function () { showFeature(featureIndex + 1, false); });
    featureCarousel.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowLeft') { showFeature(featureIndex - 1, true); e.preventDefault(); }
      else if (e.key === 'ArrowRight') { showFeature(featureIndex + 1, true); e.preventDefault(); }
      else if (e.key === 'Home') { showFeature(0, true); e.preventDefault(); }
      else if (e.key === 'End') { showFeature(featureCards.length - 1, true); e.preventDefault(); }
    });
    document.addEventListener('kpdf-languagechange', refreshFeatureLabels);
    showFeature(0, false);
  }

  var SHOTS = [
    { key: 'shot_195_22', src: 'screenshots/22.png', desc: 'The Trash Polka theme with the Blood accent' },
    { key: 'shot_19_20', src: 'screenshots/20.png', desc: 'Two-column page thumbnails in the sidebar with the Dark theme' },
    { key: 'shot_193_21', src: 'screenshots/21.png', desc: 'The Document tools panel in the sidebar with the Blue accent' },
    { key: 'shot_19_02', src: 'screenshots/02.png', desc: 'Highlights and a pen circle drawn on a page' },
    { key: 'shot_19_03', src: 'screenshots/03.png', desc: 'Side-by-side comparison of two versions of a guide with the changes marked' },
    { key: 'shot_1890_04', src: 'screenshots/04.png', desc: 'Two documents side by side in dual pane view with the Blood theme' },
    { key: 'shot_19_05', src: 'screenshots/05.png', desc: 'Distance and area measurements on a page' },
    { key: 'shot_1890_06', src: 'screenshots/06.png', desc: 'OCR menu with downloadable recognition languages' },
    { key: 'shot_1890_07', src: 'screenshots/07.png', desc: 'Stamp Pages preview with watermark and page number controls' },
    { key: 'shot_183_02', src: 'screenshots/08.png', desc: 'Transform preview with rotation, scaling, flip, and skew controls' },
    { key: 'shot_19_09', src: 'screenshots/09.png', desc: 'Filling in a PDF form' },
    { key: 'shot_19_10', src: 'screenshots/10.png', desc: 'Signing a PDF with a digital certificate' },
    { key: 'shot_1890_10', src: 'screenshots/12.png', desc: 'Grid view with the Black theme' },
    { key: 'shot_19_13', src: 'screenshots/13.png', desc: 'A form in the 98SE theme' },
    { key: 'shot_19_14', src: 'screenshots/14.png', desc: 'The outline in a sidebar on the right with the Delirium theme' },
    { key: 'shot_19_15', src: 'screenshots/15.png', desc: 'The page strip docked at the top of the window with the Greed theme' },
    { key: 'shot_19_16', src: 'screenshots/16.png', desc: 'The page strip docked at the bottom of the window with the Ectoplasm theme' },
    { key: 'shot_1890_13', src: 'screenshots/18.png', desc: 'Two-Page view in the Brazilian Portuguese interface' },
    { key: 'shot_1890_14', src: 'screenshots/19.png', desc: 'Keyboard shortcuts overlay' }
  ];
  // EVERY file in screenshots/ belongs in the list above - the strip is this array, not the
  // folder, so a new .png dropped in the folder is invisible until it is added here.

  // These captions cannot use data-i18n: that mechanism swaps innerHTML on a node, and a caption
  // has to land in alt, title and aria-label as well. So the shot's key is resolved against the
  // same I18N dictionary by hand, with the English text above as the fallback - which is also
  // what English itself uses, so a missing key degrades to English rather than to a bare key.
  function shotText(shot) {
    var lang = document.documentElement.getAttribute('lang') || 'en';
    if (lang === 'zh-Hant') lang = 'zh';
    else if (lang === 'zh-Hans') lang = 'zh-cn';
    var dict = (window.I18N && window.I18N[lang]) || null;
    return (dict && dict[shot.key] != null) ? dict[shot.key] : shot.desc;
  }
  var sbThumbs = document.getElementById('sbThumbs');
  var lightbox = document.getElementById('lightbox');
  var lightboxImg = document.getElementById('lightboxImg');
  var lightboxCaption = document.getElementById('lightboxCaption');
  var lastLightboxTrigger = null;

  // Build the sidebar screenshot thumbnails (scrollable, like the app). Click = pop out.
  SHOTS.forEach(function (shot) {
    var b = document.createElement('button');
    var text = shotText(shot);
    b.className = 'sb-thumb'; b.title = text;
    b.setAttribute('aria-label', text);
    // The caption is re-read on every language change, so the element carries its key.
    b.dataset.shotKey = shot.key;
    b.innerHTML = '<img src="' + shot.src + '" alt="" loading="lazy">';
    var alt = b.querySelector('img');
    // alt is set as a property rather than interpolated into the markup above: a translated
    // caption can contain a quote or an angle bracket, which would break out of the attribute.
    if (alt) alt.alt = text;
    var tim = b.querySelector('img');
    if (tim) tim.title = text;
    b.addEventListener('click', function () { openLightbox(shot, b); });
    var im = b.querySelector('img');
    if (im) { im.addEventListener('load', updateFade); im.addEventListener('error', updateFade); }
    sbThumbs.appendChild(b);
  });

  // Fade the bottom of the strip while it overflows and isn't scrolled to the last shot,
  // matching the app sidebar (no fade once the last screenshot is in view).
  var fadeTop = document.querySelector('.sb-fade-top');
  var fadeBottom = document.querySelector('.sb-fade-bottom');
  function updateFade() {
    var scrolledDown = sbThumbs.scrollTop > 2;
    var more = sbThumbs.scrollHeight - sbThumbs.clientHeight - sbThumbs.scrollTop > 2;
    if (fadeTop) fadeTop.classList.toggle('on', scrolledDown);
    if (fadeBottom) fadeBottom.classList.toggle('on', more);
  }
  sbThumbs.addEventListener('scroll', updateFade, { passive: true });
  window.addEventListener('resize', updateFade);
  updateFade();

  var openShot = null;   // which shot the lightbox is showing, so it can be re-captioned

  function openLightbox(shot, trigger) {
    lastLightboxTrigger = trigger;
    openShot = shot;
    var text = shotText(shot);
    lightboxImg.src = shot.src;
    lightboxImg.alt = text;
    lightboxImg.title = text;
    lightboxCaption.textContent = text;
    lightbox.setAttribute('aria-hidden', 'false');
    lightbox.classList.add('show');
    lightbox.focus();
  }

  // kp.js fires this after it has swapped every data-i18n node and set the root lang attribute,
  // so shotText reads the new language. Covers the thumbnails and the lightbox if it is open -
  // switching language with a screenshot expanded would otherwise leave its caption in the old
  // one until it was closed and reopened.
  document.addEventListener('kpdf-languagechange', function () {
    var byKey = {};
    SHOTS.forEach(function (s) { byKey[s.key] = s; });
    sbThumbs.querySelectorAll('.sb-thumb').forEach(function (b) {
      var shot = byKey[b.dataset.shotKey];
      if (!shot) return;
      var text = shotText(shot);
      b.title = text;
      b.setAttribute('aria-label', text);
      var im = b.querySelector('img');
      if (im) { im.alt = text; im.title = text; }
    });
    if (openShot) {
      var t = shotText(openShot);
      lightboxImg.alt = t;
      lightboxImg.title = t;
      lightboxCaption.textContent = t;
    }
  });
  function closeLightbox() {
    if (!lightbox.classList.contains('show')) return;
    lightbox.classList.remove('show');
    lightbox.setAttribute('aria-hidden', 'true');
    if (lastLightboxTrigger) lastLightboxTrigger.focus();
  }
  lightbox.addEventListener('click', closeLightbox);
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && lightbox.classList.contains('show')) {
      closeLightbox();
      e.preventDefault();
    }
  });
})();
