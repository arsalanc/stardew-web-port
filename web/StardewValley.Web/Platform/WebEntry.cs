using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace StardewValley.WebPlatform;

/// <summary>
/// Browser entry point. The host page calls <see cref="Tick"/> once per animation frame;
/// the first call boots the game the same way the desktop Program.Main does.
/// </summary>
public static class WebEntry
{
	private static GameRunner runner;

	/// <summary>Set if booting or a frame throws; the host shows it instead of ticking again.</summary>
	public static Exception Fault { get; private set; }

	/// <summary>
	/// Whether the OS cursor should show. The game hides it and draws its own unless the
	/// "hardware cursor" option is on. KNI's browser backend doesn't apply this, so the host does.
	/// </summary>
	public static bool IsMouseVisible => runner?.IsMouseVisible ?? true;

	// Frame timing for the ?perf overlay: ring buffers of the last ~4 seconds.
	private const int PerfSamples = 240;
	private static readonly double[] tickMs = new double[PerfSamples];
	private static readonly double[] frameMs = new double[PerfSamples];
	// XNA's fixed timestep runs extra catch-up updates when frames arrive late, so a slow or
	// throttled frame can contain many updates; this keeps the timing numbers interpretable.
	private static readonly int[] updates = new int[PerfSamples];
	private static int updatesThisFrame;

	/// <summary>Called from GameRunner.Update (web build) so the overlay can report updates per frame.</summary>
	public static void CountUpdate() => updatesThisFrame++;
	private static int perfIndex;
	private static int perfCount;
	private static long lastTickStart;

	public static void Tick()
	{
		if (Fault != null)
		{
			return;
		}
		try
		{
			if (runner == null)
			{
				Boot();
			}
			long start = Stopwatch.GetTimestamp();
			updatesThisFrame = 0;
			runner.Tick();
			WebStorage.SyncIfDue();
			Wiki.WikiContext.Update();
			RecordFrame(start, Stopwatch.GetTimestamp(), updatesThisFrame);
		}
		catch (Exception ex)
		{
			Fault = ex;
			Console.Error.WriteLine("[port] Fatal: " + ex);
		}
	}

	private static void RecordFrame(long start, long end, int updateCount)
	{
		updates[perfIndex] = Math.Max(0, updateCount);
		tickMs[perfIndex] = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
		frameMs[perfIndex] = lastTickStart == 0 ? 0 : Stopwatch.GetElapsedTime(lastTickStart, start).TotalMilliseconds;
		lastTickStart = start;
		perfIndex = (perfIndex + 1) % PerfSamples;
		perfCount = Math.Min(perfCount + 1, PerfSamples);
	}

	/// <summary>
	/// One-line frame timing summary: FPS from the interval between browser frames, and how long the
	/// game's own update+draw took (the part we can speed up; the rest is browser/GPU time).
	/// </summary>
	public static string PerfSummary()
	{
		if (perfCount < 2)
		{
			return "measuring…";
		}
		double[] work = new double[perfCount];
		Array.Copy(tickMs, work, perfCount);
		Array.Sort(work);
		double avg = 0;
		foreach (double t in work)
		{
			avg += t;
		}
		avg /= perfCount;
		double frames = 0;
		int intervals = 0;
		for (int i = 0; i < perfCount; i++)
		{
			if (frameMs[i] > 0)
			{
				frames += frameMs[i];
				intervals++;
			}
		}
		double fps = intervals > 0 ? 1000.0 * intervals / frames : 0;
		double updatesPerFrame = 0;
		for (int i = 0; i < perfCount; i++)
		{
			updatesPerFrame += updates[i];
		}
		updatesPerFrame /= perfCount;
		string where = Game1.currentLocation?.NameOrUniqueName ?? (Game1.activeClickableMenu?.GetType().Name ?? "-");
		return $"{fps:0} fps · game {avg:0.0} ms avg, p95 {work[(int)(perfCount * 0.95)]:0.0}, max {work[perfCount - 1]:0.0} · {updatesPerFrame:0.#} upd/frame · {where}";
	}

	private static void Boot()
	{
		Console.WriteLine("[port] Booting Stardew Valley (web).");
		Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
		Program.GameTesterMode = true;
		AppDomain.CurrentDomain.UnhandledException += Program.handleException;
		runner = new GameRunner();
		GameRunner.instance = runner;
		runner.Run();
	}
}
