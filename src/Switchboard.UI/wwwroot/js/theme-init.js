// Runs synchronously in <head> so the page never flashes the wrong theme.
(function () {
  var pref = "auto";
  try { pref = localStorage.getItem("sb-theme") || "auto"; } catch (e) { /* storage blocked */ }
  var dark = pref === "dark" || (pref === "auto" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.setAttribute("data-bs-theme", dark ? "dark" : "light");
  document.documentElement.setAttribute("data-sb-theme-pref", pref);
})();
