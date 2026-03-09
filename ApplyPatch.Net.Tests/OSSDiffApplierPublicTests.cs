namespace ApplyPatch.Tests;

public class OSSDiffApplierPublicTests
{
	[Fact]
	public void ApplyPatch_AppliesAddUpdateDeleteAndMove()
	{
		var patch = string.Join("\n", new[]
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

		var result = OSSDiffApplier.ApplyPatch(
			patch,
			openFn: path => files[path],
			writeFn: (path, content) => writes[path] = content,
			removeFn: path => removes.Add(path));

		Assert.Equal("Done!", result);
		Assert.Equal("alpha\nbeta2\ngamma", writes["src/new.txt"]);
		Assert.Equal("first\nsecond", writes["src/add.txt"]);
		Assert.Contains("src/old.txt", removes);
		Assert.Contains("src/remove.txt", removes);
	}

	[Fact]
	public void ApplyPatch_ThrowsWhenPatchHeaderIsMissing()
	{
		var patch = "*** Update File: file.txt\n*** End Patch";

		Assert.Throws<OSSDiffApplier.DiffError>(() =>
			OSSDiffApplier.ApplyPatch(patch));
	}

	[Fact]
	public void TextToPatch_ThrowsWhenUpdateFileIsMissingFromOriginalFiles()
	{
		var patch = string.Join("\n", new[]
		{
			"*** Begin Patch",
			"*** Update File: missing.txt",
			"@@",
			"+value",
			"*** End Patch"
		});

		Assert.Throws<OSSDiffApplier.DiffError>(() =>
			OSSDiffApplier.TextToPatch(
				patch,
				new Dictionary<string, string>(StringComparer.Ordinal)));
	}
}
