// Web Audio backend for the browser build.
// Plays the game's XACT cues from audio.json (made by tools/AudioExport) and the per-wave .wav files next to it.
// Playback rules mirror the desktop MonoGame fork (Cue / PlayWaveEvent):
//   one of the cue's sounds is picked at random; each sound has one or more layers (clips) played together.
//   volume = cue volume * category volume * RPC volume * sound volume
//   pitch (octaves) = RPC pitch + cue pitch + sound pitch; playbackRate = 2^pitch
// Exports are called synchronously from .NET via [JSImport].

let ctx = null;
let manifest = null;
let baseUrl = '';
const categoryGains = [];
const categoryIndex = new Map();
const cues = new Map();            // id -> instance
let nextId = 1;

// Decoded waves: key "bank/track" -> Promise<AudioBuffer>. Music is large once decoded, so keep only a few.
const bufferCache = new Map();
const MUSIC_CACHE_LIMIT = 3;
const musicLru = [];

const State = { Stopped: 0, Playing: 1, Paused: 2 };

// ---------- setup ----------

export async function load(manifestUrl) {
    const res = await fetch(manifestUrl);
    if (!res.ok) throw new Error(`Couldn't load ${manifestUrl}: ${res.status}`);
    manifest = await res.json();
    baseUrl = manifestUrl.substring(0, manifestUrl.lastIndexOf('/') + 1);

    ctx = new AudioContext();
    manifest.categories.forEach((c, i) => {
        const g = ctx.createGain();
        g.connect(ctx.destination);
        categoryGains[i] = g;
        categoryIndex.set(c.name, i);
    });

    // Browsers start audio suspended until the user interacts with the page.
    const unlock = () => { if (ctx.state !== 'running') ctx.resume(); };
    for (const type of ['pointerdown', 'keydown', 'touchstart']) {
        window.addEventListener(type, unlock, { capture: true });
    }
    console.log(`[port] Audio: ${Object.keys(manifest.cues).length} cues loaded (context ${ctx.state}).`);
}

export function exists(name) {
    return !!manifest && Object.prototype.hasOwnProperty.call(manifest.cues, name);
}

export function getCategoryIndex(name) {
    return categoryIndex.has(name) ? categoryIndex.get(name) : -1;
}

export function setCategoryVolume(name, volume) {
    const i = categoryIndex.get(name);
    if (i !== undefined) categoryGains[i].gain.value = volume;
}

/** True if the cue's first sound has an RPC curve driving pitch (matches Cue.IsPitchBeingControlledByRPC). */
export function pitchControlledByRpc(name) {
    const def = manifest?.cues[name];
    const sound = def?.sounds[0];
    return !!sound && sound.rpc.some(i => manifest.rpc[i].param === 'Pitch');
}

/** Debugging aid: `(await import('/js/stardewAudio.js')).debugStats()` in the console. */
export async function debugStats() {
    const settled = await Promise.allSettled([...bufferCache.values()]);
    const active = [...cues.values()].filter(c => c.state === State.Playing);
    return {
        context: ctx?.state,
        decoded: settled.filter(s => s.status === 'fulfilled').length,
        failed: settled.filter(s => s.status === 'rejected').length,
        playing: active.map(c => `${c.name}${c.voices.length ? '' : ' (loading)'}`),
        categoryVolumes: Object.fromEntries(manifest.categories.map((c, i) => [c.name, +categoryGains[i].gain.value.toFixed(2)])),
    };
}

/**
 * Debugging aid: decodes one wave and reports signal statistics. Correctly decoded game audio has a
 * moderate RMS and almost no clipping; a broken ADPCM decoder produces loud, heavily clipped noise.
 */
export async function debugDecode(bank, track) {
    const wave = waveInfo(bank, track);
    const buffer = await getBuffer(bank, track, 0);
    let sumSq = 0, peak = 0, clipped = 0, n = 0;
    for (let c = 0; c < buffer.numberOfChannels; c++) {
        const d = buffer.getChannelData(c);
        for (let i = 0; i < d.length; i++) {
            const v = Math.abs(d[i]);
            sumSq += v * v;
            if (v > peak) peak = v;
            if (v > 0.999) clipped++;
            n++;
        }
    }
    return {
        file: wave.File,
        codec: wave.Codec,
        expectedFrames: wave.SampleCount,
        decodedFrames: buffer.length,
        seconds: +buffer.duration.toFixed(2),
        rms: +Math.sqrt(sumSq / n).toFixed(4),
        peak: +peak.toFixed(4),
        clippedPct: +(100 * clipped / n).toFixed(3),
    };
}

// ---------- cue instances ----------

export function create(name) {
    if (!exists(name)) return -1;
    const id = nextId++;
    cues.set(id, {
        id, name,
        def: manifest.cues[name],
        state: State.Stopped,
        volume: 1, pitch: 0,
        vars: new Map(),
        sound: null, voices: [], output: null,
        startedAt: 0, offset: 0, token: 0,
        oneShot: false,
    });
    return id;
}

export function release(id) {
    const inst = cues.get(id);
    if (!inst) return;
    stopVoices(inst, true);
    cues.delete(id);
}

/** Fire-and-forget (SoundBank.PlayCue): the instance cleans itself up when it finishes. */
export function playOnce(name) {
    const id = create(name);
    if (id < 0) return;
    cues.get(id).oneShot = true;
    play(id);
}

export function play(id) {
    const inst = cues.get(id);
    if (!inst || inst.state === State.Playing) return;
    if (!enforceInstanceLimit(inst)) return;

    // XACT picks the sound and randomises track volume/pitch each time a cue plays.
    const sounds = inst.def.sounds;
    inst.sound = sounds[Math.floor(Math.random() * sounds.length)];
    const first = inst.sound.layers[0];
    if (inst.sound.layers.some(l => l.clipVol !== undefined)) {
        inst.volume = first.volRange ? first.volRange[0] + Math.random() * first.volRange[1] : (first.clipVol ?? 1);
        inst.pitch = first.pitchRange ? first.pitchRange[0] + Math.random() * first.pitchRange[1] : 0;
    }
    inst.offset = 0;
    startVoices(inst);
}

export function stop(id, immediate) {
    const inst = cues.get(id);
    if (!inst) return;
    stopVoices(inst, immediate);
    inst.state = State.Stopped;
    if (inst.oneShot) cues.delete(id);
}

export function pause(id) {
    const inst = cues.get(id);
    if (!inst || inst.state !== State.Playing) return;
    inst.offset += Math.max(0, ctx.currentTime - inst.startedAt);
    stopVoices(inst, true);
    inst.state = State.Paused;
}

export function resume(id) {
    const inst = cues.get(id);
    if (!inst || inst.state !== State.Paused) return;
    startVoices(inst);
}

/** 0 = stopped, 1 = playing (including while its wave is still loading), 2 = paused. */
export function state(id) {
    return cues.get(id)?.state ?? State.Stopped;
}

export function setVolume(id, v) { const i = cues.get(id); if (i) { i.volume = v; applyParams(i); } }
export function setPitch(id, p) { const i = cues.get(id); if (i) { i.pitch = p; applyParams(i); } }

export function setVariable(id, name, value) {
    const inst = cues.get(id);
    if (!inst) return;
    const index = manifest.variables.findIndex(v => v.name === name);
    if (index < 0) return;
    const v = manifest.variables[index];
    inst.vars.set(index, Math.min(v.max, Math.max(v.min, value)));
    applyParams(inst);
}

export function getVariable(id, name) {
    const inst = cues.get(id);
    const index = manifest?.variables.findIndex(v => v.name === name) ?? -1;
    if (!inst || index < 0) return 0;
    return inst.vars.has(index) ? inst.vars.get(index) : manifest.variables[index].init;
}

// ---------- playback internals ----------

function enforceInstanceLimit(inst) {
    const limit = inst.def.limit;
    if (!limit || limit >= 255) return true;
    const playing = [...cues.values()].filter(c => c.name === inst.name && c.state === State.Playing);
    if (playing.length < limit) return true;
    if (inst.def.behavior === 'ReplaceOldest') {
        stop(playing[0].id, true);
        return true;
    }
    return false;
}

function startVoices(inst) {
    inst.state = State.Playing;
    const token = ++inst.token;          // invalidates voices from an earlier play/stop
    const output = ctx.createGain();
    output.connect(categoryGains[inst.sound.cat] ?? ctx.destination);
    inst.output = output;
    inst.voices = [];
    applyParams(inst);

    let pending = inst.sound.layers.length;
    for (const layer of inst.sound.layers) {
        getBuffer(layer.bank, layer.track, inst.sound.cat).then(buffer => {
            if (inst.token !== token || inst.state !== State.Playing) return;
            const src = ctx.createBufferSource();
            src.buffer = buffer;
            const wave = waveInfo(layer.bank, layer.track);
            if (layer.loop === 255) {
                src.loop = true;
                if (wave.LoopLength > 0) {
                    src.loopStart = wave.LoopStart / wave.SampleRate;
                    src.loopEnd = (wave.LoopStart + wave.LoopLength) / wave.SampleRate;
                }
            }
            src.connect(output);
            src.playbackRate.value = playbackRate(inst);
            const offset = src.loop ? inst.offset % buffer.duration : Math.min(inst.offset, buffer.duration);
            let repeats = layer.loop > 0 && layer.loop < 255 ? layer.loop : 0;
            src.onended = () => {
                if (inst.token !== token || inst.state !== State.Playing) return;
                if (repeats-- > 0) { restartVoice(inst, token, layer, buffer, output); return; }
                if (--pending === 0) finished(inst);
            };
            src.start(0, offset);
            inst.voices.push(src);
            if (inst.voices.length === 1) inst.startedAt = ctx.currentTime;
        }).catch(err => {
            console.warn(`[port] Audio: couldn't play '${inst.name}':`, err);
            if (inst.token === token && --pending === 0) finished(inst);
        });
    }
}

function restartVoice(inst, token, layer, buffer, output) {
    const src = ctx.createBufferSource();
    src.buffer = buffer;
    src.connect(output);
    src.playbackRate.value = playbackRate(inst);
    src.onended = () => { if (inst.token === token && inst.state === State.Playing) finished(inst); };
    src.start();
    inst.voices.push(src);
}

function finished(inst) {
    inst.state = State.Stopped;
    inst.output?.disconnect();
    inst.output = null;
    inst.voices = [];
    if (inst.oneShot) cues.delete(inst.id);
}

function stopVoices(inst, immediate) {
    inst.token++;
    const output = inst.output;
    const voices = inst.voices;
    inst.voices = [];
    inst.output = null;
    if (!output) return;
    // A very short fade avoids clicks; "as authored" stops get a slightly longer one.
    const fade = immediate ? 0.02 : 0.15;
    const now = ctx.currentTime;
    output.gain.cancelScheduledValues(now);
    output.gain.setValueAtTime(output.gain.value, now);
    output.gain.linearRampToValueAtTime(0, now + fade);
    for (const v of voices) {
        try { v.stop(now + fade); } catch { /* already stopped */ }
    }
    setTimeout(() => output.disconnect(), (fade + 0.05) * 1000);
}

function rpcValues(inst) {
    let volume = 1, pitch = 0;
    for (const i of inst.sound?.rpc ?? []) {
        const curve = manifest.rpc[i];
        const x = inst.vars.has(curve.var) ? inst.vars.get(curve.var) : manifest.variables[curve.var].init;
        const y = evaluate(curve.points, x);
        if (curve.param === 'Volume') volume *= Math.pow(10, (y / 100) / 20);
        else if (curve.param === 'Pitch') pitch += y / 1200;
        // FilterFrequency / FilterQFactor: not applied yet.
    }
    return { volume, pitch };
}

function evaluate(points, x) {
    if (x <= points[0][0]) return points[0][1];
    const last = points[points.length - 1];
    if (x >= last[0]) return last[1];
    for (let i = 1; i < points.length; i++) {
        const [x1, y1] = points[i];
        if (x1 >= x) {
            const [x0, y0] = points[i - 1];
            return y0 + (y1 - y0) * ((x - x0) / (x1 - x0));
        }
    }
    return last[1];
}

function playbackRate(inst) {
    const rpc = rpcValues(inst);
    return Math.pow(2, rpc.pitch + inst.pitch + (inst.sound?.pitch ?? 0));
}

function applyParams(inst) {
    if (!inst.sound) return;
    const rpc = rpcValues(inst);
    if (inst.output) inst.output.gain.value = inst.volume * rpc.volume * inst.sound.vol;
    const rate = Math.pow(2, rpc.pitch + inst.pitch + inst.sound.pitch);
    for (const v of inst.voices) v.playbackRate.value = rate;
}

// ---------- wave loading & decoding ----------

function waveInfo(bank, track) {
    return manifest.waves[manifest.waveBanks[bank]][track];
}

function getBuffer(bank, track, category) {
    const key = `${bank}/${track}`;
    let p = bufferCache.get(key);
    if (!p) {
        const wave = waveInfo(bank, track);
        p = fetch(baseUrl + encodeURI(wave.File))
            .then(r => { if (!r.ok) throw new Error(`${wave.File}: HTTP ${r.status}`); return r.arrayBuffer(); })
            .then(data => decodeWav(data));
        bufferCache.set(key, p);
        p.catch(() => bufferCache.delete(key));
    }
    if (manifest.categories[category]?.music) touchMusic(key);
    return p;
}

function touchMusic(key) {
    const i = musicLru.indexOf(key);
    if (i >= 0) musicLru.splice(i, 1);
    musicLru.push(key);
    while (musicLru.length > MUSIC_CACHE_LIMIT) bufferCache.delete(musicLru.shift());
}

function decodeWav(arrayBuffer) {
    const view = new DataView(arrayBuffer);
    const tag = (o) => String.fromCharCode(view.getUint8(o), view.getUint8(o + 1), view.getUint8(o + 2), view.getUint8(o + 3));
    if (tag(0) !== 'RIFF' || tag(8) !== 'WAVE') throw new Error('Not a WAV file');
    let fmt = null, dataOffset = 0, dataLength = 0;
    for (let o = 12; o + 8 <= view.byteLength;) {
        const id = tag(o), size = view.getUint32(o + 4, true), body = o + 8;
        if (id === 'fmt ') {
            fmt = {
                format: view.getUint16(body, true),
                channels: view.getUint16(body + 2, true),
                rate: view.getUint32(body + 4, true),
                blockAlign: view.getUint16(body + 12, true),
                bits: view.getUint16(body + 14, true),
                samplesPerBlock: size >= 20 ? view.getUint16(body + 18, true) : 0,
                coefs: [],
            };
            if (fmt.format === 2) {
                const count = view.getUint16(body + 20, true);
                for (let i = 0; i < count; i++) {
                    fmt.coefs.push([view.getInt16(body + 22 + i * 4, true), view.getInt16(body + 24 + i * 4, true)]);
                }
            }
        } else if (id === 'data') {
            dataOffset = body;
            dataLength = Math.min(size, view.byteLength - body);
        }
        o = body + size + (size & 1);
    }
    if (!fmt || !dataOffset) throw new Error('WAV missing fmt or data chunk');
    const bytes = new Uint8Array(arrayBuffer, dataOffset, dataLength);
    if (fmt.format === 2) return decodeMsAdpcm(bytes, fmt);
    if (fmt.format === 1) return decodePcm(bytes, fmt);
    throw new Error(`Unsupported WAV format ${fmt.format}`);
}

function decodePcm(bytes, fmt) {
    const ch = fmt.channels;
    const bytesPerSample = fmt.bits / 8;
    const frames = Math.floor(bytes.length / (bytesPerSample * ch));
    const buffer = ctx.createBuffer(ch, frames, fmt.rate);
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const out = [...Array(ch)].map((_, c) => buffer.getChannelData(c));
    for (let i = 0, o = 0; i < frames; i++) {
        for (let c = 0; c < ch; c++, o += bytesPerSample) {
            out[c][i] = fmt.bits === 16 ? view.getInt16(o, true) / 32768 : (view.getUint8(o) - 128) / 128;
        }
    }
    return buffer;
}

const ADAPTATION = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

function decodeMsAdpcm(bytes, fmt) {
    const ch = fmt.channels;
    const blockAlign = fmt.blockAlign;
    const perBlock = fmt.samplesPerBlock;
    const fullBlocks = Math.floor(bytes.length / blockAlign);
    const tail = bytes.length - fullBlocks * blockAlign;
    const tailSamples = tail >= 7 * ch ? ((tail - 7 * ch) * 2) / ch + 2 : 0;
    const frames = fullBlocks * perBlock + tailSamples;
    const buffer = ctx.createBuffer(ch, Math.max(1, frames), fmt.rate);
    const out = [...Array(ch)].map((_, c) => buffer.getChannelData(c));
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);

    const c1 = new Int32Array(ch), c2 = new Int32Array(ch), delta = new Int32Array(ch);
    const s1 = new Int32Array(ch), s2 = new Int32Array(ch);
    let frame = 0;

    for (let block = 0; block * blockAlign < bytes.length && frame < frames; block++) {
        let o = block * blockAlign;
        const end = Math.min(o + blockAlign, bytes.length);
        if (end - o < 7 * ch) break;
        for (let c = 0; c < ch; c++) {
            const p = Math.min(view.getUint8(o++), fmt.coefs.length - 1);
            c1[c] = fmt.coefs[p][0];
            c2[c] = fmt.coefs[p][1];
        }
        for (let c = 0; c < ch; c++, o += 2) delta[c] = view.getInt16(o, true);
        for (let c = 0; c < ch; c++, o += 2) s1[c] = view.getInt16(o, true);
        for (let c = 0; c < ch; c++, o += 2) s2[c] = view.getInt16(o, true);

        // The header's two samples come out oldest first.
        for (let c = 0; c < ch; c++) out[c][frame] = s2[c] / 32768;
        frame++;
        for (let c = 0; c < ch; c++) out[c][frame] = s1[c] / 32768;
        frame++;

        // Remaining nibbles, high nibble first, interleaved by channel.
        let c = 0;
        for (; o < end && frame < frames; o++) {
            const byte = view.getUint8(o);
            for (const nibble of [byte >> 4, byte & 0x0f]) {
                const signed = nibble >= 8 ? nibble - 16 : nibble;
                let sample = ((s1[c] * c1[c] + s2[c] * c2[c]) >> 8) + signed * delta[c];
                sample = sample > 32767 ? 32767 : sample < -32768 ? -32768 : sample;
                delta[c] = Math.max(16, (ADAPTATION[nibble] * delta[c]) >> 8);
                s2[c] = s1[c];
                s1[c] = sample;
                out[c][frame] = sample / 32768;
                if (++c === ch) { c = 0; frame++; }
            }
        }
    }
    return buffer;
}
