using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using FergusonSourcingEngine.Controllers;
using Microsoft.Azure.Documents.Client;
using System.Linq;
using Microsoft.Azure.Documents;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System.Net;
using AzureFunctions.Extensions.Swashbuckle.Attribute;
using FergusonSourcingCore.Models;
using RestSharp;

namespace FergusonSourcingEngine
{
    public class SourcingEngineFunctions
    {
        public static FeedOptions option = new FeedOptions { EnableCrossPartitionQuery = true };
        public static string ordersContainerName = Environment.GetEnvironmentVariable("ORDERS_CONTAINER_NAME");
        public static string errorLogsUrl = Environment.GetEnvironmentVariable("ERROR_LOGS_URL");
        public static string sourcingTeamLogsUrl = Environment.GetEnvironmentVariable("SOURCING_TEAM_LOGS_URL");
        public static string manualOrderscontainerName = Environment.GetEnvironmentVariable("MANUAL_ORDERS_CONTAINER_NAME");
        public static IConfiguration _config { get; set; }

        public SourcingEngineFunctions(IConfiguration config)
        {
            _config = config;
        }

        [SwaggerIgnore]
        [FunctionName("GetOrder")]
        public static IActionResult GetOrder(
                    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "order/{id}")] HttpRequest req,
                    [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString")] DocumentClient cosmosClient,
                    ILogger log,
                    string id)
        {
            var collectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", ordersContainerName);
            var orderData = cosmosClient.CreateDocumentQuery<Document>(collectionUri, option)
                .Where(fo => fo.Id == id).AsEnumerable().FirstOrDefault();
            
            if (orderData == null)
            {
                return new NotFoundResult();
            }

            AtgOrderReq order = (dynamic)orderData;

            return new OkObjectResult(order);
        }


        [SwaggerIgnore]
        [FunctionName("DeleteOrder")]
        public static async Task<IActionResult> DeleteOrder(
            [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "order/{id}")] HttpRequest req,
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString")] DocumentClient cosmosClient,
            ILogger log,
            string id)
        {

            var collectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", ordersContainerName);
            var updatedOrderData = cosmosClient.CreateDocumentQuery<Document>(collectionUri, option)
                .Where(fo => fo.Id == id).AsEnumerable().FirstOrDefault();
            
            if (updatedOrderData == null)
            {
                return new NotFoundResult();
            }

            await cosmosClient.DeleteDocumentAsync(updatedOrderData.SelfLink, new RequestOptions { PartitionKey = new PartitionKey(updatedOrderData.Id)});

            return new OkResult();
        }
        

        [SwaggerIgnore]
        [FunctionName("SourceOrderById")]
        public static async Task<IActionResult> SourceOrderById(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "order/source/{id}")] HttpRequest req,
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString")] DocumentClient cosmosClient,
            ILogger log,
            string id)
        {
            var collectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", ordersContainerName);
            var updatedOrderData = cosmosClient.CreateDocumentQuery<Document>(collectionUri, option)
                .Where(fo => fo.Id == id).AsEnumerable().FirstOrDefault();
            
            if (updatedOrderData == null)
            {
                return new NotFoundResult();
            }

            AtgOrderRes updatedOrder = (dynamic)updatedOrderData;

            updatedOrder.processSourcing = false;
            updatedOrder.sourceComplete = true;
            updatedOrder.sourcingMessage = "This is a Test sourcing message.";

            updatedOrder.items.ForEach(line => {
                line.sourcingMessage = "This is a Test sourcing message.";
                line.shipFrom = "423";
            });

            await cosmosClient.ReplaceDocumentAsync(updatedOrderData.SelfLink, updatedOrder);

            return new OkObjectResult(updatedOrder);
        }


        /// <summary>
        ///     Endpoint for receiving the response from Trilogie and update the Trilogie status, err message and order ID on the ATGOrder and ManualOrder in CosmosDB.
        /// </summary>
        /// <param name="req">Patch request containing a FailedOrder object with a message to set on the ATG order.</param>
        /// <param name="atgOrderId">ID of the original ATG Order. Should match an existing order in CosmosDB.</param>
        [ProducesResponseType(typeof(string), 200)]
        [ProducesResponseType(typeof(BadRequestObjectResult), 400)]
        [ProducesResponseType(typeof(NotFoundObjectResult), 404)]
        [ProducesResponseType(typeof(ObjectResult), 500)]
        [FunctionName("UpdateTrilogieStatus")]
        public static async Task<IActionResult> UpdateTrilogieStatus(
            [HttpTrigger(AuthorizationLevel.Function, "patch", Route = "order/{atgOrderId}/status"), RequestBodyType(typeof(TrilogieRequest), "Trilogie status")] HttpRequest req,
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString"), SwaggerIgnore] DocumentClient document,
            ILogger log,
            string atgOrderId)
        {
            try
            {
                var reqBody = new StreamReader(req.Body).ReadToEnd();
                var trilogieReq = JsonConvert.DeserializeObject<TrilogieRequest>(reqBody);
                log.LogInformation($"ID: {atgOrderId}. TrilogieOrderId: {trilogieReq.TrilogieOrderId}. TrilogieStatus: {trilogieReq.TrilogieStatus}.TrilogieErrorMessage: {trilogieReq.TrilogieErrorMessage}");

                var manualOrderTask = OrderController.UpdateTrilogieStatusOnManualOrder(atgOrderId, document, trilogieReq);
                var atgOrderTask = OrderController.UpdateTrilogieStatusOnAtgOrder(atgOrderId, document, trilogieReq);

                await Task.WhenAll(manualOrderTask, atgOrderTask);

                // Sync with NBSupply Azure tenent DB
#if RELEASE
                var url = $"https://fergusonsourcingengine.azurewebsites.net/api/order/{atgOrderId}/status?code=iRSwaEhJGb9E6ZpmM/3r8pvKdXHZ5Cetf1ISrpjNfF3h3rDFJqA91Q==";
#endif
#if DEBUG
                var url = $"https://fergusonsourcingengine-dev.azurewebsites.net/api/order/{atgOrderId}/status?code=j2P2uKfndkdLVlduyTwjkcWla6uErwi8GV5nIa1oCWRMH7RyW0Wtcw==";
#endif
                var client = new RestClient(url);
                var request = new RestRequest(Method.PATCH).AddParameter("application/json; charset=utf-8", reqBody, ParameterType.RequestBody);

                _ = client.ExecuteAsync(request);

                return new OkObjectResult("Success");
            }
            catch (ArgumentException e)
            {
                log.LogError(e, "ArgumentException thrown.");
                return new NotFoundObjectResult(e.Message) { Value = e.Message }; ;
            }
            catch (JsonSerializationException e)
            {
                log.LogError(e, "JsonSerializationException thrown.");
                var msg = "Unable to deserialize JSON request. Ensure that trilogieStatus is 'Pass' or 'Fail' and all other data types are correct.";
                return new BadRequestObjectResult(e.Message) { Value = msg };
            }
            catch (Exception e)
            {
                var title = "Error in UpdateTrilogieStatus";
                var text = $"Error message: {e.Message}. Stacktrace: {e.StackTrace}";
                var teamsMessage = new TeamsMessage(title, text, "red", errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);

                log.LogError(@"Error in UpdateTrilogieStatus: {E}", e);
                return new ObjectResult(e.Message) { StatusCode = 500, Value = "Failure" };
            }
        }


        [ProducesResponseType(typeof(AtgOrderRes), 200)]
        [ProducesResponseType(typeof(BadRequestObjectResult), 400)]
        [ProducesResponseType(typeof(BadRequestObjectResult), 500)]
        [FunctionName("SourceOrderFromSite")]
        public static async Task<IActionResult> SourceOrderFromSite(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "order/source"), RequestBodyType(typeof(AtgOrderReq), "product request")] HttpRequest req,
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString"), SwaggerIgnore] DocumentClient cosmosClient,
            ILogger log)
        {
            try
            {
                var requestBody = new StreamReader(req.Body).ReadToEnd();
                log.LogInformation(@"Request body: {RequestBody}", requestBody);

                var orderReq = JsonConvert.DeserializeObject<AtgOrderReq>(requestBody);
                log.LogInformation($"Order ID: {orderReq.atgOrderId}");
                log.LogInformation(@"Order: {Order}", orderReq);

                var atgOrderRes = new AtgOrderRes(orderReq)
                {
                    id = orderReq.atgOrderId,
                    sourcingMessage = "Order received.",
                    sourceFrom = orderReq.sellWhse
                };

                atgOrderRes.items.ForEach(line => line.shipFrom = orderReq.sellWhse);

                var atgOrdersContainerName = Environment.GetEnvironmentVariable("ATG_ORDERS_CONTAINER_NAME");
                var collectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", atgOrdersContainerName);

                await cosmosClient.UpsertDocumentAsync(collectionUri, atgOrderRes);

                return atgOrderRes != null
                    ? (ActionResult)new OkObjectResult(atgOrderRes)
                    : new BadRequestObjectResult("Please pass an Order in JSON via the request body");
            }
            catch (Exception e)
            {
#if RELEASE
                var title = "Error in SourceOrderFromSite";
                var text = $"Error message: {e.Message}. Stacktrace: {e.StackTrace}";
                var teamsMessage = new TeamsMessage(title, text, "red", errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
#endif

                log.LogError(@"Error in SourceOrderFromSite: {E}", e);
                return new BadRequestObjectResult(e.Message);
            }
        }


        /// <summary>
        ///     When an order is added or updated to atg-orders container, this function runs the Ferguson sourcing logic to determine a ship from location
        ///     for each item on the order. The sourced order is sent back to the requester and written to the orders container. If an error occurs,
        ///     a ManualOrder object is created and written to the manual-orders container.
        /// </summary>
        /// <param name="documents">The CosmosDB change feed that contains the added or updated order(s).</param>
        /// <param name="documentClient">CosmosDB Document Client for CRUD opertions on containers.</param>
        /// <param name="log">Used to write logs to Azure Moniter.</param>
        /// <returns>AtgOrder object containing a ship from location for each item.</returns>
        [FunctionName("SourceATGOrder")]
        public async Task SourceATGOrder(
            [CosmosDBTrigger(
                databaseName: "sourcing-engine",
#if RELEASE
                collectionName: "atg-orders",
                LeaseCollectionName = "sourcing-leases",
#endif
#if DEBUG
                collectionName: "test-atg-orders",
                LeaseCollectionName = "test-sourcing-leases",
#endif
                ConnectionStringSetting = "AzureCosmosDBConnectionString",
                CreateLeaseCollectionIfNotExists = true), SwaggerIgnore]IReadOnlyList<Document> documents,
            //[HttpTrigger(AuthorizationLevel.Function, "post", Route = "order/source"), RequestBodyType(typeof(AtgOrderReq), "product request")] HttpRequest req,
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString"), SwaggerIgnore] DocumentClient documentClient,
            ILogger log)
        {
            log.LogInformation("SourceATGOrder called");
            log.LogInformation(@"documents: {Documents}", documents);

            foreach (var document in documents)
            {
                try
                {
                    //var requestBody = new StreamReader(req.Body).ReadToEnd();
                    //var atgOrderReq = JsonConvert.DeserializeObject<AtgOrderReq>(requestBody);

                    var atgOrderReq = JsonConvert.DeserializeObject<AtgOrderReq>(document.ToString());
                    log.LogInformation($"Order ID: {atgOrderReq.atgOrderId}");
                    log.LogInformation(@"Order: {Order}", atgOrderReq);

                    var atgOrderRes = new AtgOrderRes(atgOrderReq){ startTime = DateTime.Now };

                    var sourcingController = InitializeSourcingController(log);

                    await sourcingController.StartSourcing(documentClient, atgOrderRes);
                }
                catch (NullReferenceException ex)
                {
                    log.LogWarning(@"Missing required field: {E}", ex);
                }
                catch (Exception ex)
                {
                    var title = "Error in SourceATGOrder";
                    var text = $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}";
                    var teamsMessage = new TeamsMessage(title, text, "red", errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);

                    log.LogError(@"Error in SourceATGOrder: {E}", ex);
                }
            }
        }


#if RELEASE
        /// <summary>
        ///     Queries the Orders container for open orders with backordered items and re-runs them through sourcing to determine if inventory has become available.
        /// </summary>
        [SwaggerIgnore]
        [FunctionName("ReSourceBackorderedItems")]
        public static async Task ReSourceBackorderedItems(
            [TimerTrigger("0 */15 * * * *")] TimerInfo timer, // every 15 mins
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString"), SwaggerIgnore] DocumentClient documentClient,
            ILogger log)
        {
            try
            {
                var currentHour = OrderController.GetCurrentEasternHour();
                log.LogInformation($"Current Hour: {currentHour}");

                // Only run within 6am - 7pm EST
                if (currentHour < 6 || currentHour >= 19) return;

                // Get open orders with backordered item(s)
                var query = new SqlQuerySpec
                {
                    QueryText = @"
                        SELECT VALUE c FROM c 
                        JOIN s IN c.sourcing 
                        JOIN i IN s.items 
                        WHERE c.orderComplete = false 
                            AND c.claimed = false 
                            AND (i.sourcingMessage = 'Backordered.' OR CONTAINS(i.sourcingMessage, 'does not have the required quantity'))"
                };

                var manualOrdersCollectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", "manual-orders");
                var manualOrderDocs = documentClient.CreateDocumentQuery<Document>(manualOrdersCollectionUri, query, option).AsEnumerable();

                // Get the matching ATG Order from query results and run sourcing 
                foreach (var manualOrderDoc in manualOrderDocs)
                {
                    try
                    {
                        ManualOrder manualOrder = (dynamic)manualOrderDoc;
                        log.LogInformation($"Order ID: {manualOrder.atgOrderId}");

                        var atgOrderDoc = await OrderController.GetOrder<AtgOrderRes>(manualOrder.atgOrderId, documentClient);
                        AtgOrderRes atgOrder = (dynamic)atgOrderDoc;

                        await InitializeSourcingController(log).StartSourcing(documentClient, atgOrder);
                    }
                    catch (Exception ex)
                    {
                        var title = "Error in ReSourceBackorderedItems foreach loop.";
                        var teamsMessage = new TeamsMessage(title, $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}", "yellow", errorLogsUrl);
                        teamsMessage.LogToTeams(teamsMessage);
                        log.LogError(ex, title);
                    }
                }
            }
            catch (Exception ex)
            {
                var title = "Error in ReSourceBackorderedItems.";
                var teamsMessage = new TeamsMessage(title, $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}", "red", errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);
                log.LogError(ex, title);
            }
        }


        /// <summary>
        ///     Identifies atg-orders that do not exist in the orders container. If any are found, run the sourcing engine
        ///     and create the sourced order in the orders container.
        /// </summary>
        [FunctionName("SourceUnsourcedOrders")]
        public static async Task SourceUnsourcedOrders(
            [TimerTrigger("0 */5 * * * *")] TimerInfo timer, // every 5 mins
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString"), SwaggerIgnore] DocumentClient documentClient,
            ILogger log)
        {
            // Get orders within past 3 days in atg-orders container that are not in the orders container
            var query = new SqlQuerySpec
            {
                QueryText = "SELECT VALUE c FROM c WHERE c.lastModifiedDate > DateTimeAdd(\"dd\", -03, GetCurrentDateTime())"
            };

            var ordersCollectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", "orders");

            var atgOrdersCollectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", "atg-orders");

            var atgOrderDocs = documentClient.CreateDocumentQuery<Document>(atgOrdersCollectionUri, query, option).AsEnumerable();

            // Run sourcing engine on unsourced orders
            foreach (var atgOrderDoc in atgOrderDocs)
            {
                AtgOrderReq atgOrderReq = (dynamic)atgOrderDoc;

                query = new SqlQuerySpec
                {
                    QueryText = "SELECT * FROM c WHERE c.id = @id",
                    Parameters = new SqlParameterCollection() { new SqlParameter("@id", atgOrderReq.atgOrderId) }
                };

                var orderDoc = documentClient.CreateDocumentQuery<Document>(ordersCollectionUri, query, option).AsEnumerable().FirstOrDefault();
                var sourcingController = InitializeSourcingController(log);

                // If the order does not exist in orders container, run sourcing engine
                try
                {
                    if (orderDoc == null)
                    {
                        log.LogInformation($"Order ID: {atgOrderReq.atgOrderId}");
                        log.LogInformation(@"Order: {Order}", atgOrderReq);

                        var atgOrderRes = new AtgOrderRes(atgOrderReq);

                        await sourcingController.StartSourcing(documentClient, atgOrderRes);
                    }
                    else // ensure that each line item has a shipFrom location
                    {
                        AtgOrderRes atgOrderRes = (dynamic)orderDoc;

                        var requiresSourcing = OrderController.ValidateItemShipFroms(atgOrderRes.items);

                        if (requiresSourcing)
                        {
                            await sourcingController.StartSourcing(documentClient, atgOrderRes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(@"Missing required field: {E}", ex);

                    var title = "Error in SourceUnsourcedOrders: ";
                    var text = $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}";
                    var teamsMessage = new TeamsMessage(title, text, "red", errorLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);

                    log.LogError(title + @"{E}", ex);
                }
            }
        }


        /// <summary>
        ///     Re-runs sourcing logic on orders that are incomplete to ensure that the order is sourced using the most up to date inventory values.
        ///     If any are found, run the sourcing engine and create the sourced order in the orders container.
        /// </summary>
        [SwaggerIgnore]
        [FunctionName("RunSourcingOnStaleOrders")]
        public static async Task RunSourcingOnStaleOrders(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req,
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString"), SwaggerIgnore] DocumentClient documentClient,
            ILogger log)
        {
            try
            {
                var currentHour = OrderController.GetCurrentEasternHour();
                log.LogInformation($"Current Hour: {currentHour}");

                // Only run within 6am - 10pm EST
                if (currentHour < 6 || currentHour >= 22) return;

                var manualOrdersCollectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", "manual-orders");

                var query = new SqlQuerySpec
                {
                    QueryText = "SELECT * FROM c WHERE c.orderComplete = false"
                };

                var incompleteOrders = documentClient.CreateDocumentQuery<Document>(manualOrdersCollectionUri, query, option).AsEnumerable();
                log.LogInformation($"Incomplete Order Count: {incompleteOrders.Count()}");

                if (incompleteOrders.Count() == 0) return;

                foreach (var order in incompleteOrders)
                {
                    try
                    {
                        ManualOrder manualOrder = (dynamic)order;
                        log.LogInformation($"Order ID: {manualOrder.atgOrderId}");
                        log.LogInformation(@"Manual Order: {Order}", manualOrder);

                        var ordersCollectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", "orders");

                        // Get the matching order from the orders container and run sourcing
                        query = new SqlQuerySpec
                        {
                            QueryText = "SELECT * FROM c WHERE c.id = @id",
                            Parameters = new SqlParameterCollection() { new SqlParameter("@id", manualOrder.atgOrderId) }
                        };

                        var atgOrderReq = documentClient.CreateDocumentQuery<AtgOrderReq>(ordersCollectionUri, query, option)
                            .AsEnumerable().FirstOrDefault();

                        var jsonRequest = JsonConvert.SerializeObject(atgOrderReq);

                        var url = @"https://sourcing-engine.azurewebsites.net/api/order/source";

                        var client = new RestClient(url);

                        var request = new RestRequest(Method.POST)
                            .AddParameter("code", "SOURCING_ENGINE_HOST_KEY")
                            .AddParameter("application/json; charset=utf-8", jsonRequest, ParameterType.RequestBody);

                        _ = client.ExecuteAsync(request);
                    }
                    catch (Exception ex)
                    {
                        var title = "Error in RunSourcingOnStaleOrders loop";
                        var text = $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}";
                        var teamsMessage = new TeamsMessage(title, text, "yellow", errorLogsUrl);
                        teamsMessage.LogToTeams(teamsMessage);

                        log.LogError(@"Error in SourceStaleOrders loop: {E}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                var title = "Error in RunSourcingOnStaleOrders";
                var text = $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}";
                var teamsMessage = new TeamsMessage(title, text, "red", errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);

                log.LogError(@"Error in SourceStaleOrders: {E}", ex);
            }
        }


        /// <summary>
        ///     Ensures that orders are consistenly flowing from ATG. If no order has come in the last two hours, Sourcing team will be alerted.
        /// </summary>
        [SwaggerIgnore]
        [FunctionName("MonitorIncomingOrders")]
        public static void MonitorIncomingOrders(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req,
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString"), SwaggerIgnore] DocumentClient documentClient,
            ILogger log)
        {
            try
            {
                var easternStandardTime = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                var currentEasternTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternStandardTime);

                var currentHour = currentEasternTime.Hour;
                log.LogInformation($"Current Hour: {currentHour}");

                // Only run within 8am - 10pm EST
                if (currentHour < 8 || currentHour >= 22) return;

                // Get the most recent order
                var ordersCollectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", "orders");
                var query = new SqlQuerySpec
                {
                    QueryText = "SELECT TOP 1 * FROM c ORDER BY c.lastModifiedDate DESC"
                };

                var mostRecentOrderDoc = documentClient.CreateDocumentQuery<Document>(ordersCollectionUri, query, option)
                    .AsEnumerable().FirstOrDefault();

                AtgOrderRes mostRecentOrder = (dynamic)mostRecentOrderDoc;
                log.LogInformation(@"Most Recent Order: {MostRecentOrder}", mostRecentOrder);

                // Check that order came in within last two hours
                if (mostRecentOrder.lastModifiedDate > DateTime.Now.AddHours(-2))
                {
                    log.LogInformation("Most recent order is within two hour threshold.");
                }
                else
                {
                    var title = "No Orders within Last Two Hours";
                    var text = "Warning: no orders have been received from Ferguson in the last two hours.";
                    var teamsMessage = new TeamsMessage(title, text, "yellow", sourcingTeamLogsUrl);
                    teamsMessage.LogToTeams(teamsMessage);
                    log.LogInformation("Teams message sent.");
                }
            }
            catch (Exception ex)
            {
                var title = "Error in MonitorIncomingOrders";
                var text = $"Error message: {ex.Message}. Stacktrace: {ex.StackTrace}";
                var teamsMessage = new TeamsMessage(title, text, "red", errorLogsUrl);
                teamsMessage.LogToTeams(teamsMessage);

                log.LogError(@"Error in MonitorIncomingOrders: {E}", ex);
            }
        }
#endif

        /// <summary>
        ///     Initiatize instances of each controller.
        /// </summary>
        /// <param name="log">Azure logger.</param>
        /// <returns>A new instance of the Sourcing Controller.</returns>
        private static SourcingController InitializeSourcingController(ILogger log)
        {
            var itemController = new ItemController(log);
            var requirementController = new RequirementController(log, itemController);
            var locationController = new LocationController(log);
            var orderController = new OrderController(log, locationController);
            var shippingController = new ShippingController(log, itemController);
            var sourcingController = new SourcingController(log, itemController, locationController, shippingController, orderController, requirementController);

            return sourcingController;
        }
    }
}
