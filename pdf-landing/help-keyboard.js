(function(){
var SHORTCUTS = window.KillerPDFShortcuts;
var KBL = SHORTCUTS.map;
function shortcutLocale(){
  var lang=document.documentElement.getAttribute('lang')||'en';
  if(lang.indexOf('zh-Hant')===0) lang='zh'; else if(lang.indexOf('zh-Hans')===0) lang='zh-cn'; else lang=lang.split('-')[0];
  return SHORTCUTS.locales[lang] || SHORTCUTS.locales.en;
}
function kbText(fallback, key){
  var lang=document.documentElement.getAttribute('lang')||'en';
  if(lang.indexOf('zh-Hant')===0) lang='zh'; else if(lang.indexOf('zh-Hans')===0) lang='zh-cn'; else lang=lang.split('-')[0];
  var dict=(typeof I18N!=='undefined' && I18N[lang]) || null, raw=dict && dict[key];
  if(raw==null) return fallback;
  var tmp=document.createElement('div'); tmp.innerHTML=raw; return tmp.textContent || fallback;
}
function kbAction(key){ return shortcutLocale().labels[key] || SHORTCUTS.locales.en.labels[key] || key; }
function kbCategory(category){
  var group=shortcutLocale().groups.find(function(item){ return item.id===category; });
  return group ? group.title : category;
}
function renderShortcutList(){
  document.querySelectorAll('[data-shortcut-label]').forEach(function(button){
    var key=button.getAttribute('data-shortcut-label'), label=kbAction(key);
    var binding=shortcutLocale().rows.find(function(row){ return row.label===label; });
    if(!binding) return;
    var hint=button.querySelector('.kbd');
    if(!hint){ hint=document.createElement('span'); hint.className='kbd'; button.insertBefore(hint,button.querySelector('.chev')); }
    hint.textContent=binding.keys;
  });
  var container=document.querySelector('.kbd-cols');
  if(!container) return;
  var locale=shortcutLocale(), columns=[document.createElement('div'),document.createElement('div')];
  columns.forEach(function(column){ column.className='kbd-col'; });
  locale.groups.forEach(function(group){
    var section=document.createElement('div'), heading=document.createElement('h4');
    section.className='kbd-group'; heading.textContent=group.title; section.appendChild(heading);
    locale.rows.filter(function(row){ return row.category===group.id; }).forEach(function(row){
      var element=document.createElement('div'), keys=document.createElement('span'), label=document.createElement('span');
      element.className='kbd-row'; keys.className='k'; label.className='d';
      keys.textContent=row.keys; label.textContent=row.label;
      element.append(keys,label); section.appendChild(element);
    });
    columns[group.right ? 1 : 0].appendChild(section);
  });
  container.replaceChildren.apply(container,columns);
}
document.addEventListener('DOMContentLoaded',renderShortcutList);
var KEY_CAPS = {
  Ctrl:['Ctrl','ui_Key_Ctrl'], RCtrl:['Ctrl','ui_Key_Ctrl'],
  Alt:['Alt','ui_Key_Alt'], RAlt:['Alt','ui_Key_Alt'],
  Shift:['Shift','ui_Key_Shift'], RShift:['Shift','ui_Key_Shift'],
  Del:['Del','ui_Key_Delete'], Enter:['Enter','ui_Key_Enter'], Esc:['Esc','ui_Key_Esc'],
  Menu:['Menu','ui_Key_Menu'], Home:['Home','ui_Key_Home'], End:['End','ui_Key_End'],
  PgUp:['PgUp','ui_Key_PgUp'], PgDn:['PgDn','ui_Key_PgDn'], Tab:['Tab','ui_Key_Tab']
};
function kbKey(id, fallback){ var v=KEY_CAPS[id]; return v ? kbText(v[0],v[1]) : fallback; }
function localizeShortcutLabels(){
  document.querySelector('[data-kblayer="ctrl"]').textContent=kbText('Ctrl','ui_Key_Ctrl');
  document.querySelector('[data-kblayer="ctrlshift"]').textContent=kbText('Ctrl','ui_Key_Ctrl')+'+'+kbText('Shift','ui_Key_Shift');
  document.querySelector('[data-kblayer="shift"]').textContent=kbText('Shift','ui_Key_Shift');
  document.querySelector('[data-kblayer="alt"]').textContent=kbText('Alt','ui_Key_Alt');
  document.querySelector('[data-kblayer="altshift"]').textContent=kbText('Alt','ui_Key_Alt')+'+'+kbText('Shift','ui_Key_Shift');
  document.querySelector('[data-kblayer="ctrlaltshift"]').textContent=kbText('Ctrl','ui_Key_Ctrl')+'+'+kbText('Alt','ui_Key_Alt')+'+'+kbText('Shift','ui_Key_Shift');
  Object.keys(keys).forEach(function(id){
    var cap=keys[id].firstChild;
    cap.style.transform='';
    cap.textContent=kbKey(id,keys[id].getAttribute('data-key-cap')||'');
    requestAnimationFrame(function(){
      var available=keys[id].clientWidth-6;
      if(cap.scrollWidth>available) cap.style.transform='scaleX('+(available/cap.scrollWidth)+')';
    });
  });
}
var KBROWS = [
[['Esc','Esc',1],['',0.8],['F1','F1',1],['F2','F2',1],['F3','F3',1],['F4','F4',1],['',0.6],['F5','F5',1],['F6','F6',1],['F7','F7',1],['F8','F8',1],['',0.6],['F9','F9',1],['F10','F10',1],['F11','F11',1],['F12','F12',1]],
[['Grave','`',1],['D1','1',1],['D2','2',1],['D3','3',1],['D4','4',1],['D5','5',1],['D6','6',1],['D7','7',1],['D8','8',1],['D9','9',1],['D0','0',1],['Minus','-',1],['Equals','=',1],['Back','⌫',2],['',0.6],['Ins','Ins',1],['Home','Home',1],['PgUp','PgUp',1]],
[['Tab','Tab',1.5],['Q','Q',1],['W','W',1],['E','E',1],['R','R',1],['T','T',1],['Y','Y',1],['U','U',1],['I','I',1],['O','O',1],['P','P',1],['LBr','[',1],['RBr',']',1],['Bslash','\\',1.5],['',0.6],['Del','Del',1],['End','End',1],['PgDn','PgDn',1]],
[['Caps','Caps',1.8],['A','A',1],['S','S',1],['D','D',1],['F','F',1],['G','G',1],['H','H',1],['J','J',1],['K','K',1],['L','L',1],['Semi',';',1],['Quote','\'',1],['Enter','Enter',2.2]],
[['Shift','Shift',2.3],['Z','Z',1],['X','X',1],['C','C',1],['V','V',1],['B','B',1],['N','N',1],['M','M',1],['Comma',',',1],['Period','.',1],['Slash','/',1],['RShift','Shift',2.7],['',1.6],['Up','↑',1]],
[['Ctrl','Ctrl',1.5],['Win','Win',1.2],['Alt','Alt',1.5],['Space','',6.8],['RAlt','Alt',1.5],['Menu','☰',1],['RCtrl','Ctrl',1.5],['',0.6],['Left','←',1],['Down','↓',1],['Right','→',1]]];
var MODS = { base:[], ctrl:['Ctrl','RCtrl'], ctrlshift:['Ctrl','RCtrl','Shift','RShift'], shift:['Shift','RShift'], alt:['Alt','RAlt'], altshift:['Alt','RAlt','Shift','RShift'], ctrlaltshift:['Ctrl','RCtrl','Alt','RAlt','Shift','RShift'] };
var U = 46, board = document.getElementById('kbmapBoard'), detail = document.getElementById('kbmapDetail');
var layer = 'base', keys = {}, hover = null;
function startMarquee(h){   // a cut-off caption scrolls, marquee-style
  var b = KBL[layer][h.id];
  if (!b || !h.m.animate) return;
  h.act.classList.add('scrolling');   // unclamps the span so the full text width is measurable
  var d = h.m.offsetWidth - h.act.clientWidth + 4;   // +4: the act's own side padding
  if (d > 2) {
    h.k._anim = h.m.animate([{transform:'translateX(0)'},{transform:'translateX(' + (-d) + 'px)'}],
      {duration: Math.max(600, d * 40), direction: 'alternate', iterations: Infinity, easing: 'linear', delay: 350});
  } else { h.act.classList.remove('scrolling'); }
}
function stopMarquee(h){
  if (h.k._anim) { h.k._anim.cancel(); h.k._anim = null; }
  h.act.classList.remove('scrolling');
}
KBROWS.forEach(function(row){
  var r = document.createElement('div'); r.className = 'kbmap-row';
  row.forEach(function(c){
    if (!c[0]) { var g = document.createElement('div'); g.style.cssText = 'flex:none;width:' + (U * c[1]) + 'px'; r.appendChild(g); return; }
    var k = document.createElement('div'); k.className = 'kb-key'; k.style.width = (U * c[2] - 4) + 'px';
    k.innerHTML = '<span class="cap"></span><span class="act"><span></span></span><span class="bar"></span>';
    k.setAttribute('data-key-cap',c[1]);
    k.firstChild.textContent = kbKey(c[0],c[1]);
    var id = c[0], act = k.querySelector('.act'), m = act.firstChild;
    k._h = { k: k, act: act, m: m, id: id };
    k.addEventListener('mouseenter', function(){
      var b = KBL[layer][id];
      hover = k._h;
      startMarquee(k._h);
      detail.textContent = b ? (kbCategory(b[0]) + ' :: ' + kbAction(b[1])) : ' '; });
    k.addEventListener('mouseleave', function(){
      stopMarquee(k._h);
      hover = null;
      detail.textContent = ' '; });
    keys[id] = k; r.appendChild(k);
  });
  board.appendChild(r);
});
function paint(){
  Object.keys(keys).forEach(function(id){
    var k = keys[id], b = KBL[layer][id];
    k.classList.toggle('bound', !!b);
    if (b) { k.style.setProperty('--kc', 'var(--kc-' + b[0] + ')'); k.querySelector('.act').firstChild.textContent = kbAction(b[1]); }
    k.classList.toggle('mod-on', MODS[layer].indexOf(id) >= 0);
  });
  document.querySelectorAll('.kbmap-layers button').forEach(function(b){ b.classList.toggle('on', b.getAttribute('data-kblayer') === layer); });
  // Layer changed while a key is hovered (held modifier): restart its marquee for the NEW caption.
  if (hover) {
    stopMarquee(hover);
    startMarquee(hover);
    var hb = KBL[layer][hover.id];
    detail.textContent = hb ? (kbCategory(hb[0]) + ' :: ' + kbAction(hb[1])) : ' ';
  }
}
document.querySelectorAll('.kbmap-layers button').forEach(function(b){
  b.addEventListener('click', function(){ layer = b.getAttribute('data-kblayer'); paint(); });
});
function sync(e){
  var l = e.ctrlKey && e.altKey && e.shiftKey ? 'ctrlaltshift' : e.ctrlKey && e.shiftKey ? 'ctrlshift' : e.ctrlKey ? 'ctrl' : e.altKey && e.shiftKey ? 'altshift' : e.altKey ? 'alt' : e.shiftKey ? 'shift' : 'base';
  if (l !== layer) { layer = l; paint(); }
}
window.addEventListener('keydown', sync);
window.addEventListener('keyup', sync);
document.addEventListener('kpdf-languagechange', function(){ localizeShortcutLabels(); renderShortcutList(); paint(); });
localizeShortcutLabels();
paint();
// Scale the board down to the column width instead of scrolling, like the app's Viewbox.
function fit(){
  var wrap = board.parentNode;
  board.style.transform = ''; wrap.style.height = '';
  var s = wrap.clientWidth / board.offsetWidth;
  if (s < 1) { board.style.transform = 'scale(' + s + ')'; wrap.style.height = (board.offsetHeight * s + 6) + 'px'; }
}
fit();
window.addEventListener('resize', fit);
})();
