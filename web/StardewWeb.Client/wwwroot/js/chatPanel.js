// "Ask" tab of the side panel: an in-game assistant backed by a local model (Ollama via the dev
// server's /api/llm endpoints). The model answers from tools that read the live game
// (Platform/Chat/ChatTools.cs) plus a wiki search, so facts come from this game and this save.
// Everything is rendered as text (a tiny markdown subset built with DOM nodes, never innerHTML).

import { searchWiki } from './wikiPanel.js';

const DEFAULT_MODEL = 'qwen3:8b';
const MAX_TOOL_ROUNDS = 6;
const MAX_TURNS_KEPT = 6;        // older turns are dropped so the history fits the model's context
const STORAGE_MODEL = 'stardew-web.chatModel';
const STORAGE_THINK = 'stardew-web.chatThink';

const SYSTEM_PROMPT = [
    'You are the in-game assistant for Stardew Valley, running inside the player\'s own game on PC.',
    'Answer questions about the game and give practical guidance.',
    '',
    'Rules:',
    '- You do not know Stardew Valley from memory. Everything factual must come from a tool.',
    '- Player state (date, weather, money, energy, inventory, friendships, recipes they know, bundles): call the tools.',
    '- Item, recipe, villager and bundle facts: call the tools; they read this exact game version.',
    '- ANY "how do I ...", "where/when do I ...", or mechanics question: call search_wiki BEFORE answering,',
    '  even if you think you know. If the first search finds nothing useful, try different keywords.',
    '- Never invent menus, buttons or controls. The game is played with tools, the mouse and keyboard;',
    '  there are no on-screen "Place" or "Remove" buttons. Describe an action only if a tool result mentions it.',
    '- If the tools don\'t answer the question, say plainly that you couldn\'t find it and point to the wiki page',
    '  the search returned. A short honest answer beats a confident wrong one.',
    '- Keep answers short: a few sentences or a short list. Use item and villager names as the game shows them.',
].join('\n');

const WIKI_TOOL = {
    type: 'function',
    function: {
        name: 'search_wiki',
        description: 'Search the Stardew Valley Wiki and return excerpts from the best matching page. Use this for how-to and mechanics questions, strategy, locations, events, and anything the game-data tools don\'t cover. Prefer short keyword queries (the wiki matches whole words), e.g. "chest", "crab pot", "greenhouse".',
        parameters: { type: 'object', properties: { query: { type: 'string', description: 'Keywords to look up, e.g. "chest" or "mine elevator" (not a full sentence)' } }, required: ['query'] },
    },
};

const TOOL_LABELS = {
    get_player_state: () => 'Checked your current day, stats and inventory',
    get_recipe: a => `Looked up the recipe for ${a.name}`,
    lookup_item: a => `Looked up ${a.name}`,
    lookup_npc: a => `Looked up ${a.name}`,
    get_bundles_remaining: () => 'Checked the Community Center bundles',
    search_wiki: a => `Searched the wiki for "${a.query}"`,
};

let models = [];
let history = [];          // neutral-format messages, without the system prompt
let abort = null;
let els = {};

// ---------- setup ----------

export function mountChat(root) {
    const bar = div('chatBar');
    els.model = document.createElement('select');
    els.model.title = 'Model';
    els.model.onchange = () => { store(STORAGE_MODEL, els.model.value); updateThinkToggle(); };
    const thinkLabel = document.createElement('label');
    thinkLabel.title = 'Let the model reason step by step first. Slower, better for planning questions.';
    els.think = document.createElement('input');
    els.think.type = 'checkbox';
    els.think.checked = load(STORAGE_THINK) === '1';
    els.think.onchange = () => store(STORAGE_THINK, els.think.checked ? '1' : '0');
    thinkLabel.append(els.think, ' Think harder');
    const reset = button('New chat', () => { stop(); history = []; els.log.replaceChildren(intro()); });
    bar.append(els.model, thinkLabel, reset);

    els.log = div('chatLog');
    els.log.appendChild(intro());

    const form = document.createElement('form');
    form.className = 'chatForm';
    els.input = document.createElement('textarea');
    els.input.rows = 2;
    els.input.placeholder = 'Ask anything about your game…';
    els.input.onkeydown = e => {
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); }
    };
    els.send = document.createElement('button');
    els.send.type = 'submit';
    els.send.textContent = 'Send';
    form.onsubmit = e => {
        e.preventDefault();
        if (abort) { stop(); return; }
        const text = els.input.value.trim();
        if (text) { els.input.value = ''; send(text); }
    };
    form.append(els.input, els.send);

    root.append(bar, els.log, form);
    loadModels();
}

export function focusInput() {
    els.input?.focus();
}

async function loadModels() {
    try {
        const res = await fetch('/api/llm/models');
        const data = await res.json();
        models = data.models ?? [];
        if (data.errors?.length) console.warn('[port] Chat: model listing problems:', data.errors);
    } catch (err) {
        models = [];
        console.warn('[port] Chat: could not list models:', err);
    }
    els.model.replaceChildren(...models.map(m => {
        const o = document.createElement('option');
        o.value = m.id;
        o.textContent = `${m.id}${m.tools ? '' : ' (no tools)'}`;
        return o;
    }));
    if (models.length === 0) {
        const o = document.createElement('option');
        o.textContent = 'No models - is Ollama running?';
        els.model.appendChild(o);
        els.model.disabled = true;
        return;
    }
    const saved = load(STORAGE_MODEL);
    els.model.value = [saved, DEFAULT_MODEL].find(id => models.some(m => m.id === id)) ?? models.find(m => m.tools)?.id ?? models[0].id;
    updateThinkToggle();
}

function updateThinkToggle() {
    const m = currentModel();
    els.think.disabled = !m?.thinking;
    els.think.parentElement.style.opacity = m?.thinking ? '' : '.5';
}

function currentModel() {
    return models.find(m => m.id === els.model.value);
}

// ---------- conversation ----------

// "How do I ...", "where do I ...", crafting/unlocking questions: things the model has no reliable
// memory of and tends to answer by inventing menus. We look these up before it can.
const MECHANICS = /\b(how|where|when|unlock|unlocked|recipe|craft|crafting|build|upgrade|repair|catch|grow|plant|water|mine|smelt|fish|move|remove|place|open|enter|reach|get)\b/i;

async function send(text) {
    const model = currentModel();
    if (!model) { addNote('No model available. Start Ollama and reopen this tab.', 'error'); return; }

    history.push({ role: 'user', content: text });
    addBubble('user').appendChild(document.createTextNode(text));
    trimHistory();

    abort = new AbortController();
    setBusy(true);
    try {
        if (MECHANICS.test(text)) {
            await groundInWiki(text);
        }
        const tools = model.tools ? [...gameTools(), WIKI_TOOL] : undefined;
        for (let round = 0; round < MAX_TOOL_ROUNDS; round++) {
            const reply = await streamReply(model, tools, abort.signal);
            history.push({ role: 'assistant', content: reply.content, ...(reply.toolCalls.length ? { tool_calls: reply.toolCalls } : {}) });
            if (reply.toolCalls.length === 0) break;
            for (const call of reply.toolCalls) {
                const name = call.function?.name;
                const args = parseArgs(call.function?.arguments);
                addNote((TOOL_LABELS[name] ?? (() => `Used ${name}`))(args), 'tool');
                const result = await runTool(name, args);
                history.push({ role: 'tool', tool_name: name, content: result });
            }
            if (round === MAX_TOOL_ROUNDS - 1) addNote('Stopped after several lookups without a final answer.', 'error');
        }
    } catch (err) {
        if (err.name !== 'AbortError') addNote(`Something went wrong: ${err.message}`, 'error');
    } finally {
        abort = null;
        setBusy(false);
    }
}

/**
 * Looks the question up on the wiki and gives the model the passages before it answers, so a
 * mechanics answer is grounded even when the model would rather improvise. Failures are ignored:
 * the model still has the tools.
 */
async function groundInWiki(question) {
    try {
        const found = await searchWiki(question);
        if (!found?.excerpts?.length) return;
        addNote(`Looked up "${found.page}" on the wiki`, 'tool');
        history.push({
            role: 'system',
            content: `Wiki page "${found.page}" (${found.url}), relevant passages:\n`
                + found.excerpts.map(e => `- ${e}`).join('\n')
                + '\nAnswer from these. If they don\'t cover it, say so and give the link.',
        });
    } catch (err) {
        console.warn('[port] Chat: wiki grounding failed:', err);
    }
}

/** Streams one model turn into a new bubble; returns its text and any tool calls. */
async function streamReply(model, tools, signal) {
    const res = await fetch('/api/llm/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            provider: model.provider,
            model: model.id,
            messages: [{ role: 'system', content: SYSTEM_PROMPT }, ...history],
            tools,
            think: model.thinking ? els.think.checked : undefined,
        }),
        signal,
    });
    if (!res.ok || !res.body) throw new Error(`the server answered ${res.status}`);

    const bubble = addBubble('assistant');
    const thinking = document.createElement('details');
    thinking.className = 'thinking';
    const thinkingSummary = document.createElement('summary');
    thinkingSummary.textContent = 'Thinking…';
    const thinkingText = document.createElement('div');
    thinking.append(thinkingSummary, thinkingText);
    const answer = document.createElement('div');
    bubble.append(thinking, answer);
    thinking.hidden = true;

    let content = '', thought = '', toolCalls = [], stats = null;
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        let nl;
        while ((nl = buffer.indexOf('\n')) >= 0) {
            const line = buffer.slice(0, nl).trim();
            buffer = buffer.slice(nl + 1);
            if (!line) continue;
            const chunk = JSON.parse(line);
            if (chunk.error) throw new Error(chunk.error);
            const m = chunk.message ?? {};
            if (m.thinking) {
                thought += m.thinking;
                thinking.hidden = false;
                thinkingText.textContent = thought;
            }
            if (m.content) {
                content += m.content;
                renderMarkdown(answer, content);
            }
            if (m.tool_calls?.length) toolCalls.push(...m.tool_calls);
            if (chunk.done) stats = chunk;
            scrollToEnd();
        }
    }
    if (thought) thinkingSummary.textContent = 'Reasoning';
    if (!content.trim()) {
        // A turn that only calls tools has no text; drop the empty bubble.
        if (!thought) bubble.remove();
        else answer.remove();
    } else if (stats?.eval_count && stats.eval_duration) {
        const meta = div('meta');
        meta.textContent = `${model.id} · ${(stats.eval_count / (stats.eval_duration / 1e9)).toFixed(0)} tokens/s`;
        bubble.appendChild(meta);
    }
    return { content, toolCalls };
}

function gameTools() {
    try {
        return JSON.parse(window.theInstance.invokeMethod('ChatToolDefinitions'));
    } catch (err) {
        console.warn('[port] Chat: game tools unavailable:', err);
        return [];
    }
}

async function runTool(name, args) {
    try {
        if (name === 'search_wiki') {
            const found = await searchWiki(String(args.query ?? ''));
            return JSON.stringify(found ?? { error: 'No wiki page matched.' });
        }
        return window.theInstance.invokeMethod('ChatTool', name, JSON.stringify(args));
    } catch (err) {
        return JSON.stringify({ error: `Tool failed: ${err.message}` });
    }
}

function parseArgs(raw) {
    if (raw && typeof raw === 'object') return raw;
    try { return JSON.parse(raw ?? '{}'); } catch { return {}; }
}

function trimHistory() {
    const userTurns = history.map((m, i) => (m.role === 'user' ? i : -1)).filter(i => i >= 0);
    if (userTurns.length > MAX_TURNS_KEPT) history = history.slice(userTurns[userTurns.length - MAX_TURNS_KEPT]);
}

function stop() {
    abort?.abort();
}

function setBusy(busy) {
    els.send.textContent = busy ? 'Stop' : 'Send';
    els.send.classList.toggle('stop', busy);
}

// ---------- rendering ----------

function intro() {
    const p = div('note');
    p.textContent = 'Ask about your game, e.g. "How do I make a chest?", "What does Shane like?" or "What should I do today?". Answers come from a local model that looks things up in your save and the wiki.';
    return p;
}

function addBubble(role) {
    const b = div(`bubble ${role}`);
    els.log.appendChild(b);
    scrollToEnd();
    return b;
}

function addNote(text, kind) {
    const n = div(`note ${kind ?? ''}`);
    n.textContent = text;
    els.log.appendChild(n);
    scrollToEnd();
}

function scrollToEnd() {
    els.log.scrollTop = els.log.scrollHeight;
}

/** Minimal, safe markdown: paragraphs, "- " / "1. " lists, **bold**, `code`. */
function renderMarkdown(target, text) {
    const nodes = [];
    let list = null;
    for (const raw of text.split('\n')) {
        const line = raw.trimEnd();
        const bullet = /^\s*(?:[-*•]|\d+[.)])\s+(.*)$/.exec(line);
        if (bullet) {
            if (!list) { list = document.createElement(/^\s*\d/.test(line) ? 'ol' : 'ul'); nodes.push(list); }
            const li = document.createElement('li');
            inline(li, bullet[1]);
            list.appendChild(li);
            continue;
        }
        list = null;
        if (!line.trim()) continue;
        const heading = /^#{1,6}\s+(.*)$/.exec(line);
        const p = document.createElement('p');
        if (heading) { const s = document.createElement('strong'); inline(s, heading[1]); p.appendChild(s); }
        else inline(p, line);
        nodes.push(p);
    }
    target.replaceChildren(...nodes);
}

function inline(parent, text) {
    for (const part of text.split(/(\*\*[^*]+\*\*|`[^`]+`)/)) {
        if (!part) continue;
        if (part.startsWith('**') && part.endsWith('**') && part.length > 4) {
            const b = document.createElement('strong'); b.textContent = part.slice(2, -2); parent.appendChild(b);
        } else if (part.startsWith('`') && part.endsWith('`') && part.length > 2) {
            const c = document.createElement('code'); c.textContent = part.slice(1, -1); parent.appendChild(c);
        } else {
            parent.appendChild(document.createTextNode(part));
        }
    }
}

// ---------- utils ----------

function div(className) {
    const d = document.createElement('div');
    d.className = className;
    return d;
}

function button(text, onClick) {
    const b = document.createElement('button');
    b.type = 'button';
    b.textContent = text;
    b.onclick = onClick;
    return b;
}

function load(key) { try { return localStorage.getItem(key); } catch { return null; } }
function store(key, value) { try { localStorage.setItem(key, value); } catch { /* storage blocked */ } }
