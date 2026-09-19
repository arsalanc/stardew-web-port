// Browser audio backend: implements the game's audio interfaces on top of wwwroot/js/stardewAudio.js.
// Data comes from audio.json + per-wave .wav files produced by tools/AudioExport.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Audio;
using StardewValley.Audio;

namespace StardewValley.WebPlatform.Audio;

[SupportedOSPlatform("browser")]
internal static partial class AudioJs
{
	public const string Module = "stardewAudio";

	[JSImport("load", Module)] public static partial Task Load(string manifestUrl);
	[JSImport("exists", Module)] public static partial bool Exists(string name);
	[JSImport("getCategoryIndex", Module)] public static partial int GetCategoryIndex(string name);
	[JSImport("setCategoryVolume", Module)] public static partial void SetCategoryVolume(string name, double volume);
	[JSImport("pitchControlledByRpc", Module)] public static partial bool PitchControlledByRpc(string name);
	[JSImport("create", Module)] public static partial int Create(string name);
	[JSImport("release", Module)] public static partial void Release(int id);
	[JSImport("playOnce", Module)] public static partial void PlayOnce(string name);
	[JSImport("play", Module)] public static partial void Play(int id);
	[JSImport("stop", Module)] public static partial void Stop(int id, bool immediate);
	[JSImport("pause", Module)] public static partial void Pause(int id);
	[JSImport("resume", Module)] public static partial void Resume(int id);
	[JSImport("state", Module)] public static partial int State(int id);
	[JSImport("setVolume", Module)] public static partial void SetVolume(int id, double volume);
	[JSImport("setPitch", Module)] public static partial void SetPitch(int id, double pitch);
	[JSImport("setVariable", Module)] public static partial void SetVariable(int id, string name, double value);
	[JSImport("getVariable", Module)] public static partial double GetVariable(int id, string name);
}

/// <summary>Entry point used by the host page before the game boots.</summary>
[SupportedOSPlatform("browser")]
public static class WebAudio
{
	public static bool Available { get; private set; }

	/// <summary>Loads the JS module and the cue manifest. Failure leaves the game on silent dummy audio.</summary>
	public static async Task InitializeAsync(string moduleUrl, string manifestUrl)
	{
		try
		{
			await JSHost.ImportAsync(AudioJs.Module, moduleUrl);
			await AudioJs.Load(manifestUrl);
			Available = true;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[port] Audio unavailable, continuing silently: " + ex.Message);
		}
	}

	/// <summary>Cue ids whose .NET wrapper was finalized without Dispose; released on the next engine update.</summary>
	internal static readonly ConcurrentQueue<int> PendingReleases = new ConcurrentQueue<int>();
}

[SupportedOSPlatform("browser")]
internal sealed class WebAudioEngine : IAudioEngine
{
	private readonly Dictionary<string, IAudioCategory> categories = new Dictionary<string, IAudioCategory>();

	public bool IsDisposed { get; private set; }

	// Only desktop XACT code touches the raw engine; nothing does on web.
	public AudioEngine Engine => null;

	public void Update()
	{
		while (WebAudio.PendingReleases.TryDequeue(out int id))
		{
			AudioJs.Release(id);
		}
	}

	public IAudioCategory GetCategory(string name)
	{
		if (!categories.TryGetValue(name, out IAudioCategory category))
		{
			category = new WebAudioCategory(name);
			categories[name] = category;
		}
		return category;
	}

	public int GetCategoryIndex(string name) => AudioJs.GetCategoryIndex(name);

	public void Dispose()
	{
		IsDisposed = true;
	}
}

[SupportedOSPlatform("browser")]
internal sealed class WebAudioCategory : IAudioCategory
{
	private readonly string name;

	public WebAudioCategory(string name)
	{
		this.name = name;
	}

	public void SetVolume(float volume) => AudioJs.SetCategoryVolume(name, volume);
}

[SupportedOSPlatform("browser")]
internal sealed class WebSoundBank : ISoundBank
{
	// Matches SoundBankWrapper: unknown cue names log an error and play this instead.
	private const string DefaultCueName = "shiny4";

	public bool IsInUse => true;

	public bool IsDisposed { get; private set; }

	public ICue GetCue(string name)
	{
		if (!Exists(name))
		{
			Game1.log.Error($"[port] Unknown audio cue '{name}'; playing the fallback cue instead.");
			name = DefaultCueName;
		}
		return new WebCue(name);
	}

	public void PlayCue(string name)
	{
		if (!Exists(name))
		{
			Game1.log.Error($"[port] Unknown audio cue '{name}'; not playing anything.");
			return;
		}
		AudioJs.PlayOnce(name);
	}

	// No positional audio in the browser build (the game only uses it for a few split-screen cases).
	public void PlayCue(string name, AudioListener listener, AudioEmitter emitter) => PlayCue(name);

	public bool Exists(string name) => name != null && AudioJs.Exists(name);

	// Data/AudioChanges (mod-added cues) isn't supported on web yet.
	public void AddCue(CueDefinition definition)
	{
		Game1.log.Warn("[port] Ignoring runtime audio cue '" + definition?.name + "' (not supported in the web build yet).");
	}

	public CueDefinition GetCueDefinition(string name) => null;

	public void Dispose()
	{
		IsDisposed = true;
	}
}

[SupportedOSPlatform("browser")]
internal sealed class WebCue : ICue
{
	private const int StateStopped = 0, StatePlaying = 1, StatePaused = 2;

	private readonly int id;
	private float pitch;
	private float volume = 1f;
	private bool? pitchControlledByRpc;
	private bool disposed;

	public WebCue(string name)
	{
		Name = name;
		id = AudioJs.Create(name);
	}

	~WebCue()
	{
		if (!disposed && id > 0)
		{
			WebAudio.PendingReleases.Enqueue(id);
		}
	}

	public string Name { get; }

	private int CurrentState => id > 0 ? AudioJs.State(id) : StateStopped;

	public bool IsStopped => CurrentState == StateStopped;

	// Stops are immediate (with a tiny fade), so a cue is never observed mid-stop.
	public bool IsStopping => false;

	public bool IsPlaying => CurrentState == StatePlaying;

	public bool IsPaused => CurrentState == StatePaused;

	public float Pitch
	{
		get => pitch;
		set
		{
			pitch = value;
			if (id > 0)
			{
				AudioJs.SetPitch(id, value);
			}
		}
	}

	public float Volume
	{
		get => volume;
		set
		{
			volume = value;
			if (id > 0)
			{
				AudioJs.SetVolume(id, value);
			}
		}
	}

	public bool IsPitchBeingControlledByRPC => pitchControlledByRpc ??= AudioJs.PitchControlledByRpc(Name);

	public void Play()
	{
		if (id > 0)
		{
			AudioJs.Play(id);
		}
	}

	public void Pause()
	{
		if (id > 0)
		{
			AudioJs.Pause(id);
		}
	}

	public void Resume()
	{
		if (id > 0)
		{
			AudioJs.Resume(id);
		}
	}

	public void Stop(AudioStopOptions options)
	{
		if (id > 0)
		{
			AudioJs.Stop(id, options == AudioStopOptions.Immediate);
		}
	}

	public void SetVariable(string var, int val) => SetVariable(var, (float)val);

	public void SetVariable(string var, float val)
	{
		if (id > 0)
		{
			AudioJs.SetVariable(id, var, val);
		}
	}

	public float GetVariable(string var) => id > 0 ? (float)AudioJs.GetVariable(id, var) : 0f;

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		disposed = true;
		if (id > 0)
		{
			AudioJs.Release(id);
		}
		GC.SuppressFinalize(this);
	}
}
