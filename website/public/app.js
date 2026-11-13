// Language toggle: English <-> Myanmar (မြန်မာ). Persisted in localStorage.
(function () {
  var KEY = "warpconnect-lang";
  var root = document.documentElement;

  function apply(lang) {
    root.setAttribute("data-lang", lang);
    root.setAttribute("lang", lang);
    var buttons = document.querySelectorAll(".lang-btn");
    for (var i = 0; i < buttons.length; i++) {
      buttons[i].classList.toggle("is-active", buttons[i].getAttribute("data-setlang") === lang);
    }
    try { window.localStorage.setItem(KEY, lang); } catch (e) { /* private mode */ }
  }

  var saved = null;
  try { saved = window.localStorage.getItem(KEY); } catch (e) { /* private mode */ }
  apply(saved === "my" ? "my" : "en");

  var buttons = document.querySelectorAll(".lang-btn");
  for (var i = 0; i < buttons.length; i++) {
    buttons[i].addEventListener("click", function () {
      apply(this.getAttribute("data-setlang"));
    });
  }
})();
