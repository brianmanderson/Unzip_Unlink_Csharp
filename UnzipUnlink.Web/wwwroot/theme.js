// Applied before first paint so the page never flashes the wrong theme.
(function () {
  try {
    var saved = localStorage.getItem("unzipunlink.theme");
    if (saved === "light" || saved === "dark") {
      document.documentElement.setAttribute("data-theme", saved);
    }
  } catch (e) { /* storage unavailable: follow the system */ }
})();
