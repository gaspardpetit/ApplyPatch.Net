// ported from https://github.com/openai/openai-agents-python

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
		"*** Add File:"
	};

	private static readonly string[] END_SECTION_MARKERS =
	{
		END_PATCH,
		"*** Update File:",
		"*** Delete File:",
		"*** Add File:",
		END_FILE
	};

	// ---------- Data structures ----------

	public sealed record Chunk(
		int OrigIndex,
		List<string> DelLines,
		List<string> InsLines
	);

	internal sealed class ParserState
	{
		public List<string> Lines { get; }
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
		List<string> NextContext,
		List<Chunk> SectionChunks,
		int EndIndex,
		bool Eof
	);

	internal sealed record ContextMatch(
		int NewIndex,
		int Fuzz
	);

	// ---------- Public API ----------

	public static string ApplyDiff(string input, string diff, ApplyDiffMode mode = ApplyDiffMode.Default)
	{
		var diffLines = NormalizeDiffLines(diff);

		if (mode == ApplyDiffMode.Create)
			return ParseCreateDiff(diffLines);

		var parsed = ParseUpdateDiff(diffLines, input);
		return ApplyChunks(input, parsed.Chunks);
	}

	// ---------- Parsing helpers ----------

	internal static List<string> NormalizeDiffLines(string diff)
	{
		var lines = Regex.Split(diff, "\r?\n")
						 .Select(l => l.TrimEnd('\r'))
						 .ToList();

		if (lines.Count > 0 && lines[lines.Count - 1] == "")
			lines.RemoveAt(lines.Count - 1);

		return lines;
	}

	internal static bool IsDone(ParserState state, IReadOnlyList<string> prefixes)
	{
		if (state.Index >= state.Lines.Count)
			return true;

		return prefixes.Any(p => state.Lines[state.Index].StartsWith(p));
	}

	internal static string ReadStr(ParserState state, string prefix)
	{
		if (state.Index >= state.Lines.Count)
			return "";

		var current = state.Lines[state.Index];
		if (current.StartsWith(prefix))
		{
			state.Index++;
			return current.Substring(prefix.Length);
		}

		return "";
	}

	// ---------- Create diff ----------

	internal static string ParseCreateDiff(List<string> lines)
	{
		var parser = new ParserState(lines.Concat(new[] { END_PATCH }).ToList());
		var output = new List<string>();

		while (!IsDone(parser, SECTION_TERMINATORS))
		{
			if (parser.Index >= parser.Lines.Count)
				break;

			var line = parser.Lines[parser.Index++];
			if (!line.StartsWith("+"))
				throw new InvalidOperationException($"Invalid Add File Line: {line}");

			output.Add(line.Substring(1));
		}

		return string.Join("\n", output);
	}

	// ---------- Update diff ----------

	internal static ParsedUpdateDiff ParseUpdateDiff(List<string> lines, string input)
	{
		var parser = new ParserState(lines.Concat(new[] { END_PATCH }).ToList());
		var inputLines = input.Split('\n').ToList();
		var chunks = new List<Chunk>();
		int cursor = 0;

		while (!IsDone(parser, END_SECTION_MARKERS))
		{
			var anchor = ReadStr(parser, "@@ ");
			bool hasBareAnchor =
				anchor == "" &&
				parser.Index < parser.Lines.Count &&
				parser.Lines[parser.Index] == "@@";

			if (hasBareAnchor)
				parser.Index++;

			if (!(anchor.Length > 0 || hasBareAnchor || cursor == 0))
			{
				var currentLine = parser.Index < parser.Lines.Count
					? parser.Lines[parser.Index]
					: "";
				throw new InvalidOperationException($"Invalid Line:\n{currentLine}");
			}

			if (anchor.Trim().Length > 0)
				cursor = AdvanceCursorToAnchor(anchor, inputLines, cursor, parser);

			var section = ReadSection(parser.Lines, parser.Index);
			var findResult = FindContext(inputLines, section.NextContext, cursor, section.Eof);

			if (findResult.NewIndex == -1)
			{
				var ctxText = string.Join("\n", section.NextContext);
				if (section.Eof)
					throw new InvalidOperationException($"Invalid EOF Context {cursor}:\n{ctxText}");

				throw new InvalidOperationException($"Invalid Context {cursor}:\n{ctxText}");
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

	internal static int AdvanceCursorToAnchor(
		string anchor,
		List<string> inputLines,
		int cursor,
		ParserState parser)
	{
		bool found = false;

		if (!inputLines.Take(cursor).Any(l => l == anchor))
		{
			for (int i = cursor; i < inputLines.Count; i++)
			{
				if (inputLines[i] == anchor)
				{
					cursor = i + 1;
					found = true;
					break;
				}
			}
		}

		if (!found && !inputLines.Take(cursor).Any(l => l.Trim() == anchor.Trim()))
		{
			for (int i = cursor; i < inputLines.Count; i++)
			{
				if (inputLines[i].Trim() == anchor.Trim())
				{
					cursor = i + 1;
					parser.Fuzz += 1;
					found = true;
					break;
				}
			}
		}

		return cursor;
	}

	// ---------- Section parsing ----------

	internal static ReadSectionResult ReadSection(List<string> lines, int startIndex)
	{
		var context = new List<string>();
		var delLines = new List<string>();
		var insLines = new List<string>();
		var sectionChunks = new List<Chunk>();

		string mode = "keep";
		int index = startIndex;
		int origIndex = index;

		while (index < lines.Count)
		{
			var raw = lines[index];

			if (raw.StartsWith("@@") ||
				raw.StartsWith(END_PATCH) ||
				raw.StartsWith("*** Update File:") ||
				raw.StartsWith("*** Delete File:") ||
				raw.StartsWith("*** Add File:") ||
				raw.StartsWith(END_FILE))
				break;

			if (raw == "***")
				break;

			if (raw.StartsWith("***"))
				throw new InvalidOperationException($"Invalid Line: {raw}");

			index++;
			var lastMode = mode;

			var line = raw.Length > 0 ? raw : " ";
			char prefix = line[0];

			mode = prefix switch {
				'+' => "add",
				'-' => "delete",
				' ' => "keep",
				_ => throw new InvalidOperationException($"Invalid Line: {line}")
			};

			var content = line.Substring(1);
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
				delLines.Add(content);
				context.Add(content);
			}
			else if (mode == "add")
			{
				insLines.Add(content);
			}
			else
			{
				context.Add(content);
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
			throw new InvalidOperationException($"Nothing in this section - index={index} {nextLine}");
		}

		return new ReadSectionResult(context, sectionChunks, index, false);
	}

	// ---------- Context matching ----------

	internal static ContextMatch FindContext(
		List<string> lines,
		List<string> context,
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
		List<string> context,
		int start)
	{
		if (context.Count == 0)
			return new ContextMatch(start, 0);

		for (int i = start; i < lines.Count; i++)
			if (EqualsSlice(lines, context, i, v => v))
				return new ContextMatch(i, 0);

		for (int i = start; i < lines.Count; i++)
			if (EqualsSlice(lines, context, i, v => v.TrimEnd()))
				return new ContextMatch(i, 1);

		for (int i = start; i < lines.Count; i++)
			if (EqualsSlice(lines, context, i, v => v.Trim()))
				return new ContextMatch(i, 100);

		return new ContextMatch(-1, 0);
	}

	internal static bool EqualsSlice(
		List<string> source,
		List<string> target,
		int start,
		Func<string, string> mapFn)
	{
		if (start + target.Count > source.Count)
			return false;

		for (int offset = 0; offset < target.Count; offset++)
		{
			if (mapFn(source[start + offset]) != mapFn(target[offset]))
				return false;
		}

		return true;
	}

	// ---------- Apply chunks ----------

	internal static string ApplyChunks(string input, List<Chunk> chunks)
	{
		var origLines = input.Split('\n').ToList();
		var destLines = new List<string>();
		int cursor = 0;

		foreach (var chunk in chunks)
		{
			if (chunk.OrigIndex > origLines.Count)
				throw new InvalidOperationException(
					$"applyDiff: chunk.origIndex {chunk.OrigIndex} > input length {origLines.Count}");

			if (cursor > chunk.OrigIndex)
				throw new InvalidOperationException(
					$"applyDiff: overlapping chunk at {chunk.OrigIndex} (cursor {cursor})");

			destLines.AddRange(origLines.GetRange(cursor, chunk.OrigIndex - cursor));
			cursor = chunk.OrigIndex;

			if (chunk.InsLines.Count > 0)
				destLines.AddRange(chunk.InsLines);

			cursor += chunk.DelLines.Count;
		}

		destLines.AddRange(origLines.Skip(cursor));
		return string.Join("\n", destLines);
	}
}
