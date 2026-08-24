using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.1 — the block flow states as a closed union with an exhaustive, total transition table
/// (work-lifecycle: "Block cards move through a defined flow"). No CLI verb and no card write
/// path exist yet (block A's scope); these tests exercise <see cref="BlockFlowState"/> and
/// <see cref="BlockFlowTransitions"/> directly.
/// </summary>
public sealed class BlockFlowTests
{
    [Fact]
    public void Drafting_HasExactlyOneAvailableTransition_ToBriefed()
    {
        var available = BlockFlowTransitions.AvailableFrom(BlockFlowState.Drafting);

        var only = Assert.Single(available);
        Assert.Equal("brief", only.Name);
        Assert.Same(BlockFlowState.Drafting, only.From);
        Assert.Same(BlockFlowState.Briefed, only.To);
    }

    [Fact]
    public void Briefed_HasExactlyOneAvailableTransition_ToBuilding()
    {
        var only = Assert.Single(BlockFlowTransitions.AvailableFrom(BlockFlowState.Briefed));

        Assert.Equal("claim", only.Name);
        Assert.Same(BlockFlowState.Building, only.To);
    }

    [Fact]
    public void Building_HasExactlyOneAvailableTransition_ToInReview()
    {
        var only = Assert.Single(BlockFlowTransitions.AvailableFrom(BlockFlowState.Building));

        Assert.Equal("submit-for-review", only.Name);
        Assert.Same(BlockFlowState.InReview, only.To);
    }

    [Fact]
    public void InReview_HasThreeAvailableTransitions_ApproveChangesRequestedAndFixBeforeLand()
    {
        var available = BlockFlowTransitions.AvailableFrom(BlockFlowState.InReview);

        Assert.Equal(3, available.Count);

        var approve = Assert.Single(available, t => t.Name == "approve");
        Assert.Same(BlockFlowState.Approved, approve.To);

        var changesRequested = Assert.Single(available, t => t.Name == "changes-requested");
        Assert.Same(BlockFlowState.Briefed, changesRequested.To);

        var fixBeforeLand = Assert.Single(available, t => t.Name == "fix-before-land");
        Assert.Same(BlockFlowState.Briefed, fixBeforeLand.To);
    }

    [Fact]
    public void Approved_HasTwoAvailableTransitions_LandAndAmendmentRequested()
    {
        var available = BlockFlowTransitions.AvailableFrom(BlockFlowState.Approved);

        Assert.Equal(2, available.Count);

        var land = Assert.Single(available, t => t.Name == "land");
        Assert.Same(BlockFlowState.Landed, land.To);

        var amendmentRequested = Assert.Single(available, t => t.Name == "amendment-requested");
        Assert.Same(BlockFlowState.Briefed, amendmentRequested.To);
    }

    [Fact]
    public void Landed_HasExactlyOneAvailableTransition_ToClosed()
    {
        var only = Assert.Single(BlockFlowTransitions.AvailableFrom(BlockFlowState.Landed));

        Assert.Equal("close", only.Name);
        Assert.Same(BlockFlowState.Closed, only.To);
    }

    [Fact]
    public void Closed_HasNoAvailableTransitions_BecauseItIsTerminal()
    {
        Assert.Empty(BlockFlowTransitions.AvailableFrom(BlockFlowState.Closed));
    }

    [Fact]
    public void DraftingToApproved_IsNotAmongDraftingsAvailableTransitions()
    {
        // The illegal-transition scenario the spec names directly: a role attempting to skip
        // straight from drafting to approved must find that edge absent from the table, not
        // merely find some other edge present.
        var available = BlockFlowTransitions.AvailableFrom(BlockFlowState.Drafting);

        Assert.DoesNotContain(available, t => t.To == BlockFlowState.Approved);
    }

    [Theory]
    [InlineData("drafting")]
    [InlineData("briefed")]
    [InlineData("building")]
    [InlineData("in-review")]
    [InlineData("approved")]
    [InlineData("landed")]
    [InlineData("closed")]
    public void WireFormat_RoundTripsEveryState(string wireValue)
    {
        var found = BlockFlowStateWireFormat.TryParse(wireValue, out var state);

        Assert.True(found);
        Assert.Equal(wireValue, state.ToWireString());
    }

    [Fact]
    public void WireFormat_UnrecognisedValue_FailsToParse()
    {
        var found = BlockFlowStateWireFormat.TryParse("blocked", out _);

        Assert.False(found);
    }
}
