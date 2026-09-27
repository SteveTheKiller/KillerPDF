(function () {
  var metadata = document.querySelector(".installer-meta") || document.querySelector(".mcp-installer-meta");
  if (metadata && !metadata.querySelector(".mcp-runtime")) {
    var runtime = document.createElement("span"); runtime.className = "mcp-runtime"; runtime.textContent = ".NET 10"; metadata.insertBefore(runtime, metadata.children[2] || null);
  }

  document.querySelectorAll("#limits li").forEach(function (item) {
    if (/public installer/i.test(item.textContent || "")) item.remove();
  });
  var limitSource = document.querySelector("#limits .mcp-limit-source");
  if (limitSource) {
    limitSource.textContent = limitSource.textContent.replace(/\s*The public installer (?:is still being prepared and )?has not been released\.?/i, "");
  }

  var meta = document.querySelector(".installer-meta");
  if (!meta) meta = document.querySelector(".mcp-installer-meta");
  if (meta) {
    var values = meta.querySelectorAll("span");
    if (values[0] && /Version 0\.1\./.test(values[0].textContent || "")) values[0].textContent = "Version 0.2.0";
    if (values[1] && /36\.[67] MiB/.test(values[1].textContent || "")) values[1].textContent = "9.7 MiB";
    var link = meta.querySelector("a[href*='/releases/tag/v0.1.']");
    if (link) link.href = "https://github.com/SteveTheKiller/KillerMCP/releases/tag/v0.2.0";
  }
})();
