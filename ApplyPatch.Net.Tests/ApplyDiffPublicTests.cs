namespace ApplyPatch.Tests;

public class ApplyDiffPublicTests
{
	[Fact]
	public void ApplyDiff_WithFloatingHunk_AddsLines()
	{
		var diff = string.Join("\n", new[] { "@@", "+hello", "+world" });

		var result = DiffApplier.ApplyDiff(string.Empty, diff);

		Assert.Equal("hello\nworld\n", result);
	}

	[Fact]
	public void ApplyDiff_WithEmptyInput_AndCrlfDiff_PreservesCrlf()
	{
		var diff = string.Join("\r\n", new[] { "@@", "+hello", "+world" });

		var result = DiffApplier.ApplyDiff(string.Empty, diff);

		Assert.Equal("hello\r\nworld\r\n", result);
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
		var diff = string.Join("\n", new[] { "+hello", "+world", "+" });

		var result = DiffApplier.ApplyDiff(string.Empty, diff, DiffApplier.ApplyDiffMode.Create);

		Assert.Equal("hello\nworld\n", result);
	}

	[Fact]
	public void ApplyDiff_AppliesContextualReplacement()
	{
		var inputText = "line1\nline2\nline3\n";
		var diff = string.Join("\n", new[] { "@@ line1", "-line2", "+updated", " line3" });

		var result = DiffApplier.ApplyDiff(inputText, diff);

		Assert.Equal("line1\nupdated\nline3\n", result);
	}

	[Fact]
	public void ApplyDiff_RaisesOnContextMismatch()
	{
		var inputText = "one\ntwo\n";
		var diff = string.Join("\n", new[] { "@@ -1,2 +1,2 @@", " x", "-two", "+2" });

		Assert.Throws<InvalidOperationException>(() =>
			DiffApplier.ApplyDiff(inputText, diff));
	}

	[Fact]
	public void ApplyDiff_WithCrlfInput_AndLfDiff_PreservesCrlf()
	{
		var inputText = "line1\r\nline2\r\nline3\r\n";
		var diff = string.Join("\n", new[] { "@@ line1", "-line2", "+updated", " line3" });

		var result = DiffApplier.ApplyDiff(inputText, diff);

		Assert.Equal("line1\r\nupdated\r\nline3\r\n", result);
	}

	[Fact]
	public void ApplyDiff_WithLfInput_AndCrlfDiff_PreservesLf()
	{
		var inputText = "line1\nline2\nline3\n";
		var diff = string.Join("\r\n", new[] { "@@ line1", "-line2", "+updated", " line3" });

		var result = DiffApplier.ApplyDiff(inputText, diff);

		Assert.Equal("line1\nupdated\nline3\n", result);
	}

	[Fact]
	public void ApplyDiff_WithCrlfInput_AndCrlfDiff_PreservesCrlf()
	{
		var inputText = "line1\r\nline2\r\nline3\r\n";
		var diff = string.Join("\r\n", new[] { "@@ line1", "-line2", "+updated", " line3" });

		var result = DiffApplier.ApplyDiff(inputText, diff);

		Assert.Equal("line1\r\nupdated\r\nline3\r\n", result);
	}

	[Fact]
	public void ApplyDiff_CreateMode_PreservesCrlfNewlines()
	{
		var diff = string.Join("\r\n", new[] { "+hello", "+world", "+" });

		var result = DiffApplier.ApplyDiff(string.Empty, diff, DiffApplier.ApplyDiffMode.Create);

		Assert.Equal("hello\r\nworld\r\n", result);
	}

	[Fact]
	public void ApplyDiff_CreateMode_SupportsSetFileAlias()
	{
		var diff = string.Join("\n", new[] { "*** Set File: hello.txt", "+hello", "+world", "*** End Patch" });

		var result = DiffApplier.ApplyDiff(string.Empty, diff, DiffApplier.ApplyDiffMode.Create);

		Assert.Equal("hello\nworld", result);
	}

	[Fact]
	public void ApplyDiff_SupportsPrefixAndSuffixContextAnchors()
	{
		var inputText = "alpha123\nmiddle\nomega123\n";
		var diff = string.Join("\n", new[] { "@@", "^alpha", " middle", "$123", "+tail" });

		var result = DiffApplier.ApplyDiff(inputText, diff);

		Assert.Equal("alpha123\nmiddle\nomega123\ntail\n", result);
	}
}
