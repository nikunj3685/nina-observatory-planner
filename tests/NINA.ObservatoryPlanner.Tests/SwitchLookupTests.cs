using FluentAssertions;
using Moq;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.ObservatoryPlanner.Nina;
using NINA.Sequencer.SequenceItem.Switch;
using NUnit.Framework;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NINA.ObservatoryPlanner.Tests {

    [TestFixture]
    public class SwitchLookupTests {

        private static InstructionFactory FactoryWithHub(params string[] names) {
            var switches = names.Select(n => Mock.Of<IWritableSwitch>(s => s.Name == n)).ToList();
            var mediator = new Mock<ISwitchMediator>();
            mediator.Setup(m => m.GetInfo()).Returns(new SwitchInfo { Connected = true, WritableSwitches = new ReadOnlyCollection<IWritableSwitch>(switches) });
            var nina = (NinaServices)RuntimeHelpers.GetUninitializedObject(typeof(NinaServices));
            typeof(NinaServices).GetField("<Switches>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(nina, mediator.Object);
            return new InstructionFactory(nina);
        }

        [Test]
        public void Switches_are_found_by_name_whatever_their_position_on_the_hub() {
            var f = FactoryWithHub("SW CQ-350", "ZWO 290 MM", "Filter Wheel", "ZWO EAF", "ZWO EAF P", "ZWO 2600 MM", "ZWO 2600 MM P");
            f.SwitchIndexOf("ZWO 2600 MM P").Should().Be(6);
            f.SwitchIndexOf("zwo 2600 mm").Should().Be(5, "case does not matter");
            f.SwitchIndexOf("SW CQ-350").Should().Be(0);
            ((SetSwitchValue)f.SetSwitch("ZWO EAF", 1, fallbackIndex: 3)).SwitchIndex.Should().Be(3);
            ((SetSwitchValue)f.SetSwitch("Filter Wheel", 0, fallbackIndex: 6)).SwitchIndex.Should().Be(2, "the name wins over the fallback position");
        }

        [Test]
        public void A_missing_switch_uses_its_position_in_the_list() {
            var f = FactoryWithHub("Power1", "Power2");
            f.SwitchIndexOf("ZWO EAF").Should().Be(-1);
            var item = (SetSwitchValue)f.SetSwitch("ZWO EAF", 1, fallbackIndex: 3);
            item.SwitchIndex.Should().Be(3);
            item.Value.Should().Be(1);
        }
    }
}
