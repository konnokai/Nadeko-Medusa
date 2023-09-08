using LogExtension.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace LogExtension.Database
{
    public class DBContext : DbContext
    {
        public DbSet<GuildLogConfigs> GuildLogConfigs { get; set; }
        public DbSet<LogIgnores> LogIgnores { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source=./data/LogExtension.db")
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
