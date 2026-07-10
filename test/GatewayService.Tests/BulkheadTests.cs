public class BulkheadTests
{
    [Fact]
    public async Task ExecuteAsync_UnderCapacity_RunsTheAction()
    {
        var bulkhead = new Bulkhead(maxConcurrentCalls: 2);

        var result = await bulkhead.ExecuteAsync(() => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ExecuteAsync_AtCapacity_RejectsTheNextCallInstead()
    {
        // Two in-flight calls saturate a capacity-2 bulkhead; a third must be rejected
        // immediately rather than queuing behind them — that's the whole point of a
        // bulkhead over a plain semaphore-wait.
        var bulkhead = new Bulkhead(maxConcurrentCalls: 2);
        var releaseFirstTwo = new TaskCompletionSource();

        var first = bulkhead.ExecuteAsync(() => releaseFirstTwo.Task);
        var second = bulkhead.ExecuteAsync(() => releaseFirstTwo.Task);

        await Assert.ThrowsAsync<BulkheadRejectedException>(() => bulkhead.ExecuteAsync(() => Task.FromResult(1)));

        releaseFirstTwo.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task ExecuteAsync_ReleasesOnSuccess_SoASubsequentCallCanRunAgain()
    {
        var bulkhead = new Bulkhead(maxConcurrentCalls: 1);

        await bulkhead.ExecuteAsync(() => Task.FromResult(1));
        var second = await bulkhead.ExecuteAsync(() => Task.FromResult(2));

        Assert.Equal(2, second);
    }

    [Fact]
    public async Task ExecuteAsync_ReleasesOnException_SoAFailedCallDoesNotPermanentlyConsumeCapacity()
    {
        var bulkhead = new Bulkhead(maxConcurrentCalls: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bulkhead.ExecuteAsync(Task<int> () => throw new InvalidOperationException("downstream failed")));

        // If the failed call above had leaked its permit, this would throw
        // BulkheadRejectedException instead of returning normally.
        var result = await bulkhead.ExecuteAsync(() => Task.FromResult(7));
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task ExecuteAsync_VoidOverload_BehavesTheSameAsTheGenericOne()
    {
        var bulkhead = new Bulkhead(maxConcurrentCalls: 1);
        var ran = false;

        await bulkhead.ExecuteAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bulkhead(maxConcurrentCalls: 0));
    }
}
