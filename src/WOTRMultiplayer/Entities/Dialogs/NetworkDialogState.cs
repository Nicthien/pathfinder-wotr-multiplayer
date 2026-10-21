using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace WOTRMultiplayer.Entities.Dialogs
{
    public class NetworkDialogState
    {
        public NetworkDialog Dialog { get; }

        public string CurrentCueName { get; set; }

        public NetworkDialogAnswer Answer { get; set; }

        public ConcurrentDictionary<string, HashSet<long>> CueViews { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<long, string> AnswerSuggestions { get; set; } = new();

        public bool IsSelectingAnswer { get; set; }

        public bool HasSystemAnswer { get; set; }

        public string VoteRoundId { get; set; }

        public HashSet<long> VoteParticipants { get; set; } = [];

        public NetworkDialogAnswer PendingVoteAnswer { get; set; }

        public bool VoteCommitted { get; set; }

        public bool VoteRequiresCharacterSelection { get; set; }

        // Local input history, independent of asynchronously received votes.
        public string LastLocalVoteRound { get; set; }
        public string LastLocalVoteCue { get; set; }
        public string LastLocalVoteAnswer { get; set; }
        public DateTime LastVoteReminderAt { get; set; }

        public NetworkDialogState(NetworkDialog networkDialog)
        {
            Dialog = networkDialog;
        }
    }
}
