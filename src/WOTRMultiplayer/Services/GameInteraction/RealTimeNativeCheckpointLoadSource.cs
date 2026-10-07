using System;
using System.Collections.Generic;
using System.IO;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Prepares an actual SaveInfo/ISaver pair for the native loader. Does not
    // call Game.LoadGame, dispose a world, authorize saving, or lift a repair
    // obligation. The network barrier must still verify the new world.
    public static class RealTimeNativeCheckpointLoadSource
    {
        private static void ValidateJson(string json, bool requireObject)
        {
            using var text = new StringReader(json);
            using var reader = new JsonTextReader(text) { MaxDepth = 256, DateParseHandling = DateParseHandling.None };
            var objects = new Stack<HashSet<string>>();
            var started = false; var finished = false;
            while (reader.Read())
            {
                if (finished || !started && requireObject && reader.TokenType != JsonToken.StartObject)
                    throw new InvalidDataException("native-checkpoint-invalid-json-root-or-trailing-content");
                started = true;
                switch (reader.TokenType)
                {
                    case JsonToken.StartObject: objects.Push(new(StringComparer.Ordinal)); break;
                    case JsonToken.EndObject:
                        objects.Pop(); if (reader.Depth == 0) finished = true; break;
                    case JsonToken.EndArray: if (reader.Depth == 0) finished = true; break;
                    case JsonToken.PropertyName:
                        if (!objects.Peek().Add((string)reader.Value))
                            throw new InvalidDataException("native-checkpoint-duplicate-json-property");
                        break;
                    case JsonToken.Comment:
                    case JsonToken.StartConstructor:
                    case JsonToken.Undefined:
                        throw new InvalidDataException("native-checkpoint-nonstandard-json");
                }
                if (reader.Depth == 0 && reader.TokenType != JsonToken.StartObject && reader.TokenType != JsonToken.StartArray)
                    finished = true;
            }
            if (!finished) throw new InvalidDataException("native-checkpoint-incomplete-json");
        }

        public static SaveInfo Prepare(RealTimeMemorySaver source, string expectedGameId, string checkpointId,
            Func<string, SaveInfo> decodeNativeHeader = null)
        {
            if (source == null || !source.IsSealed || string.IsNullOrWhiteSpace(expectedGameId)
                || !Guid.TryParseExact(checkpointId, "N", out _))
                throw new InvalidOperationException("native-checkpoint-load-identity-invalid");
            // Syntax and duplicate-property errors must be rejected before the
            // native LoadRoutine gets an opportunity to DisposeState(). This is
            // not proof that every native entity/reference can be reconstructed.
            foreach (var file in source.GetAllFiles())
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var json = source.ReadJson(file.Substring(0, file.Length - 5));
                    // Native SaveRoutine emits an empty tbm document outside
                    // turn-based combat; ThreadedGameLoader explicitly skips it.
                    if (file.Equals("tbm.json", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(json)) continue;
                    var mechanical = file.Equals("header.json", StringComparison.OrdinalIgnoreCase)
                        || file.Equals("player.json", StringComparison.OrdinalIgnoreCase) || file.Equals("party.json", StringComparison.OrdinalIgnoreCase)
                        || file.Length >= 37 && Guid.TryParseExact(file.Substring(0, 32), "N", out _);
                    ValidateJson(json, mechanical);
                }
            var header = source.ReadHeader();
            var gameId = JObject.Parse(header)["GameId"];
            if (gameId?.Type != JTokenType.String || (string)gameId != expectedGameId)
                throw new InvalidDataException("native-checkpoint-campaign-identity-mismatch");
            decodeNativeHeader ??= json => OwlcatJsonConvert.DeserializeObject<SaveInfo>(json);
            var info = decodeNativeHeader(header)
                ?? throw new InvalidDataException("native-checkpoint-header-not-loadable");
            if (info.GameId != expectedGameId || info.Saver != null)
                throw new InvalidDataException("native-checkpoint-decoded-header-identity-mismatch");
            // Not a path to a player's save. Native LoadRoutine only needs a
            // nonempty identity plus ISaver; Clone survives disposal of source.
            info.FolderName = "RTwP-volatile-" + checkpointId + ".memory";
            info.Saver = source.Clone();
            return info;
        }
    }
}
