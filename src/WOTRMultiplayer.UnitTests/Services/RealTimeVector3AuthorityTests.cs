using System;
using FakeItEasy;
using NUnit.Framework;
using UnityEngine;
using WOTRMultiplayer.Abstractions;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeVector3AuthorityTests
    {
        [Test]
        public void OutsideRealTimeOrDuringBarrier_PreservesOriginalPoint()
        {
            var multiplayer = A.Fake<IMultiplayer>();
            var local = new Vector3(1.23456f, -0.12345f, 7.65432f);
            Assert.That(RealTimeVector3Authority.Resolve(multiplayer, "fear", local), Is.EqualTo(local));

            A.CallTo(() => multiplayer.IsRealTimeCombatActive).Returns(true);
            A.CallTo(() => multiplayer.IsRealTimeCombatBarrierActive).Returns(true);
            Assert.That(RealTimeVector3Authority.Resolve(multiplayer, "fear", local), Is.EqualTo(local));
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll(A<string>._, A<Func<int>>._)).MustNotHaveHappened();
        }

        [Test]
        public void ActiveRealTime_UsesHostDestinationRatherThanLocalChoice()
        {
            var multiplayer = A.Fake<IMultiplayer>();
            A.CallTo(() => multiplayer.IsRealTimeCombatActive).Returns(true);
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll("fear:x", A<Func<int>>.That.Matches(f => f() == 1234)))
                .Returns(3000);
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll("fear:y", A<Func<int>>.That.Matches(f => f() == -124)))
                .Returns(0);
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll("fear:z", A<Func<int>>.That.Matches(f => f() == 7654)))
                .Returns(-2000);

            var result = RealTimeVector3Authority.Resolve(multiplayer, "fear",
                new Vector3(1.2344f, -0.1236f, 7.6544f));

            Assert.That(result.x, Is.EqualTo(3f).Within(0.0001f));
            Assert.That(result.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(result.z, Is.EqualTo(-2f).Within(0.0001f));
        }

        [Test]
        public void ActiveRealTime_RejectsInvalidCoordinateBeforePublishingAnyAxis()
        {
            var multiplayer = A.Fake<IMultiplayer>();
            A.CallTo(() => multiplayer.IsRealTimeCombatActive).Returns(true);

            Assert.Throws<InvalidOperationException>(() => RealTimeVector3Authority.Resolve(multiplayer,
                "fear", new Vector3(1, 2, float.PositiveInfinity)));
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll(A<string>._, A<Func<int>>._)).MustNotHaveHappened();
        }
    }
}
