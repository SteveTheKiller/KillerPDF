(function(){
  var links=[].slice.call(document.querySelectorAll('.outline a'));
  var secs=links.map(function(a){return document.querySelector(a.getAttribute('href'));}).filter(Boolean);
  var sc=document.querySelector('.content-scroll');
  function onScroll(){ var y=(sc?sc.scrollTop:window.scrollY)+100, cur=secs[0]; secs.forEach(function(s){ if(s&&s.offsetTop<=y) cur=s; });
    links.forEach(function(a){ a.classList.toggle('on', cur && a.getAttribute('href')==='#'+cur.id); }); }
  (sc||window).addEventListener('scroll',onScroll,{passive:true}); onScroll();
})();

(function(){
  var outline=document.querySelector('.outline');
  var fadeTop=document.querySelector('.sb-fade-top');
  var fadeBottom=document.querySelector('.sb-fade-bottom');
  if(!outline) return;
  function updateFade(){
    var scrolledDown=outline.scrollTop>2;
    var more=outline.scrollHeight-outline.clientHeight-outline.scrollTop>2;
    if(fadeTop) fadeTop.classList.toggle('on', scrolledDown);
    if(fadeBottom) fadeBottom.classList.toggle('on', more);
  }
  outline.addEventListener('scroll',updateFade,{passive:true});
  window.addEventListener('resize',updateFade);
  updateFade();
})();
