// ===== TaskbarLyrics Overlay Engine v3.5 =====
const api = window.chrome.webview;

let lines = [];
let mode = 'idle';
let playing = false;

// Spectrum state
let specBands = new Array(24).fill(0.05);
let specTargets = new Array(24).fill(0.05);
let specActive = false;
let specStyle = 0;
let specResponse = 0.65;
let specRefreshMs = 33;
let specRaf = 0, lastSpecTick = 0;

// Animation tracking
var _animTimers = [];
var _lastIdx = -1;
var _transStyle = 0;

function _animClear() {
    _animTimers.forEach(function(id) { clearTimeout(id); });
    _animTimers = [];
    var row = document.getElementById('rowCurrent');
    if (row) {
        row.style.animation = 'none';
        void row.offsetHeight; // 强制重排，停止当前动画
        row.style.animation = ''; // 清除内联样式，否则后续动画类会被 inline:none 永久压制
        row.classList.remove('anim-slideUp-out','anim-slideUp-in','anim-fade-out','anim-fade-in','anim-compactSlide-out','anim-compactSlide-in');
    }
}

function _animSet(fn, ms) {
    var id = setTimeout(function() { _animTimers = _animTimers.filter(function(t) { return t !== id; }); fn(); }, ms);
    _animTimers.push(id);
    return id;
}

// ==================== MSG HANDLER ====================
api.addEventListener('message', function(e) {
    try {
        var raw = e.data;
        var m = (typeof raw === 'string') ? JSON.parse(raw) : raw;
        switch (m.type) {
            case 'config': applyTheme(m); break;
            case 'lyrics': setLyrics(m); break;
            case 'cover': setCover(m.cover); break;
            case 'track': setTrack(m); break;
            case 'state': handleState(m); break;
            case 'spectrum': updateSpectrum(m); break;
            case 'layout': applyLayout(m); break;
        }
    } catch(ex) { /* ignore */ }
});

// ==================== THEME ====================
function hexToRgb(hex) {
    var h = String(hex || '').replace('#','');
    if (h.length !== 6) return '59,208,255';
    var r = parseInt(h.substr(0,2),16);
    var g = parseInt(h.substr(2,2),16);
    var b = parseInt(h.substr(4,2),16);
    return r + ',' + g + ',' + b;
}

function applyTheme(m) {
    var r = document.documentElement.style;
    var cfg = m.theme || m;
    var pal = cfg.palette;
    var userText = cfg.textColor ? '#' + cfg.textColor.replace(/#/g,'') : null;
    if (cfg.textColor) r.setProperty('--text-color', userText);
    if (cfg.bgColor) { r.setProperty('--bg-color', '#' + cfg.bgColor.replace(/#/g,'')); r.setProperty('--bg-rgb', hexToRgb(cfg.bgColor)); }
    if (pal) {
        // 封面取色色板:应用到面板/装饰;歌词文字色优先用用户手动设置,否则用取色
        r.setProperty('--accent-color', '#' + pal.accent.replace(/#/g,''));
        r.setProperty('--accent-rgb', hexToRgb(pal.accent));
        r.setProperty('--surface-rgb', pal.surfaceRgb);
        var textPrimary = userText || '#' + pal.textPrimary.replace(/#/g,'');
        var textSecondary = userText || '#' + pal.textSecondary.replace(/#/g,'');
        r.setProperty('--text-primary', textPrimary);
        r.setProperty('--text-secondary', textSecondary);
        // 文字阴影随文字深浅自适应:深色文字用白色微光,浅色文字用黑色微影
        var tr2 = parseInt(textPrimary.replace('#','').substr(0,2),16);
        var tg2 = parseInt(textPrimary.replace('#','').substr(2,2),16);
        var tb2 = parseInt(textPrimary.replace('#','').substr(4,2),16);
        r.setProperty('--text-shadow', (tr2*0.299 + tg2*0.587 + tb2*0.114) < 128
            ? '0 1px 2px rgba(255,255,255,0.35)'
            : '0 1px 2px rgba(0,0,0,0.45)');
    } else {
        // 系统主题兜底:波形色作强调,用户背景色作表面
        if (cfg.accentColor) { r.setProperty('--accent-color', '#' + cfg.accentColor.replace(/#/g,'')); r.setProperty('--accent-rgb', hexToRgb(cfg.accentColor)); }
        if (cfg.bgColor) r.setProperty('--surface-rgb', hexToRgb(cfg.bgColor));
        r.setProperty('--text-primary', userText || '#FFFFFF');
        r.setProperty('--text-secondary', userText || '#B9B9C2');
    }
    if (cfg.fontSize != null) r.setProperty('--font-size', cfg.fontSize + 'px');
    if (cfg.coverSize != null) r.setProperty('--cover-size', cfg.coverSize + 'px');
    if (cfg.showCover != null) document.getElementById('coverArea').classList.toggle('hidden', !cfg.showCover);
    if (cfg.coverStyle != null && cfg.coverStyle >= 0) {
        var c = document.getElementById('coverArea');
        c.classList.remove('cover-square','cover-rounded','cover-circle');
        var cls = ['cover-square','cover-rounded','cover-circle'][cfg.coverStyle];
        if (cls) c.classList.add(cls);
    }
    if (cfg.backgroundEnabled != null) document.body.classList.toggle('bg-disabled', !cfg.backgroundEnabled);
    if (cfg.textShadow != null) document.getElementById('root').classList.toggle('text-shadow-on', cfg.textShadow);
    if (cfg.bgOpacity != null) r.setProperty('--bg-opacity', cfg.bgOpacity);
    if (cfg.showSpectrum != null && !cfg.showSpectrum) {
        specActive = false;
        document.getElementById('spectrum').classList.remove('active');
        if (specRaf) { cancelAnimationFrame(specRaf); specRaf = 0; lastSpecTick = 0; }
    }
    if (cfg.spectrumStyle != null) specStyle = cfg.spectrumStyle;
    if (cfg.spectrumResponse != null) specResponse = cfg.spectrumResponse;
    if (cfg.spectrumRefreshMs != null) specRefreshMs = cfg.spectrumRefreshMs;
    if (cfg.spectrumOpacity != null) r.setProperty('--spectrum-opacity', cfg.spectrumOpacity);
    if (cfg.spectrumHeightRatio != null) r.setProperty('--spectrum-height-ratio', cfg.spectrumHeightRatio);
    if (cfg.lyricTransition != null && cfg.lyricTransition !== _transStyle) {
        _animClear();
        _transStyle = cfg.lyricTransition;
        _lastIdx = -1;
    }
    if (cfg.tbCoverXOffset != null) r.setProperty('--tb-cover-x-offset', cfg.tbCoverXOffset+'px');
    if (cfg.tbCoverYOffset != null) r.setProperty('--tb-cover-y-offset', cfg.tbCoverYOffset+'px');
    if (cfg.tbContentXOffset != null) r.setProperty('--tb-content-x-offset', cfg.tbContentXOffset+'px');
    if (cfg.tbContentYOffset != null) r.setProperty('--tb-content-y-offset', cfg.tbContentYOffset+'px');
}

function applyLayout(m) {
    var root = document.getElementById('root');
    var cover = document.getElementById('coverArea');
    root.classList.remove('layout-horizontal','layout-topbottom');
    if (m.coverLayout === 1) {
        root.classList.add('layout-topbottom');
        cover.style.width = 'calc(100% - ' + (m.tbCoverXOffset||0) + 'px)';
        cover.style.height = (m.coverSize||40) + 'px';
        cover.style.marginLeft = (m.tbCoverXOffset||0) + 'px';
        cover.style.marginBottom = (m.tbCoverToContentSpacing||8) + 'px';
    } else {
        root.classList.add('layout-horizontal');
        cover.style.width = (m.coverSize||40) + 'px';
        cover.style.height = (m.coverSize||40) + 'px';
    }
}

// ==================== DATA ====================
function setLyrics(m) {
    lines = m.lines || []; _lastIdx = -1; _animClear();
    var hint = document.getElementById('unsyncedHint');
    if (hint) hint.style.display = (m.synced === false) ? 'block' : 'none';
}
function setTrack(m) {
    var ti = document.getElementById('trackInfoLine');
    if (m.title) ti.textContent = m.artist ? m.title + ' - ' + m.artist : m.title;
    setCover(m.cover);
}
function setCover(uri) {
    var img = document.getElementById('coverImg');
    if (uri) { img.src = uri; img.classList.add('loaded'); }
    else { img.classList.remove('loaded'); img.src = ''; }
}

// ==================== STATE MACHINE ====================
function handleState(m) {
    playing = (m.status === 'playing');
    specActive = m.showSpectrum === true;
    var root = document.getElementById('root');
    var lyricsEl = document.getElementById('lyricsArea');
    var trackEl = document.getElementById('trackInfoLine');
    var specEl = document.getElementById('spectrum');
    root.classList.remove('dim', 'paused');

    if (m.mode === 'idle') {
        root.classList.add('dim');
        lyricsEl.style.opacity = '0';
        trackEl.classList.remove('visible');
        specEl.classList.remove('active');
        specActive = false;
    } else if (m.mode === 'loading') {
        root.classList.add('dim');
        lyricsEl.style.opacity = '0';
        trackEl.classList.add('visible');
        specEl.classList.remove('active');
        specActive = false;
    } else {
        root.classList.add('paused');
        if (playing) root.classList.remove('paused');
        if (m.mode === 'lyrics') { lyricsEl.style.opacity = '1'; trackEl.classList.remove('visible'); if (lines.length > 0) updateLyrics(m); }
        else if (m.mode === 'trackinfo') { lyricsEl.style.opacity = '0'; trackEl.classList.add('visible'); }
        else { lyricsEl.style.opacity = '0'; trackEl.classList.remove('visible'); }
        specEl.classList.toggle('active', specActive);
    }
    mode = m.mode;
    if (specActive && !specRaf) { lastSpecTick = 0; specLoop(); }
    if (!specActive && specRaf) { cancelAnimationFrame(specRaf); specRaf = 0; lastSpecTick = 0; }
    reportSize();
}

function updateLyrics(m) {
    var idx = Math.max(0, Math.min(lines.length - 1, m.index));
    if (idx === _lastIdx) return;
    _lastIdx = idx;
    var line = lines[idx];
    if (!line) return;
    var nextLine = idx + 1 < lines.length ? lines[idx + 1] : null;
    var rowCur = document.getElementById('rowCurrent');
    var curFg = document.getElementById('curFg');
    var rowNext = document.getElementById('rowNext');

    var pairs = {0:['anim-slideUp-out','anim-slideUp-in'], 1:['anim-fade-out','anim-fade-in'], 2:['anim-compactSlide-out','anim-compactSlide-in']};
    var cls = pairs[_transStyle] || [null, null];

    function applyText() {
        document.getElementById('curBg').textContent = '';
        curFg.textContent = line.x;
        curFg.style.width = '100%';
        if (cls[1]) {
            rowCur.style.animation = ''; // 清除残留内联样式，确保动画类生效
            rowCur.classList.remove(cls[1]);
            void rowCur.offsetWidth; // 强制重排，确保入场动画每次都重新播放
            rowCur.classList.add(cls[1]);
            _animSet(function() { rowCur.classList.remove(cls[1]); }, 350);
        }
    }
    if (cls[0] && curFg.textContent) {
        rowCur.style.animation = ''; // 清除残留内联样式，确保动画类生效
        rowCur.classList.remove(cls[0]);
        void rowCur.offsetWidth; // 强制重排，确保出场动画每次都重新播放
        rowCur.classList.add(cls[0]);
        _animSet(function() { rowCur.classList.remove(cls[0]); applyText(); }, 280);
    }
    else { applyText(); }

    if (nextLine) { document.getElementById('nextBg').textContent = nextLine.x; document.getElementById('nextFg').textContent = ''; rowNext.style.display = ''; }
    else { rowNext.style.display = 'none'; }
}

// ==================== SPECTRUM ====================
function updateSpectrum(m) { specTargets = m.bands || new Array(24).fill(0); }
function specLoop(ts) {
    if (!specActive) { specRaf = 0; return; }
    if (!lastSpecTick) lastSpecTick = ts || 0;
    if ((ts||0) - lastSpecTick >= specRefreshMs) { lastSpecTick = ts||0; drawSpectrum(); }
    specRaf = requestAnimationFrame(specLoop);
}
function drawSpectrum() {
    var canvas = document.getElementById('spectrum');
    if (!canvas || !specActive) return;
    var w = canvas.parentElement.clientWidth, h = canvas.parentElement.clientHeight;
    if (w <= 0 || h <= 0) return;
    var dpr = window.devicePixelRatio || 1; // 高分屏按 DPR 渲染，避免纤细条形模糊
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr))
    { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); }
    for (var i = 0; i < 24; i++) specBands[i] += (specTargets[i] - specBands[i]) * specResponse;
    var ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0); // 绘制坐标保持 CSS 像素
    ctx.clearRect(0, 0, w, h);
    // 颜色跟随字体(一级文字色，封面取色后自动深浅)
    var color = getComputedStyle(document.documentElement).getPropertyValue('--text-primary').trim() || '#F5F5F7';
    var hr = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--spectrum-height-ratio')) || 0.8;
    var maxH = h * hr, N = 56;             // 更多条 → 更纤细
    var vals = new Array(N);
    for (var i = 0; i < N; i++) { var s = i*24/N, lo=Math.floor(s), hi=Math.min(23,lo+1), fr=s-lo; vals[i]=specBands[lo]*(1-fr)+specBands[hi]*fr; }
    ctx.fillStyle = color;
    var margin = Math.max(3, w * 0.05);    // 两端留白(左右各 5%，最小 3px)
    var usableW = w - margin * 2;
    var g = Math.max(0.6, usableW / N * 0.22); // 紧凑间隙(约条宽的 22%)
    switch (specStyle) {
        case 0: { var bw=Math.max(0.5,(usableW - g*(N+1))/N); for(var i=0;i<N;i++){var bh=Math.max(1.5,vals[i]*maxH*1.6);ctx.beginPath();ctx.roundRect(margin+g+i*(bw+g),(h-bh)/2,bw,bh,0.8);ctx.fill();} break; }
        case 1: { var bw=Math.max(0.5,(usableW - g*(N+1))/N); for(var i=0;i<N;i++){var bh=Math.max(1.5,vals[i]*maxH*1.6);ctx.beginPath();ctx.roundRect(margin+g+i*(bw+g),h-bh,bw,bh,0.8);ctx.fill();} break; }
        case 2: { ctx.strokeStyle=color;ctx.lineWidth=1;ctx.beginPath();for(var i=0;i<N;i++){var a=vals[i]*maxH*.7,x=margin+(i+.5)*usableW/N;i===0?ctx.moveTo(x,h/2-a):ctx.lineTo(x,h/2-a);}for(var i=N-1;i>=0;i--){var a=vals[i]*maxH*.7,x=margin+(i+.5)*usableW/N;ctx.lineTo(x,h/2+a);}ctx.closePath();ctx.globalAlpha=.25;ctx.fill();ctx.globalAlpha=1;ctx.stroke(); break; }
        case 3: { ctx.strokeStyle=color;ctx.lineWidth=0.8;ctx.beginPath();for(var i=0;i<N;i++){var a=vals[i]*maxH*.75,x=margin+(i+.5)*usableW/N;i===0?ctx.moveTo(x,h/2-a):ctx.lineTo(x,h/2-a);}ctx.stroke(); break; }
        case 4: { for(var i=0;i<N;i++){var r=Math.max(1,vals[i]*maxH*.55);ctx.beginPath();ctx.arc(margin+(i+.5)*usableW/N,(h-r*2)/2+r,r,0,Math.PI*2);ctx.fill();} break; }
        case 5: { var av=vals.reduce(function(a,b){return a+b;},0)/N,bh=Math.max(3,av*maxH*1.4),bW=Math.max(3,usableW*.6);ctx.beginPath();ctx.roundRect((w-bW)/2,(h-bh)/2,bW,bh,2);ctx.fill(); break; }
    }
}

// ==================== SIZE REPORT ====================
var _lw = 0, _lh = 0;
function reportSize() {
    var content = document.getElementById('contentArea'), cover = document.getElementById('coverArea'), root = document.getElementById('root');
    var cw = cover.classList.contains('hidden') ? 0 : cover.clientWidth + 8;
    var sw = Math.min(content.scrollWidth, 500) + cw + 24, sh = root.clientHeight + 8;
    if (Math.abs(sw - _lw) > 15 || Math.abs(sh - _lh) > 6) { _lw = sw; _lh = sh; api.postMessage(JSON.stringify({ type: 'sizeReport', width: Math.round(sw), height: Math.round(sh) })); }
}

// ==================== READY ====================
function onReady() { api.postMessage(JSON.stringify({ type: 'ready' })); }
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', onReady);
else onReady();
