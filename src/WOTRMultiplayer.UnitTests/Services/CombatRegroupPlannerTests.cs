using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class CombatRegroupPlannerTests
    {
        [Test]
        public void FindAnchorSelectsPartyUnitClosestToHostile()
        {
            var far = Unit("far", 50f, 0f);
            var engager = Unit("engager", 4f, 0f);
            var hostile = Unit("hostile", 0f, 0f);

            var result = CombatRegroupPlanner.FindAnchor([far, engager], [hostile]);

            Assert.That(result, Is.EqualTo(engager.UnitId));
        }

        [Test]
        public void FindAnchorUsesStableUnitIdWhenDistancesAreEqual()
        {
            var second = Unit("bravo", -5f, 0f);
            var first = Unit("alpha", 5f, 0f);
            var hostile = Unit("hostile", 0f, 0f);

            var result = CombatRegroupPlanner.FindAnchor([second, first], [hostile]);

            Assert.That(result, Is.EqualTo(first.UnitId));
        }

        [TestCase(30f, false)]
        [TestCase(30.01f, true)]
        public void IsFartherThanUsesStrictConfiguredDistance(float x, bool expected)
        {
            var result = CombatRegroupPlanner.IsFartherThan(new Vector3(x, 99f, 0f), Vector3.zero, 30f);

            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void FindAnchorReturnsNullWithoutHostile()
        {
            var result = CombatRegroupPlanner.FindAnchor([Unit("party", 0f, 0f)], new List<CombatRegroupPlanner.Point>());

            Assert.That(result, Is.Null);
        }

        private static CombatRegroupPlanner.Point Unit(string id, float x, float z)
        {
            return new CombatRegroupPlanner.Point(id, x, z);
        }
    }
}
