using Flow.Attributes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Flow.External.Nodes
{
    public class TestNode: FlowBaseNode
    {


        [FlowInput]
        public string test { get; set; }




        [FlowOutput]
        public string Output { get; set; }


        public void Execute()
        { 

        }














    }
}
