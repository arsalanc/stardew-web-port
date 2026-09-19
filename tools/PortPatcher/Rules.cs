using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static PortPatcher.Edits;

namespace PortPatcher;

public sealed record FileContext(SyntaxNode Root, SourceText Text, string RelPath);

/// <summary>
/// A patch rule. Required rules must match at least once across the game, otherwise setup fails
/// (usually meaning the game version changed and the rule needs updating).
/// </summary>
public sealed record Rule(string Name, bool Required, Func<FileContext, IEnumerable<Edit>> Find);

public static class Rules
{
	// Game files the web build doesn't compile (keep in sync with web/StardewValley.Web/StardewValley.Web.csproj).
	public static bool IsExcludedFromWebBuild(string rel) =>
		rel.StartsWith("StardewValley.SDKs.Steam", StringComparison.Ordinal)
		|| rel.StartsWith("StardewValley.SDKs.GogGalaxy", StringComparison.Ordinal)
		|| rel.StartsWith("StardewValley.SDKs.WeGameRail", StringComparison.Ordinal)
		|| rel.StartsWith("StardewValley.Network/Lidgren", StringComparison.Ordinal)
		|| rel.StartsWith("StardewValley.Network/NetBuffer", StringComparison.Ordinal)
		|| rel is "StardewValley/SoundBankWrapper.cs" or "StardewValley.Audio/AudioEngineWrapper.cs" or "StardewValley/CueWrapper.cs";

	// ======================================================================
	// Decompiler repairs: generic rules for code ILSpy emits that doesn't compile.
	// ======================================================================

	public static IEnumerable<Rule> DecompilerFixes() => new[]
	{
		// struct S(T p) { T _a = p; U _b = _a.X(); } -> _b's initializer can't read another field; read p instead.
		new Rule("Struct field initializers read the constructor parameter", Required: false, ctx =>
			ctx.Root.DescendantNodes().OfType<StructDeclarationSyntax>().Where(s => s.ParameterList != null).SelectMany(s =>
			{
				var parameters = s.ParameterList!.Parameters.Select(p => p.Identifier.Text).ToHashSet();
				var variables = s.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables).ToList();
				var fieldToParam = variables
					.Where(v => v.Initializer?.Value is IdentifierNameSyntax id && parameters.Contains(id.Identifier.Text))
					.ToDictionary(v => v.Identifier.Text, v => ((IdentifierNameSyntax)v.Initializer!.Value).Identifier.Text);
				return variables
					.Where(v => v.Initializer != null && v.Initializer.Value is not IdentifierNameSyntax)
					.SelectMany(v => v.Initializer!.Value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
					.Where(id => fieldToParam.ContainsKey(id.Identifier.Text)
						&& (id.Parent is not MemberAccessExpressionSyntax ma || ma.Expression == id))
					.Select(id => new Edit(id.Span, fieldToParam[id.Identifier.Text]));
			})),

		// ((<>c__DisplayClassN)this).member -> member (a closure's field, visible in the local function as-is).
		new Rule("Closure display-class casts", Required: false, ctx =>
			ctx.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
				.Where(ma => ma.Expression is ParenthesizedExpressionSyntax { Expression: CastExpressionSyntax { Expression: ThisExpressionSyntax } cast }
					&& cast.Type.ToString().Contains("DisplayClass"))
				.Select(ma => new Edit(ma.Span, ma.Name.ToString()))),

		// A goto from one switch case to a label inside another case (only reachable by that goto):
		// inline the labelled code at each goto and remove it from the other case.
		new Rule("Cross-case gotos", Required: false, CrossCaseGotos),
	};

	private static IEnumerable<Edit> CrossCaseGotos(FileContext ctx)
	{
		foreach (SwitchStatementSyntax sw in ctx.Root.DescendantNodes().OfType<SwitchStatementSyntax>())
		{
			foreach (SwitchSectionSyntax section in sw.Sections)
			{
				SyntaxList<StatementSyntax> stmts = section.Statements;
				for (int i = 1; i < stmts.Count; i++)
				{
					if (stmts[i] is not LabeledStatementSyntax label || !EndsWithJump(stmts[i - 1]))
					{
						continue;
					}
					int end = i;
					while (end < stmts.Count && stmts[end] is not BreakStatementSyntax)
					{
						end++;
					}
					string name = label.Identifier.Text;
					var gotos = sw.DescendantNodes().OfType<GotoStatementSyntax>()
						.Where(g => g.IsKind(SyntaxKind.GotoStatement) && g.Expression is IdentifierNameSyntax n && n.Identifier.Text == name)
						.ToList();
					if (end == stmts.Count || gotos.Count == 0 || gotos.Any(g => section.Span.Contains(g.Span)))
					{
						continue;
					}
					// Cross-case gotos are legal C#; only fix the ones that can't compile: the labelled code
					// uses a variable declared in the jumping case (e.g. a pattern variable), out of scope here.
					var from = sw.Sections.Where(s => gotos.Any(g => s.Span.Contains(g.Span))).ToList();
					var declaredThere = from.SelectMany(s => s.DescendantNodes())
						.Select(n => n switch
						{
							SingleVariableDesignationSyntax d => d.Identifier.Text,
							VariableDeclaratorSyntax v => v.Identifier.Text,
							_ => null,
						})
						.Where(n => n != null)
						.ToHashSet();
					var labelled = new[] { label.Statement }.Concat(stmts.Skip(i + 1).Take(end - i)).ToList();
					if (from.Count != 1 || !labelled.Any(s => s.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => declaredThere.Contains(id.Identifier.Text))))
					{
						continue;
					}
					var body = labelled.Select(s => s.ToString());
					string block = "{\n" + string.Join("\n", body) + "\n}";
					foreach (GotoStatementSyntax g in gotos)
					{
						yield return new Edit(g.Span, block);
					}
					var removed = TextSpan.FromBounds(stmts[i].SpanStart, stmts[end].Span.End);
					yield return new Edit(TextSpan.FromBounds(ctx.Text.Lines.GetLineFromPosition(removed.Start).Start,
						ctx.Text.Lines.GetLineFromPosition(removed.End).EndIncludingLineBreak), "");
				}
			}
		}
	}

	private static bool EndsWithJump(StatementSyntax s) => s switch
	{
		BreakStatementSyntax or ReturnStatementSyntax or GotoStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax => true,
		BlockSyntax b => b.Statements.Count > 0 && EndsWithJump(b.Statements.Last()),
		_ => false,
	};

	// ======================================================================
	// Browser port: generic rules.
	// ======================================================================

	private static readonly HashSet<string> DesktopOnlyNamespaces = new()
	{
		"StardewValley.SDKs.Steam", "StardewValley.SDKs.GogGalaxy", "Lidgren.Network", "Steamworks", "Galaxy.Api",
	};

	// Steam / GOG / raw-socket multiplayer types: excluded from the web build, so their uses are compiled out.
	private static readonly HashSet<string> DesktopOnlyTypes = new()
	{
		"SteamHelper", "SteamNetHelper", "LidgrenClient", "LidgrenServer", "NetConnection",
	};

	public static IEnumerable<Rule> WebGeneric() => new[]
	{
		new Rule("Desktop-only usings", Required: true, ctx => IsExcludedFromWebBuild(ctx.RelPath) ? Enumerable.Empty<Edit>() :
			ctx.Root.DescendantNodes().OfType<UsingDirectiveSyntax>()
				.Where(u => u.Name != null && DesktopOnlyNamespaces.Contains(u.Name.ToString()))
				.Select(u => WrapNotWeb(ctx.Text, u))),

		new Rule("Desktop-only Steam/Lidgren code", Required: true, DesktopOnlyCode),

		// Named argument "mipmap:" -> positional (KNI spells the parameter "mipMap").
		new Rule("Texture 'mipmap:' named arguments", Required: false, ctx =>
			ctx.Root.DescendantNodes().OfType<ArgumentSyntax>()
				.Where(a => a.NameColon?.Name.Identifier.Text == "mipmap")
				.Select(a => new Edit(a.Span, a.Expression.ToString()))),

		// Vector2.Dot(value2: b, value1: a) -> Vector2.Dot(a, b) (KNI's parameter names differ).
		new Rule("Vector2.Dot named arguments", Required: false, ctx =>
			ctx.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
				.Where(inv => inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Dot" }
					&& inv.ArgumentList.Arguments.Count == 2
					&& inv.ArgumentList.Arguments.All(a => a.NameColon?.Name.Identifier.Text is "value1" or "value2"))
				.Select(inv =>
				{
					var args = inv.ArgumentList.Arguments;
					ArgumentSyntax v1 = args.First(a => a.NameColon!.Name.Identifier.Text == "value1");
					ArgumentSyntax v2 = args.First(a => a.NameColon!.Name.Identifier.Text == "value2");
					return new Edit(inv.ArgumentList.Span, $"({v1.Expression}, {v2.Expression})");
				})),

		// device.Adapter.SupportedDisplayModes / CurrentDisplayMode -> PortDisplayModes (KNI's WebGL adapter doesn't implement them).
		new Rule("Display mode queries", Required: true, ctx =>
			ctx.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
				.Where(ma => ma.Name.Identifier.Text is "SupportedDisplayModes" or "CurrentDisplayMode"
					&& ma.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Adapter" })
				.Select(ma =>
				{
					var device = ((MemberAccessExpressionSyntax)ma.Expression).Expression;
					string method = ma.Name.Identifier.Text == "SupportedDisplayModes" ? "Supported" : "Current";
					return new Edit(ma.Span, $"PortDisplayModes.{method}({device})");
				})),
	};

	private static IEnumerable<Edit> DesktopOnlyCode(FileContext ctx)
	{
		if (IsExcludedFromWebBuild(ctx.RelPath))
		{
			yield break;
		}
		var targets = new List<(TextSpan Span, Func<Edit> Make)>();
		foreach (IdentifierNameSyntax id in ctx.Root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => DesktopOnlyTypes.Contains(i.Identifier.Text)))
		{
			StatementSyntax? stmt = id.Ancestors().OfType<StatementSyntax>().FirstOrDefault(s => s is not BlockSyntax);
			if (stmt == null)
			{
				continue; // not inside a method body (e.g. a using alias); handled by the compiler if ever needed
			}
			if (stmt is IfStatementSyntax ifs && ifs.Else != null && ifs.Condition.Span.Contains(id.Span))
			{
				// if (desktop) {...} else rest  ->  #if !WEB if (desktop) {...} else #endif rest
				targets.Add((ifs.Span, () =>
				{
					string indent = IndentAt(ctx.Text, ifs.SpanStart);
					string head = ctx.Text.ToString(TextSpan.FromBounds(ifs.SpanStart, ifs.Statement.Span.End));
					string rest = ctx.Text.ToString(ifs.Else.Statement.Span);
					return new Edit(ifs.Span, $"\n#if !WEB\n{indent}{head}\n{indent}else\n#endif\n{indent}{rest}");
				}));
				continue;
			}
			if (stmt is LocalDeclarationStatementSyntax && stmt.Parent is BlockSyntax block)
			{
				// Later statements use the declared variable: compile out the rest of the block too.
				StatementSyntax last = block.Statements.Last();
				targets.Add((TextSpan.FromBounds(stmt.SpanStart, last.Span.End), () => WrapNotWeb(ctx.Text, stmt, last)));
				continue;
			}
			targets.Add((stmt.Span, () => WrapNotWeb(ctx.Text, stmt)));
		}
		// Keep only outermost targets (one wrap per region).
		var distinct = targets.GroupBy(t => t.Span).Select(g => g.First()).ToList();
		foreach (var t in distinct.Where(t => !distinct.Any(o => o.Span != t.Span && o.Span.Contains(t.Span))))
		{
			yield return t.Make();
		}
	}

	// ======================================================================
	// Browser port: targeted patches (found by type + member + shape; the inserted code is ours).
	// ======================================================================

	public static IEnumerable<Rule> WebTargeted() => new[]
	{
		new Rule("Game1.InitializeSounds uses Web Audio", Required: true, ctx =>
			Methods(ctx.Root, "Game1", "InitializeSounds")
				.Select(m => m.Body?.DescendantNodes().OfType<TryStatementSyntax>().FirstOrDefault())
				.Where(t => t != null)
				.Select(t => WrapNotWeb(ctx.Text, t!, """
					// XACT can't run in the browser (the main wave bank alone is ~440 MB); the host page
					// loads the exported cues into a Web Audio backend instead. Silent if that failed.
					if (StardewValley.WebPlatform.Audio.WebAudio.Available)
					{
						log.Verbose("[port] Web build: using Web Audio.");
						audioEngine = new StardewValley.WebPlatform.Audio.WebAudioEngine();
						soundBank = new StardewValley.WebPlatform.Audio.WebSoundBank();
					}
					else
					{
						log.Verbose("[port] Web build: audio unavailable, using dummy audio.");
						audioEngine = new DummyAudioEngine();
						soundBank = new DummySoundBank();
					}
					"""))),

		new Rule("Debug builds log to the console and a file", Required: true, ctx =>
			InType<ExpressionStatementSyntax>(ctx.Root, "Game1")
				.Where(s => s.Expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.Text: "log" } } a && IsObjectCreationOf(a.Right, "DefaultLogger"))
				.Select(s => new Edit(s.Span, "\n#if DEBUG\n"
					+ "// The file logger's constructor logs through Game1.log, so it must already be set.\n"
					+ "log = new DefaultLogger(shouldWriteToConsole: true, shouldWriteToLogFile: false);\n"
					+ "log = new DefaultLogger(shouldWriteToConsole: true, shouldWriteToLogFile: true);\n"
					+ $"#else\n{s}\n#endif\n{IndentAt(ctx.Text, s.SpanStart)}"))),

		new Rule("GameRunner requests the HiDef graphics profile", Required: true, ctx =>
			InType<ExpressionStatementSyntax>(ctx.Root, "GameRunner")
				.Where(s => s.Expression is AssignmentExpressionSyntax a && IsObjectCreationOf(a.Right, "GraphicsDeviceManager"))
				.Select(s => InsertAfter(ctx.Text, s, $"""
					#if WEB
					// KNI defaults to Reach (2048px texture cap); desktop MonoGame runs the game on HiDef.
					{((AssignmentExpressionSyntax)s.Expression).Left}.GraphicsProfile = GraphicsProfile.HiDef;
					#endif
					"""))),

		new Rule("GameRunner.OnActivated uses KNI's signature", Required: true, ctx =>
			Methods(ctx.Root, "GameRunner", "OnActivated")
				.Where(m => m.ParameterList.Parameters.Count == 2 && m.Body != null)
				.Select(m =>
				{
					// Build the web variant from the method itself: KNI keeps XNA's single-argument signature.
					string sender = m.ParameterList.Parameters[0].Identifier.Text;
					string args = m.ParameterList.Parameters[1].Identifier.Text;
					string method = ctx.Text.ToString(m.Span);
					int paramStart = m.ParameterList.SpanStart - m.SpanStart;
					int bodyOpen = m.Body!.OpenBraceToken.Span.End - m.SpanStart;
					string web = method[..paramStart] + $"(EventArgs {args})" + method[(paramStart + m.ParameterList.Span.Length)..bodyOpen]
						+ $"\nobject {sender} = this;" + method[bodyOpen..];
					return WrapNotWeb(ctx.Text, m, web);
				})),

		new Rule("GameRunner.Update counts updates for ?perf", Required: true, ctx =>
			Methods(ctx.Root, "GameRunner", "Update")
				.Where(m => m.Body?.Statements.Count > 0)
				.Select(m => InsertBefore(ctx.Text, m.Body!.Statements[0], """
					#if WEB
					StardewValley.WebPlatform.WebEntry.CountUpdate();
					#endif
					"""))),

		new Rule("KeyboardDispatcher uses TextInput in the browser", Required: true, ctx =>
			InType<ConstructorDeclarationSyntax>(ctx.Root, "KeyboardDispatcher")
				.SelectMany(c => c.DescendantNodes().OfType<IfStatementSyntax>())
				.Where(i => i.Condition.ToString().Contains("PlatformID.Win32NT"))
				.Take(1)
				.Select(i => new Edit(i.Condition.Span, $"({i.Condition}) || OperatingSystem.IsBrowser()"))),

		new Rule("Content root is relative to the page", Required: true, ctx =>
			Methods(ctx.Root, "LocalizedContentManager", "GetContentRoot")
				.Select(m => m.Body?.Statements.OfType<IfStatementSyntax>().FirstOrDefault())
				.Where(i => i?.Statement is BlockSyntax { Statements.Count: > 0 } && i.Condition is BinaryExpressionSyntax)
				.Select(i =>
				{
					string field = ((BinaryExpressionSyntax)i!.Condition).Left.ToString();
					return InsertBefore(ctx.Text, ((BlockSyntax)i.Statement).Statements[0], $"""
						#if WEB
						// No local install in the browser: content is fetched relative to the page (/Content/...).
						{field} = base.RootDirectory;
						return {field};
						#endif
						""");
				})),

		// Two passes on the same method: first the parse call inside the if, then the if's condition.
		new Rule("Content manifest is parsed from fetched text", Required: true, ctx =>
			Methods(ctx.Root, "LocalizedContentManager", "PlatformEnsureManifestInitialized")
				.SelectMany(m => m.DescendantNodes().OfType<ExpressionStatementSyntax>())
				.Where(s => s.Expression is AssignmentExpressionSyntax { Right: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ParseFromFile" } } })
				.Select(s => WrapNotWeb(ctx.Text, s, $"{((AssignmentExpressionSyntax)s.Expression).Left} = PortContent.ParseContentHashes(json);"))),

		new Rule("Content manifest is fetched over HTTP", Required: true, ctx =>
			Methods(ctx.Root, "LocalizedContentManager", "PlatformEnsureManifestInitialized")
				.SelectMany(m => m.DescendantNodes().OfType<IfStatementSyntax>())
				.Where(i => i.Condition is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Exists" } } inv && inv.ArgumentList.Arguments.Count == 1)
				.Take(1)
				.Select(i =>
				{
					string path = ((InvocationExpressionSyntax)i.Condition).ArgumentList.Arguments[0].ToString();
					string indent = IndentAt(ctx.Text, i.SpanStart);
					string header = ctx.Text.ToString(TextSpan.FromBounds(i.SpanStart, i.CloseParenToken.Span.End));
					string rest = ctx.Text.ToString(TextSpan.FromBounds(i.Statement.SpanStart, i.Span.End));
					return new Edit(i.Span, $"\n#if WEB\n{indent}string json = PortContent.TryReadText({path});\n{indent}if (json != null)\n#else\n{indent}{header}\n#endif\n{indent}{rest}");
				})),

		new Rule("Streamed Ogg cues report 'not supported' on web", Required: true, ctx =>
			InType<StatementSyntax>(ctx.Root, "AudioCueModificationManager")
				.Where(s => s is ExpressionStatementSyntax && s.DescendantNodes().Any(n => IsObjectCreationOf(n, "OggStreamSoundEffect")))
				.Select(s =>
				{
					var create = s.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().First(n => IsObjectCreationOf(n, "OggStreamSoundEffect"));
					string file = create.ArgumentList?.Arguments.FirstOrDefault()?.ToString() ?? "\"\"";
					return WrapNotWeb(ctx.Text, s, $"""
						// KNI's SoundEffect is sealed, so there's no streamed-Ogg subclass (yet).
						throw new PlatformNotSupportedException("Streamed Ogg audio isn't supported in the web build yet: " + {file});
						""");
				})),

		new Rule("SoundEffect.FromStream uses KNI's overload", Required: true, ctx =>
			InType<ExpressionStatementSyntax>(ctx.Root, "AudioCueModificationManager")
				.Select(s => (Stmt: s, Call: s.DescendantNodes().OfType<InvocationExpressionSyntax>()
					.FirstOrDefault(c => c.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "FromStream" } && c.ArgumentList.Arguments.Count == 2)))
				.Where(x => x.Call != null)
				.Select(x =>
				{
					string stmt = ctx.Text.ToString(x.Stmt.Span);
					int argStart = x.Call!.ArgumentList.SpanStart - x.Stmt.SpanStart;
					string web = stmt[..argStart] + $"({x.Call.ArgumentList.Arguments[0]})" + stmt[(argStart + x.Call.ArgumentList.Span.Length)..];
					return WrapNotWeb(ctx.Text, x.Stmt, web);
				})),

		new Rule("'Exit to Desktop' returns to the title screen", Required: true, ctx =>
			Methods(ctx.Root, "InstanceGame", "Exit")
				.Where(m => m.Body?.Statements.Count > 0)
				.Select(m => WrapNotWeb(ctx.Text, m.Body!.Statements.First(), m.Body.Statements.Last(), """
					// A browser tab can't close itself (KNI throws), so "Exit to Desktop" returns to the title screen.
					Game1.quit = false;
					if (Game1.gameMode != Game1.titleScreenGameMode)
					{
						Game1.ExitToTitle();
					}
					"""))),
	};
}
