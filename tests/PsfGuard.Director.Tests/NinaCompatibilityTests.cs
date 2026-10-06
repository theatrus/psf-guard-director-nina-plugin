using Moq;
using NINA.Equipment.Interfaces.Mediator;
using PsfGuard.Director.Plugin.Acquisition;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaCompatibilityTests
{
    [Fact]
    public void NativeConstructorPrefersSafetyAwareSignature()
    {
        var safety = Mock.Of<ISafetyMonitorMediator>();
        var item = NinaCompatibility.Create<ModernAction>(safety, "argument");
        Assert.Same(safety, item.Safety);
        Assert.Equal("argument", item.Argument);
    }

    [Fact]
    public void NativeConstructorSupportsLegacySignature()
    {
        Assert.Equal("argument", NinaCompatibility.Create<LegacyAction>(Mock.Of<ISafetyMonitorMediator>(), "argument").Argument);
    }

    [Fact]
    public void ConstructorFailureCannotFallBackToLessProtectedSignature()
    {
        var state = new ConstructorState();
        var error = Assert.Throws<InvalidOperationException>(() => NinaCompatibility.Create<FailedAction>(Mock.Of<ISafetyMonitorMediator>(), state));
        Assert.Equal("native constructor failed", error.Message);
        Assert.False(state.LegacyCalled);
    }

    [Fact]
    public void UnknownNativeSignatureIsRejected()
    {
        Assert.Throws<NotSupportedException>(() => NinaCompatibility.Create<LegacyAction>(Mock.Of<ISafetyMonitorMediator>(), 42));
    }

    [Fact]
    public void LegacyFilterMatchesOnlyTheIssuedSlot()
    {
        var item = new LegacyFilter();
        NinaCompatibility.ConfigureFilter(item, new Filter { Position = 2 });
        Assert.True(NinaCompatibility.FilterMatches(item, 2));
        Assert.False(NinaCompatibility.FilterMatches(item, 1));
        item.Filter = null;
        Assert.False(NinaCompatibility.FilterMatches(item, 2));
    }

    [Fact]
    public void ExpressionFilterCannotChangeTheIssuedSlotOrUseCurrentFilter()
    {
        var item = new ModernFilter();
        NinaCompatibility.ConfigureFilter(item, new Filter { Position = 2 });
        Assert.True(NinaCompatibility.FilterMatches(item, 2));
        item.ComboBoxText = "Current";
        Assert.False(NinaCompatibility.FilterMatches(item, 2));
        item.ComboBoxText = null;
        item.XfilterDefinition = "1 + 1";
        Assert.False(NinaCompatibility.FilterMatches(item, 2));
    }

    [Fact]
    public void MissingSaveFailureEventIsAnExplicitLegacyCapability()
    {
        Assert.Null(NinaSaveFailure.Observe(new object(), _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void UnknownFilterActionCannotSilentlySkipConfiguration()
    {
        Assert.Throws<NotSupportedException>(() => NinaCompatibility.ConfigureFilter(new object(), new Filter { Position = 2 }));
    }

    public sealed class ModernAction
    {
        public string Argument { get; }
        public ISafetyMonitorMediator? Safety { get; }
        public ModernAction(string argument) { Argument = argument; }
        public ModernAction(string argument, ISafetyMonitorMediator safety) { Argument = argument; Safety = safety; }
    }
    public sealed class LegacyAction(string argument) { public string Argument { get; } = argument; }
    public sealed class ConstructorState { public bool LegacyCalled { get; set; } }
    public sealed class FailedAction
    {
        public FailedAction(ConstructorState state) { state.LegacyCalled = true; }
        public FailedAction(ConstructorState state, ISafetyMonitorMediator safety) { throw new InvalidOperationException("native constructor failed"); }
    }
    public sealed class Filter { public short Position { get; set; } }
    public sealed class LegacyFilter { public Filter? Filter { get; set; } }
    public sealed class ModernFilter
    {
        public string? ComboBoxText { get; set; }
        public string XfilterDefinition { get; set; } = "";
        public int Xfilter { get => int.TryParse(XfilterDefinition, out var slot) ? slot : -1; set => XfilterDefinition = value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }
}
