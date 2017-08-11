using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CosmosDbGraphUploader.Console
{
    public delegate void ProgressStatusHandler(Object sender, ProgressStatusEventArgs e);

    public class ProgressStatusEventArgs
    {

        public string Status { get; set; }
        public ProgressInfo Progress { get; set; }

        public ProgressStatusEventArgs(string status)
        {
            Status = status;
        }

        public ProgressStatusEventArgs(string status, int current, int total) : this(status)
        {
            Status = status;
            Progress = new ProgressInfo() { Current = current, Total = total };
        }

        public struct ProgressInfo
        {
            public int Current;
            public int Total;
        }
    }
}
