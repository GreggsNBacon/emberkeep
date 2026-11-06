// Applies the colour theme picked in App settings before the page draws (so there's no flash of the wrong theme).
// Saved per browser: each device, and each person signing in remotely, can pick their own.
(function () {
  var t = null;
  try { t = localStorage.getItem('emberkeep-theme'); } catch (e) { }
  if (['flame', 'void', 'light', 'contrast'].indexOf(t) < 0) t = 'flame';
  document.documentElement.setAttribute('data-theme', t);
})();

// Basic or Advanced (the switch at the top right). Basic unless this browser saved Advanced.
(function () {
  var m = null;
  try { m = localStorage.getItem('emberkeep-mode'); } catch (e) { }
  document.documentElement.setAttribute('data-mode', m === 'advanced' ? 'advanced' : 'basic');
})();
