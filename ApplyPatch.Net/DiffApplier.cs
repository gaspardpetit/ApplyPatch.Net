//-----------------------------------------------------------------
// PORT HISTORY
// 2025-11-13 - Add new tools for gpt-5.1
//              https://github.com/openai/openai-agents-python/pull/2079
// 2026-03-10 - fix: emit tracing function spans for shell/apply_patch/computer runtime tools
//              https://github.com/openai/openai-agents-python/pull/2498
// 2026-03-12
//  The following changes are now officially supported by the apply_patch as
//  provided by OpenAI, however, for models that are not trained with apply_patch
//  they are useful - especially those struggling to reproduce a perfect line when it is
//  long. These are optional and can be provided in instructions, but do not interfer with
//  the core functionality of the apply_patch tool.
//	- Added support for Set File (aliases Add File)
//	- Added support for hunk prefix anchors: ^ (starts with) and $ (ends with)
//-----------------------------------------------------------------

using System.Text.RegularExpressions;

namespace ApplyPatch;

public static class DiffApplier
{
	public enum ApplyDiffMode
	{
		Default,
		Create
	}

	private const string END_PATCH = "*** End Patch";
	private const string END_FILE = "*** End of File";

	private static readonly string[] SECTION_TERMINATORS =
	{
		END_PATCH,
		"*** Update File:",
		"*** Delete File:",
		"*** Add File:",
		"*** Set File:"
	};

	private static readonly string[] END_SECTION_MARKERS =
	{
		END_PATCH,
		"*** Update File:",
		"*** Delete File:",
		"*** Add File:",
		"*** Set File:",
		END_FILE
	};

	public enum ContextMatchKind
	{
		Exact,
		StartsWith,
		EndsWith
	}

	public sealed record ContextLine(
		ContextMatchKind Kind,
		string Text
	);

	public sealed record Chunk(
		int OrigIndex,
		List<string> DelLines,
		List<string> InsLines
	);

	internal sealed class ParserState
	{
		public List<string> Lines { get; private set; }
		public int Index { get; set; }
		public int Fuzz { get; set; }

		public ParserState(List<string> lines)
		{
			Lines = lines;
			Index = 0;
			Fuzz = 0;
		}
	}

	internal sealed record ParsedUpdateDiff(
		List<Chunk> Chunks,
		int Fuzz
	);

	internal sealed record ReadSectionResult(
		List<ContextLine> NextContext,
		List<Chunk> SectionChunks,
		int EndIndex,
		bool Eof
	);

	internal sealed record ContextMatch(
		int NewIndex,
		int Fuzz
	);

	public static string ApplyDiff(string input, string diff, ApplyDiffMode mode = ApplyDiffMode.Default)
	{
		var newline = DetectNewline(input, diff, mode);
		var diffLines = NormalizeDiffLines(diff);

		if (mode == ApplyDiffMode.Create)
			return ParseCreateDiff(diffLines, newline);

		var normalizedInput = NormalizeTextNewlines(input);
		var parsed = ParseUpdateDiff(diffLines, normalizedInput);
		return ApplyChunks(normalizedInput, parsed.Chunks, newline);
	}

	internal static List<string> NormalizeDiffLines(string diff)
	{
		var lines = Regex.Split(diff, "\r?\n")
			.Select(l => l.TrimEnd('\r'))
			.ToList();

		if (lines.Count > 0 && lines[lines.Count - 1] == "")
			lines.RemoveAt(lines.Count - 1);

		return lines;
	}

	internal static string DetectNewlineFromText(string text)
	{
		return text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
	}

	internal static string DetectNewline(string input, string diff, ApplyDiffMode mode)
	{
		// Create-file diffs don't have an input to infer newline style from.
		// Use the diff's newline style if present, otherwise default to LF.
		if (mode != ApplyDiffMode.Create && input.IndexOf('\n') >= 0)
			return DetectNewlineFromText(input);

		return DetectNewlineFromText(diff);
	}

	internal static string NormalizeTextNewlines(string text)
	{
		// Normalize CRLF to LF for parsing/matching. Newline style is restored when emitting.
		return text.Replace("\r\n", "\n");
	}

	internal static bool IsDone(ParserState state, IReadOnlyList<string> prefixes)
	{
		if (state.Index >= state.Lines.Count)
			return true;

		return prefixes.Any(p => state.Lines[state.Index].StartsWith(p, StringComparison.Ordinal));
	}

	internal static string ParseCreateDiff(List<string> lines, string newline)
	{
		var parser = new ParserState(lines.Concat(new[] { END_PATCH }).ToList());
		var output = new List<string>();

		if (parser.Index < parser.Lines.Count)
		{
			var firstLine = parser.Lines[parser.Index];
			if (firstLine.StartsWith("*** Add File:", StringComparison.Ordinal) ||
				firstLine.StartsWith("*** Set File:", StringComparison.Ordinal))
			{
				parser.Index++;
			}
		}

		while (!IsDone(parser, SECTION_TERMINATORS))
		{
			if (parser.Index >= parser.Lines.Count)
				break;

			var line = parser.Lines[parser.Index++];
			if (!line.StartsWith("+", StringComparison.Ordinal))
				throw new InvalidOperationException("Invalid Add File Line: " + line);

			output.Add(line.Substring(1));
		}

		return string.Join(newline, output);
	}

	internal enum AnchorMatchKind
	{
		Literal,
		StartsWith,
		EndsWith
	}

	internal sealed record ParsedAnchor(
		bool IsBare,
		AnchorMatchKind Kind,
		string Text
	);

	internal static ParsedAnchor? TryReadAnchor(ParserState state)
	{
		if (state.Index >= state.Lines.Count)
			return null;

		var line = state.Lines[state.Index];

		if (line == "@@")
		{
			state.Index++;
			return new ParsedAnchor(true, AnchorMatchKind.Literal, "");
		}

		if (line.StartsWith("@@^", StringComparison.Ordinal))
		{
			state.Index++;
			return new ParsedAnchor(false, AnchorMatchKind.StartsWith, line.Substring(3));
		}

		if (line.StartsWith("@@$", StringComparison.Ordinal))
		{
			state.Index++;
			return new ParsedAnchor(false, AnchorMatchKind.EndsWith, line.Substring(3));
		}

		if (line.StartsWith("@@ ", StringComparison.Ordinal))
		{
			state.Index++;
			return new ParsedAnchor(false, AnchorMatchKind.Literal, line.Substring(3));
		}

		return null;
	}

	internal static ParsedUpdateDiff ParseUpdateDiff(List<string> lines, string input)
	{
		var parser = new ParserState(lines.Concat(new[] { END_PATCH }).ToList());
		var inputLines = input.Split('\n').ToList();
		var chunks = new List<Chunk>();
		int cursor = 0;

		while (!IsDone(parser, END_SECTION_MARKERS))
		{
			var parsedAnchor = TryReadAnchor(parser);

			if (!(parsedAnchor != null || cursor == 0))
			{
				var currentLine = parser.Index < parser.Lines.Count
					? parser.Lines[parser.Index]
					: "";
				throw new InvalidOperationException("Invalid Line:\n" + currentLine);
			}

			if (parsedAnchor is { IsBare: false } anchor && anchor.Text.Trim().Length > 0)
				cursor = AdvanceCursorToAnchor(anchor, inputLines, cursor, parser);

			ReadSectionResult section = ReadSection(parser.Lines, parser.Index);
			ContextMatch findResult = FindContext(inputLines, section.NextContext, cursor, section.Eof);

			if (findResult.NewIndex == -1)
			{
				var ctxText = string.Join("\n", section.NextContext.Select(c =>
					c.Kind switch {
						ContextMatchKind.Exact => c.Text,
						ContextMatchKind.StartsWith => "^" + c.Text,
						ContextMatchKind.EndsWith => "$" + c.Text,
						_ => c.Text
					}));
				if (section.Eof)
					throw new InvalidOperationException("Invalid EOF Context " + cursor + ":\n" + ctxText);

				throw new InvalidOperationException("Invalid Context " + cursor + ":\n" + ctxText);
			}

			cursor = findResult.NewIndex + section.NextContext.Count;
			parser.Fuzz += findResult.Fuzz;
			parser.Index = section.EndIndex;

			foreach (var ch in section.SectionChunks)
			{
				chunks.Add(new Chunk(
					ch.OrigIndex + findResult.NewIndex,
					new List<string>(ch.DelLines),
					new List<string>(ch.InsLines)
				));
			}
		}

		return new ParsedUpdateDiff(chunks, parser.Fuzz);
	}

	internal static bool MatchesAnchor(
	string sourceLine,
	ParsedAnchor anchor,
	Func<string, string> mapFn)
	{
		var source = mapFn(sourceLine);
		var target = mapFn(anchor.Text);

		return anchor.Kind switch {
			AnchorMatchKind.Literal => source == target,
			AnchorMatchKind.StartsWith => source.StartsWith(target, StringComparison.Ordinal),
			AnchorMatchKind.EndsWith => source.EndsWith(target, StringComparison.Ordinal),
			_ => false
		};
	}

	internal static int AdvanceCursorToAnchor(
		ParsedAnchor anchor,
		List<string> inputLines,
		int cursor,
		ParserState parser)
	{
		bool found = false;

		bool SeenBefore(Func<string, string> mapFn) =>
			inputLines.Take(cursor).Any(l => MatchesAnchor(l, anchor, mapFn));

		if (!SeenBefore(v => v))
		{
			for (int i = cursor; i < inputLines.Count; i++)
			{
				if (MatchesAnchor(inputLines[i], anchor, v => v))
				{
					cursor = i + 1;
					found = true;
					break;
				}
			}
		}

		if (!found && !SeenBefore(v => v.TrimEnd()))
		{
			for (int i = cursor; i < inputLines.Count; i++)
			{
				if (MatchesAnchor(inputLines[i], anchor, v => v.TrimEnd()))
				{
					cursor = i + 1;
					parser.Fuzz += 1;
					found = true;
					break;
				}
			}
		}

		if (!found && !SeenBefore(v => v.Trim()))
		{
			for (int i = cursor; i < inputLines.Count; i++)
			{
				if (MatchesAnchor(inputLines[i], anchor, v => v.Trim()))
				{
					cursor = i + 1;
					parser.Fuzz += 100;
					found = true;
					break;
				}
			}
		}

		return cursor;
	}

	internal static ContextLine ParseContextLine(char prefix, string lineContent)
	{
		if (prefix == '^')
			return new ContextLine(ContextMatchKind.StartsWith, lineContent);

		if (prefix == '$')
			return new ContextLine(ContextMatchKind.EndsWith, lineContent);

		return new ContextLine(ContextMatchKind.Exact, lineContent);
	}

	internal static ReadSectionResult ReadSection(List<string> lines, int startIndex)
	{
		List<ContextLine> context = [];
		List<string> delLines = [];
		List<string> insLines = [];
		List<Chunk> sectionChunks = [];

		string mode = "keep";
		int index = startIndex;
		int origIndex = index;

		while (index < lines.Count)
		{
			var raw = lines[index];

			if (raw.StartsWith("@@", StringComparison.Ordinal) ||
				raw.StartsWith(END_PATCH, StringComparison.Ordinal) ||
				raw.StartsWith("*** Update File:", StringComparison.Ordinal) ||
				raw.StartsWith("*** Delete File:", StringComparison.Ordinal) ||
				raw.StartsWith("*** Add File:", StringComparison.Ordinal) ||
				raw.StartsWith("*** Set File:", StringComparison.Ordinal) ||
				raw.StartsWith(END_FILE, StringComparison.Ordinal))
			{
				break;
			}

			if (raw == "***")
				break;

			if (raw.StartsWith("***", StringComparison.Ordinal))
				throw new InvalidOperationException("Invalid Line: " + raw);

			index++;
			var lastMode = mode;

			var line = raw.Length > 0 ? raw : " ";
			char prefix = line[0];

			switch (prefix)
			{
				case '+':
					mode = "add";
					break;
				case '-':
					mode = "delete";
					break;
				case ' ':
				case '^':
				case '$':
					mode = "keep";
					break;
				default:
					throw new InvalidOperationException("Invalid Line: " + line);
			}

			string lineContent = prefix switch {
				' ' => line.Substring(1),
				'^' => line.Substring(1),
				'$' => line.Substring(1),
				'+' => line.Substring(1),
				'-' => line.Substring(1),
				_ => throw new InvalidOperationException("Invalid Line: " + line)
			};

			bool switchingToContext = mode == "keep" && lastMode != mode;

			if (switchingToContext && (delLines.Count > 0 || insLines.Count > 0))
			{
				sectionChunks.Add(new Chunk(
					context.Count - delLines.Count,
					new List<string>(delLines),
					new List<string>(insLines)
				));
				delLines.Clear();
				insLines.Clear();
			}

			if (mode == "delete")
			{
				delLines.Add(lineContent);
				context.Add(new ContextLine(ContextMatchKind.Exact, lineContent));
			}
			else if (mode == "add")
			{
				insLines.Add(lineContent);
			}
			else
			{
				context.Add(ParseContextLine(prefix, lineContent));
			}
		}

		if (delLines.Count > 0 || insLines.Count > 0)
		{
			sectionChunks.Add(new Chunk(
				context.Count - delLines.Count,
				new List<string>(delLines),
				new List<string>(insLines)
			));
		}

		if (index < lines.Count && lines[index] == END_FILE)
			return new ReadSectionResult(context, sectionChunks, index + 1, true);

		if (index == origIndex)
		{
			var nextLine = index < lines.Count ? lines[index] : "";
			throw new InvalidOperationException("Nothing in this section - index=" + index + " " + nextLine);
		}

		return new ReadSectionResult(context, sectionChunks, index, false);
	}

	internal static ContextMatch FindContext(
		List<string> lines,
		List<ContextLine> context,
		int start,
		bool eof)
	{
		if (eof)
		{
			int endStart = Math.Max(0, lines.Count - context.Count);
			var endMatch = FindContextCore(lines, context, endStart);
			if (endMatch.NewIndex != -1)
				return endMatch;

			var fallback = FindContextCore(lines, context, start);
			return new ContextMatch(fallback.NewIndex, fallback.Fuzz + 10000);
		}

		return FindContextCore(lines, context, start);
	}

	internal static ContextMatch FindContextCore(
		List<string> lines,
		List<ContextLine> context,
		int start)
	{
		if (context.Count == 0)
			return new ContextMatch(start, 0);

		for (int i = start; i < lines.Count; i++)
		{
			if (EqualsSlice(lines, context, i, v => v))
				return new ContextMatch(i, 0);
		}

		for (int i = start; i < lines.Count; i++)
		{
			if (EqualsSlice(lines, context, i, v => v.TrimEnd()))
				return new ContextMatch(i, 1);
		}

		for (int i = start; i < lines.Count; i++)
		{
			if (EqualsSlice(lines, context, i, v => v.Trim()))
				return new ContextMatch(i, 100);
		}

		return new ContextMatch(-1, 0);
	}

	internal static bool EqualsSlice(
	List<string> source,
	List<ContextLine> target,
	int start,
	Func<string, string> mapFn)
	{
		if (start + target.Count > source.Count)
			return false;

		for (int offset = 0; offset < target.Count; offset++)
		{
			var sourceLine = mapFn(source[start + offset]);
			var targetLine = target[offset];
			var targetText = mapFn(targetLine.Text);

			bool matched = targetLine.Kind switch {
				ContextMatchKind.Exact => sourceLine == targetText,
				ContextMatchKind.StartsWith => sourceLine.StartsWith(targetText, StringComparison.Ordinal),
				ContextMatchKind.EndsWith => sourceLine.EndsWith(targetText, StringComparison.Ordinal),
				_ => false
			};

			if (!matched)
				return false;
		}

		return true;
	}

	internal static string ApplyChunks(string input, List<Chunk> chunks, string newline)
	{
		var origLines = input.Split('\n').ToList();
		var destLines = new List<string>();
		int cursor = 0;

		foreach (var chunk in chunks)
		{
			if (chunk.OrigIndex > origLines.Count)
			{
				throw new InvalidOperationException(
					"applyDiff: chunk.origIndex " + chunk.OrigIndex + " > input length " + origLines.Count);
			}

			if (cursor > chunk.OrigIndex)
			{
				throw new InvalidOperationException(
					"applyDiff: overlapping chunk at " + chunk.OrigIndex + " (cursor " + cursor + ")");
			}

			destLines.AddRange(origLines.GetRange(cursor, chunk.OrigIndex - cursor));
			cursor = chunk.OrigIndex;

			if (chunk.InsLines.Count > 0)
				destLines.AddRange(chunk.InsLines);

			cursor += chunk.DelLines.Count;
		}

		destLines.AddRange(origLines.Skip(cursor));
		return string.Join(newline, destLines);
	}
}
