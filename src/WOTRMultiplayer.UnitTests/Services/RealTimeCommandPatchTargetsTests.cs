using System;
using System.Reflection;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.EntitySystem.Persistence;
using NUnit.Framework;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCommandPatchTargetsTests
    {
        [Test]
        public void UnitCommandsRunOverloads_ExistInCurrentGameAssembly()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Assert.That(typeof(UnitCommands).GetMethod("Run", flags, null, new[] { typeof(UnitCommand) }, null), Is.Not.Null);
            Assert.That(typeof(UnitCommands).GetMethod("Run", flags, null, new[] { typeof(UnitCommand), typeof(bool), typeof(bool) }, null), Is.Not.Null);
        }

        [Test]
        public void SaveGateTargets_ExistInCurrentGameAssembly()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Assert.That(typeof(SaveManager).GetMethod(nameof(SaveManager.IsSaveAllowed), flags), Is.Not.Null);
            Assert.That(typeof(SaveManager).GetMethod(nameof(SaveManager.SaveRoutine), flags), Is.Not.Null);
        }
    }
}
