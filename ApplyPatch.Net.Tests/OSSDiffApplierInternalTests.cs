namespace ApplyPatch.Tests;

public class OSSDiffApplierInternalTests
{
	[Fact]
	public void IdentifyFilesNeeded_ReturnsUpdateAndDeletePaths()
	{
		var patch = string.Join("\n", new[]
		{
			"*** Begin Patch",
			"*** Update File: src/update.txt",
			"@@",
			"+line",
			"*** Delete File: src/delete.txt",
			"*** Add File: src/add.txt",
			"+line",
			"*** End Patch"
		});

		var result = OSSDiffApplier.IdentifyFilesNeeded(patch);

		Assert.Equal(new[] { "src/update.txt", "src/delete.txt" }, result);
	}

	[Fact]
	public void IdentifyFilesAdded_ReturnsAddedPaths()
	{
		var patch = string.Join("\n", new[]
		{
			"*** Begin Patch",
			"*** Add File: src/one.txt",
			"+one",
			"*** Add File: src/two.txt",
			"+two",
			"*** End Patch"
		});

		var result = OSSDiffApplier.IdentifyFilesAdded(patch);

		Assert.Equal(new[] { "src/one.txt", "src/two.txt" }, result);
	}

	[Fact]
	public void TextToPatch_TracksWhitespaceFuzz()
	{
		var patch = string.Join("\n", new[]
		{
			"*** Begin Patch",
			"*** Update File: file.txt",
			"@@ alpha",
			"-beta",
			"+beta2",
			" gamma",
			"*** End Patch"
		});
		var orig = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["file.txt"] = " alpha \n beta \n gamma "
		};

		var result = OSSDiffApplier.TextToPatch(patch, orig);

		Assert.Single(result.Patch.Actions);
		Assert.Equal(101, result.Fuzz);
	}

	[Fact]
	public void PatchToCommit_CarriesMovePathAndUpdatedContent()
	{
		var patch = new OSSDiffApplier.Patch();
		patch.Actions["src/file.txt"] = new OSSDiffApplier.PatchAction
		{
			Type = OSSDiffApplier.ActionType.Update,
			MovePath = "dst/file.txt"
		};
		patch.Actions["src/file.txt"].Chunks.Add(new OSSDiffApplier.Chunk(
			OrigIndex: 1,
			DelLines: new List<string> { "b" },
			InsLines: new List<string> { "B" }));

		var orig = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["src/file.txt"] = "a\nb\nc"
		};

		var commit = OSSDiffApplier.PatchToCommit(patch, orig);

		var change = commit.Changes["src/file.txt"];
		Assert.Equal(OSSDiffApplier.ActionType.Update, change.Type);
		Assert.Equal("dst/file.txt", change.MovePath);
		Assert.Equal("a\nB\nc", change.NewContent);
	}

	[Fact]
	public void GetUpdatedFile_RejectsOverlappingChunks()
	{
		var action = new OSSDiffApplier.PatchAction
		{
			Type = OSSDiffApplier.ActionType.Update
		};
		action.Chunks.Add(new OSSDiffApplier.Chunk(
			OrigIndex: 0,
			DelLines: new List<string> { "a" },
			InsLines: new List<string> { "A" }));
		action.Chunks.Add(new OSSDiffApplier.Chunk(
			OrigIndex: 0,
			DelLines: new List<string> { "b" },
			InsLines: new List<string> { "B" }));

		Assert.Throws<OSSDiffApplier.DiffError>(() =>
			OSSDiffApplier.GetUpdatedFile("a\nb\nc", action, "file.txt"));
	}

	[Fact]
	public void ApplyCommit_ExecutesEachChangeType()
	{
		var commit = new OSSDiffApplier.Commit();
		commit.Changes["delete.txt"] = new OSSDiffApplier.FileChange(
			OSSDiffApplier.ActionType.Delete,
			OldContent: "old");
		commit.Changes["add.txt"] = new OSSDiffApplier.FileChange(
			OSSDiffApplier.ActionType.Add,
			NewContent: "new");
		commit.Changes["move.txt"] = new OSSDiffApplier.FileChange(
			OSSDiffApplier.ActionType.Update,
			OldContent: "before",
			NewContent: "after",
			MovePath: "moved.txt");

		var writes = new Dictionary<string, string>(StringComparer.Ordinal);
		var removes = new List<string>();

		OSSDiffApplier.ApplyCommit(
			commit,
			writeFn: (path, content) => writes[path] = content,
			removeFn: path => removes.Add(path));

		Assert.Equal("new", writes["add.txt"]);
		Assert.Equal("after", writes["moved.txt"]);
		Assert.Contains("delete.txt", removes);
		Assert.Contains("move.txt", removes);
	}
}
