// COLLAPSIBLE OUTLINE. The page has thirteen sections and twenty-four subsections, and listing
// all thirty-seven flat made the sidebar a wall you had to read to navigate.
//
// The grouping is built HERE, at runtime, rather than by nesting the markup: the thirty-seven
// links carry pt_* keys that must stay byte-identical to the headings they name, so the safest
// structure is one that never edits them. Wrapping moves the existing nodes, so the scroll-spy
// below still finds all thirty-seven through .outline a, in document order.
(function(){
  var nav=document.querySelector('.outline');
  if(!nav) return;
  var kids=[].slice.call(nav.children), groups=[], cur=null;
  kids.forEach(function(a){
    if(a.tagName!=='A') return;
    if(a.classList.contains('sub')){
      if(cur){ cur.subs.appendChild(a); }
      return;
    }
    var g=document.createElement('div'); g.className='ol-group';
    var head=document.createElement('div'); head.className='ol-head';
    var subs=document.createElement('div'); subs.className='ol-subs';
    nav.insertBefore(g,a); head.appendChild(a); g.appendChild(head); g.appendChild(subs);
    cur={el:g,subs:subs,head:head};
    groups.push(cur);
  });
  // A section with no subsections gets no chevron - an empty disclosure that opens onto
  // nothing is worse than none at all.
  groups.forEach(function(g){
    if(!g.subs.children.length){ g.el.classList.add('ol-leaf'); return; }
    var t=document.createElement('button');
    t.className='ol-chev'; t.type='button'; t.setAttribute('aria-expanded','false');
    t.setAttribute('aria-label','Expand section');
    t.innerHTML='<svg viewBox="0 0 10 10" width="10" height="10" aria-hidden="true"><path d="M3 1l4 4-4 4" fill="none" stroke="currentColor" stroke-width="1.6"/></svg>';
    t.addEventListener('click',function(e){
      e.preventDefault(); e.stopPropagation();
      var open=g.el.classList.toggle('open');
      t.setAttribute('aria-expanded',open?'true':'false');
      g.el.dataset.manual='1';   // a hand-opened section is not auto-closed by scrolling
    });
    g.head.appendChild(t);
  });
  // The section toggles are made after the page strings load, so their label follows the language here.
  function chevLabels(){
    var l=document.documentElement.getAttribute('lang')||'en'; l=l==='zh-Hant'?'zh':(l==='zh-Hans'?'zh-cn':l);
    var d=(window.I18N&&window.I18N[l])||{};
    document.querySelectorAll('.ol-chev').forEach(function(b){ b.setAttribute('aria-label', d.ui_expand_section||'Expand section'); });
  }
  chevLabels(); document.addEventListener('kpdf-languagechange', chevLabels);
  // Reading a section opens it. Sections you opened by hand stay open; the rest close, so the
  // sidebar keeps showing where you are rather than growing as you scroll.
  window.knOutlineReveal=function(a){
    var g=a&&a.closest?a.closest('.ol-group'):null;
    groups.forEach(function(x){
      var open=(x.el===g)||x.el.dataset.manual==='1';
      x.el.classList.toggle('open',open);
      var c=x.head.querySelector('.ol-chev');
      if(c) c.setAttribute('aria-expanded',open?'true':'false');
    });
  };
})();

window.setupOutlineNavigation();
