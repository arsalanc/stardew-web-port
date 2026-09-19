using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PortPatcher;

/// <summary>A text replacement in one file, produced by a rule.</summary>
public record Edit(TextSpan Span, string NewText);

/// <summary>Helpers for building edits that keep preprocessor directives on their own lines.</summary>
public static class Edits
{
	/// <summary>Applies non-overlapping edits (later ones first); overlapping edits are skipped and returned.</summary>
	public static (string Text, List<Edit> Skipped) Apply(string text, IEnumerable<Edit> edits)
	{
		var ordered = edits.OrderByDescending(e => e.Span.Start).ThenByDescending(e => e.Span.End).ToList();
		var skipped = new List<Edit>();
		var sb = new System.Text.StringBuilder(text);
		int limit = int.MaxValue;
		foreach (Edit e in ordered)
		{
			if (e.Span.End > limit)
			{
				skipped.Add(e);
				continue;
			}
			sb.Remove(e.Span.Start, e.Span.Length).Insert(e.Span.Start, e.NewText);
			limit = e.Span.Start;
		}
		return (sb.ToString(), skipped);
	}

	/// <summary>True if the nodes' span starts and ends a line (only whitespace around it).</summary>
	private static bool OwnsLines(SourceText text, TextSpan span)
	{
		TextLine first = text.Lines.GetLineFromPosition(span.Start);
		TextLine last = text.Lines.GetLineFromPosition(span.End);
		return string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(first.Start, span.Start)))
			&& string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(span.End, last.End)));
	}

	private static TextSpan FullLines(SourceText text, TextSpan span) =>
		TextSpan.FromBounds(text.Lines.GetLineFromPosition(span.Start).Start, text.Lines.GetLineFromPosition(span.End).EndIncludingLineBreak);

	public static string IndentAt(SourceText text, int position)
	{
		TextLine line = text.Lines.GetLineFromPosition(position);
		string content = text.ToString(line.Span);
		return content[..(content.Length - content.TrimStart().Length)];
	}

	/// <summary>Re-indents a block of our code to the given indentation.</summary>
	public static string Indented(string code, string indent) =>
		string.Join("\n", code.Replace("\r\n", "\n").Trim('\n').Split('\n').Select(l => l.Length == 0 ? l : indent + l)) + "\n";

	/// <summary>
	/// Wraps [first..last] in "#if !WEB ... #endif", or "#if WEB webCode #else ... #endif" when web code is given.
	/// </summary>
	public static Edit WrapNotWeb(SourceText text, SyntaxNode first, SyntaxNode last, string? webCode = null)
	{
		var span = TextSpan.FromBounds(first.SpanStart, last.Span.End);
		string indent = IndentAt(text, first.SpanStart);
		string web = webCode == null ? "" : Indented(webCode, indent);
		if (OwnsLines(text, span))
		{
			TextSpan lines = FullLines(text, span);
			string original = text.ToString(lines);
			if (!original.EndsWith('\n'))
			{
				original += "\n";
			}
			return new Edit(lines, webCode == null
				? $"#if !WEB\n{original}#endif\n"
				: $"#if WEB\n{web}#else\n{original}#endif\n");
		}
		// The code shares its line with something else: break the line around the directives.
		string inner = text.ToString(span);
		return new Edit(span, webCode == null
			? $"\n#if !WEB\n{inner}\n#endif\n{indent}"
			: $"\n#if WEB\n{web}#else\n{inner}\n#endif\n{indent}");
	}

	public static Edit WrapNotWeb(SourceText text, SyntaxNode node, string? webCode = null) => WrapNotWeb(text, node, node, webCode);

	/// <summary>Inserts our code on new lines just before the given statement.</summary>
	public static Edit InsertBefore(SourceText text, SyntaxNode node, string code)
	{
		TextLine line = text.Lines.GetLineFromPosition(node.SpanStart);
		return new Edit(new TextSpan(line.Start, 0), Indented(code, IndentAt(text, node.SpanStart)));
	}

	/// <summary>Inserts our code on new lines just after the given statement.</summary>
	public static Edit InsertAfter(SourceText text, SyntaxNode node, string code)
	{
		TextLine line = text.Lines.GetLineFromPosition(node.Span.End);
		return new Edit(new TextSpan(line.EndIncludingLineBreak, 0), Indented(code, IndentAt(text, node.SpanStart)));
	}

	// ---------- finding code ----------

	public static IEnumerable<T> InType<T>(SyntaxNode root, string typeName) where T : SyntaxNode =>
		root.DescendantNodes().OfType<TypeDeclarationSyntax>()
			.Where(t => t.Identifier.Text == typeName)
			.SelectMany(t => t.DescendantNodes().OfType<T>());

	public static IEnumerable<MethodDeclarationSyntax> Methods(SyntaxNode root, string typeName, string methodName) =>
		InType<MethodDeclarationSyntax>(root, typeName).Where(m => m.Identifier.Text == methodName);

	public static bool IsObjectCreationOf(SyntaxNode node, string typeName) =>
		node is ObjectCreationExpressionSyntax oc && LastName(oc.Type) == typeName;

	public static string LastName(TypeSyntax type) => type switch
	{
		QualifiedNameSyntax q => q.Right.Identifier.Text,
		SimpleNameSyntax s => s.Identifier.Text,
		_ => type.ToString(),
	};

	public static bool ReferencesName(SyntaxNode node, string name) =>
		node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == name);
}
