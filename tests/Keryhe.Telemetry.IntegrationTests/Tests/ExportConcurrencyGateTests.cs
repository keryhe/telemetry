using Keryhe.Telemetry.Core.Data.Read;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="ExportConcurrencyGate"/> (list-pages-server-side plan,
/// Phase 8, decision 17) — no database or HTTP dependency. Covers the "third concurrent export gets
/// 429" check from the plan's Verification item 12 at the gate-class level: this is the exact logic
/// the export controllers call before starting to stream, so exercising it directly is equivalent
/// to (and more reliable than) driving it through a real concurrent HTTP load without a
/// WebApplicationFactory harness in this test project — see <see cref="ExportTestsBase"/>'s own doc
/// comment for why that harness doesn't exist here.
/// </summary>
public class ExportConcurrencyGateTests
{
    private static ExportConcurrencyGate NewGate(int maxConcurrent)
        => new(Options.Create(new ExportOptions { MaxConcurrent = maxConcurrent }));

    [Fact]
    public void Allows_Up_To_MaxConcurrent_Slots()
    {
        var gate = NewGate(2);
        using var first = gate.TryEnter();
        using var second = gate.TryEnter();

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void Third_Concurrent_Slot_Is_Refused()
    {
        var gate = NewGate(2);
        using var first = gate.TryEnter();
        using var second = gate.TryEnter();
        using var third = gate.TryEnter();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third); // the controller maps this to 429
    }

    [Fact]
    public void Releasing_A_Slot_Frees_It_For_The_Next_Export()
    {
        var gate = NewGate(1);
        var first = gate.TryEnter();
        Assert.NotNull(first);

        Assert.Null(gate.TryEnter()); // no free slot yet

        first!.Dispose(); // export finished streaming

        using var second = gate.TryEnter();
        Assert.NotNull(second);
    }

    [Fact]
    public void Disposing_Twice_Does_Not_OverRelease_The_Semaphore()
    {
        var gate = NewGate(1);
        var first = gate.TryEnter();
        first!.Dispose();
        first.Dispose(); // must be a no-op, not a second release

        using var second = gate.TryEnter();
        Assert.NotNull(second);
        Assert.Null(gate.TryEnter()); // still only 1 slot total, despite the double-dispose above
    }

    [Fact]
    public void MaxConcurrent_Below_One_Is_Clamped_To_One_Slot()
    {
        var gate = NewGate(0);
        using var first = gate.TryEnter();
        Assert.NotNull(first);
        Assert.Null(gate.TryEnter());
    }
}
