using System.Collections.Generic;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class CombatSaveCompatibility
    {
        // These builds changed restoration timing without changing the archive
        // schema or protocol 74. Keep only their exact fingerprints as bounded
        // migration inputs; every other archive validation still applies.
        internal static IReadOnlyCollection<string> AcceptedArchiveFingerprints { get; } = new[]
        {
            "2EB06FB0790AFBB70FCC423886651C2DD3D03E1F75B588F9E101E5F90206E0EA",
            "6CC97D808D54FA135AE5715C5CC7DE4FA2EAD62BBBBFAC0E6E5C2051781BA138",
            "E69E7A4D5D6958C9EAA1D0D5413853F76585E6A515E924B038371B6BD9262613"
        };
    }
}
