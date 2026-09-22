using Discord;
using Discord.WebSocket;
using HardMute.Database.Models;
using NadekoBot.Medusa;
using Serilog;
using System.Collections.Concurrent;

namespace HardMute.Services
{
    [svc(Lifetime.Singleton)]
    public class HardMuteService
    {
        private DiscordSocketClient _client;
        private volatile bool _cancelled;

        public ConcurrentDictionary<ulong, ConcurrentHashSet<ulong>> HardMutedUsers { get; private set; } = new();
        public ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, Timer>> UnTimers { get; } = new();

        /// <summary>
        /// 每個「GuildId-UserId」一把非同步鎖。
        /// 同一使用者的禁言寫入、timer 替換與解除必須序列化，否則舊 timer 的 callback
        /// 可能在更新途中執行，把剛寫入的解除紀錄刪掉並解除新的禁言。
        /// 項目數與曾被 HardMute 過的 (guild, user) 數量同級，不另外回收。
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _userGates = new();

        private SemaphoreSlim GetUserGate(ulong guildId, ulong userId)
            => _userGates.GetOrAdd($"{guildId}-{userId}", static _ => new SemaphoreSlim(1, 1));

        public void Inject(DiscordSocketClient client)
        {
            _cancelled = false;
            _client = client;
            _client.MessageReceived += _client_MessageReceived;

            using (var db = Database.DBContext.GetDbContext())
            {
                var max = TimeSpan.FromDays(49);
                foreach (var item in db.UnmuteTimer)
                {
                    if (HardMutedUsers.ContainsKey(item.GuildId))
                        HardMutedUsers[item.GuildId].Add(item.UserId);
                    else
                        HardMutedUsers.TryAdd(item.GuildId, [item.UserId]);

                    TimeSpan after;
                    if (item.UnmuteAt - TimeSpan.FromMinutes(1) <= DateTime.UtcNow)
                    {
                        after = TimeSpan.FromMinutes(1);
                    }
                    else
                    {
                        var unmute = item.UnmuteAt - DateTime.UtcNow;
                        after = unmute > max ? max : unmute;
                    }

                    StartUn_Timer(item.GuildId, item.UserId, after);
                }
            }
        }

        public void CancelEvent()
        {
            _cancelled = true;

            _client.MessageReceived -= _client_MessageReceived;

            // 卸載時要把已建立的計時器一起釋放，否則舊計時器仍會執行並持有此 service
            foreach (var userTimers in UnTimers.Values)
            {
                foreach (var timer in userTimers.Values)
                    timer.Dispose();
            }

            UnTimers.Clear();
        }

        private async Task _client_MessageReceived(SocketMessage arg)
        {
            if (arg is not SocketUserMessage)
                return;
            if (arg.Channel is not SocketGuildChannel channel)
                return;

            if (HardMutedUsers.TryGetValue(channel.Guild.Id, out var muted))
            {
                if (muted.Contains(arg.Author.Id))
                    await arg.DeleteAsync();
            }
        }

        /// <summary>
        /// 對使用者加上強制禁言並寫入解除時間。
        /// 先清掉同一使用者的舊紀錄再寫入新的一筆，避免重複 HardMute 累積多筆而讓解除只刪到其中一筆；
        /// 資料庫寫入成功後才更新記憶體狀態，寫入失敗時就不會留下沒有解除紀錄的封鎖。
        /// 整段（查詢、刪除、新增、timer 替換）在同一使用者的鎖內執行，
        /// 否則兩個並行呼叫可能都查不到舊紀錄而各自新增，或讓舊 timer 在更新途中刪掉新紀錄。
        /// </summary>
        /// <returns>真正執行成功回 true；外掛正在卸載而略過時回 false，呼叫端不可顯示成功。</returns>
        public async Task<bool> TimedHardMute(IGuildUser user, TimeSpan after)
        {
            if (_cancelled)
                return false;

            var gate = GetUserGate(user.GuildId, user.Id);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 卸載後不要再建立新的 timer，否則沒人負責釋放
                if (_cancelled)
                    return false;

                using (var uow = Database.DBContext.GetDbContext())
                {
                    var old = uow.UnmuteTimer.Where(x => x.GuildId == user.GuildId && x.UserId == user.Id);
                    uow.UnmuteTimer.RemoveRange(old);
                    uow.UnmuteTimer.Add(new UnmuteTimer()
                    {
                        GuildId = user.GuildId,
                        UserId = user.Id,
                        UnmuteAt = DateTime.UtcNow + after
                    });
                    await uow.SaveChangesAsync();
                }

                MuteUser(user);

                StartUn_Timer(user.GuildId, user.Id, after);

                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        public void MuteUser(IGuildUser usr)
        {
            StopTimer(usr.GuildId, usr.Id);
            if (HardMutedUsers.ContainsKey(usr.GuildId))
                HardMutedUsers[usr.GuildId].Add(usr.Id);
            else
                HardMutedUsers.TryAdd(usr.GuildId, new ConcurrentHashSet<ulong> { usr.Id });
        }

        public void StartUn_Timer(
            ulong guildId,
            ulong userId,
            TimeSpan after)
        {
            if (_cancelled)
                return;

            //load the unmute timers for this guild
            var userUnTimers = UnTimers.GetOrAdd(guildId, new ConcurrentDictionary<ulong, Timer>());

            //unmute timer to be added
            Timer? toAdd = null;

            toAdd = new Timer(async _ =>
            {
                var gate = GetUserGate(guildId, userId);
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // 被新的排程取代時，舊 timer 的 callback 可能已經排進執行佇列，
                    // Dispose 阻止不了它；這裡再確認自己仍是目前登記的 timer，
                    // 否則會刪掉新寫入的解除紀錄並解除新的禁言。
                    if (!userUnTimers.TryGetValue(userId, out var current) || !ReferenceEquals(current, toAdd))
                        return;

                    Log.Information("Unhardmute user {UserId} in guild {GuildId}", userId, guildId);

                    try
                    {
                        // unmute the user, this will also remove the timer from the db
                        await UnmuteUserInternal(guildId, userId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 這裡若再失敗會變成計時器執行緒上的未處理例外，因此單獨攔一次
                        try
                        {
                            await RemoveTimerFromDbAsync(guildId, userId).ConfigureAwait(false); // if unmute errored, just remove unmute from db
                        }
                        catch (Exception dbEx)
                        {
                            Log.Error(dbEx, "Couldn't remove unmute timer for {UserId} in guild {GuildId}", userId, guildId);
                        }

                        Log.Warning(ex, "Couldn't unhardmute user {UserId} in guild {GuildId}", userId, guildId);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, null, after, Timeout.InfiniteTimeSpan);

            //add it, or dispose the old one and add this one
            userUnTimers.AddOrUpdate(userId, (key) =>
            {
                return toAdd;
            }, (key, old) =>
            {
                old.Dispose();
                return toAdd;
            });
        }

        public async Task UnmuteUser(
            ulong guildId,
            ulong usrId)
        {
            var gate = GetUserGate(guildId, usrId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await UnmuteUserInternal(guildId, usrId).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 解除的實際內容。呼叫端必須已持有該使用者的鎖。
        /// </summary>
        private async Task UnmuteUserInternal(
            ulong guildId,
            ulong usrId)
        {
            StopTimer(guildId, usrId);

            if (HardMutedUsers.TryGetValue(guildId, out ConcurrentHashSet<ulong> muted))
                muted.TryRemove(usrId);

            await RemoveTimerFromDbAsync(guildId, usrId);
        }

        public void StopTimer(ulong guildId, ulong userId)
        {
            if (!UnTimers.TryGetValue(guildId, out var userTimer))
                return;

            if (userTimer.TryRemove(userId, out var removed))
            {
                removed.Dispose();
            }
        }

        /// <summary>
        /// 刪除該使用者在資料庫中的解除紀錄。
        /// 條件一次刪除全部符合的紀錄，藉此清掉舊版可能留下的重複資料。
        /// </summary>
        private async Task RemoveTimerFromDbAsync(ulong guildId, ulong userId)
        {
            using var uow = Database.DBContext.GetDbContext();
            var toDelete = uow.UnmuteTimer.Where(x => x.GuildId == guildId && x.UserId == userId);
            uow.UnmuteTimer.RemoveRange(toDelete);

            await uow.SaveChangesAsync();
        }
    }
}
