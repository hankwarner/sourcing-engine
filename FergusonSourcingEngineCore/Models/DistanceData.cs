using System;
using System.Collections.Generic;
using System.Text;

namespace FergusonSourcingCore.Models
{
    public class DistanceData
    {
        public string BranchNumber { get; set; }

        public double DistanceFromZip { get; set; }

        public int BusinessTransitDays { get; set; }

        public bool SaturdayDelivery { get; set; } = false;

        public string Error { get; set; } = null;
    }
}
