using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioExport;
using Microsoft.Xna.Framework.Audio;

// Usage: AudioExport <game Content dir> <output dir>   (setup.ps1 runs this for you)
if (args.Length < 2)
{
	Console.Error.WriteLine("Usage: AudioExport <game Content dir> <output dir>");
	return 1;
}
string contentDir = args[0];
string outDir = args[1];
string xactDir = Path.Combine(contentDir, "XACT");
Directory.CreateDirectory(outDir);
var json = new JsonSerializerOptions { WriteIndented = true };

// 1. Wave banks -> one .wav per entry.
var banks = new JsonObject();
foreach (string xwb in Directory.GetFiles(xactDir, "*.xwb"))
{
	var (name, waves) = WaveBankSplitter.Split(xwb, outDir);
	banks[name] = JsonSerializer.SerializeToNode(waves, json);
	Console.WriteLine($"{name}: {waves.Count} waves ({waves.Count(w => w.Codec == "adpcm")} ADPCM, {waves.Count(w => w.Codec == "pcm")} PCM)");
}
File.WriteAllText(Path.Combine(outDir, "waves.json"), banks.ToJsonString(json));

// 2. Sound bank + engine settings, parsed by the game's own MonoGame XACT code, dumped raw.
// MonoGame's TitleContainer only opens files under the app folder (it normalises away "..").
// The settings and sound bank files are tiny, so stage copies next to the exe.
string staged = Path.Combine(AppContext.BaseDirectory, "xact");
Directory.CreateDirectory(staged);
foreach (string file in new[] { "FarmerSounds.xgs", "Sound Bank.xsb" })
{
	File.Copy(Path.Combine(xactDir, file), Path.Combine(staged, file), overwrite: true);
}
var engine = new AudioEngine(Path.Combine("xact", "FarmerSounds.xgs"));
var soundBank = new SoundBank(engine, Path.Combine("xact", "Sound Bank.xsb"));

const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var cues = (IDictionary)typeof(SoundBank).GetField("_cues", All)!.GetValue(soundBank)!;
var waveBankNames = (string[])typeof(SoundBank).GetField("_waveBankNames", All)!.GetValue(soundBank)!;

var rawCues = new JsonObject();
foreach (DictionaryEntry entry in cues)
{
	rawCues[(string)entry.Key] = RawDump.Dump(entry.Value!);
}
var rawBank = new JsonObject
{
	["waveBankNames"] = JsonSerializer.SerializeToNode(waveBankNames),
	["cues"] = rawCues,
};
File.WriteAllText(Path.Combine(outDir, "soundbank.raw.json"), rawBank.ToJsonString(json));
File.WriteAllText(Path.Combine(outDir, "engine.raw.json"), RawDump.Dump(engine).ToJsonString(json));
Console.WriteLine($"Sound bank: {cues.Count} cues; wave banks referenced: {string.Join(", ", waveBankNames)}");

// 3. Compact manifest for the browser.
// Round-trip through text so numbers lose their CLR types (uint/ushort/...) and convert freely.
JsonNode Reparse(JsonNode n) => JsonNode.Parse(n.ToJsonString())!;
JsonObject manifest = Normalizer.Build(Reparse(rawBank), Reparse(RawDump.Dump(engine)), banks);
File.WriteAllText(Path.Combine(outDir, "audio.json"), manifest.ToJsonString());
Console.WriteLine($"Wrote audio.json ({new FileInfo(Path.Combine(outDir, "audio.json")).Length / 1024} KB)");
return 0;

/// <summary>Reflection dump of XACT object graphs (fields only), skipping back-references and runtime state.</summary>
internal static class RawDump
{
	private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	private static readonly Type[] SkipTypes =
	{
		typeof(SoundBank), typeof(AudioEngine), typeof(WaveBank), typeof(SoundEffect), typeof(SoundEffectInstance),
		typeof(XactClip), typeof(Cue),
	};

	public static JsonNode Dump(object value, int depth = 0)
	{
		if (value == null)
		{
			return null;
		}
		Type t = value.GetType();
		if (t.IsPrimitive || value is string || value is decimal)
		{
			return JsonValue.Create(value);
		}
		if (t.IsEnum)
		{
			return JsonValue.Create(value.ToString());
		}
		if (depth > 12)
		{
			return JsonValue.Create("<depth>");
		}
		if (value is IDictionary dict)
		{
			var o = new JsonObject();
			foreach (DictionaryEntry e in dict)
			{
				o[e.Key.ToString()!] = Dump(e.Value!, depth + 1);
			}
			return o;
		}
		if (value is IEnumerable seq)
		{
			var a = new JsonArray();
			foreach (object item in seq)
			{
				a.Add(Dump(item, depth + 1));
			}
			return a;
		}
		var obj = new JsonObject { ["$type"] = t.Name };
		for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
		{
			foreach (FieldInfo f in cur.GetFields(All | BindingFlags.DeclaredOnly))
			{
				if (typeof(Delegate).IsAssignableFrom(f.FieldType) || SkipTypes.Any(s => s.IsAssignableFrom(f.FieldType)) || obj.ContainsKey(f.Name))
				{
					continue;
				}
				obj[f.Name] = Dump(f.GetValue(value)!, depth + 1);
			}
		}
		return obj;
	}
}
