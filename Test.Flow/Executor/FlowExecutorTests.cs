using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Flow.Core;
using Flow.Core.NodeMeta;
using Flow.External;
using Flow.External.Expressions;
using Flow.External.Nodes;
using Flow.Attributes;
using Flow.Core.Metas;
using Xunit;

namespace Test.Flow.Executor
{
    public class FlowExecutorTests
    {
        [Fact]
        public void Create_WithValidGraph_ShouldSucceed()
        {
            // Arrange
            var startNode = FlowNodeBuilder.Build<StartNode>();
            var debugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "Hello, World!")
                .Build();

            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, DebugLogNode>(startNode, debugNode, n => n.Start, n => n.Print),
            };

            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, debugNode }, connections);

            // Act
            var executor = FlowExecutor.Create(graphRes.Data);

            // Assert
            Assert.NotNull(executor);
            Assert.False(executor.IsRunning);
        }

        [Fact]
        public void Create_WithInvalidGraph_ShouldThrowException()
        {
            // Arrange
            var invalidNode = FlowNodeBuilder.Build<DebugLogNode>();
            var nodes = new List<FlowNodeInfo> { invalidNode }; // 没有启动节点

            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() => 
                FlowExecutor.Create(FlowGraph.Create(nodes, new List<FlowConnection>()).Data));
            Assert.Contains("流程图没有启动节点", exception.Message);
        }

        [Fact]
        public void Start_ShouldInitializeExecution()
        {
            // Arrange
            var startNode = FlowNodeBuilder.Build<StartNode>();
            var debugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "Test")
                .Build();

            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, DebugLogNode>(startNode, debugNode, n => n.Start, n => n.Print),
            };

            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, debugNode }, connections);
            var executor = FlowExecutor.Create(graphRes.Data);

            // Act
            var result = executor.Start();

            // Assert
            Assert.True(result.IsSuccess);
            Assert.True(executor.IsRunning);
        }

        [Fact]
        public void StepNext_WithSimpleFlow_ShouldExecuteCorrectly()
        {
            // Arrange
            var startNode = FlowNodeBuilder.Build<StartNode>();
            var debugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "Hello, World!")
                .Build();

            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, DebugLogNode>(startNode, debugNode, n => n.Start, n => n.Print),
            };

            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, debugNode }, connections);
            var executor = FlowExecutor.Create(graphRes.Data);
            executor.Start();

            // Act
            var step1Result = executor.StepNext(); // 执行 StartNode
            var step2Result = executor.StepNext(); // 执行 DebugLogNode
            var step3Result = executor.StepNext(); // 没有更多任务

            // Assert
            Assert.True(step1Result);
            Assert.True(step2Result);
            Assert.False(step3Result);
            Assert.False(executor.IsRunning);
        }

        [Fact]
        public void StepNext_WithForLoop_ShouldExecuteCorrectly()
        {
            // Arrange
            var startNode = FlowNodeBuilder.Build<StartNode>();
            var forNode = FlowNodeBuilder.For<ForNode>()
                .SetValue(n => n.Start, 0)
                .SetValue(n => n.End, 3)
                .SetValue(n => n.Step, 1)
                .Build();
            var debugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "Loop iteration")
                .Build();
            var endDebugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "Loop completed")
                .Build();

            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, ForNode>(startNode, forNode, n => n.Start, n => n.StartLoop),
                FlowConnection.EventToSignal<ForNode, DebugLogNode>(forNode, debugNode, n => n.LoopBody, n => n.Print),
                FlowConnection.EventToSignal<ForNode, DebugLogNode>(forNode, endDebugNode, n => n.LoopCompleted, n => n.Print),
            };

            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, forNode, debugNode, endDebugNode }, connections);
            var executor = FlowExecutor.Create(graphRes.Data);
            executor.Start();

            // Act & Assert
            // 执行 StartNode
            Assert.True(executor.StepNext());
            Assert.Equal(startNode.Id, executor.CurrentNodeId);

            // 执行 ForNode 的 StartLoop
            Assert.True(executor.StepNext());
            Assert.Equal(forNode.Id, executor.CurrentNodeId);

            // 执行第一次循环体
            Assert.True(executor.StepNext());
            Assert.Equal(debugNode.Id, executor.CurrentNodeId);

            // 继续执行循环（应该还有2次迭代）
            for (int i = 0; i < 5; i++)
            {
                executor.StepNext();
            }

            // 最后应该执行循环完成
            Assert.True(executor.StepNext());
            Assert.Equal(endDebugNode.Id, executor.CurrentNodeId);

            // 执行完成
            Assert.True(executor.StepNext());
            Assert.False(executor.IsRunning);
        }

        [Fact]
        public void StepNext_WithIfElseNode_ShouldExecuteCorrectBranch()
        {
            // Arrange
            var startNode = FlowNodeBuilder.Build<StartNode>();
            var ifElseNode = FlowNodeBuilder.For<IfElseNode>()
                .SetValue(n => n.Condition, true)
                .Build();
            var trueDebugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "True branch")
                .Build();
            var falseDebugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "False branch")
                .Build();

            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, IfElseNode>(startNode, ifElseNode, n => n.Start, n => n.Execute),
                FlowConnection.EventToSignal<IfElseNode, DebugLogNode>(ifElseNode, trueDebugNode, n => n.TrueBranch, n => n.Print),
                FlowConnection.EventToSignal<IfElseNode, DebugLogNode>(ifElseNode, falseDebugNode, n => n.FalseBranch, n => n.Print),
            };

            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, ifElseNode, trueDebugNode, falseDebugNode }, connections);
            var executor = FlowExecutor.Create(graphRes.Data);
            executor.Start();

            // Act
            Assert.True(executor.StepNext()); // StartNode
            Assert.True(executor.StepNext()); // IfElseNode
            Assert.True(executor.StepNext()); // TrueDebugNode (应该执行真分支)

            // Assert
            Assert.Equal(trueDebugNode.Id, executor.CurrentNodeId);
        }

        [Fact]
        public void StepNext_WithOutputToInputConnection_ShouldPassDataCorrectly()
        {
            // Arrange
            var startNode = FlowNodeBuilder.Build<StartNode>();
            var sourceNode = FlowNodeBuilder.For<TestSourceNode>()
                .SetValue(n => n.OutputValue, "Test Data")
                .Build();
            var targetNode = FlowNodeBuilder.For<TestTargetNode>()
                .Build();

            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, TestSourceNode>(startNode, sourceNode, n => n.Start, n => n.Produce),
                FlowConnection.OutputToInput<TestSourceNode, TestTargetNode>(sourceNode, targetNode, n => n.OutputValue, n => n.InputValue),
                FlowConnection.EventToSignal<TestSourceNode, TestTargetNode>(sourceNode, targetNode, n => n.OutputEvent, n => n.Process),
            };

            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, sourceNode, targetNode }, connections);
            var executor = FlowExecutor.Create(graphRes.Data);
            executor.Start();

            // Act
            Assert.True(executor.StepNext()); // StartNode
            Assert.True(executor.StepNext()); // TestSourceNode
            Assert.True(executor.StepNext()); // TestTargetNode

            // Assert
            Assert.Equal(targetNode.Id, executor.CurrentNodeId);
        }



        [Fact]
        public void StepNext_WithWhileLoop_ShouldExecuteCorrectly()
        {
            // Arrange
            var startNode = FlowNodeBuilder.Build<StartNode>();
            var whileNode = FlowNodeBuilder.For<WhileNode>()
                .SetValue(n => n.Condition, true)
                .SetValue(n => n.MaxIterations, 3)
                .Build();
            var debugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "While iteration")
                .Build();
            var endDebugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "While completed")
                .Build();

            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, WhileNode>(startNode, whileNode, n => n.Start, n => n.StartLoop),
                FlowConnection.EventToSignal<WhileNode, DebugLogNode>(whileNode, debugNode, n => n.LoopBody, n => n.Print),
                FlowConnection.EventToSignal<WhileNode, DebugLogNode>(whileNode, endDebugNode, n => n.LoopCompleted, n => n.Print),
            };

            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, whileNode, debugNode, endDebugNode }, connections);
            var executor = FlowExecutor.Create(graphRes.Data);
            executor.Start();

            // Act & Assert
            Assert.True(executor.StepNext()); // StartNode
            Assert.True(executor.StepNext()); // WhileNode StartLoop

            // 执行循环体（最多3次）
            for (int i = 0; i < 5; i++)
            {
                executor.StepNext();
            }

            // 应该执行循环完成
            Assert.True(executor.StepNext());
            Assert.Equal(endDebugNode.Id, executor.CurrentNodeId);
        }

        [Fact]
        public void StepNext_WithComplexFlow_ShouldExecuteAllNodes()
        {
            // Arrange
            var startNode = FlowNodeBuilder.Build<StartNode>();
            var ifElseNode = FlowNodeBuilder.For<IfElseNode>()
                .SetValue(n => n.Condition, true)
                .Build();
            var forNode = FlowNodeBuilder.For<ForNode>()
                .SetValue(n => n.Start, 0)
                .SetValue(n => n.End, 2)
                .SetValue(n => n.Step, 1)
                .Build();
            var debugNode = FlowNodeBuilder.For<DebugLogNode>()
                .SetValue(n => n.Content, "Complex flow")
                .Build();

            var connections = new List<FlowConnection>()
            {
                FlowConnection.EventToSignal<StartNode, IfElseNode>(startNode, ifElseNode, n => n.Start, n => n.Execute),
                FlowConnection.EventToSignal<IfElseNode, ForNode>(ifElseNode, forNode, n => n.TrueBranch, n => n.StartLoop),
                FlowConnection.EventToSignal<ForNode, DebugLogNode>(forNode, debugNode, n => n.LoopBody, n => n.Print),
            };

            var graphRes = FlowGraph.Create(new List<FlowNodeInfo>() { startNode, ifElseNode, forNode, debugNode }, connections);
            var executor = FlowExecutor.Create(graphRes.Data);
            executor.Start();

            // Act & Assert
            var stepCount = 0;
            while (executor.StepNext())
            {
                stepCount++;
                Assert.True(stepCount <= 20, "执行步数过多，可能存在无限循环");
            }

            Assert.False(executor.IsRunning);
        }

        // 测试用的辅助节点类
        [FlowNode("测试源节点")]
        private class TestSourceNode : FlowBaseNode
        {
            [FlowInput("输出值")]
            public string OutputValue { get; set; }

            [FlowOutput("输出值")]
            public string OutputValueOutput { get; set; }

            [FlowEvent("输出事件")]
            public FlowEndpoint OutputEvent { get; set; }

            [FlowSignal("产生")]
            public FlowOutEvent Produce()
            {
                OutputValueOutput = OutputValue;
                return Emit(() => OutputEvent);
            }
        }

        [FlowNode("测试目标节点")]
        private class TestTargetNode : FlowBaseNode
        {
            [FlowInput("输入值")]
            public string InputValue { get; set; }

            [FlowEvent("下一步")]
            public FlowEndpoint Next { get; set; }

            [FlowSignal("处理")]
            public FlowOutEvent Process()
            {
                Console.WriteLine($"Received: {InputValue}");
                return Emit(() => Next);
            }
        }
    }
}
