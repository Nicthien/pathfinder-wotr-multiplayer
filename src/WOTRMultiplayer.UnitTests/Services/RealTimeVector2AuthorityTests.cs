using System;
using FakeItEasy;
using NUnit.Framework;
using UnityEngine;
using WOTRMultiplayer.Abstractions;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeVector2AuthorityTests
    {
        [Test]
        public void OutsideRealTimeOrDuringBarrier_PreservesOriginalPoint()
        {
            var multiplayer = A.Fake<IMultiplayer>();
            var local = new Vector2(1.23456f, -0.12345f);
            Assert.That(RealTimeVector2Authority.Resolve(multiplayer, "shot", local), Is.EqualTo(local));

            A.CallTo(() => multiplayer.IsRealTimeCombatActive).Returns(true);
            A.CallTo(() => multiplayer.IsRealTimeCombatBarrierActive).Returns(true);
            Assert.That(RealTimeVector2Authority.Resolve(multiplayer, "shot", local), Is.EqualTo(local));
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll(A<string>._, A<Func<int>>._)).MustNotHaveHappened();
        }

        [Test]
        public void ActiveRealTime_UsesBothHostCoordinatesAtMillimetrePrecision()
        {
            var multiplayer = A.Fake<IMultiplayer>();
            A.CallTo(() => multiplayer.IsRealTimeCombatActive).Returns(true);
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll("shot:x", A<Func<int>>.That.Matches(f => f() == 1234)))
                .Returns(1250);
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll("shot:y", A<Func<int>>.That.Matches(f => f() == -124)))
                .Returns(-500);

            var result = RealTimeVector2Authority.Resolve(multiplayer, "shot", new Vector2(1.2344f, -0.1236f));

            Assert.That(result.x, Is.EqualTo(1.25f).Within(0.0001f));
            Assert.That(result.y, Is.EqualTo(-0.5f).Within(0.0001f));
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll("shot:x", A<Func<int>>._)).MustHaveHappenedOnceExactly();
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll("shot:y", A<Func<int>>._)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void ActiveRealTime_RejectsInvalidCoordinateBeforePublishingEitherAxis()
        {
            var multiplayer = A.Fake<IMultiplayer>();
            A.CallTo(() => multiplayer.IsRealTimeCombatActive).Returns(true);

            Assert.Throws<InvalidOperationException>(() => RealTimeVector2Authority.Resolve(multiplayer,
                "shot", new Vector2(1, float.NaN)));
            A.CallTo(() => multiplayer.ResolveRealTimeDiceRoll(A<string>._, A<Func<int>>._)).MustNotHaveHappened();
        }
    }
}
