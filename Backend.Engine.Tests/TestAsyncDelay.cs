using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine.Debugging;

namespace Backend.Engine.Tests;

internal sealed class TestAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}
