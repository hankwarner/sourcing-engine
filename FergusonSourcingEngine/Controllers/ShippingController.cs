using System;
using FergusonSourcingCore.Models;
using RestSharp;
using Polly;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Linq;

namespace FergusonSourcingEngine.Controllers
{
    public class ShippingController
    {
        private ILogger _logger;
        private ItemController itemController { get; set; }

        public ShippingController(ILogger logger, ItemController itemController)
        {
            _logger = logger;
            this.itemController = itemController;
        }

        public double EstimateShippingCost(double weight, ShippingAddress shipTo, ShippingAddress shipFrom, AtgOrderRes atgOrderRes)
        {
            var retryCount = 0;

            var retryPolicy = Policy.Handle<Exception>().Retry(5, (ex, count) =>
            {
                var title = "Error in EstimateShippingCost";
                _logger.LogWarning($"{title}. Retrying...");

                if (count == 5)
                {
                    var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "yellow", SourcingEngineFunctions.errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);
                    _logger.LogError(ex, title);
                }
            });

            return retryPolicy.Execute(() =>
            {
                var rate = 15.00;

                var requestBody = new ShipQuoteRequest
                {
                    package = new Package()
                    {
                        length = 12,
                        width = 12,
                        height = 24,
                        weight = weight,
                        address1 = shipTo.addressLine1,
                        address2 = shipTo.addressLine2,
                        city = shipTo.city,
                        state = shipTo.state,
                        zip = shipTo.zip,
                        originaddress1 = shipFrom.addressLine1,
                        originaddress2 = shipFrom.addressLine2,
                        origincity = shipFrom.city,
                        originstate = shipFrom.state,
                        originzip = shipFrom.zip,
                    }
                };

                var jsonRequest = JsonConvert.SerializeObject(requestBody);

                // Default to standing shipping rate. Only change if it is shipping freight
                var url = atgOrderRes.notes?.ToLower() == "freight" ?
                    "https://erebus.nbsupply.com:443/ShipSource/ShipQuotes/Freight" :
                    "https://erebus.nbsupply.com:443/ShipSource/ShipQuotes/Ground";
                    

                var client = new RestClient(url);

                var request = new RestRequest(Method.POST)
                    .AddHeader("Content-Type", "application/json")
                    .AddHeader("Authorization", Environment.GetEnvironmentVariable("DOM_INVENTORY_KEY"))
                    .AddParameter("application/json; charset=utf-8", jsonRequest, ParameterType.RequestBody);

                var jsonResponse = client.Execute(request).Content;

                if(jsonResponse == "")
                {
                    if (retryCount == 5)
                    {
                        return rate;
                    }
                    else
                    {
                        retryCount++;
                        throw new Exception("Est Shipping Cost returned null");
                    }
                }

                var parsedResponse = JsonConvert.DeserializeObject<ShipQuoteResponse>(jsonResponse);

                var shipVia = atgOrderRes.shipping.shipViaCode.ToUpper();
                var rateKey = "GROUND";

                if (shipVia == "NEXT_DAY")
                {
                    rateKey  = "STANDARD_OVERNIGHT";
                }
                else if (shipVia == "SECOND_DAY")
                {
                    rateKey = "2_DAY";
                }

                rate = parsedResponse.rates.Where(r => r.Key.Contains(rateKey)).Select(r => r.Value).FirstOrDefault();

                _logger.LogInformation($"Branch {shipFrom.branchNumber} Estimated Shipping Cost: {rate}");

                return rate;
            });
        }


        public double GetCumulativeItemWeight()
        {
            var cumulativeItemWeight = 0.0;

            try
            {
                var itemDict = itemController.items.ItemDict;

                cumulativeItemWeight = itemDict.Sum(item => item.Value.Weight);
            }
            catch(Exception ex)
            {
                var title = "Error in GetCumulativeItemWeight";
                var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "yellow", SourcingEngineFunctions.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
                _logger.LogError(ex, title);
            }

            return cumulativeItemWeight;
        }


        /// <summary>
        ///     Determines how the item will ship based on the item preferred ship method and quantity.
        /// </summary>
        /// <param name="prefShipMethod">The preferred shipping method of the item.</param>
        /// <param name="lineQty">Item quantity on the line.</param>
        /// <param name="mpn">Master Product Number of the item.</param>
        /// <returns>The method that the order will be shipped.</returns>
        public string GetItemPreferredShipVia(string prefShipMethod, int mpn, int lineQty)
        {
            prefShipMethod = prefShipMethod.ToUpper();

            // Next day and 2nd day orders are hard-mapped to values
            if (prefShipMethod == "2ND DAY AIR") 
                return "UP2";

            if (prefShipMethod == "OVERNIGHT") 
                return "UNN";

            itemController.items.ItemDict.TryGetValue(mpn, out ItemData item);

            if (item == null)
                return "Item data not available.";

            // Handles LTL, Ground2LTL, and Ground4LTL
            if (prefShipMethod == "LTL" || ((prefShipMethod == "GROUND2LTL" || prefShipMethod == "GROUND4LTL") && lineQty > item.GroundQuantityThreshold))
                return "LTL";

            // Handles Ground and Shippers Choice
            if (prefShipMethod == "GROUND" || prefShipMethod.Contains("CHOICE") || lineQty <= item.GroundQuantityThreshold)
                return "UPS";

            return "N/A";
        }


        public string GetOrderShipVia(string shipViaCode)
        {
            string trilogieShipVia;

            switch (shipViaCode)
            {
                case "NEXT_DAY":
                    trilogieShipVia = "UNN";
                    break;
                case "SECOND_DAY":
                    trilogieShipVia = "UP2";
                    break;
                case "LTL":
                    trilogieShipVia = "LTL";
                    break;
                case "STANDARD":
                    trilogieShipVia = "UPS";
                    break;
                case "CPU":
                    trilogieShipVia = "WCL";
                    break;
                default:
                    trilogieShipVia = "N/A";
                    break;
            }

            return trilogieShipVia;
        }
    }
}