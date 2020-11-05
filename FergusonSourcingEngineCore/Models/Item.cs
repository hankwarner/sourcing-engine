using System;
using System.Collections.Generic;

namespace FergusonSourcingCore.Models
{
    public class AllItems
    {
        // Key will be Master Product Number
        public Dictionary<int, ItemData> ItemDict = new Dictionary<int, ItemData>();
    }


    public class ItemData
    {
        public int MPN { get; set; }

        public string ItemCategory { get; set; }

        public string ItemDescription { get; set; }

        public string Manufacturer { get; set; }

        public string Vendor { get; set; }

        public bool BulkPack { get; set; }

        public int BulkPackQuantity { get; set; }

        public double Weight { get; set; }

        public string SourcingGuideline { get; set; }

        public bool? StockingStatus533 { get; set; }

        public bool? StockingStatus423 { get; set; }

        public bool? StockingStatus761 { get; set; }

        public bool? StockingStatus2911 { get; set; }

        public bool? StockingStatus2920 { get; set; }

        public bool? StockingStatus474 { get; set; }

        public bool? StockingStatus986 { get; set; }

        public bool? StockingStatus321 { get; set; }

        public bool? StockingStatus625 { get; set; }

        public bool? StockingStatus688 { get; set; }

        public bool? StockingStatus796 { get; set; }

        public string PreferredShippingMethod { get; set; } // value from the items table

        // Parses the number used in the preferred shipping method, i.e. "Ground4LTL" will return 4
        public int GroundQuantityThreshold
        {
            get
            {
                var thresholdQty = "";

                for (int i = 0; i < PreferredShippingMethod.Length; i++)
                {
                    if (char.IsDigit(PreferredShippingMethod[i]))
                    {
                        thresholdQty += PreferredShippingMethod[i];
                    }
                }

                if (thresholdQty == "") return 0;

                return int.Parse(thresholdQty);
            }
        }

        // Requirements
        public bool OverpackRequired { get; set; }
    }
    


    public class ItemResponse
    {
        // Key = mpn
        public List<Dictionary<int, ItemData>> itemResponse = new List<Dictionary<int, ItemData>>();
    }
}
