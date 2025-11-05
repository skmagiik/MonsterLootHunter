using Dalamud.Plugin.Services;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;
using MonsterLootHunter.Data;
using MonsterLootHunter.Utils;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace MonsterLootHunter.Clients
{
    public partial class WikiParser()
    {
        private delegate IEnumerable<LootDrops> Processors(HtmlNode node);

        [GeneratedRegex(@"(\d+\.?\d*)", RegexOptions.Compiled)]
        private static partial Regex CoordinatesRegex();

        [GeneratedRegex("\\d+", RegexOptions.Compiled)]
        private static partial Regex LevelRegex();

        [GeneratedRegex(@"(\d{1,2}:\d{1,2}\s(?:am|pm))", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex GatherTimeRegex();

        [GeneratedRegex("(?:patch)|(?:tree)|(?:logging)|(?:quarry)|(?:harves)|(?:mining)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex LocationNameRegex();

        [GeneratedRegex(@"(?:-\s+)(.+)(?:\s+\()", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex LocationNameAlternativeRegex();

        [GeneratedRegex(@"&#.+;")]
        private static partial Regex UnicodeCharacterRemovalRegex();

        public static Task<LootData> ParseResponse(HtmlDocument document, LootData lootData, IPluginLog pluginLog)
        {
            //pluginLog.Info("document: {0}", document.Text);
            var jsonData= document.DocumentNode.QuerySelector("body>pre");
            //pluginLog.Info("json_data: {0}", jsonData.InnerText);
            var doc = JsonDocument.Parse(jsonData.InnerText);
            var parse = doc.RootElement.GetProperty("parse");
            var text = parse.GetProperty("text").GetProperty("*").ToString();
            //pluginLog.Info("Response Text: {0}", text);


            var hap = new HtmlAgilityPack.HtmlDocument();
            hap.LoadHtml(HttpUtility.HtmlDecode(text));
            //pluginLog.Info("new HAP: {0}", hap.Text);
            var bodyContent = hap.DocumentNode.QuerySelector("div.mw-parser-output");
            pluginLog.Info("div.mw-content-ltr div.mw-parser-output: {0}", bodyContent);
            var concurrentList = new ConcurrentBag<LootDrops>();
            Parallel.Invoke(() => { AddToBag(GetDutyDrops, bodyContent); },
                            () => { AddToBag(GetMonsterDropsFromTable, bodyContent); },
                            () => { AddToBag(GetPossibleRecipe, bodyContent); },
                            () => { AddToBag(GetPossibleTreasureHunts, bodyContent); },
                            () => { AddToBag(GetPossibleDesynthesis, bodyContent); },
                            () => { AddToBag(GetPossibleGathering, bodyContent); },
                            () => { AddToBag(GetPossibleGatheringTable, bodyContent); },
                            () => { lootData.LootPurchaseLocations.AddRange(GetVendorPurchases(bodyContent, pluginLog)); });

            lootData.LootLocations.AddRange(concurrentList.AsEnumerable());
            return Task.FromResult(lootData);

            void AddToBag(Processors processor, HtmlNode node)
            {
                foreach (var gather in processor(node)) concurrentList.Add(gather);
            }
        }

        private static IEnumerable<LootDrops> GetDutyDrops(HtmlNode node)
        {
            var dutyHeader = node.QuerySelector("h3").QuerySelector("span#Duties");
            if (dutyHeader is null || !dutyHeader.InnerText.Contains("dut", StringComparison.OrdinalIgnoreCase))
                return [];

            var dutyList = node.QuerySelector("ul")?.QuerySelectorAll("li");
            var dutyListSanitized = dutyList?.Select(el => el.InnerText).ToList();
            if (dutyListSanitized is null)
                return [];

            var dutyDrops = new ConcurrentBag<LootDrops>();
            Parallel.ForEach(dutyListSanitized, duty =>
            {
                dutyDrops.Add(new LootDrops
                {
                    MobName = "Duty",
                    MobLocation = UnicodeCharacterRemovalRegex().Replace(duty, string.Empty),
                    MobFlag = string.Empty,
                    MobLevel = string.Empty
                });
            });

            return dutyDrops.AsEnumerable();
        }

        private static IEnumerable<LootDrops> GetMonsterDropsFromTable(HtmlNode node)
        {
            var dropList = node.QuerySelector("table.item tbody")?.QuerySelectorAll("tr").ToList();
            if (dropList is null || dropList.Count == 0)
                return [];

            dropList.RemoveAt(0);

            var dutyDrops = new ConcurrentBag<LootDrops>();
            Parallel.ForEach(dropList, drops =>
            {
                var info = drops.QuerySelectorAll("td").ToList();
                var flagWasParsed = CoordinatesRegex().TryMatches(info.TryGet(nodes => nodes.Last().InnerText), out var flagParsed);

                dutyDrops.Add(new LootDrops
                {
                    MobName = info.TryGet(nodes => nodes[0].InnerText).Replace("\n", ""),
                    MobLocation = info.TryGet(nodes => nodes.Last().InnerText).Split("(")[0].Replace("\n", "").TrimEnd(),
                    MobFlag = flagWasParsed ? $"({flagParsed[0]},{flagParsed[1]})" : string.Empty,
                    MobLevel = info.TryGet(nodes => nodes[1].InnerText).Replace("\n", ""),
                });
            });

            return dutyDrops.AsEnumerable();
        }

        private static IEnumerable<LootPurchase> GetVendorPurchases(HtmlNode node, IPluginLog pluginLog)
        {
            var purchaseHeader = node.QuerySelectorAll("h3").ToList();
            var purchaseTopHeader = node.QuerySelectorAll("h2").ToList();

            if (!purchaseHeader.Any(n => n.InnerText.Contains("Purchase")))
            {
                pluginLog.Info("Unable to find Purchase or span#Purchase etc...");
                return [];
            }

            var purchaseList = node.QuerySelector("table.npc tbody")?.QuerySelectorAll("tr").ToList();
            if (purchaseList is null || purchaseList.Count == 0)
            {
                pluginLog.Info("purchaseList is empty...");
                return [];
            }

            purchaseList.RemoveAt(0);

            var vendors = new ConcurrentBag<LootPurchase>();

            //pluginLog.Info("{0}", purchaseList.ToString());
            Parallel.ForEach(purchaseList, vendorNode =>
            {
                var vendor = vendorNode.QuerySelectorAll("td").ToList();
                var locationAndFlag = vendor.TryGet(nodes => nodes[1].InnerText).Split("(");

                vendors.Add(new LootPurchase
                {
                    Vendor = vendor.TryGet(nodes => nodes[0].InnerText).Replace("\n", ""),
                    Location = locationAndFlag[0].Replace("\n", "").TrimEnd(),
                    FlagPosition = $"({locationAndFlag[1]}".Replace("\n", ""),
                    Cost = vendor.TryGet(nodes => nodes[3].InnerText).Replaces("&#160;", string.Empty, "\n", string.Empty),
                    CostType = vendor.TryGet(nodes => nodes[3].QuerySelector("span a").Attributes["title"].Value).Replace("\n", "").TrimEnd()
                });
            });

            return vendors.AsEnumerable();
        }

        private static LootDrops[] GetPossibleRecipe(HtmlNode node)
        {
            var recipeBox = node.QuerySelector("div.recipe-box");
            if (recipeBox is null)
                return [];

            var recipeData = recipeBox.QuerySelector("div.wrapper").QuerySelectorAll("dd").ToList();
            return
            [
                new LootDrops
                {
                    MobName = $"Crafter Class: {recipeData.TryGet(nodes => nodes[2].QuerySelectorAll("a").ToList()[1].InnerText)}",
                    MobLocation = string.Empty,
                    MobFlag = string.Empty,
                    MobLevel = recipeData.TryGet(nodes => nodes[3].InnerText),
                }
            ];
        }

        private static IEnumerable<LootDrops> GetPossibleTreasureHunts(HtmlNode node)
        {
            var pageHeaders = node.QuerySelectorAll("h3").ToList();
            if (pageHeaders.All(hNode => hNode.QuerySelector("span#Treasure_Hunt") is null))
                return [];

            var treasureHeader = pageHeaders.First(hNode => hNode.QuerySelector("span#Treasure_Hunt") is not null);
            var treasureMapList = treasureHeader.NextSibling.NextSibling.QuerySelectorAll("li");

            return treasureMapList.Select(treasureMap => new LootDrops
            {
                MobName = "Treasure Map",
                MobLocation = treasureMap.QuerySelectorAll("a").ToList().Last().InnerText,
                MobFlag = string.Empty,
                MobLevel = string.Empty,
            });
        }

        private static IEnumerable<LootDrops> GetPossibleDesynthesis(HtmlNode node)
        {
            var pageHeaders = node.QuerySelectorAll("h3").ToList();
            if (pageHeaders.All(hNode => hNode.QuerySelector("span#Desynthesis") is null &&
                                         hNode.QuerySelector("span#_Desynthesis") is null))
                return [];

            var desynthesisHeader = pageHeaders.First(hNode => hNode.QuerySelector("span#Desynthesis") is not null ||
                                                               hNode.QuerySelector("span#_Desynthesis") is not null);
            var desynthesisList = desynthesisHeader.NextSibling.NextSibling.QuerySelectorAll("li");
            return desynthesisList.Select(treasureMap => new LootDrops
            {
                MobName = "Desynthesis",
                MobLocation = treasureMap.QuerySelectorAll("a").ToList().Last().InnerText,
                MobFlag = string.Empty,
                MobLevel = string.Empty,
            });
        }

        private static IEnumerable<LootDrops> GetPossibleGathering(HtmlNode node)
        {
            var pageHeaders = node.QuerySelectorAll("h3").ToList();
            if (pageHeaders.All(hNode => hNode.QuerySelector("span#Gathering") is null &&
                                         hNode.QuerySelector("span#Gathered") is null))
                return [];

            var gatherHeader = pageHeaders.First(hNode => hNode.QuerySelector("span#Gathering") is not null ||
                                                          hNode.QuerySelector("span#Gathered") is not null);
            var gatherList = gatherHeader.NextSibling.NextSibling.QuerySelectorAll("li").ToList();

            if (gatherList.Count != 0)
            {
                if (!gatherList.First().InnerText.Contains("Reduction"))
                    return Gathering(gatherList);

                gatherList.RemoveAt(0);
                return AetherialReduction(gatherList);
            }

            var gatherableInfo = gatherHeader.NextSibling.NextSibling;
            return gatherableInfo is not null && gatherableInfo.Name != "table" ? Gathered(gatherableInfo) : [];

            IEnumerable<LootDrops> Gathered(HtmlNode gatheredNode)
            {
                var anchors = gatheredNode.QuerySelectorAll("a").ToList();
                var flag = anchors.LastOrDefault()?.NextSibling.InnerText ?? string.Empty;
                var flagParsed = CoordinatesRegex().Matches(flag);
                var gatherTime = GatherTimeRegex().Matches(node.InnerText ?? string.Empty).FirstOrDefault()?.Value ?? string.Empty;
                var locationName = anchors.Count > 1 ? anchors.First(text => !LocationNameRegex().Match(text.InnerText).Success)?.InnerText : string.Empty;
                return new[]
                {
                    new LootDrops
                    {
                        MobName = anchors.First().InnerText,
                        MobLocation = $"{locationName}-{anchors.Last().InnerText}-{gatherTime}",
                        MobFlag = flagParsed.Count == 2 ? $"({flagParsed[0].Value},{flagParsed[1].Value})" : string.Empty,
                        MobLevel = LevelRegex().Matches(gatheredNode.ChildNodes.First().InnerText).FirstOrDefault()?.Value ?? string.Empty,
                    }
                };
            }

            IEnumerable<LootDrops> Gathering(IEnumerable<HtmlNode> gatheringList) =>
                from gatherNode in gatheringList
                let anchors = gatherNode.QuerySelectorAll("a").ToList()
                let flag = anchors.LastOrDefault()?.NextSibling.InnerText ?? string.Empty
                let flagParsed = CoordinatesRegex().Matches(flag)
                let locationName = anchors.FirstOrDefault(text => !LocationNameRegex().Match(text.InnerText).Success)?.InnerText
                let locationAlternativeName = locationName is null ? LocationNameAlternativeRegex().Match(anchors.First().NextSibling.InnerText).Groups[1].Value : string.Empty
                select new LootDrops
                {
                    MobName = anchors.First().InnerText,
                    MobLocation = $"{locationName ?? locationAlternativeName}-{anchors.Last().InnerText}",
                    MobFlag = flagParsed.Count == 2 ? $"({flagParsed[0].Value},{flagParsed[1].Value})" : string.Empty,
                    MobLevel = LevelRegex().Matches(gatherNode.ChildNodes.First().InnerText).FirstOrDefault()?.Value ?? string.Empty,
                };

            IEnumerable<LootDrops> AetherialReduction(IEnumerable<HtmlNode> reductionList) =>
                reductionList.Select(htmlNode => htmlNode.QuerySelectorAll("a").Last())
                             .Select(itemName => new LootDrops { MobName = itemName.InnerText, MobLocation = "Aetherial Reduction", MobFlag = string.Empty, MobLevel = string.Empty });
        }

        private static IEnumerable<LootDrops> GetPossibleGatheringTable(HtmlNode node)
        {
            var gatheringTable = node.QuerySelector("table.gathering-role");
            if (gatheringTable is null)
                return [];

            var gatheringList = gatheringTable.QuerySelector("tbody").QuerySelectorAll("tr").ToList();
            gatheringList.RemoveAt(0);

            return from gatherNode in gatheringList
                   select gatherNode.QuerySelectorAll("td").ToList()
                   into columns
                   let flagParsed = CoordinatesRegex().Matches(columns.Last().InnerText)
                   select new LootDrops
                   {
                       MobName = columns.TryGet(nodes => nodes[0].ChildNodes[1].InnerText),
                       MobLocation = $"{columns.TryGet(nodes => nodes[1].QuerySelectorAll("a").ToList()[0].InnerText)} - {columns.TryGet(nodes => nodes[1].QuerySelectorAll("a").ToList()[1].InnerText)}",
                       MobFlag = flagParsed.Count == 2 ? $"({flagParsed[0].Value},{flagParsed[1].Value})" : string.Empty,
                       MobLevel = columns.TryGet(nodes => nodes[2].ChildNodes[0].InnerText),
                   };
        }
    }
}
