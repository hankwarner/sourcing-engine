using System.Collections.Generic;
using Newtonsoft.Json;

namespace FergusonSourcingCore.Models
{
    public partial class ManualOrder
    {
        public string atgOrderId { get; set; }
        public string custAccountId { get; set; }
        public string customerId { get; set; }
        public string customerName { get; set; }
        public string orderSubmitDate { get; set; }
        public string orderRequiredDate { get; set; }
        public string sellWhse { get; set; }
        public string sellLogon { get; set; }
        public string sourcingMessage { get; set; }
        public string processOrderType { get; set; }
        public bool claimed { get; set; }
        public string timeClaimed { get; set; } = null;
        public bool orderComplete { get; set; }
        public string timeCompleted { get; set; } = null;
        public ManualPaymentOnAccount paymentOnAccount { get; set; }
        public ManualShipping shipping { get; set; }
        public List<Sourcing> sourcing { get; set; }
        public string sourceSystem { get; set; }
        public string id { get; set; }
        public string notes { get; set; }
    }

    public partial class ManualPaymentOnAccount
    {
        public ManualPayment payment { get; set; }
    }

    public partial class ManualPayment
    {
        public string cardType { get; set; }
        public string address1 { get; set; }
        public string address2 { get; set; }
        public string city { get; set; }
        public string state { get; set; }
        public string zip { get; set; }
        public string phone { get; set; }
    }

    public partial class ManualShipping
    {
        public string shipViaCode { get; set; }
        public string shipVia { get; set; }
        public string price { get; set; }
        public ManualShipTo shipTo { get; set; }
    }

    public partial class ManualShipTo
    {
        public string name { get; set; }
        public string address1 { get; set; }
        public string address2 { get; set; }
        public string city { get; set; }
        public string country { get; set; }
        public string state { get; set; }
        public string zip { get; set; }
        public string shipInstructionsPhoneNumberAreaDialing { get; set; }
        public string shipInstructionsPhoneNumberDialNumber { get; set; }
    }

    public partial class Sourcing
    {
        public string shipFrom { get; set; }
        public string shipFromLogon { get; set; }
        public bool sourceComplete { get; set; }
        public List<ManualItem> items { get; set; }
    }

    public partial class ManualItem
    {
        public int lineItemId { get; set; }
        public string unitPrice { get; set; }
        public string unitPriceCode { get; set; }
        public string extendedPrice { get; set; }
        public string description { get; set; }
        public string quantity { get; set; }
        public string sourcingMessage { get; set; }
        public string masterProdId { get; set; }
        public bool itemComplete { get; set; }
        public string sourcingGuide { get; set; }
        public string vendor { get; set; }
        public string preferredShipVia { get; set; }
        public string alt1Code { get; set; }
    }

}
