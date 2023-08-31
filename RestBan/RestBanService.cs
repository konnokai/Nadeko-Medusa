using Nadeko.Snake;
using Newtonsoft.Json;

namespace RestBan.Service
{
    [svc(Lifetime.Singleton)]
    public class RestBanService
    {
        public List<ulong> RestBanList { get; private set; }
        public readonly string FILE_PATH = "data/RestBanList.json";

        public RestBanService()
        {
            if (File.Exists(FILE_PATH))
                RestBanList = JsonConvert.DeserializeObject<List<ulong>>(File.ReadAllText(FILE_PATH));
            else
                RestBanList = new List<ulong>();
        }
    }
}
