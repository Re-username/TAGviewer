// Хелперы для Blazor: тема, буфер обмена, File System Access API,
// drag&drop с файловыми хэндлами, синхронизация состояния между окнами.
window.plcTags = {
    // ---------- тема ----------
    getTheme: function () {
        try { return localStorage.getItem("plctags-theme"); } catch (e) { return null; }
    },
    setTheme: function (t) {
        if (t === true) t = "dark"; if (t === false) t = "light"; // обратная совместимость
        try { localStorage.setItem("plctags-theme", t); } catch (e) { }
        var el = document.documentElement;
        el.classList.toggle("dark", t === "dark");
        el.classList.toggle("cyberpunk", t === "cyberpunk");
    },

    // ---------- звуки интерфейса (WebAudio, только в киберпанк-теме) ----------
    _ac: null,
    sfx: function (kind) {
        try {
            if (localStorage.getItem("plctags-theme") !== "cyberpunk") return;
            var AC = window.AudioContext || window.webkitAudioContext;
            if (!AC) return;
            this._ac = this._ac || new AC();
            var ac = this._ac, t0 = ac.currentTime;
            var tone = function (f0, f1, dur, type, vol, delay) {
                var o = ac.createOscillator(), g = ac.createGain();
                o.type = type || "square";
                var ts = t0 + (delay || 0);
                o.frequency.setValueAtTime(f0, ts);
                if (f1) o.frequency.exponentialRampToValueAtTime(f1, ts + dur);
                g.gain.setValueAtTime(vol, ts);
                g.gain.exponentialRampToValueAtTime(0.0001, ts + dur);
                o.connect(g); g.connect(ac.destination);
                o.start(ts); o.stop(ts + dur + 0.02);
            };
            if (kind === "error") { tone(260, null, 0.09, "square", 0.05); tone(220, null, 0.12, "square", 0.05, 0.1); }
            else if (kind === "ok") { tone(600, 900, 0.08, "sine", 0.05); }
            else { tone(1000, null, 0.04, "square", 0.03); } // click
        } catch (e) { }
    },

    // ---------- буфер обмена ----------
    copyText: async function (text) {
        try { await navigator.clipboard.writeText(text); return true; }
        catch (e) { return false; }
    },

    // ---------- заголовок окна ----------
    setTitle: function (title) {
        try { document.title = title; } catch (e) { }
    },

    // ---------- настройки интерфейса (шрифт, размер, плотность) ----------
    _uiFonts: {
        system: '"Segoe UI", system-ui, sans-serif',
        segoe: '"Segoe UI", sans-serif',
        consolas: 'Consolas, "Courier New", monospace',
        cascadia: '"Cascadia Mono", Consolas, monospace',
        courier: '"Courier New", monospace'
    },
    _applyUi: function (o) {
        const r = document.documentElement.style;
        if (!o) return;
        const ff = this._uiFonts[o.ff] || this._uiFonts.system;
        r.setProperty("--u-ff", ff);
        r.setProperty("--u-ff-mono", o.mono ? ff : (o.ff === "consolas" || o.ff === "cascadia" || o.ff === "courier" ? ff : "Consolas"));
        r.setProperty("--u-fs", (o.fs || 13) + "px");
        r.setProperty("--u-fs-sm", Math.max(10, (o.fs || 13) - 1) + "px");
        r.setProperty("--u-fs-base", ((o.fs || 13) + 1) + "px");
        const pad = o.density === "compact" ? "0px" : o.density === "roomy" ? "5px" : "2px";
        r.setProperty("--u-pady", pad);
    },
    getUi: function () {
        try { return localStorage.getItem("plctags-ui") || "{}"; } catch (e) { return "{}"; }
    },
    setUi: function (json) {
        try {
            localStorage.setItem("plctags-ui", json);
            this._applyUi(JSON.parse(json));
            return true;
        } catch (e) { return false; }
    },

    // ---------- мост WebView2 (exe) через WebMessage: файлы по полным путям ----------
    _wv: function () {
        try {
            const w = window.chrome;
            return (w && w.webview && typeof w.webview.postMessage === "function") ? w.webview : null;
        } catch (e) { return null; }
    },
    _wvPending: {},
    _wvNextId: 1,
    _wvInited: false,
    // Вызов файлового моста в exe. type: pickFiles | readFile | getLastWrite. Ответ — payload (JSON-строка).
    _wvCall: function (type, payload) {
        const wv = this._wv();
        if (!wv) return Promise.resolve(null);
        if (!this._wvInited) {
            this._wvInited = true;
            const self = this;
            wv.addEventListener("message", function (e) {
                const m = e.data;
                if (!m || !m.id) return;
                const res = self._wvPending[m.id];
                if (res) { delete self._wvPending[m.id]; res(m.payload == null ? null : m.payload); }
            });
        }
        const self = this;
        return new Promise(function (resolve) {
            const rid = "r" + (self._wvNextId++);
            self._wvPending[rid] = resolve;
            try { wv.postMessage({ id: rid, type: type, payload: payload == null ? null : String(payload) }); }
            catch (err) { delete self._wvPending[rid]; resolve(null); return; }
            setTimeout(function () { // страховка от потери ответа
                if (self._wvPending[rid]) { delete self._wvPending[rid]; resolve(null); }
            }, 120000);
        });
    },
    _bridge: function () { return this._wv(); },
    // Полный путь Windows (C:\... или \\server\...) — значит файл пришёл через мост
    _isPath: function (id) {
        return typeof id === "string" && (/^[A-Za-z]:[\\/]/.test(id) || id.indexOf("\\\\") === 0);
    },
    _b64ToBytes: function (b64) {
        const bin = atob(b64);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        return bytes;
    },
    _bridgeFile: function (o) {
        return { id: o.id, path: o.path, name: o.name, size: o.size, modified: o.modified, bytes: this._b64ToBytes(o.bytes) };
    },

    // ---------- File System Access API (Chrome/Edge) ----------
    fsSupported: function () { return !!this._bridge() || !!window.showOpenFilePicker; },

    clickFileInput: function () {
        var inp = document.querySelector('input[type="file"]');
        if (inp) inp.click();
    },
    _handles: {},
    _nextHandleId: 1,

    // Открыть диалог выбора файлов с сохранением хэндлов (для последующего «Обновить»)
    pickFiles: async function (presetName) {
        const br = this._bridge();
        if (br) {
            // exe-режим: родной диалог Windows, файлы сразу с содержимым и полным путём
            try {
                const json = await this._wvCall("pickFiles", presetName || null);
                const arr = JSON.parse(json || "[]");
                const result = [];
                for (const o of arr) result.push(this._bridgeFile(o));
                return result;
            } catch (e) { return []; }
        }
        if (!window.showOpenFilePicker) return null;
        try {
            const handles = await window.showOpenFilePicker({ multiple: true });
            const result = [];
            for (const h of handles) {
                if (h.kind !== "file") continue;
                const id = "h" + (this._nextHandleId++);
                this._handles[id] = h;
                this._persistHandle(id, h);
                const f = await h.getFile();
                result.push({ id: id, name: f.name, size: f.size, modified: f.lastModified });
            }
            return result;
        } catch (e) { return []; } // пользователь отменил
    },

    // Прочитать файл по хэндлу (с запросом разрешения при необходимости)
    readFileByHandle: async function (id) {
        const br = this._bridge();
        if (br && this._isPath(id)) {
            try {
                const json = await this._wvCall("readFile", id);
                if (!json) return null;
                return this._bridgeFile(JSON.parse(json));
            } catch (e) { return null; }
        }
        const h = this._handles[id];
        if (!h) return null;
        try {
            let p = await h.queryPermission({ mode: "read" });
            if (p !== "granted") p = await h.requestPermission({ mode: "read" });
            if (p !== "granted") return null;
            const f = await h.getFile();
            const buf = await f.arrayBuffer();
            return { bytes: new Uint8Array(buf), name: f.name, size: f.size, modified: f.lastModified };
        } catch (e) { return null; }
    },

    // Проверить, изменился ли файл (без чтения содержимого)
    checkModified: async function (id, knownModified) {
        const br = this._bridge();
        if (br && this._isPath(id)) {
            try {
                const t = parseInt(await this._wvCall("getLastWrite", id), 10);
                return isNaN(t) || t < 0 ? knownModified : t;
            } catch (e) { return knownModified; }
        }
        const h = this._handles[id];
        if (!h) return knownModified;
        try {
            const f = await h.getFile();
            return f.lastModified;
        } catch (e) { return knownModified; }
    },

    // Проверить разрешение без запроса (запрос требует жеста пользователя)
    checkPermission: async function (id) {
        const h = this._handles[id];
        if (!h) return "none";
        try { return await h.queryPermission({ mode: "read" }); }
        catch (e) { return "none"; }
    },

    // ---------- IndexedDB: хэндлы переживают перезапуск приложения ----------
    _db: null,
    _openDb: function () {
        if (this._db) return Promise.resolve(this._db);
        const self = this;
        return new Promise(function (resolve, reject) {
            const req = indexedDB.open("plcTags-db", 1);
            req.onupgradeneeded = function () {
                req.result.createObjectStore("handles");
            };
            req.onsuccess = function () { self._db = req.result; resolve(self._db); };
            req.onerror = function () { reject(req.error); };
        });
    },
    _persistHandle: function (id, handle) {
        this._openDb().then(function (db) {
            db.transaction("handles", "readwrite").objectStore("handles").put(handle, id);
        }).catch(function () { });
    },
    _removeHandle: function (id) {
        this._openDb().then(function (db) {
            db.transaction("handles", "readwrite").objectStore("handles").delete(id);
        }).catch(function () { });
    },

    /// Восстановить хэндлы по id. Возвращает [{id, name, modified, permission}].
    restoreHandles: async function (ids) {
        const result = [];
        const br = this._bridge();
        // exe-режим: пути не требуют хэндлов и разрешений — проверяем существование
        if (br) {
            for (const id of ids) {
                if (!this._isPath(id)) continue;
                try {
                    const t = parseInt(await this._wvCall("getLastWrite", id), 10);
                    if (isNaN(t) || t < 0) continue; // файл недоступен (сетевой диск отключён и т.п.)
                    result.push({ id: id, name: id.split(/[\\/]/).pop(), modified: t, permission: "granted" });
                } catch (e) { /* недоступен */ }
            }
        }
        if (!window.showOpenFilePicker) return result;
        let db;
        try { db = await this._openDb(); } catch (e) { return result; }
        for (const id of ids) {
            if (this._isPath(id)) continue; // пути уже обработаны через мост
            try {
                const handle = await new Promise(function (resolve, reject) {
                    const req = db.transaction("handles", "readonly").objectStore("handles").get(id);
                    req.onsuccess = function () { resolve(req.result); };
                    req.onerror = function () { reject(req.error); };
                });
                if (!handle || handle.kind !== "file") continue;
                this._handles[id] = handle;
                const num = parseInt(id.substring(1), 10);
                if (num >= this._nextHandleId) this._nextHandleId = num + 1;
                const f = await handle.getFile();
                const perm = await this.checkPermission(id);
                result.push({ id: id, name: f.name, modified: f.lastModified, permission: perm });
            } catch (e) { /* хэндл недоступен */ }
        }
        return result;
    },

    /// Запросить разрешения (вызывать только из обработчика клика!).
    /// Возвращает { granted: [id...], denied: [id...] }.
    requestPermissions: async function (ids) {
        const granted = [], denied = [];
        for (const id of ids) {
            if (this._isPath(id)) { granted.push(id); continue; } // exe: разрешения не нужны
            const h = this._handles[id];
            if (!h) { denied.push(id); continue; }
            try {
                const p = await h.requestPermission({ mode: "read" });
                (p === "granted" ? granted : denied).push(id);
            } catch (e) { denied.push(id); }
        }
        return { granted: granted, denied: denied };
    },

    /// Удалить сохранённые хэндлы (при закрытии файла)
    dropHandles: function (ids) {
        for (const id of ids) {
            delete this._handles[id];
            this._removeHandle(id);
        }
    },

    // ---------- drag&drop с хэндлами ----------
    initDrop: function (dotNetRef) {
        if (this._dropInited) return;
        this._dropInited = true;
        const self = this;
        window.addEventListener("dragover", function (e) { e.preventDefault(); });
        window.addEventListener("drop", async function (e) {
            if (!e.dataTransfer) return;
            e.preventDefault();
            const files = [];
            const items = e.dataTransfer.items;
            for (let i = 0; i < items.length; i++) {
                const item = items[i];
                if (item.kind !== "file") continue;
                if (item.getAsFileSystemHandle) {
                    try {
                        const h = await item.getAsFileSystemHandle();
                        if (h && h.kind === "file") {
                            const id = "h" + (self._nextHandleId++);
                            self._handles[id] = h;
                            self._persistHandle(id, h);
                            const f = await h.getFile();
                            const buf = await f.arrayBuffer();
                            files.push({ id: id, name: f.name, size: f.size, modified: f.lastModified, bytes: new Uint8Array(buf) });
                            continue;
                        }
                    } catch (err) { /* fallback ниже */ }
                }
                const f = item.getAsFile();
                if (f) {
                    const buf = await f.arrayBuffer();
                    files.push({ id: null, name: f.name, size: f.size, modified: f.lastModified, bytes: new Uint8Array(buf) });
                }
            }
            if (files.length > 0) await dotNetRef.invokeMethodAsync("OnFilesDropped", files);
        });
    },

    // ---------- сохранение состояния / синхронизация окон ----------
    saveState: function (json) {
        try { localStorage.setItem("plctags-workspace", json); return true; }
        catch (e) { return false; } // переполнение — большие деревья
    },
    loadState: function () {
        try { return localStorage.getItem("plctags-workspace"); } catch (e) { return null; }
    },
    openTabWindow: function (tabId) {
        const url = location.pathname + "?view=" + encodeURIComponent(tabId);
        window.open(url, "tagwindow_" + tabId, "popup,width=900,height=700");
    },

    // Глобальные клавиши: F1 — справка
    initGlobalKeys: function (dotNetRef) {
        if (this._globalKeysInited) return;
        this._globalKeysInited = true;
        window.addEventListener("keydown", function (e) {
            if (e.key === "F1") {
                e.preventDefault();
                dotNetRef.invokeMethodAsync("ToggleHelp");
            }
        });
    }
};

// Применить сохранённые настройки интерфейса сразу при загрузке (до старта Blazor)
try { window.plcTags._applyUi(JSON.parse(localStorage.getItem("plctags-ui") || "{}")); } catch (e) { }
