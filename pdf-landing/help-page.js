(function(){
  var items=[].slice.call(document.querySelectorAll('.acc-item'));
  items.forEach(function(it){ var h=it.querySelector('.acc-head'), b=it.querySelector('.acc-body');
    h.addEventListener('click', function(){
      var open=it.classList.contains('open');
      items.forEach(function(o){ o.classList.remove('open'); o.querySelector('.acc-body').style.maxHeight=null; });
      if(!open){ it.classList.add('open'); b.style.maxHeight=b.scrollHeight+'px'; }
    });
  });
  var links=[].slice.call(document.querySelectorAll('.outline a'));
  var secs=links.map(function(a){return document.querySelector(a.getAttribute('href'));}).filter(Boolean);
  var sc=document.querySelector('.content-scroll');
  function spy(){ var y=(sc?sc.scrollTop:window.scrollY)+100, cur=secs[0]; secs.forEach(function(s){ if(s&&s.offsetTop<=y) cur=s; });
    links.forEach(function(a){ a.classList.toggle('on', cur && a.getAttribute('href')==='#'+cur.id); }); }
  (sc||window).addEventListener('scroll',spy,{passive:true}); spy();
})();
