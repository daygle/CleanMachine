using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Tests for the reference-counted cleaning-in-progress hub that drives
/// the tray spinner. CleaningActivity is process-wide static state, so both
/// tests leave the counter balanced and keep every assertion inside one method
/// (tests within a class run sequentially). The class shares the "Cleaning"
/// collection with <see cref="ManifestAndSafetyTests"/>, which exercises real
/// cleaning services and would otherwise run in parallel - those runs are
/// balanced, but their transient active windows would race the absolute-state
/// asserts here.</summary>
[Collection("Cleaning")]
public sealed class CleaningActivityTests
{
    [Fact]
    public void ChangedFiresOnlyWhenTheCountCrossesZero()
    {
        Assert.False(CleaningActivity.IsActive);

        var raised = new List<bool>();
        void OnChanged(bool active) => raised.Add(active);
        CleaningActivity.Changed += OnChanged;
        try
        {
            using (CleaningActivity.Begin())
            {
                Assert.True(CleaningActivity.IsActive);
                using (CleaningActivity.Begin())
                {
                    Assert.True(CleaningActivity.IsActive);
                }
                // Inner scope closed but the outer one still runs: stay active,
                // and do not report a transition.
                Assert.True(CleaningActivity.IsActive);
            }
            Assert.False(CleaningActivity.IsActive);
            Assert.Equal(new[] { true, false }, raised);
        }
        finally
        {
            CleaningActivity.Changed -= OnChanged;
        }
    }

    [Fact]
    public void ScopeDisposeIsIdempotent()
    {
        Assert.False(CleaningActivity.IsActive);

        var stopped = 0;
        void OnChanged(bool active)
        {
            if (!active) stopped++;
        }
        CleaningActivity.Changed += OnChanged;
        try
        {
            var scope = CleaningActivity.Begin();
            scope.Dispose();
            scope.Dispose(); // a double dispose must not underflow the count
            Assert.False(CleaningActivity.IsActive);
            Assert.Equal(1, stopped);

            // The counter is still healthy: a fresh Begin/End cycle works.
            using (CleaningActivity.Begin())
            {
                Assert.True(CleaningActivity.IsActive);
            }
            Assert.False(CleaningActivity.IsActive);
            Assert.Equal(2, stopped);
        }
        finally
        {
            CleaningActivity.Changed -= OnChanged;
        }
    }
}
