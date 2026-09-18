using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Utils;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using Newtonsoft.Json.Linq;
using RestSharp;
using Serilog;

namespace FModel.ViewModels.ApiEndpoints;

public class DynamicApiEndpoint : AbstractApiProvider
{
    public DynamicApiEndpoint(RestClient client) : base(client) { }

    public const string DefaultAesJsonPath = "$.['mainKey','dynamicKeys']";

    public async Task<AesResponse> GetAesKeysAsync(CancellationToken token, string url, string path)
    {
        var body = await GetRequestBody(token, url).ConfigureAwait(false);
        return ParseAesKeys(body, path);
    }

    public AesResponse GetAesKeysFromFile(string filePath, string path)
    {
        return ParseAesKeys(ReadLocalJson(filePath), path);
    }

    public static AesResponse ParseAesKeys(JToken body, string path)
    {
        if (string.IsNullOrEmpty(path))
            path = DefaultAesJsonPath;

        var tokens = body.SelectTokens(path).ToArray();
        var ret = new AesResponse { MainKey = Helper.FixKey(tokens.ElementAtOrDefault(0)?.ToString()) };
        if (tokens.ElementAtOrDefault(1) is JArray dynamicKeys)
        {
            foreach (var dynamicKey in dynamicKeys)
            {
                if (dynamicKey["guid"] is not { } guid || dynamicKey["key"] is not { } key)
                    continue;

                ret.DynamicKeys.Add(new DynamicKey
                {
                    Name = dynamicKey["name"]?.ToString(),
                    Guid = guid.ToString(),
                    Key = Helper.FixKey(key.ToString())
                });
            }
        }

        if (ret.IsValid)
            return ret;

        // Saved FModel / API dumps already look like { mainKey, dynamicKeys }.
        if (body is JObject obj)
        {
            if (obj["mainKey"] != null)
                ret.MainKey = Helper.FixKey(obj["mainKey"]?.ToString());
            if (obj["dynamicKeys"] is JArray dumpedKeys)
            {
                ret.DynamicKeys.Clear();
                foreach (var dynamicKey in dumpedKeys)
                {
                    if (dynamicKey["guid"] is not { } guid || dynamicKey["key"] is not { } key)
                        continue;

                    ret.DynamicKeys.Add(new DynamicKey
                    {
                        Name = dynamicKey["name"]?.ToString(),
                        Guid = guid.ToString(),
                        Key = Helper.FixKey(key.ToString())
                    });
                }
            }
        }

        return ret;
    }

    public AesResponse GetAesKeys(CancellationToken token, string url, string path)
    {
        return GetAesKeysAsync(token, url, path).GetAwaiter().GetResult();
    }

    public async Task<MappingsResponse[]> GetMappingsAsync(CancellationToken token, string url, string path)
    {
        var body = await GetRequestBody(token, url).ConfigureAwait(false);
        var tokens = body.SelectTokens(path).ToArray();

        var ret = new MappingsResponse[] { new() };
        ret[0].Url = tokens.ElementAtOrDefault(0)?.ToString();
        if (tokens.ElementAtOrDefault(1) is not { } fileName)
            fileName = ret[0].Url?.SubstringAfterLast("/");
        ret[0].FileName = fileName.ToString();
        return ret;
    }

    public MappingsResponse[] GetMappings(CancellationToken token, string url, string path)
    {
        return GetMappingsAsync(token, url, path).GetAwaiter().GetResult();
    }

    public async Task<JToken> GetRequestBody(CancellationToken token, string url)
    {
        if (TryGetLocalPath(url, out var localPath))
        {
            Log.Information("[FILE] '{Resource}'", localPath);
            return ReadLocalJson(localPath);
        }

        var request = new FRestRequest(url)
        {
            Interceptors = [_interceptor]
        };
        var response = await _client.ExecuteAsync(request, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}'", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString);
        return response.IsSuccessful && !string.IsNullOrEmpty(response.Content) ? JToken.Parse(response.Content) : JToken.Parse("{}");
    }

    public static bool TryGetLocalPath(string url, out string localPath)
    {
        localPath = url;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (url.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase))
            localPath = System.Uri.UnescapeDataString(new System.Uri(url).LocalPath);

        return File.Exists(localPath);
    }

    private static JToken ReadLocalJson(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            return string.IsNullOrWhiteSpace(content) ? JToken.Parse("{}") : JToken.Parse(content);
        }
        catch (System.Exception e)
        {
            Log.Error(e, "Failed to read local AES json '{File}'", filePath);
            return JToken.Parse("{}");
        }
    }
}
