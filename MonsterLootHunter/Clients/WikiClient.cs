using Dalamud.Plugin.Services;
using FlareSolverrSharp;
using HtmlAgilityPack;
using MonsterLootHunter.Data;
using MonsterLootHunter.Utils;
using System.Text.Json;

namespace MonsterLootHunter.Clients;

public class WikiClient(IPluginLog pluginLog)
{
    private readonly HtmlWeb _webClient = new();
    private static FlareSolverrSharp.ClearanceHandler handler = new ClearanceHandler("http://localhost:8191/");


    public async Task GetLootData(LootData data, CancellationToken cancellationToken)
    {
        if (_itemNameFix.TryGetValue(data.LootName, out var fixedName))
            data.LootName = fixedName;


        HttpClient client = new HttpClient(handler);
        var uri = new UriBuilder(string.Format(PluginConstants.WikiBaseUrl, data.LootName.Replace(" ", "_"))).ToString();
       

        var requestBody = new
        {
            cmd = "request.get",
            url = new UriBuilder(string.Format(PluginConstants.WikiBaseUrl, data.LootName.Replace(" ", "_"))).ToString(),
            maxTimeout = 60000
        };
        var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var httpresponse = await client.PostAsync("http://localhost:8191/v1", content, cancellationToken);
        var responseJson = await httpresponse.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(responseJson);
        var solution = doc.RootElement.GetProperty("solution");
        var response = solution.GetProperty("response").ToString();
        //pluginLog.Info("{0}", response.ToString());
        var hap = new HtmlAgilityPack.HtmlDocument();
        hap.LoadHtml(response);
        try
        {
            await WikiParser.ParseResponse(hap, data, pluginLog).WaitAsync(cancellationToken);
        }
        catch (Exception e)
        {
            pluginLog.Error("{0}\n{1}", e.Message, e.StackTrace ?? string.Empty);
        }
    }

    private readonly Dictionary<string, string> _itemNameFix = new()
    {
        { "Blue Cheese", "Blue Cheese (Item)" },
        { "Gelatin", "Gelatin (Item)" },
        { "Leather", "Leather (Item)" },
        { "Morel", "Morel (Item)" }
    };
}
