using System;
using Kingmaker;

namespace WOTRMultiplayer.Services.GameInteraction
{
    internal static class RealTimeNativeCharacterUiGuard
    {
        // A normal/pause game mode does not prove that character editing has
        // finished. Replacing that world would invalidate its pending controller.
        internal static void RequireIdle(Game game)
        {
            var context = game?.RootUiContext?.InGameVM?.StaticPartVM?.CharGenContextVM;
            RequireIdle(context?.CharGenVM != null && context.RespecWindowVM != null,
                context?.CharGenVM?.Value != null, context?.RespecWindowVM?.Value != null);
        }

        internal static void RequireIdle(bool contextAvailable, bool characterEditorOpen, bool respecOpen)
        {
            if (!contextAvailable)
                throw new InvalidOperationException("native-checkpoint-character-ui-unavailable");
            if (characterEditorOpen || respecOpen)
                throw new InvalidOperationException("native-checkpoint-character-edit-in-progress");
        }
    }
}
