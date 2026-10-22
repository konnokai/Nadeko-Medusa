using System.Reflection;
using System.Text.Json;
using NadekoBot.Modules.Utility.AiAgent;

namespace McpBridge
{
    // bot 沒有提供動態登記 AI 工具的介面，只能用反射改私有成員。
    // 這裡集中列出用到的成員，bot 升級後由測試和載入時的檢查發現改名。
    public static class BotInternals
    {
        private const BindingFlags INSTANCE = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags STATIC = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly TimeSpan _indexReadyTimeout = TimeSpan.FromMinutes(2);

        private static readonly FieldInfo? _tools = typeof(AiToolRegistry).GetField("_tools", INSTANCE);
        private static readonly FieldInfo? _dataTools = typeof(AiToolRegistry).GetField("_dataTools", INSTANCE);
        private static readonly FieldInfo? _allTools = typeof(AiToolRegistry).GetField("_allTools", INSTANCE);
        private static readonly FieldInfo? _coreSchemas = typeof(AiToolRegistry).GetField("_coreSchemas", INSTANCE);
        private static readonly MethodInfo? _buildSchema = typeof(AiToolRegistry).GetMethod("BuildSchema", STATIC);
        private static readonly MethodInfo? _buildIndex = typeof(DataToolSearchService).GetMethod("BuildIndexInternalAsync", INSTANCE);

        // 每次換清單都整份重算，同時只能有一個人換
        private static readonly Lock _swapLock = new();

        public static IReadOnlyList<string> Validate()
        {
            var missing = new List<string>();

            void Expect(MemberInfo? member, string name, Func<bool> shapeOk)
            {
                if (member is null || !shapeOk())
                    missing.Add(name);
            }

            Expect(_tools, "AiToolRegistry._tools", () => _tools!.FieldType == typeof(Dictionary<string, IAiTool>));
            Expect(_dataTools, "AiToolRegistry._dataTools", () => _dataTools!.FieldType == typeof(Dictionary<string, IAiTool>));
            Expect(_allTools, "AiToolRegistry._allTools", () => _allTools!.FieldType == typeof(IAiTool[]));
            Expect(_coreSchemas, "AiToolRegistry._coreSchemas", () => _coreSchemas!.FieldType == typeof(List<JsonElement>));
            Expect(_buildSchema, "AiToolRegistry.BuildSchema", () =>
                _buildSchema!.ReturnType == typeof(JsonElement)
                && _buildSchema.GetParameters() is [{ ParameterType: var p }] && p == typeof(IAiTool));
            Expect(_buildIndex, "DataToolSearchService.BuildIndexInternalAsync", () =>
                _buildIndex!.ReturnType == typeof(Task) && _buildIndex.GetParameters().Length == 0);

            return missing;
        }

        public static void ReplaceTools(AiToolRegistry registry, IReadOnlyCollection<string> remove, IReadOnlyList<IAiTool> add)
        {
            lock (_swapLock)
            {
                var old = (Dictionary<string, IAiTool>)_tools!.GetValue(registry)!;
                var tools = new Dictionary<string, IAiTool>(old, old.Comparer);

                foreach (var name in remove)
                    tools.Remove(name);
                foreach (var tool in add)
                    tools[tool.Name] = tool;

                var dataTools = tools.Where(kv => kv.Value.IsDataTool).ToDictionary(kv => kv.Key, kv => kv.Value);
                var allTools = tools.Values.ToArray();
                var coreSchemas = tools.Values
                                       .Where(t => !t.IsDataTool)
                                       .Select(t => (JsonElement)_buildSchema!.Invoke(null, [t])!)
                                       .ToList();

                // 對話進行中的 session 會讀這些欄位，所以每個都換成新物件，不改原本的集合
                _coreSchemas!.SetValue(registry, coreSchemas);
                _dataTools!.SetValue(registry, dataTools);
                _allTools!.SetValue(registry, allTools);
                _tools.SetValue(registry, tools);
            }
        }

        public static async Task RebuildDataToolIndexAsync(DataToolSearchService search, bool waitForStartupBuild, CancellationToken ct)
        {
            // bot 上線時會在背景建第一次索引。先等它建完，免得它拿舊清單建的結果蓋掉這次的。
            // 語意模型載入失敗時索引永遠不會好，所以只有第一次要等
            var waited = TimeSpan.Zero;
            while (waitForStartupBuild && !search.IsReady && waited < _indexReadyTimeout)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                waited += TimeSpan.FromSeconds(1);
            }

            await (Task)_buildIndex!.Invoke(search, null)!;
        }
    }
}
