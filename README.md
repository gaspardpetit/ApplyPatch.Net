[![Build & Tests](https://github.com/gaspardpetit/ApplyPatch.Net/actions/workflows/run-tests.yml/badge.svg)](https://github.com/gaspardpetit/ApplyPatch.Net/actions/workflows/run-tests.yml)
![NuGet Version](https://img.shields.io/nuget/v/ApplyPatch)

# ApplyPatch.Net

`ApplyPatch.Net` is a .NET library for applying the patch formats used by OpenAI tooling:

- `DiffApplier` handles the original V4A-style contextual diff format.
- `Patch` parses `*** Begin Patch` / `*** End Patch` envelopes into file operations you can apply with `DiffApplier`.

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

### Extended Syntax Support

`DiffApplier` also supports a few compatibility extensions that are useful with models that emit approximate `apply_patch` syntax:

- `*** Set File: <path>` is accepted as an alias of `*** Add File: <path>` for create-only patches
- `@@^prefix` anchors a hunk at the next line whose text starts with `prefix`
- `@@$suffix` anchors a hunk at the next line whose text ends with `suffix`
- inside a `@@` hunk body, context lines starting with `^` or `$` match by prefix or suffix instead of exact equality

Examples:

```diff
*** Set File: notes.txt
+first
+second
```

```diff
@@^class Example
 -oldValue
 +newValue
```

```diff
@@
 ^public void
  {
 $// end marker
 +    Log();
```

### `Patch`

Use this when the model returns a full file-oriented patch envelope and you want to parse it into per-file operations.

Supported operations:

- `*** Update File: <path>`
- `*** Delete File: <path>`
- `*** Add File: <path>`
- `*** Set File: <path>`

Example:

```csharp
using ApplyPatch;

string patch = string.Join("\n", new[]
{
    "*** Begin Patch",
    "*** Update File: src/old.txt",
    "@@",
    " alpha",
    "-beta",
    "+beta2",
    " gamma",
    "*** Delete File: src/remove.txt",
    "*** Set File: src/add.txt",
    "+first",
    "+second",
    "*** End Patch"
});

var operations = Patch.ParseV4APatchOperations(patch).ToList();

string updated = Patch.ApplyDiff(
    "alpha\nbeta\ngamma",
    operations.Single(op => op.path == "src/old.txt"));
```

`Patch.ParseV4APatchOperations(...)` returns `ApplyPatchOperation` values with:

- `type` as `update_file`, `create_file`, or `delete_file`
- `path` as the target file path from the patch header
- `diff` as the body to pass to `DiffApplier` or inspect directly

## Patch Semantics

`DiffApplier` is context-based:

- exact context matches are preferred
- trailing-whitespace matches are allowed with low fuzz
- trimmed matches are allowed with higher fuzz
- invalid structure, missing files, duplicate operations, overlapping chunks, and unmatched context throw exceptions

`DiffApplier` also supports `*** End of File` in truncated hunks.

## When To Use Which

Use `DiffApplier` when your application already knows which file it is patching and only needs to transform one text blob.

Use `Patch` when you want to consume a multi-file `*** Begin Patch` envelope, split it into file operations, and then decide how to apply those operations in your own storage layer.

## Using As A Function Tool

One common integration pattern is to expose your own `apply_patch` function tool, accept a single JSON string field named `patch`, then apply the parsed operations against your local workspace.

Minimal function schema:

```json
{
  "type": "function",
  "function": {
    "name": "apply_patch",
    "description": "Apply a V4A patch to files in the local workspace.",
    "strict": true,
    "parameters": {
      "type": "object",
      "properties": {
        "patch": {
          "type": "string",
          "description": "V4A patch text. Supports *** Begin Patch, *** Update File, *** Add File, *** Set File, *** Delete File, and *** End Patch."
        }
      },
      "required": ["patch"],
      "additionalProperties": false
    }
  }
}
```

Example patch payload:

```diff
*** Begin Patch
*** Update File: src/foo.ts
@@
-old line
+new line
*** Set File: src/bar.ts
+hello
*** Delete File: src/old.ts
*** End Patch
```

Minimal C# workspace harness:

```csharp
using System.Text.Json;
using ApplyPatch;

static string ApplyPatchToWorkspace(string patch, string workspaceRoot)
{
    var operations = Patch.ParseV4APatchOperations(patch).ToList();
    var changedPaths = new List<string>();

    foreach (var operation in operations)
    {
        string fullPath = ValidatePath(workspaceRoot, operation.path);

        switch (operation.type)
        {
            case PatchOperationType.delete_file:
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
                changedPaths.Add(operation.path);
                break;

            case PatchOperationType.create_file:
            case PatchOperationType.update_file:
                string currentText = File.Exists(fullPath)
                    ? File.ReadAllText(fullPath)
                    : string.Empty;
                string updatedText = Patch.ApplyDiff(currentText, operation);
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(fullPath, updatedText);
                changedPaths.Add(operation.path);
                break;

            default:
                throw new InvalidOperationException($"Unsupported operation type: {operation.type}");
        }
    }

    return JsonSerializer.Serialize(new { ok = true, changed_paths = changedPaths });
}

static string ValidatePath(string workspaceRoot, string relativePath)
{
    string fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
    string fullRoot = Path.GetFullPath(workspaceRoot);

    if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Path escapes workspace: {relativePath}");

    return fullPath;
}
```

Typical server flow:

1. Parse the function arguments as JSON.
2. Extract the `patch` string.
3. Call `Patch.ParseV4APatchOperations(patch)`.
4. For each operation, validate the path before touching disk.
5. Use `Patch.ApplyDiff(currentText, operation)` for create/update operations.
6. Return a tool result describing the changed paths.

This keeps the patch transport separate from the patch engine. If you later support another transport, such as a built-in patch tool, you can usually reuse the same workspace application code.

## Attribution

This library ports patch application behavior used by OpenAI tooling into C#. The `DiffApplier` implementation is based on the Python logic from `openai-agents-python`.

## License

MIT
