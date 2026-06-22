using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Modules;


namespace ComplicatedMarketBoard.API;

public class Universalis
{

    public Universalis()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(P.Config.RequestTimeout),
        };
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }


    // -------------------------------- http client --------------------------------
    private const string Host = "https://universalis.app";
    private HttpClient httpClient;

    public void ReloadHttpClient()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(P.Config.RequestTimeout),
        };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "ComplicatedMarketBoard/1.0 (Dalamud; FFXIV)");
    }


    // -------------------------------- http methods --------------------------------
    public async Task<UniversalisResponse> GetDataAsync(PriceChecker.GameItem gameItem)
    {
        return await GetData(gameItem);
    }

    public async Task<UniversalisResponse> GetData(PriceChecker.GameItem gameItem)
    {
        var customScope = P.Config.CustomMarketScopes.FirstOrDefault(scope => scope.Id == P.Config.selectedCustomScopeId);
        if (customScope is not null)
            return await GetCustomScopeData(gameItem, customScope);

        return await GetDataForTarget(gameItem, gameItem.TargetRegion);
    }

    private async Task<UniversalisResponse> GetCustomScopeData(PriceChecker.GameItem gameItem, CustomMarketScope customScope)
    {
        var targets = P.MainWindow.ScopeCatalog.BuildQueryTargets(customScope.IncludedScopes);
        if (targets.Count == 0)
            return new UniversalisResponse { Status = UniversalisResponseStatus.InvalidData };

        var responses = new List<UniversalisResponse>();
        foreach (var target in targets)
        {
            var response = await GetDataForTarget(gameItem, target);
            if (response.Status != UniversalisResponseStatus.Success)
            {
                Service.Log.Warning($"[Universalis] Custom scope '{customScope.Name}' failed while fetching '{target}'.");
                return response;
            }

            responses.Add(response);
        }

        return MergeCustomScopeResponses(gameItem, customScope, responses);
    }

    private static UniversalisResponse MergeCustomScopeResponses(PriceChecker.GameItem gameItem, CustomMarketScope customScope, List<UniversalisResponse> responses)
    {
        var mergedWorldOutOfDate = new Dictionary<string, double>();
        var mergedWorldUploadTimes = new Dictionary<string, long>();

        foreach (var response in responses)
        {
            foreach (var freshness in response.WorldOutOfDate)
            {
                if (!mergedWorldOutOfDate.TryGetValue(freshness.Key, out var existing) || freshness.Value < existing)
                    mergedWorldOutOfDate[freshness.Key] = freshness.Value;
            }

            foreach (var uploadTime in response.WorldUploadTimes)
            {
                if (!mergedWorldUploadTimes.TryGetValue(uploadTime.Key, out var existing) || uploadTime.Value > existing)
                    mergedWorldUploadTimes[uploadTime.Key] = uploadTime.Value;
            }
        }

        return new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            ItemId = (ulong)gameItem.Id,
            IsCrossWorld = true,
            WorldOutOfDate = mergedWorldOutOfDate,
            FetchTime = responses.Min(response => response.FetchTime),
            LatestUploadTime = mergedWorldUploadTimes.Count > 0 ? mergedWorldUploadTimes.Values.Max() : responses.Max(response => response.LatestUploadTime),
            WorldUploadTimes = mergedWorldUploadTimes,
            UnitsForSale = responses.Sum(response => response.UnitsForSale),
            AveragePrice = AverageWeightedByListings(responses, response => response.AveragePrice),
            AveragePriceNq = AverageWeightedByListings(responses, response => response.AveragePriceNq),
            AveragePriceHq = AverageWeightedByListings(responses, response => response.AveragePriceHq),
            Velocity = responses.Sum(response => response.Velocity),
            VelocityNq = responses.Sum(response => response.VelocityNq),
            VelocityHq = responses.Sum(response => response.VelocityHq),
            Listings = responses
                .SelectMany(response => response.Listings)
                .OrderBy(listing => listing.PricePerUnit)
                .ThenBy(listing => listing.Quantity)
                .Take(P.Config.UniversalisListings)
                .ToList(),
            Entries = responses
                .SelectMany(response => response.Entries)
                .OrderByDescending(entry => entry.Timestamp)
                .Take(P.Config.UniversalisEntries)
                .ToList(),
            ScopeName = customScope.Name,
        };
    }

    private static double AverageWeightedByListings(List<UniversalisResponse> responses, Func<UniversalisResponse, double> selector)
    {
        var totalListings = responses.Sum(response => response.Listings.Count);
        if (totalListings == 0)
            return responses.Average(selector);

        return responses.Sum(response => selector(response) * response.Listings.Count) / totalListings;
    }

    private async Task<UniversalisResponse> GetDataForTarget(PriceChecker.GameItem gameItem, string targetName)
    {
        try
        {
            // build url
            var _hq = P.Config.UniversalisHqOnly ? "&hq=1" : "";
            var targetRegion = P.MainWindow.ScopeCatalog.NormalizeForUniversalis(targetName);
            var API_URL = new UriBuilder($"{Host}/api/v2/{targetRegion}/{gameItem.Id}?listings={P.Config.UniversalisListings}&entries={P.Config.UniversalisEntries}{_hq}").Uri.ToString();

            // get response
            Service.Log.Info($"[Universalis] Fetch: {API_URL}");
            var response = await httpClient.GetAsync(API_URL);
            if (response.IsSuccessStatusCode == false)
            {
                Service.Log.Warning($"[Universalis] HTTP request not successful: {response.StatusCode}");
                return new UniversalisResponse { Status = UniversalisResponseStatus.ServerError };
            }

            // decode response
            var data = await response.Content.ReadFromJsonAsync<MarketDataCurrent>();
            if (data is null)
            {
                Service.Log.Warning($"[Universalis] Parse JSON failed");
                return new UniversalisResponse { Status = UniversalisResponseStatus.InvalidData };
            }

            // update if there's world data
            var fetchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var worldUpdatedData = new Dictionary<string, double>();
            var worldUploadTimes = new Dictionary<string, long>();
            if (data.WorldUploadTimes.Count > 0)
            {
                var orderedWorldUploadTimes = data.WorldUploadTimes.OrderByDescending(w => w.Value).ToList();
                foreach (var i in orderedWorldUploadTimes)
                {
                    var worldRow = Data.WorldSheet.GetRow(uint.Parse(i.Key));
                    // debug: if (worldRow is null) continue;
                    var worldName = worldRow.Name.ToString();
                    var hours = (fetchTime - i.Value) / 1000d / 3600d;
                    worldUpdatedData.Add(worldName, hours);
                    worldUploadTimes.Add(worldName, i.Value);
                }
            }
            else
            {
                worldUpdatedData.Add(targetName, (fetchTime - data.LastUploadTime) / 1000d / 3600d);
                worldUploadTimes.Add(targetName, data.LastUploadTime);
                foreach (var listing in data.Listings)
                {
                    if (string.IsNullOrWhiteSpace(listing.WorldName))
                        listing.WorldName = targetName;
                }

                foreach (var entry in data.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.WorldName))
                        entry.WorldName = targetName;
                }
            }

            var universalisResponse = new UniversalisResponse
            {
                Status = UniversalisResponseStatus.Success,
                ItemId = data.ItemId,
                IsCrossWorld = data.WorldUploadTimes.Count > 0,
                WorldOutOfDate = worldUpdatedData,
                FetchTime = fetchTime,
                LatestUploadTime = worldUploadTimes.Count > 0 ? worldUploadTimes.Values.Max() : data.LastUploadTime,
                WorldUploadTimes = worldUploadTimes,
                UnitsForSale = data.UnitsForSale,
                AveragePrice = data.AveragePrice,
                AveragePriceNq = data.AveragePriceNq,
                AveragePriceHq = data.AveragePriceHq,
                Velocity = data.Velocity,
                VelocityNq = data.VelocityNq,
                VelocityHq = data.VelocityHq,
                Listings = data.Listings,
                Entries = data.Entries,
                ScopeName = targetName,
            };
            Service.Log.Debug($"[Universalis] UniversalisResponse: {JsonSerializer.Serialize(universalisResponse)}");

            return universalisResponse;
        }
        catch (TaskCanceledException ex)
        {
            Service.Log.Warning($"[Universalis] HTTP request cancelled by user configured timeout");
            Service.Log.Debug(ex.Message);
            return new UniversalisResponse { Status = UniversalisResponseStatus.UserCancellation };
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"[Universalis] Unknown error: {ex.Message}");
            return new UniversalisResponse { Status = UniversalisResponseStatus.UnknownError };
        }
    }
}
