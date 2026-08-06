using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Teddit
{
    /// <summary>
    /// Loads a patch config YAML file of the form:
    ///   some_id:
    ///     fieldName: value
    /// Keys starting with "_" are treated as comments and skipped.
    /// </summary>
    internal static class PatchConfig
    {
        public static Dictionary<string, Dictionary<string, JToken>> Load(string path)
        {
            var result = YamlHelper.LoadPatch(path);
            Plugin.Log.LogInfo($"[PatchConfig] {Path.GetFileName(path)}: {result.Count} entries.");
            return result;
        }
    }
}
