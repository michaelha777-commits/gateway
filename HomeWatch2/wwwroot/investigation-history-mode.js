(() => {
  'use strict';
  const THRESHOLD = 140;
  let historyMode = false;
  let allowWritesUntil = 0;
  let anchor = null;

  const active = () => document.getElementById('sessionsView')?.classList.contains('active');
  const allowed = () => Date.now() < allowWritesUntil;

  function banner() {
    let el = document.getElementById('investigationHistoryBanner');
    if (el) return el;
    el = document.createElement('button');
    el.id = 'investigationHistoryBanner';
    el.type = 'button';
    el.hidden = true;
    el.textContent = 'History mode — live updates paused. Return to newest investigations';
    el.style.cssText = 'position:sticky;top:10px;z-index:50;display:block;width:min(760px,calc(100% - 32px));margin:10px auto;padding:11px 16px;border-radius:999px;border:1px solid #4d79a5;background:#123252;color:#eaf4ff;font-weight:700;box-shadow:0 8px 24px rgba(0,0,0,.28);cursor:pointer';
    el.addEventListener('click', () => {
      historyMode = false;
      el.hidden = true;
      window.scrollTo({top:0,behavior:'smooth'});
      setTimeout(() => document.getElementById('refreshSessions')?.click(), 350);
    });
    const view = document.getElementById('sessionsView');
    view?.insertBefore(el, view.firstChild);
    return el;
  }

  function setHistory(value) {
    historyMode = value;
    banner().hidden = !value;
  }

  function capture() {
    const cards = [...document.querySelectorAll('#sessionList [data-investigation-id]')];
    const card = cards.find(x => x.getBoundingClientRect().bottom > 0);
    return {id:card?.dataset.investigationId || '',top:card?.getBoundingClientRect().top || 0,y:window.scrollY};
  }

  function restore(saved) {
    if (!saved) return;
    requestAnimationFrame(() => requestAnimationFrame(() => {
      const card = saved.id ? document.querySelector(`#sessionList [data-investigation-id="${CSS.escape(saved.id)}"]`) : null;
      if (card) window.scrollBy(0, card.getBoundingClientRect().top - saved.top);
      else window.scrollTo(0, saved.y);
    }));
  }

  function allowUpdate(ms=10000) {
    allowWritesUntil = Date.now() + ms;
    anchor = capture();
    setTimeout(() => restore(anchor), 250);
    setTimeout(() => { restore(anchor); anchor = null; }, 1200);
  }

  const inner = Object.getOwnPropertyDescriptor(Element.prototype, 'innerHTML');
  if (inner?.get && inner?.set) {
    Object.defineProperty(Element.prototype, 'innerHTML', {
      configurable: inner.configurable,
      enumerable: inner.enumerable,
      get: inner.get,
      set(value) {
        if (this?.id === 'sessionList' && historyMode && !allowed()) return;
        inner.set.call(this, value);
      }
    });
  }

  const originalReplaceChildren = Element.prototype.replaceChildren;
  Element.prototype.replaceChildren = function(...nodes) {
    if (this?.id === 'sessionList' && historyMode && !allowed()) return;
    return originalReplaceChildren.apply(this, nodes);
  };

  document.addEventListener('pointerdown', event => {
    if (event.target.closest('#loadOlderSessions')) {
      setHistory(true);
      allowUpdate();
    } else if (event.target.closest('#refreshSessions')) {
      setHistory(false);
      allowUpdate();
    }
  }, true);

  document.addEventListener('change', event => {
    if (event.target.closest('#sessionRange,#sessionFrom,#sessionTo,#sessionCategory,#sessionSearch')) {
      setHistory(false);
      allowUpdate();
    }
  }, true);

  let ticking = false;
  window.addEventListener('scroll', () => {
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(() => {
      ticking = false;
      if (active() && window.scrollY > THRESHOLD) setHistory(true);
    });
  }, {passive:true});

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', banner, {once:true});
  else banner();
})();
