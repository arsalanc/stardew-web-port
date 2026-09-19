using System.Text.Json.Nodes;

namespace AudioExport;

/// <summary>
/// Turns the raw reflection dump of the game's XACT data into the compact audio.json the browser uses.
///
/// Playback model (matches the desktop fork's Cue/PlayWaveEvent):
///   pick one of cue.sounds uniformly at random;
///   volume = cue.Volume (track volume, optionally randomised) * category volume * rpc volume * sound.vol
///   pitch (octaves) = rpc pitch + cue.Pitch (track pitch, optionally randomised) + sound.pitch
///   loop: 255 = forever, otherwise extra repeats.
/// </summary>
public static class Normalizer
{
	public static JsonObject Build(JsonNode rawBank, JsonNode rawEngine, JsonObject waves)
	{
		var categories = new JsonArray();
		foreach (JsonNode c in rawEngine["_categories"]!.AsArray())
		{
			categories.Add(new JsonObject
			{
				["name"] = (string)c!["_name"],
				["music"] = (bool)c["isBackgroundMusic"]!,
			});
		}

		var variables = new JsonArray();
		foreach (JsonNode v in rawEngine["_cueVariables"]!.AsArray())
		{
			variables.Add(new JsonObject
			{
				["name"] = (string)v!["Name"],
				["init"] = (float)v["InitValue"]!,
				["min"] = (float)v["MinValue"]!,
				["max"] = (float)v["MaxValue"]!,
			});
		}

		var rpc = new JsonArray();
		foreach (JsonNode r in rawEngine["RpcCurves"]!.AsArray())
		{
			if ((bool)r!["IsGlobal"]!)
			{
				throw new NotSupportedException("Global RPC variables aren't used by Stardew; add support if that changes.");
			}
			var points = new JsonArray();
			foreach (JsonNode p in r["Points"]!.AsArray())
			{
				points.Add(new JsonArray((float)p!["Position"]!, (float)p["Value"]!));
			}
			rpc.Add(new JsonObject
			{
				["var"] = (int)r["Variable"]!,
				["param"] = (string)r["Parameter"],
				["points"] = points,
			});
		}

		var cues = new JsonObject();
		foreach (var (name, def) in rawBank["cues"]!.AsObject())
		{
			var sounds = new JsonArray();
			foreach (JsonNode s in def!["sounds"]!.AsArray())
			{
				sounds.Add(Sound(name, s!));
			}
			cues[name] = new JsonObject
			{
				["limit"] = (int)def["instanceLimit"]!,
				["behavior"] = (string)def["limitBehavior"],
				["sounds"] = sounds,
			};
		}

		return new JsonObject
		{
			["waveBanks"] = rawBank["waveBankNames"]!.DeepClone(),
			["categories"] = categories,
			["variables"] = variables,
			["rpc"] = rpc,
			["cues"] = cues,
			["waves"] = waves.DeepClone(),
		};
	}

	private static JsonObject Sound(string cue, JsonNode s)
	{
		var o = new JsonObject
		{
			["cat"] = (int)s["categoryID"]!,
			["vol"] = (float)s["volume"]!,
			["pitch"] = (float)s["pitch"]!,
			["rpc"] = s["rpcCurves"]!.DeepClone(),
		};
		if ((bool)s["useReverb"]!)
		{
			o["reverb"] = true;
		}

		var layers = new JsonArray();
		o["layers"] = layers;

		if (!(bool)s["complexSound"]!)
		{
			layers.Add(new JsonObject
			{
				["bank"] = (int)s["waveBankIndex"]!,
				["track"] = (int)s["trackIndex"]!,
				["loop"] = 0,
			});
			return o;
		}

		// Each clip plays in parallel ("clank" layers several). Stardew only ever puts one play-wave
		// event with one variant in a clip; fail loudly if the data ever needs more.
		foreach (JsonNode clip in s["soundClips"]!.AsArray())
		{
			JsonArray events = clip!["clipEvents"]!.AsArray();
			JsonNode ev = events.Count == 1 ? events[0] : null;
			JsonArray variants = ev?["_variants"]?.AsArray();
			if (ev == null || (string)ev["$type"] != "PlayWaveEvent" || variants is not { Count: 1 })
			{
				throw new NotSupportedException($"Cue '{cue}' uses an XACT structure this exporter doesn't handle yet.");
			}
			var layer = new JsonObject
			{
				["bank"] = (int)variants[0]!["waveBank"]!,
				["track"] = (int)variants[0]!["track"]!,
				["loop"] = (int)(uint)ev["_loopCount"]!,
				["clipVol"] = (float)clip["DefaultVolume"]!,
			};
			float start = (float)ev["<TimeStamp>k__BackingField"]!;
			if (start > 0)
			{
				layer["start"] = start;
			}
			if (ev["randomVolumeRange"] is JsonObject rv)
			{
				layer["volRange"] = new JsonArray((float)rv["X"]!, (float)rv["Y"]!);
			}
			if (ev["randomPitchRange"] is JsonObject rp)
			{
				layer["pitchRange"] = new JsonArray((float)rp["X"]!, (float)rp["Y"]!);
			}
			layers.Add(layer);
		}
		return o;
	}
}
