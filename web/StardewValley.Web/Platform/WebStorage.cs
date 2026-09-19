using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace StardewValley.WebPlatform;

/// <summary>
/// Persists the game's AppData folder (saves, options, startup preferences) across page loads.
/// .NET's file system in the browser is in-memory only, so on startup we restore the folder from
/// IndexedDB (wwwroot/js/stardewStorage.js), then mirror changes back about once a second.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class WebStorage
{
	private const string Module = "stardewStorage";

	private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(1);

	[JSImport("open", Module)] private static partial Task Open();
	[JSImport("loadAll", Module)] private static partial Task<string> LoadAll();
	[JSImport("put", Module)] private static partial void Put(string path, string base64);
	[JSImport("remove", Module)] private static partial void Remove(string path);

	private static string root;
	private static Dictionary<string, (long Length, DateTime Modified)> snapshot = new();
	private static DateTime lastSync;
	private static bool reportedError;

	public static bool Available { get; private set; }

	/// <summary>Restores stored files into the in-memory file system. Call before the game boots.</summary>
	public static async Task InitializeAsync(string moduleUrl)
	{
		EnsureAbsoluteAppData();
		try
		{
			await JSHost.ImportAsync(Module, moduleUrl);
			await Open();
			root = Program.GetAppDataFolder();

			string json = await LoadAll();
			string[][] files = JsonSerializer.Deserialize<string[][]>(json) ?? Array.Empty<string[]>();
			foreach (string[] file in files)
			{
				string path = FullPath(file[0]);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllBytes(path, Convert.FromBase64String(file[1]));
			}
			snapshot = Scan();
			Available = true;
			Console.WriteLine($"[port] Storage: restored {files.Length} file(s) into {root}.");
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[port] Storage unavailable; saves won't persist across reloads: " + ex);
		}
	}

	/// <summary>Uploads changed files and deletes removed ones, at most once per <see cref="SyncInterval"/>.</summary>
	public static void SyncIfDue()
	{
		if (!Available || DateTime.UtcNow - lastSync < SyncInterval)
		{
			return;
		}
		lastSync = DateTime.UtcNow;
		try
		{
			Dictionary<string, (long, DateTime)> current = Scan();
			foreach (var (rel, stamp) in current)
			{
				if (!snapshot.TryGetValue(rel, out var old) || old != stamp)
				{
					Put(rel, Convert.ToBase64String(File.ReadAllBytes(FullPath(rel))));
				}
			}
			foreach (string rel in snapshot.Keys)
			{
				if (!current.ContainsKey(rel))
				{
					Remove(rel);
				}
			}
			snapshot = current;
		}
		catch (Exception ex)
		{
			// A save may be mid-write; try again next interval, but only report the first failure.
			if (!reportedError)
			{
				reportedError = true;
				Console.Error.WriteLine("[port] Storage sync failed (will retry): " + ex.Message);
			}
		}
	}

	/// <summary>
	/// In the browser HOME is unset, so SpecialFolder.ApplicationData comes back empty and the game's
	/// AppData paths become relative. The game assumes they're absolute: e.g. LoadGameMenu does
	/// Path.Combine(savesFolder, enumeratedDir, ...), which only works when enumeratedDir is rooted,
	/// so with relative paths every save was silently skipped. Give it a real home directory.
	/// </summary>
	private static void EnsureAbsoluteAppData()
	{
		if (Path.IsPathRooted(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)))
		{
			return;
		}
		const string home = "/home/web_user";
		Environment.SetEnvironmentVariable("HOME", home);
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home + "/.config");
		Environment.SetEnvironmentVariable("XDG_DATA_HOME", home + "/.local/share");
		Directory.CreateDirectory(home + "/.config");
		Console.WriteLine("[port] AppData folder: " + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
	}

	private static Dictionary<string, (long, DateTime)> Scan()
	{
		var files = new Dictionary<string, (long, DateTime)>();
		if (!Directory.Exists(root))
		{
			return files;
		}
		foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
			// Skip debug logs and the save system's in-progress temp files.
			if (rel.StartsWith("ErrorLogs/", StringComparison.OrdinalIgnoreCase) || rel.Contains("_STARDEWVALLEYSAVETMP"))
			{
				continue;
			}
			var info = new FileInfo(path);
			files[rel] = (info.Length, info.LastWriteTimeUtc);
		}
		return files;
	}

	private static string FullPath(string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
}
