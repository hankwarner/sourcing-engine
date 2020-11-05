using System;
using FergusonSourcingCore.Models;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

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
        public static Document GetOrder<T>(string id, DocumentClient document)
        {
            var envVariableName = "";

            if(typeof(T) == typeof(ManualOrder))
            {
                envVariableName = "MANUAL_ORDERS_CONTAINER_NAME";
            }
            else if(typeof(T) == typeof(AtgOrderRes))
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

            if (order == null && typeof(T) == typeof(AtgOrderRes))
            {
                throw new ArgumentException($"Order with ID {id} does not exist.", "id");
            }

            return order;
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
        ///     Creates a new manual order object for orders that cannot be auto-sourced and require manual intervention by a rep.
        /// </summary>
        /// <param name="order">ATG Order with sourcing fields.</param>
        /// <returns>The manual order object that was created from the ATG order.</returns>
        public ManualOrder CreateManualOrder(AtgOrderRes order)
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

                // TODO: set item values here instead of above
                source.items.ForEach(item =>
                {

                });
            });

            _logger.LogInformation("CreateManualOrder finish");

            return mOrder;
        }
    }
}
