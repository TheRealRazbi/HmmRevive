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
// Game builds: "steam" (current) or an older build id. Older builds use the game's own pick screen and one arena.
const buildName = (b) => t("build_" + (b || "steam"));
const isLegacy = (b) => !!b && b !== "steam";
const haveBuild = (b) => (S.builds || []).includes(b || "steam");

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
const isPlayer = (m) => m.team === "blue" || m.team === "red";
const teamName = (team) => t(team === "blue" ? "blue_short" : "red_short");
const nCars = (n) => (n === 1 ? t("cars_1") : t("cars_n", n));
// Draft: the cars a team picked, and the ones its players don't drive (the bots get those).
const pool = (D, team) => (D ? D.picks.filter((c) => c.team === team).map((c) => c.car) : []);
const botPool = (L, team) => pool(L.draft, team).filter((c) => !L.members.some((m) => m.team === team && m.car === c));
// My car in this lobby: after a draft it's the one the lobby gave me, not the one in my settings.
const lobbyCar = (L, me) => (L && L.draft && isPlayer(me) ? me.car : S.settings.car);

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
  // In an older build's lobby the car is picked in the game, so the chip shows the name only.
  const L = S.lobby, me = L && L.members.find((m) => m.id === L.me);
  const chipCar = me && me.team === "spec" ? t("spectator") : carName(me ? lobbyCar(L, me) : S.settings.car);
  $("#me-chip").innerHTML = `<b>${esc(S.settings.name)}</b>` + (L && isLegacy(L.build) ? "" : ` · ${esc(chipCar)}`);
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
  else if (!S.gameReady && (S.gameDir || !S.builds.length))
    b.push(["warn", S.gameDir ? t("first_time", esc(S.gameDir)) : t("no_game"), S.gameDir && S.canSetup ? [[t("set_up"), "setup"], ...choose] : choose]);
  else if (S.needsUpdate && S.canSetup)
    b.push(["warn", t("needs_update", esc(S.gameVersion), esc(S.kitVersion)), [[t("update_game"), "setup"]]]);
  if (S.setupStatus !== "running")
    for (const c of S.copies.filter((c) => isLegacy(c.build) && c.needsUpdate && c.canSetup))
      b.push(["warn", t("needs_update_copy", buildName(c.build), esc(c.version), esc(c.kitVersion)), [[t("update_game"), "setup:" + c.build]]]);
  if (S.notice) b.push(["info", esc(tr(S.notice))]);
  // The first button is the main one.
  paint($("#banners"), b.map(([k, text, btns = []]) =>
    `<div class="banner ${k}"><div>${text}</div>${btns.length ? `<div class="row">${btns.map(([label, action], i) =>
      `<button ${i ? "" : 'class="primary"'} ${action.startsWith("setup:") ? `data-setup-build="${esc(action.slice(6))}"` : `data-act="${action}"`}>${label}</button>`).join("")}</div>` : ""}</div>`).join(""));
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
    const canJoin = sameVersion && haveBuild(l.build);
    return `<div class="card">
      <div class="t">${t("someones_lobby", esc(l.host || t("someone")))}</div>
      <div class="m">${isLegacy(l.build) ? esc(buildName(l.build)) : esc(arenaName(l.arena))} · ${l.players === 1 ? t("players_1") : t("players_n", l.players)}${l.bots ? t("plus_bots", l.bots) : ""}${l.draft ? " · " + t("draft_title") : ""}${l.spectatorSeats ? " · " + t("spec_seats_n", l.spectatorSeats) : ""}</div>
      <div class="m">${esc(l.address)} · ${t("via", esc(l.via))}${sameVersion ? "" : ` · <span class="warn-t">${t("version_x", esc(l.version))}</span>`}${sameVersion && !haveBuild(l.build) ? ` · <span class="warn-t">${t("lobby_needs_copy", esc(buildName(l.build)))}</span>` : ""}</div>
      <div class="row"><span class="pill ${live ? "live" : "open"}">${live ? t("match_running_pill") : t("in_lobby_pill")}</span>
        <button class="primary" data-join="${esc(l.address)}" ${canJoin ? "" : "disabled"}>${t("join")}</button></div>
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
  // Which game the lobby plays, when more than one copy is set up.
  const builds = S.builds || [];
  $("#host-build-row").hidden = S.hosting || builds.length < 2;
  const saved = builds.includes(S.settings.hostBuild) ? S.settings.hostBuild : builds[0];
  fill($("#host-build"), builds.map((b) => [b, buildName(b)]), saved);
  $("#host-build-warn").hidden = S.hosting || !isLegacy($("#host-build").value || saved);
  $("#open-lobby").disabled = !builds.length;
  $("#open-lobby").textContent = S.hosting ? t("go_to_lobby") : t("open_lobby");
}

// ----- lobby -----
function renderLobby() {
  const L = S.lobby;
  const me = L.members.find((m) => m.id === L.me) || {};
  const amHost = S.hosting;
  const legacy = isLegacy(L.build);
  $("#lobby-title").textContent = t("someones_lobby", L.host || "?");
  const hostOut = L.members.some((m) => m.host && m.team === "none");
  $("#lobby-sub").textContent = (legacy ? buildName(L.build) : t("lobby_sub", arenaName(L.arena), points(L.score))) + (L.draftOn ? " · " + t("draft_title") : "")
    + (hostOut ? " · " + t("host_not_playing") : "") + (amHost ? "" : " · " + S.lobbyAddress);

  // status line
  let st = "", cls = "status";
  if (L.phase === "lobby" && L.countdown >= 0) { st = t(L.nextIsDraft ? "draft_in" : "starting_in", L.countdown); cls += " count"; }
  else if (L.phase === "starting") { st = t("starting_server"); cls += " live"; }
  else if (L.phase === "draft") { st = draftStatus(L, me); cls += " live"; }
  else if (L.phase === "playing") {
    cls += " live";
    st = me.inMatch ? (S.gameRunning ? t(me.team === "spec" ? "running_watching" : "running_good_luck") : t("running_no_game"))
      : me.team === "none" ? t("running_hosting") : t("running_next");
  } else {
    const waiting = L.members.filter((m) => isPlayer(m) && !m.ready).map((m) => m.name);
    st = L.cantStart ? tr(L.cantStart) : waiting.length ? t(waiting.length === 1 ? "waiting_for" : "waiting_for_n", waiting.join(", ")) : L.autoStart ? t("everyone_ready") : t("everyone_ready_host");
  }
  const status = $("#lobby-status");
  status.className = cls;
  paint(status, esc(st) + (L.message && L.phase === "lobby" ? ` <span class="hint">· ${esc(tr(L.message))}</span>` : ""));

  for (const team of ["blue", "red"]) {
    paint($("#slots-" + team), slotsHtml(L, team, me, amHost));
    const full = L.members.filter((m) => m.team === team).length >= 4;
    paint($("#switch-" + team), L.phase === "lobby" && !L.draft && me.team && me.team !== team && !full ? `<button data-team="${team}">${t("switch_here")}</button>` : "");
  }
  renderDraft(L, me);
  renderSpecs(L, me, amHost);

  // my controls (after a draft: one of my team's cars)
  const car = lobbyCar(L, me);
  const drafted = !!(L.draft && L.draft.done);
  fill($("#lobby-car"), L.draft ? pool(L.draft, me.team).map((c) => [String(c), carName(c)]) : carOptions(), car);
  fill($("#lobby-skin"), skinOptions(car), (S.settings.skins || {})[carName(car)] ?? "0");
  const locked = L.phase !== "lobby";
  const playing = isPlayer(me);
  $("#not-playing").hidden = me.team !== "none";
  $("#spectating").hidden = me.team !== "spec";
  $("#drafted-car").hidden = !playing || !drafted;
  document.querySelectorAll(".player-only").forEach((e) => (e.hidden = !playing || legacy || (L.draft && !drafted)));
  $("#legacy-pick").hidden = !playing || !legacy;
  $("#lobby-car").disabled = $("#lobby-skin").disabled = locked;
  const ready = $("#ready");
  ready.hidden = !playing;
  ready.disabled = locked;
  ready.classList.toggle("on", !!me.ready);
  ready.textContent = L.phase === "draft" ? t("drafting") : locked ? (me.inMatch ? t("in_the_match") : t("match_running_pill")) : me.ready ? t("ready_on") : t("ready");
  $("#relaunch").hidden = !(L.phase === "playing" && me.inMatch && !S.gameRunning);

  // host box
  $("#hostbox").hidden = !amHost;
  if (amHost && S.hostSetup) {
    const H = S.hostSetup;
    fill($("#h-arena"), C.arenas.map((a) => [String(a.id), a.name]), H.arena);
    fill($("#h-score"), SCORES(), H.score);
    document.querySelectorAll(".steam-only").forEach((e) => (e.hidden = legacy));
    for (const team of ["blue", "red"]) {
      $(`#h-${team}-bots`).textContent = L.teams[team].bots + (H[team].bots > L.teams[team].bots ? ` (${H[team].bots})` : "");
      fill($(`#h-${team}-diff`), DIFFS(), H[team].difficulty);
    }
    if (document.activeElement !== $("#h-auto")) $("#h-auto").checked = H.autoStart;
    $("#h-plays").checked = isPlayer(me);
    $("#h-plays").disabled = locked || !!L.draft;
    $("#h-spec").checked = H.spectators;
    $("#h-draft").checked = H.draft;
    $("#h-spec").disabled = locked;
    $("#h-draft").disabled = $("#h-order").disabled = locked;
    $("#h-order-box").hidden = !H.draft;
    const order = $("#h-order");
    if (document.activeElement !== order && order.value !== H.draftOrder) order.value = H.draftOrder;
    $("#start").hidden = locked;
    $("#start").disabled = !!L.cantStart;
    $("#start").textContent = L.nextIsDraft ? t("start_draft") : t("start_now");
    $("#stop").hidden = !locked;
    $("#stop").textContent = L.phase === "draft" ? t("stop_draft") : t("stop_match");
    $("#draft-reset").hidden = !(L.draft && L.phase === "lobby");
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
  const legacy = isLegacy(L.build);
  const drafting = L.draft && !L.draft.done;
  const out = humans.map((m) => `
    <div class="slot${m.id === L.me ? " me" : ""}">
      <div class="who"><div class="name">${esc(m.name)}${m.id === L.me ? ` <span class='hint'>${t("you_tag")}</span>` : ""}</div>
        <div class="car">${legacy ? t("picks_in_game") : drafting ? t("drafting") : esc(carName(m.car)) + (m.skin && m.skin !== "0" ? " · " + esc(skinName(m.car, m.skin)) : "")}</div></div>
      ${m.host ? `<span class="tag host">${t("tag_host")}</span>` : ""}
      ${m.ready ? `<span class="tag ready">${t("tag_ready")}</span>` : L.phase === "lobby" ? `<span class="tag">${t("tag_not_ready")}</span>` : ""}
      ${amHost && !m.host && L.phase === "lobby" ? `<button class="x" title="${t("remove_title")}" data-kick="${esc(m.id)}">✕</button>` : ""}
    </div>`);
  const left = botPool(L, team);
  for (let i = 0; i < T.bots; i++) {
    // After a draft the bots drive the team's cars nobody took.
    const car = L.draft ? (drafting ? "" : left[i] || "random") : (H && H[team].cars[i]) || T.cars[i] || "default";
    const carSel = amHost && L.phase === "lobby" && !legacy && !L.draft
      ? `<select data-botcar="${team}:${i}">${[["default", t("default_car")], ["random", t("random_car")], ...carOptions()]
          .map(([v, text]) => `<option value="${esc(v)}"${v === String(car) ? " selected" : ""}>${esc(text)}</option>`).join("")}</select>`
      : "";
    out.push(`<div class="slot bot"><div class="who"><div class="name">${t("bot")}</div>
      <div class="car">${esc(diffName(T.difficulty))}${carSel || legacy || !car ? "" : " · " + esc(car === "random" ? t("random_car") : carName(car))}</div></div>${carSel}</div>`);
  }
  for (let i = humans.length + T.bots; i < 4; i++) out.push(`<div class="slot open">${t("open_slot")}</div>`);
  return out.join("");
}

// ----- draft -----
function draftStatus(L, me) {
  const D = L.draft;
  const turn = D && D.turns[D.step];
  if (!turn) return "";
  const what = nCars(turn.count);
  if (me.team === turn.team) return t(turn.pick ? "draft_your_pick" : "draft_your_ban", what);
  return t(turn.pick ? "draft_their_pick" : "draft_their_ban", teamName(turn.team), what);
}

function renderDraft(L, me) {
  const D = L.draft;
  const el = $("#draft");
  el.hidden = !D;
  if (!D) return paint(el, "");
  const turn = D.turns[D.step];
  const myTurn = !!turn && L.phase === "draft" && me.team === turn.team;
  const A = D.first, B = A === "blue" ? "red" : "blue";
  const turns = D.turns.map((x, i) => `<span class="turn ${x.team}${i === D.step && !D.done ? " now" : i < D.step ? " past" : ""}">`
    + `${x.team === A ? "A" : "B"} · ${t(x.pick ? "draft_pick_n" : "draft_ban_n", x.count)}</span>`).join("");
  const chips = (list) => list.length ? list.map((c) => `<span class="dcar">${esc(carName(c.car))}</span>`).join("") : `<span class="hint">—</span>`;
  const side = (team) => `<div class="dteam ${team}"><h3>${esc(teamName(team))} (${team === A ? "A" : "B"})</h3>
    <div class="dl">${t("draft_bans")}</div><div class="dchips ban">${chips(D.bans.filter((c) => c.team === team))}</div>
    <div class="dl">${t("draft_picks")}</div><div class="dchips">${chips(D.picks.filter((c) => c.team === team))}</div></div>`;
  let grid = "";
  if (!D.done) {
    const state = (id) => {
      const b = D.bans.find((c) => c.car === id), p = D.picks.find((c) => c.car === id);
      if (b) return "banned " + b.team;
      if (p) return "picked " + p.team;
      return D.pending.includes(id) ? "sel" : "";
    };
    grid = `<div class="grid">${C.cars.map((c) => {
      const id = String(c.id), st = state(id), free = !st || st === "sel";
      return `<button class="tile ${st}" data-draft-car="${esc(id)}" ${myTurn && free ? "" : "disabled"}>${esc(c.name)}</button>`;
    }).join("")}</div>`;
  }
  const lock = myTurn ? `<div class="row lockrow"><button class="primary big" id="draft-lock" ${D.pending.length === turn.count ? "" : "disabled"}>${t("draft_lock", D.pending.length, turn.count)}</button>
    <span class="hint">${t("draft_hidden_hint")}</span></div>` : "";
  paint(el, `<div class="draft-head"><h2>${t("draft_title")}</h2><span class="hint">${t("draft_first", teamName(A))}</span></div>
    <div class="turns">${turns}</div>
    <div class="draft-body">${side(A)}${grid}${side(B)}</div>${lock}`);
}

// ----- spectators -----
function renderSpecs(L, me, amHost) {
  const specs = L.members.filter((m) => m.team === "spec");
  const el = $("#specs");
  el.hidden = !L.spectators && !specs.length;
  const seats = specs.map((m) => `<div class="slot${m.id === L.me ? " me" : ""}"><div class="who"><div class="name">${esc(m.name)}${m.id === L.me ? ` <span class='hint'>${t("you_tag")}</span>` : ""}</div>
      <div class="car">${t("spectator")}</div></div>${m.host ? `<span class="tag host">${t("tag_host")}</span>` : ""}
      ${amHost && !m.host && L.phase === "lobby" ? `<button class="x" title="${t("remove_title")}" data-kick="${esc(m.id)}">✕</button>` : ""}</div>`);
  const canWatch = L.spectators && me.team !== "spec" && !me.inMatch && !(isPlayer(me) && (L.phase !== "lobby" || L.draft));
  for (let i = specs.length; i < 2; i++)
    seats.push(`<div class="slot open">${canWatch && i === specs.length ? `<button data-team="spec">${t("watch")}</button>` : t("open_slot")}</div>`);
  paint(el, `<div class="team-head"><h2>${t("spectators_title")}</h2><span class="hint">${t("spectators_info")}</span></div><div class="spec-slots">${seats.join("")}</div>`);
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
  const busy = S.setupStatus === "running";
  paint($("#s-game"), `<p class="hint">${t("copies_hint")}</p>
    ${S.copies.map((c) => `<div class="copy">
      <div class="copy-head"><b>${esc(buildName(c.build))}</b>
        <span class="${c.ready ? (c.needsUpdate ? "warn-t" : "ok-t") : "hint"}">${busy && S.setupBuild === c.build ? t("copy_setting_up")
          : c.ready ? t(c.needsUpdate ? "copy_old" : "copy_ready", esc(c.version)) : t("not_set_up")}</span></div>
      <p class="hint">${c.dir ? t("copy_from", esc(c.dir)) + (c.ready ? "<br>" : "") : c.ready ? "" : t("copy_not_found")}${c.ready ? t("copy_in", esc(c.instance)) : ""}</p>
      ${c.dir && c.canSetup ? `<button data-setup-build="${esc(c.build)}" ${busy ? "disabled" : ""}>${c.ready ? t("update_game") : t("set_up")}</button>` : ""}
    </div>`).join("")}
    <div class="row">${S.canSetup ? `<button data-act="choose-game" ${busy ? "disabled" : ""}>${t("add_copy")}</button>` : ""}
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
  if (b.dataset.setupBuild) return act("setup", { build: b.dataset.setupBuild });
  if (b.dataset.draftCar) return act("lobby/draft", { action: "select", car: b.dataset.draftCar });
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
      if (!S.hosting && !(await act("host/open", { build: $("#host-build").value || (S.builds || [])[0] }))) return;
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
    case "draft-lock": return act("lobby/draft", { action: "lock" });
    case "draft-reset":
      if (confirm(t("confirm_draft_reset"))) return act("host/draft-reset", {});
      return;
    case "start": return act("host/start", {});
    case "stop":
      if (confirm(t(S.lobby.phase === "draft" ? "confirm_stop_draft" : "confirm_stop"))) return act("host/stop", {});
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
    case "lobby-car":
      if (S.lobby && S.lobby.draft) return act("lobby/update", { car: el.value }); // drafted: a lobby choice, not a setting
      return act("settings", { car: el.value });
    case "s-car": return act("settings", { car: el.value });
    case "lobby-skin": {
      const L = S.lobby, me = L ? L.members.find((m) => m.id === L.me) || {} : {};
      return act("settings", { skin: el.value, skinCar: String(lobbyCar(L, me)) });
    }
    case "s-skin": return act("settings", { skin: el.value });
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
    case "h-spec": case "h-draft": case "h-order": {
      const H = structuredClone(S.hostSetup);
      H.spectators = $("#h-spec").checked;
      H.draft = $("#h-draft").checked;
      H.draftOrder = $("#h-order").value;
      const r = await act("host/configure", H);
      if (r && el.id === "h-order" && S.hostSetup.draftOrder !== el.value.trim().toUpperCase().split(/[\s,;]+/).join(" ")) toast(t("draft_order_bad"));
      return;
    }
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
  const want = location.hash.slice(1); // e.g. #settings
  show(S && S.lobby ? "lobby" : ["play", "host", "settings", "help"].includes(want) ? want : "play");
  setInterval(poll, 700);
  setInterval(() => { if (view === "play" && !document.hidden) discover(); }, 15000);
})();
