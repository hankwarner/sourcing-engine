using System;
using System.Collections.Generic;
using System.Linq;
using FergusonSourcingCore.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using Polly;
using Microsoft.Extensions.Logging;

namespace FergusonSourcingEngine.Controllers
{
    public class ItemController
    {
        public Inventory inventory = new Inventory();
        public AllItems items = new AllItems();
        public List<int> mpns = new List<int>();
        public double cumulativeItemWeight { get; set; }
        private ILogger _logger;

        public ItemController(ILogger logger)
        {
            _logger = logger;
        }


        public void InitializeItems(AtgOrderRes atgOrderRes)
        {
            try
            {
                _logger.LogInformation("InitializeItems start");

                AddMPNs(atgOrderRes);

                var itemDataResponse = GetItemDataByMPN();

                // Write data from the response
                AddItemDataToDict(itemDataResponse, atgOrderRes);

                SetItemDetailsOnOrder(atgOrderRes);

                InitializeInventoryDict();

                AddStockingStatusesToInventoryDict();

                _logger.LogInformation("InitializeItems finish");
            }
            catch(Exception ex)
            {
                var title = "Error in InitializeItems";
                var teamsMessage = new TeamsMessage(title, $"Order Id: {atgOrderRes.atgOrderId}. Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
                _logger.LogError(title);
                throw;
            }
        }


        public void AddMPNs(AtgOrderRes atgOrderRes)
        {
            atgOrderRes.items.ForEach(item =>
            {
                int.TryParse(item.masterProdId, out int mpn);
                int.TryParse(item.quantity, out int quantity);

                if (mpn != 0 && quantity != 0) mpns.Add(mpn);
            });

            // If all items are missing MPN's, there is nothing further we can do
            if (mpns.Count() == 0) throw new NullReferenceException("masterProdId and quantity are required");
        }


        public void InitializeInventoryDict()
        {
            var validMPNs = items.ItemDict.Select(x => x.Key).ToList();

            // Create entries in the inventoryDict for each valid MPN
            validMPNs.ForEach(mpn => inventory.InventoryDict.TryAdd(mpn, new ItemInventory()) );
        }


        public Dictionary<string, ItemData> GetItemDataByMPN()
        {
            var retryPolicy = Policy.Handle<Exception>().Retry(5, (ex, count) =>
            {
                var title = "Error in GetItemDataByMPN";
                _logger.LogWarning($"{title}. Retrying...");

                if (count == 5)
                {
                    var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngine.errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);
                    _logger.LogError(ex, title);
                }
            });

            return retryPolicy.Execute(() =>
            {
                var requestBody = new List<int>(mpns.Select(x => x));
                var jsonRequest = JsonConvert.SerializeObject(requestBody);

                var url = @"https://service-sourcing.supply.com/api/v2/ItemData/GetItemDataByMPN";

                var client = new RestClient(url);

                var request = new RestRequest(Method.POST)
                    .AddHeader("Content-Type", "application/json")
                    .AddParameter("application/json; charset=utf-8", jsonRequest, ParameterType.RequestBody);

                var jsonResponse = client.Execute(request).Content;

                var itemDataResponse = JsonConvert.DeserializeObject<Dictionary<string, ItemData>>(jsonResponse);

                if(itemDataResponse == null)
                {
                    throw new Exception("Item Data returned null.");
                }

                // Send Teams message if any of the items are missing data
                foreach(var item in itemDataResponse)
                {
                    if(item.Value == null)
                    {
                        var title = "Item is missing data.";
                        var teamsMessage = new TeamsMessage(title, $"Item {item.Key} is missing data.", "red", SourcingEngine.errorLogsUrl);
                        teamsMessage.LogToTeams(teamsMessage);
                    }
                }

                return itemDataResponse;
            });
        }


        public void AddItemDataToDict(Dictionary<string, ItemData> itemDataResponse, AtgOrderRes atgOrderRes)
        {
            try
            {
                foreach(var item in itemDataResponse)
                {
                    var mpn = int.Parse(item.Key);
                    var itemData = item.Value;

                    if (itemData == null) FlagInvalidMPN(mpn, atgOrderRes);

                    items.ItemDict.TryAdd(mpn, itemData);
                }
            }
            catch (Exception ex)
            {
                var title = "Error in AddItemDataToDict";
                var teamsMessage = new TeamsMessage(title, $"Order Id: {atgOrderRes.atgOrderId}. Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
            }
        }


        /// <summary>
        ///     Sets any relevant item details, such as description and ship method, for each line item on the order.
        /// </summary>
        /// <param name="atgOrderRes">The ATG Order response object that will be written to Cosmos DB.</param>
        public void SetItemDetailsOnOrder(AtgOrderRes atgOrderRes)
        {
            try
            {
                atgOrderRes.items.ForEach(item =>
                {
                    var mpn = int.Parse(item.masterProdId);

                    item.itemDescription = items.ItemDict[mpn].ItemDescription;

                    SetItemShippingValues(item, mpn);
                });
            }
            catch (Exception ex)
            {
                var title = "Error in SetItemDetailsOnOrder";
                var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "yellow", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
            }
        }


        public void SetItemShippingValues(ItemRes item, int mpn)
        {
            try
            {
                item.preferredShippingMethod = items.ItemDict[mpn].PreferredShippingMethod;

                var shippingController = new ShippingController(_logger, this);
                
                item.preferredShipVia = shippingController.GetItemPreferredShipVia(item.preferredShippingMethod, int.Parse(item.masterProdId), int.Parse(item.quantity));
            }
            catch (Exception ex)
            {
                var title = $"Error in SetShippingValues for MPN {item.masterProdId}";
                var text = $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}";
                var teamsMessage = new TeamsMessage(title, text, "yellow", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
                _logger.LogError(ex, title);
            }
        }


        public void FlagInvalidMPN(int mpn, AtgOrderRes atgOrderRes)
        {
            try
            {
                var orderItems = SourcingController.GetOrderItemsByMPN(mpn, atgOrderRes);

                orderItems.ForEach(item => item.invalidMPN = true);
            }
            catch(Exception ex)
            {
                var title = "Error in FlagInvalidMPNs";
                var teamsMessage = new TeamsMessage(title, $"Order Id: {atgOrderRes.atgOrderId}. Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "yellow", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
            }
        }


        /// <summary>
        ///     Gets inventory from Manhattan tables and adds it to the Inventory Dictionary under the Available property,
        ///     resulting in a dictionary of branch number/inventory key value pairs for each location.
        /// </summary>
        public void InitializeInventory()
        {
            try
            {
                _logger.LogInformation("InitializeInventory start");

                // Response will be an array of JS objects
                var inventoryResponse = GetInventoryData();

                var domInventory = ParseInventoryResponse(inventoryResponse);

                // Add DOM inventory to inventoryDict available location data
                AddAvailableInventoryToDict(domInventory.InventoryData);

                _logger.LogInformation("InitializeInventory finish");
            }
            catch(Exception ex)
            {
                var title = "Error in InitializeInventory";
                var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
                _logger.LogError(title);
            }
        }


        public JArray GetInventoryData()
        {
            var retryPolicy = Policy.Handle<Exception>().Retry(5, (ex, count) =>
            {
                var title = "Error in GetInventoryData";
                _logger.LogWarning(ex, $"{title} . Retrying...");

                if (count == 5)
                {
                    var teamsMessage = new TeamsMessage(title, $"Error: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngine.errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);
                    _logger.LogError(ex, title);
                }
            });

            return retryPolicy.Execute(() =>
            {
                var requestBody = new DOMInventoryRequest(mpns);
                var jsonRequest = JsonConvert.SerializeObject(requestBody);

                var url = @"https://erebus.nbsupply.com:443/WebServices/Inventory/RequestInventoryFromDomB2CStorefront";

                var client = new RestClient(url);

                var request = new RestRequest(Method.POST)
                    .AddHeader("Content-Type", "text/plain")
                    .AddParameter("application/json; charset=utf-8", jsonRequest, ParameterType.RequestBody);

                var jsonResponse = client.Execute(request).Content;

                if (jsonResponse.Substring(0, 1) == "<")
                {
                    throw new Exception($"Erebus returned an invalid response: {jsonResponse}");
                }

                // Response should be an array of JS objects with MPN on the first line
                JArray inventoryResponse;
                try
                {
                    inventoryResponse = JArray.Parse(jsonResponse);
                }
                catch(JsonReaderException ex)
                {
                    _logger.LogWarning("Inventory API did not return a json array.", ex);
                    throw;
                }

                return inventoryResponse;
            });
        }


        public InventoryResponse ParseInventoryResponse(JArray inventoryResponse)
        {
            var domInventory = new InventoryResponse();

            try
            {
                // Parse response into a list of dictionaries
                foreach (var inventoryContent in inventoryResponse.Children<JObject>())
                {
                    var branchNumAndQuantityDict = new Dictionary<string, string>();

                    foreach (var inventoryLine in inventoryContent.Properties())
                    {
                        branchNumAndQuantityDict.TryAdd(inventoryLine.Name, inventoryLine.Value.ToString());
                    }

                    domInventory.InventoryData.Add(branchNumAndQuantityDict);
                }
            }
            catch (Exception ex)
            {
                var title = "Error in ParseInventoryResponse";
                var teamsMessage = new TeamsMessage(title, $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
            }

            return domInventory;
        }


        public void AddAvailableInventoryToDict(List<Dictionary<string, string>> inventoryList)
        {
            try
            {
                inventoryList.ForEach(locationDict =>
                {
                    var mpn = int.Parse(locationDict.FirstOrDefault(x => x.Key == "MPID").Value);

                    foreach (var line in locationDict)
                    {
                        var branchNumber = line.Key;

                        // Skip lines where the key is not a branch number
                        if (branchNumber == "MPID" || branchNumber == "RetrievedFromDOM") continue;

                        var inventoryAtLocation = int.Parse(line.Value);

                        inventory.InventoryDict.TryAdd(mpn, new ItemInventory());

                        inventory.InventoryDict[mpn].Available.TryAdd(branchNumber, inventoryAtLocation);

                        // Make a copy of available inventory that can be decremented for multi line item use
                        inventory.InventoryDict[mpn].MultiLineAvailable.TryAdd(branchNumber, inventoryAtLocation);
                    }
                });
            }
            catch(Exception ex)
            {
                var title = "Error in AddAvailableInventoryToDict";
                var text = $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}";
                var color = "red";
                var teamsMessage = new TeamsMessage(title, text, color, SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
            }
        }


        public void AddStockingStatusesToInventoryDict()
        {
            try
            {
                foreach(var itemLine in items.ItemDict)
                {
                    var mpn = itemLine.Key;
                    var itemData = itemLine.Value;

                    // Create entries in InventoryDict of stocking statuses
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("533", itemData?.StockingStatus533);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("423", itemData?.StockingStatus423);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("761", itemData?.StockingStatus761);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("2911", itemData?.StockingStatus2911);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("2920", itemData?.StockingStatus2920);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("474", itemData?.StockingStatus474);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("986", itemData?.StockingStatus986);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("321", itemData?.StockingStatus321);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("625", itemData?.StockingStatus625);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("688", itemData?.StockingStatus688);
                    inventory.InventoryDict[mpn].StockStatus.TryAdd("796", itemData?.StockingStatus796);
                }
            }
            catch (Exception ex)
            {
                var title = "Error in AddStockingStatusesToInventoryDict";
                var teamsMessage = new TeamsMessage(title, $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
            }
        }


        /// <summary>
        ///     Determines if the given item and quantity is in stock at any location in the locations dictionary.
        /// </summary>
        /// <returns>Returns true if given item is in stock at any valid location.</returns>
        public bool IsItemInStockAtAnyLocation(int mpn, int quantity)
        {
            try
            {
                KeyValuePair<string, int>? locationWithInventory = inventory.InventoryDict[mpn].Available
                    .FirstOrDefault(location => location.Value >= quantity);

                if (locationWithInventory != null) return true;
            }
            catch (Exception ex)
            {
                var title = "Error in IsItemInStockAtAnyLocation";
                var teamsMessage = new TeamsMessage(title, $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}", "yellow", SourcingEngine.errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
            }

            return false;
        }
    }
}
