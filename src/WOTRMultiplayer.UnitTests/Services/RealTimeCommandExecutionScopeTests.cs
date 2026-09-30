using NUnit.Framework;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCommandExecutionScopeTests
    {
        [Test]
        public void NestedRemoteCommandScopes_RestorePreviousState()
        {
            Assert.That(RealTimeCommandExecutionScope.IsActive, Is.False);
            using (RealTimeCommandExecutionScope.Enter())
            {
                Assert.That(RealTimeCommandExecutionScope.IsActive, Is.True);
                using (RealTimeCommandExecutionScope.Enter())
                {
                    Assert.That(RealTimeCommandExecutionScope.IsActive, Is.True);
                }
                Assert.That(RealTimeCommandExecutionScope.IsActive, Is.True);
            }
            Assert.That(RealTimeCommandExecutionScope.IsActive, Is.False);
        }
    }
}
