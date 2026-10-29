using System.Collections.Generic;
using System.Linq;
using Kingmaker.Localization;
using Kingmaker.UI.MVVM._VM.Tooltip.Bricks;
using Owlcat.Runtime.UI.Tooltips;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Content;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UI.Tooltips
{
    public class ContentDiscrepancyTooltipTemplate : TooltipBaseTemplate
    {
        private readonly NetworkPlayer _player;
        private readonly NetworkContentState _hostContent;
        private readonly RealTimeContentMismatch _realTimeMismatch;

        public ContentDiscrepancyTooltipTemplate(NetworkPlayer player, NetworkContentState hostContent,
            RealTimeContentMismatch realTimeMismatch)
        {
            _player = player;
            _hostContent = hostContent;
            _realTimeMismatch = realTimeMismatch;
        }

        public override IEnumerable<ITooltipBrick> GetHeader(TooltipTemplateType type)
        {
            yield return new TooltipBrickTitle(string.Format(new LocalizedString { Key = WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Title.Key }, _player.Name));
        }

        public override IEnumerable<ITooltipBrick> GetBody(TooltipTemplateType type)
        {
            if (_realTimeMismatch != RealTimeContentMismatch.None)
            {
                yield return new TooltipBrickTitle(
                    new LocalizedString { Key = WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Compatibility.Title.Key },
                    TooltipTitleType.H1, saberFormat: true);
                if ((_realTimeMismatch & RealTimeContentMismatch.GameVersion) != 0)
                    yield return new TooltipBrickText(string.Format(
                        new LocalizedString { Key = WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Compatibility.GameVersion.Key },
                        _hostContent?.GameVersion ?? "?", _player.ContentState?.GameVersion ?? "?"));
                if ((_realTimeMismatch & RealTimeContentMismatch.ProtocolVersion) != 0)
                    yield return new TooltipBrickText(string.Format(
                        new LocalizedString { Key = WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Compatibility.ProtocolVersion.Key },
                        _hostContent?.RealTimeProtocolVersion.ToString() ?? "?",
                        _player.ContentState?.RealTimeProtocolVersion.ToString() ?? "?"));
                if ((_realTimeMismatch & RealTimeContentMismatch.BuildFingerprint) != 0)
                    yield return new TooltipBrickText(
                        new LocalizedString { Key = WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Compatibility.BuildFingerprint.Key });
            }

            if (_player.ContentState.DiscrepantDLCs.Any())
            {
                yield return new TooltipBrickTitle(new LocalizedString { Key = WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.DLCs.Title.Key }, TooltipTitleType.H1, saberFormat: true);
                var byReason = _player.ContentState.DiscrepantDLCs.GroupBy(x => x.Reason).OrderBy(x => x.Key);
                foreach (var reasonGroup in byReason)
                {
                    yield return new TooltipBrickTitle(GetDLCDiscrepancyReasonText(reasonGroup.Key), TooltipTitleType.H4);
                    foreach (var discrepancy in reasonGroup.OrderBy(x => x.DLC.Id))
                    {
                        yield return new TooltipBrickText(discrepancy.DLC.FullName);
                    }
                }
            }

            if (_player.ContentState.DiscrepantDLCs.Any() && _player.ContentState.DiscrepantMods.Any())
            {
                yield return new TooltipBrickSpace();
                yield return new TooltipBrickSeparator(TooltipBrickElementType.Medium);
                yield return new TooltipBrickSpace();
            }

            if (_player.ContentState.DiscrepantMods.Any())
            {
                yield return new TooltipBrickTitle(new LocalizedString { Key = WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Mods.Title.Key }, TooltipTitleType.H1, saberFormat: true);
                var byReason = _player.ContentState.DiscrepantMods.GroupBy(x => x.Reason).OrderBy(x => x.Key);
                foreach (var reasonGroup in byReason)
                {
                    yield return new TooltipBrickTitle(GetModDiscrepancyReasonText(reasonGroup.Key), TooltipTitleType.H4);
                    foreach (var discrepancy in reasonGroup.OrderBy(x => x.Id))
                    {
                        var text = GetDiscrepantModText(discrepancy);
                        yield return new TooltipBrickText(text);
                    }
                }
            }
        }

        private string GetDiscrepantModText(NetworkDiscrepantMod networkDiscrepantMod)
        {
            return networkDiscrepantMod.Reason switch
            {
                NetworkDiscrepancyReason.VersionMismatch => $"{networkDiscrepantMod.Id} - {networkDiscrepantMod.HostVersion}/<color=#976c20>{networkDiscrepantMod.Version}</color> - {networkDiscrepantMod.Type}",
                NetworkDiscrepancyReason.Extra => $"{networkDiscrepantMod.Id} - {networkDiscrepantMod.Version} - {networkDiscrepantMod.Type}",
                _ => $"{networkDiscrepantMod.Id} - {networkDiscrepantMod.HostVersion} - {networkDiscrepantMod.Type}",
            };
        }

        private string GetDLCDiscrepancyReasonText(NetworkDiscrepancyReason reason)
        {
            var key = reason switch
            {
                NetworkDiscrepancyReason.Missing => WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.DLCs.Reasons.Missing.Key,
                NetworkDiscrepancyReason.Extra => WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.DLCs.Reasons.Extra.Key,
                _ => null
            };

            return string.IsNullOrEmpty(key) ? reason.ToString() : new LocalizedString { Key = key };
        }

        private string GetModDiscrepancyReasonText(NetworkDiscrepancyReason reason)
        {
            var key = reason switch
            {
                NetworkDiscrepancyReason.Missing => WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Mods.Reasons.Missing.Key,
                NetworkDiscrepancyReason.Extra => WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Mods.Reasons.Extra.Key,
                NetworkDiscrepancyReason.Disabled => WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Mods.Reasons.Disabled.Key,
                NetworkDiscrepancyReason.VersionMismatch => WellKnownKeys.LobbyWindow.Tooltips.ContentDiscrepancy.Mods.Reasons.VersionMismatch.Key,
                _ => null
            };

            return string.IsNullOrEmpty(key) ? reason.ToString() : new LocalizedString { Key = key };
        }
    }
}
