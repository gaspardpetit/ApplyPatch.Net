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
	public void TryReadAnchor_ReturnsNullWhenMissingPrefix()
	{
		var state = new DiffApplier.ParserState(new List<string> { "value" }) {
			Index = 0
		};

		var result = DiffApplier.TryReadAnchor(state);

		Assert.Null(result);
		Assert.Equal(0, state.Index);
	}

	[Fact]
	public void TryReadAnchor_ParsesPrefixAnchors()
	{
		var startsWithState = new DiffApplier.ParserState(new List<string> { "@@^prefix" });
		var endsWithState = new DiffApplier.ParserState(new List<string> { "@@$suffix" });

		var startsWith = DiffApplier.TryReadAnchor(startsWithState);
		var endsWith = DiffApplier.TryReadAnchor(endsWithState);

		Assert.NotNull(startsWith);
		Assert.Equal(DiffApplier.AnchorMatchKind.StartsWith, startsWith!.Kind);
		Assert.Equal("prefix", startsWith.Text);
		Assert.NotNull(endsWith);
		Assert.Equal(DiffApplier.AnchorMatchKind.EndsWith, endsWith!.Kind);
		Assert.Equal("suffix", endsWith.Text);
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
			new List<DiffApplier.ContextLine>
			{
				new(DiffApplier.ContextMatchKind.Exact, "missing")
			},
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
			new List<DiffApplier.ContextLine>
			{
				new(DiffApplier.ContextMatchKind.Exact, "line")
			},
			start: 0);

		Assert.Equal(0, match.NewIndex);
		Assert.Equal(100, match.Fuzz);
	}

	[Fact]
	public void ParseContextLine_RecognizesPrefixMarkers()
	{
		var startsWith = DiffApplier.ParseContextLine('^', "prefix");
		var endsWith = DiffApplier.ParseContextLine('$', "suffix");

		Assert.Equal(DiffApplier.ContextMatchKind.StartsWith, startsWith.Kind);
		Assert.Equal("prefix", startsWith.Text);
		Assert.Equal(DiffApplier.ContextMatchKind.EndsWith, endsWith.Kind);
		Assert.Equal("suffix", endsWith.Text);
	}

	[Fact]
	public void ApplyChunks_RejectsBadChunks()
	{
		Assert.Throws<InvalidOperationException>(() =>
			DiffApplier.ApplyChunks(
				"abc",
				new List<DiffApplier.Chunk>
				{
					new DiffApplier.Chunk(
						OrigIndex: 10,
						DelLines: new List<string>(),
						InsLines: new List<string>())
				},
				"\n"));
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
				},
				"\n"));
	}
}
