using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Buffs;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    internal sealed class RealTimeBuffIdentityReconciliation : IDisposable
    {
        private readonly List<(Buff Buff, string Id)> _changed = new();
        private bool _committed;
        public void Align(UnitEntityData unit, NetworkUnitBuffCollection expected, NetworkUnitBuffCollection local)
        {
            var plan = RealTimeBuffIdentityPlan.Create(expected.Buffs, local.Buffs);
            foreach (var entry in plan)
            {
                if (unit.Facts.List.Any(f => string.Equals(f.UniqueId, entry.Value, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("transition-buff-id-collision:" + entry.Value);
                var buff = unit.Buffs.Enumerable.Single(b => string.Equals(b.UniqueId, entry.Key, StringComparison.OrdinalIgnoreCase));
                _changed.Add((buff, buff.UniqueId));
                buff.UniqueId = entry.Value;
            }
        }
        public void Commit() => _committed = true;
        public void Dispose()
        {
            if (_committed) return;
            for (var i = _changed.Count - 1; i >= 0; i--) _changed[i].Buff.UniqueId = _changed[i].Id;
        }
    }
}
