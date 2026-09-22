using NadekoBot.Medusa;
using Newtonsoft.Json;
using Serilog;

namespace RestBan.Service
{
    [svc(Lifetime.Singleton)]
    public class RestBanService
    {
        public List<ulong> RestBanList { get; private set; }
        public readonly string FILE_PATH = "data/RestBanList.json";

        public RestBanService()
        {
            if (!File.Exists(FILE_PATH))
            {
                RestBanList = [];
                return;
            }

            try
            {
                // 檔案損壞或內容為 null 時退回空清單，避免 service 建構失敗導致所有指令都無法使用
                RestBanList = JsonConvert.DeserializeObject<List<ulong>>(File.ReadAllText(FILE_PATH)) ?? [];
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RestBan: 讀取 {Path} 失敗，改用空清單", FILE_PATH);
                RestBanList = [];
            }
        }
    }
}
