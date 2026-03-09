[![Build & Tests](https://github.com/gaspardpetit/V4A.Net/actions/workflows/run-tests.yml/badge.svg)](https://github.com/gaspardpetit/V4A.Net/actions/workflows/run-tests.yml)
![NuGet Version](https://img.shields.io/nuget/v/V4A)

# ApplyPatch.Net

A .NET library for applying **V4A diffs** (the patch format used by OpenAI’s `apply-patch` tool) to text files.

V4A.Net implements the same patch application semantics used by OpenAI’s **apply-patch** workflow: patched text is generated with context hunks and applied via fuzzy context matching rather than strict line numbers. This library lets .NET applications interpret and apply those patches reliably. ([OpenAI Platform][1])

---

## Purpose

Modern AI tools like OpenAI’s **`apply-patch`** API return structured patch operations with diffs in a context-based format. Consumers of that API must parse and apply these patches locally. V4A.Net provides a faithful .NET implementation of this diff format’s application logic, matching the expectations of the `apply-patch` specification. ([OpenAI Platform](https://platform.openai.com/docs/guides/tools-apply-patch))

---

## Installation

Install via NuGet:

```powershell
dotnet add package V4A
```

---

## Usage

### Apply a patch to existing content

```csharp
using V4A;

string original = "line1\nline2\nline3\n";
string diff = string.Join("\n", new[] {
    "@@ line1",
    "-line2",
    "+updated",
    " line3"
});

// Apply contextual update
string result = DiffApplier.ApplyDiff(original, diff);

// result:
// line1
// updated
// line3
```

### Create new content from a diff

Diffs in *create mode* consist only of `+` prefixed lines:

```csharp
string diff = string.Join("\n", new[] {
    "+hello",
    "+world",
    "+"
});

string result = DiffApplier.ApplyDiff(
    input: "",
    diff: diff,
    mode: DiffApplier.ApplyDiffMode.Create);

// result:
// hello
// world
```

---

## How it works

V4A.Net supports two main diff formats:

* **Update mode**: Applies a set of context hunks with `@@` anchors, fuzzy matching, deletions (`-`) and additions (`+`).
* **Create mode**: Interprets all lines as new content (only `+` prefixes allowed).

When contextual diffs can’t be matched exactly, the library attempts fuzzy matches and throws detailed exceptions on failure.

---

## Attribution

This library is a C# port of the Python `apply_diff` implementation from the **openai-agents-python** repository. The original Python version was authored by **Kazuhiro Sera** and released under the MIT license. Source implementations from the Python SDK serve as reference for semantics and behavior. ([OpenAI Platform][1])

---

## License

MIT

