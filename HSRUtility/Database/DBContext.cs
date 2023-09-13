using Microsoft.EntityFrameworkCore;
using HSRUtility.Database.Models;

namespace HSRUtility.Database
{
    public class DBContext : DbContext
    {
        public DbSet<PlayerIdLink> PlayerIdLink { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source=./data/HSRUtility.db")
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
