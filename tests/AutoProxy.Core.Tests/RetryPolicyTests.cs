using AutoProxy.Core.Detection;
using Xunit;

namespace AutoProxy.Core.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void Threshold_below_one_is_clamped_to_one()
    {
        var policy = new RetryPolicy(0);

        Assert.Equal(1, policy.FailureThreshold);
    }

    [Fact]
    public void Escalates_only_after_threshold_failures()
    {
        var policy = new RetryPolicy(3);

        Assert.False(policy.RecordFailure());
        Assert.False(policy.RecordFailure());
        Assert.True(policy.RecordFailure());
        Assert.Equal(3, policy.ConsecutiveFailures);
    }

    [Fact]
    public void Success_resets_the_counter()
    {
        var policy = new RetryPolicy(2);

        Assert.False(policy.RecordFailure());
        policy.RecordSuccess();

        Assert.Equal(0, policy.ConsecutiveFailures);
        Assert.False(policy.RecordFailure());
        Assert.True(policy.RecordFailure());
    }

    [Fact]
    public void Reset_clears_the_counter()
    {
        var policy = new RetryPolicy(1);
        Assert.True(policy.RecordFailure());

        policy.Reset();

        Assert.Equal(0, policy.ConsecutiveFailures);
    }
}
