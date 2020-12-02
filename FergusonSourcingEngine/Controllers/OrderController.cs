using System;
using FergusonSourcingCore.Models;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FergusonSourcingEngine.Controllers
{
    public class OrderController
    {
        private static ILogger _logger;
        private LocationController locationController { get; set; }

        public OrderController(ILogger logger, LocationController locationController)
        {
            _logger = logger;
            this.locationController = locationController;
        }


        /// <summary>
        ///     Returns the matching line on the ATG order. Useful when working from the AllLines class and needing to get the matching
        ///     ATG order line.
        /// </summary>
        /// <param name="lineId">Index of the line.</param>
        /// <returns>Matching line on the ATG order.</returns>
        public ItemRes GetOrderItemByLineId(int lineId, AtgOrderRes atgOrderRes)
        {
            return atgOrderRes.items.FirstOrDefault(i => i.lineId == lineId);
        }


        /// <summary>
        ///     Queries the container of type T for a single order matching on id. Throws exception if no order is found.
        /// </summary>
        /// <typeparam name="T">ManualOrder or AtgOrderRes</typeparam>
        /// <param name="id">ID of the ATG order</param>
        /// <param name="document">Cosmos Document Client</param>
        /// <returns></returns>
        public static async Task<Document> GetOrder<T>(string id, DocumentClient document)
        {
            var envVariableName = "";

            if (typeof(T) == typeof(ManualOrder))
            {
                envVariableName = "MANUAL_ORDERS_CONTAINER_NAME";
            }
            else if (typeof(T) == typeof(AtgOrderRes))
            {
                envVariableName = "ORDERS_CONTAINER_NAME";
            }

            var containerName = Environment.GetEnvironmentVariable(envVariableName);
            var collectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", containerName);
            var feedOption = new FeedOptions { EnableCrossPartitionQuery = true };

            var query = new SqlQuerySpec
            {
                QueryText = "SELECT * FROM c WHERE c.id = @id",
                Parameters = new SqlParameterCollection() { new SqlParameter("@id", id) }
            };

            var order = document.CreateDocumentQuery<Document>(collectionUri, query, feedOption)
                .AsEnumerable().FirstOrDefault();

            if (order == null)
            {
                throw new ArgumentException($"Order with ID {id} does not exist.", "id");
            }

            return order;
        }


        /// <summary>
        ///     Sets the Trilogie fields on the ATG Order, incld. error message, status and order ID.
        /// </summary>
        /// <param name="trilogieReq">Request containing the results of submitting the order to Trilogie.</param>
        /// <param name="atgOrder">ATG Order object containing line items with shipFrom values.</param>
        public static async Task SetTrilogieFieldsOnATGOrder(TrilogieRequest trilogieReq, AtgOrderRes atgOrder)
        {
            atgOrder.trilogieErrorMessage = trilogieReq.TrilogieErrorMessage;
            atgOrder.trilogieOrderId = trilogieReq.TrilogieOrderId;
            atgOrder.trilogieStatus = trilogieReq.TrilogieStatus.ToString();
#if DEBUG
            // Sets to false if order failed in Trilogie                
            atgOrder.processSourcing = trilogieReq.TrilogieStatus == TrilogieStatus.Pass;
#endif
        }


        /// <summary>
        ///     Sets the Trilogie fields on the Manual Order, incld. error message, status and order ID.
        /// </summary>
        /// <param name="trilogieReq">Request containing the results of submitting the order to Trilogie.</param>
        /// <param name="manualOrder">Manual Order object that matches the ATG Order from the request.</param>
        public static async Task SetTrilogieFieldsOnManualOrder(TrilogieRequest trilogieReq, ManualOrder manualOrder)
        {
            manualOrder.trilogieErrorMessage = trilogieReq.TrilogieErrorMessage;
            manualOrder.trilogieOrderId = trilogieReq.TrilogieOrderId;
            manualOrder.trilogieStatus = trilogieReq.TrilogieStatus.ToString();
#if DEBUG
            // Sets to false if order failed in Trilogie                
            manualOrder.orderComplete = trilogieReq.TrilogieStatus == TrilogieStatus.Pass;

            if (manualOrder.orderComplete)
                manualOrder.timeCompleted = GetCurrentEasternTime();
            else
                manualOrder.timeCompleted = null;
#endif
        }


        /// <summary>
        ///     Gets the current UTC time and converts to Eastern Standard Time.
        /// </summary>
        /// <returns>Currently EST time in "MM//DD//yyyy h:mm tt" format</returns>
        public static string GetCurrentEasternTime()
        {
            var easternStandardTime = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var currentEasternTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternStandardTime);

            return currentEasternTime.ToString("MM/dd/yyyy h:mm tt");
        }


        /// <summary>
        ///     Iterates through order items and returns true if any valid item is missing a shipFrom value.
        /// </summary>
        /// <param name="atgOrderRes">ATG Order object containing line items with shipFrom values.</param>
        /// <returns></returns>
        public static bool ValidateItemShipFroms(List<ItemRes> items)
        {
            foreach(var item in items)
            {
                if (item.sourcingGuide == "No Source") continue;

                if (string.IsNullOrEmpty(item.shipFrom)) return true;
            }
            
            return false;
        }


        /// <summary>
        ///     Sets the TrilogieStatus, TrilogieErrorMessage and TrilogieOrderID fields on the ATGOrder in CosmosDB based on the request values.
        /// </summary>
        /// <param name="atgOrderId">ID of the original ATG order.</param>
        /// <param name="document">Cosmos DocumentDB client.</param>
        /// <param name="trilogieReq">Request containing the results of submitting the order to Trilogie.</param>
        /// <returns>The updated ATG order.</returns>
        public static async Task<AtgOrderRes> UpdateTrilogieStatusOnAtgOrder(string atgOrderId, DocumentClient document, TrilogieRequest trilogieReq)
        {
            try
            {
                var orderDoc = await GetOrder<AtgOrderRes>(atgOrderId, document);

                AtgOrderRes atgOrder = (dynamic)orderDoc;

                await SetTrilogieFieldsOnATGOrder(trilogieReq, atgOrder);

                _ = document.ReplaceDocumentAsync(orderDoc.SelfLink, atgOrder);

                return atgOrder;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in UpdateTrilogieStatusOnAtgOrder");
                throw;
            }
        }


        /// <summary>
        ///     Sets the TrilogieStatus, TrilogieErrorMessage and TrilogieOrderID fieldson the ManualOrder in CosmosDB based on the request values.
        /// </summary>
        /// <param name="atgOrderId">ID of the original ATG order.</param>
        /// <param name="document">Cosmos DocumentDB client.</param>
        /// <param name="trilogieReq">Request containing the results of submitting the order to Trilogie.</param>
        public static async Task UpdateTrilogieStatusOnManualOrder(string atgOrderId, DocumentClient document, TrilogieRequest trilogieReq)
        {
            try
            {
                var manualOrderDoc = await GetOrder<ManualOrder>(atgOrderId, document);

                ManualOrder manualOrder = (dynamic)manualOrderDoc;

                if (manualOrder != null)
                {
                    await SetTrilogieFieldsOnManualOrder(trilogieReq, manualOrder);
                }
                else // create a new manual order
                {
                    var orderController = new OrderController(_logger, new LocationController(_logger));

                    var orderDoc = await GetOrder<AtgOrderRes>(atgOrderId, document);
                    AtgOrderRes atgOrder = (dynamic)orderDoc;

                    manualOrder = orderController.CreateManualOrder(atgOrder, trilogieReq);
                }

                var containerName = Environment.GetEnvironmentVariable("MANUAL_ORDERS_CONTAINER_NAME");
                var collectionUri = UriFactory.CreateDocumentCollectionUri("sourcing-engine", containerName);

                _ = document.UpsertDocumentAsync(collectionUri, manualOrder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in UpdateTrilogieStatusOnManualOrder");
                throw;
            }
        }


        /// <summary>
        ///     Creates a new manual order object for orders that cannot be auto-sourced and require manual intervention by a rep.
        /// </summary>
        /// <param name="order">ATG Order with sourcing fields.</param>
        /// <returns>The manual order object that was created from the ATG order.</returns>
        public ManualOrder CreateManualOrder(AtgOrderRes order, TrilogieRequest trilogieReq = null)
        {
            _logger.LogInformation("CreateManualOrder start");

            var mOrder = new ManualOrder()
            {
                id = order.id,
                atgOrderId = order.atgOrderId,
                custAccountId = order.custAccountId,
                customerId = order.customerId,
                customerName = order.customerName,
                orderSubmitDate = order.orderSubmitDate,
                sellWhse = order.sellWhse,
                sourcingMessage = order.sourcingMessage,
                processOrderType = "manual",
                claimed = false,
                orderComplete = false,
                sourceSystem = order.sourceSystem,
                orderRequiredDate = order.orderRequiredDate,
                notes = order.notes,
                trilogieErrorMessage = trilogieReq?.TrilogieErrorMessage ?? "",
                trilogieOrderId = trilogieReq?.TrilogieOrderId ?? "",
                trilogieStatus = trilogieReq?.TrilogieStatus.ToString() ?? "Waiting on Trilogie response.",
                paymentOnAccount = new ManualPaymentOnAccount()
                {
                    payment = new ManualPayment()
                    {
                        address1 = order.paymentOnAccount?.First().address1,
                        address2 = order.paymentOnAccount?.First().address2,
                        cardType = order.paymentOnAccount?.First().cardType,
                        city = order.paymentOnAccount?.First().city,
                        state = order.paymentOnAccount?.First().state,
                        zip = order.paymentOnAccount?.First().zip,
                        phone = order.paymentOnAccount?.First().phone
                    }
                },
                shipping = new ManualShipping()
                {
                    price = order.shipping?.price,
                    shipViaCode = order.shipping?.shipViaCode,
                    shipVia = order.shipping?.shipVia,
                    shipTo = new ManualShipTo()
                    {
                        address1 = order.shipping.shipTo?.address1,
                        address2 = order.shipping.shipTo?.address2,
                        city = order.shipping.shipTo?.city,
                        country = order.shipping.shipTo?.country,
                        name = order.shipping.shipTo?.name,
                        shipInstructionsPhoneNumberAreaDialing = order.shipping.shipTo?.shipInstructionsPhoneNumberAreaDialing,
                        shipInstructionsPhoneNumberDialNumber = order.shipping.shipTo?.shipInstructionsPhoneNumberDialNumber,
                        state = order.shipping.shipTo?.state,
                        zip = order.shipping.shipTo?.zip
                    }
                },
                sourcing = order.items.GroupBy(item =>
                    item.shipFrom,
                    item => item,
                    (key, group) => new Sourcing()
                    {
                        shipFrom = key,
                        sourceComplete = order.sourceComplete,
                        items = group.Select(item => new ManualItem()
                        {
                            lineItemId = item.lineId,
                            unitPrice = item.unitPrice,
                            unitPriceCode = item.unitPriceCode,
                            extendedPrice = item.extendedPrice,
                            description = item.itemDescription,
                            quantity = item.quantity,
                            sourcingMessage = item.sourcingMessage,
                            masterProdId = item.masterProdId,
                            itemComplete = false,
                            sourcingGuide = item.sourcingGuide,
                            vendor = item.vendor,
                            preferredShipVia = item.preferredShipVia
                        }).ToList()
                    }).ToList()
            };

            var sellWarehouse = order.sellWhse ?? "D98 DISTRIBUTION CENTERS";

            mOrder.sellLogon = locationController.GetBranchLogonID(sellWarehouse);

            mOrder.sourcing.ForEach(source =>
            {
                var branchNum = source.shipFrom;

                if (!string.IsNullOrEmpty(branchNum))
                {
                    source.shipFromLogon = locationController.GetBranchLogonID(branchNum);
                }

                source.items.ForEach(item =>
                {
                    var orderItem = GetOrderItemByLineId(item.lineItemId, order);

                    item.alt1Code = orderItem.alt1Code;
                });
            });

            _logger.LogInformation("CreateManualOrder finish");

            return mOrder;
        }
    }
}
