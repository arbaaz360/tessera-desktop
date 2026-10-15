// Injected by the shell when it gives up on the core. Replaces whatever the
// web app was showing. Invokes shell commands directly, so it works whether
// or not the page has the Tauri API loaded.
(function () {
  var message = __MESSAGE__;
  var logPath = __LOG_PATH__;
  var invoke = function (cmd) { return window.__TAURI_INTERNALS__.invoke(cmd, {}); };

  document.title = "Tessera Desktop";
  var root = document.createElement("div");
  root.style.cssText = "position:fixed;inset:0;overflow:auto;background:#1c1a22;color:#eee;font:15px/1.5 system-ui,sans-serif;padding:48px;box-sizing:border-box";
  root.innerHTML =
    '<div style="max-width:640px;margin:0 auto">' +
    '<h1 style="font-size:22px;margin:0 0 12px">The Tessera engine stopped</h1>' +
    '<p id="tessera-msg" style="margin:0 0 16px;color:#ccc"></p>' +
    '<p style="margin:0 0 24px">Log file:<br><code id="tessera-log" style="font-size:13px;word-break:break-all;color:#f5d46e"></code></p>' +
    '<button id="tessera-restart" style="font:inherit;padding:8px 16px;border-radius:6px;border:0;background:#e85d3c;color:#fff;cursor:pointer">Restart engine</button> ' +
    '<button id="tessera-logs" style="font:inherit;padding:8px 16px;border-radius:6px;border:1px solid #666;background:transparent;color:#eee;cursor:pointer;margin-left:8px">Open log folder</button>' +
    '<p id="tessera-status" style="margin:16px 0 0;color:#999"></p>' +
    "</div>";
  document.body.innerHTML = "";
  document.body.appendChild(root);
  document.getElementById("tessera-msg").textContent = message;
  document.getElementById("tessera-log").textContent = logPath;
  var status = document.getElementById("tessera-status");
  document.getElementById("tessera-restart").onclick = function () {
    status.textContent = "Restarting…";
    invoke("core_restart").then(function () { location.reload(); }, function (e) { status.textContent = String(e); });
  };
  document.getElementById("tessera-logs").onclick = function () {
    invoke("open_logs").catch(function (e) { status.textContent = String(e); });
  };
})();
