[![Build & Tests](https://github.com/gaspardpetit/ApplyPatch.Net/actions/workflows/run-tests.yml/badge.svg)](https://github.com/gaspardpetit/ApplyPatch.Net/actions/workflows/run-tests.yml)
![NuGet Version](https://img.shields.io/nuget/v/ApplyPatch)

# ApplyPatch.Net

`ApplyPatch.Net` is a .NET library for applying the patch formats used by OpenAI tooling:

- `DiffApplier` handles the original V4A-style contextual diff format.
- `OSSDiffApplier` handles the `*** Begin Patch` / `*** End Patch` format used by GPT-OSS style models.

The library applies hunks by matching context in file contents rather than trusting line numbers, with the same kind of fuzzy matching those tools expect.

## Installation

```powershell
dotnet add package ApplyPatch
```

## APIs

### `DiffApplier`

Use this when you already have the original file content in memory and want to apply a contextual diff hunk directly.

```csharp
using ApplyPatch;

string original = "line1\nline2\nline3\n";
string diff = string.Join("\n", new[]
{
    "@@ line1",
    "-line2",
    "+updated",
    " line3"
});

string result = DiffApplier.ApplyDiff(original, diff);
```

For create-only diffs, use `ApplyDiffMode.Create`:

```csharp
string diff = string.Join("\n", new[]
{
    "+hello",
    "+world"
});

string result = DiffApplier.ApplyDiff(
    input: "",
    diff: diff,
    mode: DiffApplier.ApplyDiffMode.Create);
```

### `OSSDiffApplier`

Use this when the model returns a full file-oriented patch envelope.

Supported operations:

- `*** Update File: <path>`
- `*** Move to: <new path>`
- `*** Delete File: <path>`
- `*** Add File: <path>`

Example:

```csharp
using ApplyPatch;

string patch = string.Join("\n", new[]
{
    "*** Begin Patch",
    "*** Update File: src/old.txt",
    "*** Move to: src/new.txt",
    "@@",
    " alpha",
    "-beta",
    "+beta2",
    " gamma",
    "*** Delete File: src/remove.txt",
    "*** Add File: src/add.txt",
    "+first",
    "+second",
    "*** End Patch"
});

var files = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["src/old.txt"] = "alpha\nbeta\ngamma",
    ["src/remove.txt"] = "remove me"
};

var writes = new Dictionary<string, string>(StringComparer.Ordinal);
var removes = new List<string>();

OSSDiffApplier.ApplyPatch(
    patch,
    openFn: path => files[path],
    writeFn: (path, content) => writes[path] = content,
    removeFn: path => removes.Add(path));
```

`OSSDiffApplier.ApplyPatch(...)` uses filesystem delegates so you can:

- apply directly against disk with the built-in file helpers
- test against an in-memory file map
- intercept writes, deletes, and moves in your own storage layer

Useful helpers:

- `OSSDiffApplier.IdentifyFilesNeeded(text)` returns files that must already exist because they are updated or deleted
- `OSSDiffApplier.IdentifyFilesAdded(text)` returns files created by the patch
- `OSSDiffApplier.TextToPatch(text, orig)` parses the patch and reports a `Fuzz` score for non-exact context matches
- `OSSDiffApplier.PatchToCommit(...)` and `OSSDiffApplier.ApplyCommit(...)` let you split parsing from execution

## Patch Semantics

Both appliers are context-based:

- exact context matches are preferred
- trailing-whitespace matches are allowed with low fuzz
- trimmed matches are allowed with higher fuzz
- invalid structure, missing files, duplicate operations, overlapping chunks, and unmatched context throw exceptions

`OSSDiffApplier` also supports `*** End of File` in truncated hunks and removes the original file when a patch moves it to a new path.

## When To Use Which

Use `DiffApplier` when your application already knows which file it is patching and only needs to transform one text blob.

Use `OSSDiffApplier` when you want to consume the higher-level patch format emitted by GPT-OSS style models or similar agents that describe adds, deletes, updates, and moves across multiple files.

## Attribution

This library ports the patch application behavior used by OpenAI tooling into C#. The `DiffApplier` implementation is based on the Python logic from `openai-agents-python`, and `OSSDiffApplier` implements the GPT-OSS style `apply_patch` envelope used by newer model workflows.

## License

MIT
