using Microsoft.EntityFrameworkCore;
using MuteReborn.Database.Models;

namespace MuteReborn.Database
{
    public class DBContext : DbContext
    {
        public DbSet<GuildConfigs> GuildConfigs { get; set; }
        public DbSet<MuteRebornTicket> MuteRebornTickets { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source=./data/MuteReborn.db")
#if DEBUG || DEBUG_DONTREGISTERCOMMAND
            //.LogTo((act) => System.IO.File.AppendAllText("DbTrackerLog.txt", act), Microsoft.Extensions.Logging.LogLevel.Information)
#endif
            .EnableSensitiveDataLogging();

        public static DBContext GetDbContext()
        {
            var context = new DBContext();
            context.Database.SetCommandTimeout(60);
            var conn = context.Database.GetDbConnection();
            conn.Open();
            using (var com = conn.CreateCommand())
            {
                com.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF";
                com.ExecuteNonQuery();
            }
            return context;
        }
    }
}
