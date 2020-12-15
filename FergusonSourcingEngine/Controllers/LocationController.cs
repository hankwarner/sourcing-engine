using System;
using System.Collections.Generic;
using FergusonSourcingCore.Models;
using RestSharp;
using Polly;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using Nager.Date;
using System.Threading.Tasks;

namespace FergusonSourcingEngine.Controllers
{
    public class LocationController
    {
        private ILogger _logger;
        public Locations locations = new Locations();

        public LocationController(ILogger logger)
        {
            _logger = logger;
        }

        public LocationController(){ }


        /// <summary>
        ///     Gets the available locations (local branch logon + all DC's) and builds the Location Dictionary. 
        ///     Values include distance data, location type (DC, Branch, Vendor, etc.), address, and estimated delivery date.
        /// </summary>
        public async Task InitializeLocations(AtgOrderRes atgOrderRes)
        {
            try
            {
                _logger.LogInformation("InitializeLocations start");

                var shipToState = atgOrderRes.shipping.shipTo.state;
                var shipToZip = atgOrderRes.shipping.shipTo.zip?.Substring(0, 5);

                if (string.IsNullOrEmpty(shipToZip) || string.IsNullOrEmpty(shipToState)) 
                    throw new NullReferenceException("Customer shipTo state and zip are required.");

                var sellWarehouse = atgOrderRes.sellWhse ?? "D98 DISTRIBUTION CENTERS";

                locations.LocationDict = await GetLogonLocationData(sellWarehouse);

                _ = ValidateSellWarehouse(atgOrderRes);

                var googleDistanceDataTask = GetGoogleDistanceData(shipToZip);
                var transitDataTask = GetBusinessTransitDays(shipToZip);

                await Task.WhenAll(googleDistanceDataTask, transitDataTask);

                var googleDistanceData = googleDistanceDataTask.Result;
                var transitData = transitDataTask.Result;

                // Combine data from Google and UPS into a single dict
                var distanceData =
                    googleDistanceData.Join(transitData,
                                            dist => dist.Key,
                                            trans => trans.Key,
                                            (dist, trans) =>
                                                new DistanceData(dist.Key, dist.Value, trans.Value.BusinessTransitDays, trans.Value.SaturdayDelivery))
                                        .ToDictionary(d => d.BranchNumber, d => d);

                await ValidateDistanceData(distanceData);

                var addToDictTask = AddDistanceDataToLocationDict(distanceData);
                var prefLocationTask = SetPreferredLocationFlag(shipToState, shipToZip);

                await Task.WhenAll(addToDictTask, prefLocationTask);

                await SortLocations();

                _logger.LogInformation("InitializeLocations finish");
            }
            catch (NullReferenceException ex)
            {
                _logger.LogWarning("Invalid shipping values. {Ex}", ex);
                throw;
            }
            catch (Exception ex)
            {
                var title = "Error in InitializeLocations";
#if RELEASE
                var teamsMessage = new TeamsMessage(title, $"Order Id: {atgOrderRes.atgOrderId}. Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngineFunctions.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
#endif
                _logger.LogError(ex, title);
                throw;
            }
        }


        /// <summary>
        ///     Flags the order as invalid if the sell warehouse provided in the original request is not a valid location.
        /// </summary>
        /// <param name="atgOrderRes">The ATG Order response object that will be written to Cosmos DB.</param>
        public async Task ValidateSellWarehouse(AtgOrderRes atgOrderRes)
        {
            try
            {
                atgOrderRes.validSellWarehouse = locations.LocationDict.Any(l => l.Value.Logon != "D98 DISTRIBUTION CENTERS");
            }
            catch(Exception ex)
            {
                var title = "Error in ValidateSellWarehouse";
                _logger.LogError(title);
#if RELEASE
                var teamsMessage = new TeamsMessage(title, $"Order Id: {atgOrderRes.atgOrderId}. Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngineFunctions.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
#endif
            }
        }


        /// <summary>
        ///     Sorts the location dictionary based on location type and logistics to deliver to the customer's zip code.
        /// </summary>
        public async Task SortLocations()
        {
            locations.LocationDict = locations.LocationDict.OrderByDescending(l => l.Value.IsPreferred)
                .ThenByDescending(l => l.Value.DCLocation)
                .ThenByDescending(l => l.Value.ShipHub)
                .ThenByDescending(l => l.Value.WarehouseManagementSoftware)
                .ThenBy(l => l.Value.EstDeliveryDate)
                .ThenBy(l => l.Value.BusinessDaysInTransit)
                .ThenBy(l => l.Value.Distance)
                .ToDictionary(l => l.Key, l => l.Value);
        }


        /// <summary>
        ///     Returns the Logon of the specified branch number of the selling warehouse.
        /// </summary>
        /// <param name="sellingWarehouse">Branch number of the selling warehouse on the order.</param>
        public string GetBranchLogonID(string sellingWarehouse)
        {
            var retryPolicy = Policy.Handle<Exception>().Retry(5, (ex, count) =>
            {
                var title = "Error in GetBranchLogonID";
                _logger.LogWarning($"{title}. Retrying...");

                if (count == 5)
                {
                    _logger.LogError(ex, title);
#if RELEASE
                    var teamsMessage = new TeamsMessage(title, $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngineFunctions.errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);
#endif
                }
            });

            return retryPolicy.Execute(() =>
            {
                var baseUrl = @"https://service-sourcing.supply.com/api/v2/Location/GetBranchLogonID/";

                var client = new RestClient(baseUrl + sellingWarehouse);

                var request = new RestRequest(Method.GET)
                    .AddHeader("Accept", "application/json")
                    .AddHeader("Content-Type", "application/json");

                var jsonResponse = client.Execute(request).Content;

                if (string.IsNullOrEmpty(jsonResponse))
                    throw new ArgumentNullException("jsonResponse", "GetBranchLogonID returned an empty string");

                var token = JToken.Parse(jsonResponse);

                if (token is JObject)
                    throw new Exception("GetBranchLogonID returned an error response.");

                var branchLogonID = jsonResponse.Replace("\"", "");

                return branchLogonID;
            });
        }


        /// <summary>
        ///     Returns a dictionary of branch number (key) and it's location data (value), including address, location type (DC, Branch, SOD),
        ///     WMS, and Ship Hub status.
        /// </summary>
        /// <param name="sellingWarehouse">Branch number of the selling warehouse on the order.</param>
        public async Task<Dictionary<string, Location>> GetLogonLocationData(string sellingWarehouse)
        {
            var retryPolicy = Policy.Handle<Exception>().Retry(5, onRetry: (ex, count) => 
            {
                var title = "Error in GetLogonLocationData";
                _logger.LogWarning($"{title}. Retrying...");

                if (count == 5)
                {
                    _logger.LogError(ex, title);
#if RELEASE
                    var teamsMessage = new TeamsMessage(title, $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngineFunctions.errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);
#endif
                }
             });

            return await retryPolicy.Execute(async () =>
            {
                var baseUrl = @"https://service-sourcing.supply.com/api/v2/Location/GetLogonLocationData/";

                var client = new RestClient(baseUrl + sellingWarehouse);

                var request = new RestRequest(Method.GET)
                    .AddHeader("Accept", "application/json")
                    .AddHeader("Content-Type", "application/json");

                var locationDataTask = client.ExecuteAsync(request);

                var response = await locationDataTask;
                var jsonResponse = response.Content;

                if (string.IsNullOrEmpty(jsonResponse)) 
                    throw new Exception("Sell warehouse did not return any logons.");

                var parsedResponse = JsonConvert.DeserializeObject<Dictionary<string, Location>>(jsonResponse);

                return parsedResponse;
            });
        }


        /// <summary>
        ///     Gets distance data for all branches within the logon, including distance in miles and days in transit, based on the customer's zip code.
        /// </summary>
        /// <param name="shippingZip">Customer's shipping zip code.</param>
        /// <returns>Dictionary where key is branch number and value is location data.</returns>
        public async Task<Dictionary<string, double>> GetGoogleDistanceData(string shippingZip)

        {
            var retryPolicy = Policy.Handle<Exception>().Retry(10, (ex, count) =>
            {
                var title = "Error in GetDistanceData";
                _logger.LogWarning($"{title}. Retrying...");

                if (count == 10)
                {
                    _logger.LogError(ex, title);
#if RELEASE
                    var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngineFunctions.errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);
#endif
                }
            });

            return await retryPolicy.Execute(async () =>
            {
                var allBranchNumbers = locations.LocationDict.Keys;
                var requestBody = new List<string>(allBranchNumbers);
                var jsonRequest = JsonConvert.SerializeObject(requestBody);

                var baseUrl = @"https://service-sourcing.supply.com/api/v2/DistanceData/GetBranchDistancesByZipCode/";

                shippingZip = shippingZip.Substring(0, 5);
                var client = new RestClient(baseUrl + shippingZip);

                var request = new RestRequest(Method.POST)
                    .AddHeader("Accept", "application/json")
                    .AddHeader("Content-Type", "application/json")
                    .AddParameter("application/json; charset=utf-8", jsonRequest, ParameterType.RequestBody);

                var distanceDataTask = client.ExecuteAsync(request);

                var response = await distanceDataTask;
                var jsonResponse = response.Content;

                var distanceData = JsonConvert.DeserializeObject<Dictionary<string, double>>(jsonResponse);

                if (distanceData == null)
                    throw new Exception("Distance data returned null.");

                return distanceData;
            });
        }


        /// <summary>
        ///     Gets UPS business days in transit for all branches within the logon based on the customer's zip code.
        /// </summary>
        /// <param name="shippingZip">Customer's shipping zip code.</param>
        /// <returns>Dictionary where key is branch number and value is UPSTransitData.</returns>
        public async Task<Dictionary<string, UPSTransitData>> GetBusinessTransitDays(string shippingZip)

        {
            var retryPolicy = Policy.Handle<Exception>().Retry(10, (ex, count) =>
            {
                var title = "Error in GetBusinessTransitDays";
                _logger.LogWarning($"{title}. Retrying...");

                if (count == 10)
                {
                    _logger.LogError(ex, title);
#if RELEASE
                    var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngineFunctions.errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);
#endif
                }
            });

            return await retryPolicy.Execute(async () =>
            {
                var allBranchNumbers = locations.LocationDict.Keys;
                var requestBody = new List<string>(allBranchNumbers);
                var jsonRequest = JsonConvert.SerializeObject(requestBody);

                var baseUrl = @"https://service-sourcing.supply.com/api/v2/DistanceData/transit/";

                shippingZip = shippingZip.Substring(0, 5);
                var client = new RestClient(baseUrl + shippingZip);

                var request = new RestRequest(Method.POST)
                    .AddHeader("Accept", "application/json")
                    .AddHeader("Content-Type", "application/json")
                    .AddParameter("application/json; charset=utf-8", jsonRequest, ParameterType.RequestBody);

                var transitDataTask = client.ExecuteAsync(request);

                var response = await transitDataTask;
                var jsonResponse = response.Content;

                var transitData = new Dictionary<string, UPSTransitData>();

                try
                {
                    transitData = JsonConvert.DeserializeObject<Dictionary<string, UPSTransitData>>(jsonResponse);
                }
                // If this ex is thrown, it means the business days in transit do not exist yet and it timed out while calling UPS.
                catch (JsonReaderException ex)
                {
                    _logger.LogWarning(ex, "Error parsing transit data response.");
                    // Wait 10 seconds for DB to write all of the days in transit values before calling again.
                    Thread.Sleep(10000);
                    throw;
                }

                if (transitData == null)
                    throw new Exception("Transit data returned null.");

                return transitData;
            });
        }


        /// <summary>
        ///     Checks that each location has a value for DistanceFromZip and BusinessTransitDays. If any single location is missing a 
        ///     data piece, zero out that data piece for all locations so it will not affect sorting. For example, if BusinessTransitDays is 0 for
        ///     a single location, zero out all locations so that sorting will go by DistanceFromZip.
        /// </summary>
        /// <param name="distanceData">Response object from the distance data microservice. Key is branch number and value is location data.</param>
        public async Task ValidateDistanceData(Dictionary<string, DistanceData> distanceData)
        {
            if (distanceData.Any(d => d.Value.DistanceFromZip == null || d.Value.DistanceFromZip == 0))
            {
                foreach (var dist in distanceData) { dist.Value.DistanceFromZip = 0; }
            }

            if (distanceData.Any(d => d.Value.BusinessTransitDays == 0))
            {
                foreach (var dist in distanceData) { dist.Value.BusinessTransitDays = 0; }
            }

            if (distanceData.Any(d => d.Value.DistanceFromZip == 0 && d.Value.BusinessTransitDays == 0))
            {
                var errMsg = "Sourcing cannot be performed because distance data and business days in transit are missing for all locations.";
                _logger.LogWarning(errMsg);
                throw new Exception(errMsg);
            }
        }


        /// <summary>
        ///     Adds the distance in miles from the customer's shipping zip to each value in the location dictionary. If the location does not
        ///     exist in the location dict, it will not be added.
        /// </summary>
        /// <param name="distanceDataDict">Dictionary where the key is the branch number and value is distance data, including distance in miles from destination and days in transit.</param>
        public async Task AddDistanceDataToLocationDict(Dictionary<string, DistanceData> distanceDataDict)
        {
            try
            {
                foreach(var line in distanceDataDict)
                {
                    var branchNumber = line.Key;
                    var distanceData = line.Value;
                    var hasExistingDictEntry = locations.LocationDict.TryGetValue(branchNumber, out Location locationData);

                    if (hasExistingDictEntry)
                    {
                        locationData.Distance = distanceData.DistanceFromZip;
                        locationData.BusinessDaysInTransit = distanceData.BusinessTransitDays;

                        if(locationData.BusinessDaysInTransit.HasValue && !string.IsNullOrEmpty(locationData.FedExESTCutoffTimes))
                        {
                            locationData.EstShipDate = await GetEstShipDate(locationData, DateTime.Now);

                            locationData.EstDeliveryDate = await GetEstDeliveryDate(locationData);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var title = "Exception in AddDistanceDataToLocationDict";
#if RELEASE
                var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngineFunctions.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
#endif
                _logger.LogError(ex, title);
                throw;
            }
        }


        /// <summary>
        ///     Determines the date that the shipment will go out if placed right now at the given location. Calculation is based on the cutoff time
        ///     and processing time of the given location.
        /// </summary>
        /// <param name="location">The location data object. Must include the cutoff time and processing time.</param>
        /// <returns>Estimated date that the package will be shipped from the location. Time is set to 0:00.</returns>
        public async Task<DateTime> GetEstShipDate(Location location, DateTime startDate)
        {
            // Cutoff times are all in EST
            var locationCutoffTime = location.FedExESTCutoffTimes;
            DateTime.TryParse(locationCutoffTime, out DateTime cutoff);

            var easternStandardTime = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var currentTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternStandardTime);

            var isPastCutoffTime = currentTime > cutoff;
            var processingDays = location.ProcessingTime;

            // If past cutoff time, add 1 day to the scheduled ship date. Take the max of processing days and cutoff time days to add, do not combine them.
            var daysToAdd = isPastCutoffTime ? Math.Max(1, processingDays) : processingDays;

            var estShipDate = startDate.AddDays(daysToAdd).Date;

            // Adjust for holidays and weekends
            while (IsHoliday(estShipDate) || IsWeekend(estShipDate, location.SaturdayDelivery))
            {
                estShipDate = estShipDate.AddDays(1);
            }

            return estShipDate;
        }


        /// <summary>
        ///     Determines the estimate delivery date of a package based on ship date, time in transit, holidays and weekends.
        /// </summary>
        /// <param name="location">The location data object. Must include est ship date, business days in transit, and saturday delivery indictor.</param>
        /// <returns>Estimated date that the package will be delivered. Time is set to 0:00.</returns>
        public async Task<DateTime> GetEstDeliveryDate(Location location)
        {
            // Ship date does not count as a transit day
            var startDate = location.EstShipDate.AddDays(1);
            var estDeliveryDate = location.EstShipDate.AddDays(Convert.ToDouble(location.BusinessDaysInTransit));

            // Check if any of the days in transit are weekend or holidays. If so, add an extra day
            for (var date = startDate; date <= estDeliveryDate; date = date.AddDays(1))
            {
                if (IsWeekend(date, location.SaturdayDelivery) || IsHoliday(date))
                {
                    estDeliveryDate = estDeliveryDate.AddDays(1);
                }
            }

            // Est delivery date = EstShipDate + weekends + holidays + business days in transit
            return estDeliveryDate;
        }


        /// <summary>
        ///     Uses the Nager.Date package to determine if given date is a US public holiday.
        ///     List of US holidays can be found here: https://date.nager.at/PublicHoliday/Country/US
        /// </summary>
        /// <param name="date">The date to check.</param>
        /// <returns>true if the given date is a US public holiday.</returns>
        public bool IsHoliday(DateTime date)
        {
            if (DateSystem.IsPublicHoliday(date, CountryCode.US)) return true;

            return false;
        }


        /// <summary>
        ///     Determines if the given date is a weekend based on the day of week and Saturday indicator.
        /// </summary>
        /// <param name="date">The date to check.</param>
        /// <param name="saturdayIndicator">Indicates if the Saturday should be considered a business day or weekend.</param>
        /// <returns>true if the given date is a Sat/Sun and saturday indicator is false. If saturday delivery is true, only returns true if the given date is a Sunday.</returns>
        public bool IsWeekend(DateTime date, bool saturdayIndicator)
        {
            // If saturdayIndicator is true, only use Sunday as the weekend.
            if ((date.DayOfWeek == DayOfWeek.Saturday && !saturdayIndicator) || date.DayOfWeek == DayOfWeek.Sunday) return true;

            return false;
        }


        /// <summary>
        ///     Each state has a preferred location to ship from. This function flags the location based on the customer's shipping state.
        /// </summary>
        /// <param name="state">Customer's shipping state.</param>
        /// <param name="zip">Customer's shipping state. Used in California's preferred location logic.</param>
        public async Task SetPreferredLocationFlag(string state, string zip)
        {
            var preferredDC = await GetPreferredDCByState(state, zip);
            _logger.LogInformation($"Preferred DC {preferredDC}");

            if(!string.IsNullOrEmpty(preferredDC))
            {
                var preferredLocation = new Location()
                {
                    BranchNumber = preferredDC,
                    IsPreferred = true
                };

                // Attempt to add the preferred location to the dict
                var wasAddedToDict = locations.LocationDict.TryAdd(preferredDC, preferredLocation);

                // If an entry already exists, set it as the preferred location
                if (!wasAddedToDict)
                {
                    locations.LocationDict[preferredDC].IsPreferred = true;
                }
            }
        }


        public async Task<string> GetPreferredDCByState(string state, string zip)
        {
            var preferredBranchNumber = "";

            switch (state)
            {
                case "AK":
                case "ID":
                case "MT":
                case "OR":
                case "WA":
                case "WY":
                    preferredBranchNumber = "796";
                    break;
                case "AL":
                case "GA":
                case "MS":
                case "SC":
                case "TN":
                    preferredBranchNumber = "533";
                    break;
                case "AR":
                case "CO":
                case "LA":
                case "NM":
                case "OK":
                case "TX":
                    preferredBranchNumber = "474";
                    break;
                case "AZ":
                    preferredBranchNumber = "688";
                    break;
                case "CA":
                    if (int.Parse(zip) > 93000)
                        preferredBranchNumber = "321";
                    else
                        preferredBranchNumber = "688";
                    break;
                case "CT":
                case "MA":
                case "ME":
                case "NH":
                case "NJ":
                case "NY":
                case "PA":
                case "RI":
                case "VT":
                    preferredBranchNumber = "2920";
                    break;
                case "DC":
                case "DE":
                case "MD":
                case "NC":
                case "VA":
                    preferredBranchNumber = "423";
                    break;
                case "FL":
                    preferredBranchNumber = "761";
                    break;
                case "HI":
                case "NV":
                case "UT":
                    preferredBranchNumber = "321";
                    break;
                case "IA":
                case "IL":
                case "KS":
                case "MN":
                case "MO":
                case "ND":
                case "NE":
                case "SD":
                case "WI":
                    preferredBranchNumber = "986";
                    break;
                case "IN":
                case "KY":
                case "MI":
                case "OH":
                case "WV":
                    preferredBranchNumber = "2911";
                    break;
            }

            return preferredBranchNumber;
        }
    }
}
