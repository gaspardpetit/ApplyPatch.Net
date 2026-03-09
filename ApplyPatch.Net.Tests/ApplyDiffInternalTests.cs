namespace ApplyPatch.Tests;

public class ApplyDiffInternalTests
{
	[Fact]
	public void NormalizeDiffLines_DropsTrailingBlank()
	{
		var result = DiffApplier.NormalizeDiffLines("a\nb\n");

		Assert.Equal(new[] { "a", "b" }, result);
	}

	[Fact]
	public void IsDone_TrueWhenIndexOutOfRange()
	{
		var state = new DiffApplier.ParserState(new List<string> { "line" }) {
			Index = 1
		};

		var done = DiffApplier.IsDone(state, Array.Empty<string>());

		Assert.True(done);
	}

	[Fact]
	public void ReadStr_ReturnsEmptyWhenMissingPrefix()
	{
		var state = new DiffApplier.ParserState(new List<string> { "value" }) {
			Index = 0
		};

		var result = DiffApplier.ReadStr(state, "nomatch");

		Assert.Equal(string.Empty, result);
		Assert.Equal(0, state.Index);
	}

	[Fact]
	public void ReadSection_ReturnsEofFlag()
	{
		var result = DiffApplier.ReadSection(
			new List<string> { "*** End of File" },
			startIndex: 0);

		Assert.True(result.Eof);
	}

	[Fact]
	public void ReadSection_RaisesOnInvalidMarker()
	{
		Assert.Throws<InvalidOperationException>(() =>
			DiffApplier.ReadSection(
				new List<string> { "*** Bad Marker" },
				startIndex: 0));
	}

	[Fact]
	public void ReadSection_RaisesWhenEmptySegment()
	{
		Assert.Throws<InvalidOperationException>(() =>
			DiffApplier.ReadSection(
				new List<string>(),
				startIndex: 0));
	}

	[Fact]
	public void FindContext_EofFallbacks()
	{
		var match = DiffApplier.FindContext(
			new List<string> { "one" },
			new List<string> { "missing" },
			start: 0,
			eof: true);

		Assert.Equal(-1, match.NewIndex);
		Assert.True(match.Fuzz >= 10000);
	}

	[Fact]
	public void FindContextCore_StrippedMatches()
	{
		var match = DiffApplier.FindContextCore(
			new List<string> { " line " },
			new List<string> { "line" },
			start: 0);

		Assert.Equal(0, match.NewIndex);
		Assert.Equal(100, match.Fuzz);
	}

	[Fact]
	public void ApplyChunks_RejectsBadChunks()
	{
		// Chunk(orig_index=10, del_lines=[], ins_lines=[])
		Assert.Throws<InvalidOperationException>(() =>
			DiffApplier.ApplyChunks(
				"abc",
				new List<DiffApplier.Chunk>
				{
					new DiffApplier.Chunk(
						OrigIndex: 10,
						DelLines: new List<string>(),
						InsLines: new List<string>())
				}));

		// overlapping chunks
		Assert.Throws<InvalidOperationException>(() =>
			DiffApplier.ApplyChunks(
				"abc",
				new List<DiffApplier.Chunk>
				{
					new DiffApplier.Chunk(
						OrigIndex: 0,
						DelLines: new List<string> { "a" },
						InsLines: new List<string>()),
					new DiffApplier.Chunk(
						OrigIndex: 0,
						DelLines: new List<string> { "b" },
						InsLines: new List<string>())
				}));
	}
}
