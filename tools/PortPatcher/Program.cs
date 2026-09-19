using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace PortPatcher;

/// <summary>
/// Usage: PortPatcher --src local/src --dll "<game>/Stardew Valley.dll" --refs "<game>" --work local/cs6
/// Repairs decompiler output and applies the browser-port patches to local/src in place.
/// </summary>
public static class Program
{
	public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

	public static int Main(string[] args)
	{
		string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
			?? throw new ArgumentException($"Missing {name}. Usage: PortPatcher --src <dir> --dll <Stardew Valley.dll> --refs <game dir> --work <dir>");
		string src, dll, refs, work;
		try
		{
			(src, dll, refs, work) = (Path.GetFullPath(Arg("--src")), Arg("--dll"), Arg("--refs"), Path.GetFullPath(Arg("--work")));
		}
		catch (ArgumentException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 2;
		}

		var sw = Stopwatch.StartNew();
		var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
			.Select(p => Path.GetRelativePath(src, p).Replace('\\', '/'))
			.Where(rel => !rel.StartsWith("bin/") && !rel.StartsWith("obj/"))
			.ToDictionary(rel => rel, rel => File.ReadAllText(Path.Combine(src, rel)));
		var original = new Dictionary<string, string>(files);
		var trees = files.ToDictionary(f => f.Key, f => CSharpSyntaxTree.ParseText(f.Value, ParseOptions));
		Console.WriteLine($"Loaded {files.Count} files in {sw.Elapsed.TotalSeconds:N1}s.");

		var report = new List<(string Rule, int Count, bool Required, string Note)>();
		bool failed = false;

		void RunRule(Rule rule)
		{
			int count = 0;
			foreach (string rel in files.Keys.ToList())
			{
				SyntaxTree tree = trees[rel];
				var ctx = new FileContext(tree.GetRoot(), tree.GetText(), rel);
				List<Edit> edits = rule.Find(ctx).ToList();
				if (edits.Count == 0)
				{
					continue;
				}
				var (text, skipped) = Edits.Apply(files[rel], edits);
				if (skipped.Count > 0)
				{
					Console.Error.WriteLine($"  {rule.Name}: {skipped.Count} overlapping edit(s) in {rel} were skipped.");
					failed = true;
				}
				files[rel] = text;
				trees[rel] = CSharpSyntaxTree.ParseText(text, ParseOptions);
				count += edits.Count - skipped.Count;
			}
			report.Add((rule.Name, count, rule.Required, ""));
			failed |= rule.Required && count == 0;
		}

		foreach (Rule rule in Rules.DecompilerFixes())
		{
			RunRule(rule);
		}

		var transplant = new Transplant(dll, refs, work);
		foreach (var (type, outer, name) in Transplant.Known)
		{
			string rel = type[..type.LastIndexOf('.')] + "/" + type[(type.LastIndexOf('.') + 1)..] + ".cs";
			if (!files.ContainsKey(rel))
			{
				report.Add(($"Recover local function {name}", 0, true, $"{rel} not found"));
				failed = true;
				continue;
			}
			try
			{
				files[rel] = transplant.Run(files[rel], type, outer, name, out string status);
				trees[rel] = CSharpSyntaxTree.ParseText(files[rel], ParseOptions);
				report.Add(($"Recover local function {name}", status.StartsWith("recovered") ? 1 : 0, false, status));
			}
			catch (Exception ex)
			{
				report.Add(($"Recover local function {name}", 0, true, ex.Message));
				failed = true;
			}
		}

		foreach (Rule rule in Rules.WebGeneric().Concat(Rules.WebTargeted()))
		{
			RunRule(rule);
		}

		int changed = 0;
		foreach (var (rel, text) in files)
		{
			if (text != original[rel])
			{
				File.WriteAllText(Path.Combine(src, rel), text, new UTF8Encoding(false));
				changed++;
			}
		}

		Console.WriteLine();
		foreach (var (rule, count, required, note) in report)
		{
			string status = count > 0 ? "ok" : required ? "MISSING" : "-";
			Console.WriteLine($"  {status,-8} {rule}{(count > 1 ? $" (x{count})" : "")}{(note.Length > 0 ? $": {note}" : "")}");
		}
		Console.WriteLine($"\nPatched {changed} file(s) in {sw.Elapsed.TotalSeconds:N1}s.");
		if (failed)
		{
			Console.Error.WriteLine("Some required patches didn't apply. This usually means your game version differs from the one these patches were written for (Stardew Valley 1.6).");
			return 1;
		}
		return 0;
	}
}
