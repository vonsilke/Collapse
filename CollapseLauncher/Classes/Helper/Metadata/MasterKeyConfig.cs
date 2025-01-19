﻿using System.Text.Json.Serialization;

namespace CollapseLauncher.Helper.Metadata
{
#nullable enable
    [JsonSerializable(typeof(MasterKeyConfig), GenerationMode = JsonSourceGenerationMode.Metadata)]
    internal sealed partial class MesterKeyConfigJSONContext : JsonSerializerContext;

    public sealed class MasterKeyConfig : Hashable
    {
        public byte[]? Key { get; set; }
        public int BitSize { get; set; }
    }
}
