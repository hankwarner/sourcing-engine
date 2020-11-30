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
        ///     Flags an order as error as failed by setting process sourcing to false, sourcing message to the failed order message, and creating a manual order so a rep an intervene.
        /// </summary>
        /// <param name="req">Patch request containing a FailedOrder object with a message to set on the ATG order.</param>
        /// <param name="id">ID of the ATG Order.</param>
        /// <returns>The updated ATG Order.</returns>
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(AtgOrderRes))]
        [ProducesResponseType((int)HttpStatusCode.NotFound, Type = typeof(NotFoundObjectResult))]
        [ProducesResponseType((int)HttpStatusCode.BadRequest, Type = typeof(BadRequestObjectResult))]
        [ProducesResponseType((int)HttpStatusCode.InternalServerError, Type = typeof(BadRequestObjectResult))]
        [FunctionName("UpdateOrderToFailed")]
        public static async Task<IActionResult> UpdateOrderToFailed(
            [HttpTrigger(AuthorizationLevel.Function, "patch", Route = "order/{id}/fail"), RequestBodyType(typeof(FailedOrder), "failed order")] HttpRequest req,
            [CosmosDB(ConnectionStringSetting = "AzureCosmosDBConnectionString"), SwaggerIgnore] DocumentClient document,
            ILogger log,
            string id)
        {
            try
            {
                var reqBody = new StreamReader(req.Body).ReadToEnd();
                var failedOrder = JsonConvert.DeserializeObject<FailedOrder>(reqBody);
                // TODO: will message be passed in or hard coded?
                //var message = "Failed Trilogie order.";
                log.LogInformation($"ID: {id}");
                log.LogInformation("Request body: {failedOrder}", failedOrder);

                var orderDoc = OrderController.GetOrder<AtgOrderRes>(id, document);

                AtgOrderRes order = (dynamic)orderDoc;

                order.sourcingMessage = failedOrder?.Message ?? "Failed Trilogie order.";
                order.processSourcing = false;

                await document.ReplaceDocumentAsync(orderDoc.SelfLink, order);

                var manualOrderDoc = OrderController.GetOrder<ManualOrder>(id, document);

                ManualOrder manualOrder = (dynamic)manualOrderDoc;

                // update the manual order if it exists
                if (manualOrder != null)
                {
                    manualOrder.sourcingMessage = failedOrder.Message;
                    manualOrder.orderComplete = false;
                }
                else // create a new manual order
                {
                    var orderController = new OrderController(log, new LocationController(log));

                    manualOrder = orderController.CreateManualOrder(order);
                }

                var containerName = Environment.GetEnvironmentVariable("MANUAL_ORDERS_CONTAINER_NAME");
                var collectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", containerName);

                await document.UpsertDocumentAsync(collectionUri, manualOrder);

                return new OkObjectResult(order);
            }
            catch (ArgumentException e)
            {
                log.LogError(e.Message);
                log.LogError(e.StackTrace);
                return new NotFoundObjectResult(e.Message);
            }
            catch (Exception e)
            {
                log.LogError(e.Message);
                log.LogError(e.StackTrace);
                return new BadRequestObjectResult(e.Message);
            }
        }


        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(AtgOrderRes))]
        [ProducesResponseType((int)HttpStatusCode.BadRequest, Type = typeof(BadRequestObjectResult))]
        [ProducesResponseType((int)HttpStatusCode.InternalServerError, Type = typeof(BadRequestObjectResult))]
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
                log.LogError(e.Message);
                log.LogError(e.StackTrace);
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
        [FunctionName("RunSourcingOnStaleOrders")]
        public static async Task RunSourcingOnStaleOrders(
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
