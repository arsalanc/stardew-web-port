// Port helper (not part of the original game).
// Reads content files through TitleContainer, which is a plain file read on desktop
// and an HTTP fetch from the page's origin in the browser build.
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace StardewValley;

internal static class PortContent
{
	/// <summary>Parses the content manifest (ContentHashes.json) from text already fetched over HTTP.</summary>
	public static Dictionary<string, object> ParseContentHashes(string json) =>
		ContentManifest.CHJsonParser.ParseJson(json) as Dictionary<string, object>;

	/// <summary>Returns the file's text, or null if it doesn't exist.</summary>
	public static string TryReadText(string path)
	{
		try
		{
			using Stream stream = TitleContainer.OpenStream(path);
			using StreamReader reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}
	}
}
