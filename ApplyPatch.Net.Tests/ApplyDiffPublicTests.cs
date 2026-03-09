namespace ApplyPatch.Tests;

public class ApplyDiffPublicTests
{
	[Fact]
	public void ApplyDiff_WithFloatingHunk_AddsLines()
	{
		// diff = "\n".join(["@@", "+hello", "+world"])
		var diff = string.Join("\n", new[] { "@@", "+hello", "+world" });

		var result = DiffApplier.ApplyDiff(string.Empty, diff);

		Assert.Equal("hello\nworld\n", result);
	}

	[Fact]
	public void ApplyDiff_CreateMode_RequiresPlusPrefix()
	{
		var diff = "plain line";

		Assert.Throws<InvalidOperationException>(() =>
			DiffApplier.ApplyDiff(string.Empty, diff, DiffApplier.ApplyDiffMode.Create));
	}

	[Fact]
	public void ApplyDiff_CreateMode_PreservesTrailingNewline()
	{
		// diff = "\n".join(["+hello", "+world", "+"])
		var diff = string.Join("\n", new[] { "+hello", "+world", "+" });

		var result = DiffApplier.ApplyDiff(string.Empty, diff, DiffApplier.ApplyDiffMode.Create);

		Assert.Equal("hello\nworld\n", result);
	}

	[Fact]
	public void ApplyDiff_AppliesContextualReplacement()
	{
		var inputText = "line1\nline2\nline3\n";
		// diff = "\n".join(["@@ line1", "-line2", "+updated", " line3"])
		var diff = string.Join("\n", new[] { "@@ line1", "-line2", "+updated", " line3" });

		var result = DiffApplier.ApplyDiff(inputText, diff);

		Assert.Equal("line1\nupdated\nline3\n", result);
	}

	[Fact]
	public void ApplyDiff_RaisesOnContextMismatch()
	{
		var inputText = "one\ntwo\n";
		// diff = "\n".join(["@@ -1,2 +1,2 @@", " x", "-two", "+2"])
		var diff = string.Join("\n", new[] { "@@ -1,2 +1,2 @@", " x", "-two", "+2" });

		Assert.Throws<InvalidOperationException>(() =>
			DiffApplier.ApplyDiff(inputText, diff));
	}
}
