using HardMute.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace HardMute.Database
{
    public class DBContext : DbContext
    {
        public DbSet<UnmuteTimer> UnmuteTimer { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source=./data/HardMute.db")
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
                // synchronous=OFF 在系統或電源異常時可能遺失已回報成功的交易，改用 NORMAL
                com.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL";
                com.ExecuteNonQuery();
            }
            return context;
        }
    }
}
