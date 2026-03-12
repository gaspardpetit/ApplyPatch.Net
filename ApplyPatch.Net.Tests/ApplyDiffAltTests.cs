namespace ApplyPatch.Tests;

public class ApplyDiffAltTests
{
	[Fact]
	public void ApplyDiff_WithStartsWithHunkAnchor_TargetsMatchingSection()
	{
		var inputText = "alpha: one\nkeep\nbeta: two\nkeep\n";
		var diff = string.Join("\n", new[]
		{
			"@@^beta:",
			"-keep",
			"+updated"
		});

		var result = DiffApplier.ApplyDiff(inputText, diff);

		Assert.Equal("alpha: one\nkeep\nbeta: two\nupdated\n", result);
	}

	[Fact]
	public void ApplyDiff_WithEndsWithHunkAnchor_TargetsMatchingSection()
	{
		var inputText = "start marker\nkeep\nother marker\nkeep\n";
		var diff = string.Join("\n", new[]
		{
			"@@$ marker",
			"-keep",
			"+updated"
		});

		var result = DiffApplier.ApplyDiff(inputText, diff);

		Assert.Equal("start marker\nupdated\nother marker\nkeep\n", result);
	}

	[Fact]
	public void ApplyDiff_CreateMode_SetFileAliasMatchesAddFileBehavior()
	{
		var diff = string.Join("\n", new[]
		{
			"*** Set File: sample.txt",
			"+first",
			"+second",
			"*** End Patch"
		});

		var result = DiffApplier.ApplyDiff(string.Empty, diff, DiffApplier.ApplyDiffMode.Create);

		Assert.Equal("first\nsecond", result);
	}
}
