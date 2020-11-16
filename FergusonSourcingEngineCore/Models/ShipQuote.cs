﻿using System;
using System.Collections.Generic;
using System.Text;

namespace FergusonSourcingCore.Models
{
#if RELEASE
    public class ShipQuoteRequest
    {
        public Package package { get; set; }
    }
    public class Package
    {
        public double length { get; set; }
        public double width { get; set; }
        public double height { get; set; }
        public double weight { get; set; }
        public string address1 { get; set; }
        public string address2 { get; set; }
        public string city { get; set; }
        public string state { get; set; }
        public string zip { get; set; }
        public string originaddress1 { get; set; }
        public string originaddress2 { get; set; }
        public string origincity { get; set; }
        public string originstate { get; set; }
        public string originzip { get; set; }
    }

    public class ShipQuoteResponse
    {
        public Dictionary<string, double> rates { get; set; }
    }
#endif
#if DEBUG
    public class ShipQuoteRequest
    {
        public string RateType { get; set; }

        public ShippingAddress DestinationAddress { get; set; }

        public ShippingAddress OriginAddress { get; set; }

        public Package Package { get; set; }
    }


    public class Package
    {
        public double Weight { get; set; }
    }
#endif
}
