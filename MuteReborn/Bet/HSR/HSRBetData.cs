using Discord;
using System.Collections.Concurrent;

namespace MuteReborn.Bet.HSR
{
    internal class HSRBetData
    {
        internal IUser GamblingUser { get; set; }
        internal IUserMessage GamblingMessage { get; set; }
        internal IUserMessage SelectRankMessage { get; set; }
        internal string AddMessage { get; set; }
        internal string BetGuid { get; set; } = "";
        internal ConcurrentDictionary<ulong, string> SelectedRankDic { get; set; } = new();

        public HSRBetData(IUser user, IUserMessage gamblingMessage, IUserMessage selectRankMessage, string addMessage, string guid)
        {
            GamblingUser = user;
            GamblingMessage = gamblingMessage;
            SelectRankMessage = selectRankMessage;
            AddMessage = string.IsNullOrEmpty(addMessage) ? "無" : addMessage;
            BetGuid = guid;
        }
    }
}
