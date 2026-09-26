using Moq;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using PsfGuard.Director.SimulatorProbe;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaPreparationProbeTests
{
    [Theory]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((short)2)]
    public async Task NativeFilterItemUsesExactTypedSlot(short slot)
    {
        var profiles = new Mock<IProfileService>();
        var profile = new Mock<IProfile>();
        var settings = new Mock<IFilterWheelSettings>();
        var filter = new FilterInfo { Name = "Test", Position = slot };
        settings.SetupGet(x => x.FilterWheelFilters).Returns(new ObserveAllCollection<FilterInfo>([filter]));
        profile.SetupGet(x => x.FilterWheelSettings).Returns(settings.Object);
        profiles.SetupGet(x => x.ActiveProfile).Returns(profile.Object);
        var wheel = new Mock<IFilterWheelMediator>();
        wheel.Setup(x => x.GetInfo()).Returns(new FilterWheelInfo { Connected = true });
        wheel.Setup(x => x.ChangeFilter(filter, It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>())).ReturnsAsync(filter);
        var binding = new NinaEquipmentBinding(Guid.NewGuid(), "rig", "constraint", "camera", "wheel", [new("filter", slot, "Test")], false, 0);
        var item = Assert.IsType<NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter>(SimulatorSequence.CreatePreparationItem(
            new PreparationOperation.SwitchFilter("filter"), profiles.Object, Mock.Of<ICameraMediator>(), wheel.Object, binding));
        Assert.True(item.Validate(), string.Join(", ", item.Issues));
        Assert.Equal(slot, item.Xfilter);
        await item.Execute(new Progress<ApplicationStatus>(), default);
        wheel.Verify(x => x.ChangeFilter(filter, It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()), Times.Once);
    }
}
