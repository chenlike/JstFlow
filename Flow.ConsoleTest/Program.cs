using Flow.Core;
using Flow.Core.NodeMeta;
using Flow.External;
using Flow.External.Nodes;
using System;
using System.Collections.Generic;

namespace Flow.ConsoleTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var startNode = FlowNodeBuilder.Build<StartNode>();

            FlowNodeInfo forNode = FlowNodeBuilder.For<ForNode>()
                .SetValue(n => n.Start, 0)
                .SetValue(n => n.End, 10)
                .SetValue(n => n.Step, 1)
                .Build();



            FlowNodeInfo debugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "Hello, World!")
                .Build();


            FlowNodeInfo overDebugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "Over!")
                .Build();



            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, ForNode>(startNode, forNode, n => n.Start, n => n.StartLoop),
                FlowConnection.EventToSignal<ForNode, DebugLogNode>(forNode, debugNode, n => n.LoopBody, n => n.Print),
                FlowConnection.EventToSignal<ForNode, DebugLogNode>(forNode, overDebugNode, n => n.LoopCompleted, n => n.Print),
            };


            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, forNode, debugNode, overDebugNode }, connections);
            if (graphRes.IsSuccess == false)
            {
                Console.WriteLine("创建流程图失败:" + graphRes.Message);
            }
            var executor = FlowExecutor.Create(graphRes.Data);
            executor.Start();

            int step = 0;
            while (executor.StepNext())
            {
                //Console.WriteLine($"[{step}]---------");

                step++;
            }

            System.Console.WriteLine("------全部结束---------");
        }
    }
}
