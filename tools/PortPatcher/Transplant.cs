using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PortPatcher;

/// <summary>
/// Recovers local functions ILSpy drops when they're captured by a closure: the call sites remain but
/// the function is gone. Decompiling the same type at C# 6 level (which has no local functions) emits
/// them as ordinary methods named &lt;Outer&gt;g__Name|N; we clean that up and put it back as a local
/// function next to its callers. Everything here is generated from the player's own copy of the game.
/// </summary>
public sealed class Transplant(string dll, string refsDir, string workDir)
{
	/// <summary>Local functions ILSpy (the version pinned in dotnet-tools.json) drops from Stardew Valley 1.6.</summary>
	public static readonly (string Type, string Outer, string Name)[] Known =
	{
		("StardewValley.Menus.CarpenterMenu", "receiveLeftClick", "ContinueDemolish"),
		("StardewValley.GameLocation", "MakeMapModifications", "ShowSkillMastery"),
		("StardewValley.SaveMigrations.SaveMigrator_1_6", "ApplySaveFix", "HandleItem"),
	};

	public string Run(string fileText, string type, string outer, string name, out string status)
	{
		CSharpParseOptions options = Program.ParseOptions;
		SyntaxNode root = CSharpSyntaxTree.ParseText(fileText, options).GetRoot();
		if (root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any(l => l.Identifier.Text == name)
			|| root.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == name))
		{
			status = "already present";
			return fileText;
		}
		var refs = root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == name).ToList();
		if (refs.Count == 0)
		{
			status = "not referenced";
			return fileText;
		}

		string cs6 = DecompileCSharp6(type);
		string function = ExtractLocalFunction(cs6, outer, name);

		// Innermost block (or switch section) that contains every call site; insert before the statement holding the first.
		SyntaxNode container = refs[0].Ancestors().First(a => (a is BlockSyntax || a is SwitchSectionSyntax) && refs.All(r => a.Span.Contains(r.Span)));
		var statements = container is BlockSyntax b ? b.Statements : ((SwitchSectionSyntax)container).Statements;
		StatementSyntax anchor = statements.First(s => s.Span.Contains(refs[0].Span));
		var text = SourceText.From(fileText);
		var edits = new List<Edit> { Edits.InsertBefore(text, anchor, function + "\n") };

		// The recovered code may use namespaces this file doesn't import yet.
		var have = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.Name?.ToString()).ToHashSet();
		var missing = Regex.Matches(cs6, @"^\s*using ([\w.]+);", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().Where(u => !have.Contains(u)).ToList();
		UsingDirectiveSyntax? lastUsing = root.DescendantNodes().OfType<UsingDirectiveSyntax>().LastOrDefault();
		if (missing.Count > 0 && lastUsing != null)
		{
			edits.Add(Edits.InsertAfter(text, lastUsing, string.Join("\n", missing.Select(u => $"using {u};"))));
		}

		string result = Edits.Apply(fileText, edits).Text;
		int errorsBefore = root.SyntaxTree.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
		int errorsAfter = CSharpSyntaxTree.ParseText(result, options).GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
		if (errorsAfter > errorsBefore)
		{
			throw new InvalidOperationException($"Recovering {name} in {type} produced invalid code.");
		}
		status = $"recovered ({refs.Count} call site{(refs.Count == 1 ? "" : "s")}{(missing.Count > 0 ? $", +{missing.Count} using" : "")})";
		return result;
	}

	private string DecompileCSharp6(string type)
	{
		Directory.CreateDirectory(workDir);
		string cached = Path.Combine(workDir, type + ".cs6.cs");
		if (File.Exists(cached))
		{
			return File.ReadAllText(cached);
		}
		var psi = new ProcessStartInfo("dotnet", new[] { "tool", "run", "ilspycmd", dll, "-t", type, "-r", refsDir, "-lv", "CSharp6" })
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		using Process p = Process.Start(psi)!;
		string output = p.StandardOutput.ReadToEnd();
		string errors = p.StandardError.ReadToEnd();
		p.WaitForExit();
		if (p.ExitCode != 0 || output.Length == 0)
		{
			throw new InvalidOperationException($"ilspycmd couldn't decompile {type}: {errors}");
		}
		File.WriteAllText(cached, output);
		return output;
	}

	private static readonly Regex Signature = new(
		@"^\s*(?:(?:private|internal|public|protected|static|unsafe|\[[^\]]*\])\s*)*(?<ret>.+?)\s+<\w+>g__(?<name>\w+)\|[\d_]+\((?<params>.*)\)\s*$");

	private static string ExtractLocalFunction(string cs6, string outer, string name)
	{
		string[] lines = cs6.Replace("\r\n", "\n").Split('\n');
		string marker = $"<{outer}>g__{name}|";
		int start = Array.FindIndex(lines, l => l.Contains(marker) && Signature.IsMatch(l) && !l.TrimEnd().EndsWith(';'));
		if (start < 0)
		{
			throw new InvalidOperationException($"Couldn't find {marker} in the C# 6 decompile.");
		}
		int depth = 0, end = -1;
		bool opened = false;
		for (int i = start + 1; i < lines.Length; i++)
		{
			depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
			opened |= lines[i].Contains('{');
			if (opened && depth == 0)
			{
				end = i;
				break;
			}
		}
		if (end < 0)
		{
			throw new InvalidOperationException($"Couldn't find the end of {marker}.");
		}

		Match sig = Signature.Match(lines[start]);
		var parameters = SplitParameters(sig.Groups["params"].Value)
			.Where(p => !p.Contains("DisplayClass"))                         // the closure itself
			.Select(p => Regex.Replace(p, @"^\[In\]\s*\[IsReadOnly\]\s*ref\s+", "in "));
		string header = $"{sig.Groups["ret"].Value} {name}({string.Join(", ", parameters)})";

		string baseIndent = Regex.Match(lines[start], @"^\s*").Value;
		var body = lines[(start + 1)..(end + 1)].Select(l =>
		{
			string s = l.StartsWith(baseIndent) ? l[baseIndent.Length..] : l.TrimStart();
			s = s.Replace("<>4__this.", "");
			s = Regex.Replace(s, @"CS\$<>8__locals\d+\.", "");
			s = Regex.Replace(s, @"<\w+>g__(\w+)\|[\d_]+", "$1");
			s = Regex.Replace(s, @"\bP_\d+\.", "");
			return s;
		});
		string function = header + "\n" + string.Join("\n", body);
		if (function.Contains("<>") || function.Contains("DisplayClass"))
		{
			throw new InvalidOperationException($"Couldn't fully clean up compiler-generated names in {name}.");
		}
		return function;
	}

	/// <summary>Splits a parameter list on top-level commas (generic arguments can contain commas).</summary>
	private static IEnumerable<string> SplitParameters(string list)
	{
		int depth = 0, from = 0;
		for (int i = 0; i < list.Length; i++)
		{
			char c = list[i];
			if (c is '<' or '(' or '[')
			{
				depth++;
			}
			else if (c is '>' or ')' or ']')
			{
				depth--;
			}
			else if (c == ',' && depth == 0)
			{
				yield return list[from..i].Trim();
				from = i + 1;
			}
		}
		if (list[from..].Trim().Length > 0)
		{
			yield return list[from..].Trim();
		}
	}
}
