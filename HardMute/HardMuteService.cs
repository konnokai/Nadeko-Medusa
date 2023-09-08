using Discord;
using Discord.WebSocket;
using HardMute.Database.Models;
using Nadeko.Snake;
using Serilog;
using System.Collections.Concurrent;

namespace HardMute.Services
{
    [svc(Lifetime.Singleton)]
    public class HardMuteService
    {
        private DiscordSocketClient _client;

        public ConcurrentDictionary<ulong, ConcurrentHashSet<ulong>> HardMutedUsers { get; private set; } = new();
        public ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, Timer>> UnTimers { get; } = new();

        public void Inject(DiscordSocketClient client)
        {
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
                        HardMutedUsers.TryAdd(item.GuildId, new ConcurrentHashSet<ulong> { item.UserId });

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
            _client.MessageReceived -= _client_MessageReceived;
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

        public async Task TimedHardMute(IGuildUser user, TimeSpan after)
        {
            MuteUser(user);

            using (var uow = Database.DBContext.GetDbContext())
            {
                var config = uow.UnmuteTimer.Add(new UnmuteTimer()
                {
                    GuildId = user.GuildId,
                    UserId = user.Id,
                    UnmuteAt = DateTime.UtcNow + after
                });
                await uow.SaveChangesAsync();
            }

            StartUn_Timer(user.GuildId, user.Id, after);
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
            //load the unmute timers for this guild
            var userUnTimers = UnTimers.GetOrAdd(guildId, new ConcurrentDictionary<ulong, Timer>());

            //unmute timer to be added
            var toAdd = new Timer(async _ =>
            {
                try
                {
                    // unmute the user, this will also remove the timer from the db
                    await UnmuteUser(guildId, userId);
                }
                catch (Exception ex)
                {
                    await RemoveTimerFromDbAsync(guildId, userId); // if unmute errored, just remove unmute from db
                    Log.Warning(ex, "Couldn't unmute user {UserId} in guild {GuildId}", userId, guildId);
                }
            }, null, after, Timeout.InfiniteTimeSpan);

            //add it, or stop the old one and add this one
            userUnTimers.AddOrUpdate(userId, (key) =>
            {
                return toAdd;
            }, (key, old) =>
            {
                old.Change(Timeout.Infinite, Timeout.Infinite);
                return toAdd;
            });
        }

        public async Task UnmuteUser(
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
                removed.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private async Task RemoveTimerFromDbAsync(ulong guildId, ulong userId)
        {
            using var uow = Database.DBContext.GetDbContext();
            var toDelete = uow.UnmuteTimer.FirstOrDefault(x => x.GuildId == guildId && x.UserId == userId);

            if (toDelete is not null)
                uow.Remove(toDelete);

            await uow.SaveChangesAsync();
        }
    }
}
