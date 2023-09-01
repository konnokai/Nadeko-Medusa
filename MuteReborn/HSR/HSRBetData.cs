using Discord;

namespace MuteReborn.HSR
{
    internal class HSRBetData
    {
        internal IUser GamblingUser { get; set; }
        internal IUserMessage GamblingMessage { get; set; }
        internal IUserMessage SelectRankMessage { get; set; }
        internal string AddMessage { get; set; }
        internal string BetGuid { get; set; } = "";
        internal Dictionary<ulong, string> SelectedRankDic { get; set; } = new Dictionary<ulong, string>();

        public HSRBetData(IUser user, IUserMessage gamblingMessage, IUserMessage selectRankMessage, string addMessage, string guid)
        {
            GamblingUser = user;
            GamblingMessage = gamblingMessage;
            SelectRankMessage = selectRankMessage;
            AddMessage = (string.IsNullOrEmpty(addMessage) ? "無" : addMessage);
            BetGuid = guid;
        }
    }
}
