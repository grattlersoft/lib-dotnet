using System;
using FluentAssertions;
using Grattlersoft.VsExtension.Core.Diagnostics;
using Xunit;

namespace Grattlersoft.VsExtension.Core.Tests.Diagnostics;

/// <summary>
/// Tests fuer <see cref="Safe"/>. Der Logger <see cref="Log"/> wird in diesen
/// Tests nicht initialisiert — Safe darf trotzdem nicht abstuerzen, er loggt
/// dann nur ins Debug-Output.
/// </summary>
public class SafeTests
{
    [Fact]
    public void Run_Action_WithoutException_ExecutesBody()
    {
        var called = false;

        Safe.Run("test", () => called = true);

        called.Should().BeTrue();
    }

    [Fact]
    public void Run_Action_WithException_SwallowsAndDoesNotThrow()
    {
        var act = () => Safe.Run("test", () => throw new InvalidOperationException("boom"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Run_Action_NullAction_DoesNothing()
    {
        var act = () => Safe.Run("test", (Action)null!);

        act.Should().NotThrow();
    }

    [Fact]
    public void Run_Func_WithoutException_ReturnsResult()
    {
        var result = Safe.Run("test", fallback: -1, () => 42);

        result.Should().Be(42);
    }

    [Fact]
    public void Run_Func_WithException_ReturnsFallback()
    {
        var result = Safe.Run<int>("test", fallback: -1, () => throw new InvalidOperationException("boom"));

        result.Should().Be(-1);
    }

    [Fact]
    public void Run_Func_NullFunc_ReturnsFallback()
    {
        var result = Safe.Run("test", fallback: "default", (Func<string>)null!);

        result.Should().Be("default");
    }

    [Fact]
    public void Run_Func_Reference_NullFallbackAllowed()
    {
        var result = Safe.Run<string?>("test", fallback: null, () => throw new Exception("boom"));

        result.Should().BeNull();
    }
}
