using System.Collections.Generic;
using FakeItEasy;
using Kingmaker.Settings;
using NUnit.Framework;
using UnityEngine;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Services.Settings;

namespace WOTRMultiplayer.UnitTests.Services.Settings
{
    [TestFixture]
    public class WellKnownSettingsTests
    {
        [TestCaseSource(nameof(GetWellKnownKeysTestCases))]
        public void Initialize_KeyPathIsInitializedCorrectly(WellKnownSettingTestCase testCase)
        {
            // Act
            WellKnownSettings.Initialize();
            var key = testCase.Key();

            // Assert
            Assert.That(key, Is.Not.Null.Or.Empty);
            Assert.That(key, Does.StartWith(WellKnownSettings.RootKey));
            Assert.That(key, Contains.Substring(WellKnownSettings.KeyPathSeparator), "Atleast one key path separator is expected");
        }

        [Test]
        public void ShowLobby_DefaultBinding_IsControlShiftF2()
        {
            var binding = WellKnownSettings.Hotkeys.ShowLobby.DefaultValue.Binding1;

            Assert.That(binding.Key, Is.EqualTo(KeyCode.F2));
            Assert.That(binding.IsCtrlDown, Is.True);
            Assert.That(binding.IsShiftDown, Is.True);
            Assert.That(binding.IsAltDown, Is.False);
        }

        [Test]
        public void Initialize_LegacyF2ShowLobbyBinding_MigratesToNewDefault()
        {
            var settings = A.Fake<ISettingsControllerAccessor>();
            var legacyBinding = new KeyBindingPair
            {
                Binding1 = new KeyBindingData { Key = KeyCode.F2 },
            };
            A.CallTo(() => settings.GetValue(WellKnownSettings.Hotkeys.ShowLobby)).Returns(legacyBinding);

            new MultiplayerSettingsProvider(settings).Initialize();

            A.CallTo(() => settings.SetValue(
                WellKnownSettings.Hotkeys.ShowLobby,
                WellKnownSettings.Hotkeys.ShowLobby.DefaultValue)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void Initialize_CustomShowLobbyBinding_IsPreserved()
        {
            var settings = A.Fake<ISettingsControllerAccessor>();
            var customBinding = new KeyBindingPair
            {
                Binding1 = new KeyBindingData { IsCtrlDown = true, Key = KeyCode.F2 },
            };
            A.CallTo(() => settings.GetValue(WellKnownSettings.Hotkeys.ShowLobby)).Returns(customBinding);

            new MultiplayerSettingsProvider(settings).Initialize();

            A.CallTo(() => settings.SetValue(
                WellKnownSettings.Hotkeys.ShowLobby,
                A<KeyBindingPair>.Ignored)).MustNotHaveHappened();
        }

        private static IEnumerable<WellKnownSettingTestCase> GetWellKnownKeysTestCases()
        {
            yield return new WellKnownSettingTestCase { Name = "general->playerName", Key = () => WellKnownSettings.General.PlayerName.Key };

            yield return new WellKnownSettingTestCase { Name = "networking->hostPortRangeStart", Key = () => WellKnownSettings.Networking.HostPortRangeStart.Key };
            yield return new WellKnownSettingTestCase { Name = "networking->hostPortRangeEnd", Key = () => WellKnownSettings.Networking.HostPortRangeEnd.Key };

            yield return new WellKnownSettingTestCase { Name = "dangerZone->defaultForcedPauseTimeout", Key = () => WellKnownSettings.DangerZone.DefaultForcedPauseTimeout.Key };
            yield return new WellKnownSettingTestCase { Name = "dangerZone->restEncounterForcedPauseTimeout", Key = () => WellKnownSettings.DangerZone.RestEncounterForcedPauseTimeout.Key };
        }
    }
}
