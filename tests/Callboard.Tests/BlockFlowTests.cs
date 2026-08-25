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

    // §8a remediation: 'in-review' carries three edges on the raw table but only one is
    // generically invocable — 'changes-requested'; 'approve' and 'fix-before-land' are each their
    // own dedicated door.
    [Fact]
    public void InReview_HasExactlyOneGenericallyInvocableTransition_ChangesRequested()
    {
        var only = Assert.Single(BlockFlowTransitions.GenericallyInvocableFrom(BlockFlowState.InReview));

        Assert.Equal("changes-requested", only.Name);
        Assert.Same(BlockFlowState.Briefed, only.To);
    }

    // §8a remediation: 'approved' carries two edges on the raw table — 'land' and
    // 'finding-recurred' — both legal, both one-door (reached only through
    // CardStore.CloseSectionUnderExistingLock and CardStore.RecordSectionVerdictUnderExistingLock
    // respectively, never through 'block transition', refused at parse either way).
    // GenericallyInvocableFrom(approved) is the query that reports neither — see the sibling test
    // below.
    [Fact]
    public void Approved_HasExactlyTwoAvailableTransitions_LandAndFindingRecurred()
    {
        var available = BlockFlowTransitions.AvailableFrom(BlockFlowState.Approved);

        Assert.Equal(2, available.Count);

        var land = Assert.Single(available, t => t.Name == "land");
        Assert.Same(BlockFlowState.Landed, land.To);

        var findingRecurred = Assert.Single(available, t => t.Name == "finding-recurred");
        Assert.Same(BlockFlowState.Briefed, findingRecurred.To);
    }

    // §8a remediation: neither of 'approved's two edges is generically invocable — both are
    // one-door edges, each reached only through its own dedicated write.
    [Fact]
    public void Approved_HasNoGenericallyInvocableTransitions()
    {
        Assert.Empty(BlockFlowTransitions.GenericallyInvocableFrom(BlockFlowState.Approved));
    }

    // §8a block A: 'land' still exists as a value — the thing CloseSectionUnderExistingLock
    // applies directly — it is only the GenericallyInvocableFrom(approved) invocation surface that
    // never lists it (§8a remediation: AvailableFrom(approved) lists it now, alongside
    // 'finding-recurred' — see the sibling test above).
    [Fact]
    public void LandTransition_StillExists_ApprovedToLanded_ButIsNotGenericallyInvocable()
    {
        var land = BlockFlowTransitions.LandTransition;

        Assert.Equal("land", land.Name);
        Assert.Same(BlockFlowState.Approved, land.From);
        Assert.Same(BlockFlowState.Landed, land.To);
        Assert.DoesNotContain(BlockFlowTransitions.GenericallyInvocableFrom(BlockFlowState.Approved), t => t.Name == "land");
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

    // §8a section review, carried to §9 block A: "nothing asserts GenericallyInvocableFrom(s) ⊆
    // AvailableFrom(s), or transition.From == s, across the seven states" — the tests above pin
    // each arm by hand, which is a third hand-written restatement of the table rather than an
    // invariant over it. Omission (a new edge added to AvailableFrom but not
    // GenericallyInvocableFrom) fails closed on its own; this loop fences the direction that does
    // not — commission, a one-door edge landing under the wrong state's arm — which is what bricked
    // cards the first time (§8a).
    private static readonly BlockFlowState[] AllStates =
    [
        BlockFlowState.Drafting, BlockFlowState.Briefed, BlockFlowState.Building,
        BlockFlowState.InReview, BlockFlowState.Approved, BlockFlowState.Landed, BlockFlowState.Closed,
    ];

    [Fact]
    public void GenericallyInvocableFrom_IsASubsetOfAvailableFrom_ForEveryState()
    {
        foreach (var state in AllStates)
        {
            var available = BlockFlowTransitions.AvailableFrom(state);
            var invocable = BlockFlowTransitions.GenericallyInvocableFrom(state);

            foreach (var transition in invocable)
            {
                Assert.Contains(available, candidate => ReferenceEquals(candidate, transition));
            }
        }
    }

    [Fact]
    public void EveryTransitionEitherQueryReturnsForAState_HasFromEqualToThatState()
    {
        foreach (var state in AllStates)
        {
            foreach (var transition in BlockFlowTransitions.AvailableFrom(state))
            {
                Assert.Same(state, transition.From);
            }

            foreach (var transition in BlockFlowTransitions.GenericallyInvocableFrom(state))
            {
                Assert.Same(state, transition.From);
            }
        }
    }
}
