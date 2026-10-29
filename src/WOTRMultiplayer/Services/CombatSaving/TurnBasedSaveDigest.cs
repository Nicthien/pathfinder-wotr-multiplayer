using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class TurnBasedSaveDigest
    {
        internal static string Project(JObject source)
        {
            var turn = source["m_CurrentTurn"] as JObject
                ?? throw new InvalidDataException("combat-save-turn-schema-unsupported");
            var result = new JObject();
            result["controller"] = Required(source, "MultiplayerControllerState");
            foreach (var key in new[] { "TimeSinceStart", "TimeToNextRound" })
                if (result["controller"][key] != null)
                    result["controller"][key] = ProjectTime((double)result["controller"][key]);
            if (result["controller"]["UnitActions"] is JArray units)
                foreach (var unit in units.OfType<JObject>())
                {
                    // PreviousPosition is a frame-local motion baseline, including
                    // idle non-combat NPCs. Archive it for native restoration; the
                    // world hash already compares authoritative current positions.
                    unit.Remove("PreviousPositionX"); unit.Remove("PreviousPositionY"); unit.Remove("PreviousPositionZ");
                    foreach (var key in new[] { "InitiativeCooldown", "Standard", "Move", "Swift", "OpportunityCooldown" })
                        if (unit[key] != null)
                        {
                            var value = (double)unit[key];
                            if (double.IsNaN(value) || double.IsInfinity(value))
                                throw new InvalidDataException("combat-save-action-counter-invalid");
                            // Native float accumulation differs by a few microseconds
                            // between peers. Preserve zero and action-boundary sides;
                            // only the digest is quantized, never the saved host value.
                            unit[key] = new JObject { ["time"] = Math.Round(value, 4),
                                ["sign"] = Math.Sign(value), ["actionInterval"] = Math.Ceiling(value / 3d) };
                        }
                }
            // Whitelist mechanical state. Never compare camera/selection/predictions.
            foreach (var key in new[] { "RoundNumber", "m_HasSurpriseRound", "m_KnownInitiative" })
                result[key] = Required(source, key);
            result["m_Units"] = ProjectUnits(Required(source, "m_Units"));
            var selected = new JObject();
            foreach (var key in new[] { "m_Rider", "Status", "m_ActingInSurpriseRound",
                "m_RiderMovementStats", "m_MountMovementStats", "m_AttackMode", "m_MountAttackMode", "m_GroundMovementLimit" })
                selected[key] = Required(turn, key);
            selected["m_RiderActionState"] = CurrentActions(Required(turn, "m_RiderActionState"));
            selected["m_MountActionState"] = CurrentActions(Required(turn, "m_MountActionState"));
            result["turn"] = selected;
            var canonical = Canonical(result);
            NormalizeReferenceMetadata(canonical, new Dictionary<string, string>(StringComparer.Ordinal));
            return canonical.ToString(Formatting.None);
        }
        private static void NormalizeReferenceMetadata(JToken token, IDictionary<string, string> references)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if ((property.Name == "$id" || property.Name == "$ref")
                        && property.Value.Type == JTokenType.String)
                    {
                        var original = (string)property.Value;
                        if (!references.TryGetValue(original, out var normalized))
                        {
                            normalized = "reference-" + references.Count.ToString("D8");
                            references.Add(original, normalized);
                        }
                        property.Value = normalized;
                    }
                    else NormalizeReferenceMetadata(property.Value, references);
                }
                return;
            }
            if (token is JArray array)
                foreach (var item in array) NormalizeReferenceMetadata(item, references);
        }
        internal static string DescribeFirstDifference(string expected, string actual)
        {
            var difference = FirstDifference(JToken.Parse(expected), JToken.Parse(actual), "$", out var detail);
            return difference ? detail : "none";
        }
        private static bool FirstDifference(JToken expected, JToken actual, string path, out string detail)
        {
            if (expected?.Type != actual?.Type)
            {
                detail = path + ":type expected=" + expected?.Type + ";actual=" + actual?.Type;
                return true;
            }
            if (expected is JObject expectedObject && actual is JObject actualObject)
            {
                foreach (var property in expectedObject.Properties())
                {
                    var other = actualObject.Property(property.Name);
                    if (other == null)
                    {
                        detail = path + "." + property.Name + ":missing-actual";
                        return true;
                    }
                    if (FirstDifference(property.Value, other.Value, path + "." + property.Name, out detail)) return true;
                }
                foreach (var property in actualObject.Properties())
                    if (expectedObject.Property(property.Name) == null)
                    {
                        detail = path + "." + property.Name + ":unexpected-actual";
                        return true;
                    }
                detail = null;
                return false;
            }
            if (expected is JArray expectedArray && actual is JArray actualArray)
            {
                if (expectedArray.Count != actualArray.Count)
                {
                    detail = path + ":count expected=" + expectedArray.Count + ";actual=" + actualArray.Count;
                    return true;
                }
                for (var i = 0; i < expectedArray.Count; i++)
                    if (FirstDifference(expectedArray[i], actualArray[i], path + "[" + i + "]", out detail)) return true;
                detail = null;
                return false;
            }
            if (!JToken.DeepEquals(expected, actual))
            {
                detail = path + ":expected=" + Preview(expected) + ";actual=" + Preview(actual);
                return true;
            }
            detail = null;
            return false;
        }
        private static string Preview(JToken value)
        {
            var text = value?.ToString(Formatting.None) ?? "null";
            return text.Length <= 160 ? text : text.Substring(0, 160) + "...";
        }
        private static JToken ProjectUnits(JToken value)
        {
            var units = value.DeepClone();
            if (units is not JArray array) throw new InvalidDataException("combat-save-units-schema-unsupported");
            foreach (var unit in array.OfType<JObject>())
                // GraphUpdateObject serializes only an object identity. The
                // deserialized object has no pathfinding payload and must be
                // discarded before the restored turn can advance. It is not
                // mechanical state and cannot participate in rebound equality.
                unit.Remove("LastNavmeshUpdateObject");
            return units;
        }
        private static JToken CurrentActions(JToken value)
        {
            if (value.Type == JTokenType.Null) return JValue.CreateNull();
            var state = value as JObject ?? throw new InvalidDataException("combat-save-action-schema-unsupported");
            var actions = state["ActionsStates"] as JObject
                ?? throw new InvalidDataException("combat-save-action-schema-unsupported");
            var result = new JObject { ["IsSurprise"] = Required(state, "IsSurprise") };
            foreach (var name in new[] { "FiveFootStep", "Move", "Standard", "Swift", "Free" })
            {
                var action = actions[name] as JObject
                    ?? throw new InvalidDataException("combat-save-action-schema-unsupported");
                var current = new JObject();
                foreach (var key in new[] { "AbilityActivityStateCurrent", "AttackActivityStateCurrent",
                    "MovementActivityStateCurrent", "MaxMoveDistance", "RemainingMoveDistance" })
                    current[key] = Required(action, key);
                result[name] = current;
            }
            return result;
        }
        private static JToken ProjectTime(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidDataException("combat-save-clock-invalid");
            return new JObject { ["time"] = Math.Round(value, 4), ["sign"] = Math.Sign(value),
                ["roundInterval"] = Math.Ceiling(value / 6d) };
        }
        private static JToken Required(JObject source, string key)
            => source.TryGetValue(key, out var value) ? value.DeepClone()
                : throw new InvalidDataException("combat-save-missing-native-field:" + key);
        private static JToken Canonical(JToken token)
        {
            if (token is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new JProperty(p.Name, Canonical(p.Value))));
            if (token is JArray array) return new JArray(array.Select(Canonical));
            return token.DeepClone();
        }
    }
}
