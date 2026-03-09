// DiffPatch applier based on https://github.com/openai/gpt-oss
// compatible with gpt-oss apply_patch output format.
// see https://developers.openai.com/cookbook/examples/gpt4-1_prompting_guide

using System.Text;

namespace ApplyPatch;

public static class OSSDiffApplier
{
	public enum ActionType
	{
		Add,
		Delete,
		Update
	}

	public sealed class DiffError : InvalidOperationException
	{
		public DiffError(string message) : base(message) { }
	}

	public sealed record FileChange(
		ActionType Type,
		string? OldContent = null,
		string? NewContent = null,
		string? MovePath = null
	);

	public sealed class Commit
	{
		public Dictionary<string, FileChange> Changes { get; } = new(StringComparer.Ordinal);
	}

	public sealed record Chunk(
		int OrigIndex,
		List<string> DelLines,
		List<string> InsLines
	);

	public sealed class PatchAction
	{
		public required ActionType Type { get; init; }
		public string? NewFile { get; init; }
		public List<Chunk> Chunks { get; } = new();
		public string? MovePath { get; set; }
	}

	public sealed class Patch
	{
		public Dictionary<string, PatchAction> Actions { get; } = new(StringComparer.Ordinal);
	}

	private sealed class Parser
	{
		private readonly Dictionary<string, string> _currentFiles;
		private readonly List<string> _lines;
		private int _index;

		public Patch Patch { get; } = new();
		public int Fuzz { get; private set; }

		public Parser(Dictionary<string, string> currentFiles, List<string> lines, int index = 0)
		{
			_currentFiles = currentFiles;
			_lines = lines;
			_index = index;
		}

		public void Parse()
		{
			while (!IsDone(new[] { "*** End Patch" }))
			{
				var path = ReadStr("*** Update File: ");
				if (path.Length > 0)
				{
					if (Patch.Actions.ContainsKey(path))
						throw new DiffError($"Duplicate update for file: {path}");

					var moveTo = ReadStr("*** Move to: ");
					if (!_currentFiles.TryGetValue(path, out var text))
						throw new DiffError($"Update File Error - missing file: {path}");

					var action = ParseUpdateFile(text);
					action.MovePath = moveTo.Length > 0 ? moveTo : null;
					Patch.Actions[path] = action;
					continue;
				}

				path = ReadStr("*** Delete File: ");
				if (path.Length > 0)
				{
					if (Patch.Actions.ContainsKey(path))
						throw new DiffError($"Duplicate delete for file: {path}");
					if (!_currentFiles.ContainsKey(path))
						throw new DiffError($"Delete File Error - missing file: {path}");

					Patch.Actions[path] = new PatchAction { Type = ActionType.Delete };
					continue;
				}

				path = ReadStr("*** Add File: ");
				if (path.Length > 0)
				{
					if (Patch.Actions.ContainsKey(path))
						throw new DiffError($"Duplicate add for file: {path}");
					if (_currentFiles.ContainsKey(path))
						throw new DiffError($"Add File Error - file already exists: {path}");

					Patch.Actions[path] = ParseAddFile();
					continue;
				}

				throw new DiffError($"Unknown line while parsing: {CurLine()}");
			}

			if (!StartsWith("*** End Patch"))
				throw new DiffError("Missing *** End Patch sentinel");

			_index++;
		}

		private PatchAction ParseUpdateFile(string text)
		{
			var action = new PatchAction { Type = ActionType.Update };
			var lines = text.Split('\n').ToList();
			var scanIndex = 0;

			while (!IsDone(new[]
			{
			"*** End Patch",
			"*** Update File:",
			"*** Delete File:",
			"*** Add File:",
			"*** End of File"
		}))
			{
				var defStr = ReadStr("@@ ");
				var sectionStr = string.Empty;
				if (defStr.Length == 0 && Norm(CurLine()) == "@@")
					sectionStr = ReadLine();

				if (!(defStr.Length > 0 || sectionStr.Length > 0 || scanIndex == 0))
					throw new DiffError($"Invalid line in update section:\n{CurLine()}");

				if (!string.IsNullOrWhiteSpace(defStr))
				{
					var found = false;

					if (!lines.Take(scanIndex).Contains(defStr, StringComparer.Ordinal))
					{
						for (var i = scanIndex; i < lines.Count; i++)
						{
							if (lines[i] == defStr)
							{
								scanIndex = i + 1;
								found = true;
								break;
							}
						}
					}

					if (!found && !lines.Take(scanIndex).Any(s => s.Trim() == defStr.Trim()))
					{
						for (var i = scanIndex; i < lines.Count; i++)
						{
							if (lines[i].Trim() == defStr.Trim())
							{
								scanIndex = i + 1;
								Fuzz += 1;
								found = true;
								break;
							}
						}
					}
				}

				var section = PeekNextSection(_lines, _index);
				var (newIndex, fuzz) = FindContext(lines, section.NextContext, scanIndex, section.Eof);
				if (newIndex == -1)
				{
					var ctxText = string.Join("\n", section.NextContext);
					throw new DiffError(
						$"Invalid {(section.Eof ? "EOF " : "")}context at {scanIndex}:\n{ctxText}");
				}

				Fuzz += fuzz;
				foreach (var ch in section.Chunks)
				{
					action.Chunks.Add(new Chunk(
						ch.OrigIndex + newIndex,
						new List<string>(ch.DelLines),
						new List<string>(ch.InsLines)));
				}

				scanIndex = newIndex + section.NextContext.Count;
				_index = section.EndIndex;
			}

			return action;
		}

		private PatchAction ParseAddFile()
		{
			var lines = new List<string>();

			while (!IsDone(new[]
			{
			"*** End Patch",
			"*** Update File:",
			"*** Delete File:",
			"*** Add File:"
		}))
			{
				var s = ReadLine();
				if (!s.StartsWith("+", StringComparison.Ordinal))
					throw new DiffError($"Invalid Add File line (missing '+'): {s}");
				lines.Add(s.Substring(1));
			}

			return new PatchAction {
				Type = ActionType.Add,
				NewFile = string.Join("\n", lines)
			};
		}

		private string CurLine()
		{
			if (_index >= _lines.Count)
				throw new DiffError("Unexpected end of input while parsing patch");
			return _lines[_index];
		}

		private static string Norm(string line) => line.TrimEnd('\r');

		private bool IsDone(IReadOnlyList<string>? prefixes = null)
		{
			if (_index >= _lines.Count)
				return true;

			if (prefixes is { Count: > 0 })
			{
				var cur = Norm(CurLine());
				foreach (var prefix in prefixes)
				{
					if (cur.StartsWith(prefix, StringComparison.Ordinal))
						return true;
				}
			}

			return false;
		}

		private bool StartsWith(string prefix) =>
			Norm(CurLine()).StartsWith(prefix, StringComparison.Ordinal);

		private string ReadStr(string prefix)
		{
			if (prefix.Length == 0)
				throw new ArgumentException("read_str requires non-empty prefix", nameof(prefix));

			if (_index < _lines.Count && Norm(CurLine()).StartsWith(prefix, StringComparison.Ordinal))
			{
				var text = CurLine().Substring(prefix.Length);
				_index++;
				return text;
			}

			return string.Empty;
		}

		private string ReadLine()
		{
			var line = CurLine();
			_index++;
			return line;
		}
	}

	private sealed record SectionResult(
		List<string> NextContext,
		List<Chunk> Chunks,
		int EndIndex,
		bool Eof
	);

	public static string ApplyPatch(
		string text,
		Func<string, string>? openFn = null,
		Action<string, string>? writeFn = null,
		Action<string>? removeFn = null)
	{
		openFn ??= OpenFile;
		writeFn ??= WriteFile;
		removeFn ??= RemoveFile;

		if (!text.StartsWith("*** Begin Patch", StringComparison.Ordinal))
			throw new DiffError("Patch text must start with *** Begin Patch");

		var paths = IdentifyFilesNeeded(text);
		var orig = LoadFiles(paths, openFn);
		var (patch, _) = TextToPatch(text, orig);
		var commit = PatchToCommit(patch, orig);
		ApplyCommit(commit, writeFn, removeFn);
		return "Done!";
	}

	public static (Patch Patch, int Fuzz) TextToPatch(string text, Dictionary<string, string> orig)
	{
		var lines = SplitLines(text);
		if (lines.Count < 2 ||
			!Norm(lines[0]).StartsWith("*** Begin Patch", StringComparison.Ordinal) ||
			Norm(lines[lines.Count - 1]) != "*** End Patch")
		{
			throw new DiffError("Invalid patch text - missing sentinels");
		}

		var parser = new Parser(orig, lines, 1);
		parser.Parse();
		return (parser.Patch, parser.Fuzz);
	}

	public static List<string> IdentifyFilesNeeded(string text)
	{
		var lines = SplitLines(text);
		var result = new List<string>();

		foreach (var line in lines)
		{
			if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
				result.Add(line.Substring("*** Update File: ".Length));
		}

		foreach (var line in lines)
		{
			if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
				result.Add(line.Substring("*** Delete File: ".Length));
		}

		return result;
	}

	public static List<string> IdentifyFilesAdded(string text)
	{
		var lines = SplitLines(text);
		var result = new List<string>();

		foreach (var line in lines)
		{
			if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
				result.Add(line.Substring("*** Add File: ".Length));
		}

		return result;
	}

	public static Dictionary<string, string> LoadFiles(
		IEnumerable<string> paths,
		Func<string, string> openFn)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var path in paths)
			result[path] = openFn(path);
		return result;
	}

	public static void ApplyCommit(
		Commit commit,
		Action<string, string> writeFn,
		Action<string> removeFn)
	{
		foreach (var entry in commit.Changes)
		{
			var path = entry.Key;
			var change = entry.Value;
			switch (change.Type)
			{
				case ActionType.Delete:
					removeFn(path);
					break;

				case ActionType.Add:
					if (change.NewContent is null)
						throw new DiffError($"ADD change for {path} has no content");
					writeFn(path, change.NewContent);
					break;

				case ActionType.Update:
					if (change.NewContent is null)
						throw new DiffError($"UPDATE change for {path} has no new content");
					var target = change.MovePath ?? path;
					writeFn(target, change.NewContent);
					if (change.MovePath is not null)
						removeFn(path);
					break;
			}
		}
	}

	public static Commit PatchToCommit(Patch patch, Dictionary<string, string> orig)
	{
		var commit = new Commit();

		foreach (var entry in patch.Actions)
		{
			var path = entry.Key;
			var action = entry.Value;
			switch (action.Type)
			{
				case ActionType.Delete:
					commit.Changes[path] = new FileChange(ActionType.Delete, OldContent: orig[path]);
					break;

				case ActionType.Add:
					if (action.NewFile is null)
						throw new DiffError("ADD action without file content");
					commit.Changes[path] = new FileChange(ActionType.Add, NewContent: action.NewFile);
					break;

				case ActionType.Update:
					var newContent = GetUpdatedFile(orig[path], action, path);
					commit.Changes[path] = new FileChange(
						ActionType.Update,
						OldContent: orig[path],
						NewContent: newContent,
						MovePath: action.MovePath);
					break;
			}
		}

		return commit;
	}

	public static string GetUpdatedFile(string text, PatchAction action, string path)
	{
		if (action.Type != ActionType.Update)
			throw new DiffError("_get_updated_file called with non-update action");

		var origLines = text.Split('\n').ToList();
		var destLines = new List<string>();
		var origIndex = 0;

		foreach (var chunk in action.Chunks)
		{
			if (chunk.OrigIndex > origLines.Count)
				throw new DiffError($"{path}: chunk.orig_index {chunk.OrigIndex} exceeds file length");
			if (origIndex > chunk.OrigIndex)
				throw new DiffError($"{path}: overlapping chunks at {origIndex} > {chunk.OrigIndex}");

			destLines.AddRange(origLines.Skip(origIndex).Take(chunk.OrigIndex - origIndex));
			origIndex = chunk.OrigIndex;

			destLines.AddRange(chunk.InsLines);
			origIndex += chunk.DelLines.Count;
		}

		destLines.AddRange(origLines.Skip(origIndex));
		return string.Join("\n", destLines);
	}

	private static (int NewIndex, int Fuzz) FindContext(
		List<string> lines,
		List<string> context,
		int start,
		bool eof)
	{
		if (eof)
		{
			var (newIndex, fuzz) = FindContextCore(lines, context, lines.Count - context.Count);
			if (newIndex != -1)
				return (newIndex, fuzz);

			var fallback = FindContextCore(lines, context, start);
			return (fallback.NewIndex, fallback.Fuzz + 10_000);
		}

		return FindContextCore(lines, context, start);
	}

	private static (int NewIndex, int Fuzz) FindContextCore(
		List<string> lines,
		List<string> context,
		int start)
	{
		if (context.Count == 0)
			return (start, 0);

		for (var i = start; i < lines.Count; i++)
		{
			if (SliceEquals(lines, context, i, static s => s))
				return (i, 0);
		}

		for (var i = start; i < lines.Count; i++)
		{
			if (SliceEquals(lines, context, i, static s => s.TrimEnd()))
				return (i, 1);
		}

		for (var i = start; i < lines.Count; i++)
		{
			if (SliceEquals(lines, context, i, static s => s.Trim()))
				return (i, 100);
		}

		return (-1, 0);
	}

	private static bool SliceEquals(
		List<string> lines,
		List<string> context,
		int start,
		Func<string, string> normalize)
	{
		if (start < 0 || start + context.Count > lines.Count)
			return false;

		for (var i = 0; i < context.Count; i++)
		{
			if (normalize(lines[start + i]) != normalize(context[i]))
				return false;
		}

		return true;
	}

	private static SectionResult PeekNextSection(List<string> lines, int index)
	{
		var old = new List<string>();
		var delLines = new List<string>();
		var insLines = new List<string>();
		var chunks = new List<Chunk>();
		var mode = "keep";
		var origIndex = index;

		while (index < lines.Count)
		{
			var s = lines[index];
			if (s.StartsWith("@@", StringComparison.Ordinal) ||
				s.StartsWith("*** End Patch", StringComparison.Ordinal) ||
				s.StartsWith("*** Update File:", StringComparison.Ordinal) ||
				s.StartsWith("*** Delete File:", StringComparison.Ordinal) ||
				s.StartsWith("*** Add File:", StringComparison.Ordinal) ||
				s.StartsWith("*** End of File", StringComparison.Ordinal))
			{
				break;
			}

			if (s == "***")
				break;

			if (s.StartsWith("***", StringComparison.Ordinal))
				throw new DiffError($"Invalid Line: {s}");

			index++;

			var lastMode = mode;
			if (s.Length == 0)
				s = " ";

			mode = s[0] switch {
				'+' => "add",
				'-' => "delete",
				' ' => "keep",
				_ => throw new DiffError($"Invalid Line: {s}")
			};

			var content = s.Substring(1);

			if (mode == "keep" && lastMode != mode)
			{
				if (insLines.Count > 0 || delLines.Count > 0)
				{
					chunks.Add(new Chunk(
						old.Count - delLines.Count,
						new List<string>(delLines),
						new List<string>(insLines)));
				}

				delLines.Clear();
				insLines.Clear();
			}

			if (mode == "delete")
			{
				delLines.Add(content);
				old.Add(content);
			}
			else if (mode == "add")
			{
				insLines.Add(content);
			}
			else
			{
				old.Add(content);
			}
		}

		if (insLines.Count > 0 || delLines.Count > 0)
		{
			chunks.Add(new Chunk(
				old.Count - delLines.Count,
				new List<string>(delLines),
				new List<string>(insLines)));
		}

		if (index < lines.Count && lines[index] == "*** End of File")
			return new SectionResult(old, chunks, index + 1, true);

		if (index == origIndex)
			throw new DiffError("Nothing in this section");

		return new SectionResult(old, chunks, index, false);
	}

	public static string OpenFile(string path) =>
		File.ReadAllText(path, Encoding.UTF8);

	public static void WriteFile(string path, string content)
	{
		var target = new FileInfo(path);
		target.Directory?.Create();
		File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	public static void RemoveFile(string path)
	{
		if (File.Exists(path))
			File.Delete(path);
	}

	private static List<string> SplitLines(string text) =>
		text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

	private static string Norm(string line) => line.TrimEnd('\r');

	public static string ToolInstructions = """
		# apply_patch
		
		- Use `apply_patch` to edit files.
		- If completing the user's task requires writing or modifying files:
		  - Your code and final answer should follow these _CODING GUIDELINES_:
			- Avoid unneeded complexity in your solution. Minimize change size.
			- Keep changes consistent with the style of the existing files. Changes should be minimal and focused on the task.
		- Never implement function or paragraph stubs. Provide complete working implementations and text.

		§ `apply_patch` Specification

		Your patch language is a stripped‑down, file‑oriented diff format designed to be easy to parse and safe to apply. You can think of it as a high‑level envelope:

		*** Begin Patch
		[ one or more file sections ]
		*** End Patch

		Within that envelope, you get a sequence of file operations.
		You MUST include a header to specify the action you are taking.
		Each operation starts with one of three headers:

		*** Add File: <path> - create a new file. Every following line is a + line (the initial contents).
		*** Delete File: <path> - remove an existing file. Nothing follows.
		*** Update File: <path> - patch an existing file in place (optionally with a rename).

		May be immediately followed by *** Move to: <new path> if you want to rename the file.
		Then one or more “hunks”, each introduced by @@ (optionally followed by a hunk header).
		Within a hunk each line starts with:

		- for inserted text,

		* for removed text, or
		  space ( ) for context.
		  At the end of a truncated hunk you can emit *** End of File.

		Patch := Begin { FileOp } End
		Begin := "*** Begin Patch" NEWLINE
		End := "*** End Patch" NEWLINE
		FileOp := AddFile | DeleteFile | UpdateFile
		AddFile := "*** Add File: " path NEWLINE { "+" line NEWLINE }
		DeleteFile := "*** Delete File: " path NEWLINE
		UpdateFile := "*** Update File: " path NEWLINE [ MoveTo ] { Hunk }
		MoveTo := "*** Move to: " newPath NEWLINE
		Hunk := "@@" [ header ] NEWLINE { HunkLine } [ "*** End of File" NEWLINE ]
		HunkLine := (" " | "-" | "+") text NEWLINE

		A full patch can combine several operations:

		*** Begin Patch
		*** Add File: hello.txt
		+Hello world
		*** Update File: src/app.py
		*** Move to: src/main.py
		@@ def greet():
		-print("Hi")
		+print("Hello, world!")
		*** Delete File: obsolete.txt
		*** End Patch

		It is important to remember:

		- You must include a header with your intended action (Add/Delete/Update)
		- You must prefix new lines with `+` even when creating a new file
		""";
}
