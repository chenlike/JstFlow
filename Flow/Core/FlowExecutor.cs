using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Flow.Common;
using Flow.Core;
using Flow.Core.NodeMeta;
using Flow.Core.Metas;
using Flow.External;
using Flow.External.Expressions;
using Flow.External.Nodes;

namespace Flow.Core
{
    /// <summary>
    /// 执行任务接口，支持多种任务类型
    /// </summary>
    public interface IExecutionTask
    {
        long NodeId { get; }
    }

    /// <summary>
    /// 普通信号驱动任务
    /// </summary>
    public class SignalTask : IExecutionTask
    {
        public long NodeId { get; }
        public string TriggerSignal { get; }

        public SignalTask(long nodeId, string triggerSignal)
        {
            NodeId = nodeId;
            TriggerSignal = triggerSignal;
        }
    }

    /// <summary>
    /// 恢复枚举器任务
    /// </summary>
    public class ContinueEnumerator : IExecutionTask
    {
        public long NodeId { get; }
        public long EnumeratorId { get; }

        public ContinueEnumerator(long nodeId, long enumeratorId)
        {
            NodeId = nodeId;
            EnumeratorId = enumeratorId;
        }
    }

    /// <summary>
    /// 流程图执行器，负责执行 FlowGraph 中定义的节点和连接
    /// 支持同步执行
    /// </summary>
    public class FlowExecutor
    {
        private readonly FlowGraph _graph;
        private readonly Dictionary<long, FlowNodeInfo> _nodes;
        private readonly Stack<IExecutionTask> _stack = new Stack<IExecutionTask>();
        private readonly Dictionary<long, object> _instances = new Dictionary<long, object>();
        private readonly Dictionary<long, IEnumerator<FlowOutEvent>> _enumerators = new Dictionary<long, IEnumerator<FlowOutEvent>>();
        private readonly Dictionary<long, Dictionary<string, object>> _inputs = new Dictionary<long, Dictionary<string, object>>();
        private readonly Dictionary<long, Dictionary<string, object>> _outputs = new Dictionary<long, Dictionary<string, object>>();
        private readonly Dictionary<long, object> _expressionResults = new Dictionary<long, object>();
        private readonly Dictionary<(long, string), FlowConnection> _eventConnectionMap;

        public long CurrentNodeId { get; private set; }
        public bool IsRunning => _stack.Count > 0;

        private FlowExecutor(FlowGraph graph)
        {
            _graph = graph;
            _nodes = graph.Nodes.ToDictionary(n => n.Id);
            // 初始化事件连接字典
            _eventConnectionMap = graph.Connections
                .Where(c => c.Type == ConnectionType.EventToSignal)
                .ToDictionary(
                    c => (c.SourceNodeId, c.SourceEndpointCode),
                    c => c
                );
        }

        public static FlowExecutor Create(FlowGraph graph)
        {
            var validation = graph.ValidateGraph();
            if (validation.IsFailure)
                throw new InvalidOperationException(validation.Message);
            return new FlowExecutor(graph);
        }

        public Res Start()
        {
            var startNode = _graph.Nodes.FirstOrDefault(n => n.Kind == NodeKind.StartNode)
                            ?? throw new InvalidOperationException("流程图没有启动节点");
            _stack.Push(new SignalTask(startNode.Id, nameof(StartNode.Execute)));
            return Res.Ok();
        }

        public bool StepNext()
        {
            if (_stack.Count == 0)
                return false;

            var task = _stack.Pop();
            CurrentNodeId = task.NodeId;

            // 准备并缓存输入参数
            _inputs[task.NodeId] = PrepareInputs(task.NodeId);

            // 执行并将后续任务压栈
            foreach (var next in Execute(task))
            {
                if (next != null)
                    _stack.Push(next);
            }

            // 准备并缓存输出参数
            _outputs[task.NodeId] = SaveOutputs(task.NodeId);

            return true;
        }

        private IEnumerable<IExecutionTask> Execute(IExecutionTask task)
        {
            var nodeInfo = _nodes[task.NodeId];
            var instance = GetInstance(nodeInfo);
            SetInputs(instance, _inputs[task.NodeId]);

            // 表达式节点不产生任务
            if (nodeInfo.Kind == NodeKind.Expression)
                return Enumerable.Empty<IExecutionTask>();

            if (task is ContinueEnumerator resume)
            {
                return ExecuteResume(resume);
            }
            else if (task is SignalTask signal)
            {
                var signalInfo = GetSignal(nodeInfo, signal.TriggerSignal);
                return ExecuteSignal(instance, signalInfo, task.NodeId);
            }
            else
            {
                return Enumerable.Empty<IExecutionTask>();
            }
        }

        private IEnumerable<IExecutionTask> ExecuteResume(ContinueEnumerator resume)
        {
            if (!_enumerators.ContainsKey(resume.EnumeratorId))
                yield break;

            var enumerator = _enumerators[resume.EnumeratorId];

            if (enumerator.MoveNext())
            {
                // 继续恢复此枚举器 还没执行完
                yield return new ContinueEnumerator(resume.NodeId, resume.EnumeratorId);
                yield return CreateSignalTask(resume.NodeId, enumerator.Current);
            }
            else
            {
                _enumerators.Remove(resume.EnumeratorId);
            }
        }

        private IEnumerable<IExecutionTask> ExecuteSignal(object instance, SignalInfo signal, long nodeId)
        {
            if (signal == null)
                yield break;

            var result = signal.MethodInfo.Invoke(instance, null);
            
            switch (result)
            {
                case FlowOutEvent e:
                    yield return CreateSignalTask(nodeId, e);
                    break;
                case IEnumerable<FlowOutEvent> list:
                    // 将 IEnumerable 转为 IEnumerator 统一处理
                    var enList = list.GetEnumerator();
                    if (enList.MoveNext())
                    {
                        var idList = Utils.GenId();
                        _enumerators[idList] = enList;
                        // 先入栈恢复任务，再生成信号任务
                        yield return new ContinueEnumerator(nodeId, idList);
                        yield return CreateSignalTask(nodeId, enList.Current);
                    }
                    break;
                case IEnumerator<FlowOutEvent> en:
                    if (en.MoveNext())
                    {
                        var id = Utils.GenId();
                        _enumerators[id] = en;
                        // 先入栈恢复任务，再生成信号任务
                        yield return new ContinueEnumerator(nodeId, id);
                        yield return CreateSignalTask(nodeId, en.Current);
                    }
                    break;
                default:
                    // void 或其他类型，无需处理
                    break;
            }
        }

        private IExecutionTask CreateSignalTask(long nodeId, FlowOutEvent evt)
        {
            if (evt == null)
                return null;
            // 用字典高效查找
            if (_eventConnectionMap.TryGetValue((nodeId, evt.MemberName), out var conn))
            {
                return new SignalTask(conn.TargetNodeId, conn.TargetEndpointCode);
            }
            return null;
        }

        private Dictionary<string, object> PrepareInputs(long nodeId)
        {
            var defineInputs = _nodes[nodeId].InputFields;


            // 获取所有指向当前节点的输入连接
            var inputConnections = _graph.Connections
                .Where(c => c.TargetNodeId == nodeId && c.Type == ConnectionType.OutputToInput);
            var inputs = new Dictionary<string, object>();

            foreach (var input in defineInputs)
            {
                var connection = inputConnections.FirstOrDefault(c => c.TargetEndpointCode == input.Label.Code);
                if (connection != null)
                {
                    var value = GetValue(connection);
                    if (value != null)
                    {
                        inputs[connection.TargetEndpointCode] = value;
                    }
                }else{
                    // 查找是否有默认值
                    if (input.InitValue != null)
                    {
                        inputs[input.Label.Code] = input.InitValue;
                    }
                }
            }

            return inputs;
        }

        private Dictionary<string, object> SaveOutputs(long nodeId)
        {
            // 获取节点实例
            var instance = GetInstance(_nodes[nodeId]);
            // 获取节点输出
            var outputs = _nodes[nodeId].OutputFields.ToDictionary(f => f.Label.Code, f => f.PropertyInfo.GetValue(instance));
            // 返回输出
            return outputs;
        }

        private object GetValue(FlowConnection c)
            => _nodes[c.SourceNodeId].Kind == NodeKind.Expression
                ? ExecuteExpression(c.SourceNodeId)
                : GetOutputValue(c.SourceNodeId, c.SourceEndpointCode);

        private object ExecuteExpression(long exprId)
        {

            if (_expressionResults.TryGetValue(exprId, out var cached))
                return cached;


            var info = _nodes[exprId];

            var instance = Activator.CreateInstance(info.NodeImplType);
            if (!(instance is FlowExpression<object> exprObj))
            {
                throw new InvalidOperationException($"节点 {exprId} 不是 FlowExpression<>");
            }

            var result = exprObj.Evaluate();

            _expressionResults[exprId] = result;
            return result;
        }

        private object GetOutputValue(long nodeId, string code)
        {
            var field = _nodes[nodeId].OutputFields.FirstOrDefault(f => f.Label.Code == code);
            return field?.PropertyInfo.GetValue(GetInstance(_nodes[nodeId]));
        }

        private SignalInfo GetSignal(FlowNodeInfo node, string code)
            => string.IsNullOrEmpty(code)
                ? null
                : node.Signals.FirstOrDefault(s => s.Label.Code == code);

        private object GetInstance(FlowNodeInfo node)
        {
            if (_instances.ContainsKey(node.Id))
            {
                return _instances[node.Id];
            }
            var instance = CreateInstance(node.NodeImplType);
            _instances[node.Id] = instance;
            return instance;
        }



        private object CreateInstance(Type type)
        {
            var instance = Activator.CreateInstance(type);
            return instance;
        }

        private void SetInputs(object instance, Dictionary<string, object> inputs)
        {
            foreach (var kv in inputs)
            {
                var prop = instance.GetType().GetProperty(kv.Key);
                if (prop != null && kv.Value != null)
                {
                    try { prop.SetValue(instance, Convert.ChangeType(kv.Value, prop.PropertyType)); }
                    catch { }
                }
            }
        }

    }

}
