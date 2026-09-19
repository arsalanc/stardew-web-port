// Side panel for the browser build, with two tabs:
//  - Wiki: .NET (Platform/Wiki/WikiContext.cs) sends a "card" for whatever the player is focused on
//    (facts read from the game's own data); we render it and add a short Stardew Valley Wiki summary.
//  - Ask:  the in-game assistant (chatPanel.js).
// Everything is inserted as text (never as HTML), so nothing from the network can inject markup.

const WIKI = 'https://stardewvalleywiki.com';
const API = WIKI + '/mediawiki/api.php';
const PANEL_WIDTH = 380;
const STORAGE_OPEN = 'stardew-web.wikiPanelOpen';
const STORAGE_TAB = 'stardew-web.panelTab';

let panel, wikiBody, chatRoot, toggle, tabButtons = {};
let open = false;
let tab = 'wiki';
let chat = null;                  // chatPanel.js, loaded on first use of the Ask tab
const summaryCache = new Map();   // page title -> Promise<{paragraphs, url} | null>
let summaryTimer = null;

// ---------- setup ----------

export function init() {
    // KNI sizes the game canvas from window.innerWidth/innerHeight; report the game area instead,
    // so the game resizes around the panel (and follows normal window resizes).
    for (const prop of ['innerWidth', 'innerHeight']) {
        const desc = Object.getOwnPropertyDescriptor(window, prop) ?? Object.getOwnPropertyDescriptor(Window.prototype, prop);
        Object.defineProperty(window, prop, {
            configurable: true,
            get() {
                const holder = document.getElementById('canvasHolder');
                const size = holder ? (prop === 'innerWidth' ? holder.clientWidth : holder.clientHeight) : 0;
                return size || desc.get.call(window);
            },
        });
    }

    document.head.appendChild(Object.assign(document.createElement('style'), { textContent: STYLES }));

    panel = document.createElement('aside');
    panel.id = 'sidePanel';
    const header = document.createElement('header');
    for (const [id, label] of [['wiki', 'Wiki (F1)'], ['chat', 'Ask (F2)']]) {
        const b = document.createElement('button');
        b.className = 'tab';
        b.textContent = label;
        b.onclick = () => setTab(id);
        tabButtons[id] = b;
        header.appendChild(b);
    }
    const close = document.createElement('button');
    close.className = 'close';
    close.title = 'Close panel (F1)';
    close.textContent = '×';
    close.onclick = () => setOpen(false);
    header.appendChild(close);

    wikiBody = document.createElement('div');
    wikiBody.id = 'wikiBody';
    wikiBody.appendChild(el('p', 'muted', 'Hover over an item, hold one, or talk to a villager and details will show up here.'));
    chatRoot = document.createElement('div');
    chatRoot.id = 'chatRoot';
    panel.append(header, wikiBody, chatRoot);

    toggle = document.createElement('button');
    toggle.id = 'panelToggle';
    toggle.textContent = 'WIKI · ASK';
    toggle.onclick = () => setOpen(true);

    // KNI listens for input on window in the bubble phase; stop it here so clicks, scrolling and
    // typing in the panel never reach the game (and the page's wheel blocker doesn't eat scrolling).
    for (const target of [panel, toggle]) {
        for (const type of ['keydown', 'keyup', 'keypress', 'mousedown', 'mouseup', 'mousemove', 'wheel',
            'touchstart', 'touchmove', 'touchend', 'touchcancel', 'contextmenu']) {
            target.addEventListener(type, e => e.stopPropagation());
        }
    }

    document.body.append(panel, toggle);

    window.addEventListener('keydown', e => {
        if (e.key === 'F1') {
            e.preventDefault();
            e.stopPropagation();
            setOpen(!open);
        } else if (e.key === 'F2') {
            e.preventDefault();
            e.stopPropagation();
            if (open && tab === 'chat') setOpen(false);
            else { setOpen(true); setTab('chat'); }
        }
    }, { capture: true });

    tab = load(STORAGE_TAB) === 'chat' ? 'chat' : 'wiki';
    setOpen(load(STORAGE_OPEN) === '1', true);
    setTab(tab, true);
}

/** Whether the Wiki tab is visible (WikiContext only does work then). */
export function isOpen() {
    return open && tab === 'wiki';
}

function setOpen(value, initial = false) {
    open = value;
    panel.classList.toggle('open', open);
    toggle.style.display = open ? 'none' : '';
    document.documentElement.style.setProperty('--wiki-panel-width', open ? `${PANEL_WIDTH}px` : '0px');
    store(STORAGE_OPEN, open ? '1' : '0');
    if (open && tab === 'chat') ensureChat().then(c => c.focusInput());
    // Tell KNI the game area changed size. (Not requestAnimationFrame: it never fires in hidden tabs.)
    if (!initial) setTimeout(() => window.dispatchEvent(new Event('resize')), 0);
}

function setTab(id, initial = false) {
    tab = id;
    for (const [name, b] of Object.entries(tabButtons)) b.classList.toggle('active', name === id);
    wikiBody.hidden = id !== 'wiki';
    chatRoot.hidden = id !== 'chat';
    store(STORAGE_TAB, id);
    if (id === 'chat' && (open || !initial)) ensureChat().then(c => { if (!initial) c.focusInput(); });
}

async function ensureChat() {
    if (!chat) {
        chat = await import('./chatPanel.js');
        chat.mountChat(chatRoot);
    }
    return chat;
}

// ---------- wiki card ----------

export function show(cardJson) {
    const card = JSON.parse(cardJson);
    const nodes = [el('h2', null, card.title)];
    if (card.subtitle) nodes.push(el('div', 'sub', card.subtitle));
    if (card.description) nodes.push(el('p', 'desc', card.description));

    if (card.facts?.length) {
        const table = document.createElement('table');
        for (const [label, value] of card.facts) {
            const row = table.insertRow();
            row.insertCell().textContent = label;
            row.insertCell().textContent = value;
        }
        nodes.push(table);
    }
    for (const section of card.sections ?? []) {
        nodes.push(el('h3', null, section.title));
        const list = document.createElement('ul');
        for (const item of section.items) list.appendChild(el('li', null, item));
        nodes.push(list);
    }

    const wiki = document.createElement('div');
    wiki.className = 'wiki';
    wiki.appendChild(el('p', 'muted', 'Loading wiki summary…'));
    nodes.push(wiki);
    wikiBody.replaceChildren(...nodes);
    wikiBody.scrollTop = 0;

    // Wait until the player settles on something before hitting the wiki.
    clearTimeout(summaryTimer);
    summaryTimer = setTimeout(() => fillSummary(wiki, card.wiki), 350);
}

async function fillSummary(container, title) {
    const summary = title ? await wikiSummary(title) : null;
    if (!container.isConnected) return;   // the card changed meanwhile
    const url = summary?.url ?? pageUrl(title ?? '');
    const nodes = summary?.paragraphs?.length
        ? summary.paragraphs.map(p => el('p', null, p))
        : [el('p', 'muted', 'No wiki summary found.')];
    nodes.push(el('p', null, externalLink(url, 'Open the full wiki page ↗')));
    nodes.push(el('p', 'credit', 'Summary from the Stardew Valley Wiki (CC BY-NC-SA 3.0).'));
    container.replaceChildren(...nodes);
}

// ---------- wiki API ----------

/**
 * Wiki search for the assistant: finds the best matching page and returns its short intro summary
 * (a couple of paragraphs, never the whole page) with the page's URL.
 */
export async function searchWiki(query) {
    if (!query) return null;
    const params = new URLSearchParams({ action: 'query', list: 'search', srsearch: query, srlimit: '1', format: 'json', origin: '*' });
    const res = await fetch(`${API}?${params}`);
    if (!res.ok) throw new Error(`wiki search failed (HTTP ${res.status})`);
    const hit = (await res.json()).query?.search?.[0];
    if (!hit) return null;
    const summary = await wikiSummary(hit.title);
    return { page: hit.title, summary: summary?.paragraphs?.join('\n') ?? '', url: summary?.url ?? pageUrl(hit.title) };
}

function wikiSummary(title) {
    if (!summaryCache.has(title)) {
        summaryCache.set(title, fetchSummary(title).catch(err => {
            console.warn('[port] Wiki lookup failed:', err);
            summaryCache.delete(title);
            return null;
        }));
    }
    return summaryCache.get(title);
}

async function fetchSummary(title) {
    // The wiki has no plain-text extracts API, so take the intro section's HTML and keep
    // only the text of its first couple of real paragraphs (skipping infoboxes, notices, etc.).
    const params = new URLSearchParams({
        action: 'parse', page: title, prop: 'text', section: '0',
        redirects: '1', format: 'json', origin: '*', disablelimitreport: '1',
    });
    const res = await fetch(`${API}?${params}`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const json = await res.json();
    if (json.error) return null;   // no such page

    const doc = new DOMParser().parseFromString(json.parse.text['*'], 'text/html');
    const root = doc.querySelector('.mw-parser-output') ?? doc.body;
    const paragraphs = [];
    for (const p of root.querySelectorAll(':scope > p')) {
        const text = p.textContent.replace(/\[\d+\]/g, '').replace(/\s+/g, ' ').trim();
        if (text.length > 20) paragraphs.push(text.length > 600 ? text.slice(0, 597) + '…' : text);
        if (paragraphs.length === 2) break;
    }
    return { paragraphs, url: pageUrl(json.parse.title ?? title) };
}

function pageUrl(title) {
    return `${WIKI}/${encodeURIComponent(title.replace(/ /g, '_'))}`;
}

// ---------- utils ----------

function externalLink(url, text) {
    const a = el('a', null, text);
    a.href = url;
    a.target = '_blank';
    a.rel = 'noopener noreferrer';
    return a;
}

function el(tag, className, content) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (content instanceof Node) node.appendChild(content);
    else if (content != null) node.textContent = content;
    return node;
}

function load(key) { try { return localStorage.getItem(key); } catch { return null; } }
function store(key, value) { try { localStorage.setItem(key, value); } catch { /* storage blocked */ } }

const STYLES = `
    #sidePanel { position: fixed; top: 0; right: 0; bottom: 0; width: ${PANEL_WIDTH}px; z-index: 5;
        display: none; flex-direction: column; background: #2b1d10; color: #f4e4c1;
        font: 14px/1.45 'Segoe UI', system-ui, sans-serif; border-left: 3px solid #b8743b; }
    #sidePanel.open { display: flex; }
    #sidePanel header { display: flex; align-items: stretch; background: #4a2f16; border-bottom: 2px solid #b8743b; }
    #sidePanel header .tab { flex: 1; padding: 9px 6px; background: none; border: 0; color: #c9a878;
        font: 600 13px 'Segoe UI', sans-serif; cursor: pointer; border-bottom: 3px solid transparent; }
    #sidePanel header .tab.active { color: #ffd98a; border-bottom-color: #ffd98a; }
    #sidePanel header .close { background: none; border: 0; color: inherit; font-size: 18px; padding: 0 12px; cursor: pointer; }
    #wikiBody { flex: 1; overflow-y: auto; padding: 12px 14px 20px; }
    #wikiBody h2 { margin: 0; font-size: 20px; color: #ffd98a; }
    #wikiBody .sub { margin: 2px 0 10px; color: #c9a878; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; }
    #wikiBody .desc { margin: 0 0 12px; white-space: pre-line; color: #e8d3a8; font-style: italic; }
    #wikiBody table { width: 100%; border-collapse: collapse; margin-bottom: 12px; }
    #wikiBody td { padding: 3px 0; vertical-align: top; border-bottom: 1px solid #4a3520; }
    #wikiBody td:first-child { color: #c9a878; width: 42%; padding-right: 8px; }
    #wikiBody h3 { margin: 14px 0 4px; font-size: 13px; color: #ffd98a; text-transform: uppercase; letter-spacing: .04em; }
    #wikiBody ul { margin: 0; padding-left: 18px; }
    #wikiBody .wiki { margin-top: 16px; padding-top: 10px; border-top: 2px solid #4a3520; }
    #wikiBody .wiki p { margin: 0 0 8px; }
    #sidePanel .muted, #sidePanel .credit { color: #a88b62; font-size: 12px; }
    #sidePanel a { color: #8fd3ff; }
    #chatRoot { flex: 1; min-height: 0; display: flex; flex-direction: column; }
    #chatRoot[hidden], #wikiBody[hidden] { display: none; }
    #chatRoot .chatBar { display: flex; gap: 8px; align-items: center; padding: 8px 10px; border-bottom: 1px solid #4a3520; font-size: 12px; }
    #chatRoot .chatBar select { flex: 1; min-width: 0; background: #1d130a; color: #f4e4c1; border: 1px solid #6b4a2a; padding: 3px; }
    #chatRoot .chatBar label { white-space: nowrap; color: #c9a878; }
    #chatRoot button { background: #6b4a2a; color: #f4e4c1; border: 1px solid #b8743b; border-radius: 4px; padding: 4px 10px; cursor: pointer; font: 12px 'Segoe UI', sans-serif; }
    #chatRoot button.stop { background: #7a2d1f; }
    #chatRoot .chatLog { flex: 1; overflow-y: auto; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
    #chatRoot .bubble { padding: 8px 10px; border-radius: 8px; max-width: 92%; overflow-wrap: anywhere; }
    #chatRoot .bubble.user { align-self: flex-end; background: #5a3d1c; white-space: pre-wrap; }
    #chatRoot .bubble.assistant { align-self: flex-start; background: #3a2814; border: 1px solid #4a3520; }
    #chatRoot .bubble p { margin: 0 0 6px; }
    #chatRoot .bubble p:last-child { margin-bottom: 0; }
    #chatRoot .bubble ul, #chatRoot .bubble ol { margin: 0 0 6px; padding-left: 20px; }
    #chatRoot .bubble code { background: #1d130a; padding: 0 3px; border-radius: 3px; }
    #chatRoot .bubble .meta { margin-top: 6px; color: #8a7050; font-size: 11px; }
    #chatRoot .thinking { margin-bottom: 6px; color: #a88b62; font-size: 12px; }
    #chatRoot .thinking div { white-space: pre-wrap; max-height: 160px; overflow-y: auto; }
    #chatRoot .note { color: #a88b62; font-size: 12px; }
    #chatRoot .note.tool::before { content: '↳ '; }
    #chatRoot .note.error { color: #ff9c85; }
    #chatRoot .chatForm { display: flex; gap: 8px; padding: 8px 10px; border-top: 2px solid #4a3520; }
    #chatRoot textarea { flex: 1; resize: none; background: #1d130a; color: #f4e4c1; border: 1px solid #6b4a2a; border-radius: 4px; padding: 6px; font: 13px 'Segoe UI', sans-serif; }
    #panelToggle { position: fixed; top: 50%; right: 0; z-index: 6; transform: translateY(-50%);
        padding: 10px 4px; writing-mode: vertical-rl; background: #4a2f16; color: #f4e4c1;
        border: 2px solid #b8743b; border-right: 0; border-radius: 6px 0 0 6px; cursor: pointer;
        font: 600 12px 'Segoe UI', sans-serif; opacity: .75; }
    #panelToggle:hover { opacity: 1; }
`;
