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

    // §8a block B: 'approved' has exactly one caller-facing edge — 'finding-recurred', reached
    // only through CardStore.RecordSectionVerdictUnderExistingLock, never through 'block
    // transition' (refused at parse). 'land' is still not individually invocable (see
    // BlockFlowTransitions.LandTransition for where it lives), and 'amendment-requested' no longer
    // exists as an edge on this table at all.
    [Fact]
    public void Approved_HasExactlyOneAvailableTransition_FindingRecurred()
    {
        var available = BlockFlowTransitions.AvailableFrom(BlockFlowState.Approved);

        var findingRecurred = Assert.Single(available);
        Assert.Equal("finding-recurred", findingRecurred.Name);
        Assert.Same(BlockFlowState.Briefed, findingRecurred.To);
    }

    // §8a block A: 'land' still exists as a value — the thing CloseSectionUnderExistingLock
    // applies directly — it is only the AvailableFrom(approved) invocation surface that lost it.
    [Fact]
    public void LandTransition_StillExists_ApprovedToLanded_ButIsNotOnAvailableFrom()
    {
        var land = BlockFlowTransitions.LandTransition;

        Assert.Equal("land", land.Name);
        Assert.Same(BlockFlowState.Approved, land.From);
        Assert.Same(BlockFlowState.Landed, land.To);
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
