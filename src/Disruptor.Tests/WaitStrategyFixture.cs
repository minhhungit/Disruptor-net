using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Disruptor.Tests;

[TestFixture]
public abstract class WaitStrategyFixture<T>
    where T : IWaitStrategy
{
    protected abstract T CreateWaitStrategy();

    protected TimeSpan DefaultAssertTimeout { get; } = TimeSpan.FromSeconds(5);
    protected Sequence Cursor { get; } = new();
    protected CancellationTokenSource CancellationTokenSource { get; } = new();
    protected CancellationToken CancellationToken => CancellationTokenSource.Token;

    [TestCase(10, 10, 10)]
    [TestCase(12, 10, 10)]
    [TestCase(15, 12, 12)]
    public void ShouldWaitForAvailableSequence(long cursorValue, long dependentSequenceValue, long expectedResult)
    {
        // Arrange
        var waitStrategy = CreateWaitStrategy();
        Cursor.SetValue(cursorValue);

        var dependentSequence = new Sequence(dependentSequenceValue);

        // Act
        var waitResult = waitStrategy.WaitFor(10, Cursor, dependentSequence, CancellationToken);

        // Assert
        Assert.That(waitResult, Is.EqualTo(new SequenceWaitResult(expectedResult)));
    }

    [TestCase(10, 10, 10)]
    [TestCase(12, 10, 10)]
    [TestCase(15, 12, 12)]
    public void ShouldWaitAndReturnOnceSequenceIsAvailable(long sequence, long dependentSequenceValue, long expectedResult)
    {
        // Arrange
        var waitStrategy = CreateWaitStrategy();
        var dependentSequence = new Sequence();
        var waitResult = new TaskCompletionSource<SequenceWaitResult>();

        var waitTask = Task.Run(() => waitResult.SetResult(waitStrategy.WaitFor(10, Cursor, dependentSequence, CancellationToken)));

        // Ensure waiting tasks are blocked
        AssertIsNotCompleted(waitTask);

        // Act
        Cursor.SetValue(sequence);
        waitStrategy.SignalAllWhenBlocking();
        dependentSequence.SetValue(dependentSequenceValue);

        // Assert
        AssertHasResult(waitResult.Task, new SequenceWaitResult(expectedResult));
        AssertIsCompleted(waitTask);
    }

    [Test]
    public void ShouldWaitFromMultipleThreads()
    {
        // Arrange
        var waitStrategy = CreateWaitStrategy();
        var waitResult1 = new TaskCompletionSource<SequenceWaitResult>();
        var waitResult2 = new TaskCompletionSource<SequenceWaitResult>();

        var dependentSequence1 = Cursor;
        var dependentSequence2 = new Sequence();

        var waitTask1 = Task.Run(() =>
        {
            waitResult1.SetResult(waitStrategy.WaitFor(10, Cursor, dependentSequence1, CancellationToken));
            Thread.Sleep(1);
            dependentSequence2.SetValue(10);
        });

        var waitTask2 = Task.Run(() => waitResult2.SetResult(waitStrategy.WaitFor(10, Cursor, dependentSequence2, CancellationToken)));

        // Ensure waiting tasks are blocked
        AssertIsNotCompleted(waitResult1.Task);
        AssertIsNotCompleted(waitResult2.Task);

        // Act
        Cursor.SetValue(10);
        waitStrategy.SignalAllWhenBlocking();

        // Assert
        AssertHasResult(waitResult1.Task, new SequenceWaitResult(10));
        AssertHasResult(waitResult2.Task, new SequenceWaitResult(10));
        AssertIsCompleted(waitTask1);
        AssertIsCompleted(waitTask2);
    }

    [Test]
    public void ShouldWaitFromMultipleThreadsInOrder()
    {
        // Arrange
        var waitStrategy = CreateWaitStrategy();
        var waitResult1 = new TaskCompletionSource<SequenceWaitResult>();
        var waitResult2 = new TaskCompletionSource<SequenceWaitResult>();
        var task1Signal = new ManualResetEvent(false);

        var dependentSequence1 = Cursor;
        var dependentSequence2 = new Sequence();

        var waitTask1 = Task.Run(() =>
        {
            waitResult1.SetResult(waitStrategy.WaitFor(10, Cursor, dependentSequence1, CancellationToken));
            task1Signal.WaitOne(DefaultAssertTimeout);
            dependentSequence2.SetValue(10);
        });

        var waitTask2 = Task.Run(() => waitResult2.SetResult(waitStrategy.WaitFor(10, Cursor, dependentSequence2, CancellationToken)));

        // Ensure waiting tasks are blocked
        AssertIsNotCompleted(waitResult1.Task);
        AssertIsNotCompleted(waitResult2.Task);

        // Act 1: set cursor
        Cursor.SetValue(10);
        waitStrategy.SignalAllWhenBlocking();

        // Assert
        AssertHasResult(waitResult1.Task, new SequenceWaitResult(10));
        AssertIsNotCompleted(waitResult2.Task);

        // Act 2: unblock task1
        task1Signal.Set();

        // Assert
        AssertHasResult(waitResult2.Task, new SequenceWaitResult(10));
        AssertIsCompleted(waitTask1);
        AssertIsCompleted(waitTask2);
    }

    [Test]
    public void ShouldUnblockAfterCancellation()
    {
        // Arrange
        var waitStrategy = CreateWaitStrategy();
        var dependentSequence = new Sequence();
        var waitResult = new TaskCompletionSource<Exception>();

        var waitTask = Task.Run(() =>
        {
            try
            {
                waitStrategy.WaitFor(10, Cursor, dependentSequence, CancellationToken);
            }
            catch (Exception e)
            {
                waitResult.SetResult(e);
            }
        });

        // Ensure waiting tasks are blocked
        AssertIsNotCompleted(waitTask);

        // Act
        CancellationTokenSource.Cancel();
        waitStrategy.SignalAllWhenBlocking();

        // Assert
        AssertIsCompleted(waitResult.Task);
        Assert.That(waitResult.Task.Result, Is.InstanceOf<OperationCanceledException>());
        AssertIsCompleted(waitTask);
    }

    protected static void AssertIsNotCompleted(Task task)
    {
        Assert.That(task.Wait(2), Is.False);
    }

    protected void AssertIsCompleted(Task task)
    {
        Assert.That(task.Wait(DefaultAssertTimeout), Is.True);
    }

    protected void AssertHasResult<TResult>(Task<TResult> task, TResult expectedValue)
    {
        AssertIsCompleted(task);
        Assert.That(task.Result, Is.EqualTo(expectedValue));
    }
}
