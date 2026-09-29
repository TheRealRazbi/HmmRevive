"use strict";
// HMM Revive launcher page. Talks only to HMM-Revive.exe on this PC (/api/*); the exe talks to lobbies.
// Text comes from i18n.js: t(key) for page text, tr(message) for messages from the launcher or a host.

const $ = (s) => document.querySelector(s);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

let S = null;          // last /api/state
let C = { cars: [], arenas: [] }; // /api/catalog
let view = "play";
let lastLobbyRev = -1;
const DIFFS = () => [["auto", t("diff_auto")], ["easy", t("diff_easy")], ["medium", t("diff_medium")], ["hard", t("diff_hard")]];
const SCORES = () => [["1", t("score_1")], ["2", "2"], ["3", t("score_3")], ["4", "4"], ["5", "5"]];
const RESOLUTIONS = [[1280, 720], [1600, 900], [1920, 1080], [2560, 1440], [3840, 2160]];

async function api(path, body) {
  const opt = body === undefined ? {} : { method: "POST", headers: { "X-HMM-Revive": "1", "Content-Type": "application/json" }, body: JSON.stringify(body) };
  const res = await fetch("/api/" + path, opt);
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || res.statusText);
  return data;
}

// Runs an action, shows its error as a toast, then refreshes the state.
async function act(path, body, okText) {
  try {
    const r = await api(path, body ?? {});
    if (okText) toast(okText, true);
    await poll();
    return r;
  } catch (e) {
    toast(tr(e.message));
    return null;
  }
}

let toastTimer;
function toast(text, good) {
  const el = $("#toast");
  el.textContent = text;
  el.className = "on" + (good ? " good" : "");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.className = ""), good ? 2500 : 6000);
}

// ---------- language ----------
function setLang(lang, save) {
  LANG = lang === "pt" ? "pt" : "en";
  applyStatic();
  $("#lang").value = LANG;
  $("#s-lang").value = LANG;
  $("#refresh").textContent = discovering ? t("searching_btn") : t("refresh");
  if (save) act("settings", { lang: LANG });
  render();
}

// ---------- helpers ----------
const carName = (id) => (C.cars.find((c) => String(c.id) === String(id)) || {}).name || t("default_car");
const arenaName = (id) => (C.arenas.find((a) => a.id === id) || {}).name || "Arena " + id;
const skinsOf = (id) => (C.cars.find((c) => String(c.id) === String(id)) || { skins: ["Original"] }).skins;
const skinName = (carId, skin) => {
  if (skin === "random") return t("random_skin");
  const n = parseInt(skin, 10);
  const list = skinsOf(carId);
  return !isNaN(n) && list[n] ? list[n] : skin || "Original";
};
const points = (n) => (n === 1 ? t("point_1") : t("points_n", n));

// Fills a <select> once per option-set, and sets its value unless the user is using it.
function fill(sel, options, value) {
  const sig = JSON.stringify(options);
  if (sel.dataset.sig !== sig) {
    sel.innerHTML = options.map(([v, text]) => `<option value="${esc(v)}">${esc(text)}</option>`).join("");
    sel.dataset.sig = sig;
  }
  if (document.activeElement !== sel && value !== undefined && sel.value !== String(value)) sel.value = String(value);
}

// Re-renders a container only when its content changes (keeps hover/focus stable).
function paint(el, html) {
  if (el.dataset.html !== html) {
    el.innerHTML = html;
    el.dataset.html = html;
  }
}

// Addresses friends can reach the lobby on: Tailscale, ZeroTier and a public address (servers/VPSes), else the home
// network. (A WireGuard relay only forwards the game port, not the lobby.)
const joinAddresses = () => {
  const vpn = S.addresses.filter((a) => a.kind === "Tailscale" || a.kind === "ZeroTier" || a.kind === "Internet");
  return vpn.length ? vpn : S.addresses.filter((a) => a.kind === "LAN").slice(0, 1);
};
const carOptions = () => C.cars.map((c) => [String(c.id), c.name]);
const skinOptions = (carId) => skinsOf(carId).map((s, i) => [String(i), i === 0 ? t("original", s) : s]).concat([["random", t("random")]]);
const mySkin = () => ((S.settings.skins || {})[carName(S.settings.car)] ?? "0");
const diffName = (d) => (DIFFS().find((x) => x[0] === d) || [d, d])[1];

// ---------- views ----------
function show(v) {
  view = v;
  document.querySelectorAll(".view").forEach((e) => e.classList.toggle("on", e.id === "view-" + v));
  document.querySelectorAll("#nav button").forEach((b) => b.classList.toggle("on", b.dataset.view === v));
  if (v === "play") discover();
  render();
}

function render() {
  if (!S) return;
  $("#version").textContent = "v" + S.version;
  $("#me-chip").innerHTML = `<b>${esc(S.settings.name)}</b> · ${esc(carName(S.settings.car))}`;
  const inLobby = !!S.lobby;
  $("#nav-lobby").hidden = !inLobby;
  if (!inLobby && view === "lobby") return show("play");
  renderBanners();
  if (view === "play") renderPlay();
  if (view === "host") renderHost();
  if (view === "lobby" && inLobby) renderLobby();
  if (view === "settings") renderSettings();
}

function renderBanners() {
  const b = [];
  if (!S.pathOk) b.push(["bad", t("bad_path", esc(S.root))]);
  const choose = S.canSetup ? [[t("choose_game"), "choose-game"]] : [];
  if (S.setupStatus === "running") b.push(["info", t("setting_up", esc(S.setupLog))]);
  else if (S.setupStatus.startsWith("failed"))
    b.push(["bad", t("setup_failed", esc(tr(S.setupStatus.replace(/^failed: /, ""))), esc(S.setupLog)), [[t("try_again"), "setup"], ...choose]]);
  else if (!S.gameReady)
    b.push(["warn", S.gameDir ? t("first_time", esc(S.gameDir)) : t("no_game"), S.gameDir && S.canSetup ? [[t("set_up"), "setup"], ...choose] : choose]);
  else if (S.needsUpdate && S.canSetup)
    b.push(["warn", t("needs_update", esc(S.gameVersion), esc(S.kitVersion)), [[t("update_game"), "setup"]]]);
  if (S.notice) b.push(["info", esc(tr(S.notice))]);
  // The first button is the main one.
  paint($("#banners"), b.map(([k, text, btns = []]) =>
    `<div class="banner ${k}"><div>${text}</div>${btns.length ? `<div class="row">${btns.map(([label, action], i) =>
      `<button ${i ? "" : 'class="primary"'} data-act="${action}">${label}</button>`).join("")}</div>` : ""}</div>`).join(""));
}

// ----- play -----
let lobbies = null, discovering = false;
async function discover() {
  if (discovering) return;
  discovering = true;
  $("#refresh").disabled = true;
  $("#refresh").textContent = t("searching_btn");
  try { lobbies = (await api("discover")).lobbies; } catch (e) { lobbies = []; }
  discovering = false;
  $("#refresh").disabled = false;
  $("#refresh").textContent = t("refresh");
  renderPlay();
}

function renderPlay() {
  if (!S) return;
  const list = (lobbies || []).filter((l) => !l.own);
  let html;
  if (lobbies === null) html = `<div class="empty">${t("searching")}</div>`;
  else if (!list.length) html = `<div class="empty">${t("no_lobbies")}</div>`;
  else html = list.map((l) => {
    const live = l.phase !== "lobby";
    const sameVersion = l.version === S.version;
    return `<div class="card">
      <div class="t">${t("someones_lobby", esc(l.host || t("someone")))}</div>
      <div class="m">${esc(arenaName(l.arena))} · ${l.players === 1 ? t("players_1") : t("players_n", l.players)}${l.bots ? t("plus_bots", l.bots) : ""}</div>
      <div class="m">${esc(l.address)} · ${t("via", esc(l.via))}${sameVersion ? "" : ` · <span class="warn-t">${t("version_x", esc(l.version))}</span>`}</div>
      <div class="row"><span class="pill ${live ? "live" : "open"}">${live ? t("match_running_pill") : t("in_lobby_pill")}</span>
        <button class="primary" data-join="${esc(l.address)}" ${sameVersion ? "" : "disabled"}>${t("join")}</button></div>
    </div>`;
  }).join("");
  paint($("#lobbies"), html);
  paint($("#recent"), (S.settings.recent || []).map((a) => `<button data-fill="${esc(a)}">${esc(a)}</button>`).join(""));
}

// ----- host -----
function renderHost() {
  const addr = joinAddresses().filter((a) => a.kind !== "LAN");
  paint($("#host-checks"), `
    <p>${addr.length ? t("players_join_with", addr.map((a) => `<code>${esc(a.ip)}</code> <span class="hint">(${esc(a.kind)})</span>`).join(", ")) : t("no_vpn_address")}</p>
    ${S.firewall ? `<p class="ok-t">${t("firewall_ready")}</p>` : `<div class="banner warn"><div>${t("firewall_may_block")}</div><button data-act="firewall">${t("setup_firewall")}</button></div><p></p>`}`);
  $("#open-lobby").disabled = !S.gameReady;
  $("#open-lobby").textContent = S.hosting ? t("go_to_lobby") : t("open_lobby");
}

// ----- lobby -----
function renderLobby() {
  const L = S.lobby;
  const me = L.members.find((m) => m.id === L.me) || {};
  const amHost = S.hosting;
  $("#lobby-title").textContent = t("someones_lobby", L.host || "?");
  const hostOut = L.members.some((m) => m.host && m.team === "none");
  $("#lobby-sub").textContent = t("lobby_sub", arenaName(L.arena), points(L.score)) + (hostOut ? " · " + t("host_not_playing") : "") + (amHost ? "" : " · " + S.lobbyAddress);

  // status line
  let st = "", cls = "status";
  if (L.phase === "lobby" && L.countdown >= 0) { st = t("starting_in", L.countdown); cls += " count"; }
  else if (L.phase === "starting") { st = t("starting_server"); cls += " live"; }
  else if (L.phase === "playing") {
    cls += " live";
    st = me.inMatch ? (S.gameRunning ? t("running_good_luck") : t("running_no_game")) : me.team === "none" ? t("running_hosting") : t("running_next");
  } else {
    const waiting = L.members.filter((m) => m.team !== "none" && !m.ready).map((m) => m.name);
    st = L.cantStart ? tr(L.cantStart) : waiting.length ? t(waiting.length === 1 ? "waiting_for" : "waiting_for_n", waiting.join(", ")) : L.autoStart ? t("everyone_ready") : t("everyone_ready_host");
  }
  const status = $("#lobby-status");
  status.className = cls;
  paint(status, esc(st) + (L.message && L.phase === "lobby" ? ` <span class="hint">· ${esc(tr(L.message))}</span>` : ""));

  for (const team of ["blue", "red"]) {
    paint($("#slots-" + team), slotsHtml(L, team, me, amHost));
    const full = L.members.filter((m) => m.team === team).length >= 4;
    paint($("#switch-" + team), L.phase === "lobby" && me.team && me.team !== team && !full ? `<button data-team="${team}">${t("switch_here")}</button>` : "");
  }

  // my controls
  fill($("#lobby-car"), carOptions(), S.settings.car);
  fill($("#lobby-skin"), skinOptions(S.settings.car), mySkin());
  const locked = L.phase !== "lobby";
  const playing = me.team !== "none";
  $("#not-playing").hidden = playing;
  document.querySelectorAll(".player-only").forEach((e) => (e.hidden = !playing));
  $("#lobby-car").disabled = $("#lobby-skin").disabled = locked;
  const ready = $("#ready");
  ready.hidden = !playing;
  ready.disabled = locked;
  ready.classList.toggle("on", !!me.ready);
  ready.textContent = locked ? (me.inMatch ? t("in_the_match") : t("match_running_pill")) : me.ready ? t("ready_on") : t("ready");
  $("#relaunch").hidden = !(L.phase === "playing" && me.inMatch && !S.gameRunning);

  // host box
  $("#hostbox").hidden = !amHost;
  if (amHost && S.hostSetup) {
    const H = S.hostSetup;
    fill($("#h-arena"), C.arenas.map((a) => [String(a.id), a.name]), H.arena);
    fill($("#h-score"), SCORES(), H.score);
    for (const team of ["blue", "red"]) {
      $(`#h-${team}-bots`).textContent = L.teams[team].bots + (H[team].bots > L.teams[team].bots ? ` (${H[team].bots})` : "");
      fill($(`#h-${team}-diff`), DIFFS(), H[team].difficulty);
    }
    if (document.activeElement !== $("#h-auto")) $("#h-auto").checked = H.autoStart;
    $("#h-plays").checked = me.team !== "none";
    $("#h-plays").disabled = locked;
    $("#start").hidden = locked;
    $("#start").disabled = !!L.cantStart;
    $("#stop").hidden = !locked;
    const addr = joinAddresses();
    paint($("#share"), addr.length && addr[0].kind !== "LAN"
      ? t("friends_join_with", addr.map((a) => `<code>${esc(a.ip)}</code> (${esc(a.kind)})`).join(t("or"))) + (S.lobbyPort !== 9697 ? t("port_x", S.lobbyPort) : "")
      : t("only_home"));
  }
}

function slotsHtml(L, team, me, amHost) {
  const humans = L.members.filter((m) => m.team === team);
  const T = L.teams[team];
  const H = S.hostSetup;
  const out = humans.map((m) => `
    <div class="slot${m.id === L.me ? " me" : ""}">
      <div class="who"><div class="name">${esc(m.name)}${m.id === L.me ? ` <span class='hint'>${t("you_tag")}</span>` : ""}</div>
        <div class="car">${esc(carName(m.car))}${m.skin && m.skin !== "0" ? " · " + esc(skinName(m.car, m.skin)) : ""}</div></div>
      ${m.host ? `<span class="tag host">${t("tag_host")}</span>` : ""}
      ${m.ready ? `<span class="tag ready">${t("tag_ready")}</span>` : L.phase === "lobby" ? `<span class="tag">${t("tag_not_ready")}</span>` : ""}
      ${amHost && !m.host && L.phase === "lobby" ? `<button class="x" title="${t("remove_title")}" data-kick="${esc(m.id)}">✕</button>` : ""}
    </div>`);
  for (let i = 0; i < T.bots; i++) {
    const car = (H && H[team].cars[i]) || T.cars[i] || "default";
    const carSel = amHost && L.phase === "lobby"
      ? `<select data-botcar="${team}:${i}">${[["default", t("default_car")], ["random", t("random_car")], ...carOptions()]
          .map(([v, text]) => `<option value="${esc(v)}"${v === String(car) ? " selected" : ""}>${esc(text)}</option>`).join("")}</select>`
      : "";
    out.push(`<div class="slot bot"><div class="who"><div class="name">${t("bot")}</div>
      <div class="car">${esc(diffName(T.difficulty))}${carSel ? "" : " · " + esc(car === "random" ? t("random_car") : carName(car))}</div></div>${carSel}</div>`);
  }
  for (let i = humans.length + T.bots; i < 4; i++) out.push(`<div class="slot open">${t("open_slot")}</div>`);
  return out.join("");
}

// ----- settings -----
function renderSettings() {
  const name = $("#s-name");
  if (document.activeElement !== name && name.value !== S.settings.name) name.value = S.settings.name;
  fill($("#s-car"), carOptions(), S.settings.car);
  fill($("#s-skin"), skinOptions(S.settings.car), mySkin());
  const res = RESOLUTIONS.map(([w, h]) => [`${w}x${h}`, `${w} × ${h}`]);
  const cur = `${S.settings.width}x${S.settings.height}`;
  if (cur !== "0x0" && !res.some((r) => r[0] === cur)) res.push([cur, `${S.settings.width} × ${S.settings.height}`]);
  fill($("#s-res"), [["0x0", t("res_game")], ...res], cur);
  if (document.activeElement !== $("#s-full")) $("#s-full").checked = S.settings.fullscreen;
  paint($("#s-game"), `<p class="hint">${S.gameReady ? t("game_ready_in", esc(S.gameVersion), esc(S.instance)) : t("not_set_up")}</p>
    ${S.gameDir ? `<p class="hint">${t("game_from", esc(S.gameDir))}</p>` : ""}
    <div class="row">${S.canSetup ? `<button data-act="setup">${S.gameReady ? t("update_game") : t("set_up")}</button>
    <button data-act="choose-game">${t("choose_game")}</button>` : ""}
    <button data-act="open-folder">${t("open_folder")}</button></div>`);
  paint($("#s-firewall"), `${S.firewall ? `<p class="ok-t">${t("firewall_in_place")}</p>` : ""}<p class="hint">${t("firewall_hint", S.gamePort, S.lobbyPort)}</p>
    <button data-act="firewall">${t("setup_firewall")}</button>`);
}

// Windows' open-file window (opened by the launcher) to pick HMM.exe in any copy of the game, then set up from it.
async function chooseGame() {
  toast(t("choose_game_open"), true);
  const r = await act("setup/browse", {});
  if (r && r.file) await act("setup", { gameDir: r.file });
}

// ---------- events ----------
document.addEventListener("click", async (e) => {
  const b = e.target.closest("button");
  if (!b) return;
  if (b.dataset.view) return show(b.dataset.view);
  if (b.id === "me-chip") return show("settings");
  if (b.dataset.join) return join(b.dataset.join);
  if (b.dataset.fill) { $("#join-address").value = b.dataset.fill; return; }
  if (b.dataset.team) return act("lobby/update", { team: b.dataset.team });
  if (b.dataset.kick) return act("host/kick", { id: b.dataset.kick });
  if (b.dataset.bots) {
    const [team, d] = b.dataset.bots.split(":");
    const H = structuredClone(S.hostSetup);
    const humans = S.lobby.members.filter((m) => m.team === team).length;
    H[team].bots = Math.max(0, Math.min(4 - humans, S.lobby.teams[team].bots + parseInt(d, 10)));
    return act("host/configure", H);
  }
  switch (b.dataset.act) {
    case "setup": return S.gameDir ? act("setup", {}) : chooseGame();
    case "choose-game": return chooseGame();
    case "firewall": {
      toast(t("firewall_asking"), true);
      const r = await act("firewall", {});
      if (r) toast(r.ok ? t("firewall_added") : t("firewall_not_added"), r.ok);
      return;
    }
    case "open-folder": return act("open-folder", {});
  }
  switch (b.id) {
    case "refresh": return discover();
    case "open-lobby":
      if (!S.hosting && !(await act("host/open", {}))) return;
      return show("lobby");
    case "leave": {
      const hosting = S.hosting;
      if (hosting && !confirm(S.lobby.phase === "lobby" ? t("confirm_close_lobby") : t("confirm_close_running"))) return;
      await act(hosting ? "host/close" : "leave", {});
      return show(hosting ? "host" : "play");
    }
    case "ready": {
      const me = S.lobby.members.find((m) => m.id === S.lobby.me);
      return act("lobby/update", { ready: !(me && me.ready) });
    }
    case "relaunch": return act("lobby/relaunch", {});
    case "start": return act("host/start", {});
    case "stop":
      if (confirm(t("confirm_stop"))) return act("host/stop", {});
      return;
  }
});

async function join(address) {
  const btns = document.querySelectorAll("[data-join], #join-form button");
  btns.forEach((b) => (b.disabled = true));
  const r = await act("join", { address });
  btns.forEach((b) => (b.disabled = false));
  if (r) show("lobby");
}

$("#join-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const a = $("#join-address").value.trim();
  if (a) join(a);
});
$("#quick-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const a = $("#quick-address").value.trim();
  if (a) act("quickjoin", { address: a }, t("starting_game"));
});

document.addEventListener("change", async (e) => {
  const el = e.target;
  if (el.dataset.botcar) {
    const [team, i] = el.dataset.botcar.split(":");
    const H = structuredClone(S.hostSetup);
    while (H[team].cars.length <= i) H[team].cars.push("default");
    H[team].cars[i] = el.value;
    return act("host/configure", H);
  }
  switch (el.id) {
    case "lang": case "s-lang": return setLang(el.value, true);
    case "lobby-car": case "s-car": return act("settings", { car: el.value });
    case "lobby-skin": case "s-skin": return act("settings", { skin: el.value });
    case "s-name": {
      const r = await act("settings", { name: el.value });
      if (r) toast(t("name_saved"), true);
      return;
    }
    case "s-res": {
      const [w, h] = el.value.split("x").map(Number);
      return act("settings", { width: w, height: h });
    }
    case "s-full": return act("settings", { fullscreen: el.checked });
    case "h-plays": {
      if (!el.checked) return act("lobby/update", { team: "none" });
      const n = (team) => S.lobby.members.filter((m) => m.team === team).length;
      return act("lobby/update", { team: n("blue") <= n("red") ? "blue" : "red" });
    }
    case "h-auto": case "h-arena": case "h-score": case "h-blue-diff": case "h-red-diff": {
      const H = structuredClone(S.hostSetup);
      H.autoStart = $("#h-auto").checked;
      H.arena = parseInt($("#h-arena").value, 10);
      H.score = parseInt($("#h-score").value, 10);
      H.blue.difficulty = $("#h-blue-diff").value;
      H.red.difficulty = $("#h-red-diff").value;
      return act("host/configure", H);
    }
  }
});

// ---------- polling ----------
let polling = false;
async function poll() {
  if (polling) return;
  polling = true;
  try {
    S = await api("state");
    const rev = S.lobby ? S.lobby.rev : -1;
    if (S.lobby && lastLobbyRev === -1 && view !== "lobby") show("lobby");
    lastLobbyRev = rev;
    render();
  } catch (e) {
    paint($("#banners"), `<div class="banner bad"><div>${t("launcher_down")}</div></div>`);
  } finally {
    polling = false;
  }
}

(async function start() {
  try { C = await api("catalog"); } catch (e) { }
  try { S = await api("state"); } catch (e) { }
  // The saved choice, else the browser's language (Portuguese for pt-*, English otherwise).
  const saved = S && S.settings && S.settings.lang;
  setLang(saved || ((navigator.language || "").toLowerCase().startsWith("pt") ? "pt" : "en"), false);
  await poll();
  show(S && S.lobby ? "lobby" : "play");
  setInterval(poll, 700);
  setInterval(() => { if (view === "play" && !document.hidden) discover(); }, 15000);
})();
